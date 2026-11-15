using System.Collections.Concurrent;
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
        var scraper = new Scraper<(int Page, int Depth)>(loader, options).Route("default", ctx =>
        {
            ctx.Enqueue(ctx.Html.XPathValue("//a/@href"));
            ctx.Emit((PageNumber(ctx.Request.Url), ctx.Request.Depth));
        });

        var result = await scraper.RunToListAsync("https://s.test/0");

        CollectionAssert.AreEqual(new[] { (0, 0), (1, 1), (2, 2) }, result.Items.OrderBy(i => i.Page).ToArray());
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
        var scraper = new Scraper<string>(loader, options).Route("default", ctx =>
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
        var scraper = new Scraper<string>(RobotsSite("User-agent: *\nDisallow: /private", loads), options).Route("default", ctx => ctx.Emit(ctx.Request.Url));

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
        var scraper = new Scraper<string>(RobotsSite(robots, loads, status), options).Route("default", ctx => ctx.Emit(ctx.Request.Url));

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
        var scraper = new Scraper<string>(RobotsSite(robots, loads), options).Route("default", ctx => ctx.Emit(ctx.Request.Url));

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
        var scraper = new Scraper<string>(RobotsSite("User-agent: *\nCrawl-delay: 0.2", loads), options).Route("default", _ => { });

        var result = await scraper.RunToListAsync(Enumerable.Range(0, 4).Select(i => (ScrapeRequest)$"https://s.test/{i}"));

        Assert.AreEqual(4, result.Stats.Succeeded);
        Assert.IsGreaterThanOrEqualTo(500, result.Stats.Elapsed.TotalMilliseconds);    // 3 gaps of 200 ms
    }

    // ------------------------------------------------------------------ throttling

    /// <summary>a.test answers 429 to its first request; b.test is always fine. Records when each page was loaded.</summary>
    private static DelegateLoader TwoHosts(ConcurrentQueue<(string Url, DateTime At)> loads, string? retryAfter = null)
    {
        var first = 0;
        return new DelegateLoader((r, _) =>
        {
            loads.Enqueue((r.Url, DateTime.UtcNow));
            if (r.Url.StartsWith("https://a.test") && Interlocked.Exchange(ref first, 1) == 0)
            {
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (retryAfter != null) headers["Retry-After"] = retryAfter;
                return Task.FromResult(PageResponse.FromText(429, r.Url, "slow down", headers: headers));
            }
            return Task.FromResult(PageResponse.FromText(200, r.Url, ScraperTests.Html("ok")));
        });
    }

    [TestMethod]
    public async Task AutoThrottle_SpacesTheOverloadedHost_OtherHostsKeepFullSpeed()
    {
        var loads = new ConcurrentQueue<(string Url, DateTime At)>();
        var options = ScraperTests.FastOptions();
        options.MaxConcurrency = 4;
        options.MaxThrottleDelay = TimeSpan.FromMilliseconds(300);
        var scraper = new Scraper<string>(TwoHosts(loads), options).Route("default", _ => { });

        var requests = Enumerable.Range(0, 5).Select(i => (ScrapeRequest)$"https://a.test/{i}")
            .Concat(Enumerable.Range(0, 20).Select(i => (ScrapeRequest)$"https://b.test/{i}"));
        var result = await scraper.RunToListAsync(requests);

        Assert.AreEqual(25, result.Stats.Succeeded);
        var a = loads.Where(l => l.Url.StartsWith("https://a.test")).Select(l => l.At).Order().ToArray();
        var b = loads.Where(l => l.Url.StartsWith("https://b.test")).Select(l => l.At).Order().ToArray();
        Assert.HasCount(6, a);          // 5 pages + the retry of the 429
        // after the 429 the loads of a.test are about 300 ms apart (the delay shrinks by 10 % per success)
        for (var i = 2; i < a.Length; i++)
        {
            Assert.IsGreaterThanOrEqualTo(150, (a[i] - a[i - 1]).TotalMilliseconds, $"a.test load {i} came too soon");
        }
        // b.test was not held back: it was done long before a.test
        Assert.IsLessThan(a[^1], b[^1]);
        Assert.IsGreaterThan(500, (a[^1] - b[^1]).TotalMilliseconds);
    }

    [TestMethod]
    public async Task RetryAfter_PausesTheWholeHost()
    {
        var loads = new ConcurrentQueue<(string Url, DateTime At)>();
        var options = ScraperTests.FastOptions();
        options.MaxConcurrency = 1;
        options.MaxThrottleDelay = TimeSpan.FromSeconds(2);
        var scraper = new Scraper<string>(TwoHosts(loads, retryAfter: "1"), options).Route("default", _ => { });

        var result = await scraper.RunToListAsync(Enumerable.Range(0, 3).Select(i => (ScrapeRequest)$"https://a.test/{i}"));

        Assert.AreEqual(3, result.Stats.Succeeded);
        var times = loads.Select(l => l.At).Order().ToArray();
        // every load after the 429 waited for the pause, not only the retried page
        Assert.IsTrue(times.Skip(1).All(t => (t - times[0]).TotalMilliseconds >= 900), string.Join(", ", times.Select(t => (t - times[0]).TotalMilliseconds)));
    }

    [TestMethod]
    public async Task AutoThrottleOff_DoesNotSlowDown()
    {
        var loads = new ConcurrentQueue<(string Url, DateTime At)>();
        var options = ScraperTests.FastOptions();
        options.AutoThrottle = false;
        options.MaxThrottleDelay = TimeSpan.FromSeconds(5);
        var scraper = new Scraper<string>(TwoHosts(loads), options).Route("default", _ => { });

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
        options.MaxConcurrency = 1;
        options.MaxThrottleDelay = TimeSpan.FromMilliseconds(400);
        var blocked = 0;
        var scraper = new Scraper<string>(loader, options).Route("default", _ =>
        {
            if (Interlocked.Exchange(ref blocked, 1) == 0) throw new RetryRequestException("prices hidden", blocked: true);
        });

        await scraper.RunToListAsync(Enumerable.Range(0, 3).Select(i => (ScrapeRequest)$"https://s.test/{i}"));

        var times = loads.Select(l => l.At).Order().ToArray();
        Assert.IsGreaterThanOrEqualTo(300, (times[1] - times[0]).TotalMilliseconds);
    }
}
