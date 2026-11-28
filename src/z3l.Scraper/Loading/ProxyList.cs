using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using z3l.Scraper.Diagnostics;

namespace z3l.Scraper.Loading;

/// <summary>
/// A proxy: scheme (http, https, socks4, socks4a, socks5), host, port and an optional login. <c>https</c> is TLS to the proxy
/// itself; a plain http proxy that tunnels https pages with CONNECT (what proxy sellers call an "HTTPS proxy") is <c>http</c>.
/// </summary>
/// <param name="Scheme">http, https, socks4, socks4a or socks5.</param>
/// <param name="Host">Host name or IP address; an IPv6 address without brackets.</param>
/// <param name="Port">Port.</param>
/// <param name="UserName">Login, if the proxy needs one.</param>
/// <param name="Password">Password, if the proxy needs one; never shown by <see cref="ToString"/>.</param>
public sealed partial record ProxyAddress(string Scheme, string Host, int Port, string? UserName = null, string? Password = null)
{
    /// <summary>The proxy URI without the login.</summary>
    public Uri Uri => new($"{Scheme}://{Authority}");

    /// <summary>Scheme, login and address; the password is never shown.</summary>
    public override string ToString() => UserName == null ? $"{Scheme}://{Authority}" : $"{Scheme}://{UserName}@{Authority}";

    /// <summary>Identity of the proxy, login included (two logins on one proxy are two proxies).</summary>
    internal string Key => $"{Scheme}://{UserName}:{Password}@{Authority}";

    /// <summary>host:port, an IPv6 address in brackets.</summary>
    private string Authority =>
        $"{(Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host)}:{Port.ToString(CultureInfo.InvariantCulture)}";

    internal WebProxy ToWebProxy() => new(Uri)
    {
        Credentials = UserName == null ? null : new NetworkCredential(UserName, Password),
    };

    /// <summary>
    /// Reads a proxy line: <c>host:port</c>, <c>host:port:user:password</c> or <c>user:password@host:port</c>, with or without
    /// <c>scheme://</c> in front. Without a scheme the proxy is http; <c>socks5h</c> is read as socks5, which lets the proxy resolve
    /// names as well. An IPv6 address goes in brackets: <c>[2001:db8::1]:8080</c>. A login may hold any character; before '@' it may
    /// also be URL-encoded (<c>%40</c> for '@'). A '#' at the start of the line or after a space starts a comment, so a '#' in a
    /// password stays.
    /// </summary>
    public static bool TryParse(string? line, [NotNullWhen(true)] out ProxyAddress? proxy)
    {
        proxy = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        var value = WithoutComment(line).Trim();
        if (value.Length == 0) return false;

        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        var scheme = schemeEnd < 0 ? "http" : value[..schemeEnd].ToLowerInvariant();
        if (scheme == "socks5h") scheme = "socks5";         // .NET's socks5 already sends the name for the proxy to resolve
        if (scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5")) return false;

        var text = $"{scheme}://{EncodeLogin(schemeEnd < 0 ? value : value[(schemeEnd + 3)..])}";
        if (!ExplicitPort().IsMatch(text)) return false;     // "1.2.3.4" alone is a mistake, not port 80
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) || uri.Port is < 1 or > 65535) return false;

