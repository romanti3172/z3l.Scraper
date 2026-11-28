using System.Collections.Concurrent;
using z3l.FastHtmlParser;
using System.Text;
using RabbitMQ.Client;
using z3l.Scraper.Loading;

namespace z3l.Scraper.RabbitMQ.Tests;

/// <summary>
/// The scraper, its loader and workers through a real broker. Set Z3L_TEST_RABBITMQ=amqp://user:password@host:port/;
/// without it these tests are inconclusive. Every test uses queues of its own and deletes them.
/// </summary>
[TestClass]
public sealed class RabbitMqTests
{
    public TestContext TestContext { get; set; } = null!;

    private static Uri Broker()
    {
        var uri = Environment.GetEnvironmentVariable("Z3L_TEST_RABBITMQ");
        if (string.IsNullOrEmpty(uri)) Assert.Inconclusive("Set Z3L_TEST_RABBITMQ=amqp://user:password@host:port/ to test with RabbitMQ");
        return new Uri(uri!);
    }

    private static string Name(string what) => $"z3l.test.{Guid.NewGuid():N}.{what}";

    private static ScraperOptions Options() => new()
    {
        RetryDelay = TimeSpan.Zero,
        ProgressInterval = null,
        MaxRetries = 3,
    };

    private static RabbitMqPageLoaderOptions LoaderOptions(Uri broker, TimeSpan executionTimeout) => new()
    {
        Uri = broker,
        ExecutionTimeout = executionTimeout,
        TimesyncInterval = TimeSpan.FromMilliseconds(200),
        CheckInterval = TimeSpan.FromMilliseconds(200),
    };

    private static string Html(string body) => $"<html><body>{body}</body></html>";

    private static PageResponse Page(string url, string body) => PageResponse.FromText(200, url, Html(body), "text/html");

    private sealed class Worker : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        public RabbitMqLoaderWorker Inner { get; }
        public Task Running { get; }

        public Worker(Uri broker, string queue, string id, IPageLoader loader, Action<RabbitMqLoaderWorkerOptions>? configure = null)
        {
            var options = new RabbitMqLoaderWorkerOptions { Uri = broker, Queue = queue, WorkerId = id, Concurrency = 4 };
            configure?.Invoke(options);
            Inner = new RabbitMqLoaderWorker(loader, options);
            Running = Task.Run(() => Inner.RunAsync(_stop.Token));
        }

