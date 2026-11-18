using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Loaders.ScraperApi;

/// <summary>The proxies ScraperAPI loads a page through at a level, and what a call costs.</summary>
public enum ScraperApiProxy
{
    /// <summary>The standard pool: 1 credit a call, 10 with JavaScript; some domains cost more (Amazon 5, Google 25, LinkedIn 30).</summary>
    Standard,

    /// <summary>The premium pool of residential and mobile proxies (<c>premium=true</c>): 10 credits a call, 25 with JavaScript.</summary>
    Premium,

    /// <summary>
    /// The ultra premium pool (<c>ultra_premium=true</c>) for the hardest sites, on paid plans only: 30 credits a call, 75 with
    /// JavaScript. It sends no headers of the request, and may give the page as it was up to 10 minutes ago, from its cache
    /// (<c>cache_control=no-cache</c> through <see cref="ScraperApiPageLoaderOptions.Configure"/> turns that off).
    /// </summary>
    UltraPremium,
}

/// <summary>Settings of <see cref="ScraperApiPageLoader"/>.</summary>
public sealed class ScraperApiPageLoaderOptions : ServicePageLoaderOptions
{
    /// <summary>
    /// The API key. It goes in the URL of every call (<c>api_key</c>), the only way the API takes it: a client of yours that logs the
    /// URLs of its calls logs the key.
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The API.</summary>
    public Uri Endpoint { get; set; } = new("https://api.scraperapi.com/");

    /// <summary>
    /// Render JavaScript in a browser (<c>render=true</c>). Off by default: the page as the server gives it, which costs a tenth of a
    /// rendered one with standard proxies.
    /// </summary>
    public bool RenderJs { get; set; }

    /// <summary>
    /// The proxies of the levels, the cheapest first: a page that comes back blocked, or that the site refused, goes to the next one,
    /// and its host goes on from the level that got through. Standard, then premium by default; add
    /// <see cref="ScraperApiProxy.UltraPremium"/> for the hardest sites (paid plans).
    /// </summary>
    public List<ScraperApiProxy> Levels { get; } = [ScraperApiProxy.Standard, ScraperApiProxy.Premium];

    /// <summary>
    /// Country of the proxies (<c>country_code</c>, two lowercase letters: <c>us</c>, <c>de</c>, <c>uk</c> for the United Kingdom).
    /// Hobby and Startup plans take only <c>us</c> and <c>eu</c>.
    /// </summary>
    public string? CountryCode { get; set; }

    /// <summary>A selector the browser waits for (<c>wait_for_selector</c>), when JavaScript is rendered.</summary>
    public string? WaitForSelector { get; set; }

    /// <summary>
    /// Send the headers of the request to the site, its Cookie header too (<c>keep_headers=true</c>). On by default. ScraperAPI warns
    /// that headers of your own replace its own and often lower the success rate, so set few; the ultra premium level sends none.
    /// </summary>
    public bool ForwardHeaders { get; set; } = true;

    /// <summary>
    /// Any other parameter of a call, for the request and the level: <c>session_number</c> (one IP for 15 minutes after its last
    /// use; not with premium or ultra premium proxies), <c>device_type</c> (<c>desktop</c> or <c>mobile</c>), <c>max_cost</c>,
    /// <c>follow_redirect</c>, <c>cache_control</c>... The page's <c>url</c> goes last whatever is set here.
    /// </summary>
    public Action<ScrapeRequest, int, IDictionary<string, string>>? Configure { get; set; }
}

/// <summary>
/// Loads pages through the ScraperAPI endpoint: a call per page, the service's proxies, browsers and captcha handling. Written from
/// ScraperAPI's documentation, not yet checked against the real API. The page comes back as the API gives it: the status (200, and 404
/// and 410 as they are), the headers (<c>sa-credit-cost</c> among them), the body in UTF-8, and the final URL when
/// <c>sa-final-url</c> tells it.
/// <list type="bullet">
/// <item>At most <see cref="ServicePageLoaderOptions.MaxConcurrency"/> calls go at once: set it to your plan's limit (5 on the free
/// plan); above it the API answers 429, and the page is retried.</item>
/// <item>A page that comes back blocked goes one level up, through <see cref="ScraperApiPageLoaderOptions.Levels"/>. A 500 (the API
/// tried for 70 seconds and had no page; not billed) is retried at the same level: it does not tell whether the site refused, failed
/// or did not answer. Only a 500 with <c>sa-statuscode</c> 403, 429 or 503, the site refusing, sends the page one level up.
/// <see cref="ServicePageLoader.Credits"/> sums <c>sa-credit-cost</c>.</item>
/// <item>A wrong key (401), no credits left or a call above <c>max_cost</c> (403), and a bad call (400) fail the page at once.</item>
/// <item>GET, POST and PUT go to the site with their body; the request's headers go with <c>keep_headers=true</c>.</item>
/// <item>The API tries a page for up to 70 seconds, and bills a call given up before that: keep
/// <see cref="ServicePageLoaderOptions.CallTimeout"/> above it (3 minutes by default).</item>
/// </list>
/// </summary>
/// <example>
/// <code>
/// await using var loader = new ScraperApiPageLoader(new ScraperApiPageLoaderOptions
/// {
///     ApiKey = Environment.GetEnvironmentVariable("SCRAPERAPI_KEY")!,
///     MaxConcurrency = 5,                           // the plan's limit
///     CountryCode = "us",
/// });
/// var scraper = new Scraper(loader).Route(PageType.Item, ctx => ctx.Emit(ctx.Html.XPathValue("//h1")));
/// </code>
/// </example>
public sealed class ScraperApiPageLoader : ServicePageLoader
{
    /// <summary>How long the API tries a page before it answers 500; a call given up earlier is billed.</summary>
    internal static readonly TimeSpan ServiceRetryTime = TimeSpan.FromSeconds(70);

