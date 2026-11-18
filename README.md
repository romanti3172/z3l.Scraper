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
- **Requests as they are needed.** A big table of URLs is read a batch at a time when the run is low on requests
  (`RequestsNeeded`), not loaded into memory at once.
- **Pluggable loaders.** `HttpPageLoader` and `StealthHttpPageLoader` (HTTP/2 and the headers of a browser) are built in;
  derive from `BlockAwarePageLoader` for a browser or anything else, and get the proxy rotation with it.
- **Cache and snapshots.** Pages the handlers took are kept and given back, so you work on handlers without loading the
  site each time; pages that were blocked, missing or broken are saved, so you see what the site answered.
- **One page through its handler.** `ParseAsync` loads a page, or takes a saved one, and runs its handler without
  crawling on: what it emits, the pages it found, what the checks said. For trying a handler and for its tests.
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

## Requests as the run needs them

Start requests are queued at once, all of them: fine for thousands of pages, but a table of millions would sit in memory.
Give such a source out as the run needs it: `RequestsNeeded` is raised when fewer than `RequestsNeededBelow` (1000)
requests are unfinished, and when none is left.

```csharp
long lastId = 0;
scraper.RequestsNeeded += async e =>
{
    var rows = await db.ProductsAfterAsync(lastId, take: 1000, e.CancellationToken);   // WHERE id > @lastId ORDER BY id LIMIT 1000
    foreach (var row in rows) e.Add(row.Url, PageType.Item);
    if (rows.Count > 0) lastId = rows[^1].Id;
};

await foreach (var item in scraper.RunAsync()) { ... }
```

- Unfinished requests are those queued, in progress or waiting for a retry, links found on pages included: when the pages
  give many links, the event waits until they are done. `e.Pending` is their number.
- Calls never overlap, so a handler may keep its place in a plain variable, and the run does not end while a call goes.
  When a call still leaves fewer requests than the limit, the next one follows at once.
- A call that adds nothing means the source is empty for now: in the middle of the run the next call comes when nothing is
  left, and when nothing is left and a call adds nothing, the run ends. Requests passed to `e.Add` count even when they are
  duplicates, so a handler that gives the same rows again is called again.
- `e.Add` queues a page as a start request and returns false for a duplicate or over `MaxRequests`. A URL that is not an
  absolute http(s) URL, an empty one included, does not throw: its request fails with `FailureReason.BadUrl`. It takes a URL
  and a label, a list of URLs with one label, or a `ScrapeRequest` for a POST, headers or data.
- A page already queued in this run is skipped as a duplicate. A source that gives a page out again, for example to check a
  price once more, passes `skipDuplicateCheck: true`: `e.Add(row.Url, PageType.Item, skipDuplicateCheck: true)` queues it
  even if this run has loaded it. Such a page is not remembered either, so a link to it found later is queued too.
- The event is not raised after `MaxRequests` is reached or when the run stops (`MaxRunDuration`, cancellation, `break`);
  `e.CancellationToken` cancels a call that goes then. An exception of a handler ends the run with it.
- Start requests passed to `RunAsync` are taken as before. Several handlers are called one after another with the same `e`.
- With `StatePath` the unfinished requests are saved as always, but where the source goes on after a restart is up to the
  handler: keep its place yourself, or mark rows done as their items are saved.

## Handlers

