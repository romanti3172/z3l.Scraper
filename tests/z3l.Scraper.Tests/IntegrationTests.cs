using z3l.FastHtmlParser;
using z3l.Scraper.Loading;
using z3l.Scraper.Validation;

namespace z3l.Scraper.Tests;

/// <summary>The scraper with the real <see cref="HttpPageLoader"/> against a local Kestrel shop.</summary>
[TestClass]
public sealed class IntegrationTests
{
    private sealed record Product(int Id, string? Name, string? Price, string Url);
    private sealed record Stock(int Id, int Count);
    private sealed record StockDto(int Id, int Stock);

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ScrapesShop_ThroughEveryKindOfTrouble()
    {
        await using var site = await TestSite.StartAsync();
        using var loader = new HttpPageLoader();

        var options = new ScraperOptions
        {
            MaxConcurrency = 4,
            MaxRetries = 3,
            RetryDelay = TimeSpan.FromMilliseconds(50),
            RequestTimeout = TimeSpan.FromSeconds(1),
            ProgressInterval = null,
            MaxThrottleDelay = TimeSpan.FromMilliseconds(100),   // the 429 and 503 throttle the shop; keep the pauses short
        };
        options.Rules.Default.Content = ExpectedContent.Html;
        options.Rules.Default.BlockedMarkers.Add("Just a moment");
        options.Rules["stock"].Content = ExpectedContent.Json;

        var scraper = new Scraper(loader, options)
            .Route("catalog", ctx =>
            {
                ctx.Enqueue(ctx.Html.XPathValues("//a[not(@class='next')]/@href"), "product");
                ctx.Enqueue(ctx.Html.XPathValue("//a[@class='next']/@href"), "catalog");
            })
            .Route("product", ctx =>
            {
                var card = ctx.Html.Find(HtmlTag.Div, cls: "product");
                var price = card.Find(HtmlTag.Span, cls: "price").Text();
                if (price is null) throw new RetryRequestException("no price: soft block");

                var id = int.Parse(card.AttributeByName("data-id")!);
                ctx.Enqueue(new ScrapeRequest(site.Url("/api/stock"), "stock") { Method = HttpMethod.Post, Body = $"id={id}" });
                ctx.Emit(new Product(id, card.Find(HtmlTag.H1).Text(), price, ctx.Response.Url));
            })
            .Route("stock", ctx =>
            {
                var dto = ctx.Json<StockDto>()!;
                ctx.Emit(new Stock(dto.Id, dto.Stock));
            });

        var result = await scraper.RunToListAsync(new ScrapeRequest(site.Url("/catalog?page=1"), "catalog"));

        var products = result.Items.OfType<Product>().OrderBy(p => p.Id).ToList();
        var stocks = result.Items.OfType<Stock>().OrderBy(s => s.Id).ToList();

        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6, 8, 9, 10, 11, 12 }, products.Select(p => p.Id).ToArray());
        Assert.AreEqual("Чай", products.Single(p => p.Id == 8).Name);                       // windows-1251
        Assert.AreEqual("99,00", products.Single(p => p.Id == 8).Price);
        Assert.AreEqual(site.Url("/p/9/new"), products.Single(p => p.Id == 9).Url);          // redirect: the final URL
        Assert.AreEqual("Product 11", products.Single(p => p.Id == 11).Name);                // gzip
        CollectionAssert.AreEqual(products.Select(p => p.Id * 10).ToArray(), stocks.Select(s => s.Count).ToArray());

        Assert.HasCount(1, result.Failed);
        Assert.AreEqual(FailureReason.NotFound, result.Failed[0].Reason);
        StringAssert.EndsWith(result.Failed[0].Request.Url, "/p/7");

        var stats = result.Stats;
        Assert.AreEqual(26, stats.Enqueued, stats.ToString());            // 3 catalog pages + 12 products + 11 stock calls
        Assert.AreEqual(25, stats.Succeeded, stats.ToString());
        Assert.AreEqual(2, stats.Duplicates, stats.ToString());           // "#top" and the second link to /p/4
        Assert.AreEqual(7, stats.Retries, stats.ToString());              // p3 500, p4 cut, p5 429, p6 503, p7 404, p10 timeout, p12 soft block
        Assert.AreEqual(2, stats.Blocked, stats.ToString());              // p5 429, p6 "Just a moment"
        Assert.AreEqual(33, stats.Loads, stats.ToString());
        Assert.AreEqual(0, stats.Pending, stats.ToString());

        var p5 = site.HitTimes("/p/5");
        Assert.HasCount(2, p5);
        Assert.IsGreaterThanOrEqualTo(900, (p5[1] - p5[0]).TotalMilliseconds);   // Retry-After: 1
        Assert.AreEqual(1, site.Hits("/catalog?page=1"));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task HttpPageLoader_SendsHeadersAndBodies_KeepsCookies_AndLimitsSize()
    {
        await using var site = await TestSite.StartAsync();
        var loaderOptions = new HttpPageLoaderOptions { MaxResponseBytes = 1024 * 1024 };
        loaderOptions.DefaultHeaders["Accept-Language"] = "de-DE";
        using var loader = new HttpPageLoader(loaderOptions);

        var scraper = new Scraper(loader, ScraperTests.FastOptions())
            .Route("default", ctx => ctx.Emit(ctx.Html.Find(HtmlTag.Body).Text()!))
            .Route("cookie", ctx =>
            {
                ctx.Enqueue(site.Url("/cookie/get"));       // the cookie set by this page must be sent with the next one
            })
            .Route("binary", ctx => ctx.Emit($"bytes={ctx.Response.Body.Length}"))
            .Route("text", ctx => ctx.Emit(ctx.Response.Text));

        var result = await scraper.RunToListAsync(
            new ScrapeRequest(site.Url("/echo")) { Headers = new Dictionary<string, string> { ["X-Test"] = "42" } },
            new ScrapeRequest(site.Url("/echo"), "default") { Method = HttpMethod.Post, Body = """{"q":1}""", ContentType = "application/json" },
            new ScrapeRequest(site.Url("/cookie/set"), "cookie"),
            new ScrapeRequest(site.Url("/big"), "binary"),
            new ScrapeRequest(site.Url("/latin1"), "text"));

        var items = result.ItemsOf<string>().ToList();
        Assert.IsTrue(items.Any(i => i.Contains("x=42") && i.StartsWith("ua=Mozilla/5.0") && i.Contains("lang=de-DE")), string.Join("\n", items));
        Assert.IsTrue(items.Any(i => i.Contains("ct=application/json") && i.Contains("body={\"q\":1}")), string.Join("\n", items));
        CollectionAssert.Contains(items, "session=abc123");
        CollectionAssert.Contains(items, "10€");

        var big = result.Failed.Single();
        Assert.AreEqual(FailureReason.NonRetryable, big.Reason);
        Assert.AreEqual(1, site.Hits("/big"));       // no retries of a response that is too big
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task HttpPageLoader_ConnectionRefused_IsRetriedThenFails()
    {
        // a port nobody listens on: take a free one from a site and stop it
        string url;
        await using (var site = await TestSite.StartAsync()) url = site.Url("/p/1");
        using var loader = new HttpPageLoader();
        var options = ScraperTests.FastOptions();
        options.MaxRetries = 1;
        var scraper = new Scraper(loader, options).Route("default", _ => { });

        var result = await scraper.RunToListAsync(url);

        Assert.AreEqual(FailureReason.RetriesExhausted, result.Failed[0].Reason);
        Assert.AreEqual(2, result.Stats.Loads);
        StringAssert.Contains(result.Failed[0].Message, "load error");
    }
}
