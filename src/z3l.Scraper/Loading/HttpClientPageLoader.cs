using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;

namespace z3l.Scraper.Loading;

/// <summary>Settings of the loaders built on <see cref="HttpClientPageLoader"/>: <see cref="HttpPageLoader"/>, <see cref="StealthHttpPageLoader"/>.</summary>
public class HttpClientPageLoaderOptions
{
    /// <summary>User-Agent header sent with every request unless the request sets its own; not used when <see cref="UserAgents"/> is set.</summary>
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";

    /// <summary>
    /// User agents to rotate instead of <see cref="UserAgent"/>. With <see cref="Proxies"/> every proxy session (<see cref="ProxySession"/>)
    /// takes the next one and keeps it while it lives, as it keeps its cookies: a site sees one browser per address, and a new session
    /// (after a failure or a block) is a new browser. Without proxies every request takes the next one. A User-Agent in
    /// <see cref="DefaultHeaders"/> or in the request still wins.
    /// </summary>
    public UserAgentList? UserAgents { get; set; }

    /// <summary>Headers sent with every request; a header of the request wins over one of the same name here.</summary>
    public Dictionary<string, string> DefaultHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One proxy for all requests; when null (and no <see cref="Proxies"/>) the system proxy settings are used.</summary>
    public IWebProxy? Proxy { get; set; }

    /// <summary>
    /// Proxies to load through, in sessions (<see cref="ProxySession"/>): a session is a proxy, a user agent and an HttpClient with its
    /// own connections and cookies. A session that did well serves the next load; a failure, a block or a bad response throws it away
    /// whole, the proxy rests as the list says (<see cref="ProxyList.BlockPolicy"/> for blocks, failures for all sites), and the next load
    /// opens a new session on another proxy. http, https (TLS to the proxy), socks4, socks4a and socks5 proxies, with or without a login
    /// (<see cref="ProxyAddress.TryParse"/>).
    /// </summary>
    public ProxyList? Proxies { get; set; }

    /// <summary>
    /// https proxies (TLS to the proxy): take the proxy's certificate whatever it is, issued for another name (a proxy reached by its
    /// IP address), self-signed or expired; the sites' certificates are checked as always. The TLS to such a proxy only hides the
    /// traffic from listeners: as with an http proxy, someone in the middle could pose as the proxy and see its login and the http
    /// pages, while https pages stay encrypted up to the site. For <see cref="Proxies"/> and a <see cref="WebProxy"/> in
    /// <see cref="Proxy"/>.
    /// </summary>
    public bool AcceptAnyProxyCertificate { get; set; }

    /// <summary>Keep cookies between requests (one cookie jar for the loader, or one per proxy session).</summary>
    public bool UseCookies { get; set; } = true;

    /// <summary>Largest body to read; a bigger response fails the request without retries. 0 means no limit.</summary>
    public long MaxResponseBytes { get; set; } = 50 * 1024 * 1024;
}

/// <summary>
/// Base of the loaders that send requests with <see cref="HttpClient"/>, directly or through proxies: one client per proxy session
/// (<see cref="ProxySession"/>), with its own connections and cookies, thrown away with the session, and the body read into pooled
/// buffers up to
/// <see cref="HttpClientPageLoaderOptions.MaxResponseBytes"/>. A derived class decides what the request looks like
/// (<see cref="CreateMessage"/>) and may change the handler (<see cref="CreateHandler"/>). Thread-safe; dispose it when the scraping is
/// over.
/// </summary>
public abstract class HttpClientPageLoader : BlockAwarePageLoader, IDisposable
{
    private readonly HttpClient? _client;
    private readonly Lazy<HttpClient>? _ownClient;

    /// <summary>Creates a loader with its own clients (decompression, cookies, connection pooling), made on first use.</summary>
    /// <exception cref="ArgumentException">Both <see cref="HttpClientPageLoaderOptions.Proxy"/> and <see cref="HttpClientPageLoaderOptions.Proxies"/> are set.</exception>
    protected HttpClientPageLoader(HttpClientPageLoaderOptions options)
        : base((options ?? throw new ArgumentNullException(nameof(options))).Proxies, options.UserAgents, options.UserAgent)
    {
        Options = options;
        if (options.Proxy != null && options.Proxies != null) throw new ArgumentException("Set Proxy or Proxies, not both", nameof(options));

        // not in the constructor: CreateHandler is virtual, and a derived class is not ready yet
        if (options.Proxies == null) _ownClient = new Lazy<HttpClient>(() => CreateClient(options.Proxy));
    }

