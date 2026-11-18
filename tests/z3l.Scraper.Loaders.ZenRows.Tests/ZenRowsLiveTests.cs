using System.Collections.Concurrent;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Loaders.ZenRows.Tests;

/// <summary>
/// ZenRowsPageLoader against the real API: set Z3L_TEST_ZENROWS to an API key (a trial key should do; a run should spend about 80
/// credits). The pages are httpbin.org, example.com and quotes.toscrape.com. One test at a time: a trial plan allows 5 calls at
/// once, and more answer 429. They have not run yet: no key was at hand when the loader was written.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ZenRowsLiveTests
{
    private static string Key()
    {
        var key = Environment.GetEnvironmentVariable("Z3L_TEST_ZENROWS");
        if (string.IsNullOrEmpty(key)) Assert.Inconclusive("Set Z3L_TEST_ZENROWS to a ZenRows API key to test against the real API");
        return key;
    }

    /// <summary>Keeps the query of every call without the key.</summary>
    private sealed class Recording() : DelegatingHandler(new SocketsHttpHandler())
    {
        public ConcurrentQueue<string> Queries { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var parameters = request.RequestUri!.Query.TrimStart('?').Split('&').Where(p => !p.StartsWith("apikey=", StringComparison.Ordinal));
            Queries.Enqueue(Uri.UnescapeDataString(string.Join('&', parameters)));
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static (ZenRowsPageLoader Loader, Recording Calls) Loader(Action<ZenRowsPageLoaderOptions>? configure = null, string? key = null)
    {
        var calls = new Recording();
        var options = new ZenRowsPageLoaderOptions { ApiKey = key ?? Key(), HttpClient = new HttpClient(calls) };
        configure?.Invoke(options);
        return (new ZenRowsPageLoader(options), calls);
    }

    private static Scraper ScraperOf(IPageLoader loader, int maxRetries = 1) =>
        new Scraper(loader, new ScraperOptions { RetryDelay = TimeSpan.Zero, ProgressInterval = null, MaxRetries = maxRetries, NotFoundRetries = 0 })
            .Route("default", ctx => ctx.Emit(ctx.Response.Text));

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task APage_ComesAsTheSiteGaveIt_WithTheRequestsHeaders()
    {
        var (loader, calls) = Loader();
        await using var _ = loader;
        var request = new ScrapeRequest("https://httpbin.org/headers") { Headers = new Dictionary<string, string> { ["X-Token"] = "t1" } };

        var page = await loader.LoadAsync(request, CancellationToken.None);

        Assert.AreEqual(200, page.StatusCode);
        StringAssert.StartsWith(page.ContentType, "application/json");
        StringAssert.Contains(page.Text, "\"X-Token\": \"t1\"", "a header of the request reaches the site");
        StringAssert.Contains(calls.Queries.Single(), "custom_headers=true");
        Assert.IsTrue(page.Headers.ContainsKey("X-Request-Id"));
        Assert.AreEqual(1, loader.Credits, "X-Request-Credits of a standard call");
    }

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task APost_AndCookies_ReachTheSite()
    {
        var (loader, _) = Loader();
        await using var _ = loader;

        var post = await loader.LoadAsync(new ScrapeRequest("https://httpbin.org/anything") { Method = HttpMethod.Post, Body = "q=lamp&page=2" },
            CancellationToken.None);
        var json = await loader.LoadAsync(new ScrapeRequest("https://httpbin.org/anything")
        {
            Method = HttpMethod.Put,
            Body = "{\"q\":\"lamp\"}",
            ContentType = "application/json",
        }, CancellationToken.None);
        var cookies = await loader.LoadAsync(new ScrapeRequest("https://httpbin.org/cookies")
        {
            Headers = new Dictionary<string, string> { ["Cookie"] = "a=1; b=2" },
        }, CancellationToken.None);

        StringAssert.Contains(post.Text, "\"q\": \"lamp\"");
        StringAssert.Contains(post.Text, "\"method\": \"POST\"");
        StringAssert.Contains(json.Text, "\"method\": \"PUT\"");
        StringAssert.Contains(json.Text, "\"Content-Type\": \"application/json\"", "the body's type reaches the site");
        StringAssert.Contains(cookies.Text, "\"a\": \"1\"");
        StringAssert.Contains(cookies.Text, "\"b\": \"2\"");
    }

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task ARedirect_GivesTheFinalUrl_AndA404IsNotFound()
    {
        var (loader, _) = Loader();
        await using var _ = loader;

        var page = await loader.LoadAsync(new ScrapeRequest("https://httpbin.org/redirect-to?url=https://example.com/"), CancellationToken.None);
        var missing = await ScraperOf(loader).RunToListAsync("https://httpbin.org/status/404");

        Assert.AreEqual("https://example.com/", page.Url);
        Assert.AreEqual("https://example.com/", page.Headers["Zr-Final-Url"]);
        Assert.AreEqual(FailureReason.NotFound, missing.Failed.Single().Reason);
    }

    [TestMethod]
    [Timeout(480_000, CooperativeCancellation = true)]
    public async Task TheSiteRefusing_GoesToPremiumProxies_AndItsOwn500DoesNot()
    {
        var (loader, calls) = Loader();
        await using var _ = loader;

        var refused = await ScraperOf(loader).RunToListAsync("https://httpbin.org/status/403");
        var queries = calls.Queries.ToArray();
        calls.Queries.Clear();
        var broken = await ScraperOf(loader).RunToListAsync("https://httpbin.org/status/500");

        Assert.HasCount(1, refused.Failed);
        Assert.HasCount(2, queries);
        Assert.IsFalse(queries[0].Contains("premium_proxy", StringComparison.Ordinal));
        StringAssert.Contains(queries[1], "premium_proxy=true", "the site refused (403): premium proxies next");
        Assert.HasCount(1, broken.Failed);
        Assert.IsFalse(calls.Queries.Any(q => q.Contains("premium_proxy", StringComparison.Ordinal)), "premium proxies do not mend a site's own 500");
        Assert.AreEqual(0, loader.Credits, "a page ZenRows could not get is not billed");
    }

    [TestMethod]
    [Timeout(480_000, CooperativeCancellation = true)]
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
    [Timeout(480_000, CooperativeCancellation = true)]
    public async Task TheProtectedAndTheAutoLevels_LoadAPage()
    {
        var (protectedLoader, protectedCalls) = Loader(o =>
        {
            o.Levels.Clear();
            o.Levels.Add(ZenRowsLevel.Protected);
        });
        await using var _ = protectedLoader;
        var (autoLoader, autoCalls) = Loader(o =>
        {
            o.Levels.Clear();
            o.Levels.Add(ZenRowsLevel.Auto);
        });
        await using var __ = autoLoader;

        var page = await protectedLoader.LoadAsync(new ScrapeRequest("https://example.com/"), CancellationToken.None);
        var auto = await autoLoader.LoadAsync(new ScrapeRequest("https://example.com/"), CancellationToken.None);

        StringAssert.Contains(page.Text, "Example Domain");
        StringAssert.Contains(protectedCalls.Queries.Single(), "js_render=true");
        StringAssert.Contains(protectedCalls.Queries.Single(), "premium_proxy=true");
        Assert.AreEqual(25, protectedLoader.Credits);
        StringAssert.Contains(auto.Text, "Example Domain");
        StringAssert.Contains(autoCalls.Queries.Single(), "mode=auto");
        Assert.IsTrue(autoLoader.Credits is >= 1 and <= 25, $"mode=auto bills the configuration that got through: {autoLoader.Credits}");
    }

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task AWrongKey_AndABadParameter_FailAtOnce()
    {
        Key();
        var (wrong, wrongCalls) = Loader(key: "wrong-key");
        await using var _ = wrong;
        var (bad, badCalls) = Loader(o => o.Configure = (_, _, parameters) => parameters["js_render"] = "maybe");
        await using var __ = bad;

        var noKey = await ScraperOf(wrong).RunToListAsync("https://example.com/");
        var badCall = await ScraperOf(bad).RunToListAsync("https://example.com/");

        Assert.AreEqual(FailureReason.NonRetryable, noKey.Failed.Single().Reason);
        Assert.IsFalse(noKey.Failed.Single().Message.Contains("wrong-key", StringComparison.Ordinal), "the key does not go to the logs");
        Assert.HasCount(1, wrongCalls.Queries);
        Assert.AreEqual(FailureReason.NonRetryable, badCall.Failed.Single().Reason);
        Assert.HasCount(1, badCalls.Queries);
    }
}
