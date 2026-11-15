namespace z3l.Scraper.Tests;

[TestClass]
public sealed class UrlTests
{
    private const string Base = "https://s.test/cat/list?page=1";

    [TestMethod]
    [DataRow("/p/1", "https://s.test/p/1")]
    [DataRow("p/2", "https://s.test/cat/p/2")]
    [DataRow("../p/3", "https://s.test/p/3")]
    [DataRow("//cdn.test/x.jpg", "https://cdn.test/x.jpg")]           // protocol-relative: an UNC path for Uri on Windows
    [DataRow("?page=2", "https://s.test/cat/list?page=2")]
    [DataRow("  /p/4 \n", "https://s.test/p/4")]
    [DataRow("HTTPS://S.TEST/P/5", "https://s.test/P/5")]
    [DataRow("https://s.test:443/p/6", "https://s.test/p/6")]
    [DataRow("http://other.test/a b", "http://other.test/a%20b")]
    [DataRow("/каталог/чай", "https://s.test/%D0%BA%D0%B0%D1%82%D0%B0%D0%BB%D0%BE%D0%B3/%D1%87%D0%B0%D0%B9")]
    public void Resolves(string url, string expected)
    {
        Assert.IsTrue(UrlHelper.TryResolve(Base, url, out var absolute));
        Assert.AreEqual(expected, absolute);
    }

    [TestMethod]
    [DataRow("mailto:shop@s.test")]
    [DataRow("javascript:void(0)")]
    [DataRow("tel:+123")]
    [DataRow("data:image/png;base64,AA")]
    [DataRow("ftp://s.test/file")]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(null)]
    public void Rejects(string? url)
    {
        Assert.IsFalse(UrlHelper.TryResolve(Base, url, out _));
    }

    [TestMethod]
    public void StartUrls_MustBeAbsolute()
    {
        Assert.IsTrue(UrlHelper.TryNormalize("https://s.test", out var normalized));
        Assert.AreEqual("https://s.test/", normalized);
        Assert.IsFalse(UrlHelper.TryNormalize("/relative", out _));
        Assert.IsFalse(UrlHelper.TryNormalize("s.test/page", out _));
    }

    [TestMethod]
    public void UniqueKey_IgnoresFragmentAndHostCase_ButNotQueryOrBody()
    {
        var a = new ScrapeRequest("https://S.test/p?x=1#top").GetUniqueKey();
        var b = new ScrapeRequest("https://s.test/p?x=1").GetUniqueKey();
        var c = new ScrapeRequest("https://s.test/p?x=2").GetUniqueKey();
        var d = new ScrapeRequest("https://s.test/p?x=1") { Method = HttpMethod.Post, Body = "a=1" }.GetUniqueKey();

        Assert.AreEqual(a, b);
        Assert.AreNotEqual(b, c);
        Assert.AreNotEqual(b, d);
        Assert.AreEqual("own", new ScrapeRequest("https://s.test/p") { UniqueKey = "own" }.GetUniqueKey());
    }

    [TestMethod]
    public async Task Enqueue_ResolvesAgainstTheFinalUrl_AndSkipsNonHttpLinks()
    {
        // the loader answers for /start as if it was redirected to /cat/list
        var loader = new DelegateLoader((r, _) => Task.FromResult(PageResponse.FromText(200, r.Url.EndsWith("/start") ? Base : r.Url, ScraperTests.Html("x"))));
        var added = new List<bool>();
        var scraper = new Scraper<string>(loader, ScraperTests.FastOptions())
            .Route("default", ctx =>
            {
                if (!ctx.Request.Url.EndsWith("/start")) return;
                added.Add(ctx.Enqueue("mailto:a@b.c"));
                added.Add(ctx.Enqueue("javascript:void(0)"));
                added.Add(ctx.Enqueue(""));
                added.Add(ctx.Enqueue("#top"));         // resolves to the final URL /cat/list: a page not seen yet
                added.Add(ctx.Enqueue("p/2", "leaf"));   // relative to /cat/list, not to /start
            })
            .Route("leaf", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync("https://s.test/start");

        CollectionAssert.AreEqual(new[] { "https://s.test/cat/p/2" }, result.Items.ToArray());
        CollectionAssert.AreEqual(new[] { false, false, false, true, true }, added);
        Assert.AreEqual(3, result.Stats.Succeeded);
    }
}
