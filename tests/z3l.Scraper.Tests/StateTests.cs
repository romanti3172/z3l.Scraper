using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
    private string SeenPath => StatePath + ".seen";

    /// <summary>What the journal keeps for a key: the first 16 bytes of the SHA-256 of its UTF-8, in hex.</summary>
    private static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 16);

    private static byte[] HashBytes(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key))[..16];

    private List<string> JournalHashes() => [.. File.ReadAllBytes(SeenPath).Chunk(16).Select(x => Convert.ToHexStringLower(x))];

    /// <summary>A run of the chain from /0 that stops on /<paramref name="stop"/>, whose load never ends: that page stays in the state file.</summary>
    private async Task<int[]> RunUntilStuck(int stop)
    {
        var loader = new DelegateLoader(async (r, token) =>
        {
            var n = int.Parse(r.Url[(r.Url.LastIndexOf('/') + 1)..]);
            if (n == stop) await Task.Delay(Timeout.Infinite, token);
            return PageResponse.FromText(200, r.Url, ScraperTests.Html($"<a href='/{n + 1}'>next</a>"));
        });
        var options = Options();
        options.MaxRunDuration = TimeSpan.FromMilliseconds(300);
        var result = await ChainScraper(loader, options).RunToListAsync("https://s.test/0");
        Assert.IsTrue(File.Exists(StatePath), "the stopped run must leave its state");
        return [.. result.Items.Cast<int>()];
    }

    private static string[] HashesOf(params int[] pages) => [.. pages.Select(n => Hash($"GET https://s.test/{n}"))];

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
        Assert.IsFalse(File.Exists(SeenPath));
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
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ParentUrl_SurvivesTheRestart()
    {
        // the listing is handled and redirected; the item it found never loads and stays in the state file
        var first = new DelegateLoader(async (r, token) =>
        {
            if (r.Url.EndsWith("/item", StringComparison.Ordinal)) await Task.Delay(Timeout.Infinite, token);
            return PageResponse.FromText(200, r.Url + "?moved", "x");
        });
        var options = Options(concurrency: 2);
        options.MaxRunDuration = TimeSpan.FromMilliseconds(300);
        await new Scraper(first, options).Route("default", ctx => ctx.Enqueue("/item")).RunToListAsync("https://s.test/list");
        Assert.IsTrue(File.Exists(StatePath));
        StringAssert.Contains(await File.ReadAllTextAsync(StatePath), "parentUrl");

        var fast = new DelegateLoader((r, _) => Task.FromResult(PageResponse.FromText(200, r.Url, "x")));
        var parents = new ConcurrentQueue<string?>();
        await new Scraper(fast, Options()).Route("default", ctx => parents.Enqueue(ctx.Request.ParentUrl)).RunToListAsync();

        CollectionAssert.AreEqual(new[] { "https://s.test/list?moved" }, parents.ToArray());
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

    // ------------------------------------------------------------------ the pages seen: hashes in a journal

    [TestMethod]
    public void KeyHash_IsTheStartOfTheSha256OfTheKey()
    {
        // the last one, a big POST body, is hashed from a rented buffer
        foreach (var key in new[] { "GET https://s.test/0", "POST https://s.test/p a=1&b=текст", "POST https://s.test/p " + new string('я', 5000) })
        {
            var hash = KeyHash.Of(key);
            Assert.AreEqual(Hash(key), hash.ToString());
            var bytes = new byte[KeyHash.Size];
            hash.Write(bytes);
            CollectionAssert.AreEqual(HashBytes(key), bytes);
            Assert.AreEqual(hash, KeyHash.Read(bytes));
        }
        Assert.AreNotEqual(KeyHash.Of("GET https://s.test/p"), KeyHash.Of("POST https://s.test/p"));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task SeenPages_AreHashesInTheJournal_NotKeysInTheStateFile()
    {
        CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, await RunUntilStuck(3));

        using (var state = JsonDocument.Parse(await File.ReadAllBytesAsync(StatePath)))
        {
            Assert.AreEqual(2, state.RootElement.GetProperty("version").GetInt32());
            Assert.AreEqual(4, state.RootElement.GetProperty("seenCount").GetInt64());     // /0 to /3; /3 never loaded, so no /4
        }
        CollectionAssert.AreEquivalent(HashesOf(0, 1, 2, 3), JournalHashes());

        // a page done and taken is a hash now; only the request not finished is in the state file
        var text = await File.ReadAllTextAsync(StatePath);
        Assert.IsFalse(text.Contains("https://s.test/0", StringComparison.Ordinal));
        StringAssert.Contains(text, "https://s.test/3");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task JournalTail_ThatTheStateFileDoesNotCount_IsIgnoredAndWrittenOver()
    {
        await RunUntilStuck(3);
        // a process that died after appending to the journal and before writing the state file: a hash of /5, which no run has
        // done yet, and half of one more
        await File.AppendAllBytesAsync(SeenPath, [.. HashBytes("GET https://s.test/5"), 1, 2, 3, 4, 5, 6, 7]);

        // /5 is done, not skipped as seen; the save writes over the tail
        AssertDone(await RunUntilStuck(7), [3, 4, 5, 6], lastOfTheRunBefore: 2);
        using (var state = JsonDocument.Parse(await File.ReadAllBytesAsync(StatePath)))
        {
            Assert.AreEqual(8, state.RootElement.GetProperty("seenCount").GetInt64());
        }
        Assert.AreEqual(8 * 16, new FileInfo(SeenPath).Length);
        CollectionAssert.AreEquivalent(HashesOf(0, 1, 2, 3, 4, 5, 6, 7), JournalHashes());

        var result = await ChainScraper(Chain(new ConcurrentDictionary<int, int>()), Options()).RunToListAsync("https://s.test/0");
        AssertDone([.. result.Items.Cast<int>()], [7, 8, 9], lastOfTheRunBefore: 6);
        Assert.IsFalse(File.Exists(StatePath));
        Assert.IsFalse(File.Exists(SeenPath));
    }

    /// <summary>
    /// A run that continues a stopped one did <paramref name="expected"/>, and may have done the last page of the run before again:
    /// a run stopped by its time limit saves the state before the consumer asks for one more item, so that page counts as not taken.
    /// </summary>
    private static void AssertDone(int[] items, int[] expected, int lastOfTheRunBefore)
    {
        CollectionAssert.IsSubsetOf(expected, items);
        CollectionAssert.IsSubsetOf(items, expected.Append(lastOfTheRunBefore).ToArray());
        Assert.HasCount(items.Distinct().Count(), items, "no page twice in one run");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task StateFileOfVersion1_IsContinued()
    {
        // 0.1.0-preview.7 and earlier kept the keys themselves in the state file
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath, """
            {"format":"z3l.Scraper state","version":1,"savedAt":"2026-11-17T10:00:00+00:00",
             "seen":["GET https://s.test/0","GET https://s.test/1","GET https://s.test/6"],
             "pending":[{"url":"https://s.test/1","label":"default","depth":1}]}
            """);

        // the first save writes the hashes of the old keys into the journal, with the new ones
        CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, await RunUntilStuck(4));
        using (var state = JsonDocument.Parse(await File.ReadAllBytesAsync(StatePath)))
        {
            Assert.AreEqual(2, state.RootElement.GetProperty("version").GetInt32());
        }
        CollectionAssert.AreEquivalent(HashesOf(0, 1, 6, 2, 3, 4), JournalHashes());

        // /6, seen by the run of the old version, is still a duplicate: the chain ends before it
        var loads = new ConcurrentDictionary<int, int>();
        var result = await ChainScraper(Chain(loads), Options()).RunToListAsync("https://s.test/0");
        AssertDone([.. result.Items.Cast<int>()], [4, 5], lastOfTheRunBefore: 3);
        Assert.IsFalse(loads.ContainsKey(6));
        Assert.IsFalse(File.Exists(StatePath));
        Assert.IsFalse(File.Exists(SeenPath));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MissingJournal_IsAnError_NotASilentRestart()
    {
        await RunUntilStuck(3);
        File.Delete(SeenPath);

        var scraper = ChainScraper(Chain(new ConcurrentDictionary<int, int>()), Options());
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => scraper.RunToListAsync("https://s.test/0"));
        StringAssert.Contains(error.Message, "run.json.seen holds 0 hashes");
        StringAssert.Contains(error.Message, "counts 4");
    }
}
