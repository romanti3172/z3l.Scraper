using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using z3l.Scraper.Diagnostics;

namespace z3l.Scraper.Loading;

/// <summary>State of one user agent, for logs and dashboards.</summary>
public sealed record UserAgentStatus
{
    /// <summary>The user agent.</summary>
    public required string UserAgent { get; init; }

    /// <summary>Loads made with it.</summary>
    public required long Loads { get; init; }

    /// <summary>Loads that got an answer.</summary>
    public required long Successes { get; init; }

    /// <summary>Answers that sites blocked.</summary>
    public required long Blocks { get; init; }

    /// <summary>Sites it rests for now (blocked, or bad responses in a row).</summary>
    public required int BlockedSites { get; init; }

    /// <summary>Answers that were not the page.</summary>
    public required long BadResponses { get; init; }

    /// <summary>Sessions of loaders that use it now.</summary>
    public required int Sessions { get; init; }
}

/// <summary>
/// User agents for <see cref="HttpPageLoader"/>, given out in turn. From a list, a text file (read again when it changes) or a URL
/// (read again every refresh interval). One user agent per line; empty lines and lines starting with '#' are skipped.
/// <para>
/// A loader built on <see cref="BlockAwarePageLoader"/> gives every session (<see cref="LoaderSession"/>) one user agent, and tells the
/// list what the sites said, as it tells the proxy list: a user agent whose session a site blocked rests for that site as
/// <see cref="BlockPolicy"/> says, bad responses in a row rest it for the site, and one bad response puts it after the clean ones for
/// that site for a while. Failures without an answer (no connection, a timeout, 407) are the proxy's, not the user agent's: they are not
/// counted here. When every user agent rests for a site, a new session takes the one that comes
/// back first (a load never waits for a user agent). Thread-safe.
/// </para>
/// <para>
/// The settings (<see cref="BlockPolicy"/>, <see cref="MaxConsecutiveBadResponses"/>, <see cref="BadResponsePause"/>,
/// <see cref="FileCheckInterval"/>) are read each time they are needed: a list from <see cref="FromFile"/> or
/// <see cref="FromUrlAsync"/> is set after it is made, and a change applies from the next block or check of the file on.
/// </para>
/// </summary>
/// <example>
/// <code>
/// var options = new HttpPageLoaderOptions
/// {
///     Proxies = ProxyList.FromFile("proxies.txt"),
///     UserAgents = UserAgentList.FromFile("useragents.txt"),     // every session takes one
/// };
/// </code>
/// </example>
public sealed class UserAgentList
{
    private const string What = "User agent list";

    private readonly object _lock = new();
    private readonly ILogger _logger;
    private readonly LineSource? _source;
    private List<Entry> _entries;
    private int _cursor;

    /// <summary>A list of user agents.</summary>
    /// <exception cref="ArgumentException">The list has no user agent.</exception>
    public UserAgentList(IEnumerable<string> lines, ILogger? logger = null)
        : this(null, lines, logger)
    {
    }

    private UserAgentList(LineSource? source, IEnumerable<string> lines, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(lines);
        _source = source;
        _logger = logger ?? NullLogger.Instance;
        _entries = Parse(lines, previous: null);
        if (_entries.Count == 0) throw new ArgumentException(source == null ? "No user agents in the list" : $"No user agents in {source.Name}", nameof(lines));
    }

    /// <summary>User agents from a text file, one per line. The file is read again when it changes; user agents that stay keep their state.</summary>
    /// <exception cref="FileNotFoundException">No such file.</exception>
    /// <exception cref="ArgumentException">The file has no user agent.</exception>
    public static UserAgentList FromFile(string path, ILogger? logger = null)
    {
        var (source, lines) = LineSource.OpenFile(path, What, logger ?? NullLogger.Instance);
        return new UserAgentList(source, lines, logger);
    }

    /// <summary>
    /// User agents from a URL that answers with text, one per line, or a JSON array of strings. The URL is read again every
    /// <paramref name="refreshInterval"/> (5 minutes by default) in the background; when it fails, the old list stays.
    /// </summary>
    /// <param name="url">http(s) URL of the list.</param>
    /// <param name="refreshInterval">How often the list is read again.</param>
    /// <param name="httpClient">Client for the requests (for example with an API key header); a shared one when null.</param>
    /// <param name="logger">Logger for refresh failures.</param>
    /// <param name="cancellationToken">Cancels the first read.</param>
    /// <exception cref="HttpRequestException">The first read failed.</exception>
    /// <exception cref="ArgumentException">The list has no user agent.</exception>
    public static async Task<UserAgentList> FromUrlAsync(Uri url, TimeSpan? refreshInterval = null, HttpClient? httpClient = null,
        ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        var (source, lines) = await LineSource.OpenUrlAsync(url, refreshInterval, httpClient, What, logger ?? NullLogger.Instance, cancellationToken).ConfigureAwait(false);
        return new UserAgentList(source, lines, logger);
    }

