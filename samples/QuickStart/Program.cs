using Microsoft.Extensions.Logging;
using z3l.FastHtmlParser;
using z3l.Scraper;
using z3l.Scraper.Loading;

// Scrapes the first catalogue pages of books.toscrape.com, a public sandbox made for scraping practice.

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Debug));

using var loader = new HttpPageLoader();

var options = new ScraperOptions
{
    MaxConcurrency = 4,
    MaxRunDuration = TimeSpan.FromMinutes(1),
    ProgressInterval = TimeSpan.FromSeconds(1),
    RetryDelay = TimeSpan.FromMilliseconds(300),
};
options.Rules.Default.Content = z3l.Scraper.Validation.ExpectedContent.Html;
options.Rules["book"].MustContain.Add("price_color");

var maxListPages = args.Length > 0 && int.TryParse(args[0], out var pages) ? pages : 2;

var scraper = new Scraper<Book>(loader, options, loggerFactory.CreateLogger("scraper"))
    .Route("list", ctx =>
    {
        ctx.Enqueue(ctx.Html.XPathValues("//article[@class='product_pod']//h3/a/@href"), "book");

        var page = (int?)ctx.Request.UserData ?? 1;
        if (page < maxListPages)
        {
            ctx.Enqueue(ctx.Html.XPathValue("//li[@class='next']/a/@href"), "list", userData: page + 1);
        }
    })
    .Route("book", ctx =>
    {
        var html = ctx.Html;
        ctx.Emit(new Book(
            Title: html.Find(HtmlTag.H1).Text(),
            Price: html.Find(HtmlTag.P, cls: "price_color").Text(),
            Url: ctx.Request.Url));
    });

// failures are in the log; scraper.RequestFailed gives them to your code as well

var start = new[]
{
    new ScrapeRequest("https://books.toscrape.com/", "list"),
    new ScrapeRequest("https://books.toscrape.com/catalogue/no-such-book_0/index.html", "book"),   // shows a not found page
};

await foreach (var book in scraper.RunAsync(start))
{
    Console.WriteLine($"{book.Price,8}  {book.Title}");
}

Console.WriteLine();
Console.WriteLine(scraper.Stats);

internal sealed record Book(string? Title, string? Price, string Url);
