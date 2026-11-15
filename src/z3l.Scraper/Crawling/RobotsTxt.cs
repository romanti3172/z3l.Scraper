using System.Globalization;
using System.Net;
using System.Text;

namespace z3l.Scraper.Crawling;

/// <summary>
/// A parsed robots.txt (RFC 9309): which paths a crawler may load, its crawl delay, and the sitemaps the site lists.
/// </summary>
/// <example>
/// <code>
/// var robots = RobotsTxt.Parse(text);
/// robots.IsAllowed("https://shop.example/cart?id=1", "my-bot");   // false for "Disallow: /cart"
/// robots.Sitemaps;                                                 // "Sitemap:" lines
/// </code>
/// </example>
public sealed class RobotsTxt
{
    /// <summary>RFC 9309 asks crawlers to read at least 500 KiB.</summary>
    internal const int MaxLength = 500 * 1024;

    private readonly List<Group> _groups;

    private RobotsTxt(List<Group> groups, List<string> sitemaps)
    {
        _groups = groups;
        Sitemaps = sitemaps;
    }

    /// <summary>Rules that allow everything: there is no robots.txt, or it could not be loaded.</summary>
    public static RobotsTxt AllowAll { get; } = new([], []);

    /// <summary>URLs of the "Sitemap:" lines.</summary>
    public IReadOnlyList<string> Sitemaps { get; }

    /// <summary>
    /// Parses robots.txt. Lines it does not understand are skipped. A text a browser wrapped in &lt;pre&gt;
    /// (what a headless browser returns for a plain text file) is unwrapped first.
    /// </summary>
    public static RobotsTxt Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return AllowAll;
        if (content.Length > MaxLength) content = content[..MaxLength];
        content = UnwrapBrowserText(content);

