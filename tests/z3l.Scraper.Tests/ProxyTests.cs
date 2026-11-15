using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Tests;

[TestClass]
public sealed class ProxyTests
{
    private const string Site = "shop.test";

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ProxyList List(ProxyBlockPolicy policy, ManualClock clock, params string[] lines) => new(lines, policy) { Clock = clock };

    private static async Task<string> Next(ProxyList list, string site = Site) => (await list.AcquireAsync(site, CancellationToken.None)).Address.Host;

    // ------------------------------------------------------------------ lines

    [TestMethod]
    [DataRow("1.2.3.4:8080", "http", "1.2.3.4", 8080, null, null)]
    [DataRow("1.2.3.4:8080:user:pa:ss", "http", "1.2.3.4", 8080, "user", "pa:ss")]
    [DataRow("user:pass@proxy.test:3128", "http", "proxy.test", 3128, "user", "pass")]
    [DataRow("socks5://u:p%40ss@10.0.0.1:1080", "socks5", "10.0.0.1", 1080, "u", "p@ss")]
    [DataRow("SOCKS4A://h.test:1080", "socks4a", "h.test", 1080, null, null)]
    [DataRow("  https://secure.test:443   # office", "https", "secure.test", 443, null, null)]
    [DataRow("http://p.test:80", "http", "p.test", 80, null, null)]
    public void Parses(string line, string scheme, string host, int port, string? user, string? password)
    {
        Assert.IsTrue(ProxyAddress.TryParse(line, out var proxy));
        Assert.AreEqual(new ProxyAddress(scheme, host, port, user, password), proxy);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("# a comment")]
    [DataRow("1.2.3.4")]
    [DataRow("ftp://1.2.3.4:21")]
    [DataRow("socks5://1.2.3.4")]
    [DataRow("host:notaport")]
    [DataRow("1.2.3.4:99999")]
    public void Rejects(string line)
    {
        Assert.IsFalse(ProxyAddress.TryParse(line, out _));
    }

    [TestMethod]
    public void ToString_HidesThePassword()
    {
        Assert.IsTrue(ProxyAddress.TryParse("socks5://user:secret@10.0.0.1:1080", out var proxy));

        Assert.AreEqual("socks5://user@10.0.0.1:1080", proxy.ToString());
        Assert.DoesNotContain("secret", proxy.ToString());
    }

    // ------------------------------------------------------------------ rotation and rests

    [TestMethod]
    public async Task ProxiesGoInTurn()
    {
        var list = List(ProxyBlockPolicy.Block1Hour, new ManualClock(), "a.test:1", "b.test:1", "c.test:1");

        var order = new List<string>();
        for (var i = 0; i < 6; i++) order.Add(await Next(list));

        CollectionAssert.AreEqual(new[] { "a.test", "b.test", "c.test", "a.test", "b.test", "c.test" }, order);
    }

    [TestMethod]
    [DataRow(ProxyBlockPolicy.Block10Minutes, 9, 11)]
    [DataRow(ProxyBlockPolicy.Block1Hour, 59, 61)]
    [DataRow(ProxyBlockPolicy.Block1Day, 23 * 60, 25 * 60)]
    public async Task BlockedProxy_RestsForThatSiteOnly_ForThePolicyTime(ProxyBlockPolicy policy, int stillBlockedMinutes, int freeMinutes)
    {
        var clock = new ManualClock();
        var start = clock.Now;
        var list = List(policy, clock, "a.test:1", "b.test:1");
        var a = await list.AcquireAsync(Site, CancellationToken.None);
        list.ReportBlocked(a, Site);

        Assert.AreEqual("b.test", await Next(list));
        Assert.AreEqual("b.test", await Next(list));                    // a rests for shop.test
        Assert.AreEqual("a.test", await Next(list, "other.test"));      // and works for other sites

        clock.Now = start.AddMinutes(stillBlockedMinutes);
        Assert.AreEqual("b.test", await Next(list));
        Assert.AreEqual("b.test", await Next(list));

        clock.Now = start.AddMinutes(freeMinutes);
        CollectionAssert.Contains(new[] { await Next(list), await Next(list) }, "a.test");
    }

