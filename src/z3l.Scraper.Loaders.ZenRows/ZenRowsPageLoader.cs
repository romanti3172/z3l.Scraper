using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Loaders.ZenRows;

/// <summary>How ZenRows loads a page at a level, and what a call that gets the page costs; a call that fails costs nothing.</summary>
public enum ZenRowsLevel
{
    /// <summary>The standard proxies: 1 credit a call, 5 with JavaScript.</summary>
    Standard,

    /// <summary>Premium (residential) proxies (<c>premium_proxy</c>): 10 credits a call, 25 with JavaScript.</summary>
    Premium,

    /// <summary>
    /// Premium proxies and a browser (<c>premium_proxy</c> and <c>js_render</c>), what ZenRows calls a protected request: 25 credits a
    /// call. JavaScript is rendered whatever <see cref="ZenRowsPageLoaderOptions.RenderJs"/> says, except for POST and PUT.
    /// </summary>
    Protected,

    /// <summary>
    /// Adaptive Stealth Mode (<c>mode=auto</c>): ZenRows picks the proxies and the browser itself and bills only the configuration that
    /// got the page, 1 to 25 credits. <see cref="ZenRowsPageLoaderOptions.RenderJs"/> and <see cref="ZenRowsPageLoaderOptions.WaitFor"/>
    /// do not apply. Best as the only level.
    /// </summary>
    Auto,
}

/// <summary>Settings of <see cref="ZenRowsPageLoader"/>.</summary>
public sealed class ZenRowsPageLoaderOptions : ServicePageLoaderOptions
{
    /// <summary>
    /// Creates the settings. <see cref="ServicePageLoaderOptions.CallTimeout"/> is 3 minutes 10 seconds: ZenRows works on a call for up
    /// to 3 minutes, and a call given up sooner keeps its place in the plan's concurrency until ZenRows is done with it.
    /// </summary>
    public ZenRowsPageLoaderOptions() => CallTimeout = TimeSpan.FromSeconds(190);

    /// <summary>The API key, sent as the <c>apikey</c> query parameter: the Fetch API takes it no other way.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The Fetch API.</summary>
    public Uri Endpoint { get; set; } = new("https://api.zenrows.com/v1/");

    /// <summary>
    /// Render JavaScript in a browser (<c>js_render</c>). Off by default: the page as the server gives it, which costs a fifth of a
    /// rendered one. The protected level renders anyway; POST and PUT never do, ZenRows renders GET pages only.
    /// </summary>
    public bool RenderJs { get; set; }

    /// <summary>
    /// The levels, the cheapest first: a page that comes back blocked, or that ZenRows could not get because the site refused it, goes
    /// to the next one, and its host goes on from the level that got through. Standard, then premium proxies by default; add
    /// <see cref="ZenRowsLevel.Protected"/> for the hardest sites, or keep <see cref="ZenRowsLevel.Auto"/> alone to let ZenRows choose.
    /// </summary>
    public List<ZenRowsLevel> Levels { get; } = [ZenRowsLevel.Standard, ZenRowsLevel.Premium];

    /// <summary>
    /// Country of the proxies (<c>proxy_country</c>, two letters). ZenRows takes it with premium proxies and with
    /// <see cref="ZenRowsLevel.Auto"/> only: the standard level goes without it, so leave that level out when every call must come from
    /// the country.
    /// </summary>
    public string? CountryCode { get; set; }

    /// <summary>A CSS or XPath selector the browser waits for (<c>wait_for</c>), up to 3 minutes, when JavaScript is rendered.</summary>
    public string? WaitFor { get; set; }

    /// <summary>
    /// Send the headers of the request to the site, its Cookie header among them (<c>custom_headers</c>). On by default. ZenRows keeps
    /// the browser's own headers (User-Agent, Accept-Language, Sec-Ch-Ua...) and ignores those of the request.
    /// </summary>
    public bool ForwardHeaders { get; set; } = true;

    /// <summary>
    /// Any other parameter of a call, for the request and the level: <c>session_id</c> (one IP for 10 minutes), <c>wait</c>,
    /// <c>block_resources</c>, <c>js_instructions</c>, <c>allowed_status_codes</c>... The key is not among them.
    /// </summary>
    public Action<ScrapeRequest, int, IDictionary<string, string>>? Configure { get; set; }
}

