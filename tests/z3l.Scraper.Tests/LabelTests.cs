using z3l.FastHtmlParser;
using z3l.Scraper.Validation;

namespace z3l.Scraper.Tests;

/// <summary>Labels as page types or strings, typed page data, items of any type.</summary>
[TestClass]
public sealed class LabelTests
{
    private sealed record Teaser(string Price);
    private sealed record Product(string? Name, string Price);
    private sealed record Review(string Text);

    private static DelegateLoader Shop() => new((r, _) => Task.FromResult(PageResponse.FromText(200, r.Url, r.Url.Contains("/list")
        ? ScraperTests.Html("<div class='card'><a href='/p/1'>1</a><span>10.00</span></div><div class='card'><a href='/p/2'>2</a><span>20.00</span></div>")
        : ScraperTests.Html($"<h1>{r.Url[^3..]}</h1>"))));

    [TestMethod]
    public void Label_FromPageTypeOrString_IgnoresCase()
    {
        ScrapeLabel fromType = PageType.ItemDetails;
        ScrapeLabel fromString = "itemdetails";

        Assert.AreEqual(fromType, fromString);
        Assert.IsTrue(fromType == fromString);
        Assert.AreEqual("ItemDetails", fromType.Name);
        Assert.AreEqual("default", default(ScrapeLabel).Name);
        Assert.AreEqual("Listing", new ScrapeRequest("https://s.test/", PageType.Listing).Label);
        Assert.AreEqual(ScrapeRequest.DefaultLabel, new ScrapeRequest("https://s.test/").Label);
    }

    [TestMethod]
    public async Task ListingTakesOnlyALink_ItemGetsItsDataTyped()
    {
        var scraper = new Scraper(Shop(), ScraperTests.FastOptions())
            .Route(PageType.Listing, ctx =>
            {
                foreach (var card in ctx.Html.FindAll(HtmlTag.Div, cls: "card"))
                {
                    ctx.Enqueue(card.Find(HtmlTag.A).AttributeHref(), PageType.Item, new Teaser(card.Find(HtmlTag.Span).Text()!));
                }
            })
            .Route<Teaser>(PageType.Item, (ctx, teaser) =>
            {
                ctx.Emit(new Product(ctx.Html.Find(HtmlTag.H1).Text(), teaser.Price));
                ctx.Emit(new Review($"review of {ctx.Request.Url[^3..]}"));
            });

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/list", PageType.Listing));

        CollectionAssert.AreEquivalent(new[] { new Product("p/1", "10.00"), new Product("p/2", "20.00") }, result.ItemsOf<Product>().ToArray());
        Assert.HasCount(2, result.ItemsOf<Review>());
        Assert.HasCount(4, result.Items);
    }

    [TestMethod]
    public async Task PageTypeAndStringLabels_ReachTheSameHandler()
    {
        var scraper = new Scraper(Shop(), ScraperTests.FastOptions())
            .Route(PageType.Item, ctx => ctx.Emit(ctx.Request.Label));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/p/1", "item"), new ScrapeRequest("https://s.test/p/2", PageType.Item));

        Assert.HasCount(2, result.Items);
    }

    [TestMethod]
    public async Task WrongData_FailsThePageThatQueuedIt()
    {
        var scraper = new Scraper(Shop(), ScraperTests.FastOptions())
            .Route(PageType.Listing, ctx => ctx.Enqueue("/p/1", PageType.Item, "a string instead of a teaser"))
            .Route<Teaser>(PageType.Item, (ctx, _) => ctx.Emit("must not run"));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/list", PageType.Listing));

        var failed = result.Failed.Single();
        Assert.AreEqual("https://s.test/list", failed.Request.Url);
        Assert.AreEqual(FailureReason.HandlerError, failed.Reason);
        StringAssert.Contains(failed.Message, "Teaser");
        Assert.IsEmpty(result.Items);
    }

    [TestMethod]
    public async Task TypedHandler_StartRequestWithoutData_Throws()
    {
        var scraper = new Scraper(Shop(), ScraperTests.FastOptions()).Route<Teaser>(PageType.Item, (_, _) => { });

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scraper.RunToListAsync(new ScrapeRequest("https://s.test/p/1", PageType.Item)));
        StringAssert.Contains(error.Message, "has none");

        var ok = await scraper.RunToListAsync(ScrapeRequest.For("https://s.test/p/1", PageType.Item, new Teaser("1.00")));
        Assert.AreEqual(1, ok.Stats.Succeeded);
    }

    [TestMethod]
    public async Task PlainTypedData_PageNumbers()
    {
        // a listing that knows its page number, up to page 3
        var loader = new DelegateLoader((r, _) => Task.FromResult(PageResponse.FromText(200, r.Url, ScraperTests.Html("<a rel='next' href='?page=next'>next</a>"))));
        var scraper = new Scraper(loader, ScraperTests.FastOptions()).Route<int>(PageType.Listing, (ctx, page) =>
        {
            ctx.Emit(page);
            if (page < 3) ctx.Enqueue($"/list?page={page + 1}", PageType.Listing, page + 1);
        });

        var result = await scraper.RunToListAsync(ScrapeRequest.For("https://s.test/list?page=1", PageType.Listing, 1));

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, result.ItemsOf<int>().Order().ToArray());
    }

    [TestMethod]
    public async Task FileAndApi_HaveTheirOwnChecks_EvenWithAnHtmlDefault()
    {
        var bodies = new Dictionary<string, (string Type, byte[] Body)>
        {
            ["https://s.test/1.jpg"] = ("image/jpeg", [0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0xD9]),
            ["https://s.test/api/ok"] = ("application/json", "{\"ok\":true}"u8.ToArray()),
            ["https://s.test/api/html"] = ("text/html", "<html><body>login</body></html>"u8.ToArray()),
        };
        var loader = new DelegateLoader((r, _) => Task.FromResult(new PageResponse(200, r.Url, bodies[r.Url].Body, bodies[r.Url].Type)));
        var options = ScraperTests.FastOptions();
        options.Rules.Default.Content = ExpectedContent.Html;
        var scraper = new Scraper(loader, options)
            .Route(PageType.File, ctx => ctx.Emit(ctx.Response.Body.Length))
            .Route(PageType.Api, ctx => ctx.Emit(ctx.Response.Text));

        var result = await scraper.RunToListAsync(
            new ScrapeRequest("https://s.test/1.jpg", PageType.File),
            new ScrapeRequest("https://s.test/api/ok", PageType.Api),
            new ScrapeRequest("https://s.test/api/html", PageType.Api));

        CollectionAssert.AreEquivalent(new object[] { 6, "{\"ok\":true}" }, result.Items.ToArray());
        StringAssert.Contains(result.Failed.Single().Message, "not json");
    }

    [TestMethod]
    public async Task OwnLabelRules_StillWinOverThePageTypeDefault()
    {
        var loader = new DelegateLoader((r, _) => Task.FromResult(PageResponse.FromText(200, r.Url, "plain text answer")));
        var options = ScraperTests.FastOptions();
        options.Rules[PageType.Api].Content = ExpectedContent.Any;
        var scraper = new Scraper(loader, options).Route(PageType.Api, ctx => ctx.Emit(ctx.Response.Text));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/api", PageType.Api));

        Assert.HasCount(1, result.Items);
    }
}
