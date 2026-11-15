using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using z3l.FastHtmlParser;

namespace z3l.Scraper.Tests;

/// <summary>ScraperOptions.StatePath: save, resume, at-least-once.</summary>
[TestClass]
public sealed class StateTests
{
    private string _directory = null!;

    [TestInitialize]
    public void CreateDirectory()
    {
        _directory = Path.Combine(Path.GetTempPath(), "z3l-scraper-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void DeleteDirectory()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string StatePath => Path.Combine(_directory, "state", "run.json");

    private ScraperOptions Options(int concurrency = 1)
    {
        var options = ScraperTests.FastOptions();
        options.StatePath = StatePath;
        options.MaxConcurrency = concurrency;
        return options;
    }

    /// <summary>/0 -> /1 -> ... -> /9</summary>
    private static DelegateLoader Chain(ConcurrentDictionary<int, int> loads, TimeSpan delay = default) => new(async (r, token) =>
    {
        var n = int.Parse(r.Url[(r.Url.LastIndexOf('/') + 1)..]);
        loads.AddOrUpdate(n, 1, (_, c) => c + 1);
        if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
        return PageResponse.FromText(200, r.Url, ScraperTests.Html(n < 9 ? $"<a href='/{n + 1}'>next</a>" : "end"));
    });

    private static Scraper ChainScraper(DelegateLoader loader, ScraperOptions options) =>
        new Scraper(loader, options).Route("default", ctx =>
        {
            ctx.Enqueue(ctx.Html.XPathValue("//a/@href"));
            ctx.Emit(int.Parse(ctx.Request.Url[(ctx.Request.Url.LastIndexOf('/') + 1)..]));
        });

    [TestMethod]
    public async Task FinishedRun_LeavesNoStateFile()
    {
        var loads = new ConcurrentDictionary<int, int>();
        var result = await ChainScraper(Chain(loads), Options()).RunToListAsync("https://s.test/0");

        Assert.HasCount(10, result.Items);
        Assert.IsFalse(File.Exists(StatePath));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task StoppedRun_IsContinued_NothingLost_NothingTakenRedone()
    {
        var loads = new ConcurrentDictionary<int, int>();
        var options = Options();
        options.ItemBufferSize = 2;

        // first run: the consumer stops after four items; the scraper has already done a few pages more
        var first = new List<int>();
        await foreach (var item in ChainScraper(Chain(loads), options).RunAsync(["https://s.test/0"]))
        {
            first.Add((int)item);
            if (first.Count == 4) break;
        }
        Assert.IsTrue(File.Exists(StatePath), "the stopped run must leave its state");

        // second run with the same start: continues instead of starting over
        var scraper = ChainScraper(Chain(loads), Options());
        var second = new List<int>();
        await foreach (var item in scraper.RunAsync(["https://s.test/0"])) second.Add((int)item);

        CollectionAssert.AreEquivalent(Enumerable.Range(0, 10).ToArray(), first.Union(second).ToArray(), "nothing lost");
        // items taken by the first loop are not redone; only the last one (its loop body was left by break) may come again
        CollectionAssert.IsSubsetOf(first.Intersect(second).ToArray(), new[] { first[^1] });
        Assert.IsGreaterThan(0, scraper.Stats.Restored);
        Assert.IsGreaterThanOrEqualTo(1, scraper.Stats.Duplicates);     // at least the start request, seen by the first run
        Assert.IsFalse(File.Exists(StatePath));
        Assert.AreEqual(1, loads[0]);                        // the first page was not loaded again
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task StateIsSavedWhileTheRunGoes()
    {
        var loads = new ConcurrentDictionary<int, int>();
        var options = Options();
        options.StateSaveInterval = TimeSpan.FromMilliseconds(50);
        var seenOnDisk = false;
        var scraper = new Scraper(Chain(loads, TimeSpan.FromMilliseconds(40)), options).Route("default", ctx =>
        {
            ctx.Enqueue(ctx.Html.XPathValue("//a/@href"));
            if (ctx.Request.Url.EndsWith("/6")) seenOnDisk = File.Exists(StatePath);
        });

        await scraper.RunToListAsync("https://s.test/0");

        Assert.IsTrue(seenOnDisk, "the state file must exist during the run");
        Assert.IsFalse(File.Exists(StatePath));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task UserDataAndRequestFields_SurviveTheRestart()
    {
        var json = JsonSerializer.SerializeToElement(new Dictionary<string, int> { ["a"] = 1 });
        var stamp = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.FromHours(5));
        var requests = new ScrapeRequest[]
        {
            new("https://s.test/1") { Data = 42 },
            new("https://s.test/2") { Data = 42L },
            new("https://s.test/3") { Data = 0.1 },
            new("https://s.test/4") { Data = 12.50m },
            new("https://s.test/5") { Data = "текст" },
            new("https://s.test/6") { Data = true },
            new("https://s.test/7") { Data = stamp },
            new("https://s.test/8") { Data = json },
            new("https://s.test/9", "post") { Method = HttpMethod.Post, Body = "q=1", ContentType = "text/plain", Headers = new Dictionary<string, string> { ["X-A"] = "b" }, Priority = ScrapePriority.High, MaxRetries = 7, UniqueKey = "own" },
        };

        // first run never finishes a load: everything stays in the state file
        var stuck = new DelegateLoader(async (r, token) => { await Task.Delay(Timeout.Infinite, token); return null!; });
        var options = Options(concurrency: 4);
        options.MaxRunDuration = TimeSpan.FromMilliseconds(200);
        await new Scraper(stuck, options).Route("default", _ => { }).Route("post", _ => { }).RunToListAsync(requests);
        Assert.IsTrue(File.Exists(StatePath));

        var fast = new DelegateLoader((r, _) => Task.FromResult(PageResponse.FromText(200, r.Url, "x")));
        var restored = new ConcurrentDictionary<string, ScrapeRequest>();
        await new Scraper(fast, Options())
            .Route("default", ctx => restored[ctx.Request.Url] = ctx.Request)
            .Route("post", ctx => restored[ctx.Request.Url] = ctx.Request)
            .RunToListAsync();

        Assert.HasCount(9, restored);
        Assert.AreEqual(42, restored["https://s.test/1"].Data);
        Assert.AreEqual(42L, restored["https://s.test/2"].Data);
        Assert.AreEqual(0.1, restored["https://s.test/3"].Data);
        Assert.AreEqual(12.50m, restored["https://s.test/4"].Data);
        Assert.AreEqual("текст", restored["https://s.test/5"].Data);
        Assert.IsInstanceOfType<bool>(restored["https://s.test/6"].Data);
        Assert.IsTrue((bool)restored["https://s.test/6"].Data!);
        Assert.AreEqual(stamp, restored["https://s.test/7"].Data);
        Assert.AreEqual(1, ((JsonElement)restored["https://s.test/8"].Data!).GetProperty("a").GetInt32());
        var post = restored["https://s.test/9"];
        Assert.AreEqual(HttpMethod.Post, post.Method);
        Assert.AreEqual("q=1", post.Body);
        Assert.AreEqual("text/plain", post.ContentType);
        Assert.AreEqual("b", post.Headers!["X-A"]);
        Assert.AreEqual(ScrapePriority.High, post.Priority);
        Assert.AreEqual(7, post.MaxRetries);
        Assert.AreEqual("own", post.UniqueKey);
        Assert.AreEqual("post", post.Label);
    }

    [TestMethod]
    public async Task UnsupportedUserData_IsRefusedAtOnce()
    {
        var scraper = new Scraper(new FakeLoader(), Options()).Route("default", _ => { });

        var error = await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            scraper.RunToListAsync(new ScrapeRequest("https://s.test/a") { Data = new List<int>() }));
        StringAssert.Contains(error.Message, "List<Int32>");
        StringAssert.Contains(error.Message, "typed handler");
    }

    [TestMethod]
    public async Task BrokenStateFile_IsAnError_NotASilentRestart()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath, "{ not json");
        var scraper = new Scraper(new FakeLoader(), Options()).Route("default", _ => { });

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => scraper.RunToListAsync("https://s.test/a"));

        await File.WriteAllTextAsync(StatePath, """{"format":"z3l.Scraper state","version":99}""");
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => scraper.RunToListAsync("https://s.test/a"));
        StringAssert.Contains(error.Message, "version 99");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task StateWithALabelThatHasNoHandler_IsAnError()
    {
        var stuck = new DelegateLoader(async (r, token) => { await Task.Delay(Timeout.Infinite, token); return null!; });
        var options = Options();
        options.MaxRunDuration = TimeSpan.FromMilliseconds(100);
        await new Scraper(stuck, options).Route("old-label", _ => { }).RunToListAsync(new ScrapeRequest("https://s.test/a", "old-label"));

        var scraper = new Scraper(new FakeLoader(), Options()).Route("default", _ => { });
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scraper.RunToListAsync());
        StringAssert.Contains(error.Message, "old-label");
    }

    public sealed record Teaser(string Price, List<string> Tags);

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task TypedData_SurvivesTheRestart_WithStateJson()
    {
        var teaser = new Teaser("10.00", ["new", "sale"]);
        var stuck = new DelegateLoader(async (r, token) => { await Task.Delay(Timeout.Infinite, token); return null!; });
        var options = Options();
        options.StateJson = new DefaultJsonTypeInfoResolver();
        options.MaxRunDuration = TimeSpan.FromMilliseconds(150);
        await new Scraper(stuck, options).Route<Teaser>(PageType.Item, (_, _) => { })
            .RunToListAsync(ScrapeRequest.For("https://s.test/p/1", PageType.Item, teaser));
        Assert.IsTrue(File.Exists(StatePath));

        var fast = new DelegateLoader((r, _) => Task.FromResult(PageResponse.FromText(200, r.Url, "x")));
        var resumed = Options();
        resumed.StateJson = new DefaultJsonTypeInfoResolver();
        Teaser? restored = null;
        await new Scraper(fast, resumed).Route<Teaser>(PageType.Item, (_, data) => restored = data).RunToListAsync();

        Assert.IsNotNull(restored);
        Assert.AreEqual(teaser.Price, restored.Price);
        CollectionAssert.AreEqual(teaser.Tags, restored.Tags);
        Assert.IsFalse(File.Exists(StatePath));
    }

    [TestMethod]
    public async Task TypedData_WithoutStateJson_IsRefusedAtOnce()
    {
        var scraper = new Scraper(new FakeLoader(), Options()).Route<Teaser>(PageType.Item, (_, _) => { });

        var error = await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            scraper.RunToListAsync(ScrapeRequest.For("https://s.test/p/1", PageType.Item, new Teaser("1", []))));
        StringAssert.Contains(error.Message, "StateJson");
    }
}
