using z3l.FastHtmlParser;
using z3l.Scraper.Validation;

namespace z3l.Scraper.Tests;

[TestClass]
public sealed class ScraperTests
{
    private static ScraperOptions FastOptions() => new() { RetryDelay = TimeSpan.Zero, ProgressInterval = null, MaxRetries = 2 };

    private static string Html(string body) => $"<html><body>{body}</body></html>";

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task NoStartRequests_FinishesAtOnce()
    {
        var scraper = new Scraper<string>(new FakeLoader(), FastOptions()).Route("default", _ => { });

        var result = await scraper.RunToListAsync();

        Assert.IsEmpty(result.Items);
        Assert.AreEqual(0, result.Stats.Enqueued);
    }

    [TestMethod]
    public async Task FollowsLinksByLabel_AndStreamsItems()
    {
        var loader = new FakeLoader()
            .Page("https://s.test/list", Html("<a class='p' href='/p/1'>1</a><a class='p' href='/p/2'>2</a><a rel='next' href='/list2'>next</a>"))
            .Page("https://s.test/list2", Html("<a class='p' href='/p/2'>2 again</a><a class='p' href='p/3'>3</a>"))
            .Page("https://s.test/p/1", Html("<h1>One</h1>"))
            .Page("https://s.test/p/2", Html("<h1>Two</h1>"))
            .Page("https://s.test/p/3", Html("<h1>Three</h1>"));

        var scraper = new Scraper<string>(loader, FastOptions())
            .Route("list", ctx =>
            {
                ctx.Enqueue(ctx.Html.XPathValues("//a[@class='p']/@href"), "product");
                ctx.Enqueue(ctx.Html.XPathValue("//a[@rel='next']/@href"), "list");
            })
            .Route("product", async ctx => await ctx.EmitAsync(ctx.Html.Find(HtmlTag.H1).Text()!));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/list", "list"));

        CollectionAssert.AreEquivalent(new[] { "One", "Two", "Three" }, result.Items.ToArray());
        Assert.AreEqual(5, result.Stats.Succeeded);
        Assert.AreEqual(1, result.Stats.Duplicates);
        Assert.IsEmpty(result.Failed);
    }

    [TestMethod]
    public async Task RetriesBrokenPage_ThenHandlesIt()
    {
        var loader = new FakeLoader().Responses("https://s.test/a",
            (500, "error"),
            (200, "<html><body>cut off"),
            (200, Html("ok")));
        var options = FastOptions();
        options.Rules.Default.Content = ExpectedContent.Html;
        var scraper = new Scraper<string>(loader, options).Route("default", async ctx => await ctx.EmitAsync("ok"));

        var result = await scraper.RunToListAsync("https://s.test/a");

        Assert.HasCount(1, result.Items);
        Assert.AreEqual(3, result.Stats.Loads);
        Assert.AreEqual(2, result.Stats.Retries);
    }

    [TestMethod]
    public async Task GivesUpAfterMaxRetries()
    {
        var loader = new FakeLoader().Page("https://s.test/a", "error", 500);
        var scraper = new Scraper<string>(loader, FastOptions()).Route("default", _ => Assert.Fail("handler must not run"));
        FailedRequest? reported = null;
        scraper.RequestFailed += f => reported = f;

        var result = await scraper.RunToListAsync("https://s.test/a");

        Assert.HasCount(1, result.Failed);
        Assert.AreEqual(FailureReason.RetriesExhausted, result.Failed[0].Reason);
        Assert.AreEqual(500, result.Failed[0].StatusCode);
        Assert.AreEqual(3, result.Stats.Loads);   // first attempt + MaxRetries 2
        Assert.IsNotNull(reported);
    }

    [TestMethod]
    public async Task LoaderException_IsRetried()
    {
        var loader = new FakeLoader().Throws("https://s.test/a", new HttpRequestException("connection reset"));
        var scraper = new Scraper<string>(loader, FastOptions()).Route("default", _ => { });

        var result = await scraper.RunToListAsync("https://s.test/a");

        Assert.AreEqual(3, result.Stats.Loads);
        StringAssert.Contains(result.Failed[0].Message, "connection reset");
        Assert.IsInstanceOfType<HttpRequestException>(result.Failed[0].Exception);
    }

    [TestMethod]
    public async Task NotFound_ByStatusAndMarker_RetriedOnceThenFailed()
    {
        var loader = new FakeLoader()
            .Page("https://s.test/gone", Html("x"), 404)
            .Page("https://s.test/removed", Html("Sorry, product not found"));
        var options = FastOptions();
        options.Rules.Default.NotFoundMarkers.Add("PRODUCT NOT FOUND");
        var scraper = new Scraper<string>(loader, options).Route("default", _ => Assert.Fail("handler must not run"));

        var result = await scraper.RunToListAsync("https://s.test/gone", "https://s.test/removed");

        Assert.HasCount(2, result.Failed);
        Assert.IsTrue(result.Failed.All(f => f.Reason == FailureReason.NotFound));
        Assert.AreEqual(4, result.Stats.Loads);   // NotFoundRetries = 1
        Assert.AreEqual(2, result.Stats.NotFound);
    }

