using System.Globalization;
using System.Net;

namespace z3l.Scraper.Loading;

/// <summary>
/// The headers a browser sends, worked out from its user agent, in the browser's order: Chrome and Edge with their client hints,
/// Firefox, Safari. A page is asked for as a navigation (a click on a link), a request of <see cref="PageType.Api"/> as fetch() of
/// the page it was found on. Host, Cookie and the headers of the body are the handler's.
/// </summary>
internal static class BrowserHeaders
{
    internal enum Engine
    {
        Chromium,
        Firefox,
        Safari,
        Other,
    }

    /// <summary>
    /// What a user agent says: the engine, the brand of a Chromium browser ("Google Chrome", "Microsoft Edge"; null for the other
    /// Chromium browsers, which get no client hints), the major version, the platform as the client hints name it, a phone.
    /// </summary>
    internal readonly record struct Agent(Engine Engine, string? Brand, int Major, string Platform, bool Mobile);

    internal const string ChromeAccept =
        "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7";

    internal const string PageAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

#if NET11_0_OR_GREATER
    /// <summary>
    /// What HttpClient decodes, as Chrome and Firefox announce it: zstd too since .NET 11. The handler would add zstd anyway, as it
    /// adds every encoding it decodes that the header lacks.
    /// </summary>
    internal const string AcceptEncoding = "gzip, deflate, br, zstd";
#else
    /// <summary>What HttpClient decodes: zstd, which Chrome and Firefox announce too, it decodes from .NET 11 on.</summary>
    internal const string AcceptEncoding = "gzip, deflate, br";
#endif

    internal static Agent Parse(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return new Agent(Engine.Other, null, 0, "Unknown", false);

        var ua = userAgent;
        var mobile = ua.Contains("Mobile", StringComparison.Ordinal);
        var platform = Has(ua, "Android") ? "Android"
            : Has(ua, "iPhone") || Has(ua, "iPad") ? "iOS"
            : Has(ua, "CrOS") ? "Chrome OS"
            : Has(ua, "Windows") ? "Windows"
            : Has(ua, "Macintosh") || Has(ua, "Mac OS X") ? "macOS"
            : Has(ua, "Linux") || Has(ua, "X11") ? "Linux"
            : "Unknown";

        // every browser on iOS is WebKit: Chrome (CriOS), Firefox (FxiOS) and Edge (EdgiOS) ask there as Safari does
        if (platform == "iOS" || Has(ua, "CriOS/") || Has(ua, "FxiOS/") || Has(ua, "EdgiOS/"))
        {
            return new Agent(Engine.Safari, null, MajorAfter(ua, "Version/"), platform, mobile);
        }
        if (MajorAfter(ua, "Firefox/") is > 0 and var firefox) return new Agent(Engine.Firefox, null, firefox, platform, mobile);
        if (MajorAfter(ua, "Chrome/") is > 0 and var chromium)
        {
            var brand = Has(ua, "Edg/") ? "Microsoft Edge"
                : Has(ua, "OPR/") || Has(ua, "YaBrowser/") || Has(ua, "SamsungBrowser/") || Has(ua, "Vivaldi/") ? null
                : "Google Chrome";
            return new Agent(Engine.Chromium, brand, chromium, platform, mobile);
        }
        if (Has(ua, "Safari/") && Has(ua, "Version/")) return new Agent(Engine.Safari, null, MajorAfter(ua, "Version/"), platform, mobile);
        return new Agent(Engine.Other, null, 0, platform, mobile);
    }

    private static bool Has(string ua, string token) => ua.Contains(token, StringComparison.Ordinal);

