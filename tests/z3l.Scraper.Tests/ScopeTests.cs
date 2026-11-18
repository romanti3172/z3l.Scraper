using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using z3l.FastHtmlParser;

namespace z3l.Scraper.Tests;

/// <summary>The crawl scope (AllowedHosts, UrlFilter, the filter of Enqueue) and the URLs a run takes (IgnoredQueryParameters, the key of duplicates).</summary>
[TestClass]
public sealed class ScopeTests
{
    private static string Links(params string[] hrefs) => ScraperTests.Html(string.Concat(hrefs.Select(h => $"<a href='{h}'>link</a>")));

    /// <summary>Every URL is a page with no links; the loads are recorded.</summary>
    private static DelegateLoader AnyPage(ConcurrentQueue<string> loads) => new((r, _) =>
    {
        loads.Enqueue(r.Url);
        return Task.FromResult(PageResponse.FromText(200, r.Url, ScraperTests.Html("page")));
    });

    // ------------------------------------------------------------------ AllowedHosts

    [TestMethod]
    public async Task AllowedHosts_TakeTheHostAndItsSubdomains_OnlyThem()
    {
        var loader = new FakeLoader()
            .Page("https://shop.test/", Links("/a", "https://www.shop.test/b", "https://othershop.test/c", "https://shop.test.evil.test/d", "https://cdn.other.test/e"))
            .Page("https://shop.test/a", ScraperTests.Html("a"))
            .Page("https://www.shop.test/b", ScraperTests.Html("b"));
        var options = ScraperTests.FastOptions();
        options.AllowedHosts.Add("SHOP.test");
        var scraper = new Scraper(loader, options).Route("default", ctx =>
        {
            ctx.Enqueue(ctx.Html.XPathValues("//a/@href"));
            ctx.Emit(ctx.Request.Url);
        });

        var result = await scraper.RunToListAsync("https://shop.test/");

        string[] taken = ["https://shop.test/", "https://shop.test/a", "https://www.shop.test/b"];
        CollectionAssert.AreEquivalent(taken, result.Items.ToArray());
        CollectionAssert.AreEquivalent(taken, loader.Loaded.ToArray(), "a link out of the scope is not loaded");
        Assert.AreEqual(3, result.Stats.OutOfScope);
        Assert.AreEqual(3, result.Stats.Enqueued);
        Assert.AreEqual(0, result.Stats.SkippedByLimit);
        StringAssert.Contains(result.Stats.ToString(), "3 out of scope");
    }

    [TestMethod]
    public async Task AllowedHosts_StartRequestsAndThoseOfRequestsNeeded_AreTakenAsGiven()
    {
        var loads = new ConcurrentQueue<string>();
        var options = ScraperTests.FastOptions();
        options.AllowedHosts.Add("shop.test");
        var scraper = new Scraper(AnyPage(loads), options).Route("default", ctx => ctx.Enqueue("/linked"));
        var given = 0;
        scraper.RequestsNeeded += e =>
        {
            if (Interlocked.Exchange(ref given, 1) == 0) e.Add("https://feed.test/1");
            return Task.CompletedTask;
        };

        var result = await scraper.RunToListAsync("https://other.test/");

        CollectionAssert.AreEquivalent(new[] { "https://other.test/", "https://feed.test/1" }, loads.ToArray());
        Assert.AreEqual(2, result.Stats.OutOfScope);          // the links of those pages are checked
    }

    [TestMethod]
    public async Task AllowedHosts_AnInternationalizedNameIsTakenWrittenEitherWay()
    {
        var loads = new ConcurrentQueue<string>();
        var options = ScraperTests.FastOptions();
        options.AllowedHosts.Add("магазин.рф");
        var scraper = new Scraper(AnyPage(loads), options).Route("default", ctx =>
        {
            if (ctx.Request.Depth > 0) return;
            ctx.Enqueue("https://www.магазин.рф/x");
            ctx.Enqueue("https://xn--80aairftm.xn--p1ai/y");      // the same name, as DNS writes it
            ctx.Enqueue("https://другой.рф/z");
        });

        var result = await scraper.RunToListAsync("https://магазин.рф/");

        Assert.AreEqual(3, result.Stats.Succeeded);
        Assert.AreEqual(1, result.Stats.OutOfScope);
        Assert.IsFalse(loads.Any(u => u.Contains("/z")));
    }

