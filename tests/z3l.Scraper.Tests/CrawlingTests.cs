using System.Collections.Concurrent;
using System.Diagnostics;
using z3l.FastHtmlParser;

namespace z3l.Scraper.Tests;

/// <summary>Depth, robots.txt and throttling inside real runs.</summary>
[TestClass]
public sealed class CrawlingTests
{
    private static int PageNumber(string url) => int.Parse(url[(url.LastIndexOf('/') + 1)..]);

    // ------------------------------------------------------------------ depth

    [TestMethod]
    public async Task Depth_IsCounted_AndMaxDepthStopsTheChain()
    {
        // /0 -> /1 -> /2 -> ... forever
        var loader = new DelegateLoader((r, _) => Task.FromResult(PageResponse.FromText(200, r.Url, ScraperTests.Html($"<a href='/{PageNumber(r.Url) + 1}'>next</a>"))));
        var options = ScraperTests.FastOptions();
        options.MaxDepth = 2;
        var scraper = new Scraper(loader, options).Route("default", ctx =>
        {
            ctx.Enqueue(ctx.Html.XPathValue("//a/@href"));
            ctx.Emit((PageNumber(ctx.Request.Url), ctx.Request.Depth));
        });

        var result = await scraper.RunToListAsync("https://s.test/0");

        CollectionAssert.AreEqual(new[] { (0, 0), (1, 1), (2, 2) }, result.ItemsOf<(int Page, int Depth)>().OrderBy(i => i.Page).ToArray());
        Assert.AreEqual(1, result.Stats.SkippedByLimit);
    }

