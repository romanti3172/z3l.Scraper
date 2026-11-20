using System.Net;

namespace z3l.Scraper.Loading;

/// <summary>Settings of <see cref="StealthHttpPageLoader"/>.</summary>
public sealed class StealthHttpPageLoaderOptions : HttpClientPageLoaderOptions
{
    /// <summary>Accept-Language of the requests.</summary>
    public string AcceptLanguage { get; set; } = "en-US,en;q=0.9";

    /// <summary>
    /// Ask for HTTP/2, as browsers do. A server without it answers over HTTP/1.1, and http:// sites always do. On by default:
    /// a browser's user agent over HTTP/1.1 is one of the first things anti-bot checks see.
    /// </summary>
    public bool UseHttp2 { get; set; } = true;

    /// <summary>
    /// Send a Referer from the page the link was found on (<see cref="ScrapeRequest.ParentUrl"/>), as browsers do by default: the whole
    /// page URL to the same origin, the origin of the page elsewhere, nothing from https to http.
    /// </summary>
    public bool SendReferer { get; set; } = true;
}

/// <summary>
/// Loads pages with <see cref="HttpClient"/> the way a browser asks for them, in sessions, directly or through proxies: over HTTP/2, with
/// the headers of the browser of the user agent in its order, Referer and Sec-Fetch-* from the page the link was found on.
/// <list type="bullet">
/// <item>Chrome and Edge: client hints (<c>sec-ch-ua</c> with the brands as Chromium makes them for the version, <c>sec-ch-ua-mobile</c>,
/// <c>sec-ch-ua-platform</c>), the Accept of Chrome, <c>Upgrade-Insecure-Requests</c>, <c>Priority</c>. Other Chromium browsers (Opera,
/// Yandex, Samsung) get the same without client hints.</item>
/// <item>Firefox and Safari: their headers, without client hints; every browser on iOS asks as Safari.</item>
/// <item>A page is asked for as a navigation, a request of <see cref="PageType.Api"/> as fetch() of the page it was found on (Accept
/// <c>*/*</c>, cors, empty). A request that is neither GET nor HEAD sends Origin.</item>
/// </list>
/// The user agent comes from <see cref="HttpClientPageLoaderOptions.UserAgent"/> or <see cref="HttpClientPageLoaderOptions.UserAgents"/>
/// (one per session), or from a User-Agent in the default headers or the request. A header of
/// <see cref="HttpClientPageLoaderOptions.DefaultHeaders"/> or of the request replaces the browser's header of the same name in its
/// place, and one the browser does not send goes after them.
/// <para>
/// The TLS handshake and the HTTP/2 settings stay those of .NET, which a strong anti-bot (one that reads JA3/JA4) can tell from a
/// browser's; for such sites use a browser or a TLS library through <see cref="HttpClientPageLoader.CreateHandler"/>.
/// </para>
/// </summary>
public class StealthHttpPageLoader : HttpClientPageLoader
{
    private readonly StealthHttpPageLoaderOptions _options;

    /// <summary>Creates a loader with its own <see cref="HttpClient"/> (decompression, cookies, connection pooling).</summary>
    /// <exception cref="ArgumentException">Both <see cref="HttpClientPageLoaderOptions.Proxy"/> and <see cref="HttpClientPageLoaderOptions.Proxies"/> are set.</exception>
    public StealthHttpPageLoader(StealthHttpPageLoaderOptions? options = null)
        : base(options ?? new StealthHttpPageLoaderOptions())
    {
        _options = (StealthHttpPageLoaderOptions)Options;
    }

    /// <inheritdoc />
    protected override HttpRequestMessage CreateMessage(ScrapeRequest request, Uri uri, string? userAgent)
    {
        var message = NewMessage(request, uri);
        if (_options.UseHttp2)
        {
            message.Version = HttpVersion.Version20;
            message.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }

        // a User-Agent of the request or of the default headers is the browser to look like
        var agent = Find(request.Headers, "User-Agent") ?? Find(_options.DefaultHeaders, "User-Agent") ?? userAgent;
        var headers = BrowserHeaders.For(request, uri, agent, _options.AcceptLanguage, _options.SendReferer);
        Override(headers, _options.DefaultHeaders);
        Override(headers, request.Headers);
        foreach (var header in headers) SetHeader(message, header.Key, header.Value);
        return message;
    }

    private static string? Find(IEnumerable<KeyValuePair<string, string>>? headers, string name) =>
        headers?.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>A header of <paramref name="with"/> replaces the browser's one of the same name where it is; a new one goes after them.</summary>
    private static void Override(List<KeyValuePair<string, string>> headers, IEnumerable<KeyValuePair<string, string>>? with)
    {
        if (with == null) return;
        foreach (var header in with)
        {
            var at = headers.FindIndex(h => string.Equals(h.Key, header.Key, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) headers[at] = new(headers[at].Key, header.Value);
            else headers.Add(header);
        }
    }
}
