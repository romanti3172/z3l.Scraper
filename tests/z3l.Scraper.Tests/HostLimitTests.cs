using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Tests;

/// <summary>Limits of each host: MaxConcurrencyPerHost and HostDelay, with the lines of the hosts that keep the other hosts going.</summary>
[TestClass]
public sealed class HostLimitTests
{
    /// <summary>Records when each load started and how many loads of each host went at once; each load takes <paramref name="loadMs"/>.</summary>
    private sealed class Site(int loadMs, Func<ScrapeRequest, Task>? before = null)
    {
        private readonly ConcurrentDictionary<string, int> _active = new();
        public readonly ConcurrentDictionary<string, int> MaxActive = new();
        public readonly ConcurrentQueue<(string Url, DateTime At)> Loads = new();
        private int _total;
        public int MaxTotal;

        public DelegateLoader Loader => new(LoadAsync);

        public async Task<PageResponse> LoadAsync(ScrapeRequest request, CancellationToken token)
        {
            var host = new Uri(request.Url).Host;
            Loads.Enqueue((request.Url, DateTime.UtcNow));
            var active = _active.AddOrUpdate(host, 1, (_, n) => n + 1);
            MaxActive.AddOrUpdate(host, active, (_, n) => Math.Max(n, active));
            var total = Interlocked.Increment(ref _total);
            lock (Loads) MaxTotal = Math.Max(MaxTotal, total);
            try
            {
                if (before != null) await before(request);
                if (loadMs > 0) await Task.Delay(loadMs, token);
                return PageResponse.FromText(200, request.Url, ScraperTests.Html("ok"));
            }
            finally
            {
                _active.AddOrUpdate(host, 0, (_, n) => n - 1);
                Interlocked.Decrement(ref _total);
            }
        }

        public DateTime[] LoadsOf(string host) => [.. Loads.Where(l => new Uri(l.Url).Host == host).Select(l => l.At).Order()];
    }

    private static IEnumerable<ScrapeRequest> Pages(string host, int count, ScrapePriority priority = ScrapePriority.Normal) =>
        Enumerable.Range(0, count).Select(i => new ScrapeRequest($"https://{host}/{i}") { Priority = priority });

