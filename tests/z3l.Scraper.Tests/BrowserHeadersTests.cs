using z3l.Scraper.Loading;

namespace z3l.Scraper.Tests;

/// <summary>BrowserHeaders: the headers of the browser of a user agent, as StealthHttpPageLoader sends them.</summary>
[TestClass]
public sealed class BrowserHeadersTests
{
    internal const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";
    private const string Edge = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Edg/124.0.0.0";
    private const string AndroidChrome = "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36";
    private const string Firefox = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0";
    private const string MacSafari = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Safari/605.1.15";
    private const string IPhoneChrome =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/131.0.6778.73 Mobile/15E148 Safari/604.1";
    private const string Opera = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 OPR/115.0.0.0";

    [TestMethod]
    [DataRow(120, "\"Not_A Brand\";v=\"8\", \"Chromium\";v=\"120\", \"Google Chrome\";v=\"120\"")]
    [DataRow(124, "\"Chromium\";v=\"124\", \"Google Chrome\";v=\"124\", \"Not-A.Brand\";v=\"99\"")]
    [DataRow(125, "\"Google Chrome\";v=\"125\", \"Chromium\";v=\"125\", \"Not.A/Brand\";v=\"24\"")]
    [DataRow(131, "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"")]
    public void Brands_AreWhatChromeSends(int major, string expected) => Assert.AreEqual(expected, BrowserHeaders.Brands(major, "Google Chrome"));

    [TestMethod]
    public void UserAgents_AreRead()
    {
        Assert.AreEqual(new BrowserHeaders.Agent(BrowserHeaders.Engine.Chromium, "Google Chrome", 142, "Windows", false), BrowserHeaders.Parse(Chrome));
        Assert.AreEqual(new BrowserHeaders.Agent(BrowserHeaders.Engine.Chromium, "Microsoft Edge", 124, "Windows", false), BrowserHeaders.Parse(Edge));
        Assert.AreEqual(new BrowserHeaders.Agent(BrowserHeaders.Engine.Chromium, "Google Chrome", 131, "Android", true), BrowserHeaders.Parse(AndroidChrome));
        Assert.AreEqual(new BrowserHeaders.Agent(BrowserHeaders.Engine.Firefox, null, 133, "Windows", false), BrowserHeaders.Parse(Firefox));
        Assert.AreEqual(new BrowserHeaders.Agent(BrowserHeaders.Engine.Safari, null, 18, "macOS", false), BrowserHeaders.Parse(MacSafari));
        Assert.AreEqual(new BrowserHeaders.Agent(BrowserHeaders.Engine.Chromium, null, 130, "Windows", false), BrowserHeaders.Parse(Opera));
        Assert.AreEqual(BrowserHeaders.Engine.Safari, BrowserHeaders.Parse(IPhoneChrome).Engine, "every browser on iOS is WebKit");
        Assert.AreEqual(BrowserHeaders.Engine.Other, BrowserHeaders.Parse("curl/8.5.0").Engine);
        Assert.AreEqual(BrowserHeaders.Engine.Other, BrowserHeaders.Parse(null).Engine);
    }

    [TestMethod]
    public void FetchSite_AndReferer_AreTheBrowsers()
    {
        var page = new Uri("https://www.shop.com/catalog?page=2#top");

        Assert.AreEqual("none", BrowserHeaders.FetchSite(null, new Uri("https://www.shop.com/"), navigation: true));
        Assert.AreEqual("same-origin", BrowserHeaders.FetchSite(null, new Uri("https://www.shop.com/api"), navigation: false));
        Assert.AreEqual("same-origin", BrowserHeaders.FetchSite(page, new Uri("https://www.shop.com/p/1"), navigation: true));
        Assert.AreEqual("same-site", BrowserHeaders.FetchSite(page, new Uri("https://img.shop.com/1.jpg"), navigation: true));
        Assert.AreEqual("cross-site", BrowserHeaders.FetchSite(page, new Uri("http://www.shop.com/p/1"), navigation: true), "another scheme");
        Assert.AreEqual("cross-site", BrowserHeaders.FetchSite(page, new Uri("https://other.com/"), navigation: true));
        Assert.AreEqual("same-site", BrowserHeaders.FetchSite(new Uri("https://m.shop.co.uk/"), new Uri("https://shop.co.uk/"), navigation: true));
        Assert.AreEqual("cross-site", BrowserHeaders.FetchSite(new Uri("https://a.co.uk/"), new Uri("https://b.co.uk/"), navigation: true));

        Assert.AreEqual("https://www.shop.com/catalog?page=2", BrowserHeaders.Referer(page, new Uri("https://www.shop.com/p/1")));
        Assert.AreEqual("https://www.shop.com/", BrowserHeaders.Referer(page, new Uri("https://other.com/")));
        Assert.IsNull(BrowserHeaders.Referer(page, new Uri("http://www.shop.com/p/1")), "https to http sends none");
        Assert.IsNull(BrowserHeaders.Referer(null, new Uri("https://www.shop.com/")));
    }

    [TestMethod]
    public void Chrome_AsksForAPage_AsANavigation()
    {
        var request = new ScrapeRequest("https://www.shop.com/p/1", PageType.Item) { ParentUrl = "https://www.shop.com/catalog" };

        var headers = BrowserHeaders.For(request, new Uri(request.Url), Chrome, "en-US,en;q=0.9", sendReferer: true);

        CollectionAssert.AreEqual(new[]
        {
            "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform", "Upgrade-Insecure-Requests", "User-Agent", "Accept",
            "Sec-Fetch-Site", "Sec-Fetch-Mode", "Sec-Fetch-User", "Sec-Fetch-Dest", "Referer", "Accept-Encoding", "Accept-Language", "Priority",
        }, headers.Select(h => h.Key).ToArray());
        var values = headers.ToDictionary(h => h.Key, h => h.Value);
        Assert.AreEqual(BrowserHeaders.Brands(142, "Google Chrome"), values["sec-ch-ua"]);
        Assert.AreEqual("?0", values["sec-ch-ua-mobile"]);
        Assert.AreEqual("\"Windows\"", values["sec-ch-ua-platform"]);
        Assert.AreEqual(BrowserHeaders.ChromeAccept, values["Accept"]);
        Assert.AreEqual("same-origin", values["Sec-Fetch-Site"]);
        Assert.AreEqual("https://www.shop.com/catalog", values["Referer"]);
        Assert.AreEqual("u=0, i", values["Priority"]);
    }

    [TestMethod]
    public void Api_IsAFetch_AndAPostSendsOrigin()
    {
        var request = new ScrapeRequest("https://www.shop.com/api/price", PageType.Api) { Method = HttpMethod.Post, Body = "{}", ParentUrl = "https://www.shop.com/p/1" };

        var values = BrowserHeaders.For(request, new Uri(request.Url), Chrome, "en", sendReferer: true).ToDictionary(h => h.Key, h => h.Value);

        Assert.AreEqual("*/*", values["Accept"]);
        Assert.AreEqual("cors", values["Sec-Fetch-Mode"]);
        Assert.AreEqual("empty", values["Sec-Fetch-Dest"]);
        Assert.AreEqual("https://www.shop.com", values["Origin"]);
        Assert.AreEqual("u=1, i", values["Priority"]);
        Assert.IsFalse(values.ContainsKey("Sec-Fetch-User"));
        Assert.IsFalse(values.ContainsKey("Upgrade-Insecure-Requests"));
    }

    [TestMethod]
    public void Firefox_Safari_AndOtherChromiums_SendNoClientHints()
    {
        foreach (var userAgent in new[] { Firefox, MacSafari, IPhoneChrome, Opera })
        {
            var names = BrowserHeaders.For(new ScrapeRequest("https://s.test/"), new Uri("https://s.test/"), userAgent, "en", sendReferer: true)
                .Select(h => h.Key).ToArray();
            Assert.IsFalse(names.Any(n => n.StartsWith("sec-ch-ua", StringComparison.Ordinal)), userAgent);
            CollectionAssert.Contains(names, "Sec-Fetch-Mode", userAgent);
            CollectionAssert.Contains(names, "User-Agent", userAgent);
        }
    }

    [TestMethod]
    public void NoReferer_WhenItIsOff()
    {
        var request = new ScrapeRequest("https://www.shop.com/p/1") { ParentUrl = "https://www.shop.com/catalog" };
        var names = BrowserHeaders.For(request, new Uri(request.Url), Chrome, "en", sendReferer: false).Select(h => h.Key).ToArray();
        CollectionAssert.DoesNotContain(names, "Referer");
    }
}
