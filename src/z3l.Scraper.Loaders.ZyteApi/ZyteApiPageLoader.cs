using System.Buffers;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Loaders.ZyteApi;

/// <summary>How Zyte API loads a page at a level: the request type, which with the site sets the price tier.</summary>
public enum ZyteApiRequestType
{
    /// <summary>
    /// An HTTP request (<c>httpResponseBody</c>): the response as the site sent it, to the request's method, body, headers and cookies.
    /// The cheaper tiers.
    /// </summary>
    Http,

    /// <summary>
    /// A browser request (<c>browserHtml</c>): the HTML a browser makes of the page, after its scripts ran. A browser sends GET only, and
    /// of the request's headers only its Referer and its cookies. The dearer tiers.
    /// </summary>
    Browser,
}

/// <summary>Settings of <see cref="ZyteApiPageLoader"/>.</summary>
public sealed class ZyteApiPageLoaderOptions : ServicePageLoaderOptions
{
    /// <summary>The API key, sent as the user name of HTTP basic authentication, with no password.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The extract endpoint.</summary>
    public Uri Endpoint { get; set; } = new("https://api.zyte.com/v1/extract");

    /// <summary>
    /// The request types of the levels, the cheapest first: a page that comes back blocked, or that Zyte API could not get past a ban
    /// (520), goes to the next one, and its host goes on from the level that got through. An HTTP request, then a browser by default;
    /// remove <see cref="ZyteApiRequestType.Browser"/> to never pay for a browser.
    /// </summary>
    public List<ZyteApiRequestType> Levels { get; } = [ZyteApiRequestType.Http, ZyteApiRequestType.Browser];

    /// <summary>
    /// Requests that go as HTTP requests at every level, as those a browser cannot send do (a method other than GET, a body): the JSON of
    /// an API, files, anything that is not a web page. A browser gives the HTML it shows, and wraps such an answer in it.
    /// </summary>
    public Func<ScrapeRequest, bool>? NoBrowser { get; set; }

    /// <summary>
    /// Country the call goes from (<c>geolocation</c>, two letters, such as <c>DE</c>). Null, the default, lets Zyte API pick the one
    /// that suits the site; a country other than the 20 standard ones costs more.
    /// </summary>
    public string? Geolocation { get; set; }

    /// <summary>
    /// Any other field of a call, for the request and the level: <c>ipType</c>, <c>device</c>, <c>session</c>, <c>actions</c>,
    /// <c>javascript</c>, <c>followRedirect</c>... The call's JSON is ready when this runs: set, replace or remove its fields.
    /// </summary>
    public Action<ScrapeRequest, int, JsonObject>? Configure { get; set; }
}

/// <summary>
/// Loads pages through Zyte API (<c>POST /v1/extract</c>): a call per page, the service's ban handling, proxies and browsers. The page
/// comes back as the site gave it: its status (<c>statusCode</c>: a 404 or a 500 of the site comes as it is), the final URL, the
/// site's headers, and the body as the site sent it (<c>httpResponseBody</c>) or the HTML a browser made of it (<c>browserHtml</c>).
/// <list type="bullet">
/// <item>At most <see cref="ServicePageLoaderOptions.MaxConcurrency"/> calls go at once. Zyte API limits calls per minute (3000 on
/// standard plans), not calls at once: set it to about the limit ÷ 60 × the seconds a call takes. Above the limit the API answers 429,
/// and the page is retried.</item>
/// <item>A page that comes back blocked, or that Zyte API could not get past a ban in time (520, not billed), goes one level up, through
/// <see cref="ZyteApiPageLoaderOptions.Levels"/>: an HTTP request, then a browser. A request a browser cannot send (a method other
/// than GET, a body) or that <see cref="ZyteApiPageLoaderOptions.NoBrowser"/> names goes as an HTTP request at every level. Zyte API's
/// other failures (500, 521) and its limits (429, 503) are retried at the same level.</item>
/// <item>A wrong key (401), a suspended account (403: no credit left, or a spending limit reached), a bad call (400, 422), a domain that
/// cannot be reached (421) or that Zyte API does not load (451) fail the page at once.</item>
/// </list>
/// Zyte API bills each page it gives, by the site's tier and the request type, and does not tell the price in its answer:
/// <see cref="ServicePageLoader.Credits"/> stays 0, and the answered calls of <see cref="ServicePageLoader.ToString"/> are the billed ones.
/// </summary>
/// <example>
/// <code>
/// await using var loader = new ZyteApiPageLoader(new ZyteApiPageLoaderOptions
/// {
///     ApiKey = Environment.GetEnvironmentVariable("ZYTE_API_KEY")!,
///     MaxConcurrency = 50,                          // about the rate limit ÷ 60 × the seconds a call takes
///     Geolocation = "DE",
/// });
/// var scraper = new Scraper(loader).Route(PageType.Item, ctx => ctx.Emit(ctx.Html.XPathValue("//h1")));
/// </code>
/// </example>
public sealed class ZyteApiPageLoader : ServicePageLoader
{
    /// <summary>The methods of an HTTP request (<c>httpRequestMethod</c>).</summary>
    private static readonly string[] Methods = ["GET", "POST", "PUT", "DELETE", "OPTIONS", "TRACE", "PATCH", "HEAD"];