```csharp
scraper.Route(PageType.Item, async ctx =>
{
    ctx.Request;               // the request (Url, Label, Data, Depth, ParentUrl: the page it was found on, Attempts...)
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

## One page through its handler

`ParseAsync` loads one page and runs its handler as a run would, but loads nothing else: the pages the handler queues
come back in `Found` instead. Try a handler on a live page without crawling the site:

```csharp
var page = await scraper.ParseAsync("https://shop.example/catalog?page=2", PageType.Listing);
page.Items          // what the handler emitted
page.Found          // the pages it queued (URL, label, data), not loaded
page.Validation     // what the response checks said: Ok, Retry, Blocked or NotFound, and why
page.Response       // the page: status, headers, text
page.Exception      // what the handler threw, if it did
```

The same on a page at hand, a saved one for example, with no loader and no network, for tests of the handlers:

```csharp
var html = File.ReadAllText("catalog.html");
var page = await scraper.ParseAsync(PageResponse.FromText(200, "https://shop.example/catalog", html), PageType.Listing);
Assert.AreEqual(24, page.Found.Count);
```

- One attempt with the loader of the label and `RequestTimeout`; no retries, robots.txt, throttle, cache, snapshots, state
  or `Stats`, so it may go while a run goes. A network error throws, and no response in time throws `TimeoutException`.
- The page is checked with the rules of its label, and the handler runs only when it passes, as in a run.
- `Found` takes what a run would take: duplicates and URLs that are not http(s) are left out, and `ctx.Enqueue` returns
  false for them as in a run. `MaxDepth` and `MaxRequests` do not apply.
- Items emitted before the handler threw stay in `Items`, and `Exception` holds what it threw. `Succeeded` is true when
  the page passed the checks and the handler finished.
- A typed handler needs its data: `ParseAsync(ScrapeRequest.For(url, PageType.Item, teaser))`.

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
(up to `MaxRetryDelay`). Keep the reason of a custom check short and the same for alike pages (`"page too short"`, not
the length): `Stats.Reasons` groups the attempts by it.

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

`https://` is TLS to the proxy itself, and the proxy's certificate must be valid for its name. A proxy reached by its IP
address often shows a certificate of a host name, or a self-signed one: `AcceptAnyProxyCertificate = true` takes any
certificate from https proxies, while the sites' certificates are checked as always. Such TLS only hides the traffic from
listeners: as with an http proxy, someone in the middle could pose as the proxy and see its login and the http pages.
What proxy sellers call an "HTTPS proxy" is usually a plain http proxy that tunnels https pages with CONNECT: write it as
`http://`. An http proxy that cannot CONNECT loads http pages only.

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
(a page found too deep is still taken if another path reaches it closer). `MaxDepth = 0` loads only the start requests
and those of `RequestsNeeded`: the pages the handlers queue are skipped. To see them without loading them, use
`ParseAsync`.

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
| `RequestsNeededBelow` | 1000 | with `RequestsNeeded` handlers: ask for more requests when fewer are unfinished |
| `MaxDepth` | none | deepest link to follow; start requests are depth 0, so 0 loads only them |
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
scraper.FailedRequests;       // the requests that failed, the same as result.Failed below

// or collect
var result = await scraper.RunToListAsync(requests);
result.Items;                 // all items, in the order they came
result.ItemsOf<Product>();    // the items of one type
result.Failed;                // FailedRequest: Request, Reason (RetriesExhausted / NotFound / HandlerError / NonRetryable / DisallowedByRobotsTxt / BadUrl), Message, StatusCode, Exception
result.Stats;                 // counters

scraper.RequestFailed += failed => logger.LogWarning("{Url}: {Message}", failed.Request.Url, failed.Message);
```

`scraper.Stats` is live during the run: succeeded, failed, pending, queued, in progress, loads, from cache, retries, blocked,
items, duplicates, elapsed time. Breaking out of `await foreach` stops the run; the scraper can be run again.

`Stats.Reasons` tells why attempts gave no page, so a site that blocks or a selector that broke shows at once. Every retry
and every failure is counted once, in a group of its kind, reason, label and host, most frequent first. The reason is the
same for alike attempts:
- the check that failed: `status 403`, `marker "captcha"`, `no "price"`;
- the timeout;
- the kind of a load error (`ConnectionError`, `ProxyTunnelError`), not its message, which names the proxy and the IP;
- the type of what a handler threw, or the message of its `RetryRequestException` or `NonRetryableException`.

`Count` is how many times, `Failed` how many of them ended their request, and `Example` and `ExampleUrl` keep the message
and the URL of the last one. The last log line of a run names the first five:

```
Scraping finished: ... Top reasons: 8711x blocked: marker "captcha" (Item, shop.com; 812 failed); 2040x timeout: 120 s (Item, shop.com); ...
```

Up to 1000 groups are kept; past them, new ones are counted in an "other reasons" group of their kind.

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

// asks for pages the way a browser does: HTTP/2, the browser's headers in its order, Referer
using var loader = new StealthHttpPageLoader(new StealthHttpPageLoaderOptions
{
    Proxies = ProxyList.FromFile("proxies.txt"),
    UserAgents = UserAgentList.FromFile("useragents.txt"), // one per proxy; Chrome, Edge, Firefox and Safari get their own headers
    AcceptLanguage = "de-DE,de;q=0.9",
});

// or your own client (IHttpClientFactory, Polly...)
var loader = new HttpPageLoader(httpClient);

// or a headless browser: the z3l.Scraper.Playwright package
await using var loader = new PlaywrightPageLoader(new PlaywrightPageLoaderOptions { Channel = "chrome", WaitForSelector = "div.product" });
```

