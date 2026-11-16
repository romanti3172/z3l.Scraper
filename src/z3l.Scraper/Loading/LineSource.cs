using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace z3l.Scraper.Loading;

/// <summary>
/// Where the lines of a list (proxies, user agents) come from: a file, read again when it changes, or a URL, read again
/// every refresh interval in the background. The owner calls <see cref="Poll"/> under its own lock; it never blocks on I/O
/// of the URL. A failed read keeps the old lines.
/// </summary>
internal sealed class LineSource
{
    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

    private readonly string? _path;
    private readonly Func<CancellationToken, Task<List<string>>>? _read;
    private readonly string? _sourceName;
    private readonly TimeSpan _urlInterval;
    private readonly string _what;
    private readonly ILogger _logger;
    private DateTime _fileWriteTime;
    private DateTimeOffset _nextCheck;
    private Task<List<string>?>? _fetch;

    private LineSource(string? path, Func<CancellationToken, Task<List<string>>>? read, string? sourceName, TimeSpan urlInterval, string what, ILogger logger)
    {
        _path = path;
        _read = read;
        _sourceName = sourceName;
        _urlInterval = urlInterval;
        _what = what;
        _logger = logger;
    }

    /// <summary>The file path, the URL without its login and query (an API key may be there), or the name of a source.</summary>
    public string Name => _path ?? _sourceName!;

    /// <summary>Opens a file and reads its lines.</summary>
    /// <exception cref="FileNotFoundException">No such file.</exception>
    public static (LineSource Source, List<string> Lines) OpenFile(string path, string what, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var lines = ReadFile(full);
        var source = new LineSource(full, null, null, default, what, logger) { _fileWriteTime = File.GetLastWriteTimeUtc(full) };
        return (source, lines);
    }

    /// <summary>Reads the lines of a URL: plain text, one per line, or a JSON array of strings.</summary>
    /// <exception cref="HttpRequestException">The URL did not answer with a success status.</exception>
    public static async Task<(LineSource Source, List<string> Lines)> OpenUrlAsync(
        Uri url, TimeSpan? refreshInterval, HttpClient? client, string what, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Not an http(s) URL: \"{url}\"", nameof(url));
        }
        var name = $"{url.Scheme}://{url.Authority}{url.AbsolutePath}";
        return await OpenAsync(token => ReadUrlAsync(url, client, token), name, refreshInterval, what, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the lines with a delegate (an API that needs a POST, a database...), again every refresh interval.</summary>
    public static async Task<(LineSource Source, List<string> Lines)> OpenAsync(Func<CancellationToken, Task<List<string>>> read, string name,
        TimeSpan? refreshInterval, string what, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var interval = refreshInterval ?? TimeSpan.FromMinutes(5);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(refreshInterval), "The refresh interval must be positive");

        var source = new LineSource(null, read, name, interval, what, logger);
        var lines = await source._read!(cancellationToken).ConfigureAwait(false);
        return (source, lines);
    }

    /// <summary>
    /// New lines when the source changed since the last call, otherwise null. A file is checked every
    /// <paramref name="fileCheckInterval"/>; a URL is fetched in the background every refresh interval and its lines come with
    /// the first call after the fetch ends.
    /// </summary>
    public List<string>? Poll(DateTimeOffset now, TimeSpan fileCheckInterval)
    {
        return _path != null ? PollFile(now, fileCheckInterval) : PollUrl(now);
    }

    private List<string>? PollFile(DateTimeOffset now, TimeSpan interval)
    {
        if (now < _nextCheck) return null;
        _nextCheck = now + interval;
        try
        {
            var written = File.GetLastWriteTimeUtc(_path!);
            if (written == _fileWriteTime) return null;
            var lines = ReadFile(_path!);
            _fileWriteTime = written;
            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("{What} {Path} could not be read again ({Message}): the old list stays", _what, _path, ex.Message);
            return null;
        }
    }

    private List<string>? PollUrl(DateTimeOffset now)
    {
        if (_fetch != null)
        {
            if (!_fetch.IsCompleted) return null;
            var lines = _fetch.Result;      // FetchSafeAsync never throws
            _fetch = null;
            return lines;
        }

        // the first call starts the clock: the list was read when it was opened
        if (_nextCheck == default) _nextCheck = now + _urlInterval;
        if (now < _nextCheck) return null;
        _nextCheck = now + _urlInterval;
        _fetch = Task.Run(FetchSafeAsync);
        return null;
    }

    private async Task<List<string>?> FetchSafeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            return await _read!(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("{What} {Url} could not be read again ({Message}): the old list stays", _what, Name, ex.Message);
            return null;
        }
    }

    private static async Task<List<string>> ReadUrlAsync(Uri url, HttpClient? client, CancellationToken cancellationToken)
    {
        using var response = await (client ?? SharedClient.Value).GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseText(text);
    }

    /// <summary>A JSON array of strings, or text with one item per line.</summary>
    internal static List<string> ParseText(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.Length > 0 && trimmed[0] == '[')
        {
            try
            {
                using var json = JsonDocument.Parse(text);
                if (json.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return [.. json.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)];
                }
            }
            catch (JsonException)
            {
                // not JSON after all: a text line may start with '['
            }
        }

        var lines = new List<string>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    private static List<string> ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }
}
