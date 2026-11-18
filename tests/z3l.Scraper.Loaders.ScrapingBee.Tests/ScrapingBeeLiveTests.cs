using System.Collections.Concurrent;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Loaders.ScrapingBee.Tests;

/// <summary>
/// ScrapingBeePageLoader against the real API: set Z3L_TEST_SCRAPINGBEE to an API key (a trial key does; a run spends about 90
/// credits). The pages are httpbin.org, example.com and quotes.toscrape.com. One test at a time: a trial plan allows few calls at
/// once, and more answer 429.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ScrapingBeeLiveTests
{
    private static string Key()
    {
        var key = Environment.GetEnvironmentVariable("Z3L_TEST_SCRAPINGBEE");
        if (string.IsNullOrEmpty(key)) Assert.Inconclusive("Set Z3L_TEST_SCRAPINGBEE to a ScrapingBee API key to test against the real API");
        return key;
    }

    /// <summary>Keeps the query of every call (the key goes in a header and is not kept).</summary>
    private sealed class Recording() : DelegatingHandler(new SocketsHttpHandler())
    {
        public ConcurrentQueue<string> Queries { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Queries.Enqueue(Uri.UnescapeDataString(request.RequestUri!.Query));
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static (ScrapingBeePageLoader Loader, Recording Calls) Loader(Action<ScrapingBeePageLoaderOptions>? configure = null, string? key = null)
    {
        var calls = new Recording();
        var options = new ScrapingBeePageLoaderOptions { ApiKey = key ?? Key(), HttpClient = new HttpClient(calls) };
        configure?.Invoke(options);
        return (new ScrapingBeePageLoader(options), calls);
    }

    private static Scraper ScraperOf(IPageLoader loader, int maxRetries = 1) =>
        new Scraper(loader, new ScraperOptions { RetryDelay = TimeSpan.Zero, ProgressInterval = null, MaxRetries = maxRetries, NotFoundRetries = 0 })
            .Route("default", ctx => ctx.Emit(ctx.Response.Text));

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task APage_ComesAsTheSiteGaveIt_WithTheRequestsHeaders()
    {
        var (loader, _) = Loader();
        await using var _ = loader;
        var request = new ScrapeRequest("https://httpbin.org/headers") { Headers = new Dictionary<string, string> { ["X-Token"] = "t1" } };

        var page = await loader.LoadAsync(request, CancellationToken.None);

        Assert.AreEqual(200, page.StatusCode);
        StringAssert.StartsWith(page.ContentType, "application/json");
        StringAssert.Contains(page.Text, "\"X-Token\": \"t1\"", "a forwarded header reaches the site");
        Assert.IsTrue(page.Headers.ContainsKey("Spb-request-id"));
        Assert.AreEqual(1, loader.Credits);
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task APost_AndCookies_ReachTheSite()
    {
        var (loader, _) = Loader();
        await using var _ = loader;

        var post = await loader.LoadAsync(new ScrapeRequest("https://httpbin.org/anything") { Method = HttpMethod.Post, Body = "q=lamp&page=2" },
            CancellationToken.None);
        var cookies = await loader.LoadAsync(new ScrapeRequest("https://httpbin.org/cookies")
        {
            Headers = new Dictionary<string, string> { ["Cookie"] = "a=1; b=2" },
        }, CancellationToken.None);

        StringAssert.Contains(post.Text, "\"q\": \"lamp\"");
        StringAssert.Contains(post.Text, "\"method\": \"POST\"");
        StringAssert.Contains(cookies.Text, "\"a\": \"1\"");
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

        Assert.AreEqual("https://example.com/", page.Url);
        Assert.AreEqual("302", page.Headers["Spb-initial-status-code"]);
        Assert.AreEqual(FailureReason.NotFound, missing.Failed.Single().Reason);
    }

    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task TheSiteRefusing_GoesToPremiumProxies_AndItsOwn500DoesNot()
    {
        var (loader, calls) = Loader();
        await using var _ = loader;

        var refused = await ScraperOf(loader).RunToListAsync("https://httpbin.org/status/403");
        var queries = calls.Queries.ToArray();
        calls.Queries.Clear();
        var broken = await ScraperOf(loader).RunToListAsync("https://httpbin.org/status/500");

        Assert.AreEqual(FailureReason.RetriesExhausted, refused.Failed.Single().Reason);
        Assert.HasCount(2, queries);
        Assert.IsFalse(queries[0].Contains("premium_proxy", StringComparison.Ordinal));
        StringAssert.Contains(queries[1], "premium_proxy=true", "the site refused (403): stronger proxies next");
        StringAssert.Contains(refused.Failed.Single().Message, "ScrapingBee could not scrape the page");
        Assert.IsFalse(calls.Queries.Any(q => q.Contains("premium_proxy", StringComparison.Ordinal)), "premium proxies do not mend a site's own 500");
        Assert.AreEqual(FailureReason.RetriesExhausted, broken.Failed.Single().Reason);
        Assert.AreEqual(0, loader.Credits, "a page ScrapingBee could not scrape is not billed");
    }

    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task JavaScript_IsRendered_WhenAsked()
    {
        var (plain, _) = Loader();
        await using var _ = plain;
        var (rendering, __) = Loader(o =>
        {
            o.RenderJs = true;
            o.WaitFor = "div.quote";
        });
        await using var ___ = rendering;

        var raw = await plain.LoadAsync(new ScrapeRequest("https://quotes.toscrape.com/js/"), CancellationToken.None);
        var rendered = await rendering.LoadAsync(new ScrapeRequest("https://quotes.toscrape.com/js/"), CancellationToken.None);

        Assert.IsFalse(raw.Text.Contains("<div class=\"quote\"", StringComparison.Ordinal), "the page builds its quotes with a script");
        StringAssert.Contains(rendered.Text, "<div class=\"quote\"");
        Assert.AreEqual(1, plain.Credits);
        Assert.AreEqual(5, rendering.Credits);
    }

    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task TheStealthPool_LoadsAPage()
    {
        var (loader, calls) = Loader(o =>
        {
            o.Levels.Clear();
            o.Levels.Add(ScrapingBeeProxy.Stealth);
        });
        await using var _ = loader;

        var page = await loader.LoadAsync(new ScrapeRequest("https://example.com/"), CancellationToken.None);

        StringAssert.Contains(page.Text, "Example Domain");
        StringAssert.Contains(calls.Queries.Single(), "stealth_proxy=true");
        StringAssert.Contains(calls.Queries.Single(), "render_js=true");
        Assert.AreEqual(75, loader.Credits);
    }

    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task ACallThatTakesTooLong_IsRetried()
    {
        var (loader, calls) = Loader(o => o.CallTimeout = TimeSpan.FromSeconds(8));     // ScrapingBee waits 3 s
        await using var _ = loader;

        var result = await ScraperOf(loader).RunToListAsync("https://httpbin.org/delay/6");

        Assert.AreEqual(FailureReason.RetriesExhausted, result.Failed.Single().Reason);
        Assert.HasCount(2, calls.Queries);
        Assert.IsTrue(calls.Queries.All(q => q.Contains("timeout=3000", StringComparison.Ordinal)));
        Assert.AreEqual(0, loader.Credits);
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AWrongKey_AndABadParameter_FailAtOnce()
    {
        Key();
        var (wrong, wrongCalls) = Loader(key: "wrong-key");
        await using var _ = wrong;
        var (bad, badCalls) = Loader(o => o.Configure = (_, _, parameters) => parameters["render_js"] = "maybe");
        await using var __ = bad;

        var noKey = await ScraperOf(wrong).RunToListAsync("https://example.com/");
        var badCall = await ScraperOf(bad).RunToListAsync("https://example.com/");

        Assert.AreEqual(FailureReason.NonRetryable, noKey.Failed.Single().Reason);
        Assert.IsFalse(noKey.Failed.Single().Message.Contains("wrong-key", StringComparison.Ordinal), "the key does not go to the logs");
        Assert.HasCount(1, wrongCalls.Queries);
        Assert.AreEqual(FailureReason.NonRetryable, badCall.Failed.Single().Reason);
        StringAssert.Contains(badCall.Failed.Single().Message, "render_js");
        Assert.HasCount(1, badCalls.Queries);
    }
}