    [TestMethod]
    [DataRow("https://shop.test")]
    [DataRow("shop.test/catalog")]
    [DataRow("shop.test:8080")]
    [DataRow("*.shop.test")]
    [DataRow(" ")]
    public async Task AllowedHosts_TakeHostNamesOnly(string entry)
    {
        var options = ScraperTests.FastOptions();
        options.AllowedHosts.Add(entry);
        var scraper = new Scraper(new FakeLoader(), options).Route("default", _ => { });

        var error = await Assert.ThrowsExactlyAsync<ArgumentException>(() => scraper.RunToListAsync("https://shop.test/"));

        StringAssert.Contains(error.Message, "AllowedHosts");
    }

    [TestMethod]
    public async Task OutOfScope_LinksTakeNothingOfMaxRequests_NorLoadTheRobotsTxtOfTheirHosts()
    {
        var loads = new ConcurrentQueue<string>();
        var options = ScraperTests.FastOptions();
        options.AllowedHosts.Add("s.test");
        options.MaxRequests = 3;
        options.RespectRobotsTxt = true;
        var scraper = new Scraper(AnyPage(loads), options).Route("default", ctx =>
        {
            if (ctx.Request.Depth > 0) return;
            ctx.Enqueue(Enumerable.Range(0, 5).Select(i => $"https://other{i}.test/"));
            ctx.Enqueue(["/a", "/b"]);
        });

        var result = await scraper.RunToListAsync("https://s.test/");

        Assert.AreEqual(3, result.Stats.Succeeded);
        Assert.AreEqual(0, result.Stats.SkippedByLimit);
        Assert.AreEqual(5, result.Stats.OutOfScope);
        CollectionAssert.AreEquivalent(new[] { "https://s.test/robots.txt", "https://s.test/", "https://s.test/a", "https://s.test/b" }, loads.ToArray());
    }

    [TestMethod]
    public async Task OutOfScope_TheFirstLinkIsLogged_OnceForTheRun()
    {
        var logger = new ListLogger();
        var options = ScraperTests.FastOptions();
        options.AllowedHosts.Add("s.test");
        var scraper = new Scraper(AnyPage(new ConcurrentQueue<string>()), options, logger).Route("default", ctx =>
        {
            if (ctx.Request.Depth == 0) ctx.Enqueue(["https://a.test/1", "https://b.test/2", "https://c.test/3"]);
        });

        await scraper.RunToListAsync("https://s.test/");

        var lines = logger.Lines.Where(l => l.Contains("out of the crawl scope")).ToArray();
        Assert.HasCount(1, lines);
        StringAssert.Contains(lines[0], "https://a.test/1");
        StringAssert.Contains(lines[0], "https://s.test/");      // the page it was found on
    }

    // ------------------------------------------------------------------ UrlFilter and the filter of Enqueue

    [TestMethod]
    public async Task UrlFilter_SkipsWhatItTurnsDown_AfterAllowedHosts_AndSeesTheCleanUrl()
    {
        var loads = new ConcurrentQueue<string>();
        var asked = new ConcurrentQueue<string>();
        var options = ScraperTests.FastOptions();
        options.AllowedHosts.Add("s.test");
        options.UrlFilter = url =>
        {
            asked.Enqueue(url.AbsoluteUri);
            return !url.AbsolutePath.StartsWith("/blog/");
        };
        var scraper = new Scraper(AnyPage(loads), options).Route("default", ctx =>
        {
            if (ctx.Request.Depth > 0) return;
            ctx.Enqueue("/p/1?utm_source=mail&id=7");
            ctx.Enqueue("/blog/1");
            ctx.Enqueue("https://other.test/p/2");
        });

        var result = await scraper.RunToListAsync("https://s.test/?utm_source=feed");

        CollectionAssert.AreEquivalent(new[] { "https://s.test/", "https://s.test/p/1?id=7" }, loads.ToArray());
        CollectionAssert.AreEquivalent(new[] { "https://s.test/p/1?id=7", "https://s.test/blog/1" }, asked.ToArray(),
            "the filter is not asked for start requests nor for hosts AllowedHosts turned down, and it sees no tracking parameters");
        Assert.AreEqual(2, result.Stats.OutOfScope);
    }

