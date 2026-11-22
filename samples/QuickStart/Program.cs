using Microsoft.Extensions.Logging;
using z3l.FastHtmlParser;
using z3l.Scraper;
using z3l.Scraper.Loading;

// Scrapes the first catalogue pages of books.toscrape.com, a public sandbox made for scraping practice.
// Run: dotnet run -- [listing pages] [proxy file]

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Debug));

var maxListPages = args.Length > 0 && int.TryParse(args[0], out var pages) ? pages : 2;
var proxies = args.Length > 1 ? ProxyList.FromFile(args[1], BlockPolicy.Block1Hour, loggerFactory.CreateLogger("proxies")) : null;

using var loader = new HttpPageLoader(new HttpPageLoaderOptions { Proxies = proxies });

var options = new ScraperOptions
{
    MaxConcurrency = 4,
    MaxRunDuration = TimeSpan.FromMinutes(1),
    ProgressInterval = TimeSpan.FromSeconds(1),
    RetryDelay = TimeSpan.FromMilliseconds(300),
};
options.Rules.Default.Content = z3l.Scraper.Validation.ExpectedContent.Html;
options.Rules[PageType.Item].MustContain.Add("price_color");

var scraper = new Scraper(loader, options, loggerFactory.CreateLogger("scraper"))
    .Route(PageType.Listing, ctx =>                                  // a listing needs only its link
    {
        foreach (var card in ctx.Html.FindAll(HtmlTag.Article, cls: "product_pod"))
        {
            // what the listing knows about a book goes to its page as typed data
            var rating = card.Find(HtmlTag.P, cls: "star-rating").AttributeByName("class")?.Replace("star-rating", "").Trim();
            ctx.Enqueue(card.Find(HtmlTag.H3).Find(HtmlTag.A).AttributeHref(), PageType.Item, new Teaser(rating ?? "?"));
        }

        // listing pages form a chain: page n has depth n - 1
        if (ctx.Request.Depth + 1 < maxListPages)
        {
            ctx.Enqueue(ctx.Html.XPathValue("//li[@class='next']/a/@href"), PageType.Listing);
        }
    })
    .Route<Teaser>(PageType.Item, (ctx, teaser) => ctx.Emit(new Book(
        Title: ctx.Html.Find(HtmlTag.H1).Text(),
        Price: ctx.Html.Find(HtmlTag.P, cls: "price_color").Text(),
        Rating: teaser.Rating,
        Url: ctx.Request.Url)));

var start = new[]
{
    new ScrapeRequest("https://books.toscrape.com/", PageType.Listing),
    ScrapeRequest.For("https://books.toscrape.com/catalogue/no-such-book_0/index.html", PageType.Item, new Teaser("?")),   // shows a missing page
};

await foreach (var item in scraper.RunAsync(start))
{
    if (item is Book book) Console.WriteLine($"{book.Price,8}  {book.Rating,-5}  {book.Title}");
}

Console.WriteLine();
Console.WriteLine(scraper.Stats);
if (proxies != null) Console.WriteLine(proxies);

internal sealed record Teaser(string Rating);
internal sealed record Book(string? Title, string? Price, string Rating, string Url);