    /// <summary>Uses an existing client (for example from <c>IHttpClientFactory</c>); it is not disposed by the loader.</summary>
    /// <exception cref="ArgumentException"><see cref="HttpClientPageLoaderOptions.Proxies"/> is set: proxy sessions need clients of the loader.</exception>
    protected HttpClientPageLoader(HttpClient client, HttpClientPageLoaderOptions options)
        : base(null, (options ?? throw new ArgumentNullException(nameof(options))).UserAgents, options.UserAgent)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Options = options;
        if (options.Proxies != null) throw new ArgumentException("Proxy sessions need clients made by the loader: use the constructor with options", nameof(options));
    }

    /// <summary>Settings.</summary>
    protected HttpClientPageLoaderOptions Options { get; }

    /// <summary>
    /// The request of a page: its method and body (<see cref="NewMessage"/>), its headers and user agent, its HTTP version. The loader
    /// disposes it after the load.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="uri">Its URL, an absolute http(s) one.</param>
    /// <param name="userAgent">The user agent of this load; null for none.</param>
    protected abstract HttpRequestMessage CreateMessage(ScrapeRequest request, Uri uri, string? userAgent);

    /// <summary>
    /// Makes the handler of a client: one for the loader without <see cref="HttpClientPageLoaderOptions.Proxies"/>, one for every proxy
    /// session.
    /// The default is a <see cref="SocketsHttpHandler"/> with decompression, cookies (<see cref="HttpClientPageLoaderOptions.UseCookies"/>),
    /// connections renewed every 5 minutes, redirects and the proxy. Override it to wrap that one (<c>base.CreateHandler(proxy)</c>:
    /// Polly, logging, signed requests) or to replace it (a TLS library, another outgoing address); a replacement must go through
    /// <paramref name="proxy"/>.
    /// </summary>
    /// <param name="proxy">The proxy of the client; null for the system proxy settings.</param>
    protected virtual HttpMessageHandler CreateHandler(IWebProxy? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = Options.UseCookies,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = true,
        };
        if (proxy != null)
        {
            handler.Proxy = proxy;     // without it the system proxy (HTTP_PROXY, Windows settings) is used
        }
        if (Options.AcceptAnyProxyCertificate && (proxy as WebProxy)?.Address is { Scheme: "https" } address)
        {
            // one callback checks the TLS to the proxy and the TLS to the sites inside it: they differ by the host they are for
            var proxyHost = address.IdnHost.Trim('[', ']');
            handler.SslOptions.RemoteCertificateValidationCallback = (sender, _, _, errors) => errors == SslPolicyErrors.None
                || sender is SslStream { TargetHostName: { } host } && string.Equals(host.Trim('[', ']'), proxyHost, StringComparison.OrdinalIgnoreCase);
        }
        return handler;
    }

    /// <summary>A request with the method and the body of <paramref name="request"/>, and no headers yet.</summary>
    /// <exception cref="NonRetryableException"><see cref="ScrapeRequest.ContentType"/> is not a media type.</exception>
    protected static HttpRequestMessage NewMessage(ScrapeRequest request, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(request);
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
        return message;
    }

    /// <summary>Sets a header as it is, replacing one of the same name; a Content-* header goes to the body when there is one.</summary>
    protected static void SetHeader(HttpRequestMessage message, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(message);
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
    protected sealed override async Task<PageResponse> LoadThroughAsync(ScrapeRequest request, Uri uri, ProxySession? session, string? userAgent,
        CancellationToken cancellationToken)
    {
        // a session serves one load at a time: its client is made on its first load, with an empty cookie jar
        var client = session == null
            ? _client ?? _ownClient!.Value
            : session.State as HttpClient ?? (HttpClient)(session.State = CreateClient(session.Proxy.Address.ToWebProxy()));
        using var message = CreateMessage(request, uri, userAgent);
        return await SendAsync(client, message, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The session is thrown away: its client goes with its connections and cookies.</summary>
    protected sealed override void CloseSession(ProxySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        (session.State as HttpClient)?.Dispose();
        session.State = null;
    }

    private HttpClient CreateClient(IWebProxy? proxy) => new(CreateHandler(proxy)) { Timeout = Timeout.InfiniteTimeSpan };   // the scraper applies RequestTimeout

    private async Task<PageResponse> SendAsync(HttpClient client, HttpRequestMessage message, ScrapeRequest request, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (Options.MaxResponseBytes > 0 && response.Content.Headers.ContentLength > Options.MaxResponseBytes)
        {
            throw new NonRetryableException($"Response is {response.Content.Headers.ContentLength} bytes, more than MaxResponseBytes {Options.MaxResponseBytes}");
        }

        var body = await ReadBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers) headers[header.Key] = string.Join(", ", header.Value);
        foreach (var header in response.Content.Headers) headers[header.Key] = string.Join(", ", header.Value);

        var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? request.Url;
        return new PageResponse((int)response.StatusCode, finalUrl, body, response.Content.Headers.ContentType?.ToString(), headers);
    }

    /// <summary>
    /// Reads the body into pooled buffers and copies it once into an array of its size. Growing arrays of its own would
    /// cost about three times the page for every page, mostly on the large object heap (arrays over 85 KB), which only
    /// full collections clean.
    /// </summary>
    private async Task<byte[]> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var limit = Options.MaxResponseBytes > 0 ? Options.MaxResponseBytes : long.MaxValue;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        // the length is unknown for compressed and chunked answers; with a known one a single buffer is enough
        var length = content.Headers.ContentLength;
        var buffer = ArrayPool<byte>.Shared.Rent(length is > 0 and < MaxBodyLength ? (int)length.Value + 1 : FirstBufferSize);
        var total = 0;
        try
        {
            while (true)
            {
                if (total == buffer.Length)
                {
                    if (buffer.Length >= MaxBodyLength) throw new NonRetryableException($"Response is more than {MaxBodyLength} bytes");
                    var bigger = ArrayPool<byte>.Shared.Rent((int)Math.Min(2L * buffer.Length, MaxBodyLength));
                    buffer.AsSpan(0, total).CopyTo(bigger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = bigger;
                }

                var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > limit) throw new NonRetryableException($"Response is more than MaxResponseBytes {Options.MaxResponseBytes}");
            }
            return total == 0 ? [] : buffer.AsSpan(0, total).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private const int FirstBufferSize = 64 * 1024;
    private const int MaxBodyLength = 0x7FFFFFC7;    // Array.MaxLength

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Disposes the clients the loader made; a client it was given is not disposed.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;
        if (_ownClient is { IsValueCreated: true }) _ownClient.Value.Dispose();
        CloseSessions();
    }
}
