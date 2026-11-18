# z3l.Scraper.Loaders.ZyteApi

A page loader for [z3l.Scraper](https://github.com/romanti3172/z3l.Scraper) through [Zyte API](https://www.zyte.com/zyte-api/).
Zyte API keeps the proxies, the browsers and the ban handling; the scraper keeps the queue, the checks, the retries and the
handlers. The loader is a `ServicePageLoader`: at most `MaxConcurrency` calls go at once, no new call starts while the
scraper's handlers are behind, and a page that comes back blocked is loaded again in a browser.

**It has not met the real API yet: that needs a Zyte API key.** The loader follows Zyte API's documentation
([docs.zyte.com/zyte-api](https://docs.zyte.com/zyte-api/), read in November 2026), and its tests use a fake service that
answers as the documentation says. The tests against the real API are written and wait for the key; what they should answer
is in [TODO.md](TODO.md).

## Use

```csharp
await using var loader = new ZyteApiPageLoader(new ZyteApiPageLoaderOptions
{
    ApiKey = Environment.GetEnvironmentVariable("ZYTE_API_KEY")!,
    MaxConcurrency = 50,                               // about your rate limit ÷ 60 × the seconds a call takes
    Geolocation = "DE",
    NoBrowser = request => request.Label == "api",     // JSON stays on HTTP requests
});

var scraper = new Scraper(loader, new ScraperOptions { MaxConcurrency = 8 })     // 8 handlers at once
    .Route(PageType.Item, ctx => ctx.Emit(ctx.Html.XPathValue("//h1")));
```

- **The call.** Every call is a POST of JSON to `https://api.zyte.com/v1/extract`, with the key as the user name of HTTP basic
  authentication and no password.
  - An HTTP request (`httpResponseBody`) takes the request's method (`httpRequestMethod`: GET, POST, PUT, DELETE, OPTIONS,
    TRACE, PATCH or HEAD), its body as text (`httpRequestText`), its headers with the body's Content-Type
    (`customHttpRequestHeaders`), and its Cookie header as `requestCookies`.
  - A browser (`browserHtml`) sends GET only, with the request's Referer (`requestHeaders.referer`) and cookies; its other
    headers stay behind.
- **The page** comes back as the site gave it:
  - the site's status (`statusCode`): Zyte API answers 200 with a 404 or a 500 of the site in it, and the scraper's checks
    take them as they take any page;
  - the final URL (`url`);
  - the site's headers (`httpResponseHeaders`, asked for on every call), without Content-Encoding and Content-Length: Zyte
    API decompresses the body;
  - the body as the site sent it, decoded from Base64, so the charset of Content-Type or of `<meta>` reads it; or the
    browser's HTML as the text. Zyte API cuts both at 10 MB.
- **Levels.** A page goes again one level up when it comes back blocked (by the scraper's checks), or when Zyte API could not
  get it past a ban in time (520, not billed). The host then goes on from the level that got through.

  | Level | Zyte API | Per 1,000 pages, pay as you go (zyte.com, November 2026) |
  |---|---|---|
  | HTTP | `httpResponseBody` | $0.13 to $1.27, by the site's tier |
  | browser | `browserHtml` | $1.01 to $16.08, by the site's tier |

  A request a browser cannot send (a method other than GET, a body) goes as an HTTP request at every level, and so does one
  that `NoBrowser` names: a browser gives the HTML it shows, and a JSON or a file comes wrapped in it. Such a page does not
  move its host to the browser. `options.Levels.Remove(ZyteApiRequestType.Browser)` never pays for a browser.
- **Errors.** These fail the page at once:
  - a wrong or missing key (401);
  - a suspended account (403): no credit left, or a spending limit reached;
  - a bad call (400), or fields that do not go together (422);
  - a domain that cannot be reached (421), or that Zyte API does not load (451).

  These are retried at the same level:
  - Zyte API's limits (429, 503), without spending an attempt (see the limits below);
  - its timeout or its own error (500), and its download error (521);
  - a call without an answer in `CallTimeout` (3 minutes); Zyte API may still finish it, and bill it.

  Zyte API bills none of its errors. A message tells its problem type and detail (`400 /request/invalid: ...`), never the key.
- **Limits.** Zyte API limits calls per minute (3000 on standard plans), not calls at once. Its documentation reckons the
  calls at once as the limit ÷ 60 × the seconds a call takes on average: 100 for 2 seconds. Above its limits it answers 429
  (`/limits/over-user-limit`, `/limits/over-domain-limit`...), or 503 when the whole service is over its own
  (`/limits/over-global-limit`), with a `Retry-After`. It bills none of them and asks to retry them without end, with long
  waits. The page was not tried, so the scraper loads it again after Zyte API's `Retry-After` (up to `MaxRetryDelay`, 1
  minute), and such a retry takes none of its attempts: `MaxRetries` stays for the site. A page takes `RateLimitRetries`
  (20) of them, about a quarter of an hour of waits; past them a refusal counts as an attempt, so a run ends even when Zyte
  API refuses every call. Raise `RateLimitRetries` to wait longer. `Stats.RateLimited` counts the refusals, and
  `Stats.Reasons` names each limit (`rate limited: Zyte API limits the calls (429 /limits/over-user-limit): ...`).
- **More fields.**
  - `Geolocation` is the country of the call, two letters; a country other than the 20 standard ones costs more.
  - `Configure` sets any other field of the call, for the request and the level, on the call's JSON:
    `options.Configure = (request, level, json) => json["device"] = "mobile";`. `ipType`, `session`, `actions`,
    `javascript`, `followRedirect` and the others work the same way.
- **Credits.** Zyte API bills each page it gives, by the site's tier and the request type, and does not tell the price in its
  answer: `loader.Credits` stays 0. The answered calls in `loader.ToString()` are the billed ones.

## Tests against the real API

`ZyteApiLiveTests` run against the real API when `Z3L_TEST_ZYTEAPI` holds an API key; the $5 of free credit of a new
account is plenty. Without the key they are skipped, and the other tests use the fake service.

They have not run: there is no key yet. [TODO.md](TODO.md) holds what the loader takes from the documentation or guesses
where it says nothing, as questions for the first run with a key.
