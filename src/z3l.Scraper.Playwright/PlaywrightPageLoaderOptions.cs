using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Playwright;

/// <summary>Browser engine of <see cref="PlaywrightPageLoader"/>.</summary>
public enum PlaywrightBrowser
{
    /// <summary>Chromium (also Chrome and Edge through <see cref="PlaywrightPageLoaderOptions.Channel"/>).</summary>
    Chromium,
    /// <summary>Firefox.</summary>
    Firefox,
    /// <summary>WebKit.</summary>
    Webkit,
}

/// <summary>Settings of <see cref="PlaywrightPageLoader"/>.</summary>
public sealed class PlaywrightPageLoaderOptions
{
    /// <summary>Browser engine.</summary>
    public PlaywrightBrowser Browser { get; set; } = PlaywrightBrowser.Chromium;

    /// <summary>Run without a window.</summary>
    public bool Headless { get; set; } = true;

    /// <summary>
    /// An installed browser instead of the downloaded one: "chrome", "chrome-beta", "msedge"... (Chromium only). With it no
    /// <c>playwright install</c> is needed.
    /// </summary>
    public string? Channel { get; set; }

    /// <summary>Path of the browser executable, instead of the downloaded one.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>Extra command line arguments of the browser.</summary>
    public List<string> LaunchArgs { get; } = [];

    /// <summary>Pages (tabs) loading at once; more loads wait. Every page costs memory and CPU of the browser.</summary>
    public int MaxPages { get; set; } = 4;

    /// <summary>
    /// Proxies to rotate: every proxy has its own browser context (cookies, cache, user agent); a blocked answer rests the proxy for
    /// the site, failures rest it for all sites. Chromium does not support socks5 proxies with a login.
    /// </summary>
    public ProxyList? Proxies { get; set; }

    /// <summary>User agents: every browser context takes one. When null the browser's own user agent is sent.</summary>
    public UserAgentList? UserAgents { get; set; }

    /// <summary>Changes the options of every new browser context: locale, viewport, time zone, geolocation...</summary>
    public Action<BrowserNewContextOptions>? ConfigureContext { get; set; }

    /// <summary>Resource types that are not loaded: "image", "media", "font", "stylesheet"... Empty loads everything.</summary>
    public HashSet<string> BlockedResourceTypes { get; } = new(StringComparer.OrdinalIgnoreCase) { "image", "media", "font" };

    /// <summary>When the navigation counts as done.</summary>
    public WaitUntilState WaitUntil { get; set; } = WaitUntilState.Load;

    /// <summary>Longest navigation; the scraper's RequestTimeout ends a load earlier when it is shorter.</summary>
    public TimeSpan NavigationTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// A selector to wait for after a successful (2xx) navigation, for pages that draw their content with scripts. When it does not
    /// appear in <see cref="SelectorTimeout"/> the load fails and is retried.
    /// </summary>
    public string? WaitForSelector { get; set; }

    /// <summary>How long to wait for <see cref="WaitForSelector"/>.</summary>
    public TimeSpan SelectorTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Runs after the navigation and before the page is read: scroll, click "show more", accept a dialog...</summary>
    public Func<IPage, ScrapeRequest, CancellationToken, Task>? OnPageLoaded { get; set; }

    /// <summary>
    /// After a block the browser context that got it is replaced by a new one (new cookies and user agent) once its open pages
    /// are done.
    /// </summary>
    public bool ResetSessionOnBlock { get; set; } = true;

    /// <summary>Logger for browser starts, session resets and proxy rests.</summary>
    public ILogger? Logger { get; set; }
}
