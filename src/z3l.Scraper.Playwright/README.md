# z3l.Scraper.Playwright

A headless browser page loader for [z3l.Scraper](https://www.nuget.org/packages/z3l.Scraper) on
[Microsoft Playwright](https://playwright.dev/dotnet/): for sites that draw their pages with scripts or block plain HTTP
clients. The scraper's queue, retries, response checks and throttling work on top, as with `HttpPageLoader`.

```
dotnet add package z3l.Scraper.Playwright --prerelease
```

```csharp
using z3l.Scraper;
using z3l.Scraper.Loading;
using z3l.Scraper.Playwright;

await using var loader = new PlaywrightPageLoader(new PlaywrightPageLoaderOptions
{
    Channel = "chrome",                                      // the installed Chrome; or download Chromium, see below
    MaxPages = 4,                                            // tabs loading at once
    Proxies = ProxyList.FromFile("proxies.txt"),             // a browser context per proxy
    UserAgents = UserAgentList.FromFile("useragents.txt"),   // every context takes one
    WaitForSelector = "div.product",                         // the content is drawn by scripts
});

var scraper = new Scraper(loader)
    .Route(PageType.Item, ctx => ctx.Emit(ctx.Html.XPathValue("//h1")));
```

## The browser

With `Channel = "chrome"` or `"msedge"` the browser installed on the machine is used. Otherwise download one once:

```csharp
PlaywrightPageLoader.InstallBrowser(PlaywrightBrowser.Chromium);    // returns 0 when it went well
```

or `pwsh bin/Debug/net10.0/playwright.ps1 install chromium` after a build. In Docker start from
`mcr.microsoft.com/playwright/dotnet`, which has the browsers and their libraries.

## How pages are loaded

- One browser, started with the first load and again if it crashes. `MaxPages` pages load at once, each in a new tab
  that is closed after the load; the scraper's `RequestTimeout` closes a tab that takes too long.
- A browser context per proxy (or one without proxies): its own cookies, cache and user agent, like one browser per
  address. A blocked answer rests the proxy for that site (`ProxyList.BlockPolicy`); with `ResetSessionOnBlock` (on by
  default) the context that got the block is replaced by a new one, with new cookies and the next user agent.
- GET requests navigate. The response is the page as the browser has drawn it (`PageResponse.Text`), after
  `WaitUntil` (`Load`), `WaitForSelector` for 2xx pages and your `OnPageLoaded` (scroll, click "show more"...). JSON,
  images and other non-HTML answers are returned as they came; downloads are fetched with the context's request API.
- POST and other methods go through the context's request API, with its cookies.
- Images, media and fonts are not loaded (`BlockedResourceTypes`).
- `ConfigureContext` sets the locale, viewport, time zone or geolocation of new contexts.

Browsers cannot log in to socks5 proxies, so a socks5 proxy with a login goes through a small relay on 127.0.0.1: the
browser talks to it without a login, the relay logs in to the proxy and passes the connection on (TLS stays end to end).

## License

The same as z3l.Scraper ([LICENSE.md](https://github.com/romanti3172/z3l.Scraper/blob/main/LICENSE.md)). Microsoft
Playwright is a separate package under the Apache License 2.0; the browsers it downloads have their own licenses.
