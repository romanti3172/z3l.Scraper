using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace z3l.Scraper.Loading;

/// <summary>
/// User agents for <see cref="HttpPageLoader"/>, given out in turn. From a list, a text file (read again when it changes) or a URL
/// (read again every refresh interval). One user agent per line; empty lines and lines starting with '#' are skipped.
/// Thread-safe.
/// </summary>
/// <example>
/// <code>
/// var options = new HttpPageLoaderOptions
/// {
///     Proxies = ProxyList.FromFile("proxies.txt"),
///     UserAgents = UserAgentList.FromFile("useragents.txt"),     // every proxy keeps one user agent
/// };
/// </code>
/// </example>
public sealed class UserAgentList
{
    private const string What = "User agent list";

    private readonly object _lock = new();
    private readonly ILogger _logger;
    private readonly LineSource? _source;
    private string[] _agents;
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
        _agents = Parse(lines);
        if (_agents.Length == 0) throw new ArgumentException(source == null ? "No user agents in the list" : $"No user agents in {source.Name}", nameof(lines));
    }

    /// <summary>User agents from a text file, one per line. The file is read again when it changes.</summary>
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

    /// <summary>How often the file is checked for changes.</summary>
    public TimeSpan FileCheckInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Time source; tests set a manual one before the first call.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Number of user agents.</summary>
    public int Count
    {
        get { lock (_lock) return _agents.Length; }
    }

    /// <summary>The next user agent.</summary>
    public string Next()
    {
        lock (_lock)
        {
            Refresh();
            var agent = _agents[_cursor % _agents.Length];
            _cursor = (_cursor + 1) % _agents.Length;
            return agent;
        }
    }

    private void Refresh()
    {
        if (_source?.Poll(Clock.GetUtcNow(), FileCheckInterval) is not { } lines) return;
        var agents = Parse(lines);
        if (agents.Length == 0)
        {
            _logger.LogWarning("{Source} has no user agents now: the old list stays", _source.Name);
            return;
        }
        _agents = agents;
        _cursor = 0;
        _logger.LogInformation("User agent list {Source} read again: {Count} user agents", _source.Name, agents.Length);
    }

    private static string[] Parse(IEnumerable<string> lines) =>
        [.. lines.Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#').Distinct(StringComparer.Ordinal)];
}
