using System.Collections.Concurrent;
using System.Text;
using z3l.FastHtmlParser;
using z3l.Scraper.Loading;
using z3l.Scraper.Validation;

namespace z3l.Scraper.Tests;

[TestClass]
public sealed class CacheTests
{
    private static string Html(string body) => ScraperTests.Html(body);

    /// <summary>A listing with two products.</summary>
    private static FakeLoader Shop() => new FakeLoader()
        .Page("https://s.test/list", Html("<a class='p' href='/p/1'>1</a><a class='p' href='/p/2'>2</a>"))
        .Page("https://s.test/p/1", Html("<h1>One</h1>"))
        .Page("https://s.test/p/2", Html("<h1>Two</h1>"));

    /// <summary>No network: every load fails.</summary>
    private static DelegateLoader Offline() => new((_, _) => throw new HttpRequestException("offline"));

    private static Scraper ShopScraper(IPageLoader loader, string? cachePath, ScraperOptions? options = null)
    {
        var scraper = new Scraper(loader, options ?? ScraperTests.FastOptions())
            .Route(PageType.Listing, ctx => ctx.Enqueue(ctx.Html.XPathValues("//a[@class='p']/@href"), PageType.Item))
            .Route(PageType.Item, ctx => ctx.Emit(ctx.Html.Find(HtmlTag.H1).Text()!));
        if (cachePath != null) scraper.CachingPageLoader = new CachingPageLoader { Path = cachePath };
        return scraper;
    }

    private static Scraper ShopScraper(IPageLoader loader, ICachingPageLoader cache)
    {
        var scraper = ShopScraper(loader, cachePath: null);
        scraper.CachingPageLoader = cache;
        return scraper;
    }

    private static ScrapeRequest List() => new("https://s.test/list", PageType.Listing);

    // ------------------------------------------------------------------ the folder

    [TestMethod]
    public async Task NextRun_TakesThePagesFromTheCache_WithoutTheLoader()
    {
        using var folder = new TempFolder();

        var first = await ShopScraper(Shop(), folder.Path).RunToListAsync(List());
        CollectionAssert.AreEquivalent(new[] { "One", "Two" }, first.Items.ToArray());
        Assert.AreEqual(3, first.Stats.Loads);
        Assert.AreEqual(0, first.Stats.FromCache);
        Assert.HasCount(3, folder.Files(".meta.json"));
        Assert.HasCount(3, folder.Files(".html"));

        var second = await ShopScraper(Offline(), folder.Path).RunToListAsync(List());
        CollectionAssert.AreEquivalent(new[] { "One", "Two" }, second.Items.ToArray());
        Assert.AreEqual(0, second.Stats.Loads);
        Assert.AreEqual(3, second.Stats.FromCache);
        Assert.IsEmpty(second.Failed);
        StringAssert.Contains(second.Stats.ToString(), "0 loads, 3 from cache");
    }

    [TestMethod]
    public async Task Loader_HearsNothingAboutKeptPages()
    {
        using var folder = new TempFolder();
        await ShopScraper(Shop(), folder.Path).RunToListAsync(List());

        var loader = Shop();
        var result = await ShopScraper(loader, folder.Path).RunToListAsync(List());

        Assert.HasCount(2, result.Items);
        Assert.IsEmpty(loader.Loaded);
        Assert.IsEmpty(loader.Good);
        Assert.IsEmpty(loader.Bad);
        Assert.IsEmpty(loader.Blocked);
    }

