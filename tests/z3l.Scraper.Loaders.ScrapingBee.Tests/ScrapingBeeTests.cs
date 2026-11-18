using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Web;
using z3l.Scraper.Loading;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace z3l.Scraper.Loaders.ScrapingBee.Tests;

/// <summary>ScrapingBeePageLoader against a fake ScrapingBee that answers as its documentation says.</summary>
[TestClass]
public sealed class ScrapingBeeTests
{
    private const string Page = "https://shop.test/p/1";

    /// <summary>A call as ScrapingBee got it.</summary>
    private sealed record Call(HttpMethod Method, Dictionary<string, string> Query, Dictionary<string, string> Headers, string? Body, string? ContentType);

    /// <summary>The fake service: keeps every call and answers with <c>answer</c>.</summary>
    private sealed class FakeBee(Func<Call, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public ConcurrentQueue<Call> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var call = new Call(
                request.Method,
                query.AllKeys.ToDictionary(k => k!, k => query[k]!),
                request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase),
                request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Content?.Headers.ContentType?.ToString());
            Calls.Enqueue(call);
            return answer(call);
        }
    }

    /// <summary>A page as ScrapingBee gives it: the site's headers with the Spb- prefix, its own headers, the cost.</summary>
    private static HttpResponseMessage Answer(string html, HttpStatusCode status = HttpStatusCode.OK, double cost = 1, string? resolved = null)
    {
        var answer = new HttpResponseMessage(status) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
        answer.Headers.TryAddWithoutValidation("Spb-cost", cost.ToString(System.Globalization.CultureInfo.InvariantCulture));
        answer.Headers.TryAddWithoutValidation("Spb-request-id", "req-1");
        answer.Headers.TryAddWithoutValidation("Spb-Set-Cookie", "session=abc; Path=/");
        answer.Headers.TryAddWithoutValidation("Spb-Content-Type", "text/html; charset=utf-8");
        if (resolved != null) answer.Headers.TryAddWithoutValidation("Spb-resolved-url", resolved);
        return answer;
    }

    private static HttpResponseMessage Error(HttpStatusCode status) => new(status) { Content = new StringContent("{\"error\":\"…\"}") };

    /// <summary>ScrapingBee's 500 for a page it could not scrape, with what the site answered, as the real API sends it.</summary>
    private static HttpResponseMessage CouldNotScrape(int siteStatus)
    {
        var answer = Error(HttpStatusCode.InternalServerError);
        answer.Headers.TryAddWithoutValidation("spb-cost", "0");
        answer.Headers.TryAddWithoutValidation("spb-initial-status-code", siteStatus.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return answer;
    }

    private static ScrapingBeePageLoaderOptions Options(FakeBee bee) => new() { ApiKey = "test-key", HttpClient = new HttpClient(bee) };

    private static ScraperOptions FastScraper() => new() { RetryDelay = TimeSpan.Zero, ProgressInterval = null, MaxRetries = 2 };

    private static string Html(string body) => $"<html><body>{body}</body></html>";

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task APage_IsAsTheSiteGaveIt()
    {
        var bee = new FakeBee(_ => Answer(Html("<h1>Lamp</h1>"), resolved: "https://shop.test/p/1?moved"));
        await using var loader = new ScrapingBeePageLoader(Options(bee));

        var page = await loader.LoadAsync(new ScrapeRequest(Page), CancellationToken.None);

        var call = bee.Calls.Single();
        Assert.AreEqual(HttpMethod.Get, call.Method);
        Assert.AreEqual("Bearer test-key", call.Headers["Authorization"], "the key goes in a header, not in the query");
        Assert.IsFalse(call.Query.ContainsKey("api_key"));
        Assert.AreEqual(Page, call.Query["url"]);
        Assert.AreEqual("false", call.Query["render_js"], "no browser by default: a fifth of the price");
        Assert.IsFalse(call.Query.ContainsKey("premium_proxy"));
        Assert.IsFalse(call.Query.ContainsKey("timeout"), "the default CallTimeout waits longer than ScrapingBee does");

        Assert.AreEqual(200, page.StatusCode);
        Assert.AreEqual("https://shop.test/p/1?moved", page.Url);
        StringAssert.Contains(page.Text, "Lamp");
        Assert.AreEqual("session=abc; Path=/", page.Headers["Set-Cookie"], "the site's headers lose their Spb- prefix");
        Assert.AreEqual("text/html; charset=utf-8", page.ContentType);
        Assert.AreEqual("req-1", page.Headers["Spb-request-id"], "ScrapingBee's own headers stay");
        Assert.AreEqual(1, loader.Credits);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task APostAndItsHeaders_GoToTheSite()
    {
        var bee = new FakeBee(_ => Answer(Html("ok")));
        var options = Options(bee);
        options.CountryCode = "DE";
        await using var loader = new ScrapingBeePageLoader(options);
        var request = new ScrapeRequest("https://shop.test/search")
        {
            Method = HttpMethod.Post,
            Body = "q=lamp",
            Headers = new Dictionary<string, string> { ["X-Token"] = "t1", ["Cookie"] = "a=1; b=2" },
        };

        await loader.LoadAsync(request, CancellationToken.None);

        var call = bee.Calls.Single();
        Assert.AreEqual(HttpMethod.Post, call.Method);
        Assert.AreEqual("q=lamp", call.Body);
        Assert.AreEqual("application/x-www-form-urlencoded", call.ContentType);
        Assert.AreEqual("true", call.Query["forward_headers"]);
        Assert.AreEqual("t1", call.Headers["Spb-X-Token"]);
        Assert.AreEqual("a=1;b=2", call.Query["cookies"], "the Cookie header goes as ScrapingBee's cookies");
        Assert.IsFalse(call.Headers.ContainsKey("Spb-Cookie"));
        Assert.AreEqual("de", call.Query["country_code"]);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task APageTheSiteRefused_GoesToPremiumProxies()
    {
        var bee = new FakeBee(call => call.Query.ContainsKey("premium_proxy") ? Answer(Html("the page"), cost: 10) : CouldNotScrape(403));
        await using var loader = new ScrapingBeePageLoader(Options(bee));
        var scraper = new Scraper(loader, FastScraper()).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var first = await scraper.RunToListAsync(Page);
        var second = await scraper.RunToListAsync("https://shop.test/p/2");

        Assert.HasCount(1, first.Items);
        Assert.HasCount(1, second.Items);
        CollectionAssert.AreEqual(new[] { false, true, true }, bee.Calls.Select(c => c.Query.ContainsKey("premium_proxy")).ToArray(),
            "the 500 is not billed; the host goes on with premium proxies");
        Assert.AreEqual(20, loader.Credits);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AServerErrorOrATimeout_IsRetriedAtTheSameLevel()
    {
        var attempts = 0;
        var bee = new FakeBee(_ => Interlocked.Increment(ref attempts) == 1 ? CouldNotScrape(500) : Answer(Html("the page")));
        await using var loader = new ScrapingBeePageLoader(Options(bee));
        var scraper = new Scraper(loader, FastScraper()).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync(Page);

        Assert.HasCount(1, result.Items);
        Assert.IsFalse(bee.Calls.Any(c => c.Query.ContainsKey("premium_proxy")), "premium proxies do not mend a site's own 500");
        Assert.HasCount(2, bee.Calls);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ABlockedPage_GoesUpToTheStealthPool()
    {
        var bee = new FakeBee(call => call.Query.ContainsKey("stealth_proxy") ? Answer(Html("the page"), cost: 75) : Answer(Html("captcha"), cost: 1));
        var options = Options(bee);
        options.Levels.Add(ScrapingBeeProxy.Stealth);
        await using var loader = new ScrapingBeePageLoader(options);
        var scraperOptions = FastScraper();
        scraperOptions.Rules.Default.BlockedMarkers.Add("captcha");
        var request = new ScrapeRequest(Page) { Headers = new Dictionary<string, string> { ["X-Token"] = "t1" } };
        var scraper = new Scraper(loader, scraperOptions).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync(request);

        Assert.HasCount(1, result.Items);
        var calls = bee.Calls.ToArray();
        Assert.HasCount(3, calls, "classic, premium, stealth");
        var stealth = calls[2];
        Assert.AreEqual("true", stealth.Query["stealth_proxy"]);
        Assert.AreEqual("true", stealth.Query["render_js"], "the stealth pool works with JavaScript only");
        Assert.IsFalse(stealth.Query.ContainsKey("forward_headers"), "the stealth pool takes no headers");
        Assert.IsFalse(stealth.Headers.ContainsKey("Spb-X-Token"));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Statuses_MeanWhatScrapingBeeSays()
    {
        var bee = new FakeBee(call => call.Query["url"] switch
        {
            "https://shop.test/gone" => Answer("Not Found", HttpStatusCode.NotFound),
            "https://shop.test/key" => Error(HttpStatusCode.Unauthorized),
            "https://shop.test/busy" when !BusyOnce() => Error(HttpStatusCode.TooManyRequests),
            _ => Answer(Html("ok")),
        });
        await using var loader = new ScrapingBeePageLoader(Options(bee));
        var scraper = new Scraper(loader, FastScraper()).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync("https://shop.test/gone", "https://shop.test/key", "https://shop.test/busy");

        CollectionAssert.AreEqual(new[] { "https://shop.test/busy" }, result.Items.ToArray(), "429 is retried");
        Assert.AreEqual(FailureReason.NotFound, result.Failed.Single(f => f.Request.Url.EndsWith("/gone", StringComparison.Ordinal)).Reason);
        Assert.AreEqual(FailureReason.NonRetryable, result.Failed.Single(f => f.Request.Url.EndsWith("/key", StringComparison.Ordinal)).Reason);
        Assert.AreEqual(1, bee.Calls.Count(c => c.Query["url"] == "https://shop.test/key"), "a wrong key is not called again");
    }

    private int _busy;

    private bool BusyOnce() => Interlocked.Increment(ref _busy) > 1;

    [TestMethod]
    public void TheCallWaitsLessThanTheLoader_AndConfigureAddsParameters()
    {
        var options = Options(new FakeBee(_ => Answer("")));
        options.CallTimeout = TimeSpan.FromSeconds(60);
        options.RenderJs = true;
        options.WaitFor = "div.price";
        options.Configure = (_, level, parameters) => parameters["session_id"] = $"{100 + level}";
        var loader = new ScrapingBeePageLoader(options);

        var uri = loader.CallUri(new ScrapeRequest(Page), ScrapingBeeProxy.Premium, 1);

        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.AreEqual("55000", query["timeout"], "ScrapingBee answers before the loader gives the call up");
        Assert.AreEqual("true", query["render_js"]);
        Assert.AreEqual("div.price", query["wait_for"]);
        Assert.AreEqual("true", query["premium_proxy"]);
        Assert.AreEqual("101", query["session_id"]);
        Assert.AreEqual(Page, query["url"]);
        Assert.StartsWith("https://app.scrapingbee.com/api/v1?", uri.AbsoluteUri);
    }

    [TestMethod]
    public async Task Mistakes_Throw()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ScrapingBeePageLoader(new ScrapingBeePageLoaderOptions()));
        var noLevels = new ScrapingBeePageLoaderOptions { ApiKey = "k" };
        noLevels.Levels.Clear();
        Assert.ThrowsExactly<ArgumentException>(() => new ScrapingBeePageLoader(noLevels));

        await using var loader = new ScrapingBeePageLoader(Options(new FakeBee(_ => Answer(""))));
        await Assert.ThrowsExactlyAsync<NonRetryableException>(() => loader.LoadAsync(new ScrapeRequest(Page) { Method = HttpMethod.Delete }, CancellationToken.None));
    }
}