    /// <summary>
    /// User agents read by a delegate (an API that needs a POST, a database...). The delegate is called again every
    /// <paramref name="refreshInterval"/> (5 minutes by default) in the background; when it throws, the old list stays.
    /// </summary>
    /// <param name="read">Returns the user agents.</param>
    /// <param name="name">Name of the source for the log, without secrets.</param>
    /// <param name="refreshInterval">How often the list is read again.</param>
    /// <param name="logger">Logger for refresh failures.</param>
    /// <param name="cancellationToken">Cancels the first read.</param>
    /// <exception cref="ArgumentException">The first read has no user agent.</exception>
    public static async Task<UserAgentList> FromSourceAsync(Func<CancellationToken, Task<IEnumerable<string>>> read, string name,
        TimeSpan? refreshInterval = null, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        var (source, lines) = await LineSource.OpenAsync(async token => [.. await read(token).ConfigureAwait(false)], name, refreshInterval, What,
            logger ?? NullLogger.Instance, cancellationToken).ConfigureAwait(false);
        return new UserAgentList(source, lines, logger);
    }

    /// <summary>What happens to a user agent whose session a site blocked: it rests for that site. An hour by default.</summary>
    public BlockPolicy BlockPolicy { get; set; } = BlockPolicy.Block1Hour;

    /// <summary>Bad responses in a row on a site after which a user agent rests for that site.</summary>
    public int MaxConsecutiveBadResponses { get; set; } = 3;

