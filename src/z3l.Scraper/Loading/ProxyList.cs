using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace z3l.Scraper.Loading;

/// <summary>What happens to a proxy when a site blocks it. The block is for that site only: other sites keep using the proxy.</summary>
public enum ProxyBlockPolicy
{
    /// <summary>Blocks are counted, the proxy keeps working.</summary>
    Ignore,
    /// <summary>The proxy rests 10 minutes for that site.</summary>
    Block10Minutes,
    /// <summary>The proxy rests an hour for that site. The default.</summary>
    Block1Hour,
    /// <summary>The proxy rests a day for that site.</summary>
    Block1Day,
    /// <summary>The proxy is not used for that site again while the list lives.</summary>
    Remove,
}

/// <summary>A proxy: scheme (http, https, socks4, socks4a, socks5), host, port and an optional login.</summary>
/// <param name="Scheme">http, https, socks4, socks4a or socks5.</param>
/// <param name="Host">Host name or IP address.</param>
/// <param name="Port">Port.</param>
/// <param name="UserName">Login, if the proxy needs one.</param>
/// <param name="Password">Password, if the proxy needs one; never shown by <see cref="ToString"/>.</param>
public sealed partial record ProxyAddress(string Scheme, string Host, int Port, string? UserName = null, string? Password = null)
{
    /// <summary>The proxy URI without the login.</summary>
    public Uri Uri => new($"{Scheme}://{Host}:{Port.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>Scheme, login and address; the password is never shown.</summary>
    public override string ToString() => UserName == null ? $"{Scheme}://{Host}:{Port}" : $"{Scheme}://{UserName}@{Host}:{Port}";

    /// <summary>Identity of the proxy, login included (two logins on one proxy are two proxies).</summary>
    internal string Key =>$"{Scheme}://{UserName}:{Password}@{Host}:{Port}";

    internal WebProxy ToWebProxy() => new(Uri)
    {
        Credentials = UserName == null ? null : new NetworkCredential(UserName, Password),
    };

    /// <summary>
    /// Reads a proxy line: <c>host:port</c>, <c>host:port:user:password</c>, <c>user:password@host:port</c> or
    /// <c>scheme://[user:password@]host:port</c>. Without a scheme the proxy is http. Text after '#' is a comment.
    /// </summary>
    public static bool TryParse(string? line, [NotNullWhen(true)] out ProxyAddress? proxy)
    {
        proxy = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        var value = line;
        var comment = value.IndexOf('#');
        if (comment >= 0) value = value[..comment];
        value = value.Trim();
        if (value.Length == 0) return false;

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            if (!value.Contains('@') && value.Count(c => c == ':') >= 3)
            {
                // host:port:user:password (the password may hold ':')
                var parts = value.Split(':', 4);
                value = $"{Uri.EscapeDataString(parts[2])}:{Uri.EscapeDataString(parts[3])}@{parts[0]}:{parts[1]}";
            }
            value = "http://" + value;
        }

        if (!ExplicitPort().IsMatch(value)) return false;     // "1.2.3.4" alone is a mistake, not port 80
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) || uri.Port is < 1 or > 65535) return false;

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5")) return false;

        string? user = null, password = null;
        if (uri.UserInfo.Length > 0)
        {
            var colon = uri.UserInfo.IndexOf(':');
            user = Uri.UnescapeDataString(colon < 0 ? uri.UserInfo : uri.UserInfo[..colon]);
            password = colon < 0 ? null : Uri.UnescapeDataString(uri.UserInfo[(colon + 1)..]);
        }
        proxy = new ProxyAddress(scheme, uri.Host, uri.Port, user, password);
        return true;
    }

    /// <summary>scheme://[userinfo@]host:port with the port written out.</summary>
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.-]*://(?:[^@/]*@)?[^@/:]+:\d+/?$")]
    private static partial Regex ExplicitPort();
}

