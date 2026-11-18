# z3l.Scraper.Loaders.ZenRows

A page loader for [z3l.Scraper](https://github.com/romanti3172/z3l.Scraper) through the [ZenRows](https://www.zenrows.com)
Fetch API (formerly the Universal Scraper API). ZenRows keeps the proxies, the browsers and the anti-bot handling; the scraper
keeps the queue, the checks, the retries and the handlers. The loader is a `ServicePageLoader`: at most `MaxConcurrency` calls
go at once (the plan's limit), no new call starts while the scraper's handlers are behind, and a page that comes back blocked
is loaded again with premium proxies or a browser.

> **Not checked against the real API.** The loader follows ZenRows' documentation and its OpenAPI description as they were in
> November 2026. It has not met the real API yet: no key was at hand. `ZenRowsLiveTests` check it once there is one; what the
> documentation leaves open is listed at the end.

## Use

```csharp
await using var loader = new ZenRowsPageLoader(new ZenRowsPageLoaderOptions
{
    ApiKey = Environment.GetEnvironmentVariable("ZENROWS_KEY")!,
    MaxConcurrency = 20,                        // your plan's limit of calls at once
    RenderJs = false,                           // the default: the page as the server gives it; true: a browser
    CountryCode = "de",                         // with premium proxies only
    Levels = { ZenRowsLevel.Protected },        // after standard and premium proxies: premium proxies and a browser
});

var scraper = new Scraper(loader, new ScraperOptions { MaxConcurrency = 8 })     // 8 handlers at once
    .Route(PageType.Item, ctx => ctx.Emit(ctx.Html.XPathValue("//h1")));
```

- **The call.** Every call goes to `https://api.zenrows.com/v1/` with the key as `apikey`, the only way the Fetch API takes
  it, and with `original_status=true`. GET, POST and PUT go to the site with their body and its Content-Type. The request's
  headers go as they are, its Cookie header among them, with `custom_headers=true`; ZenRows keeps its browser's own
  (User-Agent, Accept-Language, Sec-Ch-Ua...).
- **The page** comes back as the site gave it:
  - its status (`original_status`);
  - the final URL (`Zr-Final-Url`);
  - the site's headers without their `Zr-` prefix, but for those of the transfer (`Content-Encoding`, `Content-Length`):
    the body comes decoded;
  - ZenRows' own headers (`X-Request-Credits`, `X-Request-Cost`, `X-Request-Id`, `Concurrency-Remaining`, `Zr-Cookies`)
    as they are.
- **Levels.** A page goes again one level up when it comes back blocked (by the scraper's checks), when ZenRows could not get
  it because the site refused (RESP001, not billed: a 403, 429 or 503 of the site, or a 422 when ZenRows does not tell), or
  when ZenRows loads its domain only with JavaScript or premium proxies (REQS002, refused before loading, not billed; at the
  top level it fails the page). The host then goes on from the level that got through. ZenRows' timeout (CTX0002), its own
  errors and a 5xx of the site (RESP001 with a 500) are retried at the same level. `loader.Credits` sums `X-Request-Credits`.

  | Level | Credits | With JavaScript |
  |---|---|---|
  | `Standard` | 1 | 5 |
  | `Premium` | 10 | 25 |
  | `Protected` | — | 25; always rendered |
  | `Auto` (`mode=auto`) | 1 to 25: ZenRows picks the proxies and the browser, and bills the configuration that got through | — |

  A failed call costs nothing; a 404 and a 410 are billed. JavaScript is rendered for GET only: ZenRows renders no POST or
  PUT. `CountryCode` goes as `proxy_country` with premium proxies and `Auto`, the only levels ZenRows takes it with: leave
  `Standard` out when every call must come from the country.
- **Errors.** These fail the page at once:
  - a missing or wrong key, no credits left, a key at its cap, a plan without the feature (401, 402: AUTH...);
  - a bad call (400: REQS004 and the like); its detail goes into the message, without the key;
  - a page bigger than the plan allows (413, RESP005);
  - an error of the site's configuration or certificate (RESP008), which ZenRows does not retry either.

  These are retried:
  - too many calls at once (429: AUTH006, AUTH008);
  - ZenRows' timeout (504, CTX0002), its own errors (500, ERR...), no free capacity (503, RESP009);
  - this IP blocked for too many failed calls (403, BLK0001): it lifts in a few minutes;
  - a call without an answer in `CallTimeout`.

  A 404 or a 410 is the page: the site has no such page (RESP002), or its domain does not resolve (RESP007).
- **More parameters.**
  - `WaitFor` is a selector to wait for, with JavaScript.
  - `Configure` sets anything else of the API, for the request and the level:
    `options.Configure = (request, level, parameters) => parameters["session_id"] = "42";` keeps one IP for 10 minutes;
    `wait`, `block_resources`, `js_instructions` and the others work the same way.
  - With `allowed_status_codes`, ZenRows gives the site's page for those statuses too (billed), and the loader returns it
    with its status.
  - `json_response`, `response_type`, `css_extractor`, `outputs` and `screenshot` change the body: the page is then what
    ZenRows makes of it.
- **Timeout.** ZenRows works on a call for up to 3 minutes and has no parameter to give up sooner, so `CallTimeout` is 3
  minutes 10 seconds by default. A call given up sooner keeps its place in the plan's concurrency until ZenRows is done with
  it, and the next calls may get 429.

## Tests against the real API

`ZenRowsLiveTests` run against the real API when `Z3L_TEST_ZENROWS` holds an API key; a run should spend about 80 credits.
Without the key they are skipped, and the other tests use a fake service that answers as the documentation says. They have not
run yet.

What the documentation leaves open, and the loader assumes:
- With `original_status=true` the status of every answer is the site's, a page ZenRows could not get (RESP001) included: the
  docs advise it to see the site's status behind a 422, and say no more. Without it, every such page would be a 422, and a
  5xx of the site would go a level up too.
- ZenRows' errors come as `application/problem+json` with a `code` (the OpenAPI description; the docs show no whole example).
  An answer without such a code is taken for the site's page.
- `X-Request-Credits` holds the credits of a call. Only the OpenAPI description lists it; the docs' table of headers has
  `X-Request-Cost` alone, which the OpenAPI description gives in US dollars.
- The Content-Type of a POST or PUT body reaches the site: the OpenAPI description says the body goes to the site as it is,
  and nothing of its type.
- ZenRows renders no JavaScript for POST and PUT: the docs of its Python SDK say so, the API reference does not.
- `proxy_country` goes with `mode=auto` without premium proxies: the docs say both that the country needs premium proxies and
  that it works with `mode=auto`.
- A 404 comes with ZenRows' error document (RESP002) rather than the site's page; the loader returns either as the page.
- Too many calls at once is AUTH006 in the docs and AUTH008 in the OpenAPI description; both are retried.
