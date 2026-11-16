using System.Diagnostics;
using System.Text.Json;
using z3l.FastHtmlParser;
using z3l.Scraper;
using z3l.Scraper.Loading;

// Usage: Z3lBench <start url> <concurrency>
var start = args[0];
var concurrency = int.Parse(args[1]);

var clock = Stopwatch.StartNew();
using var loader = new HttpPageLoader();
var scraper = new Scraper(loader, new ScraperOptions { MaxConcurrency = concurrency, ProgressInterval = null })
    .Route(PageType.Root, ctx => ctx.Enqueue(ctx.Html.XPathValues("//a[@class='catalog']/@href"), PageType.Listing))
    .Route(PageType.Listing, ctx => ctx.Enqueue(ctx.Html.XPathValues("//article[@class='product_pod']//h3/a/@href"), PageType.Item))
    .Route(PageType.Item, ctx => ctx.Emit(new Book(
        ctx.Html.Find(HtmlTag.H1).Text(),
        ctx.Html.Find(HtmlTag.P, cls: "price_color").Text(),
        ctx.Html.Find(HtmlTag.P, cls: "availability").Text(),
        ctx.Request.Url)));

var items = 0;
await foreach (Book book in scraper.RunAsync(new ScrapeRequest(start, PageType.Root)))
{
    if (book.Title is null || book.Price is null) throw new InvalidOperationException($"empty item {book.Url}");
    items++;
}
clock.Stop();

// written by hand, without reflection, so the same code runs under Native AOT
var process = Process.GetCurrentProcess();
using var output = Console.OpenStandardOutput();
using (var json = new Utf8JsonWriter(output))
{
    json.WriteStartObject();
    json.WriteNumber("items", items);
    json.WriteNumber("pages", scraper.Stats.Succeeded);
    json.WriteNumber("failed", scraper.Stats.Failed);
    json.WriteNumber("seconds", clock.Elapsed.TotalSeconds);
    json.WriteNumber("cpu_seconds", process.TotalProcessorTime.TotalSeconds);
    json.WriteNumber("peak_mb", process.PeakWorkingSet64 / 1048576.0);
    json.WriteEndObject();
}
output.Write("\n"u8);

record Book(string? Title, string? Price, string? Availability, string Url);