/// <summary>State of one proxy, for logs and dashboards.</summary>
/// <param name="Address">The proxy.</param>
/// <param name="Loads">Loads given to it.</param>
/// <param name="Successes">Loads that got an answer.</param>
/// <param name="Failures">Loads that failed in the proxy (no connection, 407...).</param>
/// <param name="Blocks">Answers that sites blocked.</param>
/// <param name="Dead">Resting after failures in a row.</param>
/// <param name="BlockedSites">Sites it rests for now.</param>
public sealed record ProxyStatus(ProxyAddress Address, long Loads, long Successes, long Failures, long Blocks, bool Dead, int BlockedSites);

/// <summary>
/// Proxies for <see cref="HttpPageLoader"/>: given out in turn, put to rest for a site that blocks them
/// (<see cref="ProxyBlockPolicy"/>), and for every site after <see cref="MaxConsecutiveFailures"/> failures in a row.
/// When every proxy rests for a site, a load waits for the first one to come back (the scraper's RequestTimeout limits the wait).
/// Thread-safe.
/// </summary>
/// <example>
/// <code>
/// var proxies = ProxyList.FromFile("proxies.txt");                      // re-read when the file changes
/// // or: await ProxyList.FromUrlAsync(new Uri("https://proxies.example/list.txt"));   // re-read every 5 minutes
/// using var loader = new HttpPageLoader(new HttpPageLoaderOptions { Proxies = proxies });
/// </code>
/// </example>
public sealed class ProxyList
{
    private readonly object _lock = new();
    private readonly ILogger _logger;
    private readonly LineSource? _source;
    private List<Entry> _entries;
    private int _cursor;
    private readonly HashSet<string> _waitLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A list of proxy lines (see <see cref="ProxyAddress.TryParse"/>); bad lines are skipped.</summary>
    /// <exception cref="ArgumentException">No line is a proxy.</exception>
    public ProxyList(IEnumerable<string> lines, ProxyBlockPolicy blockPolicy = ProxyBlockPolicy.Block1Hour, ILogger? logger = null)
        : this(null, lines, blockPolicy, logger)
    {
    }

    private ProxyList(LineSource? source, IEnumerable<string> lines, ProxyBlockPolicy blockPolicy, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(lines);
        _source = source;
        _logger = logger ?? NullLogger.Instance;
        BlockPolicy = blockPolicy;
        _entries = Parse(lines, previous: null);
        if (_entries.Count == 0) throw new ArgumentException(source == null ? "No proxies in the list" : $"No proxies in {source.Name}", nameof(lines));
    }

    /// <summary>
    /// Proxies from a text file, one per line, '#' for comments. The file is read again when it changes; proxies that stay in it
    /// keep their state.
    /// </summary>
    /// <exception cref="FileNotFoundException">No such file.</exception>
    /// <exception cref="ArgumentException">No line of the file is a proxy.</exception>
    public static ProxyList FromFile(string path, ProxyBlockPolicy blockPolicy = ProxyBlockPolicy.Block1Hour, ILogger? logger = null)
    {
        var (source, lines) = LineSource.OpenFile(path, What, logger ?? NullLogger.Instance);
        return new ProxyList(source, lines, blockPolicy, logger);
    }

