# z3l.Scraper.Loaders.ScrapingBee

A page loader for [z3l.Scraper](https://github.com/romanti3172/z3l.Scraper) through [ScrapingBee](https://www.scrapingbee.com).
ScrapingBee keeps the proxies, the browsers and the captchas; the scraper keeps the queue, the checks, the retries and the
handlers. The loader is a `ServicePageLoader`: at most `MaxConcurrency` calls go at once (the plan's limit), no new call
starts while the scraper's handlers are behind, and a page that comes back blocked is loaded again with stronger proxies.

## Use

```csharp
await using var loader = new ScrapingBeePageLoader(new ScrapingBeePageLoaderOptions
{
    ApiKey = Environment.GetEnvironmentVariable("SCRAPINGBEE_KEY")!,
    MaxConcurrency = 10,                        // your plan's limit of calls at once
    RenderJs = false,                           // the default: the page as the server gives it; true: a browser
    CountryCode = "de",
    Levels = { ScrapingBeeProxy.Stealth },      // after classic and premium proxies: the stealth pool for the hardest sites
});

var scraper = new Scraper(loader, new ScraperOptions { MaxConcurrency = 8 })     // 8 handlers at once
    .Route(PageType.Item, ctx => ctx.Emit(ctx.Html.XPathValue("//h1")));
```

- **The call.** Every call goes to `https://app.scrapingbee.com/api/v1`, with the key in `Authorization: Bearer`. GET, POST and
  PUT go to the site with their body. The request's headers go as `Spb-` headers with `forward_headers`, and its Cookie
  header goes as `cookies`.
- **The page** comes back as the site gave it:
  - a 2xx as 200, and 404, 410 and 413 as they are;
  - the final URL (`Spb-resolved-url`);
  - the site's headers without their `Spb-` prefix;
  - ScrapingBee's own headers (`Spb-cost`, `Spb-request-id`) as they are.
- **Levels.** A page goes again with the proxies of the next level when it comes back blocked (by the scraper's checks), or
  when ScrapingBee could not scrape it because the site refused (500, not billed, with `Spb-initial-status-code` 403, 429 or
  503). The host then goes on from the level that got through. ScrapingBee's other 500s are retried at the same level: a
  timeout, a site that does not answer, a 5xx of the site. `loader.Credits` sums `Spb-cost`.

  | Level | Credits | With JavaScript |
  |---|---|---|
  | classic | 1 | 5 |
  | premium | 10 | 25 |
  | stealth | — | 75; always rendered, without the request's headers and cookies |
- **Errors.** These fail the page at once:
  - a wrong key, or no credits left (401);
  - a banned account, or a blocked destination (403);
  - a bad call (400).

  These are retried:
  - too many calls at once (429);
  - a call without an answer in `CallTimeout` (3 minutes).
- **More parameters.**
  - `WaitFor` is a selector to wait for, with JavaScript.
  - `Configure` sets anything else of the API, for the request and the level:
    `options.Configure = (request, level, parameters) => parameters["session_id"] = "42";` keeps one IP for 5 minutes;
    `device`, `js_scenario`, `block_resources`, `wait` and the others work the same way.
- **Timeout.** A `CallTimeout` shorter than ScrapingBee's 140 seconds is sent as its `timeout`, 5 seconds less, so the service
  answers before the loader gives the call up.

## Tests against the real API

`ScrapingBeeLiveTests` run against the real API when `Z3L_TEST_SCRAPINGBEE` holds an API key. A trial key does: it gives 1000
credits and 5 calls at once, and a run spends about 90 credits. Without the key they are skipped, and the other tests use a
fake service that answers as the real one does.

What the API does beyond its documentation, and the loader relies on:
- The site's status comes in `Spb-initial-status-code` also when ScrapingBee answers 500: 403 when the site refused, 500 when
  it timed out or did not answer.
- After premium proxies retry a refused page for about 30 seconds, the 500 can come with 500 there instead of the site's 403.
- The answer to a wrong key repeats the key, so the loader writes its own message for 401.