    [TestMethod]
    public async Task UrlFilter_ThatThrows_FailsTheHandler()
    {
        var options = ScraperTests.FastOptions();
        options.UrlFilter = _ => throw new InvalidOperationException("broken filter");
        var scraper = new Scraper(AnyPage(new ConcurrentQueue<string>()), options).Route("default", ctx => ctx.Enqueue("/next"));

        var result = await scraper.RunToListAsync("https://s.test/");

        var failed = result.Failed.Single();
        Assert.AreEqual(FailureReason.HandlerError, failed.Reason);
        StringAssert.Contains(failed.Message, "broken filter");
    }

    [TestMethod]
    public async Task FilterOfEnqueue_SendsOneListOfLinksToSeveralLabels()
    {
        var loads = new ConcurrentQueue<string>();
        string[] links = ["/p/1", "p/2", "/c/1", "/about", "mailto:shop@s.test"];
        var counts = new ConcurrentQueue<int>();
        var scraper = new Scraper(AnyPage(loads), ScraperTests.FastOptions())
            .Route("list", ctx =>
            {
                if (ctx.Request.Depth > 0)
                {
                    ctx.Emit($"list {new Uri(ctx.Request.Url).AbsolutePath}");
                    return;
                }
                counts.Enqueue(ctx.Enqueue(links, "item", filter: url => url.AbsolutePath.StartsWith("/p/")));     // relative links resolved
                counts.Enqueue(ctx.Enqueue(links, "list", filter: url => url.AbsolutePath.StartsWith("/c/")));
                counts.Enqueue(ctx.Enqueue(links, "item", filter: url => url.AbsolutePath.StartsWith("/p/")));     // duplicates now
            })
            .Route("item", ctx => ctx.Emit($"item {new Uri(ctx.Request.Url).AbsolutePath}"));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/", "list"));

        CollectionAssert.AreEquivalent(new[] { "item /p/1", "item /p/2", "list /c/1" }, result.Items.ToArray());
        CollectionAssert.AreEqual(new[] { 2, 1, 0 }, counts.ToArray());
        Assert.IsFalse(loads.Any(u => u.EndsWith("/about")));
        Assert.AreEqual(0, result.Stats.OutOfScope, "what the filter of a call turns down is not out of the scope of the run");
    }

    // ------------------------------------------------------------------ IgnoredQueryParameters and the key of duplicates

    [TestMethod]
    public async Task TrackingParameters_AreDroppedFromTheUrl_LinksThatDifferByThemAreOnePage()
    {
        var loads = new ConcurrentQueue<string>();
        var returned = new ConcurrentQueue<bool>();
        var scraper = new Scraper(AnyPage(loads), ScraperTests.FastOptions()).Route("default", ctx =>
        {
            if (ctx.Request.Depth == 0)
            {
                returned.Enqueue(ctx.Enqueue("/p?id=1&utm_source=mail&utm_medium=email"));
                returned.Enqueue(ctx.Enqueue("/p?gclid=abc&id=1"));
                returned.Enqueue(ctx.Enqueue("/p?id=1&YCLID=5#reviews"));
            }
            ctx.Emit(ctx.Request.Url);
        });

        var result = await scraper.RunToListAsync("https://s.test/?ysclid=42");

        CollectionAssert.AreEquivalent(new[] { "https://s.test/", "https://s.test/p?id=1" }, result.Items.ToArray());
        CollectionAssert.AreEquivalent(new[] { "https://s.test/", "https://s.test/p?id=1" }, loads.ToArray());
        CollectionAssert.AreEqual(new[] { true, false, false }, returned.ToArray());
        Assert.AreEqual(2, result.Stats.Duplicates);
    }