    /// <summary>
    /// Proxies from a URL that answers with text, one proxy per line, or a JSON array of proxy strings. The URL is read again every
    /// <paramref name="refreshInterval"/> (5 minutes by default) in the background; when it fails, the old list stays. Proxies that
    /// stay in the list keep their state.
    /// </summary>
    /// <param name="url">http(s) URL of the list.</param>
    /// <param name="blockPolicy">What happens to a proxy a site blocks.</param>
    /// <param name="refreshInterval">How often the list is read again.</param>
    /// <param name="httpClient">Client for the requests (for example with an API key header); a shared one when null.</param>
    /// <param name="logger">Logger for blocks, rests and refresh failures.</param>
    /// <param name="cancellationToken">Cancels the first read.</param>
    /// <exception cref="HttpRequestException">The first read failed.</exception>
    /// <exception cref="ArgumentException">No line of the answer is a proxy.</exception>
    public static async Task<ProxyList> FromUrlAsync(Uri url, ProxyBlockPolicy blockPolicy = ProxyBlockPolicy.Block1Hour, TimeSpan? refreshInterval = null,
        HttpClient? httpClient = null, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        var (source, lines) = await LineSource.OpenUrlAsync(url, refreshInterval, httpClient, What, logger ?? NullLogger.Instance, cancellationToken).ConfigureAwait(false);
        return new ProxyList(source, lines, blockPolicy, logger);
    }

    private const string What = "Proxy list";

    /// <summary>What happens to a proxy a site blocks.</summary>
    public ProxyBlockPolicy BlockPolicy { get; }

    /// <summary>Failures in a row (no connection, 407...) after which a proxy rests for every site.</summary>
    public int MaxConsecutiveFailures { get; init; } = 3;

    /// <summary>How long a failing proxy rests.</summary>
    public TimeSpan DeadProxyPause { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How often the file is checked for changes.</summary>
    public TimeSpan FileCheckInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Time source; tests set a manual one before the first load.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Number of proxies.</summary>
    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>State of every proxy.</summary>
    public IReadOnlyList<ProxyStatus> Status()
    {
        lock (_lock)
        {
            var now = Clock.GetUtcNow();
            return [.. _entries.Select(e => new ProxyStatus(e.Address, e.Loads, e.Successes, e.Failures, e.Blocks, e.DeadUntil > now, e.CountBlockedSites(now)))];
        }
    }

    /// <summary>One-line summary.</summary>
    public override string ToString()
    {
        var status = Status();
        return $"{status.Count} proxies: {status.Count(s => !s.Dead)} alive, {status.Count(s => s.Dead)} resting after failures, " +
               $"{status.Count(s => s.BlockedSites > 0)} blocked on some site; {status.Sum(s => s.Loads)} loads, {status.Sum(s => s.Blocks)} blocks";
    }

    // ---------------------------------------------------------------- for loaders

    /// <summary>
    /// The next proxy that may load from <paramref name="site"/>; waits while every proxy rests for it. A loader reports how the
    /// load went with <see cref="ReportSuccess"/>, <see cref="ReportFailure"/> or <see cref="ReportBlocked"/>.
    /// </summary>
    /// <param name="site">Host of the page.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <exception cref="NonRetryableException">Every proxy is removed for the site (<see cref="ProxyBlockPolicy.Remove"/>).</exception>
    public async Task<Entry> AcquireAsync(string site, CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                ReloadIfChanged();
                var now = Clock.GetUtcNow();
                var count = _entries.Count;
                for (var i = 0; i < count; i++)
                {
                    var index = (_cursor + i) % count;
                    var entry = _entries[index];
                    if (entry.DeadUntil > now || entry.BlockedUntil(site) > now) continue;

                    _cursor = (index + 1) % count;
                    entry.Loads++;
                    _waitLogged.Remove(site);
                    return entry;
                }

                var next = _entries.Select(e => e.DeadUntil > e.BlockedUntil(site) ? e.DeadUntil : e.BlockedUntil(site)).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
                if (next == DateTimeOffset.MaxValue)
                {
                    throw new NonRetryableException($"Every proxy is removed for {site} (ProxyBlockPolicy.Remove)");
                }
                wait = next - now;
                if (_waitLogged.Add(site))
                {
                    _logger.LogWarning("Every proxy rests for {Site}: the next one comes back in {Wait}", site, wait.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
                }
            }
            await Task.Delay(wait > TimeSpan.FromMilliseconds(10) ? wait : TimeSpan.FromMilliseconds(10), Clock, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The proxy gave an answer: its failures in a row start again.</summary>
    public void ReportSuccess(Entry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            entry.Successes++;
            entry.FailuresInARow = 0;
        }
    }

    /// <summary>No answer through the proxy (no connection, 407...): after <see cref="MaxConsecutiveFailures"/> in a row it rests for every site.</summary>
    public void ReportFailure(Entry entry, string reason)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            entry.Failures++;
            if (++entry.FailuresInARow < MaxConsecutiveFailures) return;
            entry.FailuresInARow = 0;
            entry.DeadUntil = Clock.GetUtcNow() + DeadProxyPause;
        }
        _logger.LogWarning("Proxy {Proxy} failed {Count} times in a row ({Reason}): it rests {Pause}", entry.Address, MaxConsecutiveFailures, reason, DeadProxyPause);
    }

