using System.Collections.Concurrent;
using System.Text.Json;
using z3l.FastHtmlParser;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Tests;

/// <summary>Keeps the snapshots in memory, as a snapshot store of your own would keep them in a database.</summary>
internal sealed class MemorySnapshots : ISnapshotStore
{
    public ConcurrentQueue<(SnapshotKind Kind, string Url, int Attempt, string Reason, string Text, bool FromCache, Exception? Exception)> Saved { get; } = new();

    public ValueTask SaveAsync(PageSnapshot snapshot, CancellationToken cancellationToken)
    {
        // the response is valid only during the call: take the text now
        Saved.Enqueue((snapshot.Kind, snapshot.Request.Url, snapshot.Request.Attempts, snapshot.Reason, snapshot.Response.Text, snapshot.FromCache,
            snapshot.Exception));
        return ValueTask.CompletedTask;
    }
}

[TestClass]
public sealed class SnapshotTests
{
    private static string Html(string body) => ScraperTests.Html(body);

    /// <summary>A product for each kind of trouble, and a good one.</summary>
    private static FakeLoader Troubles() => new FakeLoader()
        .Page("https://s.test/p/ok", Html("<h1>Ok</h1><span class='price'>1</span>"))
        .Page("https://s.test/p/blocked", "<html>captcha</html>", 403)
        .Page("https://s.test/p/missing", Html("gone"), 404)
        .Page("https://s.test/p/broken", Html("<h1>Broken</h1>"))
        .Page("https://s.test/p/boom", Html("<h1>Boom</h1><span class='price'>1</span>"));

    private static ScrapeRequest[] Products(params string[] names) => [.. names.Select(name => new ScrapeRequest($"https://s.test/p/{name}", PageType.Item))];

    /// <summary>A product page needs a price; the handler chokes on "Boom".</summary>
    private static Scraper TroubleScraper(IPageLoader loader, ISnapshotStore snapshots, int maxRetries = 0)
    {
        var options = ScraperTests.FastOptions();
        options.MaxRetries = maxRetries;
        options.NotFoundRetries = 0;
        options.Rules[PageType.Item].MustContain.Add("class='price'");
        return new Scraper(loader, options) { SnapshotStore = snapshots }
            .Route(PageType.Item, ctx =>
            {
                var title = ctx.Html.Find(HtmlTag.H1).Text()!;
                if (title == "Boom") throw new InvalidOperationException("boom");
                ctx.Emit(title);
            });
    }

    private static string[] MetaFiles(TempFolder folder, string label, string kind)
    {
        var path = Path.Combine(folder.Path, label, kind);
        return Directory.Exists(path) ? Directory.GetFiles(path, "*.meta.json") : [];
    }

    [TestMethod]
    public async Task SavesThePagesThatWereNotTaken_ByLabelAndKind()
    {
        using var folder = new TempFolder();

        var result = await TroubleScraper(Troubles(), new SnapshotStore { Path = folder.Path })
            .RunToListAsync(Products("ok", "blocked", "missing", "broken", "boom"));

        CollectionAssert.AreEqual(new[] { "Ok" }, result.Items.ToArray());
        foreach (var kind in new[] { "blocked", "not-found", "retry", "handler-error" })
        {
            Assert.HasCount(1, MetaFiles(folder, "item", kind), kind);
            Assert.HasCount(1, Directory.GetFiles(Path.Combine(folder.Path, "item", kind), "*.html"), kind);
        }
        Assert.HasCount(4, folder.Files(".meta.json"));
    }

    [TestMethod]
    public async Task Description_TellsWhatHappened()
    {
        using var folder = new TempFolder();
        await TroubleScraper(Troubles(), new SnapshotStore { Path = folder.Path }).RunToListAsync(Products("boom", "blocked"));

        var meta = MetaFiles(folder, "item", "handler-error").Single();
        using (var json = JsonDocument.Parse(File.ReadAllBytes(meta)))
        {
            var root = json.RootElement;
            Assert.AreEqual("handler-error", root.GetProperty("kind").GetString());
            Assert.AreEqual("boom", root.GetProperty("reason").GetString());
            Assert.AreEqual("Item", root.GetProperty("label").GetString());
            Assert.AreEqual("https://s.test/p/boom", root.GetProperty("requestUrl").GetString());
            Assert.AreEqual(200, root.GetProperty("status").GetInt32());
            Assert.AreEqual(1, root.GetProperty("attempt").GetInt32());
            Assert.IsFalse(root.GetProperty("fromCache").GetBoolean());
            StringAssert.Contains(root.GetProperty("exception").GetString(), "InvalidOperationException: boom");

            var body = Path.Combine(Path.GetDirectoryName(meta)!, root.GetProperty("body").GetString()!);
            Assert.AreEqual(Html("<h1>Boom</h1><span class='price'>1</span>"), File.ReadAllText(body));
        }

        using (var json = JsonDocument.Parse(File.ReadAllBytes(MetaFiles(folder, "item", "blocked").Single())))
        {
            Assert.AreEqual("status 403", json.RootElement.GetProperty("reason").GetString());
            Assert.IsFalse(json.RootElement.TryGetProperty("exception", out _));
        }
    }

