# z3l.Scraper

Block-aware web scraping engine for .NET 9 and .NET 10.

You write what to take from a page; the scraper takes care of the rest: a request queue with priorities and
de-duplication, parallel loading, retries with backoff, recognising blocked, missing and cut-off pages,
rate and time limits, and streaming typed results while the scraping goes on. HTML is parsed with
[z3l.FastHtmlParser](https://www.nuget.org/packages/z3l.FastHtmlParser).

- **Label routing.** Each request has a label (`"list"`, `"product"`); each label has its own handler, sync or async.
- **Response checks.** Blocked (403/429, captcha, "Just a moment..."), not found (404, "Product not found"),
  incomplete (`</html>` missing, not JSON, a required part missing): configured per label, retried automatically.
- **Retries with backoff and jitter**, `Retry-After` respected, a per-request timeout, a time limit for the whole run.
  A handler can ask for a retry (`RetryRequestException`) or give up at once (`NonRetryableException`).
- **Whole pages or nothing.** Items a handler emits reach you when it finishes; if it throws, they are dropped,
  so a retried page never gives duplicates.
- **Streaming results.** `await foreach` over items as they are scraped; a slow consumer slows the scraper down
  instead of filling the memory.
- **Politeness and limits.** `MaxConcurrency`, `MaxRequestsPerMinute`, `MaxRequests`, `MaxRunDuration`.
- **De-duplication** by method, URL (without fragment) and body: POST pages with different bodies are different pages.
- **Pluggable loaders.** `HttpPageLoader` is built in; implement `IPageLoader` for a browser, a proxy pool or a
  loader farm. `IBlockAwarePageLoader` is told which responses were blocked, so it can retire a proxy or a session.
- **Trimming and AOT compatible.**

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
    .Route("book", ctx => ctx.Emit(new Book(
        Title: ctx.Html.Find(HtmlTag.H1).Text(),
        Price: ctx.Html.Find(HtmlTag.P, cls: "price_color").Text(),
        Url: ctx.Request.Url)));

await foreach (var book in scraper.RunAsync(new ScrapeRequest("https://books.toscrape.com/", "list")))
{
    Console.WriteLine(book);
}
Console.WriteLine(scraper.Stats);

record Book(string? Title, string? Price, string Url);
```

Relative links are resolved against the page (its final URL after redirects); links that are not http(s)
(`mailto:`, `javascript:`, `tel:`) are skipped; pages already queued are skipped.

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

Start requests need absolute http(s) URLs; a bad one throws `ArgumentException` at once.

## Handlers

```csharp
scraper.Route("product", async ctx =>
{
    ctx.Request;               // the request (Url, Label, UserData, Attempts...)
    ctx.Response;              // StatusCode, Url after redirects, Body, Text, Headers
    ctx.Html;                  // parsed page (z3l.FastHtmlParser), made on first use
    ctx.Json<Product>();       // body as JSON; Json(MyJsonContext.Default.Product) for AOT
    ctx.Enqueue(url, "label", userData);
    ctx.Emit(item);            // thread-safe
    await SomethingAsync(ctx.CancellationToken);
});
scraper.FallbackRoute(ctx => ...);    // labels without a handler of their own
```

A label without a handler is an error at once (`InvalidOperationException`), not a silent skip.

When a page passes the checks but is still wrong, the handler decides:

```csharp
scraper.Route("product", ctx =>
{
    var price = ctx.Html.Find(HtmlTag.Span, cls: "price").Text();
    if (price is null) throw new RetryRequestException("prices hidden for bots", blocked: true);   // load again, tell the loader
    if (ctx.Html.Find(HtmlTag.Div, cls: "age-gate") != null) throw new NonRetryableException("age gate");   // give up at once
    ctx.Emit(new Product(price));
});
```

Any other exception fails the request with `FailureReason.HandlerError` (set `Options.RetryOnHandlerError` to retry it).
Items emitted by a handler that throws are dropped.

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

Checks run in this order; the first that matches decides:

| Check | Verdict | What happens |
|---|---|---|
| status in `BlockedStatusCodes` (403, 429) | Blocked | the loader is told, retried |
| a `BlockedMarkers` string, whatever the status | Blocked | the loader is told, retried |
| a `NotFoundMarkers` string, or status 404 / 410 | NotFound | retried `NotFoundRetries` times (1), then failed |
| any other non-2xx status | Retry | retried up to `MaxRetries` (5), then failed |
| empty body (except 204), wrong format, `MustContain`, `MustNotContain`, `Custom` | Retry | as above |
| load error or timeout | Retry | as above |

String checks ignore case. A blocked response or a 429 with `Retry-After` waits as long as the server asks
(up to `MaxRetryDelay`).

## Options

| Option | Default | |
|---|---|---|
| `MaxConcurrency` | 8 | requests loaded and handled at once |
| `MaxRequestsPerMinute` | none | loads spread evenly, retries included |
| `MaxRequests` | none | accept at most this many requests; handy for trying selectors on a big site |
| `MaxRetries` | 5 | retries after the first attempt |
| `NotFoundRetries` | 1 | retries of a page that looks missing |
| `RetryDelay`, `RetryBackoffFactor`, `MaxRetryDelay` | 1 s, ×2, 1 min | delay before retry *n* is `RetryDelay × factorⁿ⁻¹` ±20 % |
| `RequestTimeout` | 2 min | a slower load is cancelled and retried |
| `MaxRunDuration` | none | stop and return what was scraped |
| `RetryOnHandlerError` | false | retry when a handler throws |
| `ItemBufferSize` | 1000 | items waiting for the consumer before workers wait |
| `ProgressInterval` | 30 s | progress line in the log |

## Results

```csharp
// stream
await foreach (var item in scraper.RunAsync(requests, cancellationToken)) { ... }

// or collect
var result = await scraper.RunToListAsync(requests);
result.Items;    // emitted items
result.Failed;   // FailedRequest: Request, Reason (RetriesExhausted / NotFound / HandlerError / NonRetryable), Message, StatusCode, Exception
result.Stats;    // counters

scraper.RequestFailed += failed => logger.LogWarning("{Url}: {Message}", failed.Request.Url, failed.Message);
```

`scraper.Stats` is live during the run: succeeded, failed, pending, queued, in progress, loads, retries, blocked,
items, duplicates, elapsed time. Breaking out of `await foreach` stops the run; the scraper can be run again.
Several item types: use `Scraper<object>` (or a base type) and `OfType<T>()`.

## Loaders

```csharp
// built in: HttpClient with decompression, cookies and connection pooling
using var loader = new HttpPageLoader(new HttpPageLoaderOptions
{
    Proxy = new WebProxy("http://proxy:8080"),             // null: the system proxy settings
    UserAgent = "...",
    DefaultHeaders = { ["Accept-Language"] = "de-DE" },
    MaxResponseBytes = 10 * 1024 * 1024,                   // bigger responses fail without retries
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

A loader returns any HTTP status as a normal result and throws only when there is no response; the scraper
retries that. A loader throws `NonRetryableException` when another attempt cannot help.

Text is decoded the way browsers do it: a byte order mark, the Content-Type charset, then `<meta charset>` or the
XML declaration, then UTF-8. Labels sites use but .NET does not know (`cp1251`, `win-1251`, `utf8`) are understood,
and `iso-8859-1` is read as windows-1252, so `0x80` is `€`.

## Logging

Pass an `ILogger` to the constructor: progress, failures and retries (debug level) go there. The scraper writes
nothing to the console by itself.

```csharp
var scraper = new Scraper<Book>(loader, options, loggerFactory.CreateLogger("scraper"));
```

## License

Free for companies under USD 1M annual revenue and under 100 employees; others need a commercial license.
See [LICENSE.md](LICENSE.md).