    /// <summary>A site blocked an answer of the proxy: it rests for that site as <see cref="BlockPolicy"/> says.</summary>
    public void ReportBlocked(Entry entry, string site)
    {
        ArgumentNullException.ThrowIfNull(entry);
        TimeSpan? rest = BlockPolicy switch
        {
            ProxyBlockPolicy.Block10Minutes => TimeSpan.FromMinutes(10),
            ProxyBlockPolicy.Block1Hour => TimeSpan.FromHours(1),
            ProxyBlockPolicy.Block1Day => TimeSpan.FromDays(1),
            _ => null,
        };
        lock (_lock)
        {
            entry.Blocks++;
            if (BlockPolicy == ProxyBlockPolicy.Ignore) return;
            entry.BlockedSites[site] = BlockPolicy == ProxyBlockPolicy.Remove ? DateTimeOffset.MaxValue : Clock.GetUtcNow() + rest!.Value;
        }
        _logger.LogInformation("Proxy {Proxy} is blocked on {Site}: {Policy}", entry.Address, site, BlockPolicy);
    }

    // ---------------------------------------------------------------- file or URL

    private void ReloadIfChanged()
    {
        if (_source?.Poll(Clock.GetUtcNow(), FileCheckInterval) is not { } lines) return;
        var entries = Parse(lines, _entries);
        if (entries.Count == 0)
        {
            _logger.LogWarning("{Source} has no proxies now: the old list stays", _source.Name);
            return;
        }
        _entries = entries;
        _cursor = 0;
        _logger.LogInformation("Proxy list {Source} read again: {Count} proxies", _source.Name, entries.Count);
    }

    /// <summary>Proxies of the lines; a proxy that was in the old list keeps its entry (its rests and counters).</summary>
    private static List<Entry> Parse(IEnumerable<string> lines, List<Entry>? previous)
    {
        var old = previous?.ToDictionary(e => e.Address.Key) ?? [];
        var seen = new HashSet<string>();
        var entries = new List<Entry>();
        foreach (var line in lines)
        {
            if (!ProxyAddress.TryParse(line, out var address) || !seen.Add(address.Key)) continue;
            entries.Add(old.TryGetValue(address.Key, out var entry) ? entry : new Entry(address));
        }
        return entries;
    }

    /// <summary>A proxy of the list, given to a load by <see cref="AcquireAsync"/>.</summary>
    public sealed class Entry
    {
        internal Entry(ProxyAddress address) => Address = address;

        /// <summary>The proxy.</summary>
        public ProxyAddress Address { get; }

        internal Dictionary<string, DateTimeOffset> BlockedSites { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal DateTimeOffset DeadUntil;
        internal int FailuresInARow;
        internal long Loads, Successes, Failures, Blocks;

        internal DateTimeOffset BlockedUntil(string site) => BlockedSites.TryGetValue(site, out var until) ? until : DateTimeOffset.MinValue;

        internal int CountBlockedSites(DateTimeOffset now) => BlockedSites.Count(b => b.Value > now);

        /// <summary>The proxy, without its password.</summary>
        public override string ToString() => Address.ToString();
    }
}
