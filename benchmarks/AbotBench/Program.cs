using System.Diagnostics;
using System.Text.Json;
using Abot2.Crawler;
using Abot2.Poco;

// Usage: AbotBench <start url> <concurrency>
// Abot follows every link it finds; the decision makers keep it on the same pages the other scrapers visit.
var start = new Uri(args[0]);
var concurrency = int.Parse(args[1]);

var clock = Stopwatch.StartNew();
var config = new CrawlConfiguration
{
    MaxConcurrentThreads = concurrency,
    MaxPagesToCrawl = 0,
    MaxPagesToCrawlPerDomain = 0,
    MaxCrawlDepth = 10,
    MinCrawlDelayPerDomainMilliSeconds = 0,
    IsRespectRobotsDotTextEnabled = false,
    IsExternalPageCrawlingEnabled = false,
    HttpRequestTimeoutInSeconds = 60,
};

var items = 0;
var pages = 0;
var failed = 0;
var basePath = start.AbsolutePath;

var crawler = new PoliteWebCrawler(config);
crawler.ShouldCrawlPageDecisionMaker = (page, _) => new CrawlDecision
{
    Allow = page.Uri.AbsolutePath.StartsWith(basePath, StringComparison.Ordinal),
    Reason = "outside the shop",
};
crawler.ShouldCrawlPageLinksDecisionMaker = (page, _) => new CrawlDecision
{
    Allow = !page.Uri.AbsolutePath.Contains("/item/"),
    Reason = "item pages are leaves",
};
crawler.PageCrawlCompleted += (_, e) =>
{
    var page = e.CrawledPage;
    if (page.HttpRequestException != null || page.HttpResponseMessage?.IsSuccessStatusCode != true)
    {
        Interlocked.Increment(ref failed);
        return;
    }
    Interlocked.Increment(ref pages);
    if (!page.Uri.AbsolutePath.Contains("/item/")) return;

    var doc = page.AngleSharpHtmlDocument;
    var title = doc.QuerySelector("h1")?.TextContent;
    var price = doc.QuerySelector("p.price_color")?.TextContent;
    var availability = doc.QuerySelector("p.availability")?.TextContent;
    if (title is null || price is null || availability is null) throw new InvalidOperationException($"empty item {page.Uri}");
    Interlocked.Increment(ref items);
};

await crawler.CrawlAsync(start);
clock.Stop();

var process = Process.GetCurrentProcess();
Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object>
{
    ["items"] = items,
    ["pages"] = pages,
    ["failed"] = failed,
    ["seconds"] = clock.Elapsed.TotalSeconds,
    ["cpu_seconds"] = process.TotalProcessorTime.TotalSeconds,
    ["peak_mb"] = process.PeakWorkingSet64 / 1048576.0,
}));
