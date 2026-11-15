using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;

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

    /// <summary>One proxy for all requests; when null (and no <see cref="Proxies"/>) the system proxy settings are used.</summary>
    public IWebProxy? Proxy { get; set; }

    /// <summary>
    /// Proxies to rotate: each load takes the next one that may load from the site, a blocked answer rests the proxy for that
    /// site (<see cref="ProxyList.BlockPolicy"/>), failures rest it for all sites. http, https, socks4, socks4a and socks5 proxies,
    /// with or without a login. Every proxy has its own connections and cookies.
    /// </summary>
    public ProxyList? Proxies { get; set; }

    /// <summary>Keep cookies between requests (one cookie jar for the loader, or one per proxy).</summary>
    public bool UseCookies { get; set; } = true;

    /// <summary>Largest body to read; a bigger response fails the request without retries. 0 means no limit.</summary>
    public long MaxResponseBytes { get; set; } = 50 * 1024 * 1024;
}

/// <summary>Loads pages with <see cref="HttpClient"/>, directly or through rotating proxies. Thread-safe; dispose it when the scraping is over.</summary>
public sealed class HttpPageLoader : IBlockAwarePageLoader, IDisposable
{
    private readonly HttpClient? _client;
    private readonly bool _ownsClient;
    private readonly HttpPageLoaderOptions _options;
    private readonly ConcurrentDictionary<string, Lazy<HttpClient>> _proxyClients = new();

    /// <summary>Which proxy loaded a response, so a block reported for it rests the right proxy.</summary>
    private readonly ConditionalWeakTable<PageResponse, ProxyList.Entry> _servedBy = [];

    /// <summary>Creates a loader with its own <see cref="HttpClient"/> (decompression, cookies, connection pooling).</summary>
    /// <exception cref="ArgumentException">Both <see cref="HttpPageLoaderOptions.Proxy"/> and <see cref="HttpPageLoaderOptions.Proxies"/> are set.</exception>
    public HttpPageLoader(HttpPageLoaderOptions? options = null)
    {
        _options = options ?? new HttpPageLoaderOptions();
        if (_options.Proxy != null && _options.Proxies != null) throw new ArgumentException("Set Proxy or Proxies, not both", nameof(options));

        if (_options.Proxies == null)
        {
            _client = CreateClient(_options.Proxy);
            _ownsClient = true;
        }
    }

    /// <summary>Uses an existing client (for example from <c>IHttpClientFactory</c>); it is not disposed by the loader.</summary>
    /// <exception cref="ArgumentException"><see cref="HttpPageLoaderOptions.Proxies"/> is set: rotating proxies need clients of the loader.</exception>
    public HttpPageLoader(HttpClient client, HttpPageLoaderOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? new HttpPageLoaderOptions();
        if (_options.Proxies != null) throw new ArgumentException("Rotating proxies need clients made by the loader: use HttpPageLoader(options)", nameof(options));
        _ownsClient = false;
    }

    /// <inheritdoc />
    public async Task<PageResponse> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new NonRetryableException($"Not an http(s) URL: \"{request.Url}\"");
        }

        using var message = CreateMessage(request, uri);

        if (_options.Proxies is not { } proxies) return await SendAsync(_client!, message, request, cancellationToken).ConfigureAwait(false);

        var proxy = await proxies.AcquireAsync(uri.Host, cancellationToken).ConfigureAwait(false);
        var client = _proxyClients.GetOrAdd(proxy.Address.Key, _ => new Lazy<HttpClient>(() => CreateClient(proxy.Address.ToWebProxy()))).Value;
        PageResponse response;
        try
        {
            response = await SendAsync(client, message, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or NonRetryableException))
        {
            proxies.ReportFailure(proxy, ex.Message);       // no answer through this proxy
            throw;
        }

        if (response.StatusCode == (int)HttpStatusCode.ProxyAuthenticationRequired) proxies.ReportFailure(proxy, "407 Proxy Authentication Required");
        else proxies.ReportSuccess(proxy);
        _servedBy.AddOrUpdate(response, proxy);
        return response;
    }

    /// <summary>The scraper saw a block in this response: the proxy that loaded it rests for the site.</summary>
    public void ReportBlocked(ScrapeRequest request, PageResponse response, string reason)
    {
        if (_options.Proxies is { } proxies && _servedBy.TryGetValue(response, out var proxy) && Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            proxies.ReportBlocked(proxy, uri.Host);
        }
    }

    private HttpClient CreateClient(IWebProxy? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = _options.UseCookies,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = true,
        };
        if (proxy != null)
        {
            handler.Proxy = proxy;     // without it the system proxy (HTTP_PROXY, Windows settings) is used
        }
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };   // the scraper applies RequestTimeout
    }

    private HttpRequestMessage CreateMessage(ScrapeRequest request, Uri uri)
    {
        var message = new HttpRequestMessage(request.Method, uri);

        if (request.Body != null)
        {
            var contentType = request.ContentType ?? "application/x-www-form-urlencoded";
            if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType))
            {
                message.Dispose();
                throw new NonRetryableException($"Bad ContentType \"{contentType}\"");
            }
            message.Content = new StringContent(request.Body);
            message.Content.Headers.ContentType = mediaType;
        }

        message.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        foreach (var header in _options.DefaultHeaders) SetHeader(message, header.Key, header.Value);
        if (request.Headers != null)
        {
            foreach (var header in request.Headers) SetHeader(message, header.Key, header.Value);
        }
        return message;
    }

    private async Task<PageResponse> SendAsync(HttpClient client, HttpRequestMessage message, ScrapeRequest request, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (_options.MaxResponseBytes > 0 && response.Content.Headers.ContentLength > _options.MaxResponseBytes)
        {
            throw new NonRetryableException($"Response is {response.Content.Headers.ContentLength} bytes, more than MaxResponseBytes {_options.MaxResponseBytes}");
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
                throw new NonRetryableException($"Response is more than MaxResponseBytes {_options.MaxResponseBytes}");
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
        if (_ownsClient) _client?.Dispose();
        foreach (var client in _proxyClients.Values)
        {
            if (client.IsValueCreated) client.Value.Dispose();
        }
        _proxyClients.Clear();
    }
}