    /// <summary>How long a user agent with bad responses in a row rests for the site, and how long one puts it after the clean ones.</summary>
    public TimeSpan BadResponsePause { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How often the file of <see cref="FromFile"/> is checked for changes.</summary>
    public TimeSpan FileCheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Time source; tests set a manual one before the first call.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>The rules of the proxies, without failures: a user agent is not told of them.</summary>
    private RestRules Rules => new(BlockPolicy, int.MaxValue, TimeSpan.Zero, MaxConsecutiveBadResponses, BadResponsePause);

    /// <summary>Number of user agents.</summary>
    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>The next user agent in turn, whatever its state (for loaders without sessions).</summary>
    public string Next()
    {
        lock (_lock)
        {
            Refresh();
            var entry = _entries[_cursor % _entries.Count];
            _cursor = (_cursor + 1) % _entries.Count;
            entry.Health.Loads++;
            return entry.UserAgent;
        }
    }

    /// <summary>State of every user agent.</summary>
    public IReadOnlyList<UserAgentStatus> Status()
    {
        lock (_lock)
        {
            var now = Clock.GetUtcNow();
            return [.. _entries.Select(e => new UserAgentStatus
            {
                UserAgent = e.UserAgent,
                Loads = e.Health.Loads,
                Successes = e.Health.Successes,
                Blocks = e.Health.Blocks,
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
        return $"{status.Count} user agents: {status.Count(s => s.BlockedSites > 0)} resting for some site, " +
               $"{status.Count(s => s.Sessions > 0)} in sessions; {status.Sum(s => s.Loads)} loads, " +
               $"{status.Sum(s => s.Blocks)} blocks, {status.Sum(s => s.BadResponses)} bad responses";
    }

    // ---------------------------------------------------------------- for loaders

    /// <summary>
    /// A user agent for a new session that loads from <paramref name="site"/>: the next clean one no session uses, else as
    /// <see cref="ProxyList"/> picks; when every user agent rests for the site, the one that comes back first.
    /// </summary>
    internal Entry AcquireForSession(string site)
    {
        lock (_lock)
        {
            Refresh();
            var now = Clock.GetUtcNow();
            var index = Health.Pick(_entries, e => e.Health, _cursor, site, now, Rules);
            if (index < 0)
            {
                var soonest = _entries.Select(e => e.Health.BackAt(site)).Min();
                index = _entries.FindIndex(e => e.Health.BackAt(site) == soonest);
                _logger.LogDebug("Every user agent rests for {Site}: the one that comes back first is taken", site);
            }
            var entry = _entries[index];
            _cursor = (index + 1) % _entries.Count;
            entry.Health.Loads++;
            entry.Health.Sessions++;
            return entry;
        }
    }

    /// <summary>Whether a session with this user agent may load from the site now (it rests neither for it nor for every site).</summary>
    internal bool MayLoad(Entry entry, string site)
    {
        lock (_lock)
        {
            Refresh();
            return entry.Health.Listed && !entry.Health.Rests(site, Clock.GetUtcNow());
        }
    }

    /// <summary>A session with the user agent takes another load.</summary>
    internal void CountLoad(Entry entry)
    {
        lock (_lock) entry.Health.Loads++;
    }

    /// <summary>A session with the user agent is closed.</summary>
    internal void CloseSession(Entry entry)
    {
        lock (_lock)
        {
            if (entry.Health.Sessions > 0) entry.Health.Sessions--;
        }
    }

    /// <summary>A load with the user agent got an answer: its failures in a row start again.</summary>
    internal void ReportSuccess(Entry entry)
    {
        lock (_lock) entry.Health.Success();
        CountResult("success");
    }

    /// <summary>A site blocked a session with the user agent: it rests for that site as <see cref="BlockPolicy"/> says.</summary>
    internal void ReportBlocked(Entry entry, string site)
    {
        CountResult("blocked");
        TimeSpan? rest;
        lock (_lock) rest = entry.Health.Blocked(site, Clock.GetUtcNow(), Rules);
        if (rest != null) _logger.LogDebug("User agent {UserAgent} is blocked on {Site}: {Policy}", entry.UserAgent, site, BlockPolicy);
    }

    /// <summary>An answer with the user agent was not the page of the site: as <see cref="ProxyList.ReportBadResponse"/>.</summary>
    internal void ReportBadResponse(Entry entry, string site, string reason)
    {
        CountResult("bad_response");
        TimeSpan? rest;
        lock (_lock) rest = entry.Health.BadResponse(site, Clock.GetUtcNow(), Rules);
        if (rest != null)
        {
            _logger.LogDebug("User agent {UserAgent} gave {Count} bad responses in a row on {Site} ({Reason}): it rests {Pause} for the site",
                entry.UserAgent, MaxConsecutiveBadResponses, site, reason, rest);
        }
    }

    /// <summary>An answer with the user agent was the page: its bad responses in a row for the site start again.</summary>
    internal void ReportGoodResponse(Entry entry, string site)
    {
        lock (_lock) entry.Health.GoodResponse(site);
    }

    /// <summary>A block or a bad response the session survived: counted, without a rest. A failure is not the user agent's.</summary>
    internal void CountForgiven(Entry entry, SessionFailure failure)
    {
        if (failure == SessionFailure.Failure) return;
        lock (_lock)
        {
            if (failure == SessionFailure.Blocked) entry.Health.Blocks++;
            else entry.Health.BadResponses++;
        }
        CountResult(ProxyList.ResultOf(failure));
    }

    // ---------------------------------------------------------------- metrics

    private LoaderInstruments? _metrics;

    /// <summary>
    /// A run with <see cref="ScraperOptions.PublishMetrics"/> uses a loader with this list: from now on its reports are counted
    /// (<c>z3l.scraper.user_agent.results</c>) and its user agents are observed by state (<c>z3l.scraper.user_agents</c>).
    /// </summary>
    internal void PublishMetrics()
    {
        if (Volatile.Read(ref _metrics) != null) return;
        var instruments = ScraperTelemetry.LoaderInstruments;
        instruments.Add(this);
        Volatile.Write(ref _metrics, instruments);
    }

    private void CountResult(string result) =>
        Volatile.Read(ref _metrics)?.UserAgentResults.Add(1, new KeyValuePair<string, object?>(ScraperTelemetry.ResultTag, result));

    // ---------------------------------------------------------------- file or URL

    private void Refresh()
    {
        if (_source?.Poll(Clock.GetUtcNow(), FileCheckInterval) is not { } lines) return;
        var entries = Parse(lines, _entries);
        if (entries.Count == 0)
        {
            _logger.LogWarning("{Source} has no user agents now: the old list stays", _source.Name);
            return;
        }
        foreach (var gone in _entries.Except(entries)) gone.Health.Listed = false;
        _entries = entries;
        _cursor = 0;
        _logger.LogInformation("User agent list {Source} read again: {Count} user agents", _source.Name, entries.Count);
    }

    /// <summary>User agents of the lines; one that was in the old list keeps its entry (its rests and counters).</summary>
    private static List<Entry> Parse(IEnumerable<string> lines, List<Entry>? previous)
    {
        var old = previous?.ToDictionary(e => e.UserAgent, StringComparer.Ordinal) ?? [];
        return [.. lines.Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#').Distinct(StringComparer.Ordinal)
            .Select(agent => old.TryGetValue(agent, out var entry) ? entry : new Entry(agent))];
    }

    /// <summary>A user agent of the list, given to a session.</summary>
    public sealed class Entry
    {
        internal Entry(string userAgent) => UserAgent = userAgent;

        /// <summary>The user agent.</summary>
        public string UserAgent { get; }

        internal Health Health { get; } = new();

        /// <summary>The user agent.</summary>
        public override string ToString() => UserAgent;
    }
}
