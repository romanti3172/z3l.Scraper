# ScraperAPI loader: open questions

The loader is written from ScraperAPI's documentation and has not met the real API. The documentation does not answer the
questions below, and the answers decide whether the loader does the right thing.

**Answering them needs a ScraperAPI API key, and there is none yet.** A free plan's key does for all but the ultra premium
level, which needs a paid plan. With the key in `Z3L_TEST_SCRAPERAPI`, `ScraperApiLiveTests` check most of them (about 30
credits, some minutes):

    dotnet test tests/z3l.Scraper.Loaders.ScraperApi.Tests -c Release

Then fix the loader where an answer differs, and write in the README what the API does beyond its documentation, as the
ScrapingBee README does.

## Questions

- [ ] **Does a 500 say why?** The loader goes one level up only when a 500 carries `sa-statuscode` 403, 429 or 503; the
  documentation shows that header only in the results of the async API. Without it, a site that refuses the standard
  proxies fails with RetriesExhausted after its retries, 70 seconds each, and never gets premium ones: ScraperAPI hides
  most blocks behind its 500. If nothing tells, choose: one level up after any 500, after two 500s of a host, or leave it
  to `Levels`. Checked by `TheSiteRefusing_AndItsOwn500_AreRetried_AndNotBilled`.
- [ ] **The site's 403 and 5xx: as they are, or as 500?** The loader reads a 403 as ScraperAPI's own (no credits, or more
  than `max_cost`) and fails the page at once. If a site's 403 comes as it is, tell the two apart by the body, and send the
  site's one level up instead. Same test.
- [ ] **`sa-final-url` and the site's headers.** Do they come with the page from this API? Without `sa-final-url` a
  redirected page keeps the request's URL, and its relative links resolve against the wrong page. Checked by
  `ARedirect_GivesTheFinalUrl_AndA404IsNotFound`.
- [ ] **The body's Content-Type.** Does the Content-Type of a POST or PUT body reach the site without `keep_headers`?
  Checked by `APostAPut_AndCookies_ReachTheSite`.
- [ ] **UTF-8.** Does a page in another charset (windows-1251) come as UTF-8, with `charset=utf-8` in Content-Type? The
  loader adds the charset when it is missing. What comes for an image or a PDF: its bytes as they are?
- [ ] **Error bodies.** Does the body of a 401, or of another error, repeat the key? The loader writes its own message for
  401 and hides the key in the others; check that no message of a failed page holds the key. Checked in part by
  `AWrongKey_AndACallAboveMaxCost_FailAtOnce`.
- [ ] **410 and 3xx.** Does a site's 410 come as 410 (the loader takes it as a page)? What comes for a redirect with
  `follow_redirect=false`? The loader retries any status it does not know.
- [ ] **Plans.** How many calls at once the paid plans allow (the documentation gives 5 for the free one); whether premium
  proxies work on the free plan; what error comes for ultra premium without a paid plan.
- [ ] **Billing.** Is a 403 above `max_cost` free? Is a cached ultra premium page billed in full?
