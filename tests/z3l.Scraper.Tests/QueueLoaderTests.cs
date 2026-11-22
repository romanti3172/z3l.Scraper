using System.Collections.Concurrent;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Tests;

/// <summary>A loader that hands loads to a queue (IQueuePageLoader): the scraper sends everything at once and pauses it.</summary>
[TestClass]
public sealed class QueueLoaderTests
{
    /// <summary>Loads wait until the test answers them; Paused is recorded.</summary>
    private sealed class ManualQueueLoader : IQueuePageLoader
    {
        public readonly ConcurrentDictionary<string, TaskCompletionSource<PageResponse>> Waiting = new();
        public readonly List<bool> PausedChanges = [];
        private bool _paused;

        public bool Paused
        {
            get => _paused;
            set
            {
                _paused = value;
                lock (PausedChanges) PausedChanges.Add(value);
            }
        }

        public Task<PageResponse> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken)
        {
            var load = new TaskCompletionSource<PageResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => load.TrySetCanceled(cancellationToken));
            Waiting[request.Url] = load;
            return load.Task;
        }

        public void AnswerAll(Func<string, string> page)
        {
            foreach (var (url, load) in Waiting)
            {
                if (Waiting.TryRemove(url, out _)) load.TrySetResult(PageResponse.FromText(200, url, page(url), "text/html"));
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.IsTrue(condition(), what);
    }

    private static IEnumerable<ScrapeRequest> Pages(int count) => Enumerable.Range(0, count).Select(i => new ScrapeRequest($"https://q.test/{i}"));

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task EveryRequestGoesToTheQueueAtOnce_NotMaxConcurrency()
    {
        var loader = new ManualQueueLoader();
        var options = ScraperTests.FastOptions();
        options.MaxConcurrency = 2;
        var scraper = new Scraper(loader, options).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var run = scraper.RunToListAsync(Pages(50));
        await WaitUntilAsync(() => loader.Waiting.Count == 50, "all 50 loads are in the queue though MaxConcurrency is 2");

        loader.AnswerAll(_ => ScraperTests.Html("ok"));
        var result = await run;

        Assert.HasCount(50, result.Items);
        Assert.AreEqual(50, result.Stats.Succeeded);
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task RequestTimeoutDoesNotCount_TheLoadMayWaitInTheQueue()
    {
        var loader = new ManualQueueLoader();
        var options = ScraperTests.FastOptions();
        options.RequestTimeout = TimeSpan.FromMilliseconds(100);
        var scraper = new Scraper(loader, options).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var run = scraper.RunToListAsync(Pages(3));
        await WaitUntilAsync(() => loader.Waiting.Count == 3, "loads sent");
        await Task.Delay(500);      // five times RequestTimeout in the queue
        loader.AnswerAll(_ => ScraperTests.Html("ok"));
        var result = await run;

        Assert.AreEqual(3, result.Stats.Succeeded);
        Assert.AreEqual(0, result.Stats.Retries);
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task MaxConcurrencyHandlers_TooManyWaitingPausesTheLoader()
    {
        var loader = new ManualQueueLoader();
        var options = ScraperTests.FastOptions();
        options.MaxConcurrency = 2;
        options.MaxLoadedNotHandled = 4;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0, mostAtOnce = 0;
        var scraper = new Scraper(loader, options).Route("default", async ctx =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref mostAtOnce, now);
            await gate.Task;
            Interlocked.Decrement(ref running);
            ctx.Emit(ctx.Request.Url);
        });

        var run = scraper.RunToListAsync(Pages(20));
        await WaitUntilAsync(() => loader.Waiting.Count == 20, "loads sent");
        loader.AnswerAll(_ => ScraperTests.Html("ok"));

        // 2 in handlers, 18 waiting > 4: paused. The gate opens once both handlers are in: a page may have its slot and not be in its
        // handler yet when the loader is paused, and then it would come in after the first handler is done
        await WaitUntilAsync(() => loader.Paused, "the loader is paused while pages wait for handlers");
        await WaitUntilAsync(() => Volatile.Read(ref running) >= 2, "MaxConcurrency handlers at once");
        gate.SetResult();
        var result = await run;

        Assert.AreEqual(20, result.Stats.Succeeded);
        Assert.AreEqual(2, mostAtOnce, "MaxConcurrency handlers at once");
        Assert.IsFalse(loader.Paused, "resumed when the handlers caught up");
        lock (loader.PausedChanges) CollectionAssert.AreEqual(new[] { true, false }, loader.PausedChanges, "paused once, resumed once");
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task ALostLoadThrows_AndIsRetried()
    {
        var calls = new ConcurrentDictionary<string, int>();
        var loader = new QueueDelegateLoader((request, _) =>
            calls.AddOrUpdate(request.Url, 1, (_, n) => n + 1) == 1
                ? Task.FromException<PageResponse>(new TimeoutException("lost: no answer"))
                : Task.FromResult(PageResponse.FromText(200, request.Url, ScraperTests.Html("ok"), "text/html")));
        var scraper = new Scraper(loader, ScraperTests.FastOptions()).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync(Pages(5));

        Assert.AreEqual(5, result.Stats.Succeeded);
        Assert.AreEqual(5, result.Stats.Retries);
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task StoppingTheRun_CancelsTheLoadsInTheQueue()
    {
        var loader = new ManualQueueLoader();
        var scraper = new Scraper(loader, ScraperTests.FastOptions()).Route("default", _ => { });
        using var stop = new CancellationTokenSource();

        var run = scraper.RunToListAsync(Pages(10), stop.Token);
        await WaitUntilAsync(() => loader.Waiting.Count == 10, "loads sent");
        await stop.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => run);

        Assert.IsTrue(loader.Waiting.Values.All(x => x.Task.IsCanceled), "every load got the cancellation");
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task PagesThroughTheQueue_FilesWithALocalLoader_EachWithItsOwnRules()
    {
        const string Hung = "https://q.test/img/3.jpg";
        var queue = new ManualQueueLoader();
        var outOfTime = new ConcurrentQueue<string>();
        int filesAtOnce = 0, mostFilesAtOnce = 0, hungLoads = 0;
        var files = new LocalBlockAwareLoader(async (request, token) =>
        {
            InterlockedMax(ref mostFilesAtOnce, Interlocked.Increment(ref filesAtOnce));
            try
            {
                // the first load of one file hangs: RequestTimeout applies to the local loader
                if (request.Url == Hung && Interlocked.Increment(ref hungLoads) == 1) await Task.Delay(Timeout.Infinite, token);
                await Task.Delay(20, token);
                return new PageResponse(200, request.Url, [1, 2, 3], "image/jpeg");
            }
            catch (OperationCanceledException)
            {
                outOfTime.Enqueue(request.Url);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref filesAtOnce);
            }
        });
        var options = ScraperTests.FastOptions();
        options.MaxConcurrency = 2;
        options.RequestTimeout = TimeSpan.FromSeconds(1);      // the hung file waits it out; the others get slack for a busy machine
        var scraper = new Scraper(queue, options)
            .UseLoader(files, PageType.File)
            .Route(PageType.Listing, ctx => ctx.Enqueue(Enumerable.Range(0, 10).Select(i => $"/img/{i}.jpg"), PageType.File))
            .Route(PageType.File, ctx => ctx.Emit(ctx.Request.Url));

        var run = scraper.RunToListAsync(Enumerable.Range(0, 20).Select(i => new ScrapeRequest($"https://q.test/list/{i}", PageType.Listing)));
        await WaitUntilAsync(() => queue.Waiting.Count == 20, "all listings go to the queue at once");
        queue.AnswerAll(_ => ScraperTests.Html("ok"));
        var result = await run;

        Assert.HasCount(10, result.Items, "10 distinct files");
        Assert.AreEqual(30, result.Stats.Succeeded);
        Assert.IsLessThanOrEqualTo(2, mostFilesAtOnce, "files: MaxConcurrency loads at once");
        // the hung load always runs out of time; a 20 ms one may too on a busy machine, so the timeouts are matched to the loads, not counted
        CollectionAssert.Contains(files.TimedOut.ToArray(), Hung, "the hung file timed out and was retried, reported to its own loader");
        CollectionAssert.AreEquivalent(outOfTime.ToArray(), files.TimedOut.ToArray(), "every load out of time is reported once");
    }

    private sealed class LocalBlockAwareLoader(Func<ScrapeRequest, CancellationToken, Task<PageResponse>> load) : IBlockAwarePageLoader
    {
        public readonly ConcurrentQueue<string> TimedOut = new();
        public Task<PageResponse> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken) => load(request, cancellationToken);
        public void ReportBlocked(ScrapeRequest request, PageResponse response, string reason) { }
        public void ReportTimeout(ScrapeRequest request) => TimedOut.Enqueue(request.Url);
    }

    private sealed class QueueDelegateLoader(Func<ScrapeRequest, CancellationToken, Task<PageResponse>> load) : IQueuePageLoader
    {
        public bool Paused { get; set; }
        public Task<PageResponse> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken) => load(request, cancellationToken);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}