    /// <summary>The number right after <paramref name="token"/> ("Chrome/" in "Chrome/142.0.0.0" gives 142); 0 when there is none.</summary>
    internal static int MajorAfter(string ua, string token)
    {
        var at = ua.IndexOf(token, StringComparison.Ordinal);
        if (at < 0) return 0;
        var rest = ua.AsSpan(at + token.Length);
        var digits = 0;
        while (digits < rest.Length && digits < 6 && char.IsAsciiDigit(rest[digits])) digits++;
        return digits == 0 ? 0 : int.Parse(rest[..digits], NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static readonly string[] GreaseyChars = [" ", "(", ":", "-", ".", "/", ")", ";", "=", "?", "_"];
    private static readonly string[] GreasedVersions = ["8", "99", "24"];
    private static readonly int[][] Orders = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];

    /// <summary>
    /// sec-ch-ua as Chromium makes it: a made-up GREASE brand, Chromium and the browser, in an order and with characters the major
    /// version picks (Chrome 124 sends <c>"Chromium";v="124", "Google Chrome";v="124", "Not-A.Brand";v="99"</c>).
    /// </summary>
    internal static string Brands(int major, string brand)
    {
        var order = Orders[major % Orders.Length];
        var list = new string[3];
        list[order[0]] = string.Create(CultureInfo.InvariantCulture,
            $"\"Not{GreaseyChars[major % GreaseyChars.Length]}A{GreaseyChars[(major + 1) % GreaseyChars.Length]}Brand\";v=\"{GreasedVersions[major % GreasedVersions.Length]}\"");
        list[order[1]] = string.Create(CultureInfo.InvariantCulture, $"\"Chromium\";v=\"{major}\"");
        list[order[2]] = string.Create(CultureInfo.InvariantCulture, $"\"{brand}\";v=\"{major}\"");
        return string.Join(", ", list);
    }

    /// <summary>
    /// Sec-Fetch-Site of a request for <paramref name="target"/> made from the page at <paramref name="parent"/>: none for a navigation
    /// with no page (a typed address, a bookmark), same-origin, same-site (another host of the same site, the same scheme) or cross-site.
    /// A fetch with no page is taken as made by a page of its own origin.
    /// </summary>
    internal static string FetchSite(Uri? parent, Uri target, bool navigation)
    {
        if (parent == null) return navigation ? "none" : "same-origin";
        if (SameOrigin(parent, target)) return "same-origin";
        return parent.Scheme == target.Scheme && string.Equals(SiteOf(parent.IdnHost), SiteOf(target.IdnHost), StringComparison.OrdinalIgnoreCase)
            ? "same-site"
            : "cross-site";
    }

    internal static bool SameOrigin(Uri a, Uri b) =>
        a.Scheme == b.Scheme && a.Port == b.Port && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The site of a host, roughly its registrable domain without the public suffix list: the last two labels, or three under a
    /// two-letter country code with a generic second level (shop.co.uk, shop.com.au). An IP address is its own site.
    /// </summary>
    internal static string SiteOf(string host)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out _)) return host;
        var labels = host.TrimEnd('.').Split('.');
        if (labels.Length <= 2) return host;
        var take = labels[^1].Length == 2 && labels[^2] is "co" or "com" or "net" or "org" or "gov" or "edu" or "ac" or "or" or "ne" or "go" ? 3 : 2;
        return labels.Length <= take ? host : string.Join('.', labels[^take..]);
    }

    /// <summary>
    /// Referer as browsers send it by default (strict-origin-when-cross-origin): the whole page URL to its own origin, the origin of the
    /// page elsewhere, nothing from https to http. Without the fragment and the user info.
    /// </summary>
    internal static string? Referer(Uri? parent, Uri target)
    {
        if (parent == null) return null;
        if (parent.Scheme == Uri.UriSchemeHttps && target.Scheme == Uri.UriSchemeHttp) return null;
        return SameOrigin(parent, target)
            ? parent.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)
            : parent.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + "/";
    }

    /// <summary>
    /// The headers of a request in the order the browser of <paramref name="userAgent"/> sends them. Origin goes with a request that is
    /// neither GET nor HEAD; Referer and Sec-Fetch-Site come of <see cref="ScrapeRequest.ParentUrl"/>.
    /// </summary>
    internal static List<KeyValuePair<string, string>> For(ScrapeRequest request, Uri uri, string? userAgent, string acceptLanguage, bool sendReferer)
    {
        var agent = Parse(userAgent);
        var navigation = !string.Equals(request.Label, ScrapeLabel.NameOf(PageType.Api), StringComparison.OrdinalIgnoreCase);
        var parent = request.ParentUrl != null && Uri.TryCreate(request.ParentUrl, UriKind.Absolute, out var page) ? page : null;
        var site = FetchSite(parent, uri, navigation);
        var referer = sendReferer ? Referer(parent, uri) : null;
        var origin = request.Method != HttpMethod.Get && request.Method != HttpMethod.Head
            ? (parent ?? uri).GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)
            : null;
        var hints = agent.Engine == Engine.Chromium && agent.Brand != null;
        var brands = hints ? Brands(agent.Major, agent.Brand!) : null;
        var phone = agent.Mobile ? "?1" : "?0";
        var platform = $"\"{agent.Platform}\"";

        var headers = new List<KeyValuePair<string, string>>(16);
        void Add(string name, string? value)
        {
            if (value != null) headers.Add(new(name, value));
        }

        switch (agent.Engine)
        {
            case Engine.Chromium when navigation:
                if (hints)
                {
                    Add("sec-ch-ua", brands);
                    Add("sec-ch-ua-mobile", phone);
                    Add("sec-ch-ua-platform", platform);
                }
                Add("Upgrade-Insecure-Requests", "1");
                Add("User-Agent", userAgent);
                Add("Accept", ChromeAccept);
                Add("Origin", origin);
                Add("Sec-Fetch-Site", site);
                Add("Sec-Fetch-Mode", "navigate");
                Add("Sec-Fetch-User", "?1");
                Add("Sec-Fetch-Dest", "document");
                Add("Referer", referer);
                Add("Accept-Encoding", AcceptEncoding);
                Add("Accept-Language", acceptLanguage);
                Add("Priority", "u=0, i");
                break;

            case Engine.Chromium:
                if (hints) Add("sec-ch-ua-platform", platform);
                Add("User-Agent", userAgent);
                Add("Accept", "*/*");
                if (hints)
                {
                    Add("sec-ch-ua", brands);
                    Add("sec-ch-ua-mobile", phone);
                }
                Add("Origin", origin);
                Add("Sec-Fetch-Site", site);
                Add("Sec-Fetch-Mode", "cors");
                Add("Sec-Fetch-Dest", "empty");
                Add("Referer", referer);
                Add("Accept-Encoding", AcceptEncoding);
                Add("Accept-Language", acceptLanguage);
                Add("Priority", "u=1, i");
                break;

            case Engine.Firefox:
                Add("User-Agent", userAgent);
                Add("Accept", navigation ? PageAccept : "*/*");
                Add("Accept-Language", acceptLanguage);
                Add("Accept-Encoding", AcceptEncoding);
                Add("Referer", referer);
                Add("Origin", origin);
                if (navigation) Add("Upgrade-Insecure-Requests", "1");
                Add("Sec-Fetch-Dest", navigation ? "document" : "empty");
                Add("Sec-Fetch-Mode", navigation ? "navigate" : "cors");
                Add("Sec-Fetch-Site", site);
                if (navigation) Add("Sec-Fetch-User", "?1");
                if (navigation) Add("Priority", "u=0, i");
                break;

            case Engine.Safari:
                Add("Accept", navigation ? PageAccept : "*/*");
                Add("Origin", origin);
                Add("Sec-Fetch-Site", site);
                Add("Sec-Fetch-Dest", navigation ? "document" : "empty");
                Add("Accept-Language", acceptLanguage);
                Add("Sec-Fetch-Mode", navigation ? "navigate" : "cors");
                Add("User-Agent", userAgent);
                Add("Accept-Encoding", AcceptEncoding);
                Add("Referer", referer);
                break;

            default:
                Add("User-Agent", userAgent);
                Add("Accept", navigation ? PageAccept : "*/*");
                Add("Accept-Language", acceptLanguage);
                Add("Accept-Encoding", AcceptEncoding);
                Add("Origin", origin);
                Add("Referer", referer);
                break;
        }
        return headers;
    }
}
