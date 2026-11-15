# z3l.Scraper

Block-aware web scraping engine for .NET 9 and .NET 10.

You write what to take from a page; the scraper takes care of the rest: a request queue with priorities and
de-duplication, parallel loading, retries with backoff, recognising blocked, missing and cut-off pages,
time limits, and streaming typed results while the scraping goes on. HTML is parsed with
[z3l.FastHtmlParser](https://www.nuget.org/packages/z3l.FastHtmlParser).

- **Label routing.** Each request has a label (`"list"`, `"product"`); each label has its own async handler.
- **Response checks.** Blocked (403/429, captcha, "Just a moment..."), not found (404, "Product not found"),
  incomplete (`</html>` missing, not JSON, a required part missing): configured per label, retried automatically.
- **Retries with backoff and jitter**, a per-request timeout, a time limit for the whole run.
- **Streaming results.** `await foreach` over items as they are scraped; a slow consumer slows the scraper down
  instead of filling the memory.
- **De-duplication** by method, URL (without fragment) and body: POST pages with different bodies are different pages.
- **Pluggable loaders.** `HttpPageLoader` is built in; implement `IPageLoader` for a browser, a proxy pool or a
  loader farm. `IBlockAwarePageLoader` is told which responses were blocked, so it can retire a proxy or a session.

## Install

```
dotnet add package z3l.Scraper
```

## Quick start

```csharp
using z3l.FastHtmlParser;
using z3l.Scraper;
using z3l.Scraper.Loading;

using var loader = new HttpPageLoader();

var scraper = new Scraper<Book>(loader)
    .Route("list", ctx =>
    {
        ctx.Enqueue(ctx.Html.XPathValues("//article[@class='product_pod']//h3/a/@href"), "book");
        ctx.Enqueue(ctx.Html.XPathValue("//li[@class='next']/a/@href"), "list");      // next page, null is ignored
    })
    .Route("book", async ctx =>
    {
        var html = ctx.Html;
        await ctx.EmitAsync(new Book(
            Title: html.Find(HtmlTag.H1).Text(),
            Price: html.Find(HtmlTag.P, cls: "price_color").Text(),
            Url: ctx.Request.Url));
    });

await foreach (var book in scraper.RunAsync(new ScrapeRequest("https://books.toscrape.com/", "list")))
{
    Console.WriteLine(book);
}
Console.WriteLine(scraper.Stats);

record Book(string? Title, string? Price, string Url);
```

Relative links are resolved against the page; links that are not http(s) (`mailto:`, `javascript:`) are skipped;
pages already queued are skipped.

## Requests

```csharp
new ScrapeRequest("https://shop.example/catalog", "list");
"https://shop.example/";                                   // a string is a request with the "default" label

new ScrapeRequest("https://shop.example/api/search", "search")
{
    Method = HttpMethod.Post,
    Body = """{"page":2}""",
    ContentType = "application/json",
    Headers = new Dictionary<string, string> { ["X-Requested-With"] = "XMLHttpRequest" },
    Priority = ScrapePriority.High,
    UserData = category,                                   // available in the handler as ctx.Request.UserData
    MaxRetries = 10,
};
```

## Handlers

```csharp
scraper.Route("product", async ctx =>
{
    ctx.Request;          // the request (Url, Label, UserData, Attempts...)
    ctx.Response;         // StatusCode, Url after redirects, Body, Text, Headers
    ctx.Html;             // parsed page (z3l.FastHtmlParser), made on first use
    ctx.Json<Product>();  // body as JSON
    ctx.Enqueue(url, "label", userData);
    await ctx.EmitAsync(item);
});
scraper.FallbackRoute(ctx => ...);    // labels without a handler of their own
```

A label without a handler is an error at once (`InvalidOperationException`), not a silent skip.
If a handler throws, the request fails with `FailureReason.HandlerError`
(set `Options.RetryOnHandlerError` to retry it); items it emitted before the exception are kept.

## Response checks

```csharp
var options = new ScraperOptions();

// for all labels
options.Rules.Default.Content = ExpectedContent.Html;                 // a cut-off page is retried
options.Rules.Default.BlockedMarkers.AddRange(["Just a moment...", "captcha"]);
options.Rules.Default.BlockedStatusCodes.Add(503);                    // 403 and 429 are there already

// for one label, added to the default rules
options.Rules["product"].MustContain.Add("itemprop=\"price\"");
options.Rules["product"].NotFoundMarkers.Add("Product not found");
options.Rules["api"].Content = ExpectedContent.Json;
options.Rules["image"].Content = ExpectedContent.Binary;              // no text checks

// your own check
options.Rules["product"].Custom = (request, response) =>
    response.Text.Length < 5000 ? ValidationResult.Retry("page too short") : ValidationResult.Ok;
```

| Verdict | When | What happens |
|---|---|---|
| Blocked | status in `BlockedStatusCodes`, a `BlockedMarkers` string | the loader is told, retried |
| NotFound | 404 / 410, a `NotFoundMarkers` string | retried `NotFoundRetries` times (1), then failed |
| Retry | other non-2xx, empty body, wrong format, `MustContain` / `MustNotContain`, load error, timeout | retried up to `MaxRetries` (5), then failed |

String checks ignore case.

## Options

| Option | Default | |
|---|---|---|
| `MaxConcurrency` | 8 | requests loaded and handled at once |
| `MaxRetries` | 5 | retries after the first attempt |
| `NotFoundRetries` | 1 | retries of a page that looks missing |
| `RetryDelay`, `RetryBackoffFactor`, `MaxRetryDelay` | 1 s, ×2, 1 min | delay before retry *n* is `RetryDelay × factorⁿ⁻¹` ±20 % |
| `RequestTimeout` | 2 min | a slower load is cancelled and retried |
| `MaxRunDuration` | none | stop and return what was scraped |
| `ItemBufferSize` | 1000 | items waiting for the consumer before handlers wait |
| `ProgressInterval` | 30 s | progress line in the log |

## Results

```csharp
// stream
await foreach (var item in scraper.RunAsync(requests, cancellationToken)) { ... }

// or collect
var result = await scraper.RunToListAsync(requests);
result.Items;    // emitted items
result.Failed;   // FailedRequest: Request, Reason (RetriesExhausted / NotFound / HandlerError), Message, StatusCode, Exception
result.Stats;    // counters

scraper.RequestFailed += failed => logger.LogWarning("{Url}: {Message}", failed.Request.Url, failed.Message);
```

`scraper.Stats` is live during the run: succeeded, failed, pending, queued, in progress, loads, retries, blocked,
items, duplicates, elapsed time. Breaking out of `await foreach` stops the run; the scraper can be run again.

## Loaders

```csharp
// built in: HttpClient with decompression, cookies and connection pooling
using var loader = new HttpPageLoader(new HttpPageLoaderOptions
{
    Proxy = new WebProxy("http://proxy:8080"),
    UserAgent = "...",
    DefaultHeaders = { ["Accept-Language"] = "de-DE" },
});

// or your own client (IHttpClientFactory, Polly...)
var loader = new HttpPageLoader(httpClient);

// or anything else
public sealed class BrowserLoader : IBlockAwarePageLoader
{
    public async Task<PageResponse> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken)
    {
        var html = await ...;
        return PageResponse.FromText(200, finalUrl, html);
    }

    public void ReportBlocked(ScrapeRequest request, PageResponse response, string reason)
    {
        // retire the proxy / browser session that got this response
    }
}
```

A loader returns any HTTP status as a normal result and throws only when there is no response;
the scraper retries both. Text is decoded from the Content-Type charset, then `<meta charset>`, then UTF-8
(windows-1251, koi8-r, shift_jis... are supported).

## Logging

Pass an `ILogger` to the constructor: progress, failures and retries (debug level) go there.

```csharp
var scraper = new Scraper<Book>(loader, options, loggerFactory.CreateLogger("scraper"));
```

## License

Free for companies under USD 1M annual revenue and under 100 employees; others need a commercial license.
See [LICENSE.md](LICENSE.md).
