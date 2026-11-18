using System.Collections.Concurrent;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Tests;

/// <summary>ServicePageLoader (and QueuePageLoader under it): a scraping service called a page at a time, within the plan's limit.</summary>
[TestClass]
public sealed class ServicePageLoaderTests
{
    private static string Url(int n, string host = "s.test") => $"https://{host}/p/{n}";

    /// <summary>A service: every call goes to <c>call</c>; a call at level L costs L + 1 credits.</summary>
    private sealed class FakeService(ServicePageLoaderOptions options, Func<ScrapeRequest, int, CancellationToken, Task<PageResponse>> call, int maxLevel = 0)
        : ServicePageLoader(options)
    {
        private int _active, _maxActive;

        public ConcurrentQueue<(string Url, int Level)> Calls { get; } = new();

        public int MaxActive => Volatile.Read(ref _maxActive);

        protected override int MaxLevel => maxLevel;

        protected override async Task<PageResponse> CallAsync(HttpClient client, ScrapeRequest request, int level, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            for (var max = Volatile.Read(ref _maxActive); active > max; max = Volatile.Read(ref _maxActive))
            {
                if (Interlocked.CompareExchange(ref _maxActive, active, max) == max) break;
            }
            Calls.Enqueue((request.Url, level));
            try
            {
                var page = await call(request, level, cancellationToken);
                AddCredits(level + 1);
                return page;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private static PageResponse Page(ScrapeRequest request, string body = "ok") => PageResponse.FromText(200, request.Url, ScraperTests.Html(body));

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Calls_StayWithinThePlan_AndEveryPageComes()
    {
        await using var service = new FakeService(new ServicePageLoaderOptions { MaxConcurrency = 3 }, async (r, _, token) =>
        {
            await Task.Delay(20, token);
            return Page(r);
        });
        var options = ScraperTests.FastOptions();
        options.MaxConcurrency = 8;
        var scraper = new Scraper(service, options).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync(Enumerable.Range(0, 20).Select(n => new ScrapeRequest(Url(n))));

        Assert.HasCount(20, result.Items);
        Assert.AreEqual(3, service.MaxActive, "three calls at once, the plan's limit, whatever the scraper's MaxConcurrency");
        Assert.AreEqual(20, service.Credits);
        Assert.AreEqual(0, service.Pending);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task NoCallStarts_WhileTheLoaderIsPaused()
    {
        await using var service = new FakeService(new ServicePageLoaderOptions(), (r, _, _) => Task.FromResult(Page(r)));
        service.Paused = true;

        var loads = Enumerable.Range(0, 3).Select(n => service.LoadAsync(new ScrapeRequest(Url(n)), CancellationToken.None)).ToArray();
        await Task.Delay(100);

        Assert.IsEmpty(service.Calls, "paused: the handlers are behind, no call starts");
        Assert.AreEqual(3, service.Pending);
        service.Paused = false;
        var pages = await Task.WhenAll(loads);
        Assert.HasCount(3, pages);
        Assert.HasCount(3, service.Calls);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ACallWithoutAnAnswer_IsLost_AndTheScraperRetriesIt()
    {
        var attempts = new ConcurrentDictionary<string, int>();
        await using var service = new FakeService(new ServicePageLoaderOptions { CallTimeout = TimeSpan.FromMilliseconds(100) }, async (r, _, token) =>
        {
            if (attempts.AddOrUpdate(r.Url, 1, (_, n) => n + 1) == 1) await Task.Delay(Timeout.Infinite, token);     // the first call hangs
            return Page(r);
        });
        var scraper = new Scraper(service, ScraperTests.FastOptions()).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync(Url(1));

        CollectionAssert.AreEqual(new[] { Url(1) }, result.Items.ToArray());
        Assert.AreEqual(2, attempts[Url(1)]);
        StringAssert.Contains(service.ToString(), "lost 1");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ServiceErrors_AreRetried_AndAWrongKeyFailsAtOnce()
    {
        var attempts = new ConcurrentDictionary<string, int>();
        await using var service = new FakeService(new ServicePageLoaderOptions(), (r, _, _) =>
        {
            var attempt = attempts.AddOrUpdate(r.Url, 1, (_, n) => n + 1);
            return r.Url switch
            {
                _ when r.Url == Url(1) && attempt == 1 => throw new RemoteLoadException("the service had no page"),
                _ when r.Url == Url(2) && attempt == 1 => throw new HttpRequestException("the service is down"),
                _ when r.Url == Url(3) => throw new NonRetryableException("wrong API key"),
                _ => Task.FromResult(Page(r)),
            };
        });
        var scraper = new Scraper(service, ScraperTests.FastOptions()).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync(Url(1), Url(2), Url(3));

        CollectionAssert.AreEquivalent(new[] { Url(1), Url(2) }, result.Items.ToArray());
        Assert.AreEqual(2, attempts[Url(1)]);
        Assert.AreEqual(2, attempts[Url(2)], "a network error to the service is retried");
        Assert.AreEqual(1, attempts[Url(3)], "another call cannot help");
        Assert.AreEqual(FailureReason.NonRetryable, result.Failed.Single().Reason);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Blocked_GoesOneLevelUp_AndTheHostGoesOnFromThere()
    {
        // the site blocks the plain calls; premium proxies (level 1) get through
        await using var service = new FakeService(new ServicePageLoaderOptions(), (r, level, _) =>
            Task.FromResult(Page(r, level == 0 ? "captcha" : "the page")), maxLevel: 2);
        var options = ScraperTests.FastOptions();
        options.Rules.Default.BlockedMarkers.Add("captcha");
        var scraper = new Scraper(service, options).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var first = await scraper.RunToListAsync(Url(1));
        var second = await scraper.RunToListAsync(Url(2));

        Assert.HasCount(1, first.Items);
        Assert.HasCount(1, second.Items);
        CollectionAssert.AreEqual(new[] { (Url(1), 0), (Url(1), 1), (Url(2), 1) }, service.Calls.ToArray(),
            "blocked at 0, through at 1, and the host's next page starts at 1");
        Assert.AreEqual(1 + 2 + 2, service.Credits);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AStoppedRun_CancelsTheCallsThatWait()
    {
        await using var service = new FakeService(new ServicePageLoaderOptions { MaxConcurrency = 1 }, async (r, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Page(r);
        });
        var options = ScraperTests.FastOptions();
        options.MaxRunDuration = TimeSpan.FromMilliseconds(200);
        var scraper = new Scraper(service, options).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync(Url(1), Url(2), Url(3));

        Assert.IsEmpty(result.Items);
        Assert.IsTrue(result.Stats.StoppedByTimeLimit);
        Assert.AreEqual(0, service.Pending, "every load the run gave up left the loader");
        Assert.HasCount(1, service.Calls, "one call at a time: the others never started");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Dispose_CancelsTheLoadsThatWait()
    {
        var service = new FakeService(new ServicePageLoaderOptions(), async (r, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Page(r);
        });
        var load = service.LoadAsync(new ScrapeRequest(Url(1)), CancellationToken.None);
        while (service.Calls.IsEmpty) await Task.Delay(10);

        await service.DisposeAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => load);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => service.LoadAsync(new ScrapeRequest(Url(2)), CancellationToken.None));
    }

    [TestMethod]
    public void BadSettings_AreRefused()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new FakeService(new ServicePageLoaderOptions { MaxConcurrency = 0 }, (r, _, _) => Task.FromResult(Page(r))));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new FakeService(new ServicePageLoaderOptions { CallTimeout = TimeSpan.Zero }, (r, _, _) => Task.FromResult(Page(r))));
    }
}