    /// <summary>
    /// Headers of the request that do not go: Host would change the call, Accept-Encoding the encoding of the API's own answer (the
    /// client of the calls does not decompress), and the others belong to one connection.
    /// </summary>
    private static readonly HashSet<string> NotForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Accept-Encoding", "Connection", "Proxy-Connection", "Keep-Alive", "TE", "Transfer-Encoding", "Upgrade",
    };

    private readonly ScraperApiPageLoaderOptions _options;
    private readonly ScraperApiProxy[] _levels;

    /// <summary>Creates the loader.</summary>
    /// <exception cref="ArgumentException">No API key, or no levels.</exception>
    public ScraperApiPageLoader(ScraperApiPageLoaderOptions options, ILogger? logger = null)
        : base(options, logger)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new ArgumentException("Set ApiKey", nameof(options));
        if (options.Levels.Count == 0) throw new ArgumentException("Set at least one level", nameof(options));
        _options = options;
        _levels = [.. options.Levels];
        if (options.CallTimeout < ServiceRetryTime)
        {
            Logger.LogWarning("CallTimeout is {Seconds} s: ScraperAPI tries a page for up to 70 s, and bills a call given up before that",
                options.CallTimeout.TotalSeconds);
        }
    }

    /// <inheritdoc />
    protected override int MaxLevel => _levels.Length - 1;

    /// <inheritdoc />
    protected override async Task<PageResponse> CallAsync(HttpClient client, ScrapeRequest request, int level, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Post && request.Method != HttpMethod.Put)
        {
            throw new NonRetryableException($"ScraperAPI takes GET, POST and PUT, not {request.Method}");
        }

        var proxy = _levels[level];
        using var message = new HttpRequestMessage(request.Method, CallUri(request, proxy, level));
        if (Forwards(request, proxy))
        {
            foreach (var header in request.Headers!)
            {
                if (IsForwarded(header.Key)) message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        if (request.Body != null)
        {
            // the body goes to the site as it is
            message.Content = new StringContent(request.Body, Encoding.UTF8);
            message.Content.Headers.Remove("Content-Type");
            message.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType ?? "application/x-www-form-urlencoded");
        }

        using var answer = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var status = (int)answer.StatusCode;
        switch (status)
        {
            case 200 or 404 or 410:
                return await PageOf(request, answer, cancellationToken).ConfigureAwait(false);
            case 400:
                throw new NonRetryableException($"ScraperAPI refused the call (400): {await ErrorOf(answer, cancellationToken).ConfigureAwait(false)}");
            case 401:
                throw new NonRetryableException("ScraperAPI: a wrong API key (401)");
            case 403:
                throw new NonRetryableException(
                    $"ScraperAPI: no credits left, or the call costs more than max_cost (403): {await ErrorOf(answer, cancellationToken).ConfigureAwait(false)}");
            case 429:
                throw new RemoteLoadException("ScraperAPI: too many calls at once (429): set MaxConcurrency to the plan's limit");
            case 500:
            {
                // not billed: the API tried for 70 seconds. Its documentation gives no reason; sa-statuscode, when it comes, is what the
                // site answered: a refusal (403, 429, 503) - stronger proxies may get through; anything else - the same level again
                var siteStatus = SiteStatusOf(answer);
                var refused = siteStatus is 403 or 429 or 503;
                if (refused) LevelUp(request, level, $"the site answered {siteStatus}");
                var why = siteStatus is { } code ? $", the site answered {code}" : "";
                throw new RemoteLoadException($"ScraperAPI had no page after its retries (500, not billed{why}){(refused ? ": stronger proxies next" : "")}");
            }
            default:
                throw new RemoteLoadException($"ScraperAPI answered {status}: {await ErrorOf(answer, cancellationToken).ConfigureAwait(false)}");
        }
    }

    /// <summary>The API call: the key first, then the parameters of the level, the page's URL last, as the API asks.</summary>
    internal Uri CallUri(ScrapeRequest request, ScraperApiProxy proxy, int level)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_options.RenderJs)
        {
            parameters["render"] = "true";
            if (!string.IsNullOrWhiteSpace(_options.WaitForSelector)) parameters["wait_for_selector"] = _options.WaitForSelector;
        }
        if (proxy == ScraperApiProxy.Premium) parameters["premium"] = "true";
        if (proxy == ScraperApiProxy.UltraPremium) parameters["ultra_premium"] = "true";
        if (!string.IsNullOrWhiteSpace(_options.CountryCode)) parameters["country_code"] = _options.CountryCode.Trim().ToLowerInvariant();
        if (Forwards(request, proxy)) parameters["keep_headers"] = "true";
        parameters["url"] = request.Url;

        _options.Configure?.Invoke(request, level, parameters);

        // "all ScraperAPI parameters ... before the url parameter"; the key is the options' one
        var url = parameters.Remove("url", out var given) ? given : request.Url;
        parameters.Remove("api_key");
        var query = new StringBuilder("api_key=").Append(Uri.EscapeDataString(_options.ApiKey));
        foreach (var (name, value) in parameters)
        {
            query.Append('&').Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
        }
        query.Append("&url=").Append(Uri.EscapeDataString(url));
        return new Uri($"{_options.Endpoint.AbsoluteUri.TrimEnd('?')}?{query}");
    }

    private bool Forwards(ScrapeRequest request, ScraperApiProxy proxy) =>
        _options.ForwardHeaders && proxy != ScraperApiProxy.UltraPremium && request.Headers?.Any(h => IsForwarded(h.Key)) == true;

    /// <summary>A header of the request that goes to the site: not the body's (Content-*), not one of the call itself.</summary>
    private static bool IsForwarded(string name) =>
        !NotForwarded.Contains(name) && !name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase);

    /// <summary>The page as the API gave it: its headers, the final URL, the body in UTF-8, the cost counted.</summary>
    private async Task<PageResponse> PageOf(ScrapeRequest request, HttpResponseMessage answer, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in answer.Headers.Concat(answer.Content.Headers)) headers[header.Key] = string.Join(", ", header.Value);

        if (headers.TryGetValue("sa-credit-cost", out var cost) && double.TryParse(cost, NumberStyles.Float, CultureInfo.InvariantCulture, out var credits))
        {
            AddCredits(credits);
        }

        var body = await answer.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var url = headers.TryGetValue("sa-final-url", out var final) && Uri.TryCreate(final, UriKind.Absolute, out var finalUri)
            ? finalUri.AbsoluteUri
            : request.Url;
        var contentType = Utf8(answer.Content.Headers.ContentType?.ToString());
        if (contentType != null) headers["Content-Type"] = contentType;
        return new PageResponse((int)answer.StatusCode, url, body, contentType, headers);
    }

    /// <summary>
    /// The API gives every page in UTF-8, whatever its HTML says: an HTML or XML type without a charset gets <c>charset=utf-8</c>, so the
    /// page's own meta charset does not mislead the decoding.
    /// </summary>
    internal static string? Utf8(string? contentType) =>
        contentType != null
        && !contentType.Contains("charset", StringComparison.OrdinalIgnoreCase)
        && (contentType.Contains("html", StringComparison.OrdinalIgnoreCase) || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
            ? contentType + "; charset=utf-8"
            : contentType;

    /// <summary>What the site answered the API (<c>sa-statuscode</c>); null when the header is not there.</summary>
    private static int? SiteStatusOf(HttpResponseMessage answer) =>
        answer.Headers.TryGetValues("sa-statuscode", out var values)
        && int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var status)
            ? status
            : null;

    /// <summary>The start of an error's body, without the key: an error may repeat the call.</summary>
    private async Task<string> ErrorOf(HttpResponseMessage answer, CancellationToken cancellationToken)
    {
        var text = Hide(await answer.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).ReplaceLineEndings(" ").Trim();
        return text.Length <= 200 ? text : text[..200] + "…";
    }

    /// <summary>The text with the API key, as it is and as a URL has it, replaced by <c>***</c>.</summary>
    internal string Hide(string text)
    {
        text = text.Replace(_options.ApiKey, "***", StringComparison.Ordinal);
        var escaped = Uri.EscapeDataString(_options.ApiKey);
        return escaped == _options.ApiKey ? text : text.Replace(escaped, "***", StringComparison.Ordinal);
    }
}