    [TestMethod]
    public async Task KeepsOnlyThePagesTheHandlerTook()
    {
        using var folder = new TempFolder();
        var loader = new FakeLoader()
            .Page("https://s.test/p/ok", Html("<h1>Ok</h1>"))
            .Page("https://s.test/p/blocked", "<html>captcha</html>", 403)
            .Page("https://s.test/p/missing", Html("gone"), 404)
            .Page("https://s.test/p/cut", "<html><body><h1>Cut")
            .Page("https://s.test/p/boom", Html("<h1>Boom</h1>"));
        var options = ScraperTests.FastOptions();
        options.MaxRetries = 0;
        options.NotFoundRetries = 0;
        options.Rules.Default.Content = ExpectedContent.Html;
        var scraper = new Scraper(loader, options) { CachingPageLoader = new CachingPageLoader { Path = folder.Path } }
            .Route(PageType.Item, ctx =>
            {
                var title = ctx.Html.Find(HtmlTag.H1).Text()!;
                if (title == "Boom") throw new InvalidOperationException("boom");
                ctx.Emit(title);
            });

        var result = await scraper.RunToListAsync(
            new ScrapeRequest("https://s.test/p/ok", PageType.Item),
            new ScrapeRequest("https://s.test/p/blocked", PageType.Item),
            new ScrapeRequest("https://s.test/p/missing", PageType.Item),
            new ScrapeRequest("https://s.test/p/cut", PageType.Item),
            new ScrapeRequest("https://s.test/p/boom", PageType.Item));

        CollectionAssert.AreEqual(new[] { "Ok" }, result.Items.ToArray());
        Assert.HasCount(4, result.Failed);
        Assert.HasCount(1, folder.Files(".meta.json"));
        Assert.HasCount(1, folder.Files(".html"));
    }

    [TestMethod]
    public async Task Labels_PickThePagesToKeep()
    {
        using var folder = new TempFolder();
        await ShopScraper(Shop(), new CachingPageLoader { Path = folder.Path, Labels = { PageType.Item } }).RunToListAsync(List());

        var loader = Shop();
        var result = await ShopScraper(loader, new CachingPageLoader { Path = folder.Path, Labels = { PageType.Item } }).RunToListAsync(List());

        CollectionAssert.AreEquivalent(new[] { "One", "Two" }, result.Items.ToArray());
        CollectionAssert.AreEqual(new[] { "https://s.test/list" }, loader.Loaded.ToArray());
        Assert.AreEqual(2, result.Stats.FromCache);
    }

    [TestMethod]
    public async Task Filter_DecidesWhichPagesAreKept()
    {
        using var folder = new TempFolder();
        CachingPageLoader Cache() => new() { Path = folder.Path, Filter = (request, _) => !request.Url.EndsWith("/2", StringComparison.Ordinal) };
        await ShopScraper(Shop(), Cache()).RunToListAsync(List());

        var loader = Shop();
        var result = await ShopScraper(loader, Cache()).RunToListAsync(List());

        CollectionAssert.AreEquivalent(new[] { "One", "Two" }, result.Items.ToArray());
        CollectionAssert.AreEqual(new[] { "https://s.test/p/2" }, loader.Loaded.ToArray());
        Assert.AreEqual(2, result.Stats.FromCache);
    }

    [TestMethod]
    public async Task MaxPages_CountsThePagesOfEarlierRuns()
    {
        using var folder = new TempFolder();
        await ShopScraper(Shop(), new CachingPageLoader { Path = folder.Path, MaxPages = 2 }).RunToListAsync(List());
        Assert.HasCount(2, folder.Files(".meta.json"));

        var loader = Shop();
        var result = await ShopScraper(loader, new CachingPageLoader { Path = folder.Path, MaxPages = 2 }).RunToListAsync(List());

        CollectionAssert.AreEquivalent(new[] { "One", "Two" }, result.Items.ToArray());
        Assert.AreEqual(2, result.Stats.FromCache);         // the kept pages are given back
        Assert.HasCount(1, loader.Loaded);                  // the third one is loaded
        Assert.HasCount(2, folder.Files(".meta.json"));     // and not kept: the cache is full
    }

    [TestMethod]
    public async Task MaxAge_LoadsOldPagesAgain()
    {
        using var folder = new TempFolder();
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        CachingPageLoader Cache() => new() { Path = folder.Path, MaxAge = TimeSpan.FromHours(1), Clock = clock };
        await ShopScraper(Shop(), Cache()).RunToListAsync(List());

        clock.Now += TimeSpan.FromMinutes(59);
        var young = await ShopScraper(Offline(), Cache()).RunToListAsync(List());
        Assert.AreEqual(3, young.Stats.FromCache);

        clock.Now += TimeSpan.FromMinutes(2);
        var loader = Shop();
        var old = await ShopScraper(loader, Cache()).RunToListAsync(List());
        Assert.AreEqual(0, old.Stats.FromCache);
        Assert.HasCount(3, loader.Loaded);
        Assert.HasCount(3, folder.Files(".meta.json"));     // kept again, as new pages

        var again = await ShopScraper(Offline(), Cache()).RunToListAsync(List());
        Assert.AreEqual(3, again.Stats.FromCache);
    }