    // ------------------------------------------------------------------ MaxConcurrencyPerHost

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task MaxConcurrencyPerHost_LimitsTheLoadsOfEachHost_NotOfTheRun()
    {
        var site = new Site(loadMs: 60);
        var options = ScraperTests.FastOptions();
        options.MaxConcurrency = 8;
        options.MaxConcurrencyPerHost = 2;
        var scraper = new Scraper(site.Loader, options).Route("default", _ => { });

        var result = await scraper.RunToListAsync(Pages("a.test", 10).Concat(Pages("b.test", 10)));

        Assert.AreEqual(20, result.Stats.Succeeded);
        Assert.AreEqual(2, site.MaxActive["a.test"]);
        Assert.AreEqual(2, site.MaxActive["b.test"]);
        Assert.IsGreaterThanOrEqualTo(3, site.MaxTotal, "the hosts load at the same time");
        Assert.AreEqual(0, result.Stats.Queued);
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task MaxConcurrencyPerHost_ABigSiteAtItsLimit_DoesNotHoldBackTheOthers()
    {
        var site = new Site(loadMs: 50);
        var options = ScraperTests.FastOptions();
        options.MaxConcurrency = 4;
        options.MaxConcurrencyPerHost = 1;
        var scraper = new Scraper(site.Loader, options).Route("default", _ => { });

        // the big site is queued first: its pages wait in its line, and the workers take the small site's meanwhile
        var result = await scraper.RunToListAsync(Pages("big.test", 30).Concat(Pages("small.test", 5)));

        Assert.AreEqual(35, result.Stats.Succeeded);
        var big = site.LoadsOf("big.test");
        var small = site.LoadsOf("small.test");
        Assert.IsLessThan(big[10], small[^1], "the small site was done while the big one had loaded only a few pages");
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task MaxConcurrencyPerHost_TheLineOfAHostKeepsThePriorities()
    {
        var order = new ConcurrentQueue<string>();
        var aStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var site = new Site(loadMs: 0, before: async r =>
        {
            if (r.Url.StartsWith("https://a.test/")) order.Enqueue(r.Url["https://a.test/".Length..]);
            if (r.Url != "https://a.test/0") return;
            aStarted.TrySetResult();
            await Task.Delay(300);          // the other pages of a.test wait in its line meanwhile
        });
        var options = ScraperTests.FastOptions();
        options.MaxConcurrencyPerHost = 1;
        // b.test is loaded while a.test/0 loads, and its page links to a.test: three pages, then one with a high priority
        var scraper = new Scraper(site.Loader, options).Route("default", async ctx =>
        {
            if (ctx.Request.Url != "https://b.test/0") return;
            await aStarted.Task;
            ctx.Enqueue(["https://a.test/1", "https://a.test/2", "https://a.test/3"]);
            ctx.Enqueue("https://a.test/high", priority: ScrapePriority.High);
        });

        var result = await scraper.RunToListAsync("https://a.test/0", "https://b.test/0");

        Assert.AreEqual(6, result.Stats.Succeeded);
        // the page of high priority came last and went first; the others keep the order they joined the line in, which the
        // workers that took them from the queue at the same moment may have mixed
        var loaded = order.ToArray();
        CollectionAssert.AreEqual(new[] { "0", "high" }, loaded[..2], string.Join(", ", loaded));
        CollectionAssert.AreEquivalent(new[] { "1", "2", "3" }, loaded[2..], string.Join(", ", loaded));
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task MaxConcurrencyPerHost_RequestsInAHostsLine_CountAsQueued()
    {
        Scraper? scraper = null;
        var sampled = 0;
        long queued = -1, inProgress = -1;
        var site = new Site(loadMs: 0, before: async _ =>
        {
            if (Interlocked.Exchange(ref sampled, 1) != 0) return;
            await Task.Delay(150);          // the others are in the line of the host by now
            queued = scraper!.Stats.Queued;
            inProgress = scraper.Stats.InProgress;
        });
        var options = ScraperTests.FastOptions();
        options.MaxConcurrencyPerHost = 1;
        scraper = new Scraper(site.Loader, options).Route("default", _ => { });

        var result = await scraper.RunToListAsync(Pages("a.test", 5));

        Assert.AreEqual(5, result.Stats.Succeeded);
        Assert.AreEqual(4, queued);
        Assert.AreEqual(1, inProgress);
        Assert.AreEqual(0, result.Stats.Queued);
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task MaxConcurrencyPerHost_ALoadThatThrows_GivesItsSlotBack()
    {
        var loader = new FakeLoader().Throws("https://a.test/0", new HttpRequestException("connection refused"));
        for (var i = 1; i < 4; i++) loader.Page($"https://a.test/{i}", ScraperTests.Html("ok"));
        var options = ScraperTests.FastOptions();
        options.MaxConcurrencyPerHost = 1;
        var scraper = new Scraper(loader, options).Route("default", _ => { });

        var result = await scraper.RunToListAsync(Pages("a.test", 4));

        Assert.AreEqual(3, result.Stats.Succeeded);
        Assert.AreEqual(FailureReason.RetriesExhausted, result.Failed.Single().Reason);
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task MaxConcurrencyPerHost_WithAQueueLoader_LimitsTheRequestsOfAHostInItsQueue()
    {
        var site = new Site(loadMs: 30);
        var loader = new QueueLoader(site.LoadAsync);
        var options = ScraperTests.FastOptions();
        options.MaxConcurrencyPerHost = 2;
        var scraper = new Scraper(loader, options).Route("default", _ => { });

        var result = await scraper.RunToListAsync(Pages("a.test", 10).Concat(Pages("b.test", 10)));

        Assert.AreEqual(20, result.Stats.Succeeded);
        Assert.AreEqual(2, site.MaxActive["a.test"]);
        Assert.AreEqual(2, site.MaxActive["b.test"]);
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task MaxConcurrencyPerHost_WithAutoThrottle_TheOverloadedHostIsSpaced_AndFinishes()
    {
        var first = 0;
        var site = new Site(loadMs: 20);
        var loader = new DelegateLoader(async (r, token) =>
        {
            var response = await site.LoadAsync(r, token);
            return r.Url.StartsWith("https://a.test/") && Interlocked.Exchange(ref first, 1) == 0
                ? PageResponse.FromText(429, r.Url, "slow down")
                : response;
        });
        var options = ScraperTests.FastOptions();
        options.AutoThrottle = true;
        options.MaxConcurrencyPerHost = 3;
        options.MaxThrottleDelay = TimeSpan.FromMilliseconds(200);
        var scraper = new Scraper(loader, options).Route("default", ctx =>
        {
            if (ctx.Request.Depth == 0) ctx.Enqueue(Enumerable.Range(1, 6).Select(i => $"https://a.test/{i}"));
        });

        var result = await scraper.RunToListAsync("https://a.test/0");

        Assert.AreEqual(7, result.Stats.Succeeded);
        var a = site.LoadsOf("a.test");
        Assert.HasCount(8, a);          // 7 pages and the retry of the 429
        // after the 429 the loads of a.test are spaced (the delay shrinks by 10 % per success), not three at a time
        for (var i = 2; i < a.Length; i++)
        {
            Assert.IsGreaterThanOrEqualTo(100, (a[i] - a[i - 1]).TotalMilliseconds, $"a.test load {i} came too soon");
        }
    }

    // ------------------------------------------------------------------ HostDelay

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task HostDelay_SpacesTheLoadsOfEachHost_EachHostByItself()
    {
        var site = new Site(loadMs: 0);
        var options = ScraperTests.FastOptions();
        options.HostDelay = TimeSpan.FromMilliseconds(100);
        var scraper = new Scraper(site.Loader, options).Route("default", _ => { });

        var result = await scraper.RunToListAsync(Pages("a.test", 4).Concat(Pages("b.test", 4)));

        Assert.AreEqual(8, result.Stats.Succeeded);
        var a = site.LoadsOf("a.test");
        var b = site.LoadsOf("b.test");
        for (var i = 1; i < 4; i++)
        {
            Assert.IsGreaterThanOrEqualTo(90, (a[i] - a[i - 1]).TotalMilliseconds, $"a.test load {i} came too soon");
            Assert.IsGreaterThanOrEqualTo(90, (b[i] - b[i - 1]).TotalMilliseconds, $"b.test load {i} came too soon");
        }
        Assert.IsLessThan(80, Math.Abs((b[0] - a[0]).TotalMilliseconds), "b.test does not wait for the delay of a.test");
    }

    [TestMethod]
    [DataRow(250, "0.05")]      // HostDelay is longer than Crawl-delay
    [DataRow(50, "0.25")]       // Crawl-delay is longer than HostDelay
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task HostDelay_AndCrawlDelay_TheLongerWins(int hostDelayMs, string crawlDelay)
    {
        var loads = new ConcurrentQueue<DateTime>();
        var loader = new DelegateLoader((r, _) =>
        {
            if (r.Url.EndsWith("/robots.txt")) return Task.FromResult(PageResponse.FromText(200, r.Url, $"User-agent: *\nCrawl-delay: {crawlDelay}", "text/plain"));
            loads.Enqueue(DateTime.UtcNow);
            return Task.FromResult(PageResponse.FromText(200, r.Url, ScraperTests.Html("ok")));
        });
        var options = ScraperTests.FastOptions();
        options.RespectRobotsTxt = true;
        options.HostDelay = TimeSpan.FromMilliseconds(hostDelayMs);
        options.MaxThrottleDelay = TimeSpan.FromSeconds(1);
        var scraper = new Scraper(loader, options).Route("default", _ => { });

        var result = await scraper.RunToListAsync(Pages("s.test", 3));

        Assert.AreEqual(3, result.Stats.Succeeded);
        var times = loads.Order().ToArray();
        for (var i = 1; i < times.Length; i++) Assert.IsGreaterThanOrEqualTo(235, (times[i] - times[i - 1]).TotalMilliseconds, $"load {i} came too soon");
    }

    [TestMethod]
    public void HostDelay_IsARandomZeroToFiftyPercentLonger()
    {
        var delay = TimeSpan.FromMilliseconds(100);
        var waits = Enumerable.Range(0, 200).Select(_ => HostThrottle.Jittered(delay)).ToArray();

        Assert.IsTrue(waits.All(w => w >= delay && w <= delay * 1.5), string.Join(", ", waits.Select(w => w.TotalMilliseconds)));
        Assert.IsGreaterThan(50, waits.Distinct().Count());
        Assert.AreEqual(TimeSpan.Zero, HostThrottle.Jittered(TimeSpan.Zero));
    }

    [TestMethod]
    public async Task HostLines_OfIdleHostsAreForgotten_SoACrawlOfManyHostsDoesNotKeepThem()
    {
        var options = new ScraperOptions { HostDelay = TimeSpan.FromMilliseconds(1) };
        var throttle = new HostThrottle(options, new ScraperStats(), _ => { }, NullLogger.Instance, CancellationToken.None);
        for (var i = 0; i < 1500; i++) Assert.IsTrue(throttle.Enter($"h{i}.test", new ScrapeRequest($"https://h{i}.test/")));
        await Task.Delay(50);       // their slots have passed

        for (var i = 1500; i < 2500; i++) Assert.IsTrue(throttle.Enter($"h{i}.test", new ScrapeRequest($"https://h{i}.test/")));

        Assert.IsLessThan(1500, throttle.HostCount);
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(null, -1)]
    [DataRow(null, 2 * 24 * 60 * 60 * 1000)]
    public async Task Options_AreChecked(int? maxConcurrencyPerHost, int hostDelayMs)
    {
        var options = ScraperTests.FastOptions();
        options.MaxConcurrencyPerHost = maxConcurrencyPerHost;
        options.HostDelay = TimeSpan.FromMilliseconds(hostDelayMs);
        var scraper = new Scraper(new FakeLoader(), options).Route("default", _ => { });

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => scraper.RunToListAsync("https://s.test/"));
    }

    /// <summary>A queue loader made of a function: the scraper sends it every request at once.</summary>
    private sealed class QueueLoader(Func<ScrapeRequest, CancellationToken, Task<PageResponse>> load) : IQueuePageLoader
    {
        public bool Paused { get; set; }

        public Task<PageResponse> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken) => load(request, cancellationToken);
    }
}