    [TestMethod]
    public async Task IgnoredQueryParameters_TakeYourOwn_OrClearedLoadUrlsAsTheyAre()
    {
        var loads = new ConcurrentQueue<string>();
        var options = ScraperTests.FastOptions();
        options.IgnoredQueryParameters.Add("sessionid");
        await new Scraper(AnyPage(loads), options).Route("default", _ => { }).RunToListAsync("https://s.test/p?SessionId=9&id=1&utm_campaign=x");
        CollectionAssert.AreEqual(new[] { "https://s.test/p?id=1" }, loads.ToArray());

        loads.Clear();
        options = ScraperTests.FastOptions();
        options.IgnoredQueryParameters.Clear();
        await new Scraper(AnyPage(loads), options).Route("default", _ => { })
            .RunToListAsync("https://s.test/p?id=1&utm_source=mail", "https://s.test/p?id=1");
        CollectionAssert.AreEquivalent(new[] { "https://s.test/p?id=1&utm_source=mail", "https://s.test/p?id=1" }, loads.ToArray());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("*")]
    [DataRow("u*m")]
    [DataRow("a=b")]
    public async Task IgnoredQueryParameters_TakeNamesAndPrefixesOnly(string entry)
    {
        var options = ScraperTests.FastOptions();
        options.IgnoredQueryParameters.Add(entry);
        var scraper = new Scraper(new FakeLoader(), options).Route("default", _ => { });

        var error = await Assert.ThrowsExactlyAsync<ArgumentException>(() => scraper.RunToListAsync("https://s.test/"));

        StringAssert.Contains(error.Message, "IgnoredQueryParameters");
    }

    [TestMethod]
    [DataRow("https://s.test/p?id=1&utm_source=a", "https://s.test/p?id=1")]
    [DataRow("https://s.test/p?utm_source=a&id=1&UTM_Medium=b&x=2", "https://s.test/p?id=1&x=2")]      // the others keep their order
    [DataRow("https://s.test/p?utm_source=a#top", "https://s.test/p#top")]                                // no query left, no '?'
    [DataRow("https://s.test/p?gclid=1&&id=2", "https://s.test/p?id=2")]
    [DataRow("https://s.test/p?id=1#a?utm_source=b", "https://s.test/p?id=1#a?utm_source=b")]            // the fragment is no query
    [DataRow("https://s.test/p#a?gclid=b", "https://s.test/p#a?gclid=b")]
    [DataRow("https://s.test/utm_source/p?utm=1&utmost=2", "https://s.test/utm_source/p?utm=1&utmost=2")] // utm_* is a prefix with '_'
    public void IgnoredQueryParameters_AreCutOutOfTheUrl(string url, string expected)
    {
        Assert.AreEqual(expected, UrlHelper.WithoutParameters(url, new QueryParameterSet(ScraperOptions.TrackingParameters)));
    }

    [TestMethod]
    public void IgnoredQueryParameters_AUrlWithoutThemIsLeftAsItIs()
    {
        const string url = "https://s.test/p?id=1&page=2";
        Assert.AreSame(url, UrlHelper.WithoutParameters(url, new QueryParameterSet(ScraperOptions.TrackingParameters)));
    }

    [TestMethod]
    public void DuplicateKey_IgnoresTheOrderOfQueryParameters_ButNotOfOneName()
    {
        static string Key(string url) => new ScrapeRequest(url).GetUniqueKey();

        Assert.AreEqual(Key("https://s.test/p?a=1&b=2"), Key("https://s.test/p?b=2&a=1"));
        Assert.AreEqual("GET https://s.test/p?a=1&b=2", Key("https://s.test/p?b=2&a=1"), "a sorted URL keeps the key it always had");
        Assert.AreEqual("GET https://s.test/p?a=1&b=2&c", Key("https://s.test/p?c&b=2&a=1"));
        Assert.AreNotEqual(Key("https://s.test/p?id=1&id=2"), Key("https://s.test/p?id=2&id=1"), "parameters of one name may be a list");
        Assert.AreEqual(Key("https://s.test/p?a=1&b=2"), Key("https://s.test/p?a=1&&b=2&"));
        Assert.AreEqual(Key("https://s.test/p"), Key("https://s.test/p?"));
        Assert.AreEqual(Key("https://s.test/p?a=2&b=1#x"), Key("https://s.test/p?b=1&a=2"));
        Assert.AreEqual("POST https://s.test/api?a=1&b=2 q=1", new ScrapeRequest("https://s.test/api?b=2&a=1") { Method = HttpMethod.Post, Body = "q=1" }.GetUniqueKey());
    }

