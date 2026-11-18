using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Loaders.ScrapingBee;

/// <summary>The proxies ScrapingBee loads a page through at a level, and what a call costs.</summary>
public enum ScrapingBeeProxy
{
    /// <summary>Classic proxies: 1 credit a call, 5 with JavaScript.</summary>
    Classic,

    /// <summary>Premium proxies (<c>premium_proxy</c>): 10 credits a call, 25 with JavaScript.</summary>
    Premium,

    /// <summary>
    /// The stealth pool (<c>stealth_proxy</c>), for the hardest sites: 75 credits a successful call. It always renders JavaScript, and
    /// sends no headers or cookies of the request.
    /// </summary>
    Stealth,
}

/// <summary>Settings of <see cref="ScrapingBeePageLoader"/>.</summary>
public sealed class ScrapingBeePageLoaderOptions : ServicePageLoaderOptions
{
    /// <summary>The API key, sent as <c>Authorization: Bearer</c>.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The HTML API.</summary>
    public Uri Endpoint { get; set; } = new("https://app.scrapingbee.com/api/v1");

    /// <summary>
    /// Render JavaScript in a browser (<c>render_js</c>). Off by default: the page as the server gives it, which costs a fifth of a
    /// rendered one. The stealth level renders anyway.
    /// </summary>
    public bool RenderJs { get; set; }

    /// <summary>
    /// The proxies of the levels, the cheapest first: a page that comes back blocked, or that ScrapingBee could not scrape, goes to the
    /// next one, and its host goes on from the level that got through. Classic, then premium by default; add
    /// <see cref="ScrapingBeeProxy.Stealth"/> for the hardest sites.
    /// </summary>
    public List<ScrapingBeeProxy> Levels { get; } = [ScrapingBeeProxy.Classic, ScrapingBeeProxy.Premium];

    /// <summary>Country of the proxies (<c>country_code</c>, two letters); classic and premium proxies have their own lists.</summary>
    public string? CountryCode { get; set; }

    /// <summary>A CSS or XPath selector the browser waits for (<c>wait_for</c>), when JavaScript is rendered.</summary>
    public string? WaitFor { get; set; }

    /// <summary>
    /// Send the headers of the request to the site (<c>forward_headers</c>, their names prefixed with <c>Spb-</c>), and its Cookie
    /// header as <c>cookies</c>. On by default; the stealth level sends neither.
    /// </summary>
    public bool ForwardHeaders { get; set; } = true;

    /// <summary>
    /// Any other parameter of a call, for the request and the level: <c>js_scenario</c>, <c>session_id</c> (one IP for 5 minutes),
    /// <c>device</c> (<c>desktop</c> or <c>mobile</c>), <c>block_resources</c>, <c>wait</c>, <c>window_width</c>...
    /// </summary>
    public Action<ScrapeRequest, int, IDictionary<string, string>>? Configure { get; set; }
}

