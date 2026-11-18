using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Loaders.ZyteApi.Tests;

/// <summary>
/// ZyteApiPageLoader against the real API: set Z3L_TEST_ZYTEAPI to an API key (the $5 of free credit of a new account do; a run makes
/// about 20 calls, a few of them in a browser). The pages are httpbin.org, example.com, quotes.toscrape.com and api.country.is. These
/// tests follow Zyte API's documentation: they have not run against the real API yet. One test at a time, as for the other services.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ZyteApiLiveTests
{
    private static string Key()
    {
        var key = Environment.GetEnvironmentVariable("Z3L_TEST_ZYTEAPI");
        if (string.IsNullOrEmpty(key)) Assert.Inconclusive("Set Z3L_TEST_ZYTEAPI to a Zyte API key to test against the real API");
        return key;
    }

    /// <summary>Keeps the JSON of every call (the key goes in a header and is not kept).</summary>
    private sealed class Recording() : DelegatingHandler(new SocketsHttpHandler())
    {
        public ConcurrentQueue<JsonObject> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Enqueue(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private static (ZyteApiPageLoader Loader, Recording Calls) Loader(Action<ZyteApiPageLoaderOptions>? configure = null, string? key = null)
    {
        var calls = new Recording();
        var options = new ZyteApiPageLoaderOptions { ApiKey = key ?? Key(), HttpClient = new HttpClient(calls) };
        configure?.Invoke(options);
        return (new ZyteApiPageLoader(options), calls);
    }

    private static Scraper ScraperOf(IPageLoader loader, int maxRetries = 1) =>
        new Scraper(loader, new ScraperOptions { RetryDelay = TimeSpan.Zero, ProgressInterval = null, MaxRetries = maxRetries, NotFoundRetries = 0 })
            .Route("default", ctx => ctx.Emit(ctx.Response.Text));

    private static bool IsBrowser(JsonObject call) => call["browserHtml"]?.GetValue<bool>() == true;

    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task APage_ComesAsTheSiteGaveIt_WithTheRequestsHeaders()
    {
        var (loader, calls) = Loader();
        await using var _ = loader;
        var request = new ScrapeRequest("https://httpbin.org/headers") { Headers = new Dictionary<string, string> { ["X-Token"] = "t1" } };

        var page = await loader.LoadAsync(request, CancellationToken.None);

        Assert.AreEqual(200, page.StatusCode);
        StringAssert.StartsWith(page.ContentType, "application/json", "the site's headers come with the page");
        StringAssert.Contains(page.Text, "\"X-Token\": \"t1\"", "a header of the request reaches the site");
        Assert.IsFalse(IsBrowser(calls.Calls.Single()));
    }

    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
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

        StringAssert.Contains(post.Text, "\"q\": \"lamp\"", "the body went form-encoded");
        StringAssert.Contains(post.Text, "\"method\": \"POST\"");
        StringAssert.Contains(cookies.Text, "\"a\": \"1\"");
        StringAssert.Contains(cookies.Text, "\"b\": \"2\"");
    }

    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task ARedirect_GivesTheFinalUrl_AndA404IsNotFound()
    {
        var (loader, _) = Loader();
        await using var _ = loader;

        var page = await loader.LoadAsync(new ScrapeRequest("https://httpbin.org/redirect-to?url=https://example.com/"), CancellationToken.None);
        var missing = await ScraperOf(loader).RunToListAsync("https://httpbin.org/status/404");

        Assert.AreEqual("https://example.com/", page.Url);
        StringAssert.Contains(page.Text, "Example Domain");
        Assert.AreEqual(FailureReason.NotFound, missing.Failed.Single().Reason, "the site's 404 comes in statusCode");
    }

    [TestMethod]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task TheSitesOwn500_IsRetriedOverHttp()
    {
        var (loader, calls) = Loader();
        await using var _ = loader;

        var broken = await ScraperOf(loader).RunToListAsync("https://httpbin.org/status/500");

        Assert.AreEqual(FailureReason.RetriesExhausted, broken.Failed.Single().Reason);
        Assert.HasCount(2, calls.Calls);
        Assert.IsFalse(calls.Calls.Any(IsBrowser), "a browser does not mend the site's own 500");
    }

    [TestMethod]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task TheBrowser_RunsThePagesScripts()
    {
        var (plain, _) = Loader();
        await using var _ = plain;
        var (browser, calls) = Loader(o =>
        {
            o.Levels.Clear();
            o.Levels.Add(ZyteApiRequestType.Browser);
        });
        await using var __ = browser;

        var raw = await plain.LoadAsync(new ScrapeRequest("https://quotes.toscrape.com/js/"), CancellationToken.None);
        var rendered = await browser.LoadAsync(new ScrapeRequest("https://quotes.toscrape.com/js/"), CancellationToken.None);

        Assert.IsFalse(raw.Text.Contains("<div class=\"quote\"", StringComparison.Ordinal), "the page builds its quotes with a script");
        StringAssert.Contains(rendered.Text, "<div class=\"quote\"");
        Assert.AreEqual(200, rendered.StatusCode);
        Assert.IsTrue(IsBrowser(calls.Calls.Single()));
    }

    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task TheGeolocation_IsTheCountryOfTheCall()
    {
        var (loader, calls) = Loader(o => o.Geolocation = "au");
        await using var _ = loader;

        var page = await loader.LoadAsync(new ScrapeRequest("https://api.country.is/"), CancellationToken.None);

        StringAssert.Contains(page.Text, "AU");
        Assert.AreEqual("AU", (string?)calls.Calls.Single()["geolocation"]);
    }

    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task AWrongKey_ABadField_AndABadMix_FailAtOnce()
    {
        Key();
        var (wrong, wrongCalls) = Loader(key: "wrong-key");
        await using var _ = wrong;
        var (bad, badCalls) = Loader(o => o.Configure = (_, _, json) => json["geolocation"] = "XX");
        await using var __ = bad;
        var (mixed, mixedCalls) = Loader(o => o.Configure = (_, _, json) => json["browserHtml"] = true);     // with httpResponseBody
        await using var ___ = mixed;

        var noKey = await ScraperOf(wrong).RunToListAsync("https://example.com/");
        var badField = await ScraperOf(bad).RunToListAsync("https://example.com/");
        var badMix = await ScraperOf(mixed).RunToListAsync("https://example.com/");

        Assert.AreEqual(FailureReason.NonRetryable, noKey.Failed.Single().Reason);
        StringAssert.Contains(noKey.Failed.Single().Message, "401");
        Assert.IsFalse(noKey.Failed.Single().Message.Contains("wrong-key", StringComparison.Ordinal), "the key does not go to the logs");
        Assert.HasCount(1, wrongCalls.Calls);
        Assert.AreEqual(FailureReason.NonRetryable, badField.Failed.Single().Reason);
        StringAssert.Contains(badField.Failed.Single().Message, "400");
        Assert.HasCount(1, badCalls.Calls);
        Assert.AreEqual(FailureReason.NonRetryable, badMix.Failed.Single().Reason);
        StringAssert.Contains(badMix.Failed.Single().Message, "422");
        Assert.HasCount(1, mixedCalls.Calls);
    }
}