    // ------------------------------------------------------------------ kept pages that are not the page

    [TestMethod]
    public async Task KeptPage_ThatFailsTheChecks_IsLoadedAgain_WithoutTakingAnAttempt()
    {
        using var folder = new TempFolder();
        await ShopScraper(Shop(), folder.Path).RunToListAsync(List());

        var loader = new FakeLoader()
            .Page("https://s.test/list", Html("<a class='p' href='/p/1'>1</a><a class='p' href='/p/2'>2</a>"))
            .Page("https://s.test/p/1", Html("<h1>One v2</h1><span class='fresh'></span>"))
            .Page("https://s.test/p/2", Html("<h1>Two v2</h1><span class='fresh'></span>"));
        var options = ScraperTests.FastOptions();
        options.MaxRetries = 0;                                   // a kept page must not use up the only attempt
        options.Rules[PageType.Item].MustContain.Add("fresh");   // the kept product pages lack it
        var snapshots = new MemorySnapshots();
        var attempts = new ConcurrentQueue<int>();
        var scraper = new Scraper(loader, options)
            {
                CachingPageLoader = new CachingPageLoader { Path = folder.Path },
                SnapshotPageLoader = snapshots,
            }
            .Route(PageType.Listing, ctx => ctx.Enqueue(ctx.Html.XPathValues("//a[@class='p']/@href"), PageType.Item))
            .Route(PageType.Item, ctx =>
            {
                attempts.Enqueue(ctx.Request.Attempts);
                ctx.Emit(ctx.Html.Find(HtmlTag.H1).Text()!);
            });

        var result = await scraper.RunToListAsync(List());

        CollectionAssert.AreEquivalent(new[] { "One v2", "Two v2" }, result.Items.ToArray());
        CollectionAssert.AreEqual(new[] { 1, 1 }, attempts.ToArray());
        Assert.IsEmpty(result.Failed);
        Assert.AreEqual(0, result.Stats.Retries);
        Assert.AreEqual(3, result.Stats.FromCache);              // the listing, and the two pages that turned out stale
        Assert.AreEqual(2, result.Stats.Loads);
        CollectionAssert.AreEquivalent(new[] { "https://s.test/p/1", "https://s.test/p/2" }, loader.Loaded.ToArray());

        // what was wrong with the kept pages
        Assert.HasCount(2, snapshots.Saved);
        Assert.IsTrue(snapshots.Saved.All(s => s.FromCache && s.Kind == SnapshotKind.Retry));

        // the fresh pages are kept in place of the stale ones
        var offline = await ShopScraper(Offline(), folder.Path).RunToListAsync(List());
        CollectionAssert.AreEquivalent(new[] { "One v2", "Two v2" }, offline.Items.ToArray());
        Assert.HasCount(3, folder.Files(".meta.json"));
    }

    [TestMethod]
    public async Task KeptPage_TheHandlerRejects_IsLoadedAgain()
    {
        using var folder = new TempFolder();
        var product = new ScrapeRequest("https://s.test/p/1", PageType.Item);
        await ShopScraper(new FakeLoader().Page("https://s.test/p/1", Html("<h1>Old</h1>")), folder.Path).RunToListAsync(product);

        var loader = new FakeLoader().Page("https://s.test/p/1", Html("<h1>New</h1>"));
        var options = ScraperTests.FastOptions();
        options.MaxRetries = 0;
        var scraper = new Scraper(loader, options) { CachingPageLoader = new CachingPageLoader { Path = folder.Path } }
            .Route(PageType.Item, ctx =>
            {
                var title = ctx.Html.Find(HtmlTag.H1).Text()!;
                if (title == "Old") throw new RetryRequestException("prices hidden for bots", blocked: true);
                ctx.Emit(title);
            });

        var result = await scraper.RunToListAsync(product);

        CollectionAssert.AreEqual(new[] { "New" }, result.Items.ToArray());
        Assert.IsEmpty(result.Failed);
        Assert.AreEqual(0, result.Stats.Blocked);                // the kept page is no answer of the site
        Assert.IsEmpty(loader.Blocked);
        Assert.IsEmpty(loader.Bad);
        CollectionAssert.AreEqual(new[] { "https://s.test/p/1" }, loader.Good.ToArray());
    }

