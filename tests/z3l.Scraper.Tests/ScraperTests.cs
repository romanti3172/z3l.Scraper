using z3l.FastHtmlParser;
using z3l.Scraper.Validation;

namespace z3l.Scraper.Tests;

[TestClass]
public sealed class ScraperTests
{
    /// <summary>No retry delays and throttle pauses of 50 ms at most, so tests about other things run fast.</summary>
    internal static ScraperOptions FastOptions() => new()
    {
        RetryDelay = TimeSpan.Zero,
        ProgressInterval = null,
        MaxRetries = 2,
        MaxThrottleDelay = TimeSpan.FromMilliseconds(50),
    };

    internal static string Html(string body) => $"<html><body>{body}</body></html>";

    // ------------------------------------------------------------------ flow

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
            .Route("product", ctx => ctx.Emit(ctx.Html.Find(HtmlTag.H1).Text()!));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/list", "list"));

        CollectionAssert.AreEquivalent(new[] { "One", "Two", "Three" }, result.Items.ToArray());
        Assert.AreEqual(5, result.Stats.Succeeded);
        Assert.AreEqual(1, result.Stats.Duplicates);
        Assert.IsEmpty(result.Failed);
    }

    [TestMethod]
    public async Task AsyncHandlers_Work()
    {
        var loader = new FakeLoader().Page("https://s.test/a", Html("x"));
        var scraper = new Scraper<string>(loader, FastOptions()).Route("default", async ctx =>
        {
            await Task.Delay(10, ctx.CancellationToken);
            ctx.Emit("after await");
        });

        var result = await scraper.RunToListAsync("https://s.test/a");

        CollectionAssert.AreEqual(new[] { "after await" }, result.Items.ToArray());
    }

    // ------------------------------------------------------------------ retries and failures

    [TestMethod]
    public async Task RetriesBrokenPage_ThenHandlesIt()
    {
        var loader = new FakeLoader().Responses("https://s.test/a",
            (500, "error"),
            (200, "<html><body>cut off"),
            (200, Html("ok")));
        var options = FastOptions();
        options.Rules.Default.Content = ExpectedContent.Html;
        var scraper = new Scraper<string>(loader, options).Route("default", ctx => ctx.Emit("ok"));

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
    public async Task PerRequestMaxRetries_OverridesOptions()
    {
        var loader = new FakeLoader().Page("https://s.test/a", "error", 500);
        var scraper = new Scraper<string>(loader, FastOptions()).Route("default", _ => { });

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/a") { MaxRetries = 0 });

        Assert.AreEqual(1, result.Stats.Loads);
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
    public async Task LoaderNonRetryableException_FailsAtOnce()
    {
        var loader = new FakeLoader().Throws("https://s.test/a", new NonRetryableException("too big"));
        var scraper = new Scraper<string>(loader, FastOptions()).Route("default", _ => { });

        var result = await scraper.RunToListAsync("https://s.test/a");

        Assert.AreEqual(1, result.Stats.Loads);
        Assert.AreEqual(FailureReason.NonRetryable, result.Failed[0].Reason);
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
        var scraper = new Scraper<string>(loader, options).Route("default", ctx => ctx.Emit("data"));

        var result = await scraper.RunToListAsync("https://s.test/a");

        Assert.HasCount(1, result.Items);
        Assert.AreEqual(2, result.Stats.Blocked);
        Assert.HasCount(2, loader.Blocked);
    }

    [TestMethod]
    public async Task BlockMarker_OnErrorStatus_IsBlocked_NotPlainRetry()
    {
        // Cloudflare answers 503 with "Just a moment...": the loader must hear about it to change the proxy
        var loader = new FakeLoader().Responses("https://s.test/a", (503, Html("<title>Just a moment...</title>")), (200, Html("ok")));
        var options = FastOptions();
        options.Rules.Default.BlockedMarkers.Add("Just a moment");
        var scraper = new Scraper<string>(loader, options).Route("default", ctx => ctx.Emit("ok"));

        var result = await scraper.RunToListAsync("https://s.test/a");

        Assert.AreEqual(1, result.Stats.Blocked);
        Assert.HasCount(1, loader.Blocked);
        Assert.HasCount(1, result.Items);
    }

    [TestMethod]
    public async Task NoContent204_IsAcceptedWithEmptyBody()
    {
        var loader = new DelegateLoader((r, _) => Task.FromResult(new PageResponse(204, r.Url, [])));
        var scraper = new Scraper<int>(loader, FastOptions()).Route("default", ctx => ctx.Emit(ctx.Response.StatusCode));

        var result = await scraper.RunToListAsync("https://s.test/a");

        CollectionAssert.AreEqual(new[] { 204 }, result.Items.ToArray());
    }

    [TestMethod]
    public async Task EmptyBody_IsRetried()
    {
        var loader = new DelegateLoader((r, _) => Task.FromResult(new PageResponse(200, r.Url, [])));
        var scraper = new Scraper<string>(loader, FastOptions()).Route("default", _ => { });

        var result = await scraper.RunToListAsync("https://s.test/a");

        StringAssert.Contains(result.Failed[0].Message, "empty body");
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
            .Route("product", ctx => ctx.Emit("product"))
            .Route("other", ctx => ctx.Emit("other"));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/p", "product"), new ScrapeRequest("https://s.test/other", "other"));

        Assert.HasCount(2, result.Items);
        Assert.AreEqual(3, result.Stats.Loads);
    }

    [TestMethod]
    public async Task CustomRule_CanBlock()
    {
        var loader = new FakeLoader().Responses("https://s.test/a", (200, Html("tiny")), (200, Html(new string('x', 200))));
        var options = FastOptions();
        options.Rules.Default.Custom = (_, response) => response.Body.Length < 100 ? ValidationResult.Blocked("too short") : ValidationResult.Ok;
        var scraper = new Scraper<string>(loader, options).Route("default", ctx => ctx.Emit("ok"));

        var result = await scraper.RunToListAsync("https://s.test/a");

        Assert.AreEqual(1, result.Stats.Blocked);
        Assert.HasCount(1, result.Items);
    }

    // ------------------------------------------------------------------ handlers

    [TestMethod]
    public async Task HandlerError_FailsWithoutRetry_ByDefault()
    {
        var loader = new FakeLoader().Page("https://s.test/a", Html("x"));
        var scraper = new Scraper<string>(loader, FastOptions()).Route("default", _ => throw new FormatException("bad price"));

        var result = await scraper.RunToListAsync("https://s.test/a");

        Assert.AreEqual(FailureReason.HandlerError, result.Failed[0].Reason);
        Assert.IsInstanceOfType<FormatException>(result.Failed[0].Exception);
        Assert.AreEqual(1, result.Stats.Loads);
    }

    [TestMethod]
    public async Task HandlerError_RetriedWhenEnabled()
    {
        var loader = new FakeLoader().Page("https://s.test/a", Html("x"));
        var options = FastOptions();
        options.RetryOnHandlerError = true;
        var calls = 0;
        var scraper = new Scraper<string>(loader, options).Route("default", ctx =>
        {
            if (Interlocked.Increment(ref calls) < 3) throw new FormatException("flaky");
            ctx.Emit("ok");
        });

        var result = await scraper.RunToListAsync("https://s.test/a");

        Assert.HasCount(1, result.Items);
        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public async Task RetryRequestException_RetriesEvenWhenHandlerErrorsAreNotRetried()
    {
        var loader = new FakeLoader().Page("https://s.test/a", Html("x"));
        var calls = 0;
        var scraper = new Scraper<string>(loader, FastOptions()).Route("default", ctx =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new RetryRequestException("prices hidden", blocked: true);
            ctx.Emit("ok");
        });

        var result = await scraper.RunToListAsync("https://s.test/a");

        Assert.HasCount(1, result.Items);
        Assert.AreEqual(1, result.Stats.Blocked);
        Assert.HasCount(1, loader.Blocked);
    }

    [TestMethod]
    public async Task NonRetryableException_FromHandler_FailsEvenWhenRetryOnHandlerError()
    {
        var loader = new FakeLoader().Page("https://s.test/a", Html("x"));
        var options = FastOptions();
        options.RetryOnHandlerError = true;
        var scraper = new Scraper<string>(loader, options).Route("default", _ => throw new NonRetryableException("adult page"));

        var result = await scraper.RunToListAsync("https://s.test/a");

        Assert.AreEqual(FailureReason.NonRetryable, result.Failed[0].Reason);
        Assert.AreEqual(1, result.Stats.Loads);
    }

    [TestMethod]
    public async Task ItemsOfFailedHandler_AreDropped_SoRetryGivesNoDuplicates()
    {
        var loader = new FakeLoader().Page("https://s.test/a", Html("x"));
        var calls = 0;
        var scraper = new Scraper<string>(loader, FastOptions()).Route("default", ctx =>
        {
            ctx.Emit("item");
            if (Interlocked.Increment(ref calls) == 1) throw new RetryRequestException("half done");
        });

        var result = await scraper.RunToListAsync("https://s.test/a");

        CollectionAssert.AreEqual(new[] { "item" }, result.Items.ToArray());
        Assert.AreEqual(1, result.Stats.Items);
    }

    [TestMethod]
    public async Task EmitAfterHandlerFinished_Throws()
    {
        var loader = new FakeLoader().Page("https://s.test/a", Html("x"));
        ScrapeContext<string>? leaked = null;
        var scraper = new Scraper<string>(loader, FastOptions()).Route("default", ctx => leaked = ctx);

        await scraper.RunToListAsync("https://s.test/a");

        Assert.ThrowsExactly<InvalidOperationException>(() => leaked!.Emit("late"));
        Assert.ThrowsExactly<InvalidOperationException>(() => leaked!.Enqueue(new ScrapeRequest("https://s.test/b")));
    }

    [TestMethod]
    public async Task Emit_IsThreadSafe()
    {
        var loader = new FakeLoader().Page("https://s.test/a", Html("x"));
        var scraper = new Scraper<int>(loader, FastOptions()).Route("default", ctx =>
            Parallel.For(0, 1000, i => ctx.Emit(i)));

        var result = await scraper.RunToListAsync("https://s.test/a");

        Assert.HasCount(1000, result.Items.Distinct().ToArray());
    }

    [TestMethod]
    public async Task Json_DeserializesBody()
    {
        var loader = new DelegateLoader((r, _) => Task.FromResult(PageResponse.FromText(200, r.Url, """{"name":"Tea","price":9.5}""", "application/json")));
        var scraper = new Scraper<Dto>(loader, FastOptions()).Route("default", ctx => ctx.Emit(ctx.Json<Dto>()!));

        var result = await scraper.RunToListAsync("https://s.test/api");

        Assert.AreEqual(new Dto("Tea", 9.5m), result.Items[0]);
    }

    public sealed record Dto(string Name, decimal Price);

    [TestMethod]
    public async Task UnknownLabel_ThrowsAtStart()
    {
        var scraper = new Scraper<string>(new FakeLoader(), FastOptions()).Route("list", _ => { });

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scraper.RunToListAsync(new ScrapeRequest("https://s.test/a", "product")));
        StringAssert.Contains(error.Message, "product");
    }

    [TestMethod]
    public async Task UnknownLabel_FromHandler_FailsThatRequest()
    {
        var loader = new FakeLoader().Page("https://s.test/a", Html("x"));
        var scraper = new Scraper<string>(loader, FastOptions()).Route("default", ctx => ctx.Enqueue("/b", "typo"));

        var result = await scraper.RunToListAsync("https://s.test/a");

        Assert.AreEqual(FailureReason.HandlerError, result.Failed[0].Reason);
        StringAssert.Contains(result.Failed[0].Message, "typo");
    }

    [TestMethod]
    public async Task NoHandlers_Throws()
    {
        var scraper = new Scraper<string>(new FakeLoader(), FastOptions());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scraper.RunToListAsync("https://s.test/a"));
    }

    [TestMethod]
    public async Task BadStartUrl_Throws()
    {
        var scraper = new Scraper<string>(new FakeLoader(), FastOptions()).Route("default", _ => { });

        var error = await Assert.ThrowsExactlyAsync<ArgumentException>(() => scraper.RunToListAsync("s.test/no-scheme"));
        StringAssert.Contains(error.Message, "s.test/no-scheme");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => scraper.RunToListAsync("ftp://s.test/file"));
    }

    [TestMethod]
    public async Task BadOptions_Throw()
    {
        var options = FastOptions();
        options.RequestTimeout = TimeSpan.Zero;
        var scraper = new Scraper<string>(new FakeLoader(), options).Route("default", _ => { });

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => scraper.RunToListAsync("https://s.test/a"));
    }

    [TestMethod]
    public async Task FallbackRoute_HandlesUnknownLabels()
    {
        var loader = new FakeLoader().Page("https://s.test/a", Html("x"));
        var scraper = new Scraper<string>(loader, FastOptions()).FallbackRoute(ctx => { ctx.Emit(ctx.Request.Label); return Task.CompletedTask; });

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/a", "anything"));

        CollectionAssert.AreEqual(new[] { "anything" }, result.Items.ToArray());
    }

    [TestMethod]
    public async Task RequestFailedHandlerThatThrows_DoesNotBreakTheRun()
    {
        var loader = new FakeLoader().Page("https://s.test/a", "error", 500).Page("https://s.test/b", Html("x"));
        var scraper = new Scraper<string>(loader, FastOptions()).Route("default", ctx => ctx.Emit(ctx.Request.Url));
        scraper.RequestFailed += _ => throw new InvalidOperationException("bad subscriber");

        var result = await scraper.RunToListAsync("https://s.test/a", "https://s.test/b");

        Assert.HasCount(1, result.Items);
        Assert.HasCount(1, result.Failed);
    }

    // ------------------------------------------------------------------ queue

    [TestMethod]
    public async Task PostRequests_WithDifferentBodies_AreNotDuplicates()
    {
        var loader = new FakeLoader().Page("https://s.test/api", "{\"ok\":true}");
        var scraper = new Scraper<string>(loader, FastOptions()).Route("default", ctx => ctx.Emit(ctx.Request.Body!));

        var result = await scraper.RunToListAsync(
            new ScrapeRequest("https://s.test/api") { Method = HttpMethod.Post, Body = "page=1" },
            new ScrapeRequest("https://s.test/api") { Method = HttpMethod.Post, Body = "page=2" },
            new ScrapeRequest("https://s.test/api") { Method = HttpMethod.Post, Body = "page=2" },
            new ScrapeRequest("https://s.test/api#frag") { Method = HttpMethod.Post, Body = "page=1" });

        Assert.HasCount(2, result.Items);
        Assert.AreEqual(2, result.Stats.Duplicates);
    }

    [TestMethod]
    public async Task SkipDuplicateCheck_LoadsTheSamePageAgain()
    {
        var loader = new FakeLoader().Page("https://s.test/a", Html("x"));
        var scraper = new Scraper<string>(loader, FastOptions()).Route("default", ctx => ctx.Emit("x"));

        var result = await scraper.RunToListAsync(
            new ScrapeRequest("https://s.test/a"),
            new ScrapeRequest("https://s.test/a") { SkipDuplicateCheck = true });

        Assert.HasCount(2, result.Items);
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
    public async Task MaxRequests_StopsAcceptingNewPages()
    {
        // every page links to two new pages: without the limit the crawl would never end
        var loader = new DelegateLoader((r, _) =>
        {
            var n = int.Parse(r.Url[(r.Url.LastIndexOf('/') + 1)..]);
            return Task.FromResult(PageResponse.FromText(200, r.Url, Html($"<a href='/{2 * n}'>a</a><a href='/{2 * n + 1}'>b</a>")));
        });
        var options = FastOptions();
        options.MaxRequests = 10;
        var scraper = new Scraper<string>(loader, options).Route("default", ctx => ctx.Enqueue(ctx.Html.XPathValues("//a/@href")));

        var result = await scraper.RunToListAsync("https://s.test/1");

        Assert.AreEqual(10, result.Stats.Enqueued);
        Assert.AreEqual(10, result.Stats.Succeeded);
        Assert.IsGreaterThan(0, result.Stats.SkippedByLimit);
        StringAssert.Contains(result.Stats.ToString(), "over limits");
    }

    [TestMethod]
    public async Task MaxRequestsPerMinute_SpreadsLoads()
    {
        var loader = new FakeLoader();
        var urls = Enumerable.Range(0, 6).Select(i => $"https://s.test/{i}").ToArray();
        foreach (var url in urls) loader.Page(url, Html("x"));
        var options = FastOptions();
        options.MaxConcurrency = 6;
        options.MaxRequestsPerMinute = 600;     // one load per 100 ms

        var scraper = new Scraper<string>(loader, options).Route("default", _ => { });
        var result = await scraper.RunToListAsync(urls.Select(u => (ScrapeRequest)u));

        Assert.AreEqual(6, result.Stats.Succeeded);
        Assert.IsGreaterThanOrEqualTo(450, result.Stats.Elapsed.TotalMilliseconds);  // 5 gaps of 100 ms, some slack for timers
    }

    // ------------------------------------------------------------------ stopping

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
        var scraper = new Scraper<string>(loader, options).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync(urls.Select(u => (ScrapeRequest)u));

        Assert.IsTrue(result.Stats.StoppedByTimeLimit);
        Assert.IsGreaterThan(0, result.Items.Count);
        Assert.IsLessThan(100, result.Items.Count);
        Assert.IsGreaterThan(0, result.Stats.Pending);
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task MaxRunDuration_DoesNotWaitForDelayedRetries()
    {
        var loader = new FakeLoader().Page("https://s.test/a", "error", 500);
        var options = FastOptions();
        options.RetryDelay = TimeSpan.FromSeconds(30);
        options.MaxRunDuration = TimeSpan.FromMilliseconds(300);
        var scraper = new Scraper<string>(loader, options).Route("default", _ => { });

        var result = await scraper.RunToListAsync("https://s.test/a");

        Assert.IsTrue(result.Stats.StoppedByTimeLimit);
        Assert.IsLessThan(5, result.Stats.Elapsed.TotalSeconds);
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
        var scraper = new Scraper<string>(loader, options).Route("default", ctx => ctx.Emit(ctx.Request.Url));

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
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task SecondRunWhileRunning_Throws()
    {
        var loader = new FakeLoader { Delay = TimeSpan.FromMilliseconds(300) };
        loader.Page("https://s.test/a", Html("x"));
        var scraper = new Scraper<string>(loader, FastOptions()).Route("default", _ => { });

        var first = scraper.RunToListAsync("https://s.test/a");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scraper.RunToListAsync("https://s.test/a"));
        Assert.ThrowsExactly<InvalidOperationException>(() => scraper.Route("other", _ => { }));

        var result = await first;
        Assert.AreEqual(1, result.Stats.Succeeded);
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

    // ------------------------------------------------------------------ retry delays

    [TestMethod]
    public void RetryDelay_GrowsAndIsCapped()
    {
        var options = new ScraperOptions { RetryDelay = TimeSpan.FromSeconds(1), RetryBackoffFactor = 2, MaxRetryDelay = TimeSpan.FromSeconds(5) };
        var run = new ScraperRun<string>(new Scraper<string>(new FakeLoader(), options), CancellationToken.None);

        Assert.IsTrue(run.RetryDelay(1) is { TotalMilliseconds: >= 800 and <= 1200 });
        Assert.IsTrue(run.RetryDelay(3) is { TotalMilliseconds: >= 3200 and <= 4800 });
        Assert.IsTrue(run.RetryDelay(10) is { TotalMilliseconds: >= 4000 and <= 6000 });
    }

    [TestMethod]
    public void RetryAfter_ParsesSecondsAndDates()
    {
        static PageResponse WithRetryAfter(string value) =>
            PageResponse.FromText(429, "https://s.test/", "x", headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["retry-after"] = value });

        Assert.AreEqual(TimeSpan.FromSeconds(7), ScraperRun<string>.RetryAfter(WithRetryAfter("7")));
        var date = DateTimeOffset.UtcNow.AddSeconds(30).ToString("r");
        Assert.IsTrue(ScraperRun<string>.RetryAfter(WithRetryAfter(date)) is { TotalSeconds: > 25 and <= 31 });
        Assert.AreEqual(TimeSpan.Zero, ScraperRun<string>.RetryAfter(WithRetryAfter("Wed, 21 Oct 2015 07:28:00 GMT")));
        Assert.IsNull(ScraperRun<string>.RetryAfter(WithRetryAfter("soon")));
        Assert.IsNull(ScraperRun<string>.RetryAfter(PageResponse.FromText(429, "https://s.test/", "x")));
    }

    [TestMethod]
    public async Task RetryAfter_IsRespected()
    {
        var calls = new List<DateTime>();
        var loader = new DelegateLoader((r, _) =>
        {
            lock (calls) calls.Add(DateTime.UtcNow);
            return Task.FromResult(calls.Count == 1
                ? PageResponse.FromText(429, r.Url, "slow down", headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Retry-After"] = "1" })
                : PageResponse.FromText(200, r.Url, Html("ok")));
        });
        var options = FastOptions();
        options.RetryDelay = TimeSpan.FromMilliseconds(10);
        var scraper = new Scraper<string>(loader, options).Route("default", ctx => ctx.Emit("ok"));

        var result = await scraper.RunToListAsync("https://s.test/a");

        Assert.HasCount(1, result.Items);
        Assert.IsGreaterThanOrEqualTo(900, (calls[1] - calls[0]).TotalMilliseconds);
    }
}