    [TestMethod]
    public async Task QueryParametersInAnotherOrder_AreOnePage_LoadedAsFirstGiven()
    {
        var loads = new ConcurrentQueue<string>();
        var scraper = new Scraper(AnyPage(loads), ScraperTests.FastOptions()).Route("default", ctx =>
        {
            if (ctx.Request.Depth > 0) return;
            ctx.Enqueue("/list?sort=price&page=2");
            ctx.Enqueue("/list?page=2&sort=price");
        });

        var result = await scraper.RunToListAsync("https://s.test/");

        CollectionAssert.AreEquivalent(new[] { "https://s.test/", "https://s.test/list?sort=price&page=2" }, loads.ToArray());
        Assert.AreEqual(1, result.Stats.Duplicates);
    }

    // ------------------------------------------------------------------ ParseAsync and the state file

    [TestMethod]
    public async Task ParseAsync_FoundIsWhatARunWouldTake()
    {
        var options = ScraperTests.FastOptions();
        options.AllowedHosts.Add("s.test");
        options.UrlFilter = url => !url.AbsolutePath.StartsWith("/blog/");
        var returned = new List<bool>();
        var scraper = new Scraper(new FakeLoader(), options).Route("default", ctx =>
        {
            returned.Add(ctx.Enqueue("/p/1?utm_source=x"));
            returned.Add(ctx.Enqueue("https://other.test/p/2"));
            returned.Add(ctx.Enqueue("/blog/3"));
        });

        var page = await scraper.ParseAsync(PageResponse.FromText(200, "https://s.test/?utm_source=feed", ScraperTests.Html("")));

        Assert.AreEqual("https://s.test/", page.Request.Url);
        CollectionAssert.AreEqual(new[] { "https://s.test/p/1" }, page.Found.Select(r => r.Url).ToArray());
        CollectionAssert.AreEqual(new[] { true, false, false }, returned);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ContinuedRun_DropsTheSavedLinksANarrowedScopeTurnsDown()
    {
        using var folder = new TempFolder();
        var statePath = Path.Combine(folder.Path, "run.json");
        var stuck = true;
        var loader = new DelegateLoader(async (r, token) =>
        {
            if (stuck && r.Url != "https://s.test/") await Task.Delay(Timeout.Infinite, token);
            return PageResponse.FromText(200, r.Url, ScraperTests.Html("page"));
        });
        // the start page emits nothing: a page whose last item the consumer had not taken when the run stopped would be done again
        Scraper Make(ScraperOptions options) => new Scraper(loader, options).Route("default", ctx =>
        {
            if (ctx.Request.Depth == 0) ctx.Enqueue(["/a/1", "/b/1"]);
            else ctx.Emit(ctx.Request.Url);
        });

        var first = ScraperTests.FastOptions();
        first.StatePath = statePath;
        first.MaxRunDuration = TimeSpan.FromMilliseconds(500);
        await Make(first).RunToListAsync("https://s.test/");
        Assert.IsTrue(File.Exists(statePath), "the stopped run must leave its state");

        stuck = false;
        var second = ScraperTests.FastOptions();
        second.StatePath = statePath;
        second.UrlFilter = url => !url.AbsolutePath.StartsWith("/b/");
        var result = await Make(second).RunToListAsync("https://s.test/");

        CollectionAssert.AreEqual(new[] { "https://s.test/a/1" }, result.Items.ToArray());
        Assert.AreEqual(1, result.Stats.Restored);
        Assert.AreEqual(1, result.Stats.OutOfScope);
        Assert.IsFalse(File.Exists(statePath), "the dropped request is not left in the state");
    }

    private sealed class ListLogger : ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue(formatter(state, exception));
    }
}
