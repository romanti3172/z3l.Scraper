using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Loaders.ScraperApi.Tests;

/// <summary>
/// ScraperApiPageLoader against the real API: set Z3L_TEST_SCRAPERAPI to an API key (a free plan's key does; a run spends about 30
/// credits, and takes some minutes: a page the API cannot get takes it 70 seconds). The pages are httpbin.org, example.com and
/// quotes.toscrape.com. One test at a time: the free plan allows 5 calls at once, and more answer 429.
/// The loader is written from the documentation; these tests check what it relies on, the documented and the rest.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed partial class ScraperApiLiveTests
{
    private static string Key()
    {
        var key = Environment.GetEnvironmentVariable("Z3L_TEST_SCRAPERAPI");
        if (string.IsNullOrEmpty(key)) Assert.Inconclusive("Set Z3L_TEST_SCRAPERAPI to a ScraperAPI key to test against the real API");
        return key;
    }

    /// <summary>Keeps the query of every call, its key hidden, and what the API answered: the status and <c>sa-statuscode</c>.</summary>
    private sealed class Recording() : DelegatingHandler(new SocketsHttpHandler())
    {
        public ConcurrentQueue<string> Queries { get; } = new();

        public ConcurrentQueue<(int Status, string? SiteStatus)> Answers { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Queries.Enqueue(Uri.UnescapeDataString(KeyRegex().Replace(request.RequestUri!.Query, "api_key=***")));
            var answer = await base.SendAsync(request, cancellationToken);
            Answers.Enqueue(((int)answer.StatusCode, answer.Headers.TryGetValues("sa-statuscode", out var values) ? values.FirstOrDefault() : null));
            return answer;
        }
    }

    [GeneratedRegex("api_key=[^&]*")]
    private static partial Regex KeyRegex();

    private static (ScraperApiPageLoader Loader, Recording Calls) Loader(Action<ScraperApiPageLoaderOptions>? configure = null, string? key = null)
    {
        var calls = new Recording();
        var options = new ScraperApiPageLoaderOptions { ApiKey = key ?? Key(), HttpClient = new HttpClient(calls) };
        configure?.Invoke(options);
        return (new ScraperApiPageLoader(options), calls);
    }

    private static Scraper ScraperOf(IPageLoader loader, int maxRetries = 1) =>
        new Scraper(loader, new ScraperOptions { RetryDelay = TimeSpan.Zero, ProgressInterval = null, MaxRetries = maxRetries, NotFoundRetries = 0 })
            .Route("default", ctx => ctx.Emit(ctx.Response.Text));

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task APage_ComesAsTheSiteGaveIt_WithTheRequestsHeaders()
    {
        var (loader, calls) = Loader();
        await using var _ = loader;
        var request = new ScrapeRequest("https://httpbin.org/headers") { Headers = new Dictionary<string, string> { ["X-Token"] = "t1" } };

        var page = await loader.LoadAsync(request, CancellationToken.None);

        Assert.AreEqual(200, page.StatusCode);
        StringAssert.StartsWith(page.ContentType, "application/json");
        StringAssert.Contains(page.Text, "\"X-Token\": \"t1\"", "keep_headers=true: a header of the request reaches the site");
        StringAssert.Contains(calls.Queries.Single(), "keep_headers=true");
        Assert.IsTrue(page.Headers.ContainsKey("sa-credit-cost"));
        Assert.AreEqual(1, loader.Credits);
    }

    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task APostAPut_AndCookies_ReachTheSite()
    {
        var (loader, _) = Loader();
        await using var _ = loader;

        var post = await loader.LoadAsync(new ScrapeRequest("https://httpbin.org/anything") { Method = HttpMethod.Post, Body = "q=lamp&page=2" },
            CancellationToken.None);
        var put = await loader.LoadAsync(new ScrapeRequest("https://httpbin.org/anything")
        {
            Method = HttpMethod.Put,
            Body = "{\"q\":\"lamp\"}",
            ContentType = "application/json",
        }, CancellationToken.None);
        var cookies = await loader.LoadAsync(new ScrapeRequest("https://httpbin.org/cookies")
        {
            Headers = new Dictionary<string, string> { ["Cookie"] = "a=1; b=2" },
        }, CancellationToken.None);

        StringAssert.Contains(post.Text, "\"q\": \"lamp\"", "a form body reaches the site");
        StringAssert.Contains(post.Text, "\"method\": \"POST\"");
        StringAssert.Contains(put.Text, "\"method\": \"PUT\"");
        StringAssert.Contains(put.Text, "\"application/json\"", "the body's Content-Type reaches the site: not documented");
        StringAssert.Contains(cookies.Text, "\"a\": \"1\"", "a Cookie header goes with keep_headers=true");
        StringAssert.Contains(cookies.Text, "\"b\": \"2\"");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ARedirect_GivesTheFinalUrl_AndA404IsNotFound()
    {
        var (loader, _) = Loader();
        await using var _ = loader;

        var page = await loader.LoadAsync(new ScrapeRequest("https://httpbin.org/redirect-to?url=https://example.com/"), CancellationToken.None);
        var missing = await ScraperOf(loader).RunToListAsync("https://httpbin.org/status/404");

        StringAssert.Contains(page.Text, "Example Domain", "follow_redirect is on by default");
        Assert.AreEqual("https://example.com/", page.Url, "sa-final-url: the async API's results have it, the docs do not show it here");
        Assert.AreEqual(FailureReason.NotFound, missing.Failed.Single().Reason);
        Assert.AreEqual(2, loader.Credits, "200 and 404 are billed");
    }

    [TestMethod]
    [Timeout(480_000, CooperativeCancellation = true)]
    public async Task TheSiteRefusing_AndItsOwn500_AreRetried_AndNotBilled()
    {
        var (loader, calls) = Loader();
        await using var _ = loader;

        var refused = await ScraperOf(loader).RunToListAsync("https://httpbin.org/status/403");
        var queries = calls.Queries.ToArray();
        var answers = calls.Answers.ToArray();
        calls.Queries.Clear();
        var broken = await ScraperOf(loader).RunToListAsync("https://httpbin.org/status/500");

        Assert.AreEqual(FailureReason.RetriesExhausted, refused.Failed.Single().Reason, "the API answers 500 after its retries, not the site's 403");
        StringAssert.Contains(refused.Failed.Single().Message, "ScraperAPI had no page after its retries (500, not billed");
        Assert.HasCount(2, queries);
        Assert.IsFalse(queries[0].Contains("premium=true", StringComparison.Ordinal));
        Assert.AreEqual(answers[0].SiteStatus == "403", queries[1].Contains("premium=true", StringComparison.Ordinal),
            "premium proxies next only when the 500 carries the site's 403 in sa-statuscode, which the docs do not show");
        Assert.AreEqual(FailureReason.RetriesExhausted, broken.Failed.Single().Reason);
        Assert.IsFalse(calls.Queries.Any(q => q.Contains("premium=true", StringComparison.Ordinal)), "premium proxies do not mend a site's own 500");
        Assert.AreEqual(0, loader.Credits, "a page the API could not get is not billed");
    }

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task JavaScript_IsRendered_WhenAsked()
    {
        var (plain, _) = Loader();
        await using var _ = plain;
        var (rendering, __) = Loader(o =>
        {
            o.RenderJs = true;
            o.WaitForSelector = "div.quote";
        });
        await using var ___ = rendering;

        var raw = await plain.LoadAsync(new ScrapeRequest("https://quotes.toscrape.com/js/"), CancellationToken.None);
        var rendered = await rendering.LoadAsync(new ScrapeRequest("https://quotes.toscrape.com/js/"), CancellationToken.None);

        Assert.IsFalse(raw.Text.Contains("<div class=\"quote\"", StringComparison.Ordinal), "the page builds its quotes with a script");
        StringAssert.Contains(rendered.Text, "<div class=\"quote\"");
        Assert.AreEqual(1, plain.Credits);
        Assert.AreEqual(10, rendering.Credits);
    }

    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task PremiumProxies_LoadAPage()
    {
        var (loader, calls) = Loader(o =>
        {
            o.Levels.Clear();
            o.Levels.Add(ScraperApiProxy.Premium);
        });
        await using var _ = loader;

        var page = await loader.LoadAsync(new ScrapeRequest("https://example.com/"), CancellationToken.None);

        StringAssert.Contains(page.Text, "Example Domain");
        StringAssert.Contains(calls.Queries.Single(), "premium=true");
        Assert.AreEqual(10, loader.Credits);
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AWrongKey_AndACallAboveMaxCost_FailAtOnce()
    {
        Key();
        var (wrong, wrongCalls) = Loader(key: "wrong-key");
        await using var _ = wrong;
        var (costly, costlyCalls) = Loader(o =>
        {
            o.RenderJs = true;                                                     // 10 credits
            o.Configure = (_, _, parameters) => parameters["max_cost"] = "5";
        });
        await using var __ = costly;

        var noKey = await ScraperOf(wrong).RunToListAsync("https://example.com/");
        var tooDear = await ScraperOf(costly).RunToListAsync("https://example.com/");

        Assert.AreEqual(FailureReason.NonRetryable, noKey.Failed.Single().Reason);
        Assert.IsFalse(noKey.Failed.Single().Message.Contains("wrong-key", StringComparison.Ordinal), "the key does not go to the logs");
        Assert.HasCount(1, wrongCalls.Queries);
        Assert.AreEqual(FailureReason.NonRetryable, tooDear.Failed.Single().Reason);
        StringAssert.Contains(tooDear.Failed.Single().Message, "max_cost");
        Assert.HasCount(1, costlyCalls.Queries);
        Assert.AreEqual(0, costly.Credits);
    }
}