    [TestMethod]
    public async Task MaxDepth_APageFoundTooDeepIsTakenWhenFoundCloser()
    {
        // s1 -> m -> c (depth 2, over the limit); s2 -> c (depth 1): c must be scraped
        var links = new Dictionary<string, string>
        {
            ["https://s.test/s1"] = "<a href='/m'>m</a>",
            ["https://s.test/m"] = "<a href='/c'>c</a>",
            ["https://s.test/s2"] = "<a href='/c'>c</a>",
        };
        var loader = new DelegateLoader((r, _) => Task.FromResult(PageResponse.FromText(200, r.Url, ScraperTests.Html(links.GetValueOrDefault(r.Url) ?? ""))));
        var options = ScraperTests.FastOptions();
        options.MaxDepth = 1;
        options.MaxConcurrency = 1;
        var scraper = new Scraper(loader, options).Route("default", ctx =>
        {
            ctx.Enqueue(ctx.Html.XPathValues("//a/@href"));
            ctx.Emit($"{ctx.Request.Url["https://s.test".Length..]}@{ctx.Request.Depth}");
        });

        var result = await scraper.RunToListAsync(
            new ScrapeRequest("https://s.test/s1") { Priority = ScrapePriority.High },
            new ScrapeRequest("https://s.test/s2") { Priority = ScrapePriority.Low });

        CollectionAssert.AreEquivalent(new[] { "/s1@0", "/m@1", "/s2@0", "/c@1" }, result.Items.ToArray());
        Assert.AreEqual(1, result.Stats.SkippedByLimit);
    }

    // ------------------------------------------------------------------ robots.txt

    private static DelegateLoader RobotsSite(string? robots, ConcurrentQueue<string> loads, int robotsStatus = 200) =>
        new((r, _) =>
        {
            loads.Enqueue(r.Url);
            return Task.FromResult(r.Url.EndsWith("/robots.txt")
                ? PageResponse.FromText(robots == null ? 404 : robotsStatus, r.Url, robots ?? "not found", "text/plain")
                : PageResponse.FromText(200, r.Url, ScraperTests.Html("page")));
        });

    [TestMethod]
    public async Task RobotsTxt_DisallowedPagesFail_WithoutBeingLoaded()
    {
        var loads = new ConcurrentQueue<string>();
        var options = ScraperTests.FastOptions();
        options.RespectRobotsTxt = true;
        var scraper = new Scraper(RobotsSite("User-agent: *\nDisallow: /private", loads), options).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync("https://s.test/a", "https://s.test/private/1", "https://s.test/b", "https://other.test/private/1");

        CollectionAssert.AreEquivalent(new[] { "https://s.test/a", "https://s.test/b" }, result.Items.ToArray());
        Assert.HasCount(2, result.Failed);
        Assert.IsTrue(result.Failed.All(f => f.Reason == FailureReason.DisallowedByRobotsTxt));
        Assert.IsFalse(loads.Any(u => u.Contains("/private/")), "a disallowed page was loaded");
        Assert.AreEqual(1, loads.Count(u => u == "https://s.test/robots.txt"));        // once per host
        Assert.AreEqual(1, loads.Count(u => u == "https://other.test/robots.txt"));
        Assert.AreEqual(2, result.Stats.Loads);                                          // robots.txt loads are not counted
    }

    [TestMethod]
    [DataRow(null, 0)]          // 404: no rules
    [DataRow("x", 500)]         // server error: crawled without rules (logged)
    public async Task RobotsTxt_MissingOrBroken_AllowsAll(string? robots, int status)
    {
        var loads = new ConcurrentQueue<string>();
        var options = ScraperTests.FastOptions();
        options.RespectRobotsTxt = true;
        var scraper = new Scraper(RobotsSite(robots, loads, status), options).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync("https://s.test/private/1");

        Assert.HasCount(1, result.Items);
    }

    [TestMethod]
    public async Task RobotsTxt_NamedGroupForOurToken()
    {
        var loads = new ConcurrentQueue<string>();
        var options = ScraperTests.FastOptions();
        options.RespectRobotsTxt = true;
        options.RobotsUserAgent = "shop-bot";
        const string robots = "User-agent: *\nDisallow: /\n\nUser-agent: shop-bot\nDisallow: /cart";
        var scraper = new Scraper(RobotsSite(robots, loads), options).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync("https://s.test/p/1", "https://s.test/cart");

        CollectionAssert.AreEqual(new[] { "https://s.test/p/1" }, result.Items.ToArray());
    }

    [TestMethod]
    public async Task RobotsTxt_CrawlDelay_SpacesTheHost()
    {
        var loads = new ConcurrentQueue<string>();
        var options = ScraperTests.FastOptions();
        options.RespectRobotsTxt = true;
        options.MaxConcurrency = 4;
        options.MaxThrottleDelay = TimeSpan.FromSeconds(1);
        var scraper = new Scraper(RobotsSite("User-agent: *\nCrawl-delay: 0.2", loads), options).Route("default", _ => { });

        var result = await scraper.RunToListAsync(Enumerable.Range(0, 4).Select(i => (ScrapeRequest)$"https://s.test/{i}"));

        Assert.AreEqual(4, result.Stats.Succeeded);
        Assert.IsGreaterThanOrEqualTo(500, result.Stats.Elapsed.TotalMilliseconds);    // 3 gaps of 200 ms
    }

    // ------------------------------------------------------------------ throttling

    /// <summary>
    /// The clock the throttle spaces the loads by. A loader is called after the throttle let its load go, and a thread held up on the
    /// way makes that load look late and the gap after it short: a gap is measured from a time that came before the throttle let the
    /// first load go, so a late thread can only make it look longer.
    /// </summary>
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>A load of <see cref="TwoHosts"/>: when the loader was called and when it answered, by <see cref="Clock"/>.</summary>
    private readonly record struct Load(string Url, TimeSpan Start, TimeSpan End);

    /// <summary>
    /// a.test answers 429 to its first request; b.test is always fine, each page taking <paramref name="bLoadMs"/>.
    /// Records each load.
    /// </summary>
    private static DelegateLoader TwoHosts(ConcurrentQueue<Load> loads, string? retryAfter = null, int bLoadMs = 0)
    {
        var first = 0;
        return new DelegateLoader(async (r, token) =>
        {
            var start = Clock.Elapsed;
            try
            {
                if (r.Url.StartsWith("https://a.test") && Interlocked.Exchange(ref first, 1) == 0)
                {
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (retryAfter != null) headers["Retry-After"] = retryAfter;
                    return PageResponse.FromText(429, r.Url, "slow down", headers: headers);
                }
                if (bLoadMs > 0 && r.Url.StartsWith("https://b.test")) await Task.Delay(bLoadMs, token);
                return PageResponse.FromText(200, r.Url, ScraperTests.Html("ok"));
            }
            finally
            {
                loads.Enqueue(new Load(r.Url, start, Clock.Elapsed));
            }
        });
    }

    private static Load[] LoadsOf(ConcurrentQueue<Load> loads, string host) =>
        loads.Where(l => l.Url.StartsWith($"https://{host}/")).OrderBy(l => l.Start).ToArray();

    [TestMethod]
    public async Task AutoThrottle_SpacesTheOverloadedHost_OtherHostsKeepFullSpeed()
    {
        var loads = new ConcurrentQueue<Load>();
        var options = ScraperTests.FastOptions();
        options.AutoThrottle = true;
        options.MaxConcurrency = 4;
        options.MaxThrottleDelay = TimeSpan.FromMilliseconds(300);
        // the start page links to the rest, so they are queued after its 429 (by its retry). Pages queued together with it
        // would go to the other workers at once, and those that pass the throttle before the 429 is handled load at full
        // speed: the throttle cannot know about a 429 that has not come back yet
        var scraper = new Scraper(TwoHosts(loads), options).Route("default", ctx =>
        {
            if (ctx.Request.Depth > 0) return;
            ctx.Enqueue(Enumerable.Range(1, 4).Select(i => $"https://a.test/{i}"));
            ctx.Enqueue(Enumerable.Range(0, 20).Select(i => $"https://b.test/{i}"));
        });

        var result = await scraper.RunToListAsync("https://a.test/0");

        Assert.AreEqual(25, result.Stats.Succeeded);
        var a = LoadsOf(loads, "a.test");
        var b = LoadsOf(loads, "b.test");
        Assert.HasCount(6, a);          // 5 pages + the retry of the 429
        // after the 429 the retry waits out the pause of 300 ms, and each load of a.test after it the delay the host had when the
        // one before started, 300 ms shrinking by 10 % per success. Each load is measured from the answer of the 429, which the
        // throttle heard of only after it
        var least = TimeSpan.FromMilliseconds(300);
        var delay = least;
        for (var i = 1; i < a.Length; i++)
        {
            Assert.IsGreaterThanOrEqualTo(least.TotalMilliseconds, (a[i].Start - a[0].End).TotalMilliseconds, $"a.test load {i} came too soon");
            least += delay;
            delay *= 0.9;
        }
        // b.test was not held back: it was done long before a.test
        Assert.IsLessThan(a[^1].Start, b[^1].Start);
        Assert.IsGreaterThan(500, (a[^1].Start - b[^1].Start).TotalMilliseconds);
    }

    [TestMethod]
    public async Task AutoThrottle_WorkersBusyWithOtherHosts_LoadsStaySpaced()
    {
        // one worker and slow b.test pages queued after a.test: the slots of a.test come while the worker loads b.test
        var loads = new ConcurrentQueue<Load>();
        var options = ScraperTests.FastOptions();
        options.AutoThrottle = true;
        options.MaxConcurrency = 1;
        options.MaxThrottleDelay = TimeSpan.FromMilliseconds(150);
        var scraper = new Scraper(TwoHosts(loads, bLoadMs: 250), options).Route("default", _ => { });

        var requests = Enumerable.Range(0, 4).Select(i => (ScrapeRequest)$"https://a.test/{i}")
            .Concat(Enumerable.Range(0, 8).Select(i => (ScrapeRequest)$"https://b.test/{i}"));
        var started = Clock.Elapsed;
        var result = await scraper.RunToListAsync(requests);

        Assert.AreEqual(12, result.Stats.Succeeded);
        var all = loads.OrderBy(l => l.Start).ToArray();       // one worker: one load after the other
        var a = LoadsOf(loads, "a.test");
        var b = LoadsOf(loads, "b.test");
        Assert.HasCount(5, a);          // 4 pages + the retry of the 429
        // a slot that comes while the worker is busy waits for it, and the next slot counts from that load: the pages of a.test do
        // not come in a burst when the worker gets free. The worker takes a request only when it is done with the one before, so
        // each load of a.test is measured from the end of the load before the previous one of a.test (the start of the run for
        // the first): the throttle let that previous one go only after it
        for (var i = 1; i < a.Length; i++)
        {
            var previous = Array.IndexOf(all, a[i - 1]);
            var from = previous > 0 ? all[previous - 1].End : started;
            Assert.IsGreaterThanOrEqualTo(100, (a[i].Start - from).TotalMilliseconds, $"a.test load {i} came too soon");
        }
        // and a slot goes to the next free worker, not to the end of the queue behind the waiting b.test pages
        Assert.IsLessThan(b[^1].Start, a[3].Start);
    }

    [TestMethod]
    public async Task RetryAfter_PausesTheWholeHost()
    {
        var loads = new ConcurrentQueue<Load>();
        var options = ScraperTests.FastOptions();
        options.AutoThrottle = true;
        options.MaxConcurrency = 1;
        options.MaxThrottleDelay = TimeSpan.FromSeconds(2);
        var scraper = new Scraper(TwoHosts(loads, retryAfter: "1"), options).Route("default", _ => { });

        var result = await scraper.RunToListAsync(Enumerable.Range(0, 3).Select(i => (ScrapeRequest)$"https://a.test/{i}"));

        Assert.AreEqual(3, result.Stats.Succeeded);
        var times = loads.Select(l => l.Start).Order().ToArray();
        // every load after the 429 waited for the pause, not only the retried page
        Assert.IsTrue(times.Skip(1).All(t => (t - times[0]).TotalMilliseconds >= 900), string.Join(", ", times.Select(t => (t - times[0]).TotalMilliseconds)));
    }

    [TestMethod]
    public async Task AutoThrottleIsOffByDefault_DoesNotSlowDown()
    {
        var loads = new ConcurrentQueue<Load>();
        var options = ScraperTests.FastOptions();
        Assert.IsFalse(options.AutoThrottle);
        options.MaxThrottleDelay = TimeSpan.FromSeconds(5);
        var scraper = new Scraper(TwoHosts(loads), options).Route("default", _ => { });

        var result = await scraper.RunToListAsync(Enumerable.Range(0, 10).Select(i => (ScrapeRequest)$"https://a.test/{i}"));

        Assert.AreEqual(10, result.Stats.Succeeded);
        Assert.IsLessThan(1000, result.Stats.Elapsed.TotalMilliseconds);
    }

    [TestMethod]
    public async Task HandlerSoftBlock_ThrottlesTheHostToo()
    {
        var loads = new ConcurrentQueue<(string Url, DateTime At)>();
        var loader = new DelegateLoader((r, _) =>
        {
            loads.Enqueue((r.Url, DateTime.UtcNow));
            return Task.FromResult(PageResponse.FromText(200, r.Url, ScraperTests.Html("ok")));
        });
        var options = ScraperTests.FastOptions();
        options.AutoThrottle = true;
        options.MaxConcurrency = 1;
        options.MaxThrottleDelay = TimeSpan.FromMilliseconds(400);
        var blocked = 0;
        var scraper = new Scraper(loader, options).Route("default", _ =>
        {
            if (Interlocked.Exchange(ref blocked, 1) == 0) throw new RetryRequestException("prices hidden", blocked: true);
        });

        await scraper.RunToListAsync(Enumerable.Range(0, 3).Select(i => (ScrapeRequest)$"https://s.test/{i}"));

        var times = loads.Select(l => l.At).Order().ToArray();
        Assert.IsGreaterThanOrEqualTo(300, (times[1] - times[0]).TotalMilliseconds);
    }
}