    [TestMethod]
    public async Task LabelsAndFilter_PickWhatIsSaved()
    {
        using var folder = new TempFolder();
        var loader = Troubles().Page("https://s.test/list", "<html>captcha</html>", 403);
        var snapshots = new SnapshotStore { Path = folder.Path, Labels = { PageType.Item }, Filter = snapshot => snapshot.Kind == SnapshotKind.Blocked };
        var scraper = TroubleScraper(loader, snapshots).Route(PageType.Listing, _ => { });
        ScrapeRequest[] start = [new("https://s.test/list", PageType.Listing), .. Products("blocked", "missing", "boom")];

        await scraper.RunToListAsync(start);

        Assert.HasCount(1, MetaFiles(folder, "item", "blocked"));
        Assert.HasCount(1, folder.Files(".meta.json"));
    }

    [TestMethod]
    public async Task Limits_PerKindAndInAll()
    {
        using var folder = new TempFolder();
        var loader = new FakeLoader();
        for (var i = 0; i < 5; i++)
        {
            loader.Page($"https://s.test/p/b{i}", "<html>captcha</html>", 403).Page($"https://s.test/p/m{i}", Html("gone"), 404);
        }

        await TroubleScraper(loader, new SnapshotStore { Path = folder.Path, MaxPerKind = 2, MaxSnapshots = 3 })
            .RunToListAsync(Products("b0", "b1", "b2", "b3", "b4", "m0", "m1", "m2", "m3", "m4"));

        var blocked = MetaFiles(folder, "item", "blocked").Length;
        var missing = MetaFiles(folder, "item", "not-found").Length;
        Assert.AreEqual(3, blocked + missing);
        Assert.IsLessThanOrEqualTo(2, blocked);
        Assert.IsLessThanOrEqualTo(2, missing);
    }

    [TestMethod]
    public async Task EveryAttempt_ThatBroughtAWrongPage_IsSaved()
    {
        var loader = new FakeLoader().Responses("https://s.test/p/1",
            (403, "<html>captcha</html>"),
            (200, Html("<h1>Broken</h1>")),
            (200, Html("<h1>One</h1><span class='price'>1</span>")));
        var snapshots = new MemorySnapshots();

        var result = await TroubleScraper(loader, snapshots, maxRetries: 2).RunToListAsync(Products("1"));

        CollectionAssert.AreEqual(new[] { "One" }, result.Items.ToArray());
        var saved = snapshots.Saved.ToArray();
        Assert.HasCount(2, saved);
        Assert.AreEqual((SnapshotKind.Blocked, 1, "<html>captcha</html>"), (saved[0].Kind, saved[0].Attempt, saved[0].Text));
        Assert.AreEqual((SnapshotKind.Retry, 2), (saved[1].Kind, saved[1].Attempt));
        StringAssert.Contains(saved[1].Reason, "price");
        Assert.IsFalse(saved.Any(snapshot => snapshot.FromCache));
    }

    [TestMethod]
    public async Task HandlerRetries_AreSavedWithTheirKind()
    {
        var loader = new FakeLoader().Responses("https://s.test/p/1",
            (200, Html("<h1>Hidden</h1><span class='price'></span>")),
            (200, Html("<h1>One</h1><span class='price'>1</span>")));
        var snapshots = new MemorySnapshots();
        var scraper = new Scraper(loader, ScraperTests.FastOptions()) { SnapshotStore = snapshots }
            .Route(PageType.Item, ctx =>
            {
                var title = ctx.Html.Find(HtmlTag.H1).Text()!;
                if (title == "Hidden") throw new RetryRequestException("prices hidden for bots", blocked: true);
                ctx.Emit(title);
            });

        var result = await scraper.RunToListAsync(Products("1"));

        CollectionAssert.AreEqual(new[] { "One" }, result.Items.ToArray());
        var saved = snapshots.Saved.Single();
        Assert.AreEqual(SnapshotKind.Blocked, saved.Kind);
        Assert.AreEqual("handler: prices hidden for bots", saved.Reason);
        Assert.IsInstanceOfType<RetryRequestException>(saved.Exception);
    }

    [TestMethod]
    public async Task LoadsThatBroughtNoPage_HaveNoSnapshot()
    {
        var loader = new FakeLoader().Throws("https://s.test/p/1", new HttpRequestException("connection refused"));
        var snapshots = new MemorySnapshots();

        var result = await TroubleScraper(loader, snapshots, maxRetries: 1).RunToListAsync(Products("1"));

        Assert.HasCount(1, result.Failed);
        Assert.IsEmpty(snapshots.Saved);
    }
}