    /// <summary>Headers of the site that do not fit the page given: Zyte API decompresses the body, and a browser's HTML is no body.</summary>
    private static readonly HashSet<string> DroppedHeaders = new(StringComparer.OrdinalIgnoreCase) { "Content-Encoding", "Content-Length" };

    /// <summary>A call is no web page: its URLs and texts go as they are, not escaped for HTML.</summary>
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly object Marked = new();

    private readonly ZyteApiPageLoaderOptions _options;
    private readonly ZyteApiRequestType[] _levels;
    private readonly string _key;
    private readonly string _credentials;

    /// <summary>Pages of a browser level that came from an HTTP request: they tell nothing of the browser.</summary>
    private readonly ConditionalWeakTable<PageResponse, object> _overHttp = [];

    /// <summary>Creates the loader.</summary>
    /// <exception cref="ArgumentException">No API key, or no levels.</exception>
    public ZyteApiPageLoader(ZyteApiPageLoaderOptions options, ILogger? logger = null)
        : base(options, logger)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new ArgumentException("Set ApiKey", nameof(options));
        if (options.Levels.Count == 0) throw new ArgumentException("Set at least one level", nameof(options));
        _options = options;
        _levels = [.. options.Levels];
        _key = options.ApiKey.Trim();
        _credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(_key + ":"));     // the key as the user name, no password
    }

    /// <inheritdoc />
    protected override int MaxLevel => _levels.Length - 1;

    /// <inheritdoc />
    protected override async Task<PageResponse> CallAsync(HttpClient client, ScrapeRequest request, int level, CancellationToken cancellationToken)
    {
        var type = TypeOf(request, level);
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint) { Content = new ByteArrayContent(CallBody(request, level, type)) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.Authorization = new AuthenticationHeaderValue("Basic", _credentials);

        using var answer = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var status = (int)answer.StatusCode;
        if (status == 200)
        {
            var (page, browser) = await PageOf(request, answer, cancellationToken).ConfigureAwait(false);
            if (!browser && _levels[level] == ZyteApiRequestType.Browser) _overHttp.AddOrUpdate(page, Marked);
            return page;
        }

        var (problem, said) = await ProblemOf(answer, cancellationToken).ConfigureAwait(false);
        var code = problem == null ? $"{status}" : $"{status} {problem}";
        switch (status)
        {
            case 401:
                throw new NonRetryableException($"Zyte API: a wrong or missing API key ({code})");
            case 403:
                throw new NonRetryableException(
                    $"Zyte API: the account is suspended (no credit left, or a spending limit reached) or may not make the call ({code}): {said}");
            case 400 or 422:
                throw new NonRetryableException($"Zyte API refused the call ({code}): {said}");
            case 421:
                throw new NonRetryableException($"Zyte API could not reach the site's domain ({code}): {said}");
            case 451:
                throw new NonRetryableException($"Zyte API does not load the site's domain ({code}): {said}");
            case 429 or 503:
                throw new RemoteLoadException($"Zyte API limits the calls ({code}): {said}");
            case 520:
            {
                // not billed: a ban Zyte API could not avoid in time, and a browser may get through. Its other failures go again as they are
                LevelUp(request, level, $"Zyte API could not avoid a ban in time ({code})");
                var next = type == ZyteApiRequestType.Http && level < MaxLevel && TypeOf(request, level + 1) == ZyteApiRequestType.Browser
                    ? ": a browser next"
                    : "";
                throw new RemoteLoadException($"Zyte API could not get the page past a ban in time ({code}){next}");
            }
            default:
                throw new RemoteLoadException($"Zyte API answered {code}: {said}");
        }
    }

    /// <summary>The page got through: its host goes on from its level, unless the page came from an HTTP request at a browser level.</summary>
    public override void ReportGoodResponse(ScrapeRequest request, PageResponse response)
    {
        if (!_overHttp.TryGetValue(response, out _)) base.ReportGoodResponse(request, response);
    }

    /// <summary>The request type of a call: the level's, but a browser sends only GET without a body, and no request NoBrowser names.</summary>
    private ZyteApiRequestType TypeOf(ScrapeRequest request, int level) =>
        _levels[level] == ZyteApiRequestType.Browser && request.Method == HttpMethod.Get && request.Body == null
        && _options.NoBrowser?.Invoke(request) != true
            ? ZyteApiRequestType.Browser
            : ZyteApiRequestType.Http;

    /// <summary>
    /// The JSON of a call: the page's URL and the output of the request type, the request's method, body, headers and cookies, the
    /// geolocation, then <see cref="ZyteApiPageLoaderOptions.Configure"/>.
    /// </summary>
    /// <exception cref="NonRetryableException">A method Zyte API does not send.</exception>
    internal byte[] CallBody(ScrapeRequest request, int level, ZyteApiRequestType type)
    {
        var body = new JsonObject { ["url"] = request.Url };
        if (type == ZyteApiRequestType.Browser)
        {
            body["browserHtml"] = true;
            if (HeaderOf(request, "Referer") is { } referer) body["requestHeaders"] = new JsonObject { ["referer"] = referer };
        }
        else
        {
            body["httpResponseBody"] = true;
            var method = request.Method.Method.ToUpperInvariant();
            if (!Methods.Contains(method))
            {
                throw new NonRetryableException($"Zyte API sends GET, POST, PUT, DELETE, OPTIONS, TRACE, PATCH and HEAD, not {request.Method}");
            }
            if (method != "GET") body["httpRequestMethod"] = method;
            if (!string.IsNullOrEmpty(request.Body)) body["httpRequestText"] = request.Body;
            if (HeadersOf(request) is { } headers) body["customHttpRequestHeaders"] = headers;
        }
        body["httpResponseHeaders"] = true;
        if (CookiesOf(request) is { } cookies) body["requestCookies"] = cookies;
        if (!string.IsNullOrWhiteSpace(_options.Geolocation)) body["geolocation"] = _options.Geolocation.Trim().ToUpperInvariant();

        _options.Configure?.Invoke(request, level, body);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            body.WriteTo(writer);
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The headers of an HTTP call: the Content-Type of the body, then the request's headers but its Cookie.</summary>
    private static JsonArray? HeadersOf(ScrapeRequest request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.Body != null) headers["Content-Type"] = request.ContentType ?? "application/x-www-form-urlencoded";
        if (request.Headers != null)
        {
            foreach (var header in request.Headers)
            {
                if (!IsCookie(header.Key)) headers[header.Key] = header.Value;
            }
        }
        if (headers.Count == 0) return null;

        var list = new JsonArray();
        foreach (var header in headers) list.Add((JsonNode)new JsonObject { ["name"] = header.Key, ["value"] = header.Value });
        return list;
    }

    /// <summary>The Cookie header of the request as Zyte API takes cookies (<c>requestCookies</c>): a name, a value and the page's host each.</summary>
    internal static JsonArray? CookiesOf(ScrapeRequest request)
    {
        var header = HeaderOf(request, "Cookie");
        if (string.IsNullOrWhiteSpace(header) || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)) return null;

        var cookies = new JsonArray();
        foreach (var cookie in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = cookie.IndexOf('=', StringComparison.Ordinal);
            if (equals == 0) continue;     // no name
            var name = equals < 0 ? cookie : cookie[..equals].TrimEnd();
            var value = equals < 0 ? "" : cookie[(equals + 1)..].TrimStart();
            cookies.Add((JsonNode)new JsonObject { ["name"] = name, ["value"] = value, ["domain"] = uri.IdnHost });
        }
        return cookies.Count == 0 ? null : cookies;
    }

    private static string? HeaderOf(ScrapeRequest request, string name) =>
        request.Headers?.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool IsCookie(string name) => string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The page as the site gave it: its status, the final URL, its headers, and its body or the browser's HTML, whichever came (the
    /// output of the request type, or another one <see cref="ZyteApiPageLoaderOptions.Configure"/> asked for).
    /// </summary>
    private static async Task<(PageResponse Page, bool Browser)> PageOf(ScrapeRequest request, HttpResponseMessage answer,
        CancellationToken cancellationToken)
    {
        JsonDocument json;
        try
        {
            var stream = await answer.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            json = await JsonDocument.ParseAsync(stream, default, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new RemoteLoadException($"Zyte API answered 200 without JSON: {ex.Message}", ex);
        }

        using (json)
        {
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new RemoteLoadException("Zyte API answered 200 without a JSON object");

            // statusCode: the site's, after the redirects; url: where they ended
            var status = root.TryGetProperty("statusCode", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var site)
                ? site
                : 200;
            var url = StringOf(root, "url") is { } final && Uri.TryCreate(final, UriKind.Absolute, out var uri) ? uri.AbsoluteUri : request.Url;
            var headers = SiteHeadersOf(root);
            headers.TryGetValue("Content-Type", out var contentType);

            if (root.TryGetProperty("httpResponseBody", out var encoded) && encoded.ValueKind == JsonValueKind.String)
            {
                if (!encoded.TryGetBytesFromBase64(out var body))
                {
                    throw new RemoteLoadException("Zyte API answered an httpResponseBody that is not Base64");
                }
                return (new PageResponse(status, url, body, contentType, headers), false);
            }
            if (StringOf(root, "browserHtml") is { } html)
            {
                return (PageResponse.FromText(status, url, html, contentType, headers), true);
            }
            if (request.Method == HttpMethod.Head || status is 204 or 304)
            {
                return (new PageResponse(status, url, [], contentType, headers), false);     // no body to give
            }
            throw new RemoteLoadException("Zyte API answered without httpResponseBody or browserHtml");
        }
    }

    /// <summary>The site's headers (<c>httpResponseHeaders</c>), the values of a name joined, without those that do not fit the page given.</summary>
    private static Dictionary<string, string> SiteHeadersOf(JsonElement root)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("httpResponseHeaders", out var list) || list.ValueKind != JsonValueKind.Array) return headers;

        foreach (var header in list.EnumerateArray())
        {
            if (header.ValueKind != JsonValueKind.Object
                || StringOf(header, "name") is not { Length: > 0 } name
                || StringOf(header, "value") is not { } value
                || DroppedHeaders.Contains(name))
            {
                continue;
            }
            headers[name] = headers.TryGetValue(name, out var before) ? $"{before}, {value}" : value;
        }
        return headers;
    }

    /// <summary>
    /// What an error answer says (<c>application/problem+json</c>): its type, and its detail or its title (the text itself when it is not
    /// JSON), short and without the key.
    /// </summary>
    private async Task<(string? Type, string Said)> ProblemOf(HttpResponseMessage answer, CancellationToken cancellationToken)
    {
        var text = await answer.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string? type = null;
        string? said = null;
        try
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.ValueKind == JsonValueKind.Object)
            {
                type = StringOf(json.RootElement, "type");
                said = StringOf(json.RootElement, "detail") ?? StringOf(json.RootElement, "title");
            }
        }
        catch (JsonException)
        {
            // a page of a proxy or a gateway: its text
        }
        return (type == null ? null : Clean(type), Clean(said ?? text));
    }

    /// <summary>A text of the service for a message: on one line, at most 200 characters, never the key.</summary>
    private string Clean(string text)
    {
        text = text.Replace(_key, "…", StringComparison.Ordinal).ReplaceLineEndings(" ").Trim();
        return text.Length <= 200 ? text : text[..200] + "…";
    }

    private static string? StringOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
