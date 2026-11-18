# z3l.Scraper.Loaders.ZenRows: to do

The loader is written from ZenRows' documentation and its OpenAPI description (November 2026) and has not met the real API.

**Checking it needs a ZenRows API key.** A trial key should do; a run of the live tests should spend about 80 credits. Set
`Z3L_TEST_ZENROWS` to the key and run `ZenRowsLiveTests`:

```powershell
$env:Z3L_TEST_ZENROWS = "<key>"; dotnet test tests/z3l.Scraper.Loaders.ZenRows.Tests -c Release --filter ZenRowsLiveTests
```

Then answer the questions below. Where the API differs, fix the loader and the fake service of `ZenRowsTests`, and write what
the API does beyond its documentation into the README, as the ScrapingBee README does. When all are answered, drop the "not
checked" notes from the README, the main README, the remarks of `ZenRowsPageLoader`, the csproj and `ZenRowsLiveTests`, and
this file.

## Questions

- [ ] **The status with `original_status=true`.** Is it the site's, also for a page ZenRows could not get (RESP001)? The loader
  sends a page with RESP001 one level up, unless the site's status is a 5xx other than 503. Does a site that does not answer
  come as RESP001 with a 422 (it goes a level up then) or as CTX0002 (same level)?
  `TheSiteRefusing_GoesToPremiumProxies_AndItsOwn500DoesNot`
- [ ] **The errors.** Does every error of ZenRows come as `application/problem+json` with a `code`? An answer without one is
  taken for the site's page. What do `title` and `detail` hold, and does any of them repeat the key?
  `AWrongKey_AndABadParameter_FailAtOnce`
- [ ] **The credits.** Is `X-Request-Credits` sent, with 1, 5, 10 or 25? Only the OpenAPI description lists it; without it
  `Credits` stays 0. Is `X-Request-Cost` in US dollars?
  `APage_ComesAsTheSiteGaveIt_WithTheRequestsHeaders`, `JavaScript_IsRendered_WhenAsked`, `TheProtectedAndTheAutoLevels_LoadAPage`
- [ ] **404 and 410.** Do they come with the site's page or with ZenRows' error document (RESP002)? Both are billed, the docs
  say. `ARedirect_GivesTheFinalUrl_AndA404IsNotFound`
- [ ] **The body of a POST or PUT.** Does its Content-Type reach the site? `APost_AndCookies_ReachTheSite`
- [ ] **JavaScript with POST and PUT.** Does ZenRows really refuse `js_render` with them? Only the docs of its Python SDK say so;
  the loader leaves it out.
- [ ] **`proxy_country` with `mode=auto`.** Does it work without `premium_proxy`? The docs say both that the country needs
  premium proxies and that it works with `mode=auto`.
- [ ] **Too many calls at once.** AUTH006 (the docs) or AUTH008 (the OpenAPI description)? Both are retried; only the message
  differs.
- [ ] **The site's headers.** Do they all come with the `Zr-` prefix, Set-Cookie as `Zr-Set-Cookie`? What is `Zr-Cookies`? Is
  the Content-Type of a page answer the site's? The loader takes `Zr-Content-Type` first.
