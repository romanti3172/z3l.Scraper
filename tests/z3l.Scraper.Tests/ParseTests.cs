using z3l.FastHtmlParser;
using z3l.Scraper.Validation;

namespace z3l.Scraper.Tests;

/// <summary>Scraper.ParseAsync: one page through its handler; the pages it queues are kept, not loaded.</summary>
[TestClass]
public sealed class ParseTests
{
    private const string Catalog = "https://s.test/catalog";
    private const string Lamp = "https://s.test/item/lamp";

    private sealed record Teaser(int Price);

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Listing_TheFoundPagesAreKept_NotLoaded()
    {
        var loader = new FakeLoader().Page(Catalog, ScraperTests.Html("catalog"));
        var returned = new List<bool>();
        var scraper = new Scraper(loader, ScraperTests.FastOptions())
            .Route(PageType.Listing, ctx =>
            {
                ctx.Emit("listing");
                returned.Add(ctx.Enqueue("/item/1", PageType.Item));                         // relative: resolved against the page
                returned.Add(ctx.Enqueue("https://s.test/item/2", PageType.Item, new Teaser(5)));
                returned.Add(ctx.Enqueue("/item/1", PageType.Item));                         // a duplicate
                returned.Add(ctx.Enqueue("mailto:shop@s.test", PageType.Item));              // not http(s)
                returned.Add(ctx.Enqueue(Catalog, PageType.Listing));                        // the page itself
                returned.Add(ctx.Enqueue("/catalog?page=2", PageType.Listing));
            })
            .Route(PageType.Item, ctx => ctx.Emit("item"));

        var page = await scraper.ParseAsync(Catalog, PageType.Listing);

        Assert.IsTrue(page.Succeeded);
        Assert.AreEqual(ValidationOutcome.Ok, page.Validation.Outcome);
        CollectionAssert.AreEqual(new[] { true, true, false, false, false, true }, returned, "Enqueue returns what a run returns");
        CollectionAssert.AreEqual(new[] { "listing" }, page.Items.ToArray());
        CollectionAssert.AreEqual(new[] { "https://s.test/item/1", "https://s.test/item/2", "https://s.test/catalog?page=2" },
            page.Found.Select(r => r.Url).ToArray());
        CollectionAssert.AreEqual(new[] { "Item", "Item", "Listing" }, page.Found.Select(r => r.Label).ToArray());
        Assert.AreEqual(new Teaser(5), page.Found[1].Data);
        Assert.IsTrue(page.Found.All(r => r.Depth == 1));
        CollectionAssert.AreEqual(new[] { Catalog }, loader.Loaded.ToArray(), "only the page itself is loaded");
        Assert.AreEqual(200, page.Response.StatusCode);
        StringAssert.Contains(page.Response.Text, "catalog");
        Assert.AreEqual(1, page.Request.Attempts);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ChecksFail_TheHandlerDoesNotRun_AndTheLoaderHearsNothing()
    {
        var options = ScraperTests.FastOptions();
        options.Rules.Default.BlockedMarkers.Add("Just a moment");
        var loader = new FakeLoader().Page(Catalog, ScraperTests.Html("Just a moment..."));
        var handled = false;
        var scraper = new Scraper(loader, options).Route(PageType.Listing, _ => handled = true);

        var page = await scraper.ParseAsync(Catalog, PageType.Listing);

        Assert.IsFalse(page.Succeeded);
        Assert.AreEqual(ValidationOutcome.Blocked, page.Validation.Outcome);
        Assert.IsFalse(handled);
        Assert.IsEmpty(page.Items);
        Assert.IsEmpty(page.Found);
        Assert.HasCount(1, loader.Loaded, "one attempt, no retries");
        Assert.IsEmpty(loader.Blocked);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task HandlerThrows_ItsItemsAndFoundPagesStay_WithTheException()
    {
        var loader = new FakeLoader().Page(Catalog, ScraperTests.Html("catalog"));
        var scraper = new Scraper(loader, ScraperTests.FastOptions())
            .Route(PageType.Listing, ctx =>
            {
                ctx.Emit("first");
                ctx.Enqueue("/item/1", PageType.Listing);
                throw new InvalidOperationException("broken selector");
            });

        var page = await scraper.ParseAsync(Catalog, PageType.Listing);

        Assert.IsFalse(page.Succeeded);
        Assert.AreEqual(ValidationOutcome.Ok, page.Validation.Outcome);
        Assert.IsInstanceOfType<InvalidOperationException>(page.Exception);
        Assert.AreEqual("broken selector", page.Exception?.Message);
        CollectionAssert.AreEqual(new[] { "first" }, page.Items.ToArray());
        Assert.HasCount(1, page.Found);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task RetryRequest_IsInTheResult_AndTheLoaderHearsNothing()
    {
        var loader = new FakeLoader().Page(Lamp, ScraperTests.Html("lamp"));
        var scraper = new Scraper(loader, ScraperTests.FastOptions())
            .Route(PageType.Item, _ => throw new RetryRequestException("prices hidden for bots", blocked: true));

        var page = await scraper.ParseAsync(Lamp, PageType.Item);

        Assert.IsFalse(page.Succeeded);
        Assert.IsInstanceOfType<RetryRequestException>(page.Exception);
        Assert.IsEmpty(loader.Blocked);
        Assert.HasCount(1, loader.Loaded);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task PageAtHand_IsParsedWithoutTheLoader()
    {
        var loader = new DelegateLoader((_, _) => throw new InvalidOperationException("the loader is not called"));
        var scraper = new Scraper(loader, ScraperTests.FastOptions())
            .Route(PageType.Item, ctx => ctx.Emit(ctx.Html.Find(HtmlTag.H1).Text()!))
            .Route<Teaser>(PageType.ItemDetails, (ctx, teaser) => ctx.Emit(teaser.Price));
        var saved = PageResponse.FromText(200, Lamp, ScraperTests.Html("<h1>Lamp</h1>"));

        var page = await scraper.ParseAsync(saved, PageType.Item);
        var typed = await scraper.ParseAsync(ScrapeRequest.For(Lamp, PageType.ItemDetails, new Teaser(7)), saved);

        CollectionAssert.AreEqual(new[] { "Lamp" }, page.Items.ToArray());
        Assert.AreSame(saved, page.Response);
        CollectionAssert.AreEqual(new[] { 7 }, typed.ItemsOf<int>().ToArray());
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task PageAtHand_IsCheckedWithTheRulesOfItsLabel()
    {
        var options = ScraperTests.FastOptions();
        options.Rules[PageType.Item].MustContain.Add("class='price'");
        var scraper = new Scraper(new FakeLoader(), options).Route(PageType.Item, ctx => ctx.Emit("item"));

        var page = await scraper.ParseAsync(PageResponse.FromText(200, Lamp, ScraperTests.Html("<h1>Lamp</h1>")), PageType.Item);

        Assert.AreEqual(ValidationOutcome.Retry, page.Validation.Outcome);
        StringAssert.Contains(page.Validation.Reason ?? "", "price");
        Assert.IsEmpty(page.Items);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Mistakes_Throw()
    {
        var scraper = new Scraper(new FakeLoader(), ScraperTests.FastOptions())
            .Route<Teaser>(PageType.ItemDetails, (ctx, teaser) => ctx.Emit(teaser));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scraper.ParseAsync(Lamp, "nobody"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scraper.ParseAsync(Lamp, PageType.ItemDetails));   // no data
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => scraper.ParseAsync("not a url", PageType.ItemDetails));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task NoResponseInTime_ThrowsTimeoutException_AndCancellationCancels()
    {
        var options = ScraperTests.FastOptions();
        options.RequestTimeout = TimeSpan.FromMilliseconds(50);
        var loader = new DelegateLoader(async (r, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return PageResponse.FromText(200, r.Url, "never");
        });
        var scraper = new Scraper(loader, options).Route(PageType.Item, ctx => ctx.Emit("item"));

        await Assert.ThrowsExactlyAsync<TimeoutException>(() => scraper.ParseAsync(Lamp, PageType.Item));

        options.RequestTimeout = Timeout.InfiniteTimeSpan;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<OperationCanceledException>(() => scraper.ParseAsync(Lamp, PageType.Item, cancel.Token));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task GoesWhileARunGoes_AndStaysOutOfItsStats()
    {
        var release = new TaskCompletionSource();
        var loader = new FakeLoader().Page(Catalog, ScraperTests.Html("catalog")).Page(Lamp, ScraperTests.Html("lamp"));
        var scraper = new Scraper(loader, ScraperTests.FastOptions())
            .Route(PageType.Listing, async ctx =>
            {
                await release.Task;
                ctx.Emit("listing");
            })
            .Route(PageType.Item, ctx => ctx.Emit("item"));

        var run = scraper.RunToListAsync(new ScrapeRequest(Catalog, PageType.Listing));
        var page = await scraper.ParseAsync(Lamp, PageType.Item);
        release.SetResult();
        var result = await run;

        CollectionAssert.AreEqual(new[] { "item" }, page.Items.ToArray());
        CollectionAssert.AreEqual(new[] { "listing" }, result.Items.ToArray());
        Assert.AreEqual(1, result.Stats.Loads, "the parse is no load of the run");
    }
}