/// <summary>
/// Loads pages through the ZenRows Fetch API (formerly the Universal Scraper API): a call per page, the service's proxies, browsers and
/// anti-bot handling. The page comes back as the site gave it: its status (every call sets <c>original_status</c>), the final URL
/// (<c>Zr-Final-Url</c>), the site's headers without their <c>Zr-</c> prefix, and ZenRows' own headers (<c>X-Request-Credits</c>,
/// <c>X-Request-Id</c>...) as they are.
/// <list type="bullet">
/// <item>At most <see cref="ServicePageLoaderOptions.MaxConcurrency"/> calls go at once: set it to your plan's limit; above it the API
/// answers 429, and the page is retried.</item>
/// <item>A page that comes back blocked, or that ZenRows could not get because the site refused it (<c>RESP001</c>, not billed), goes
/// one level up, through <see cref="ZenRowsPageLoaderOptions.Levels"/>; so does a page of a domain ZenRows loads only with JavaScript
/// or premium proxies (<c>REQS002</c>). ZenRows' timeout, its own errors and a 5xx of the site are retried at the same level.
/// <see cref="ServicePageLoader.Credits"/> sums <c>X-Request-Credits</c>.</item>
/// <item>A wrong key, no credits left or a plan without the feature (401, 402), a bad call (400) and a page bigger than the plan
/// allows (413) fail the page at once.</item>
/// <item>GET, POST and PUT go to the site with their body; the request's headers go with <c>custom_headers</c>.</item>
/// </list>
/// </summary>
/// <remarks>Written from ZenRows' documentation and OpenAPI description; not yet run against the real API.</remarks>
/// <example>
/// <code>
/// await using var loader = new ZenRowsPageLoader(new ZenRowsPageLoaderOptions
/// {
///     ApiKey = Environment.GetEnvironmentVariable("ZENROWS_KEY")!,
///     MaxConcurrency = 20,                          // the plan's limit
/// });
/// var scraper = new Scraper(loader).Route(PageType.Item, ctx => ctx.Emit(ctx.Html.XPathValue("//h1")));
/// </code>
/// </example>
public sealed class ZenRowsPageLoader : ServicePageLoader
{
    /// <summary>Headers of ZenRows itself, kept as they are; the other <c>Zr-</c> headers are the site's.</summary>
    private static readonly HashSet<string> OwnHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "X-Request-Credits", "X-Request-Cost", "X-Request-Id", "X-Key-Credits-Remaining", "Concurrency-Limit", "Concurrency-Remaining",
        "Zr-Final-Url", "Zr-Cookies",
    };

    /// <summary>Headers of the site's transfer, left out: ZenRows gives the body decoded.</summary>
    private static readonly HashSet<string> TransferHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Encoding", "Content-Length", "Transfer-Encoding",
    };

    /// <summary>Headers of the request that are not the site's: the connection to ZenRows and the encoding of its answer.</summary>
    private static readonly HashSet<string> NotForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
        "Accept-Encoding",
    };

    /// <summary>The families of ZenRows' error codes: AUTH002, REQS004, RESP001, CTX0002, ERR0001, BLK0001, REQ_INVALID_PARAMS.</summary>
    private static readonly string[] CodeFamilies = ["AUTH", "REQ", "RESP", "CTX", "ERR", "BLK"];

    private const int MaxProblemBytes = 64 * 1024;

    private readonly ZenRowsPageLoaderOptions _options;
    private readonly ZenRowsLevel[] _levels;

    /// <summary>Creates the loader.</summary>
    /// <exception cref="ArgumentException">No API key, or no levels.</exception>
    public ZenRowsPageLoader(ZenRowsPageLoaderOptions options, ILogger? logger = null)
        : base(options, logger)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new ArgumentException("Set ApiKey", nameof(options));
        if (options.Levels.Count == 0) throw new ArgumentException("Set at least one level", nameof(options));
        _options = options;
        _levels = [.. options.Levels];
    }

    /// <inheritdoc />
    protected override int MaxLevel => _levels.Length - 1;

    /// <inheritdoc />
    protected override async Task<PageResponse> CallAsync(HttpClient client, ScrapeRequest request, int level, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Post && request.Method != HttpMethod.Put)
        {
            throw new NonRetryableException($"ZenRows takes GET, POST and PUT, not {request.Method}");
        }

        using var message = new HttpRequestMessage(request.Method, CallUri(request, _levels[level], level));
        if (request.Body != null)
        {
            // the body goes to the site as it is
            message.Content = new StringContent(request.Body, Encoding.UTF8);
            message.Content.Headers.Remove("Content-Type");
            message.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType ?? "application/x-www-form-urlencoded");
        }
        if (_options.ForwardHeaders && request.Headers != null)
        {
            foreach (var header in request.Headers)
            {
                if (header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                {
                    // a Content-* header goes with the body, as with the scraper's own loaders
                    if (message.Content == null) continue;
                    message.Content.Headers.Remove(header.Key);
                    message.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                else if (IsForwarded(header.Key))
                {
                    message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        using var answer = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        CountCredits(answer);
        var body = await answer.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var status = (int)answer.StatusCode;

        // 404 and 410 are billed as pages: a page the site does not have (RESP002) or a domain that does not resolve (RESP007)
        if (status is 404 or 410 || ProblemOf(answer, body) is not { } problem) return PageOf(request, answer, body);
        throw FailureOf(request, level, status, problem);
    }

    /// <summary>The API call: the parameters of the request and the level, and the key.</summary>
    internal Uri CallUri(ScrapeRequest request, ZenRowsLevel zenLevel, int level)
    {
        var parameters = ParametersOf(request, zenLevel, level)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}");
        return new Uri($"{_options.Endpoint.AbsoluteUri.TrimEnd('?')}?apikey={Uri.EscapeDataString(_options.ApiKey)}&{string.Join('&', parameters)}");
    }

    /// <summary>The parameters of a call but the key, <see cref="ZenRowsPageLoaderOptions.Configure"/> applied.</summary>
    internal Dictionary<string, string> ParametersOf(ScrapeRequest request, ZenRowsLevel zenLevel, int level)
    {
        var auto = zenLevel == ZenRowsLevel.Auto;
        var premium = zenLevel is ZenRowsLevel.Premium or ZenRowsLevel.Protected;
        var render = !auto && request.Method == HttpMethod.Get && (_options.RenderJs || zenLevel == ZenRowsLevel.Protected);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["url"] = request.Url,
            ["original_status"] = "true",     // the site's status, not ZenRows' 422 for every page it could not get
        };
        if (auto) parameters["mode"] = "auto";     // ZenRows sets js_render and premium_proxy itself, and refuses them from the call
        if (render) parameters["js_render"] = "true";
        if (premium) parameters["premium_proxy"] = "true";
        if ((premium || auto) && !string.IsNullOrWhiteSpace(_options.CountryCode))
        {
            parameters["proxy_country"] = _options.CountryCode.Trim().ToLowerInvariant();
        }
        if (render && !string.IsNullOrWhiteSpace(_options.WaitFor)) parameters["wait_for"] = _options.WaitFor;
        if (Forwards(request)) parameters["custom_headers"] = "true";

        _options.Configure?.Invoke(request, level, parameters);
        return parameters;
    }

    private bool Forwards(ScrapeRequest request) => _options.ForwardHeaders && request.Headers?.Any(h => IsForwarded(h.Key)) == true;

    private static bool IsForwarded(string name) => !NotForwarded.Contains(name) && !name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Counts the credits ZenRows charged (<c>X-Request-Credits</c>; <c>X-Request-Cost</c> is the same in US dollars).</summary>
    private void CountCredits(HttpResponseMessage answer)
    {
        if (answer.Headers.TryGetValues("X-Request-Credits", out var values)
            && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var credits)
            && credits > 0)
        {
            AddCredits(credits);
        }
    }

    /// <summary>The page as the site gave it: the site's headers without their <c>Zr-</c> prefix, the final URL.</summary>
    private static PageResponse PageOf(ScrapeRequest request, HttpResponseMessage answer, byte[] body)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? siteType = null;
        foreach (var header in answer.Headers.Concat(answer.Content.Headers))
        {
            var value = string.Join(", ", header.Value);
            if (OwnHeaders.Contains(header.Key))
            {
                headers[header.Key] = value;
            }
            else if (header.Key.StartsWith("Zr-", StringComparison.OrdinalIgnoreCase))
            {
                var name = header.Key[3..];
                if (TransferHeaders.Contains(name)) continue;
                headers[name] = value;
                if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase)) siteType = value;
            }
        }

        var url = headers.TryGetValue("Zr-Final-Url", out var resolved) && Uri.TryCreate(resolved, UriKind.Absolute, out var final)
            ? final.AbsoluteUri
            : request.Url;
        var contentType = siteType ?? answer.Content.Headers.ContentType?.ToString();
        if (contentType != null) headers["Content-Type"] = contentType;
        return new PageResponse((int)answer.StatusCode, url, body, contentType, headers);
    }

    /// <summary>An error of ZenRows itself: a problem document (RFC 9457) with its code.</summary>
    private sealed record Problem(string Code, string? Title, string? Detail);

    /// <summary>
    /// ZenRows' own error, or null when the answer is the site's page. A 2xx is the page unless it comes as
    /// <c>application/problem+json</c>; another status may be the site's page too, through <c>allowed_status_codes</c>.
    /// </summary>
    private static Problem? ProblemOf(HttpResponseMessage answer, byte[] body)
    {
        var type = answer.Content.Headers.ContentType?.MediaType;
        var candidate = answer.IsSuccessStatusCode
            ? string.Equals(type, "application/problem+json", StringComparison.OrdinalIgnoreCase)
            : type == null || type.Contains("json", StringComparison.OrdinalIgnoreCase);
        if (!candidate || body.Length is 0 or > MaxProblemBytes) return null;

        try
        {
            using var json = JsonDocument.Parse(body.AsMemory(body.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? 3 : 0));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || TextOf(root, "code") is not { } code || !IsZenRowsCode(code)) return null;
            return new Problem(code, TextOf(root, "title"), TextOf(root, "detail"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TextOf(JsonElement problem, string name) =>
        problem.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsZenRowsCode(string code) =>
        CodeFamilies.Any(family => code.StartsWith(family, StringComparison.Ordinal))
        && code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');

    /// <summary>What ZenRows' error means for the page: another call at this level, one level up, or none.</summary>
    private Exception FailureOf(ScrapeRequest request, int level, int status, Problem problem)
    {
        var code = problem.Code;
        switch (code)
        {
            case "RESP001" when status is >= 500 and not 503:
                // with original_status the status is the site's: its own error, which stronger proxies do not mend
                return new RemoteLoadException($"ZenRows could not get the page ({code}): the site answered {status}");
            case "RESP001" or "RESP006":
                // the site refused: anti-bot, a captcha, its 403, 429 or 503; premium proxies or a browser may get through
                LevelUp(request, level, $"{code}, status {status}");
                return new RemoteLoadException($"ZenRows could not get the page ({code}, status {status}){Next(level)}");
            case "REQS002" when level < MaxLevel:
                LevelUp(request, level, code);
                return new RemoteLoadException($"ZenRows loads this domain only with JavaScript or premium proxies ({code}){Next(level)}");
            case "REQS002":
                return new NonRetryableException($"ZenRows loads this domain only with JavaScript or premium proxies ({code}): add a level that has them");
            case "AUTH006" or "AUTH008":
                return new RemoteLoadException($"ZenRows: too many calls at once ({code}): set MaxConcurrency to the plan's limit");
            case "CTX0002":
                return new RemoteLoadException($"ZenRows gave the page up after 3 minutes ({code})");
            case "BLK0001":
                return new RemoteLoadException($"ZenRows blocked this IP for too many failed calls ({code}): it lifts in a few minutes");
            case "RESP004":
                return new NonRetryableException($"ZenRows refused the call ({status}, {code}): {Describe(problem)}");
            case "RESP005":
                return new NonRetryableException($"ZenRows: the page is bigger than the plan allows ({code})");
            case "RESP008":
                // ZenRows does not retry it either; failed calls in a row may get this IP blocked (BLK0001)
                return new NonRetryableException($"ZenRows: the site's own error, its configuration or certificate ({code})");
        }

        // the key, the credits, the plan: the body may repeat the key, so only the code goes into the message
        if (code.StartsWith("AUTH", StringComparison.Ordinal))
        {
            return new NonRetryableException($"ZenRows refused the call ({status}, {code}): {AccountProblem(code)}");
        }
        // a bad call (REQS001, REQS004, REQS005...): the same call fails the same way
        if (status is >= 400 and < 500 and not 408 and not 429)
        {
            return new NonRetryableException($"ZenRows refused the call ({status}, {code}): {Describe(problem)}");
        }
        return new RemoteLoadException($"ZenRows answered {status} ({code}): {Describe(problem)}");
    }

    private string Next(int level) => level < MaxLevel ? ": the next call goes one level up" : "";

    private static string AccountProblem(string code) => code switch
    {
        "AUTH001" or "AUTH002" or "AUTH003" => "a missing or wrong API key",
        "AUTH004" => "the credits of the period are spent",
        "AUTH005" => "the API key is no longer valid",
        "AUTH009" => "the account is not verified",
        "AUTH014" => "the API key reached its credit cap",
        _ => "the plan or the account does not allow it",
    };

    /// <summary>The detail of an error on one line, without the key: an error may repeat the call.</summary>
    private string Describe(Problem problem)
    {
        var text = (problem.Detail ?? problem.Title ?? "").ReplaceLineEndings(" ").Trim();
        text = text.Replace(_options.ApiKey, "***", StringComparison.Ordinal);
        var escaped = Uri.EscapeDataString(_options.ApiKey);
        if (escaped != _options.ApiKey) text = text.Replace(escaped, "***", StringComparison.Ordinal);
        return text.Length <= 200 ? text : text[..200] + "…";
    }
}
