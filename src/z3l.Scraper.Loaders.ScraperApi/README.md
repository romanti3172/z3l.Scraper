# z3l.Scraper.Loaders.ScraperApi

A page loader for [z3l.Scraper](https://github.com/romanti3172/z3l.Scraper) through [ScraperAPI](https://www.scraperapi.com).
ScraperAPI keeps the proxies, the browsers and the captchas; the scraper keeps the queue, the checks, the retries and the
handlers. The loader is a `ServicePageLoader`: at most `MaxConcurrency` calls go at once (the plan's limit), no new call
starts while the scraper's handlers are behind, and a page that comes back blocked is loaded again with stronger proxies.

**The loader follows [ScraperAPI's documentation](https://docs.scraperapi.com) and has not met the real API yet.** What it
assumes beyond the documentation is listed at the end; the tests against the real API check it.

## Use

```csharp
await using var loader = new ScraperApiPageLoader(new ScraperApiPageLoaderOptions
{
    ApiKey = Environment.GetEnvironmentVariable("SCRAPERAPI_KEY")!,
    MaxConcurrency = 5,                                 // your plan's limit of calls at once: 5 on the free plan
    RenderJs = false,                                   // the default: the page as the server gives it; true: a browser
    CountryCode = "us",
    Levels = { ScraperApiProxy.UltraPremium },          // after standard and premium proxies: ultra premium (paid plans)
});

var scraper = new Scraper(loader, new ScraperOptions { MaxConcurrency = 8 })     // 8 handlers at once
    .Route(PageType.Item, ctx => ctx.Emit(ctx.Html.XPathValue("//h1")));
```

- **The call.** Every call goes to `https://api.scraperapi.com/`, the key first (`api_key`, the only way the API takes it) and
  the page's `url` last, as the documentation asks. A client of yours (`HttpClient`) that logs the URLs of its calls logs the
  key. GET, POST and PUT go to the site with their body and its Content-Type.
- **Headers.** The request's headers, the Cookie header too, go to the site with `keep_headers=true` (`ForwardHeaders`, on by
  default). Host, Accept-Encoding and the connection's own headers do not go. ScraperAPI warns that headers of your own
  replace its own and often lower the success rate: set only those the site needs. Ultra premium proxies take no headers.
- **The page** comes back as the API gives it:
  - 200, and 404 and 410 as they are;
  - the body in UTF-8, which the API makes of every page: an HTML or XML Content-Type without a charset gets
    `charset=utf-8`, so the page's own `<meta charset>` does not mislead the decoding;
  - the headers of the answer, `sa-credit-cost` among them;
  - the final URL from `sa-final-url`, when it comes; the request's URL otherwise.
- **Levels.** A page goes again with the proxies of the next level when it comes back blocked (by the scraper's checks), and
  the host then goes on from the level that got through. `loader.Credits` sums `sa-credit-cost`.

  | Level | Credits | With JavaScript |
  |---|---|---|
  | standard | 1 | 10 |
  | premium (`premium=true`) | 10 | 25 |
  | ultra premium (`ultra_premium=true`, paid plans) | 30 | 75; no headers; a page up to 10 minutes old from the cache |

  Some domains cost more: Amazon, Walmart and eBay 5, Google and Bing 25, LinkedIn 30.
- **500.** The API tries a page for up to 70 seconds, then answers 500, not billed. The documentation gives no reason with
  it: the site may have refused, failed or not answered. So a 500 is retried at the same level, and only a 500 with
  `sa-statuscode` 403, 429 or 503 (the site refused) goes one level up. When your sites refuse the standard proxies, start
  higher: `options.Levels.Remove(ScraperApiProxy.Standard)`.
- **Errors.** These fail the page at once:
  - a wrong key (401);
  - no credits left this month, or a call that costs more than `max_cost` (403);
  - a bad call (400).

  These are retried:
  - too many calls at once (429);
  - a call without an answer in `CallTimeout` (3 minutes).

  The messages never hold the key: the loader writes its own for 401, and hides the key in the bodies it quotes.
- **Timeout.** The API has no timeout parameter, and bills a call given up before its 70 seconds. Keep `CallTimeout` above
  that: the loader warns in its log when it is shorter.
- **More parameters.**
  - `WaitForSelector` is a selector to wait for, with JavaScript (`wait_for_selector`).
  - `Configure` sets anything else of the API, for the request and the level. Sessions do not go with premium proxies:
    `options.Configure = (request, level, parameters) => { if (level == 0) parameters["session_number"] = "42"; };` keeps one
    IP until 15 minutes after its last use. `device_type`, `max_cost`, `follow_redirect`, `cache_control=no-cache` and the
    others work the same way.

## Tests against the real API

`ScraperApiLiveTests` run against the real API when `Z3L_TEST_SCRAPERAPI` holds an API key. A free plan's key does: a run
spends about 30 credits and takes some minutes, since a page the API cannot get takes it 70 seconds. Without the key they are
skipped, and the other tests use a fake service that answers as the documentation says.

Nobody has run them yet. What the loader assumes beyond the documentation, and they check:
- The site's headers come back with the page, and `sa-final-url` gives the final URL: the documentation shows both only in
  the results of the async API.
- A 500 may carry the site's status in `sa-statuscode`; without it no 500 sends a page one level up.
- The site's own statuses other than 404 (403, 5xx) come as 500, not as they are: a 403 as it is would read as the API's own
  403 and fail the page at once.
- The Content-Type of a POST or PUT body reaches the site without `keep_headers`.