A loader returns any HTTP status as a normal result and throws only when there is no response; the scraper
retries that. A loader throws `NonRetryableException` when another attempt cannot help.

`HttpPageLoader` sends the request as it is, over HTTP/1.1. `StealthHttpPageLoader` asks for it the way a browser does:
- over HTTP/2, falling back to HTTP/1.1 where the server has no HTTP/2 (and on http:// sites);
- with the headers of the browser of the user agent, in its order. Chrome and Edge send client hints (`sec-ch-ua` with the
  brands Chromium makes for the version, `sec-ch-ua-mobile`, `sec-ch-ua-platform`), the Accept of Chrome,
  `Upgrade-Insecure-Requests`, `Sec-Fetch-*` and `Priority`. Firefox and Safari send theirs, without client hints, and every
  browser on iOS asks as Safari. Other Chromium browsers (Opera, Yandex) get Chrome's headers without client hints.
- a page is asked for as a navigation, a request of `PageType.Api` as `fetch()` from the page it was found on;
- `Referer` and `Sec-Fetch-Site` come from `ScrapeRequest.ParentUrl`, the page the link was found on, by the browsers'
  default policy: the whole URL to the same origin, the origin elsewhere, nothing from https to http.

A header of `DefaultHeaders` or of the request replaces the browser's header of the same name in its place, and a
User-Agent there is the browser to look like. The TLS handshake and the HTTP/2 settings stay those of .NET, which a strong
anti-bot that reads JA3/JA4 tells from a browser's: for such sites use a browser, or a TLS library through `CreateHandler`.

### Loaders of your own

```
local loaders (IPageLoader): the scraper's MaxConcurrency and RequestTimeout
BlockAwarePageLoader            rotating proxies, a user agent per proxy, Report… to ProxyList
 └─ HttpClientPageLoader        HttpClient per proxy with its cookies, CreateHandler, CreateMessage, the body limit
     ├─ HttpPageLoader          the request as it is
     └─ StealthHttpPageLoader   the way a browser asks

remote loaders (IQueuePageLoader): every page at once, the loader keeps its own pace and time
QueuePageLoader                 the loads that wait for answers, Paused, lost loads, counters
 ├─ RabbitMqPageLoader          remote workers behind RabbitMQ (z3l.Scraper.RabbitMQ)
 └─ ServicePageLoader           a scraping service, a call per page: the plan's limit, a call timeout, levels
```

```csharp
// a loader of anything (a browser, curl...): BlockAwarePageLoader takes the proxy of every load and tells the list how it did
public sealed class CurlLoader(ProxyList proxies) : BlockAwarePageLoader(proxies, userAgents: null, userAgent: "Mozilla/5.0 ...")
{
    protected override async Task<PageResponse> LoadThroughAsync(ScrapeRequest request, Uri uri, ProxyList.Entry? proxy,
        string? userAgent, CancellationToken cancellationToken)
    {
        var html = await ...;                              // through proxy.Address
        return PageResponse.FromText(200, finalUrl, html);
    }
}

// HttpClient with a handler of your own: Polly, logging, signed requests, a TLS library, another outgoing address
public sealed class RetryingLoader(StealthHttpPageLoaderOptions options) : StealthHttpPageLoader(options)
{
    protected override HttpMessageHandler CreateHandler(IWebProxy? proxy) => new MyDelegatingHandler(base.CreateHandler(proxy));
}
```

`BlockAwarePageLoader` hears from the scraper which responses were blocked (`ReportBlocked`), which were not the page
(they failed the checks, a 5xx, the handler asked for a retry: `ReportBadResponse`), which were (`ReportGoodResponse`) and
which loads ran out of `RequestTimeout` (`ReportTimeout`), and passes that on to the proxy that loaded them; override these
to retire a browser session too. A loader can also implement `IPageLoader` or `IBlockAwarePageLoader` itself and rotate
proxies with `ProxyList`: `AcquireAsync(host)` gives the next proxy that may load from the site, and `ReportSuccess`,
`ReportFailure`, `ReportBlocked`, `ReportBadResponse` and `ReportGoodResponse` tell the list how it went.

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

### Remote loaders

`QueuePageLoader` is the waiting part of such a loader: it keeps the loads that wait for their answers, stops the clock of
their deadlines while the scraper pauses it, and counts what was sent, answered, failed where it went, lost and answered
late. A derived class sends a load (`SendAsync`) and gives its answer with `Answer`, `Fail` or `Lose`; a lost load throws
`RemoteLoadException`, and the scraper retries it.

`ServicePageLoader` is the base of a scraping service that loads a page per HTTP call (ScraperAPI, ScrapingBee, Zyte
API...), where the proxies, the browsers and the captchas are the service's:

```csharp
public sealed class MyServiceLoader(string apiKey) : ServicePageLoader(new ServicePageLoaderOptions { MaxConcurrency = 10 })
{
    protected override int MaxLevel => 1;      // 0: plain, 1: premium proxies

    protected override async Task<PageResponse> CallAsync(HttpClient client, ScrapeRequest request, int level, CancellationToken cancellationToken)
    {
        using var answer = await client.GetAsync($"https://api.service.example/?key={apiKey}&url={Uri.EscapeDataString(request.Url)}&premium={level > 0}",
            cancellationToken);
        if (answer.StatusCode == HttpStatusCode.Unauthorized) throw new NonRetryableException("wrong API key");
        if ((int)answer.StatusCode >= 500) throw new RemoteLoadException($"the service failed: {answer.StatusCode}");
        AddCredits(level > 0 ? 10 : 1);
        return PageResponse.FromText(200, request.Url, await answer.Content.ReadAsStringAsync(cancellationToken));
    }
}
```

- At most `MaxConcurrency` calls go at once, the plan's limit, whatever the scraper's `MaxConcurrency` is; the other pages
  wait in the loader, and no time limit counts that wait.
- No new call starts while the scraper pauses the loader. A call without an answer in `CallTimeout` (3 minutes) is lost.
- `CallAsync` returns the page as the site gave it, throws `RemoteLoadException` when the service had no page and
  `NonRetryableException` when another call cannot help. Any other exception of a call, such as a network error to the
  service, is a `RemoteLoadException`.
- A page that comes back blocked is loaded again one level up, to `MaxLevel`, and the host goes on from the level that got
  through. `CallAsync` calls `LevelUp` itself when the service says the site resisted. `Credits` sums what `AddCredits`
  counted, and `ToString()` shows it with the counters.

Such loaders come one package per service:
[z3l.Scraper.Loaders.ScrapingBee](https://github.com/romanti3172/z3l.Scraper/blob/main/src/z3l.Scraper.Loaders.ScrapingBee/README.md),
`new ScrapingBeePageLoader(new() { ApiKey = ..., MaxConcurrency = 10 })`.

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

Pass an `ILogger` to the constructor: progress, failures and retries (debug level) go there, and the last line of a
run names the most frequent reasons of `Stats.Reasons`. The scraper writes nothing to the console by itself.

```csharp
var scraper = new Scraper(loader, options, loggerFactory.CreateLogger("scraper"));
```

## License

Free for companies under USD 1M annual revenue and under 100 employees; others need a commercial license.
See [LICENSE.md](https://github.com/romanti3172/z3l.Scraper/blob/main/LICENSE.md). The commercial license is one payment of USD 1,000 for all z3l libraries, z3l.FastHtmlParser included.
