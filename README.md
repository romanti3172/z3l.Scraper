# z3l.Scraper

Block-aware web scraping engine for .NET 9 and .NET 10.

You write what to take from a page; the scraper takes care of the rest: a request queue with priorities and
de-duplication, parallel loading, retries with backoff, recognising blocked, missing and cut-off pages,
rotating proxies, rate and time limits, and streaming results while the scraping goes on. HTML is parsed with
[z3l.FastHtmlParser](https://www.nuget.org/packages/z3l.FastHtmlParser).

- **Labels and page data.** Each request has a label, a `PageType` (`Listing`, `Item`...) or your own string; each label
  has its handler. A page that knows something about the pages it links to passes it on as typed data.
- **Items of any type.** Handlers emit products, reviews, anything; you get them as they come.
- **Response checks.** Blocked (403/429, captcha, "Just a moment..."), not found (404, "Product not found"),
  incomplete (`</html>` missing, not JSON, a required part missing): configured per label, retried automatically.
- **Retries with backoff and jitter**, `Retry-After` respected, a per-request timeout, a time limit for the whole run.
  A handler can ask for a retry (`RetryRequestException`) or give up at once (`NonRetryableException`).
- **Whole pages or nothing.** Items a handler emits reach you when it finishes; if it throws, they are dropped,
  so a retried page never gives duplicates.
- **Proxies.** A proxy list from a text file (http, https, socks4, socks5, with logins), given out in turn; a proxy a site
  blocks rests for that site, a failing one rests for all.
- **Politeness and limits.** A host that answers 429 / 503 or blocks is slowed down by itself while other hosts keep
  full speed; robots.txt and its Crawl-delay on request; `MaxConcurrency`, `MaxRequestsPerMinute`, `MaxRequests`,
  `MaxDepth`, `MaxRunDuration`.
- **Sitemaps.** Sitemap indexes, gzipped and text sitemaps in one call: `ctx.EnqueueSitemap(PageType.Item)`.
- **Runs that survive a crash.** With `StatePath` the queue is saved as it goes and the next run continues;
  pages in work or not taken yet are done again, nothing is lost.
- **Streaming with backpressure.** `await foreach` over items; a slow consumer slows the scraper down instead of
  filling the memory.
- **Pluggable loaders.** `HttpPageLoader` is built in; implement `IPageLoader` for a browser or anything else.
- **Cache and snapshots.** Pages the handlers took are kept and given back, so you work on handlers without loading the
  site each time; pages that were blocked, missing or broken are saved, so you see what the site answered.
- **Trimming and AOT compatible.**

## Install

```
dotnet add package z3l.Scraper --prerelease
```

## Quick start

```csharp
using z3l.FastHtmlParser;
using z3l.Scraper;
using z3l.Scraper.Loading;

using var loader = new HttpPageLoader();

var scraper = new Scraper(loader)
    .Route(PageType.Listing, ctx =>
    {
        ctx.Enqueue(ctx.Html.XPathValues("//article[@class='product_pod']//h3/a/@href"), PageType.Item);
        ctx.Enqueue(ctx.Html.XPathValue("//li[@class='next']/a/@href"), PageType.Listing);      // next page, null is ignored
    })
    .Route(PageType.Item, ctx => ctx.Emit(new Book(
        Title: ctx.Html.Find(HtmlTag.H1).Text(),
        Price: ctx.Html.Find(HtmlTag.P, cls: "price_color").Text(),
        Url: ctx.Request.Url)));

await foreach (var item in scraper.RunAsync(new ScrapeRequest("https://books.toscrape.com/", PageType.Listing)))
{
    Console.WriteLine(item);
}
Console.WriteLine(scraper.Stats);

record Book(string? Title, string? Price, string Url);
```

Relative links are resolved against the page (its final URL after redirects); links that are not http(s)
(`mailto:`, `javascript:`, `tel:`) are skipped; pages already queued are skipped.

## Labels and page data

A label is a `PageType` or any string; both work everywhere a label is taken (`Route`, `Enqueue`, `ScrapeRequest`,
`Rules[...]`), and labels ignore case. `PageType` has `Root`, `Category`, `Listing`, `Item`, `ItemDetails`, `Api`, `File`
and `Sitemap`; `File` pages are checked as binary and `Api` pages as JSON unless their rules say otherwise.

A listing needs only its link. A page that already knows something about the pages it links to (a price, a rating,
the category) passes it on as data, and the handler of that label gets it typed:

```csharp
record Teaser(string Price, string Category);

var scraper = new Scraper(loader)
    .Route(PageType.Listing, ctx =>
    {
        foreach (var card in ctx.Html.FindAll(HtmlTag.Div, cls: "card"))
        {
            ctx.Enqueue(card.Find(HtmlTag.A).AttributeHref(), PageType.Item,
                new Teaser(card.Find(HtmlTag.Span, cls: "price").Text()!, "tea"));
        }
        ctx.Enqueue(ctx.Html.XPathValue("//a[@rel='next']/@href"), PageType.Listing);
    })
    .Route<Teaser>(PageType.Item, (ctx, teaser) =>
    {
        ctx.Emit(new Product(ctx.Html.XPathValue("//h1"), teaser.Price, teaser.Category));
        foreach (var review in ctx.Html.FindAll(HtmlTag.Div, cls: "review")) ctx.Emit(new Review(review.Text()));
    })
    .Route("reviews-api", ctx => ...);                      // your own labels next to page types

await foreach (var item in scraper.RunAsync(new ScrapeRequest("https://shop.example/tea", PageType.Listing)))
{
    switch (item)
    {
        case Product product: await db.SaveProductAsync(product); break;
        case Review review: await db.SaveReviewAsync(review); break;
    }
}
```

Data of the wrong type, or none, for a typed handler is an error where the request is queued, not later.
A start request with data: `ScrapeRequest.For(url, PageType.Item, new Teaser(...))`.

## Requests

```csharp
new ScrapeRequest("https://shop.example/catalog", PageType.Listing);
"https://shop.example/";                                   // a string is a request with the "default" label

new ScrapeRequest("https://shop.example/api/search", PageType.Api)
{
    Method = HttpMethod.Post,
    Body = """{"page":2}""",
    ContentType = "application/json",
    Headers = new Dictionary<string, string> { ["X-Requested-With"] = "XMLHttpRequest" },
    Priority = ScrapePriority.High,
    Data = category,                                       // for Route<Category>, or ctx.Request.Data
    MaxRetries = 10,
};
```

Start requests need absolute http(s) URLs; a bad one throws `ArgumentException` at once.

## Handlers

```csharp
scraper.Route(PageType.Item, async ctx =>
{
    ctx.Request;               // the request (Url, Label, Data, Depth, Attempts...)
    ctx.Response;              // StatusCode, Url after redirects, Body, Text, Headers
    ctx.Html;                  // parsed page (z3l.FastHtmlParser), made on first use
    ctx.Json<Product>();       // body as JSON; Json(MyJsonContext.Default.Product) for AOT
    ctx.Enqueue(url, PageType.ItemDetails);
    ctx.Enqueue(url, PageType.ItemDetails, data);
    ctx.Emit(item);            // any type, thread-safe
    await SomethingAsync(ctx.CancellationToken);
});
scraper.FallbackRoute(ctx => ...);    // labels without a handler of their own
```

A label without a handler is an error at once (`InvalidOperationException`), not a silent skip.

When a page passes the checks but is still wrong, the handler decides:

```csharp
scraper.Route(PageType.Item, ctx =>
{
    var price = ctx.Html.Find(HtmlTag.Span, cls: "price").Text();
    if (price is null) throw new RetryRequestException("prices hidden for bots", blocked: true);   // load again, tell the loader
    if (ctx.Html.Find(HtmlTag.Div, cls: "age-gate") != null) throw new NonRetryableException("age gate");   // give up at once
    ctx.Emit(new Product(price));
});
```

Any other exception fails the request with `FailureReason.HandlerError` (set `Options.RetryOnHandlerError` to retry it).
Items emitted by a handler that throws are dropped; the requests it queued before it threw stay queued.

When the site changes its pages, a handler breaks: every page of its label fails, and loading the rest only wastes
traffic. `MaxConsecutiveHandlerErrors` stops the run after that many handler errors in a row of one label on one host;
a page the handler takes starts the count again, so odd pages here and there do not stop it. `RetryRequestException` and
`NonRetryableException` are not handler errors. The run ends as after `MaxRunDuration`, with
`Stats.StoppedByHandlerErrors`; with `StatePath` the pages the handler failed in a row stay with the unfinished ones, so
the run continued after the fix does them again.

```csharp
options.MaxConsecutiveHandlerErrors = 20;
```

A page lives only while its handler runs: then the parser is disposed and the page is let go, even if the consumer is
slow to take the items. Items and request data carry values taken from the page (`Text()`, `AttributeHref()`,
`XPathValue()`...); `Emit` and `Enqueue` refuse tags, the parser and the context, a tag kept anyway throws
`ObjectDisposedException`, and a kept context no longer holds the page. So the scraper holds at most `MaxConcurrency`
pages and `ItemBufferSize` items; what grows with a run is the set of pages seen, for duplicates (a 16-byte hash of
each key, about 40 bytes a page), and the list of failed requests. The text of a page is decoded into a pooled buffer
that the response checks, `ctx.Html` and `ctx.Json` read in place: a string of it is made only when a handler reads
`ctx.Response.Text`.

## Response checks

```csharp
var options = new ScraperOptions();

// for all labels
options.Rules.Default.Content = ExpectedContent.Html;                 // a cut-off page is retried
options.Rules.Default.BlockedMarkers.AddRange(["Just a moment...", "captcha"]);
options.Rules.Default.BlockedStatusCodes.Add(503);                    // 403 and 429 are there already

// for one label, added to the default rules
options.Rules[PageType.Item].MustContain.Add("itemprop=\"price\"");
options.Rules[PageType.Item].NotFoundMarkers.Add("Product not found");
options.Rules["reviews-api"].Content = ExpectedContent.Json;

// your own check; text is the page read in place (no string), valid during the call
options.Rules[PageType.Item].Custom = (request, response, text) =>
    text.Length < 5000 ? ValidationResult.Retry("page too short") : ValidationResult.Ok;
```

Checks run in this order; the first that matches decides:

| Check | Verdict | What happens |
|---|---|---|
| status in `BlockedStatusCodes` (403, 429) | Blocked | the loader is told, the host slows down, retried |
| a `BlockedMarkers` string, whatever the status | Blocked | as above |
| a `NotFoundMarkers` string, or status 404 / 410 | NotFound | retried `NotFoundRetries` times (1), then failed |
| any other non-2xx status | Retry | retried up to `MaxRetries` (5), then failed |
| empty body (except 204), wrong format, `MustContain`, `MustNotContain`, `Custom` | Retry | as above |
| load error or timeout | Retry | as above |

String checks ignore case. A blocked response or a 429 with `Retry-After` waits as long as the server asks
(up to `MaxRetryDelay`).

## Proxies

```csharp
var proxies = ProxyList.FromFile("proxies.txt", ProxyBlockPolicy.Block1Hour, logger);
using var loader = new HttpPageLoader(new HttpPageLoaderOptions { Proxies = proxies });
```

One proxy per line; a `#` at the start of a line or after a space starts a comment:

```
1.2.3.4:8080                     # http
1.2.3.4:8080:user:password
user:password@1.2.3.4:8080
socks5://user:password@1.2.3.4:1080
socks5://1.2.3.4:1080:user:password
[2001:db8::1]:8080
https://proxy.example:443
```

http, https, socks4, socks4a and socks5 proxies, with or without a login, are handled by .NET itself; every proxy has
its own connections and cookies. A line without a scheme is an http proxy. `socks5h://` is read as socks5, which lets the
proxy resolve the site's name, as socks4a does (socks4 gets an address resolved here). A login may hold any character;
before `@` it may also be URL-encoded (`%40` for `@`).

`https://` is TLS to the proxy itself, and the proxy's certificate must be valid for its name. What proxy sellers call an
"HTTPS proxy" is usually a plain http proxy that tunnels https pages with CONNECT: write it as `http://`. An http proxy
that cannot CONNECT loads http pages only.

Proxies are given out in turn. When a site blocks an answer, the proxy that loaded it rests for that site only, as
`ProxyBlockPolicy` says: `Ignore`, `Block10Minutes`, `Block1Hour` (the default), `Block1Day` or `Remove`.

A failure (no connection, a timeout, 407) or a bad response (the proxy answered, but not with the page: its own page, a
cut-off one, the page fails `MustContain`, the format or another check, comes with a 5xx, or the handler throws
`RetryRequestException`) puts the proxy after the clean ones: it is taken only when no clean proxy is free. Successes do
not put a proxy ahead, so the turn stays even and no proxy gets more loads at once than the others. After
`MaxConsecutiveFailures` (3) failures in a row a proxy rests for every site for `DeadProxyPause` (10 min); after
`MaxConsecutiveBadResponses` (3) bad responses in a row on a site it rests for that site for `BadResponsePause` (10 min).
An error older than its pause is forgotten, a good page starts the count again. Bad responses are counted per site, so a
broken site does not take the proxies from the others; 429 and 503 are left to the throttling. When every proxy rests for a site, its loads wait for the first one to come back,
no longer than `RequestTimeout`. The file is read again when it changes; proxies that stay keep their rests.
`proxies.Status()` and `proxies.ToString()` show what each proxy did.

The list can also come from a proxy service: the URL answers with the same lines or with a JSON array of them, and is read
again every 5 minutes in the background (a failed read keeps the old list):

```csharp
var api = new HttpClient { DefaultRequestHeaders = { { "X-Api-Key", key } } };
var proxies = await ProxyList.FromUrlAsync(new Uri("https://proxies.example/list"), refreshInterval: TimeSpan.FromMinutes(5), httpClient: api);
```

An API that wants a POST, a key in the body or answers in its own format goes through a delegate that returns the lines;
it is called again the same way (`UserAgentList.FromSourceAsync` is the same for user agents):

```csharp
var proxies = await ProxyList.FromSourceAsync(async token =>
{
    using var answer = await api.PostAsJsonAsync("https://proxies.example/v2/proxies", new { country = "pl" }, token);
    var items = await answer.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<ProxyItem[]>(token);
    return items!.Select(x => $"socks5://{x.Login}:{x.Password}@{x.Host}:{x.Port}");
}, name: "proxies API");
```

### User agents

```csharp
using var loader = new HttpPageLoader(new HttpPageLoaderOptions
{
    Proxies = ProxyList.FromFile("proxies.txt"),
    UserAgents = UserAgentList.FromFile("useragents.txt"),     // or await UserAgentList.FromUrlAsync(url)
});
```

One user agent per line, `#` for comments; the file is read again when it changes. With proxies every proxy takes one
user agent and keeps it, as it keeps its cookies, so a site sees one browser per address; without proxies every request
takes the next one. A `User-Agent` in `DefaultHeaders` or in the request still wins.

## Throttling

`AutoThrottle = true` turns on slowing a host down; it is off by default. It is for scraping from one address, where a 429,
a 503 or a block means the site is loaded too hard. With rotating proxies a block is about the proxy (the proxy list rests
it), and remote loaders set their own pace: there slowing the whole host only slows the scraping. Without it a 429 or 503
is retried after the retry delay, or as long as its `Retry-After` asks (up to `MaxRetryDelay`).

When it is on, a host that answers 429 or 503, or whose responses are blocked (also when a handler throws
`RetryRequestException(..., blocked: true)`), is slowed down as a whole: its requests wait in a line and load one at a time,
1 s apart at first, twice as long after each next such answer (up to `MaxThrottleDelay`, 1 min), and the delay shrinks
again by 10 % with each good page. A `Retry-After` pauses the host as long as it asks. Requests to other hosts keep
going at full speed meanwhile: the line holds requests, not workers. robots.txt `Crawl-delay` (with `RespectRobotsTxt`)
spaces a host the same way whatever `AutoThrottle` is.

## Sitemaps and robots.txt

```csharp
scraper
    .Route(PageType.Sitemap, ctx => ctx.EnqueueSitemap(PageType.Item,          // pages as Item, nested sitemaps as Sitemap
        filter: page => page.LastModified > DateTimeOffset.UtcNow.AddDays(-7)))
    .Route(PageType.Item, ctx => ...);

await foreach (var item in scraper.RunAsync(new ScrapeRequest("https://shop.example/sitemap.xml", PageType.Sitemap))) { }

ctx.ParseSitemap();                       // Urls (Url, LastModified) and nested Sitemaps, if you want to look first
RobotsTxt.Parse(text).Sitemaps;           // the "Sitemap:" lines of a robots.txt
```

Sitemaps may be XML (`urlset`, `sitemapindex`), gzipped (`.xml.gz`) or a text list of URLs.

`RespectRobotsTxt` is off by default; then robots.txt is not even loaded. With `RespectRobotsTxt = true` the scraper loads
robots.txt of each host once, with your loader, before its first page: disallowed pages fail with
`FailureReason.DisallowedByRobotsTxt` without being loaded, and `Crawl-delay` spaces the loads of the host. Rules are matched
as RFC 9309 says (longest match, Allow wins a tie, `*` and `$`) for the group of `RobotsUserAgent` ("z3l-scraper"), or for
`*`. A missing robots.txt allows everything; one that cannot be loaded (5xx, network error) is logged and the host is
crawled without rules.

## Depth

`ctx.Request.Depth` is 0 for start requests and one more than the page that queued it. `MaxDepth` skips deeper links
(a page found too deep is still taken if another path reaches it closer).

## Resuming a run

```csharp
var options = new ScraperOptions
{
    StatePath = "state/shop.json",
    StateJson = new DefaultJsonTypeInfoResolver(),        // for typed page data; MyJsonContext.Default for AOT
};

await foreach (var item in scraper.RunAsync(startRequests))
{
    await db.SaveAsync(item);      // an item counts as taken when the loop asks for the next one
}
```

The state file holds the requests not finished; `state/shop.json.seen` next to it holds the pages seen, a 16-byte hash
of each key, and every save only appends the new ones, so a run of millions of pages is not written out whole each time.
Both are written every `StateSaveInterval` (30 s) and when the run stops (time limit, cancellation, `break`, an
exception in your loop), and deleted when the run has finished everything and you have taken every item. Start the
program again with the same `StatePath` and the run continues where it stopped; the start requests are skipped as
already seen.

Pages being loaded when the process died, and pages whose items you had not taken yet, are done again: you get every
item at least once, so make saving idempotent (an upsert by URL). `RunToListAsync` keeps items in memory, so a crash
loses them; stream with `RunAsync` when you keep state. Page data must be a plain value (a string, a number, a bool,
a Guid, a DateTime(Offset), a `JsonElement`) or the data of a typed handler, saved as JSON with `StateJson`: a
`JsonSerializerContext` with your data types (trimming and AOT safe) or `new DefaultJsonTypeInfoResolver()`.

## Options

| Option | Default | |
|---|---|---|
| `MaxConcurrency` | 8 | requests loaded and handled at once |
| `MaxRequestsPerMinute` | none | loads spread evenly, retries included |
| `MaxRequests` | none | accept at most this many requests; handy for trying selectors on a big site |
| `MaxDepth` | none | deepest link to follow; start requests are depth 0 |
| `MaxRetries` | 5 | retries after the first attempt |
| `NotFoundRetries` | 1 | retries of a page that looks missing |
| `RetryDelay`, `RetryBackoffFactor`, `MaxRetryDelay` | 1 s, ×2, 1 min | delay before retry *n* is `RetryDelay × factorⁿ⁻¹` ±20 % |
| `RequestTimeout` | 2 min | a slower load is cancelled and retried |
| `MaxRunDuration` | none | stop and return what was scraped |
| `AutoThrottle`, `MaxThrottleDelay` | off, 1 min | slow down a host that answers 429 / 503 or blocks (scraping from one address) |
| `RespectRobotsTxt`, `RobotsUserAgent` | off, "z3l-scraper" | skip what robots.txt disallows, honour Crawl-delay |
| `StatePath`, `StateSaveInterval`, `StateJson` | none, 30 s, none | save the run and continue it after a crash |
| `RetryOnHandlerError` | false | retry when a handler throws |
| `MaxConsecutiveHandlerErrors` | none | stop the run when the handler of a label fails this many times in a row on a host |
| `ItemBufferSize` | 1000 | items waiting for the consumer before workers wait |
| `ProgressInterval` | 30 s | progress line in the log |

## Results

```csharp
// stream
await foreach (var item in scraper.RunAsync(requests, cancellationToken)) { ... }

// or collect
var result = await scraper.RunToListAsync(requests);
result.Items;                 // all items, in the order they came
result.ItemsOf<Product>();    // the items of one type
result.Failed;                // FailedRequest: Request, Reason (RetriesExhausted / NotFound / HandlerError / NonRetryable / DisallowedByRobotsTxt), Message, StatusCode, Exception
result.Stats;                 // counters

scraper.RequestFailed += failed => logger.LogWarning("{Url}: {Message}", failed.Request.Url, failed.Message);
```

`scraper.Stats` is live during the run: succeeded, failed, pending, queued, in progress, loads, from cache, retries, blocked,
items, duplicates, elapsed time. Breaking out of `await foreach` stops the run; the scraper can be run again.

## Loaders

```csharp
// built in: HttpClient with decompression, cookies and connection pooling
using var loader = new HttpPageLoader(new HttpPageLoaderOptions
{
    Proxies = ProxyList.FromFile("proxies.txt"),           // or Proxy = new WebProxy(...) for one; none: the system settings
    UserAgent = "...",                                     // or UserAgents = UserAgentList.FromFile("useragents.txt")
    DefaultHeaders = { ["Accept-Language"] = "de-DE" },
    MaxResponseBytes = 10 * 1024 * 1024,                   // bigger responses fail without retries
});

// or your own client (IHttpClientFactory, Polly...)
var loader = new HttpPageLoader(httpClient);

// or a headless browser: the z3l.Scraper.Playwright package
await using var loader = new PlaywrightPageLoader(new PlaywrightPageLoaderOptions { Channel = "chrome", WaitForSelector = "div.product" });

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

    // optional: the response was not the page (failed the checks, a 5xx, the handler asked for a retry) / it was the page /
    // the last load of the request ran out of RequestTimeout
    public void ReportBadResponse(ScrapeRequest request, PageResponse response, string reason) { }
    public void ReportGoodResponse(ScrapeRequest request, PageResponse response) { }
    public void ReportTimeout(ScrapeRequest request) { }
}
```

A loader returns any HTTP status as a normal result and throws only when there is no response; the scraper
retries that. A loader throws `NonRetryableException` when another attempt cannot help. A loader of your own can
rotate proxies with `ProxyList` too: `AcquireAsync(host)` gives the next proxy that may load from the site, and
`ReportSuccess`, `ReportFailure`, `ReportBlocked`, `ReportBadResponse` and `ReportGoodResponse` tell the list how it went.

[z3l.Scraper.Playwright](https://github.com/romanti3172/z3l.Scraper/blob/main/src/z3l.Scraper.Playwright/README.md) loads pages in Chromium, Firefox or WebKit, with a
browser context per proxy.

Pages of some labels can go to another loader: for example pages through remote browser workers and files with a local
`HttpPageLoader`, or categories without proxies. Each loader keeps its own way: a local one works with `MaxConcurrency`
loads and `RequestTimeout`, a queue loader (below) gets every request at once.

```csharp
var scraper = new Scraper(browserFarm, options)
    .UseLoader(http, PageType.File)
    .Route(...);
```

A loader that hands pages to a queue of remote workers implements `IQueuePageLoader`. The scraper then sends every
request as soon as it is accepted (the queue can be of any length), `MaxConcurrency` limits only the handlers that run at
once, and `RequestTimeout` does not apply: the loader finds lost loads itself. When more than `MaxLoadedNotHandled` (100)
loaded pages wait for a handler, the scraper sets the loader's `Paused`, and the answers wait in the queue instead of the
memory. [z3l.Scraper.RabbitMQ](https://github.com/romanti3172/z3l.Scraper/blob/main/src/z3l.Scraper.RabbitMQ/README.md)
is such a loader on RabbitMQ, with the workers for it.

Text is decoded the way browsers do it: a byte order mark, the Content-Type charset, then `<meta charset>` or the
XML declaration, then UTF-8. Labels sites use but .NET does not know (`cp1251`, `win-1251`, `utf8`) are understood,
and `iso-8859-1` is read as windows-1252, so `0x80` is `€`.

## Cache and snapshots

Two helpers for the pages of every loader and label, set on the scraper:

```csharp
var scraper = new Scraper(loader, options)
{
    CachingPageLoader = new CachingPageLoader { Path = "cache" },          // the pages the handlers took, given back next time
    SnapshotPageLoader = new SnapshotPageLoader { Path = "snapshots" },    // the pages that were not taken, to look at
};
```

**The cache** keeps the pages that passed the checks and that their handler took, and gives them back instead of loading
them again: run the scraper over and over while you work on the handlers, without loading the site each time. Block
pages and cut-off pages never get in. A page from the cache skips the host throttle, the rate limit and the loader, is
checked with the rules of its label like any page, and the loader hears nothing about it. A kept page that fails the
checks later (the rules changed) or whose handler throws `RetryRequestException` is forgotten and loaded, without taking
an attempt. `Stats.FromCache` counts the pages the cache gave.

```csharp
new CachingPageLoader
{
    Path = "cache",                    // a folder per host; per page the body (.html, .json...) and its description (.meta.json)
    Labels = { PageType.Item },        // only these labels; empty, the default, for every label
    Filter = (request, response) => response.Url.Contains("/p/"),     // which of them to keep; all by default
    MaxAge = TimeSpan.FromDays(1),     // older pages are loaded again; zero, the default, keeps them for ever
    MaxPages = 1000,                   // pages to keep at most, earlier runs included; 0, the default, for no limit
};
```

**Snapshots** are the loaded pages that were not taken: blocked, missing, failing the checks, or those whose handler threw,
one for every such attempt. They go to `{Path}/{label}/{kind}`, the kind being `blocked`, `retry`, `not-found` or
`handler-error`, two files each: the body and its description (the request, the reason, the status, the headers, what the
handler threw). A load that brought no page (a network error, a timeout) has no snapshot. `SnapshotPageLoader` takes
`Labels` and a `Filter` too (`snapshot => snapshot.Kind == SnapshotKind.Blocked`), and `MaxPerKind` (for each label and
kind) and `MaxSnapshots` limit how many it saves; 0, the default, for no limit. Files of earlier runs stay.

To keep the pages elsewhere, in a database for example, override the storage and keep the labels, the filter and the
limits of the classes (`ReadAsync`, `WriteAsync`, `DeleteAsync` and `CountStored` of `CachingPageLoader`, `WriteAsync` of
`SnapshotPageLoader`), or implement `ICachingPageLoader` or `ISnapshotPageLoader` yourself; `CachedPage.From` and
`ToResponse` turn a response into what to keep and back.

## Logging

Pass an `ILogger` to the constructor: progress, failures and retries (debug level) go there. The scraper writes
nothing to the console by itself.

```csharp
var scraper = new Scraper(loader, options, loggerFactory.CreateLogger("scraper"));
```

## License

Free for companies under USD 1M annual revenue and under 100 employees; others need a commercial license.
See [LICENSE.md](https://github.com/romanti3172/z3l.Scraper/blob/main/LICENSE.md). The commercial license is one payment of USD 1,000 for all z3l libraries, z3l.FastHtmlParser included.