        var groups = new List<Group>();
        var sitemaps = new List<string>();
        Group? current = null;
        var agentLines = false;     // the last record line was a user-agent: the next one joins the same group

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine;
            var comment = line.IndexOf('#');
            if (comment >= 0) line = line[..comment];

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            switch (key)
            {
                case "user-agent":
                    if (current == null || !agentLines)
                    {
                        current = new Group();
                        groups.Add(current);
                    }
                    if (value.Length > 0) current.Agents.Add(value.ToLowerInvariant());
                    agentLines = true;
                    break;

                case "allow":
                case "disallow":
                    agentLines = false;
                    if (current != null && value.Length > 0) current.Rules.Add(new Rule(NormalizePattern(value), key == "allow"));
                    break;

                case "crawl-delay":
                    agentLines = false;
                    if (current != null
                        && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                        && double.IsFinite(seconds) && seconds >= 0)
                    {
                        current.CrawlDelay ??= TimeSpan.FromSeconds(Math.Min(seconds, 86_400));
                    }
                    break;

                case "sitemap":
                    if (value.Length > 0) sitemaps.Add(value);
                    break;
            }
        }

        return new RobotsTxt(groups, sitemaps);
    }

    /// <summary>
    /// Whether the crawler with this product token may load the URL. The longest matching rule wins; on a tie Allow wins;
    /// with no matching rule the URL is allowed. /robots.txt itself is always allowed.
    /// </summary>
    /// <param name="url">Absolute URL.</param>
    /// <param name="userAgent">Product token of the crawler (case does not matter); "*" groups apply when no group names it.</param>
    public bool IsAllowed(string url, string userAgent = "*")
    {
        ArgumentNullException.ThrowIfNull(url);
        return !Uri.TryCreate(url, UriKind.Absolute, out var uri) || IsAllowed(uri, userAgent);
    }

    /// <inheritdoc cref="IsAllowed(string, string)" />
    public bool IsAllowed(Uri url, string userAgent = "*")
    {
        ArgumentNullException.ThrowIfNull(url);
        var path = NormalizePath(url.PathAndQuery);
        if (path == "/robots.txt") return true;

        Rule? best = null;
        foreach (var group in GroupsFor(userAgent))
        {
            foreach (var rule in group.Rules)
            {
                if (!Matches(rule.Pattern, path)) continue;
                if (best == null
                    || rule.Pattern.Length > best.Pattern.Length
                    || (rule.Pattern.Length == best.Pattern.Length && rule.Allow && !best.Allow))
                {
                    best = rule;
                }
            }
        }
        return best?.Allow ?? true;
    }

    /// <summary>The Crawl-delay for this product token, null when the file sets none.</summary>
    public TimeSpan? CrawlDelay(string userAgent = "*") => GroupsFor(userAgent).Select(g => g.CrawlDelay).FirstOrDefault(d => d != null);

    private IEnumerable<Group> GroupsFor(string userAgent)
    {
        var token = (userAgent ?? "*").Trim().ToLowerInvariant();
        if (token != "*")
        {
            var named = _groups.Where(g => g.Agents.Contains(token)).ToList();
            if (named.Count > 0) return named;
        }
        return _groups.Where(g => g.Agents.Contains("*"));
    }

    /// <summary>
    /// Matches a robots.txt path pattern against the start of a path: '*' is any sequence, a final '$' anchors the end.
    /// Linear backtracking over the last '*' (no regular expressions: the patterns come from the site).
    /// </summary>
    internal static bool Matches(string pattern, string path)
    {
        var anchored = pattern.EndsWith('$');
        if (anchored) pattern = pattern[..^1];

        int p = 0, s = 0, star = -1, starAt = 0;
        while (true)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                starAt = s;
                continue;
            }
            if (p == pattern.Length)
            {
                if (!anchored || s == path.Length) return true;
            }
            else if (s < path.Length && pattern[p] == path[s])
            {
                p++;
                s++;
                continue;
            }

            // mismatch: let the last '*' swallow one more character
            if (star < 0 || starAt >= path.Length) return false;
            p = star + 1;
            s = ++starAt;
        }
    }

    /// <summary>Percent-encodes characters outside ASCII and upper-cases escapes, so patterns compare with URI paths.</summary>
    private static string NormalizePattern(string pattern)
    {
        var builder = new StringBuilder(pattern.Length);
        Span<byte> utf8 = stackalloc byte[4];
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c > 0x7F)
            {
                var length = char.IsSurrogatePair(pattern, i)
                    ? Encoding.UTF8.GetBytes(pattern.AsSpan(i++, 2), utf8)
                    : Encoding.UTF8.GetBytes(pattern.AsSpan(i, 1), utf8);
                foreach (var b in utf8[..length]) builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }
        return NormalizePath(builder.ToString());
    }

    private static string NormalizePath(string path)
    {
        if (!path.Contains('%')) return path;
        var chars = path.ToCharArray();
        for (var i = 0; i + 2 < chars.Length; i++)
        {
            if (chars[i] != '%' || !char.IsAsciiHexDigit(chars[i + 1]) || !char.IsAsciiHexDigit(chars[i + 2])) continue;
            chars[i + 1] = char.ToUpperInvariant(chars[i + 1]);
            chars[i + 2] = char.ToUpperInvariant(chars[i + 2]);
        }
        return new string(chars);
    }

    /// <summary>Chrome shows a text file as &lt;html&gt;...&lt;pre&gt;text&lt;/pre&gt;; browser loaders return that.</summary>
    private static string UnwrapBrowserText(string content)
    {
        var start = content.AsSpan().TrimStart();
        if (!start.StartsWith("<html", StringComparison.OrdinalIgnoreCase) && !start.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)) return content;

        var pre = content.IndexOf("<pre", StringComparison.OrdinalIgnoreCase);
        if (pre < 0) return content;
        var open = content.IndexOf('>', pre);
        var close = content.IndexOf("</pre>", StringComparison.OrdinalIgnoreCase);
        if (open < 0 || close < open) return content;
        return WebUtility.HtmlDecode(content[(open + 1)..close]);
    }

    private sealed class Group
    {
        public List<string> Agents { get; } = [];
        public List<Rule> Rules { get; } = [];
        public TimeSpan? CrawlDelay { get; set; }
    }

    private sealed record Rule(string Pattern, bool Allow);
}
