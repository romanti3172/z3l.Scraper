using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using z3l.FastHtmlParser;
using z3l.Scraper.Loading;

[assembly: Parallelize(Scope = ExecutionScope.ClassLevel)]

namespace z3l.Scraper.Playwright.Tests;

/// <summary>
/// Runs a real browser: the downloaded Chromium (<c>playwright.ps1 install chromium</c>) or an installed one named by the
/// Z3L_PLAYWRIGHT_CHANNEL variable ("chrome", "msedge"). Without a browser the tests are inconclusive.
/// </summary>
[TestClass]
public sealed class PlaywrightTests
{
    private static Site _site = null!;

    [ClassInitialize]
    public static async Task StartSiteAsync(TestContext _) => _site = await Site.StartAsync();

    [ClassCleanup]
    public static async Task StopSiteAsync() => await _site.DisposeAsync();

    private static PlaywrightPageLoaderOptions Options() => new()
    {
        Channel = Environment.GetEnvironmentVariable("Z3L_PLAYWRIGHT_CHANNEL") is { Length: > 0 } channel ? channel : null,
        MaxPages = 2,
        NavigationTimeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>Loads, or ends the test as inconclusive when no browser is installed.</summary>
    private static async Task<PageResponse> LoadAsync(PlaywrightPageLoader loader, ScrapeRequest request)
    {
        try
        {
            return await loader.LoadAsync(request, CancellationToken.None);
        }
        catch (NonRetryableException ex) when (ex.Message.Contains("is not installed", StringComparison.Ordinal))
        {
            Assert.Inconclusive(ex.Message);
            throw;
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ReturnsThePageAsTheBrowserDrewIt_AfterTheSelectorAppears()
    {
        var options = Options();
        options.WaitForSelector = "div.product";
        await using var loader = new PlaywrightPageLoader(options);

        var response = await LoadAsync(loader, _site.Url("/rendered"));

        Assert.AreEqual(200, response.StatusCode);
        StringAssert.Contains(response.Text, "<div class=\"product\">Drawn by a script</div>");
        Assert.AreEqual("text/html; charset=utf-8", response.ContentType);
        Assert.IsFalse(response.Headers.ContainsKey("content-length"));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task StatusRedirectsAndNonHtmlBodies()
    {
        await using var loader = new PlaywrightPageLoader(Options());

        var missing = await LoadAsync(loader, _site.Url("/missing"));
        Assert.AreEqual(404, missing.StatusCode);

        var moved = await LoadAsync(loader, _site.Url("/old"));
        Assert.AreEqual(200, moved.StatusCode);
        Assert.AreEqual(_site.Url("/rendered"), moved.Url);

        var json = await LoadAsync(loader, _site.Url("/api"));
        Assert.AreEqual("""{"price":42}""", json.Text);
        StringAssert.StartsWith(json.ContentType, "application/json");

        var file = await LoadAsync(loader, _site.Url("/file"));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, file.Body);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task PostGoesThroughTheContextWithItsCookies()
    {
        await using var loader = new PlaywrightPageLoader(Options());

        await LoadAsync(loader, _site.Url("/login"));     // sets a cookie in the context
        var response = await LoadAsync(loader, new ScrapeRequest(_site.Url("/echo"))
        {
            Method = HttpMethod.Post,
            Body = "q=tea",
            Headers = new Dictionary<string, string> { ["X-Test"] = "yes" },
        });

        Assert.AreEqual("POST q=tea session=42 x-test=yes", response.Text);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task BlockedImagesAreNotLoaded()
    {
        await using var loader = new PlaywrightPageLoader(Options());
        var before = _site.Hits("/pixel.png");

        await LoadAsync(loader, _site.Url("/with-image"));

        Assert.AreEqual(before, _site.Hits("/pixel.png"));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task UserAgentPerSession_ABlockStartsANewSession()
    {
        var options = Options();
        options.UserAgents = new UserAgentList(["UA-1", "UA-2"]);
        await using var loader = new PlaywrightPageLoader(options);

        var first = await LoadAsync(loader, _site.Url("/ua"));
        var second = await LoadAsync(loader, _site.Url("/ua"));
        StringAssert.Contains(first.Text, "UA-1");
        StringAssert.Contains(second.Text, "UA-1");          // one context, one user agent

        loader.ReportBlocked(_site.Url("/ua"), second, "captcha");
        var third = await LoadAsync(loader, _site.Url("/ua"));
        StringAssert.Contains(third.Text, "UA-2");
        Assert.AreEqual(1, loader.SessionCount);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Proxies_AContextPerProxy_ABlockedProxyRestsForTheSite()
    {
        await using var blocking = await Site.StartProxyAsync(403, "<html><body>Access denied</body></html>");
        await using var good = await Site.StartProxyAsync(200, "<html><body>ok</body></html>");
        var options = Options();
        options.Proxies = new ProxyList([blocking.BaseUrl, good.BaseUrl]);
        await using var loader = new PlaywrightPageLoader(options);

        var denied = await LoadAsync(loader, "http://shop.test/1");
        Assert.AreEqual(403, denied.StatusCode);
        loader.ReportBlocked("http://shop.test/1", denied, "403");

        for (var i = 2; i <= 4; i++) Assert.AreEqual(200, (await LoadAsync(loader, $"http://shop.test/{i}")).StatusCode);

        Assert.AreEqual(1, blocking.Hits("/1"));
        Assert.AreEqual(3, good.ProxiedRequests.Count(r => r.StartsWith("shop.test/", StringComparison.Ordinal) && !r.EndsWith("favicon.ico", StringComparison.Ordinal)));
        Assert.AreEqual(1, options.Proxies.Status().Single(s => s.Address.Port == new Uri(blocking.BaseUrl).Port).BlockedSites);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Socks5WithALogin_GoesThroughTheLocalRelay()
    {
        await using var socks = FakeSocks5.Start("user", "secret", new Uri(_site.BaseUrl).Port);
        var options = Options();
        options.Proxies = new ProxyList([$"socks5://user:secret@127.0.0.1:{socks.Port}"]);
        await using var loader = new PlaywrightPageLoader(options);

        var response = await LoadAsync(loader, "http://shop.test/title");

        Assert.AreEqual(200, response.StatusCode);
        StringAssert.Contains(response.Text, "<h1>Tea</h1>");
        Assert.IsTrue(socks.Targets.Contains("shop.test:80"), "the host name reached the proxy: " + string.Join(", ", socks.Targets));
    }

    /// <summary>A real SOCKS5 proxy with a login (3proxy): Z3L_TEST_SOCKS5=socks5://user:password@host:port.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Socks5WithALogin_RealProxy()
    {
        if (Environment.GetEnvironmentVariable("Z3L_TEST_SOCKS5") is not { Length: > 0 } line || !ProxyAddress.TryParse(line, out var proxy))
        {
            Assert.Inconclusive("Set Z3L_TEST_SOCKS5=socks5://user:password@host:port to test a real proxy");
            return;
        }
        var options = Options();
        options.Proxies = new ProxyList([line]);
        await using var loader = new PlaywrightPageLoader(options);

        var response = await LoadAsync(loader, "https://api.ipify.org/");

        Assert.AreEqual(200, response.StatusCode);
        var addresses = await System.Net.Dns.GetHostAddressesAsync(proxy.Host);
        Assert.IsTrue(addresses.Any(a => response.Text.Contains(a.ToString(), StringComparison.Ordinal)), "the site saw the proxy's address: " + response.Text);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task WorksUnderTheScraper_CancelledLoadsCloseTheirPages()
    {
        var options = Options();
        await using var loader = new PlaywrightPageLoader(options);
        await LoadAsync(loader, _site.Url("/rendered"));     // inconclusive here when there is no browser

        var scraperOptions = new ScraperOptions { RetryDelay = TimeSpan.Zero, ProgressInterval = null, MaxRetries = 0, RequestTimeout = TimeSpan.FromSeconds(1) };
        var scraper = new Scraper(loader, scraperOptions)
            .Route("default", ctx => ctx.Emit(ctx.Html.XPathValue("//h1") ?? ""));

        var result = await scraper.RunToListAsync([_site.Url("/slow"), _site.Url("/title")]);

        CollectionAssert.AreEqual(new[] { "Tea" }, result.Items.ToArray());
        Assert.AreEqual(1, result.Stats.Failed);            // /slow ran out of time
        var stillWorks = await LoadAsync(loader, _site.Url("/title"));
        Assert.AreEqual(200, stillWorks.StatusCode);
    }
}

/// <summary>A small site on Kestrel, or a fake HTTP proxy that answers every request itself.</summary>
internal sealed class Site : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, int> _hits = new();

    private Site(WebApplication app) => _app = app;

    public string BaseUrl { get; private set; } = "";
    public ConcurrentQueue<string> ProxiedRequests { get; } = new();
    public string Url(string path) => BaseUrl + path;
    public int Hits(string path) => _hits.GetValueOrDefault(path);

    public static async Task<Site> StartAsync()
    {
        var (app, site) = Create();
        app.MapGet("/rendered", () => Html("""
            <html><body><h1>Shop</h1>
            <script>setTimeout(() => { const d = document.createElement('div'); d.className = 'product'; d.textContent = 'Drawn by a script'; document.body.appendChild(d); }, 300);</script>
            </body></html>
            """));
        app.MapGet("/title", () => Html("<html><body><h1>Tea</h1></body></html>"));
        app.MapGet("/missing", () => Html("<html><body>No such page</body></html>", 404));
        app.MapGet("/old", () => Results.Redirect("/rendered"));
        app.MapGet("/api", () => Results.Text("""{"price":42}""", "application/json"));
        app.MapGet("/file", (HttpContext http) =>
        {
            http.Response.Headers.ContentDisposition = "attachment; filename=data.bin";
            return Results.Bytes([1, 2, 3, 4], "application/octet-stream");
        });
        app.MapGet("/login", (HttpContext http) =>
        {
            http.Response.Cookies.Append("session", "42");
            return Html("<html><body>Welcome</body></html>");
        });
        app.MapPost("/echo", async (HttpContext http) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            var body = await reader.ReadToEndAsync();
            return Results.Text($"POST {body} session={http.Request.Cookies["session"]} x-test={http.Request.Headers["X-Test"]}");
        });
        app.MapGet("/with-image", () => Html("""<html><body><img src="/pixel.png"></body></html>"""));
        app.MapGet("/pixel.png", () => Results.Bytes([137, 80, 78, 71], "image/png"));
        app.MapGet("/ua", (HttpContext http) => Html($"<html><body>{http.Request.Headers.UserAgent}</body></html>"));
        app.MapGet("/slow", async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            return Html("<html><body><h1>Late</h1></body></html>");
        });
        await site.StartListeningAsync();
        return site;
    }

    public static async Task<Site> StartProxyAsync(int status, string body)
    {
        var (app, site) = Create();
        app.Run(async http =>
        {
            site.ProxiedRequests.Enqueue($"{http.Request.Host}{http.Request.Path}");
            http.Response.StatusCode = status;
            http.Response.ContentType = "text/html";
            await http.Response.WriteAsync(body);
        });
        await site.StartListeningAsync();
        return site;
    }

    private static (WebApplication App, Site Site) Create()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var site = new Site(app);
        app.Use(async (http, next) =>
        {
            site._hits.AddOrUpdate(http.Request.Path, 1, (_, n) => n + 1);
            await next(http);
        });
        return (app, site);
    }

    private async Task StartListeningAsync()
    {
        await _app.StartAsync();
        BaseUrl = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    private static IResult Html(string html, int status = 200) => Results.Content(html, "text/html; charset=utf-8", statusCode: status);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
