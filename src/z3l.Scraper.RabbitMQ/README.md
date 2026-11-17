# z3l.Scraper.RabbitMQ

Remote page loaders for [z3l.Scraper](https://github.com/romanti3172/z3l.Scraper) through RabbitMQ. One scraper keeps
the list of pages (the queue, the seen pages, the limits, the state file) and parses everything; loader workers on other
machines only load pages, each with its own proxies, user agents or browser.

```
            scraper: queue, duplicates, throttling, checks, handlers
            RabbitMqPageLoader ──► task queue per page type ──► workers (HttpPageLoader, PlaywrightPageLoader...)
                     ▲                                               │
                     └──────────────── result queue ◄────────────────┘
```

## The scraper

```csharp
await using var loader = new RabbitMqPageLoader(new RabbitMqPageLoaderOptions { Uri = new("amqp://user:pass@rabbit:5672/") })
    .Queue("loaders.http", PageType.File)      // files: plain HTTP workers
    .Queue("loaders.browser");                // every other label: browser workers

var scraper = new Scraper(loader, new ScraperOptions { MaxConcurrency = 8 })     // 8 handlers at once
    .Route(PageType.Listing, ctx => ctx.Enqueue(ctx.Html.XPathValues("//a[@class='product']/@href"), PageType.Item))
    .Route(PageType.Item, ctx => ctx.Emit(ctx.Html.XPathValue("//h1")));

await foreach (var item in scraper.RunAsync(new ScrapeRequest("https://shop.example/catalog", PageType.Listing)))
{
    // save it
}
```

- Every page goes to the queue at once: the loader is an `IQueuePageLoader`, so `MaxConcurrency` limits only the handlers,
  and `RequestTimeout` does not apply.
- A label goes to the queue that names it, others to the queue without labels; a label no queue takes fails at once.
- `ScrapePriority` becomes the message priority (the task queues are declared with `x-max-priority` 2).
- When more than `MaxLoadedNotHandled` (100) pages wait for a handler, the scraper pauses the loader: it stops taking
  answers (`ResultPrefetch`, 50, are taken at most) and they wait in the result queue.
- The worker that loaded a page is in the `X-Z3l-Worker` response header. `loader.ToString()` shows what was sent,
  answered, lost and what each queue has pending.

## Lost tasks: timesync, not timeouts

A queue can be long (browser workers are slow, or none runs for a while), so the time a page waits in it must not count.
The loader finds lost tasks with timesync markers:

1. Every `TimesyncInterval` (2 min), while a task queue has tasks not yet taken, a marker with the next number goes to it,
   with the lowest priority, so it is behind every task sent before it.
2. A worker that takes a marker answers it at once. When marker N comes back, every task sent before it was taken by a
   worker by then, and gets a deadline: **the time the marker came back + `ExecutionTimeout` (5 min)**.
3. A task without an answer by its deadline is lost (its worker died or hung): its load throws `RemoteLoadException`, and
   the scraper sends it again. A late answer to it is ignored.
4. While the loader is paused the deadlines stand still (the answers wait in the broker then), and no task is lost while
   answers wait in the result queue: they may be the ones a deadline waits for. Answers are handled one at a time in the
   order they came, so a marker is never handled before the results that came before it.

A marker covers the tasks whose publish ended before its number was taken; the time a task was created does not matter.
A burst of 10,000 tasks that the workers go through for 30 minutes is not lost when the marker behind it comes back after
those 30 minutes: the deadline counts from then. Times live only in the scraper, so the clocks of the machines do not matter.

A worker that stops or loses its connection does not acknowledge the tasks it was loading, and the broker gives them to
another worker at once; timesync is for a worker that hangs with its connection open.

## The workers

```csharp
var proxies = ProxyList.FromFile("proxies.txt");            // or await ProxyList.FromSourceAsync(...) for an API
await using var http = new HttpPageLoader(new HttpPageLoaderOptions { Proxies = proxies });

var rules = new ResponseRulesCollection();                   // optional: lets the worker rest a proxy that got a block page
rules.Default.BlockedMarkers.Add("captcha");

await using var worker = new RabbitMqLoaderWorker(http, new RabbitMqLoaderWorkerOptions
{
    Uri = new("amqp://user:pass@rabbit:5672/"),
    Queue = "loaders.http",
    Concurrency = 16,          // pages at once; the worker takes no more tasks than this from the broker
    Rules = rules,
});
await worker.RunAsync(stoppingToken);
```

- Any `IPageLoader` works: `HttpPageLoader`, `PlaywrightPageLoader`, your own.
- The worker takes its proxies itself (a file, a URL, an API through `ProxyList.FromSourceAsync`); the scraper does not
  report blocks to it. With `Rules` the worker runs those checks itself and tells its loader about blocks and broken pages,
  so a blocked proxy rests. The scraper checks every page again with its own rules and retries as usual.
- A task is acknowledged after its answer is sent. A load that throws is answered as an error the scraper retries
  (`NonRetryableException`: fails at once); one slower than `LoadTimeout` (2 min) too.
- Pages from a browser come as text and stay text; others come as bytes. Answers are compressed with brotli.
- An answer larger than `MaxMessageSize` (16 MiB, RabbitMQ 4's default `max_message_size`) is answered as an error that is
  not retried: the broker would drop it, and the page would be sent again and again.

## Message format

Task queues are durable, declared with `x-max-priority` 2 by both sides (`DeclareQueues` / `DeclareQueue` off when you
declare them yourself). A task is JSON (`type` `load`, `MessageId` the task id, `ReplyTo` the result queue); a marker is
JSON with `type` `timesync`, answered as it came. An answer is `type` `result`, brotli of a 4-byte header length, the JSON
header (id, status, url, content type, headers, text flag, error, worker) and the page bytes.

## Tests

`tests/z3l.Scraper.RabbitMQ.Tests` run against a real broker: set `Z3L_TEST_RABBITMQ=amqp://user:password@host:port/`.
Without it those tests are inconclusive; CI runs them with a RabbitMQ service container.