        string? user = null, password = null;
        if (uri.UserInfo.Length > 0)
        {
            var colon = uri.UserInfo.IndexOf(':');
            user = Uri.UnescapeDataString(colon < 0 ? uri.UserInfo : uri.UserInfo[..colon]);
            password = colon < 0 ? null : Uri.UnescapeDataString(uri.UserInfo[(colon + 1)..]);
        }
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host[1..^1] : uri.Host;
        proxy = new ProxyAddress(scheme, host, uri.Port, user, password);
        return true;
    }

    /// <summary>The line before its comment, which starts with a '#' at the start of the line or after a space or a tab.</summary>
    private static string WithoutComment(string line)
    {
        for (var i = line.IndexOf('#'); i >= 0; i = line.IndexOf('#', i + 1))
        {
            if (i == 0 || char.IsWhiteSpace(line[i - 1])) return line[..i];
        }
        return line;
    }

    /// <summary>
    /// The address as <c>[user:password@]host:port</c> with the login URL-encoded: <c>host:port:user:password</c> is turned around
    /// (its login is taken as it is), a login before '@' is encoded where it holds '@', '#', '/'... as they are.
    /// </summary>
    private static string EncodeLogin(string address)
    {
        var at = address.LastIndexOf('@');
        if (at >= 0 && HostAndPort().IsMatch(address[(at + 1)..]))
        {
            var login = address[..at];
            var colon = login.IndexOf(':');
            var encoded = colon < 0 ? Reencode(login) : $"{Reencode(login[..colon])}:{Reencode(login[(colon + 1)..])}";
            return $"{encoded}@{address[(at + 1)..]}";
        }

        var match = HostPortLogin().Match(address);
        return match.Success
            ? $"{Uri.EscapeDataString(match.Groups["user"].Value)}:{Uri.EscapeDataString(match.Groups["password"].Value)}@{match.Groups["host"].Value}:{match.Groups["port"].Value}"
            : address;

        static string Reencode(string part) => Uri.EscapeDataString(Uri.UnescapeDataString(part));
    }

    /// <summary>A host name, an IPv4 address or an IPv6 address in brackets.</summary>
    private const string HostPattern = @"(?:\[[0-9a-fA-F:.]+\]|[^@/:\[\]]+)";

    /// <summary>host:port.</summary>
    [GeneratedRegex($@"^{HostPattern}:\d+/?$")]
    private static partial Regex HostAndPort();

    /// <summary>host:port:user:password; the password may hold ':'.</summary>
    [GeneratedRegex($@"^(?<host>{HostPattern}):(?<port>\d+):(?<user>[^:]+):(?<password>.*)$")]
    private static partial Regex HostPortLogin();

    /// <summary>scheme://[userinfo@]host:port with the port written out.</summary>
    [GeneratedRegex($@"^[a-zA-Z][a-zA-Z0-9+.-]*://(?:[^@/]*@)?{HostPattern}:\d+/?$")]
    private static partial Regex ExplicitPort();
}

/// <summary>State of one proxy, for logs and dashboards.</summary>
public sealed record ProxyStatus
{
    /// <summary>The proxy.</summary>
    public required ProxyAddress Address { get; init; }

    /// <summary>Loads given to it.</summary>
    public required long Loads { get; init; }

    /// <summary>Loads that got an answer.</summary>
    public required long Successes { get; init; }

    /// <summary>Loads that failed in the proxy (no connection, 407...).</summary>
    public required long Failures { get; init; }

    /// <summary>Answers that sites blocked.</summary>
    public required long Blocks { get; init; }

    /// <summary>Resting after failures in a row.</summary>
    public required bool Dead { get; init; }

    /// <summary>Sites it rests for now (blocked, or bad responses in a row).</summary>
    public required int BlockedSites { get; init; }

    /// <summary>Answers that were not the page (its own page, a cut-off one...).</summary>
    public long BadResponses { get; init; }

    /// <summary>Sessions of loaders open on it now (see <see cref="BlockAwarePageLoader"/>).</summary>
    public int Sessions { get; init; }
}

