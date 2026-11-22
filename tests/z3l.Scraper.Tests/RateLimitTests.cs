using System.Collections.Concurrent;
using System.Diagnostics;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Tests;

/// <summary>
/// A load a scraping service refused for its limits (RemoteLoadException.RateLimited): loaded again after the service's Retry-After,
/// without spending an attempt, up to RateLimitRetries.
/// </summary>
[TestClass]
public sealed class RateLimitTests
{
    private const string Url = "https://s.test/p/1";

    /// <summary>A loader that throws what <c>refusal</c> gives for the n-th load (1, 2...), or gives the page; it keeps the time of every load.</summary>
    private sealed class Service(Func<int, RemoteLoadException?> refusal) : IPageLoader
    {
        private readonly ConcurrentQueue<long> _loads = new();

        public int Loads => _loads.Count;

        public Task<PageResponse> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken)
        {
            _loads.Enqueue(Stopwatch.GetTimestamp());
            return refusal(_loads.Count) is { } error
                ? Task.FromException<PageResponse>(error)
                : Task.FromResult(PageResponse.FromText(200, request.Url, ScraperTests.Html("ok")));
        }

        /// <summary>Milliseconds from each load to the next.</summary>
        public double[] Gaps()
        {
            var loads = _loads.ToArray();
            return [.. loads.Skip(1).Select((at, i) => Stopwatch.GetElapsedTime(loads[i], at).TotalMilliseconds)];
        }
    }

    private static RemoteLoadException Refusal(TimeSpan? retryAfter = null) => new("over the plan's limit") { RateLimited = true, RetryAfter = retryAfter };

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ARefusal_WaitsAsLongAsTheServiceAsks_AndTakesNoAttempt()
    {
        // 300 ms asked, then an hour, cut to MaxRetryDelay
        var service = new Service(n => n switch
        {
            1 => Refusal(TimeSpan.FromMilliseconds(300)),
            2 => Refusal(TimeSpan.FromHours(1)),
            _ => null,
        });
        var options = ScraperTests.FastOptions();      // no retry delay of its own
        options.MaxRetries = 0;                        // an attempt more would fail the page
        options.MaxRetryDelay = TimeSpan.FromMilliseconds(300);
        var scraper = new Scraper(service, options).Route("default", ctx => ctx.Emit(ctx.Request.Attempts));

        var result = await scraper.RunToListAsync(Url);

        CollectionAssert.AreEqual(new object[] { 1 }, result.Items.ToArray(), "the page came at the first attempt");
        var gaps = service.Gaps();
        Assert.HasCount(2, gaps);
        foreach (var gap in gaps) Assert.IsGreaterThanOrEqualTo(270, gap, "the service's time, up to MaxRetryDelay");
        var stats = result.Stats;
        Assert.AreEqual(3, stats.Loads);
        Assert.AreEqual(2, stats.Retries);
        Assert.AreEqual(2, stats.RateLimited);
        Assert.AreEqual(1, stats.Succeeded);
        Assert.AreEqual(0, stats.Failed);
        Assert.AreEqual(0, stats.Pending);
        CollectionAssert.AreEqual(new[]
        {
            ReasonTests.Group(ReasonKind.RateLimited, "over the plan's limit", "default", "s.test", 2, 0, "over the plan's limit", Url),
        }, stats.Reasons.ToArray());
        StringAssert.Contains(stats.ToString(), "2 retries, 0 blocked, 2 rate limited");
        StringAssert.Contains(stats.Reasons[0].ToString(), "2x rate limited: over the plan's limit (default, s.test)");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task RefusalsPastRateLimitRetries_AreAttempts_SoTheRunEnds()
    {
        var service = new Service(_ => Refusal());      // the service refuses every call
        var options = ScraperTests.FastOptions();      // MaxRetries 2: three attempts
        options.RateLimitRetries = 3;
        var scraper = new Scraper(service, options).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync(Url);

        Assert.IsEmpty(result.Items);
        Assert.AreEqual(3 + 3, service.Loads, "three refusals without an attempt, then three attempts");
        var failed = result.Failed.Single();
        Assert.AreEqual(FailureReason.RetriesExhausted, failed.Reason);
        Assert.AreEqual("3 attempts, 3 more loads refused for the service's limits, last: rate limited: over the plan's limit", failed.Message);
        Assert.IsTrue(failed.Exception is RemoteLoadException { RateLimited: true });
        var stats = result.Stats;
        Assert.AreEqual(6, stats.Loads);
        Assert.AreEqual(6, stats.RateLimited);
        Assert.AreEqual(5, stats.Retries);
        Assert.AreEqual(1, stats.Failed);
        Assert.AreEqual(0, stats.Pending);
        var reason = stats.Reasons.Single();
        Assert.AreEqual(ReasonKind.RateLimited, reason.Kind);
        Assert.AreEqual(6, reason.Count);
        Assert.AreEqual(1, reason.Failed);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task TheRetryAfterOfAServiceFailure_IsWaited_ButItIsAnAttempt()
    {
        var service = new Service(n => n == 1 ? new RemoteLoadException("the service failed") { RetryAfter = TimeSpan.FromMilliseconds(300) } : null);
        var scraper = new Scraper(service, ScraperTests.FastOptions()).Route("default", ctx => ctx.Emit(ctx.Request.Attempts));

        var result = await scraper.RunToListAsync(Url);

        CollectionAssert.AreEqual(new object[] { 2 }, result.Items.ToArray(), "the failure was the first attempt");
        Assert.IsGreaterThanOrEqualTo(270, service.Gaps().Single());
        Assert.AreEqual(0, result.Stats.RateLimited);
        Assert.AreEqual(ReasonKind.LoadError, result.Stats.Reasons.Single().Kind);
    }

    [TestMethod]
    public async Task ANegativeRateLimitRetries_Throws()
    {
        var options = ScraperTests.FastOptions();
        options.RateLimitRetries = -1;
        var scraper = new Scraper(new FakeLoader(), options).Route("default", _ => { });

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => scraper.RunToListAsync(Url));
    }
}