/// <summary>
/// Loads pages through the ScrapingBee HTML API: a call per page, the service's proxies, browsers and captcha handling. The page comes
/// back as the site gave it: the status (2xx is 200, 404, 410 and 413 as they are), the final URL, the site's headers without their
/// <c>Spb-</c> prefix, and ScrapingBee's own headers (<c>Spb-cost</c>, <c>Spb-request-id</c>...) as they are.
/// <list type="bullet">
/// <item>At most <see cref="ServicePageLoaderOptions.MaxConcurrency"/> calls go at once: set it to your plan's limit; above it the API
/// answers 429, and the page is retried.</item>
/// <item>A page that comes back blocked, or that ScrapingBee could not scrape because the site refused it (500, not billed, with
/// <c>Spb-initial-status-code</c> 403, 429 or 503), goes one level up, through <see cref="ScrapingBeePageLoaderOptions.Levels"/>. Its
/// other 500s (a timeout, a site that does not answer, a 5xx of the site) are retried at the same level.
/// <see cref="ServicePageLoader.Credits"/> sums <c>Spb-cost</c>.</item>
/// <item>A wrong key or no credits left (401), a banned account or a blocked destination (403) and a bad call (400) fail the page
/// at once.</item>
/// <item>GET, POST and PUT go to the site with their body; the request's headers go as <c>Spb-</c> headers with
/// <c>forward_headers</c>.</item>
/// </list>
/// </summary>
/// <example>
/// <code>
/// await using var loader = new ScrapingBeePageLoader(new ScrapingBeePageLoaderOptions
/// {
///     ApiKey = Environment.GetEnvironmentVariable("SCRAPINGBEE_KEY")!,
///     MaxConcurrency = 10,                          // the plan's limit
///     CountryCode = "de",
/// });
/// var scraper = new Scraper(loader).Route(PageType.Item, ctx => ctx.Emit(ctx.Html.XPathValue("//h1")));
/// </code>
/// </example>
public sealed class ScrapingBeePageLoader : ServicePageLoader
{
    /// <summary>Headers of ScrapingBee itself, kept as they are; the other <c>Spb-</c> headers are the site's.</summary>
    private static readonly HashSet<string> OwnHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Spb-cost", "Spb-auto-cost", "Spb-initial-status-code", "Spb-resolved-url", "Spb-request-id",
    };

    private const int MaxServiceTimeout = 140_000;     // ms, ScrapingBee's longest wait and its default

    private readonly ScrapingBeePageLoaderOptions _options;
    private readonly ScrapingBeeProxy[] _levels;

    /// <summary>Creates the loader.</summary>
    /// <exception cref="ArgumentException">No API key, or no levels.</exception>
    public ScrapingBeePageLoader(ScrapingBeePageLoaderOptions options, ILogger? logger = null)
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
            throw new NonRetryableException($"ScrapingBee takes GET, POST and PUT, not {request.Method}");
        }

        var proxy = _levels[level];
        using var message = new HttpRequestMessage(request.Method, CallUri(request, proxy, level));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        if (Forwards(request, proxy))
        {
            foreach (var header in request.Headers!)
            {
                if (IsCookie(header.Key) || header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) continue;
                message.Headers.TryAddWithoutValidation("Spb-" + header.Key, header.Value);
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
            case 200 or 404 or 410 or 413:
                return await PageOf(request, answer, cancellationToken).ConfigureAwait(false);
            case 400:
                throw new NonRetryableException($"ScrapingBee refused the call (400): {await ErrorOf(answer, cancellationToken).ConfigureAwait(false)}");
            case 401:
                throw new NonRetryableException("ScrapingBee: no credits left or a wrong API key (401)");
            case 403:
                throw new NonRetryableException("ScrapingBee: the account is banned, or the page goes to a blocked destination (403)");
            case 429:
                throw new RemoteLoadException("ScrapingBee: too many calls at once (429): set MaxConcurrency to the plan's limit");
            case 500:
            {
                // not billed. Spb-initial-status-code tells why: the site refused (403, 429, 503) - stronger proxies may get through;
                // a timeout, a site that does not answer, a 5xx of the site - the same level again
                var siteStatus = SiteStatusOf(answer);
                var refused = siteStatus is 403 or 429 or 503;
                if (refused) LevelUp(request, level, $"the site answered {siteStatus}");
                var why = siteStatus is { } code ? $"the site answered {code}" : "no answer of the site";
                throw new RemoteLoadException($"ScrapingBee could not scrape the page (500, {why}){(refused ? ": stronger proxies next" : "")}");
            }
            default:
                throw new RemoteLoadException($"ScrapingBee answered {status}: {await ErrorOf(answer, cancellationToken).ConfigureAwait(false)}");
        }
    }

    /// <summary>The API call: the page's URL and the parameters of the level.</summary>
    internal Uri CallUri(ScrapeRequest request, ScrapingBeeProxy proxy, int level)
    {
        var stealth = proxy == ScrapingBeeProxy.Stealth;
        var render = _options.RenderJs || stealth;     // the stealth pool works with JavaScript only
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["url"] = request.Url,
            ["render_js"] = render ? "true" : "false",
        };
        if (proxy == ScrapingBeeProxy.Premium) parameters["premium_proxy"] = "true";
        if (stealth) parameters["stealth_proxy"] = "true";
        if (!string.IsNullOrWhiteSpace(_options.CountryCode)) parameters["country_code"] = _options.CountryCode.Trim().ToLowerInvariant();
        if (render && !string.IsNullOrWhiteSpace(_options.WaitFor)) parameters["wait_for"] = _options.WaitFor;
        if (Forwards(request, proxy)) parameters["forward_headers"] = "true";
        if (!stealth && _options.ForwardHeaders && CookiesOf(request) is { } cookies) parameters["cookies"] = cookies;

        // a call that waits less than ScrapingBee would: it answers before the loader gives the call up (the stealth pool has no timeout)
        var wait = (long)(_options.CallTimeout - TimeSpan.FromSeconds(5)).TotalMilliseconds;
        if (!stealth && wait < MaxServiceTimeout) parameters["timeout"] = Math.Max(1000, wait).ToString(CultureInfo.InvariantCulture);

        _options.Configure?.Invoke(request, level, parameters);

        var query = string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        return new Uri($"{_options.Endpoint.AbsoluteUri.TrimEnd('?')}?{query}");
    }

    private bool Forwards(ScrapeRequest request, ScrapingBeeProxy proxy) =>
        _options.ForwardHeaders && proxy != ScrapingBeeProxy.Stealth
        && request.Headers?.Any(h => !IsCookie(h.Key) && !h.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) == true;

    private static bool IsCookie(string name) => string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase);

    /// <summary>The Cookie header of the request as ScrapingBee takes cookies: <c>name=value;name2=value2</c>.</summary>
    internal static string? CookiesOf(ScrapeRequest request)
    {
        var header = request.Headers?.FirstOrDefault(h => IsCookie(h.Key)).Value;
        if (string.IsNullOrWhiteSpace(header)) return null;
        var cookies = header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return cookies.Length == 0 ? null : string.Join(';', cookies);
    }

    /// <summary>The page as the site gave it: the site's headers without their <c>Spb-</c> prefix, the final URL, the cost counted.</summary>
    private async Task<PageResponse> PageOf(ScrapeRequest request, HttpResponseMessage answer, CancellationToken cancellationToken)
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
            else if (header.Key.StartsWith("Spb-", StringComparison.OrdinalIgnoreCase))
            {
                var name = header.Key[4..];
                headers[name] = value;
                if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase)) siteType = value;
            }
        }

        if (headers.TryGetValue("Spb-cost", out var cost) && double.TryParse(cost, NumberStyles.Float, CultureInfo.InvariantCulture, out var credits))
        {
            AddCredits(credits);
        }

        var body = await answer.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var url = headers.TryGetValue("Spb-resolved-url", out var resolved) && Uri.TryCreate(resolved, UriKind.Absolute, out var final)
            ? final.AbsoluteUri
            : request.Url;
        var contentType = siteType ?? answer.Content.Headers.ContentType?.ToString();
        if (contentType != null) headers["Content-Type"] = contentType;
        return new PageResponse((int)answer.StatusCode, url, body, contentType, headers);
    }

    /// <summary>What the site answered ScrapingBee (<c>Spb-initial-status-code</c>); null when the header is not there.</summary>
    private static int? SiteStatusOf(HttpResponseMessage answer) =>
        answer.Headers.TryGetValues("Spb-initial-status-code", out var values)
        && int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var status)
            ? status
            : null;

    private static async Task<string> ErrorOf(HttpResponseMessage answer, CancellationToken cancellationToken)
    {
        var text = await answer.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= 200 ? text : text[..200] + "…";
    }
}
