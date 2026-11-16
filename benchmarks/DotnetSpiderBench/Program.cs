using System.Diagnostics;
using System.Text.Json;
using DotnetSpider;
using DotnetSpider.DataFlow;
using DotnetSpider.DataFlow.Parser;
using DotnetSpider.Http;
using DotnetSpider.Scheduler;
using DotnetSpider.Scheduler.Component;
using DotnetSpider.Selector;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Usage: DotnetSpiderBench <start url> <concurrency>
// DotnetSpider limits requests per second (Speed, at most 500) and the requests in flight (RequestedQueueCount);
// the second is set to the concurrency, the first to its maximum. It stops only after EmptySleepTime seconds
// (1 at least) with an empty queue, so the crawl time is taken at the last page.
// Speed is kept by a token bucket of one token refilled every 1000 / Speed ms by a timer; on Windows the timer
// ticks every 15.6 ms, so whatever the settings it sends at most ~64 requests a second there.
BenchSpider.Start = args[0];
var concurrency = int.Parse(args[1]);
BenchSpider.Clock.Start();

var builder = Builder.CreateDefaultBuilder<BenchSpider>(options =>
{
    options.Speed = 500;
    options.RequestedQueueCount = concurrency;
    options.EmptySleepTime = 1;
    options.RetriedTimes = 3;
});
builder.UseQueueDistinctBfsScheduler<HashSetDuplicateRemover>();
builder.ConfigureLogging(b => b.SetMinimumLevel(LogLevel.Warning));
await builder.Build().RunAsync();

var process = Process.GetCurrentProcess();
Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object>
{
    ["items"] = BenchParser.Items,
    ["pages"] = BenchParser.Pages,
    ["failed"] = BenchSpider.Failed,
    ["seconds"] = BenchParser.LastPage.TotalSeconds,
    ["cpu_seconds"] = process.TotalProcessorTime.TotalSeconds,
    ["peak_mb"] = process.PeakWorkingSet64 / 1048576.0,
}));

public class BenchSpider(IOptions<SpiderOptions> options, DependenceServices services, ILogger<Spider> logger)
    : Spider(options, services, logger)
{
    public static string Start = "";
    public static readonly Stopwatch Clock = new();
    public static int Failed;

    protected override async Task InitializeAsync(CancellationToken stoppingToken = default)
    {
        OnRequestError += (request, response) =>
        {
            if (Interlocked.Increment(ref Failed) == 1 && Environment.GetEnvironmentVariable("BENCH_DEBUG") != null)
                Console.Error.WriteLine($"{request.RequestUri} {response?.StatusCode} {response?.ReasonPhrase} {response?.Agent} {request.Downloader} {request.Policy}");
        };
        AddDataFlow<BenchParser>();
        await AddRequestsAsync(new Request(Start));
    }
}

public class BenchParser : DataParser
{
    public static int Items, Pages;
    private static long _lastPageTicks;
    public static TimeSpan LastPage => TimeSpan.FromTicks(Interlocked.Read(ref _lastPageTicks));

    public override Task InitializeAsync()
    {
        // the built-in builder resolves relative links against the site root ("catalog/1" -> "/catalog/1");
        // given the page URL instead, they resolve the way a browser does
        SelectableBuilder = context => new HtmlSelectable(context.Response.ReadAsString(), context.Request.RequestUri.ToString(), false);
        return Task.CompletedTask;
    }

    protected override Task ParseAsync(DataFlowContext context)
    {
        Interlocked.Increment(ref Pages);
        var uri = context.Request.RequestUri;
        var html = context.Selectable;

        if (uri.AbsolutePath.Contains("/item/"))
        {
            var title = html.Select(Selectors.XPath("//h1"))?.Value;
            var price = html.Select(Selectors.XPath("//p[@class='price_color']"))?.Value;
            var availability = html.Select(Selectors.XPath("//p[@class='availability']"))?.Value;
            if (title is null || price is null || availability is null) throw new InvalidOperationException($"empty item {uri}");
            Interlocked.Increment(ref Items);
        }
        else
        {
            var xpath = uri.AbsolutePath.Contains("/catalog/") ? "//article[@class='product_pod']//h3/a/@href" : "//a[@class='catalog']/@href";
            foreach (var link in html.SelectList(Selectors.XPath(xpath)))
                context.AddFollowRequests(context.CreateNewRequest(new Uri(uri, link.Value)));
        }

        var now = BenchSpider.Clock.Elapsed.Ticks;
        long seen;
        while ((seen = Interlocked.Read(ref _lastPageTicks)) < now && Interlocked.CompareExchange(ref _lastPageTicks, now, seen) != seen) { }
        return Task.CompletedTask;
    }
}