        public async Task StopAsync()
        {
            await _stop.CancelAsync();
            await Running;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_stop.IsCancellationRequested) await StopAsync();
            await Inner.DisposeAsync();
            _stop.Dispose();
        }
    }

    private sealed class FuncLoader(Func<ScrapeRequest, CancellationToken, Task<PageResponse>> load) : IPageLoader
    {
        public Task<PageResponse> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken) => load(request, cancellationToken);
    }

    private sealed class BlockAwareLoader(Func<ScrapeRequest, PageResponse> load) : IBlockAwarePageLoader
    {
        public int Blocked, Good;
        public Task<PageResponse> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken) => Task.FromResult(load(request));
        public void ReportBlocked(ScrapeRequest request, PageResponse response, string reason) => Interlocked.Increment(ref Blocked);
        public void ReportGoodResponse(ScrapeRequest request, PageResponse response) => Interlocked.Increment(ref Good);
    }

    private static async Task DeleteQueuesAsync(Uri broker, params string[] queues)
    {
        await using var connection = await new ConnectionFactory { Uri = broker }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        foreach (var queue in queues)
        {
            try { await channel.QueueDeleteAsync(queue); }
            catch { /* not there */ }
        }
    }

    // ------------------------------------------------------------------ tests

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task LabelsGoToTheirQueues_TheWorkersLoadAndTheScraperHandles()
    {
        var broker = Broker();
        string http = Name("http"), browser = Name("browser");
        try
        {
            await using var httpWorker = new Worker(broker, http, "http", new FuncLoader((r, _) =>
                // a binary file: text instead of a file is retried (CheckFileContent)
                Task.FromResult(new PageResponse(200, r.Url, [0, .. Encoding.ASCII.GetBytes("file " + r.Url)], "application/octet-stream"))));
            await using var browserWorker = new Worker(broker, browser, "browser", new FuncLoader((r, _) => Task.FromResult(r.Url.EndsWith("/list")
                ? Page(r.Url, string.Concat(Enumerable.Range(0, 100).Select(i => $"<a class='p' href='/p/{i}'>p</a>"))
                              + string.Concat(Enumerable.Range(0, 10).Select(i => $"<a class='f' href='/f/{i}'>f</a>")))
                : Page(r.Url, $"<h1>{r.Url}</h1>"))));

            await using var loader = new RabbitMqPageLoader(LoaderOptions(broker, TimeSpan.FromMinutes(1))).Queue(http, PageType.File).Queue(browser);
            var scraper = new Scraper(loader, Options())
                .Route("list", ctx =>
                {
                    ctx.Enqueue(ctx.Html.XPathValues("//a[@class='p']/@href"), "product");
                    ctx.Enqueue(ctx.Html.XPathValues("//a[@class='f']/@href"), PageType.File);
                })
                .Route("product", ctx => ctx.Emit($"product {ctx.Response.Headers[RabbitMqPageLoader.WorkerHeader]}"))
                .Route(PageType.File, ctx => ctx.Emit($"file {ctx.Response.Headers[RabbitMqPageLoader.WorkerHeader]}"));

            var result = await scraper.RunToListAsync([new ScrapeRequest("https://s.test/list", "list")], TestContext.CancellationToken);

            Assert.AreEqual(100, result.Items.Count(x => (string)x == "product browser"));
            Assert.AreEqual(10, result.Items.Count(x => (string)x == "file http"));
            Assert.AreEqual(111, result.Stats.Succeeded);
            Assert.IsEmpty(result.Failed);
        }
        finally
        {
            await DeleteQueuesAsync(broker, http, browser);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ALoadThatWaitsInTheQueueLongerThanTheTimeout_IsNotLost()
    {
        var broker = Broker();
        var queue = Name("tasks");
        try
        {
            await using var loader = new RabbitMqPageLoader(LoaderOptions(broker, TimeSpan.FromSeconds(1))).Queue(queue);
            var scraper = new Scraper(loader, Options()).Route("default", ctx => ctx.Emit(ctx.Request.Url));

            var run = scraper.RunToListAsync(Enumerable.Range(0, 20).Select(i => new ScrapeRequest($"https://s.test/{i}")), TestContext.CancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.CancellationToken);     // no worker: three timeouts in the queue

            await using var worker = new Worker(broker, queue, "late", new FuncLoader((r, _) => Task.FromResult(Page(r.Url, "ok"))));
            var result = await run;

            Assert.AreEqual(20, result.Stats.Succeeded);
            Assert.AreEqual(0, result.Stats.Retries);
            StringAssert.Contains(loader.ToString(), "lost 0");
        }
        finally
        {
            await DeleteQueuesAsync(broker, queue);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AWorkerThatHangs_ItsTaskIsLostByTimesync_AndSentAgain()
    {
        var broker = Broker();
        var queue = Name("tasks");
        try
        {
            var calls = new ConcurrentDictionary<string, int>();
            await using var worker = new Worker(broker, queue, "w", new FuncLoader(async (r, token) =>
            {
                if (r.Url.EndsWith("/hang") && calls.AddOrUpdate(r.Url, 1, (_, n) => n + 1) == 1)
                {
                    await Task.Delay(Timeout.Infinite, token);     // took the task and hung
                }
                return Page(r.Url, "ok");
            }), o => o.LoadTimeout = Timeout.InfiniteTimeSpan);

            await using var loader = new RabbitMqPageLoader(LoaderOptions(broker, TimeSpan.FromSeconds(1))).Queue(queue);
            var scraper = new Scraper(loader, Options()).Route("default", ctx => ctx.Emit(ctx.Request.Url));

            var result = await scraper.RunToListAsync(
                ["https://s.test/1", "https://s.test/hang", "https://s.test/2"], TestContext.CancellationToken);

            Assert.AreEqual(3, result.Stats.Succeeded);
            Assert.AreEqual(1, result.Stats.Retries);
            StringAssert.Contains(loader.ToString(), "lost 1");
        }
        finally
        {
            await DeleteQueuesAsync(broker, queue);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AWorkerThatStops_ItsTasksGoToAnotherWorker()
    {
        var broker = Broker();
        var queue = Name("tasks");
        try
        {
            var taken = 0;
            var dying = new Worker(broker, queue, "dying", new FuncLoader(async (_, token) =>
            {
                Interlocked.Increment(ref taken);
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("never");
            }));

            await using var loader = new RabbitMqPageLoader(LoaderOptions(broker, TimeSpan.FromMinutes(5))).Queue(queue);
            var scraper = new Scraper(loader, Options()).Route("default", ctx => ctx.Emit(ctx.Request.Url));
            var run = scraper.RunToListAsync(Enumerable.Range(0, 4).Select(i => new ScrapeRequest($"https://s.test/{i}")), TestContext.CancellationToken);

            for (var i = 0; i < 200 && Volatile.Read(ref taken) < 4; i++) await Task.Delay(50, TestContext.CancellationToken);
            Assert.AreEqual(4, Volatile.Read(ref taken), "the first worker took all 4");
            await dying.DisposeAsync();     // without acknowledging them

            await using var healthy = new Worker(broker, queue, "healthy", new FuncLoader((r, _) => Task.FromResult(Page(r.Url, "ok"))));
            var result = await run;

            Assert.AreEqual(4, result.Stats.Succeeded);
            Assert.AreEqual(0, result.Stats.Retries, "the broker gave them again, nothing was lost");
        }
        finally
        {
            await DeleteQueuesAsync(broker, queue);
        }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ThousandsOfPagesAtOnce()
    {
        var broker = Broker();
        var queue = Name("tasks");
        try
        {
            await using var w1 = new Worker(broker, queue, "w1", new FuncLoader((r, _) => Task.FromResult(Page(r.Url, "ok"))), o => o.Concurrency = 32);
            await using var w2 = new Worker(broker, queue, "w2", new FuncLoader((r, _) => Task.FromResult(Page(r.Url, "ok"))), o => o.Concurrency = 32);
            await using var loader = new RabbitMqPageLoader(LoaderOptions(broker, TimeSpan.FromMinutes(1))).Queue(queue);
            var scraper = new Scraper(loader, Options()).Route("default", ctx => ctx.Emit(ctx.Request.Url));

            var result = await scraper.RunToListAsync(Enumerable.Range(0, 3000).Select(i => new ScrapeRequest($"https://s.test/{i}")), TestContext.CancellationToken);

            Assert.AreEqual(3000, result.Stats.Succeeded);
            Assert.IsEmpty(result.Failed);
            TestContext.WriteLine($"{result.Stats}; {loader}; {w1.Inner}; {w2.Inner}");
        }
        finally
        {
            await DeleteQueuesAsync(broker, queue);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SlowHandlers_PauseTheLoader_AndThePauseDoesNotLoseTasks()
    {
        var broker = Broker();
        var queue = Name("tasks");
        try
        {
            await using var worker = new Worker(broker, queue, "w", new FuncLoader((r, _) => Task.FromResult(Page(r.Url, "ok"))));
            await using var loader = new RabbitMqPageLoader(LoaderOptions(broker, TimeSpan.FromSeconds(1))).Queue(queue);
            var options = Options();
            options.MaxConcurrency = 1;
            options.MaxLoadedNotHandled = 2;
            var wasPaused = false;
            var scraper = new Scraper(loader, options).Route("default", async ctx =>
            {
                wasPaused |= loader.Paused;
                await Task.Delay(150, ctx.CancellationToken);     // 20 pages: 3 s of handlers, three ExecutionTimeouts
                ctx.Emit(ctx.Request.Url);
            });

            var result = await scraper.RunToListAsync(Enumerable.Range(0, 20).Select(i => new ScrapeRequest($"https://s.test/{i}")), TestContext.CancellationToken);

            Assert.AreEqual(20, result.Stats.Succeeded);
            Assert.AreEqual(0, result.Stats.Retries);
            Assert.IsTrue(wasPaused, "the loader was paused while pages waited for the handler");
            StringAssert.Contains(loader.ToString(), "lost 0");
        }
        finally
        {
            await DeleteQueuesAsync(broker, queue);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnAnswerTooLargeForTheBroker_FailsAtOnce()
    {
        var broker = Broker();
        var queue = Name("tasks");
        try
        {
            var noise = new byte[64 * 1024];
            Random.Shared.NextBytes(noise);     // does not compress
            await using var worker = new Worker(broker, queue, "w", new FuncLoader((r, _) => Task.FromResult(new PageResponse(200, r.Url, noise, "application/octet-stream"))),
                o => o.MaxMessageSize = 16 * 1024);
            await using var loader = new RabbitMqPageLoader(LoaderOptions(broker, TimeSpan.FromMinutes(1))).Queue(queue);
            var scraper = new Scraper(loader, Options()).Route(PageType.File, ctx => ctx.Emit(ctx.Request.Url));

            var result = await scraper.RunToListAsync([new ScrapeRequest("https://s.test/big.bin", PageType.File)], TestContext.CancellationToken);

            Assert.HasCount(1, result.Failed);
            Assert.AreEqual(FailureReason.NonRetryable, result.Failed[0].Reason);
            Assert.AreEqual(1, result.Failed[0].Request.Attempts);
        }
        finally
        {
            await DeleteQueuesAsync(broker, queue);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheWorkerChecksPagesForItsLoader_ABlockPageRestsTheProxy()
    {
        var broker = Broker();
        var queue = Name("tasks");
        try
        {
            var calls = 0;
            var proxied = new BlockAwareLoader(r => Interlocked.Increment(ref calls) == 1 ? Page(r.Url, "captcha") : Page(r.Url, "ok"));
            var rules = new Validation.ResponseRulesCollection();
            rules.Default.BlockedMarkers.Add("captcha");
            await using var worker = new Worker(broker, queue, "w", proxied, o => o.Rules = rules);

            await using var loader = new RabbitMqPageLoader(LoaderOptions(broker, TimeSpan.FromMinutes(1))).Queue(queue);
            var options = Options();
            options.Rules.Default.BlockedMarkers.Add("captcha");
            var scraper = new Scraper(loader, options).Route("default", ctx => ctx.Emit(ctx.Request.Url));

            var result = await scraper.RunToListAsync([new ScrapeRequest("https://s.test/1")], TestContext.CancellationToken);

            Assert.AreEqual(1, result.Stats.Succeeded);
            Assert.AreEqual(1, result.Stats.Blocked, "the scraper saw the block too and retried");
            Assert.AreEqual(1, proxied.Blocked, "the worker told its loader");
            Assert.AreEqual(1, proxied.Good);
        }
        finally
        {
            await DeleteQueuesAsync(broker, queue);
        }
    }
}