/// <summary>
/// Proxies for <see cref="HttpPageLoader"/>: given out in turn, put to rest for a site that blocks them
/// (<see cref="BlockPolicy"/>), and for every site after <see cref="MaxConsecutiveFailures"/> failures in a row.
/// One failure or bad response already puts a proxy after the clean ones for a while: it is taken only when no clean one is free.
/// With <see cref="RestGrowth"/> over 1 every rest of a proxy is longer than the one before (up to <see cref="MaxRest"/>), and only
/// good loads in a row make them shorter again: a proxy that fails again and again is used less and less.
/// The loaders built on <see cref="BlockAwarePageLoader"/> open a session on a proxy (a client with its cookies and a user agent)
/// and keep loading through it while it does well; a new session takes a proxy no other session uses when there is one.
/// When every proxy rests for a site, a load waits for the first one to come back (the scraper's RequestTimeout limits the wait).
/// Thread-safe.
/// <para>
/// The settings (<see cref="BlockPolicy"/>, <see cref="MaxConsecutiveFailures"/>, the pauses...) are read each time they are needed:
/// a list from <see cref="FromFile"/> or <see cref="FromUrlAsync"/> is set after it is made, and a change applies from the next
/// block, failure or check of the file on.
/// </para>
/// </summary>
/// <example>
/// <code>
/// var proxies = ProxyList.FromFile("proxies.txt");                      // re-read when the file changes
/// // or: await ProxyList.FromUrlAsync(new Uri("https://proxies.example/list.txt"));   // re-read every 5 minutes
/// proxies.DeadProxyPause = TimeSpan.FromMinutes(30);
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
    public ProxyList(IEnumerable<string> lines, BlockPolicy blockPolicy = BlockPolicy.Block1Hour, ILogger? logger = null)
        : this(null, lines, blockPolicy, logger)
    {
    }

    private ProxyList(LineSource? source, IEnumerable<string> lines, BlockPolicy blockPolicy, ILogger? logger)
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
    public static ProxyList FromFile(string path, BlockPolicy blockPolicy = BlockPolicy.Block1Hour, ILogger? logger = null)
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
    public static async Task<ProxyList> FromUrlAsync(Uri url, BlockPolicy blockPolicy = BlockPolicy.Block1Hour, TimeSpan? refreshInterval = null,
        HttpClient? httpClient = null, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        var (source, lines) = await LineSource.OpenUrlAsync(url, refreshInterval, httpClient, What, logger ?? NullLogger.Instance, cancellationToken).ConfigureAwait(false);
        return new ProxyList(source, lines, blockPolicy, logger);
    }

    /// <summary>
    /// Proxies read by a delegate: an API that needs a POST or a key in the body, a database... The delegate is called again every
    /// <paramref name="refreshInterval"/> (5 minutes by default) in the background; when it throws, the old list stays. Proxies that
    /// stay in the list keep their state.
    /// </summary>
    /// <param name="read">Returns the proxy lines (see <see cref="ProxyAddress.TryParse"/>); bad lines are skipped.</param>
    /// <param name="name">Name of the source for the log, without secrets.</param>
    /// <param name="blockPolicy">What happens to a proxy a site blocks.</param>
    /// <param name="refreshInterval">How often the list is read again.</param>
    /// <param name="logger">Logger for blocks, rests and refresh failures.</param>
    /// <param name="cancellationToken">Cancels the first read.</param>
    /// <exception cref="ArgumentException">No line of the first read is a proxy.</exception>
    public static async Task<ProxyList> FromSourceAsync(Func<CancellationToken, Task<IEnumerable<string>>> read, string name,
        BlockPolicy blockPolicy = BlockPolicy.Block1Hour, TimeSpan? refreshInterval = null, ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        var (source, lines) = await LineSource.OpenAsync(async token => [.. await read(token).ConfigureAwait(false)], name, refreshInterval, What,
            logger ?? NullLogger.Instance, cancellationToken).ConfigureAwait(false);
        return new ProxyList(source, lines, blockPolicy, logger);
    }

    private const string What = "Proxy list";

    /// <summary>What happens to a proxy a site blocks. An hour of rest for that site by default.</summary>
    public BlockPolicy BlockPolicy { get; set; }

    /// <summary>Failures in a row (no connection, 407...) after which a proxy rests for every site.</summary>
    public int MaxConsecutiveFailures { get; set; } = 3;

    /// <summary>How long a failing proxy rests.</summary>
    public TimeSpan DeadProxyPause { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Bad responses in a row (not the page: the proxy's own page, a cut-off one...) after which a proxy rests for that site.
    /// Counted per site, so a broken site does not take the proxies from the others.
    /// </summary>
    public int MaxConsecutiveBadResponses { get; set; } = 3;

    /// <summary>How long a proxy with bad responses in a row rests for the site.</summary>
    public TimeSpan BadResponsePause { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How much longer every next rest of a proxy is: after failures in a row (for every site), and after a block or bad responses in a
    /// row (for that site, counted apart). 3: a <see cref="DeadProxyPause"/> of 10 minutes, then 30, 90... up to <see cref="MaxRest"/>;
    /// an hour of <see cref="BlockPolicy.Block1Hour"/>, then 3 hours, 9... Five good loads in a row make the next rest a step
    /// shorter. 1, the default: rests don't grow.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Less than 1.</exception>
    public double RestGrowth
    {
        get;
        set => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "RestGrowth is 1 or more");
    } = 1;

    /// <summary>The longest a grown rest gets (<see cref="RestGrowth"/>); a day by default. A longer rest by itself stays.</summary>
    public TimeSpan MaxRest { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How often the file of <see cref="FromFile"/> is checked for changes.</summary>
    public TimeSpan FileCheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Time source; tests set a manual one before the first load.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Number of proxies.</summary>
    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    private RestRules Rules => new(BlockPolicy, MaxConsecutiveFailures, DeadProxyPause, MaxConsecutiveBadResponses, BadResponsePause, RestGrowth, MaxRest);

    /// <summary>State of every proxy.</summary>
    public IReadOnlyList<ProxyStatus> Status()
    {
        lock (_lock)
        {
            var now = Clock.GetUtcNow();
            return [.. _entries.Select(e => new ProxyStatus
            {
                Address = e.Address,
                Loads = e.Health.Loads,
                Successes = e.Health.Successes,
                Failures = e.Health.Failures,
                Blocks = e.Health.Blocks,
                Dead = e.Health.DeadUntil > now,
                BlockedSites = e.Health.CountBlockedSites(now),
                BadResponses = e.Health.BadResponses,
                Sessions = e.Health.Sessions,
            })];
        }
    }

    /// <summary>One-line summary.</summary>
    public override string ToString()
    {
        var status = Status();
        return $"{status.Count} proxies: {status.Count(s => !s.Dead)} alive, {status.Count(s => s.Dead)} resting after failures, " +
               $"{status.Count(s => s.BlockedSites > 0)} resting for some site, {status.Count(s => s.Sessions > 0)} in sessions; {status.Sum(s => s.Loads)} loads, {status.Sum(s => s.Failures)} failures, " +
               $"{status.Sum(s => s.Blocks)} blocks, {status.Sum(s => s.BadResponses)} bad responses";
    }

    // ---------------------------------------------------------------- for loaders

    /// <summary>
    /// The next proxy that may load from <paramref name="site"/>; waits while every proxy rests for it. A loader reports how the
    /// load went with <see cref="ReportSuccess"/>, <see cref="ReportFailure"/> or <see cref="ReportBlocked"/>, and whether the answer
    /// was the page with <see cref="ReportGoodResponse"/> or <see cref="ReportBadResponse"/>.
    /// </summary>
    /// <param name="site">Host of the page.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <exception cref="NonRetryableException">Every proxy is removed for the site (<see cref="BlockPolicy.Remove"/>).</exception>
    public Task<Entry> AcquireAsync(string site, CancellationToken cancellationToken) => TakeAsync(site, session: false, cancellationToken);

    /// <summary>
    /// A proxy for a new session of a loader: as <see cref="AcquireAsync(string, CancellationToken)"/>, but a proxy no session uses
    /// goes first, and the proxy counts the session until <see cref="CloseSession"/>.
    /// </summary>
    internal Task<Entry> AcquireForSessionAsync(string site, CancellationToken cancellationToken) => TakeAsync(site, session: true, cancellationToken);

    private async Task<Entry> TakeAsync(string site, bool session, CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                ReloadIfChanged();
                var now = Clock.GetUtcNow();
                var index = Health.Pick(_entries, e => e.Health, _cursor, site, now, Rules);
                if (index >= 0)
                {
                    var entry = _entries[index];
                    _cursor = (index + 1) % _entries.Count;
                    entry.Health.Loads++;
                    if (session) entry.Health.Sessions++;
                    _waitLogged.Remove(site);
                    return entry;
                }

                var next = _entries.Select(e => e.Health.BackAt(site)).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
                if (next == DateTimeOffset.MaxValue)
                {
                    throw new NonRetryableException($"Every proxy is removed for {site} (BlockPolicy.Remove)");
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

    /// <summary>
    /// Whether a session open on <paramref name="entry"/> may load from <paramref name="site"/> now: the proxy is still in the list
    /// and rests neither after failures nor for the site.
    /// </summary>
    internal bool MayLoad(Entry entry, string site)
    {
        lock (_lock)
        {
            ReloadIfChanged();
            return entry.Health.Listed && !entry.Health.Rests(site, Clock.GetUtcNow());
        }
    }

    /// <summary>Whether the proxy is still in the list (a list read again may drop it).</summary>
    internal bool IsListed(Entry entry)
    {
        lock (_lock) return entry.Health.Listed;
    }

    /// <summary>A session open on the proxy takes another load.</summary>
    internal void CountLoad(Entry entry)
    {
        lock (_lock) entry.Health.Loads++;
    }

    /// <summary>A session that <see cref="AcquireForSessionAsync"/> opened on the proxy is closed.</summary>
    internal void CloseSession(Entry entry)
    {
        lock (_lock)
        {
            if (entry.Health.Sessions > 0) entry.Health.Sessions--;
        }
    }

    /// <summary>The proxy gave an answer: its failures in a row start again.</summary>
    public void ReportSuccess(Entry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock) entry.Health.Success();
        CountResult("success");
    }

    /// <summary>
    /// No answer through the proxy (no connection, a timeout, 407...): it goes after the clean proxies for
    /// <see cref="DeadProxyPause"/>, and after <see cref="MaxConsecutiveFailures"/> in a row it rests for every site.
    /// </summary>
    public void ReportFailure(Entry entry, string reason)
    {
        ArgumentNullException.ThrowIfNull(entry);
        CountResult("failure");
        TimeSpan? rest;
        lock (_lock) rest = entry.Health.Failure(Clock.GetUtcNow(), Rules);
        if (rest != null)
        {
            _logger.LogWarning("Proxy {Proxy} failed {Count} times in a row ({Reason}): it rests {Pause}", entry.Address, MaxConsecutiveFailures, reason, rest);
        }
    }

    /// <summary>A site blocked an answer of the proxy: it rests for that site as <see cref="BlockPolicy"/> says.</summary>
    public void ReportBlocked(Entry entry, string site)
    {
        ArgumentNullException.ThrowIfNull(entry);
        CountResult("blocked");
        TimeSpan? rest;
        lock (_lock) rest = entry.Health.Blocked(site, Clock.GetUtcNow(), Rules);
        if (rest != null) _logger.LogDebug("Proxy {Proxy} is blocked on {Site}: {Policy}, it rests {Pause}", entry.Address, site, BlockPolicy, rest);
    }

    /// <summary>
    /// The proxy answered, but not with the page of <paramref name="site"/> (its own page, a cut-off one...): for that site it goes
    /// after the clean proxies for <see cref="BadResponsePause"/>, and after <see cref="MaxConsecutiveBadResponses"/> in a row it
    /// rests for the site <see cref="BadResponsePause"/>.
    /// </summary>
    public void ReportBadResponse(Entry entry, string site, string reason)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(site);
        CountResult("bad_response");
        TimeSpan? rest;
        lock (_lock) rest = entry.Health.BadResponse(site, Clock.GetUtcNow(), Rules);
        if (rest != null)
        {
            _logger.LogDebug("Proxy {Proxy} gave {Count} bad responses in a row on {Site} ({Reason}): it rests {Pause} for the site",
                entry.Address, MaxConsecutiveBadResponses, site, reason, rest);
        }
    }

    /// <summary>The proxy gave the page of <paramref name="site"/>: its bad responses in a row for the site start again.</summary>
    public void ReportGoodResponse(Entry entry, string site)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(site);
        lock (_lock) entry.Health.GoodResponse(site);
    }

    /// <summary>
    /// A load of a session failed, was blocked or was not the page, but the session goes on (it did well before): the proxy counts it,
    /// without a rest or a place after the clean proxies.
    /// </summary>
    internal void CountForgiven(Entry entry, SessionFailure failure)
    {
        lock (_lock)
        {
            switch (failure)
            {
                case SessionFailure.Failure: entry.Health.Failures++; break;
                case SessionFailure.Blocked: entry.Health.Blocks++; break;
                default: entry.Health.BadResponses++; break;
            }
        }
        CountResult(ResultOf(failure));
    }

    internal static string ResultOf(SessionFailure failure) => failure switch
    {
        SessionFailure.Failure => "failure",
        SessionFailure.Blocked => "blocked",
        _ => "bad_response",
    };

    // ---------------------------------------------------------------- metrics

    private LoaderInstruments? _metrics;

    /// <summary>
    /// A run with <see cref="ScraperOptions.PublishMetrics"/> uses a loader with this list: from now on its reports are counted
    /// (<c>z3l.scraper.proxy.results</c>) and its proxies are observed by state (<c>z3l.scraper.proxies</c>).
    /// </summary>
    internal void PublishMetrics()
    {
        if (Volatile.Read(ref _metrics) != null) return;
        var instruments = ScraperTelemetry.LoaderInstruments;
        instruments.Add(this);
        Volatile.Write(ref _metrics, instruments);
    }

    private void CountResult(string result) =>
        Volatile.Read(ref _metrics)?.ProxyResults.Add(1, new KeyValuePair<string, object?>(ScraperTelemetry.ResultTag, result));

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
        foreach (var gone in _entries.Except(entries)) gone.Health.Listed = false;
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

        internal Health Health { get; } = new();

        /// <summary>The proxy, without its password.</summary>
        public override string ToString() => Address.ToString();
    }
}