    [TestMethod]
    public async Task BlockedResponse_IsReportedToLoader_AndRetried()
    {
        var loader = new FakeLoader()
            .Responses("https://s.test/a", (200, Html("<title>Just a moment...</title>")), (403, "denied"), (200, Html("data")));
        var options = FastOptions();
        options.Rules.Default.BlockedMarkers.Add("just a moment");
        var scraper = new Scraper<string>(loader, options).Route("default", async ctx => await ctx.EmitAsync("data"));

        var result = await scraper.RunToListAsync("https://s.test/a");

        Assert.HasCount(1, result.Items);
        Assert.AreEqual(2, result.Stats.Blocked);
        Assert.HasCount(2, loader.Blocked);
    }

    [TestMethod]
    public async Task LabelRules_AddToDefaultRules()
    {
        var loader = new FakeLoader()
            .Responses("https://s.test/p", (200, Html("no price")), (200, Html("price: 10")))
            .Page("https://s.test/other", Html("no price"));
        var options = FastOptions();
        options.Rules["product"].MustContain.Add("price:");
        var scraper = new Scraper<string>(loader, options)
            .Route("product", async ctx => await ctx.EmitAsync("product"))
            .Route("other", async ctx => await ctx.EmitAsync("other"));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/p", "product"), new ScrapeRequest("https://s.test/other", "other"));

        Assert.HasCount(2, result.Items);
        Assert.AreEqual(3, result.Stats.Loads);
    }

    [TestMethod]
    public async Task HandlerError_FailsWithoutRetry_ByDefault()
    {
        var loader = new FakeLoader().Page("https://s.test/a", Html("x"));
        var scraper = new Scraper<string>(loader, FastOptions()).Route("default", _ => throw new FormatException("bad price"));

        var result = await scraper.RunToListAsync("https://s.test/a");

        Assert.AreEqual(FailureReason.HandlerError, result.Failed[0].Reason);
        Assert.AreEqual(1, result.Stats.Loads);
    }

    [TestMethod]
    public async Task HandlerError_RetriedWhenEnabled()
    {
        var loader = new FakeLoader().Page("https://s.test/a", Html("x"));
        var options = FastOptions();
        options.RetryOnHandlerError = true;
        var calls = 0;
        var scraper = new Scraper<string>(loader, options).Route("default", async ctx =>
        {
            if (Interlocked.Increment(ref calls) < 3) throw new FormatException("flaky");
            await ctx.EmitAsync("ok");
        });

        var result = await scraper.RunToListAsync("https://s.test/a");

        Assert.HasCount(1, result.Items);
        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public async Task UnknownLabel_ThrowsAtStart()
    {
        var scraper = new Scraper<string>(new FakeLoader(), FastOptions()).Route("list", _ => { });

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scraper.RunToListAsync(new ScrapeRequest("https://s.test/a", "product")));
        StringAssert.Contains(error.Message, "product");
    }

    [TestMethod]
    public async Task FallbackRoute_HandlesUnknownLabels()
    {
        var loader = new FakeLoader().Page("https://s.test/a", Html("x"));
        var scraper = new Scraper<string>(loader, FastOptions()).FallbackRoute(async ctx => await ctx.EmitAsync(ctx.Request.Label));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/a", "anything"));

        CollectionAssert.AreEqual(new[] { "anything" }, result.Items.ToArray());
    }

    [TestMethod]
    public async Task PostRequests_WithDifferentBodies_AreNotDuplicates()
    {
        var loader = new FakeLoader().Page("https://s.test/api", "{\"ok\":true}");
        var scraper = new Scraper<string>(loader, FastOptions()).Route("default", async ctx => await ctx.EmitAsync(ctx.Request.Body!));

        var result = await scraper.RunToListAsync(
            new ScrapeRequest("https://s.test/api") { Method = HttpMethod.Post, Body = "page=1" },
            new ScrapeRequest("https://s.test/api") { Method = HttpMethod.Post, Body = "page=2" },
            new ScrapeRequest("https://s.test/api") { Method = HttpMethod.Post, Body = "page=2" },
            new ScrapeRequest("https://s.test/api#frag") { Method = HttpMethod.Post, Body = "page=1" });

        Assert.HasCount(2, result.Items);
        Assert.AreEqual(2, result.Stats.Duplicates);
    }

    [TestMethod]
    public async Task RespectsMaxConcurrency()
    {
        var loader = new FakeLoader { Delay = TimeSpan.FromMilliseconds(30) };
        var urls = Enumerable.Range(0, 20).Select(i => $"https://s.test/{i}").ToArray();
        foreach (var url in urls) loader.Page(url, Html("x"));
        var options = FastOptions();
        options.MaxConcurrency = 3;
        var scraper = new Scraper<string>(loader, options).Route("default", _ => { });

        var result = await scraper.RunToListAsync(urls.Select(u => (ScrapeRequest)u));

        Assert.AreEqual(20, result.Stats.Succeeded);
        Assert.IsLessThanOrEqualTo(3, loader.MaxActive);
        Assert.IsGreaterThan(1, loader.MaxActive);
    }

