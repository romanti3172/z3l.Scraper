using System.Net;
using System.Net.Http.Headers;

namespace z3l.Scraper.Loading;

/// <summary>Settings of <see cref="HttpPageLoader"/>.</summary>
public sealed class HttpPageLoaderOptions
{
    /// <summary>User-Agent header sent with every request unless the request sets its own.</summary>
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";

    /// <summary>Headers sent with every request.</summary>
    public Dictionary<string, string> DefaultHeaders { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
        ["Accept-Language"] = "en-US,en;q=0.9",
    };

    /// <summary>Proxy for all requests.</summary>
    public IWebProxy? Proxy { get; set; }

    /// <summary>Keep cookies between requests (one cookie jar for the loader).</summary>
    public bool UseCookies { get; set; } = true;

    /// <summary>Largest body to read; bigger responses throw and are retried. 0 means no limit.</summary>
    public long MaxResponseBytes { get; set; } = 50 * 1024 * 1024;
}

/// <summary>Loads pages with <see cref="HttpClient"/>. Thread-safe; dispose it when the scraping is over.</summary>
public sealed class HttpPageLoader : IPageLoader, IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly HttpPageLoaderOptions _options;

    /// <summary>Creates a loader with its own <see cref="HttpClient"/> (decompression, cookies, connection pooling).</summary>
    public HttpPageLoader(HttpPageLoaderOptions? options = null)
    {
        _options = options ?? new HttpPageLoaderOptions();
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = _options.UseCookies,
            Proxy = _options.Proxy,
            UseProxy = _options.Proxy != null,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = true,
        };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };   // the scraper applies RequestTimeout
        _ownsClient = true;
    }

    /// <summary>Uses an existing client (for example from <c>IHttpClientFactory</c>); it is not disposed by the loader.</summary>
    public HttpPageLoader(HttpClient client, HttpPageLoaderOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? new HttpPageLoaderOptions();
        _ownsClient = false;
    }

    /// <inheritdoc />
    public async Task<PageResponse> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(request.Method, request.Url);

        if (request.Body != null)
        {
            message.Content = new StringContent(request.Body);
            message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType ?? "application/x-www-form-urlencoded");
        }

        message.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        foreach (var header in _options.DefaultHeaders) SetHeader(message, header.Key, header.Value);
        if (request.Headers != null)
        {
            foreach (var header in request.Headers) SetHeader(message, header.Key, header.Value);
        }

        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (_options.MaxResponseBytes > 0 && response.Content.Headers.ContentLength > _options.MaxResponseBytes)
        {
            throw new HttpRequestException($"Response is {response.Content.Headers.ContentLength} bytes, more than MaxResponseBytes {_options.MaxResponseBytes}");
        }

        var body = await ReadBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers) headers[header.Key] = string.Join(", ", header.Value);
        foreach (var header in response.Content.Headers) headers[header.Key] = string.Join(", ", header.Value);

        var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? request.Url;
        return new PageResponse((int)response.StatusCode, finalUrl, body, response.Content.Headers.ContentType?.ToString(), headers);
    }

    private async Task<byte[]> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (_options.MaxResponseBytes <= 0) return await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > _options.MaxResponseBytes)
            {
                throw new HttpRequestException($"Response is more than MaxResponseBytes {_options.MaxResponseBytes}");
            }
        }
        return buffer.ToArray();
    }

    private static void SetHeader(HttpRequestMessage message, string name, string value)
    {
        if (message.Content != null && name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
        {
            message.Content.Headers.Remove(name);
            message.Content.Headers.TryAddWithoutValidation(name, value);
            return;
        }
        message.Headers.Remove(name);
        message.Headers.TryAddWithoutValidation(name, value);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