    [TestMethod]
    public async Task RemovePolicy_NeverComesBack_IgnorePolicy_NeverRests()
    {
        var clock = new ManualClock();
        var removed = List(ProxyBlockPolicy.Remove, clock, "a.test:1", "b.test:1");
        var a = await removed.AcquireAsync(Site, CancellationToken.None);
        removed.ReportBlocked(a, Site);
        clock.Now = clock.Now.AddDays(30);
        Assert.AreEqual("b.test", await Next(removed));
        Assert.AreEqual("b.test", await Next(removed));

        var ignored = List(ProxyBlockPolicy.Ignore, clock, "a.test:1", "b.test:1");
        var first = await ignored.AcquireAsync(Site, CancellationToken.None);
        ignored.ReportBlocked(first, Site);
        Assert.AreEqual("b.test", await Next(ignored));
        Assert.AreEqual("a.test", await Next(ignored));
        Assert.AreEqual(1, ignored.Status().Single(s => s.Address.Host == "a.test").Blocks);
    }

    [TestMethod]
    public async Task FailuresInARow_RestTheProxyForEverySite_ASuccessResetsTheCount()
    {
        var clock = new ManualClock();
        var list = new ProxyList(["a.test:1", "b.test:1"]) { Clock = clock, MaxConsecutiveFailures = 3, DeadProxyPause = TimeSpan.FromMinutes(10) };
        var a = (await list.AcquireAsync(Site, CancellationToken.None));

        list.ReportFailure(a, "refused");
        list.ReportFailure(a, "refused");
        list.ReportSuccess(a);                 // the count starts again
        list.ReportFailure(a, "refused");
        list.ReportFailure(a, "refused");
        Assert.IsFalse(list.Status().Single(s => s.Address.Host == "a.test").Dead);

        list.ReportFailure(a, "refused");      // third in a row
        Assert.IsTrue(list.Status().Single(s => s.Address.Host == "a.test").Dead);
        Assert.AreEqual("b.test", await Next(list));
        Assert.AreEqual("b.test", await Next(list, "other.test"));

        clock.Now = clock.Now.AddMinutes(11);
        CollectionAssert.Contains(new[] { await Next(list), await Next(list) }, "a.test");
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task EveryProxyResting_TheLoadWaitsForTheFirstToComeBack()
    {
        var list = new ProxyList(["a.test:1"]) { MaxConsecutiveFailures = 1, DeadProxyPause = TimeSpan.FromMilliseconds(300) };
        var a = await list.AcquireAsync(Site, CancellationToken.None);
        list.ReportFailure(a, "refused");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.AreEqual("a.test", await Next(list));
        Assert.IsGreaterThanOrEqualTo(250, clock.ElapsedMilliseconds);
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task EveryProxyBlocked_TheWaitEndsWithTheRequestTimeout()
    {
        var list = new ProxyList(["a.test:1"]);
        list.ReportBlocked(await list.AcquireAsync(Site, CancellationToken.None), Site);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<OperationCanceledException>(() => list.AcquireAsync(Site, timeout.Token));
    }

    [TestMethod]
    public async Task EveryProxyRemovedForASite_FailsAtOnce()
    {
        var list = new ProxyList(["a.test:1"], ProxyBlockPolicy.Remove);
        list.ReportBlocked(await list.AcquireAsync(Site, CancellationToken.None), Site);

        await Assert.ThrowsExactlyAsync<NonRetryableException>(() => list.AcquireAsync(Site, CancellationToken.None));
        Assert.AreEqual("a.test", await Next(list, "other.test"));
    }

    // ------------------------------------------------------------------ file

    [TestMethod]
    public async Task File_IsReadAgainWhenItChanges_ProxiesKeepTheirRests()
    {
        var path = Path.Combine(Path.GetTempPath(), $"z3l-proxies-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllLinesAsync(path, ["# proxies", "a.test:1", "b.test:1", "not a proxy"]);
            var clock = new ManualClock();
            var list = ProxyList.FromFile(path);
            list.Clock = clock;
            Assert.AreEqual(2, list.Count);
            var a = await list.AcquireAsync(Site, CancellationToken.None);
            list.ReportBlocked(a, Site);

            await File.WriteAllLinesAsync(path, ["a.test:1", "c.test:1"]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            clock.Now = clock.Now.AddSeconds(10);       // past FileCheckInterval

            Assert.AreEqual("c.test", await Next(list));     // b is gone, a still rests for shop.test
            Assert.AreEqual("c.test", await Next(list));
            Assert.AreEqual(2, list.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void File_MissingOrWithoutProxies_Throws()
    {
        Assert.ThrowsExactly<FileNotFoundException>(() => ProxyList.FromFile(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.txt")));

        var path = Path.Combine(Path.GetTempPath(), $"z3l-proxies-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, "# nothing here\n");
            Assert.ThrowsExactly<ArgumentException>(() => ProxyList.FromFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task Status_AndSummary()
    {
        var list = new ProxyList(["a.test:1", "b.test:1"]);
        var a = await list.AcquireAsync(Site, CancellationToken.None);
        list.ReportSuccess(a);
        list.ReportBlocked(a, Site);

        var status = list.Status().Single(s => s.Address.Host == "a.test");
        Assert.AreEqual(1, status.Loads);
        Assert.AreEqual(1, status.Successes);
        Assert.AreEqual(1, status.BlockedSites);
        StringAssert.Contains(list.ToString(), "2 proxies");
    }

    // ------------------------------------------------------------------ through HttpPageLoader

    /// <summary>An HTTP proxy on Kestrel: answers every request itself (the absolute-form target says which site) and records it.</summary>
    private sealed class FakeProxy : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private FakeProxy(WebApplication app) => _app = app;

        public string Address { get; private set; } = "";
        public ConcurrentQueue<string> Requests { get; } = new();

        public static async Task<FakeProxy> StartAsync(int status, string body)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var proxy = new FakeProxy(app);
            app.Run(async http =>
            {
                proxy.Requests.Enqueue($"{http.Request.Host}{http.Request.Path}");
                http.Response.StatusCode = status;
                http.Response.ContentType = "text/html";
                await http.Response.WriteAsync(body);
            });
            await app.StartAsync();
            proxy.Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return proxy;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Loader_RotatesProxies_AndRestsTheOneASiteBlocked()
    {
        await using var blocking = await FakeProxy.StartAsync(403, "<html>Access denied</html>");
        await using var good = await FakeProxy.StartAsync(200, "<html><body>ok</body></html>");
        var proxies = new ProxyList([blocking.Address, good.Address]);
        using var loader = new HttpPageLoader(new HttpPageLoaderOptions { Proxies = proxies });
        var options = ScraperTests.FastOptions();
        options.MaxConcurrency = 1;
        var scraper = new Scraper(loader, options).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync(Enumerable.Range(1, 6).Select(i => (ScrapeRequest)$"http://{Site}/p/{i}"));

        Assert.HasCount(6, result.Items);
        Assert.HasCount(1, blocking.Requests);          // its 403 rested it for shop.test: never asked again
        Assert.HasCount(6, good.Requests);
        Assert.AreEqual($"{Site}/p/1", blocking.Requests.Single());
        Assert.AreEqual(1, StatusOf(proxies, blocking).Blocks);
        Assert.AreEqual(1, StatusOf(proxies, blocking).BlockedSites);
        Assert.AreEqual(6, StatusOf(proxies, good).Successes);
    }

    private static ProxyStatus StatusOf(ProxyList proxies, FakeProxy proxy) =>
        proxies.Status().Single(s => s.Address.Port == new Uri(proxy.Address).Port);

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Loader_ProxyThatAsksForALogin_RestsAfterFailuresInARow()
    {
        await using var noLogin = await FakeProxy.StartAsync(407, "Proxy Authentication Required");
        await using var good = await FakeProxy.StartAsync(200, "<html><body>ok</body></html>");
        var proxies = new ProxyList([noLogin.Address, good.Address]);
        using var loader = new HttpPageLoader(new HttpPageLoaderOptions { Proxies = proxies });
        var options = ScraperTests.FastOptions();
        options.MaxConcurrency = 1;
        options.MaxRetries = 5;
        var scraper = new Scraper(loader, options).Route("default", _ => { });

        var result = await scraper.RunToListAsync(Enumerable.Range(1, 10).Select(i => (ScrapeRequest)$"http://{Site}/p/{i}"));

        Assert.AreEqual(10, result.Stats.Succeeded);
        Assert.HasCount(3, noLogin.Requests);           // three failures in a row, then it rests
        Assert.IsTrue(StatusOf(proxies, noLogin).Dead);
        Assert.AreEqual(3, StatusOf(proxies, noLogin).Failures);
    }

    [TestMethod]
    public void Loader_ProxyAndProxies_AreExclusive()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new HttpPageLoader(new HttpPageLoaderOptions
        {
            Proxy = new System.Net.WebProxy("http://127.0.0.1:1"),
            Proxies = new ProxyList(["127.0.0.1:2"]),
        }));
        Assert.ThrowsExactly<ArgumentException>(() => new HttpPageLoader(new HttpClient(), new HttpPageLoaderOptions { Proxies = new ProxyList(["127.0.0.1:2"]) }));
    }
}