    [TestMethod]
    public async Task KeptPages_KeepTheirText_WhateverTheEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var folder = new TempFolder();
        var html = Html("<h1>Чай и кофе</h1>");
        var online = new DelegateLoader((request, _) => Task.FromResult(request.Url.EndsWith("/browser", StringComparison.Ordinal)
            // a browser gives the text; its Content-Type still names the charset the server sent
            ? PageResponse.FromText(200, request.Url, html, "text/html; charset=windows-1251")
            : new PageResponse(200, request.Url, Encoding.GetEncoding("windows-1251").GetBytes(html), "text/html; charset=windows-1251")));
        ScrapeRequest[] pages = [new("https://s.test/browser", PageType.Item), new("https://s.test/http", PageType.Item)];

        await ShopScraper(online, folder.Path).RunToListAsync(pages);
        var result = await ShopScraper(Offline(), folder.Path).RunToListAsync(pages);

        CollectionAssert.AreEqual(new[] { "Чай и кофе", "Чай и кофе" }, result.Items.ToArray());
        Assert.AreEqual(2, result.Stats.FromCache);
    }

    // ------------------------------------------------------------------ caches of your own

    /// <summary>Keeps the pages in a dictionary, as a database would; the labels, the filter and the limits are the base class's.</summary>
    private sealed class MemoryCache : CachingPageLoader
    {
        public ConcurrentDictionary<string, CachedPage> Pages { get; } = new();

        protected override ValueTask<CachedPage?> ReadAsync(string key, ScrapeRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<CachedPage?>(Pages.TryGetValue(key, out var page) ? page : null);

        protected override ValueTask WriteAsync(CachedPage page, ScrapeRequest request, CancellationToken cancellationToken)
        {
            Pages[page.Key] = page;
            return ValueTask.CompletedTask;
        }

        protected override ValueTask<bool> DeleteAsync(string key, ScrapeRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Pages.TryRemove(key, out _));

        protected override long CountStored() => Pages.Count;
    }

    [TestMethod]
    public async Task Storage_CanBeReplaced_KeepingTheLimits()
    {
        var cache = new MemoryCache { MaxPages = 2 };
        await ShopScraper(Shop(), cache).RunToListAsync(List());
        Assert.HasCount(2, cache.Pages);

        var loader = Shop();
        var result = await ShopScraper(loader, cache).RunToListAsync(List());

        CollectionAssert.AreEquivalent(new[] { "One", "Two" }, result.Items.ToArray());
        Assert.AreEqual(2, result.Stats.FromCache);
        Assert.HasCount(1, loader.Loaded);
        Assert.HasCount(2, cache.Pages);
    }

    /// <summary>The smallest cache of your own: <see cref="CachedPage"/> takes what is needed from a response and gives it back.</summary>
    private sealed class DictionaryCache : ICachingPageLoader
    {
        public ConcurrentDictionary<string, CachedPage> Pages { get; } = new();

        public ValueTask<PageResponse?> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Pages.TryGetValue(request.Url, out var page) ? page.ToResponse() : null);

        public ValueTask StoreAsync(ScrapeRequest request, PageResponse response, CancellationToken cancellationToken)
        {
            Pages[request.Url] = CachedPage.From(request.Url, request, response, DateTimeOffset.UtcNow);
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(ScrapeRequest request, CancellationToken cancellationToken)
        {
            Pages.TryRemove(request.Url, out _);
            return ValueTask.CompletedTask;
        }
    }

    [TestMethod]
    public async Task CacheOfYourOwn_ImplementsTheInterface()
    {
        var cache = new DictionaryCache();
        await ShopScraper(Shop(), cache).RunToListAsync(List());

        var result = await ShopScraper(Offline(), cache).RunToListAsync(List());

        CollectionAssert.AreEquivalent(new[] { "One", "Two" }, result.Items.ToArray());
        Assert.AreEqual(3, result.Stats.FromCache);
    }

    [TestMethod]
    public async Task CacheAndSnapshots_CannotChangeWhileRunning()
    {
        var scraper = ShopScraper(Shop(), cachePath: null);
        await foreach (var _ in scraper.RunAsync(List()))
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => { scraper.CachingPageLoader = new CachingPageLoader { Path = "cache" }; });
            Assert.ThrowsExactly<InvalidOperationException>(() => { scraper.SnapshotPageLoader = new SnapshotPageLoader { Path = "snapshots" }; });
            break;
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