    [TestMethod]
    public async Task HighPriority_IsLoadedFirst()
    {
        var loader = new FakeLoader();
        var low = Enumerable.Range(0, 5).Select(i => new ScrapeRequest($"https://s.test/low{i}") { Priority = ScrapePriority.Low }).ToList();
        var high = new ScrapeRequest("https://s.test/high") { Priority = ScrapePriority.High };
        foreach (var r in low.Append(high)) loader.Page(r.Url, Html("x"));
        var options = FastOptions();
        options.MaxConcurrency = 1;
        var scraper = new Scraper<string>(loader, options).Route("default", _ => { });

        await scraper.RunToListAsync(low.Append(high));

        Assert.AreEqual("https://s.test/high", loader.Loaded.First());
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task MaxRunDuration_StopsAndReturnsWhatWasScraped()
    {
        var loader = new FakeLoader { Delay = TimeSpan.FromMilliseconds(100) };
        var urls = Enumerable.Range(0, 100).Select(i => $"https://s.test/{i}").ToArray();
        foreach (var url in urls) loader.Page(url, Html("x"));
        var options = FastOptions();
        options.MaxConcurrency = 2;
        options.MaxRunDuration = TimeSpan.FromMilliseconds(400);
        var scraper = new Scraper<string>(loader, options).Route("default", async ctx => await ctx.EmitAsync(ctx.Request.Url));

        var result = await scraper.RunToListAsync(urls.Select(u => (ScrapeRequest)u));

        Assert.IsTrue(result.Stats.StoppedByTimeLimit);
        Assert.IsGreaterThan(0, result.Items.Count);
        Assert.IsLessThan(100, result.Items.Count);
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task Cancellation_Throws()
    {
        var loader = new FakeLoader { Delay = TimeSpan.FromSeconds(5) };
        loader.Page("https://s.test/a", Html("x"));
        var scraper = new Scraper<string>(loader, FastOptions()).Route("default", _ => { });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<OperationCanceledException>(() => scraper.RunToListAsync(["https://s.test/a"], cts.Token));
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task BreakingEnumeration_StopsRun_AndScraperCanRunAgain()
    {
        var loader = new FakeLoader();
        var urls = Enumerable.Range(0, 50).Select(i => $"https://s.test/{i}").ToArray();
        foreach (var url in urls) loader.Page(url, Html("x"));
        var options = FastOptions();
        options.ItemBufferSize = 1;
        var scraper = new Scraper<string>(loader, options).Route("default", async ctx => await ctx.EmitAsync(ctx.Request.Url));

        var taken = 0;
        await foreach (var _ in scraper.RunAsync(urls.Select(u => (ScrapeRequest)u)))
        {
            if (++taken == 3) break;
        }
        var second = await scraper.RunToListAsync("https://s.test/0");

        Assert.AreEqual(3, taken);
        Assert.HasCount(1, second.Items);
    }

    [TestMethod]
    public async Task RequestTimeout_RetriesSlowLoad()
    {
        var loader = new FakeLoader { Delay = TimeSpan.FromSeconds(2) };
        loader.Page("https://s.test/a", Html("x"));
        var options = FastOptions();
        options.RequestTimeout = TimeSpan.FromMilliseconds(50);
        options.MaxRetries = 1;
        var scraper = new Scraper<string>(loader, options).Route("default", _ => { });

        var result = await scraper.RunToListAsync("https://s.test/a");

        Assert.HasCount(1, result.Failed);
        StringAssert.Contains(result.Failed[0].Message, "timeout");
    }

    [TestMethod]
    public async Task Enqueue_SkipsNonHttpLinks()
    {
        var loader = new FakeLoader().Page("https://s.test/a", Html("x"));
        var added = new List<bool>();
        var scraper = new Scraper<string>(loader, FastOptions()).Route("default", ctx =>
        {
            added.Add(ctx.Enqueue("mailto:a@b.c"));
            added.Add(ctx.Enqueue("javascript:void(0)"));
            added.Add(ctx.Enqueue(""));
            added.Add(ctx.Enqueue("#top"));       // same page: duplicate
        });

        await scraper.RunToListAsync("https://s.test/a");

        CollectionAssert.AreEqual(new[] { false, false, false, false }, added);
    }

    [TestMethod]
    public void RetryDelay_GrowsAndIsCapped()
    {
        var options = new ScraperOptions { RetryDelay = TimeSpan.FromSeconds(1), RetryBackoffFactor = 2, MaxRetryDelay = TimeSpan.FromSeconds(5) };
        var run = new ScraperRun<string>(new Scraper<string>(new FakeLoader(), options), CancellationToken.None);

        Assert.IsTrue(run.RetryDelay(1) is { TotalMilliseconds: >= 800 and <= 1200 });
        Assert.IsTrue(run.RetryDelay(3) is { TotalMilliseconds: >= 3200 and <= 4800 });
        Assert.IsTrue(run.RetryDelay(10) is { TotalMilliseconds: >= 4000 and <= 6000 });
    }
}
