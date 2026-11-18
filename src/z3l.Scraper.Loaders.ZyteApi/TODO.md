# Zyte API loader: to do

The loader follows Zyte API's documentation (read in November 2026) and has not met the real API: **it needs a Zyte API
key.** A new account gets $5 of free credit for its first month, plenty for the live tests (about 20 calls, a few of them in
a browser).

## With a key

- [ ] Run the live tests:
  ```powershell
  $env:Z3L_TEST_ZYTEAPI = "<the key>"
  dotnet test tests/z3l.Scraper.Loaders.ZyteApi.Tests -c Release --filter "FullyQualifiedName~ZyteApiLiveTests"
  ```
- [ ] Answer the questions below; fix the loader and its fake service where the API differs.
- [ ] Write what the API does into the package README, drop "has not met the real API" there and in the main README, and
  `IsPackable` false in the csproj. Then delete this file.

## Questions for the first run

- [ ] **Errors.** Is an error `application/problem+json` with `type`, `title`, `status` and `detail`, as the reference says?
  Does the answer to a wrong key (401) repeat the key? The loader writes its own message for 401, and hides the key in the
  texts it quotes.
- [ ] **The site's refusals.** Does a 403, 429 or 503 of the site come in a 200 (`statusCode`), or as a 520 after Zyte API's
  own retries? Either way the page goes to the browser: a 520 by the loader, a 403 or a 429 page by the scraper's checks.
- [ ] **The site's own 500.** Does it come in a 200 with `statusCode` 500, a bad response that is no ban?
  `TheSitesOwn500_IsRetriedOverHttp` expects that, and no browser call.
- [ ] **No body.** Does the answer to HEAD, or to a 204 or a 304 of the site, have `httpResponseBody`? The loader takes a
  missing one as an empty body there, and as a failure elsewhere.
- [ ] **statusCode.** Is it in every answer, a browser's too? The loader takes a missing one as 200.
- [ ] **A browser's headers.** Does `httpResponseHeaders` with `browserHtml` give the main response's headers, its
  Content-Type included?
- [ ] **JSON in a browser.** Does a JSON or a file loaded with `browserHtml` come wrapped in HTML, as `NoBrowser` assumes?
- [ ] **Retry-After.** What do 429, 503 and 520 put in it? The loader does not wait for it: the scraper's `RetryDelay` does,
  and a call Zyte API limits spends one of the page's attempts. A core change is under way: a retry after Retry-After that
  spends no attempt.
- [ ] **Slow calls.** How long can a call take before Zyte API answers 500 `/server/timed-out`? The documentation gives no
  client timeout; the loader keeps the base's `CallTimeout` of 3 minutes.
- [ ] **Zyte API's own headers.** Does its answer carry a request id, or the price, worth keeping on the page? The
  documentation names none, and `Credits` stays 0.

## Choices to look at again

- [ ] **One 520 sends the page to the browser**, and a page that gets through there moves its host to the browser: eight to
  thirteen times the price, tier for tier. Zyte API says 520s are normal on some sites; retry at the same level first?
- [ ] **521** (`/download/internal-error`) is retried at the same level. The documentation says some are bans: send it to
  the browser like a 520?
