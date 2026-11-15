using System.IO.Compression;
using System.Text;
using z3l.Scraper.Crawling;

namespace z3l.Scraper.Tests;

[TestClass]
public sealed class SitemapTests
{
    [ClassInitialize]
    public static void RegisterCodePages(TestContext _) => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [TestMethod]
    public void UrlSet_WithLastmod_Entities_Cdata_AndImageExtension()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:image="http://www.google.com/schemas/sitemap-image/1.1">
              <url>
                <image:image><image:loc>https://s.test/img/1.jpg</image:loc></image:image>
                <loc>https://s.test/p/1?a=1&amp;b=2</loc>
                <lastmod>2026-11-01</lastmod>
              </url>
              <url><loc> https://s.test/p/2 </loc></url>
              <url><loc><![CDATA[https://s.test/p/3]]></loc><lastmod>2026-11-02T10:30:00+02:00</lastmod></url>
            </urlset>
            """;

        var sitemap = Sitemap.Parse(xml, "https://s.test/sitemap.xml");

        CollectionAssert.AreEqual(new[] { "https://s.test/p/1?a=1&b=2", "https://s.test/p/2", "https://s.test/p/3" }, sitemap.Urls.Select(u => u.Url).ToArray());
        Assert.AreEqual(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), sitemap.Urls[0].LastModified);
        Assert.IsNull(sitemap.Urls[1].LastModified);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 2, 10, 30, 0, TimeSpan.FromHours(2)), sitemap.Urls[2].LastModified);
        Assert.IsEmpty(sitemap.Sitemaps);
    }

    [TestMethod]
    public void SitemapIndex_ListsNestedSitemaps()
    {
        const string xml = """
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://s.test/s1.xml.gz</loc><lastmod>2026-01-01</lastmod></sitemap>
              <sitemap><loc>https://s.test/s2.xml</loc></sitemap>
            </sitemapindex>
            """;

        var sitemap = Sitemap.Parse(xml);

        CollectionAssert.AreEqual(new[] { "https://s.test/s1.xml.gz", "https://s.test/s2.xml" }, sitemap.Sitemaps.ToArray());
        Assert.IsEmpty(sitemap.Urls);
    }

    [TestMethod]
    public void Gzipped_IsUnpacked()
    {
        var xml = "<urlset><url><loc>https://s.test/p/1</loc></url></urlset>";
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.Fastest)) gzip.Write(Encoding.UTF8.GetBytes(xml));

        var sitemap = Sitemap.Parse(new PageResponse(200, "https://s.test/s.xml.gz", packed.ToArray(), "application/x-gzip"));

        Assert.AreEqual("https://s.test/p/1", sitemap.Urls.Single().Url);
    }

    [TestMethod]
    public void TextSitemap_OneUrlPerLine()
    {
        var sitemap = Sitemap.Parse("https://s.test/a\r\nhttps://s.test/b\n\nnot a url\nmailto:x@y.z\n");

        CollectionAssert.AreEqual(new[] { "https://s.test/a", "https://s.test/b" }, sitemap.Urls.Select(u => u.Url).ToArray());
    }

    [TestMethod]
    public void RelativeLocations_ResolveAgainstTheSitemapUrl()
    {
        var sitemap = Sitemap.Parse("<urlset><url><loc>/p/9</loc></url></urlset>", "https://s.test/maps/sitemap.xml");

        Assert.AreEqual("https://s.test/p/9", sitemap.Urls.Single().Url);
    }

    [TestMethod]
    public void XmlDeclarationEncoding_IsUsed()
    {
        var xml = "<?xml version=\"1.0\" encoding=\"windows-1251\"?><urlset><url><loc>https://s.test/чай</loc></url></urlset>";
        var body = Encoding.GetEncoding("windows-1251").GetBytes(xml);

        var sitemap = Sitemap.Parse(new PageResponse(200, "https://s.test/sitemap.xml", body, "text/xml"));

        Assert.AreEqual("https://s.test/%D1%87%D0%B0%D0%B9", sitemap.Urls.Single().Url);
    }

    [TestMethod]
    public void EmptyOrBroken_GivesNothing()
    {
        Assert.IsEmpty(Sitemap.Parse("").Urls);
        Assert.IsEmpty(Sitemap.Parse("<urlset><url><loc></loc></url></urlset>").Urls);
        Assert.IsEmpty(Sitemap.Parse("<html><body>Not found</body></html>").Urls);
    }

    [TestMethod]
    public async Task EnqueueSitemap_WalksTheIndex_AndFilters()
    {
        var pages = new Dictionary<string, string>
        {
            ["https://s.test/sitemap.xml"] = "<sitemapindex><sitemap><loc>https://s.test/s1.xml</loc></sitemap><sitemap><loc>https://s.test/s2.xml</loc></sitemap></sitemapindex>",
            ["https://s.test/s1.xml"] = "<urlset><url><loc>https://s.test/p/1</loc><lastmod>2026-01-01</lastmod></url><url><loc>https://s.test/p/2</loc><lastmod>2020-01-01</lastmod></url></urlset>",
            ["https://s.test/s2.xml"] = "<urlset><url><loc>https://s.test/p/3</loc><lastmod>2026-02-01</lastmod></url><url><loc>https://s.test/p/1</loc></url></urlset>",
        };
        var loader = new DelegateLoader((r, _) => Task.FromResult(PageResponse.FromText(200, r.Url, pages.GetValueOrDefault(r.Url) ?? "<html>product</html>", "application/xml")));
        var since = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var scraper = new Scraper<string>(loader, ScraperTests.FastOptions())
            .Route("sitemap", ctx => ctx.EnqueueSitemap("product", filter: page => page.LastModified is null || page.LastModified > since))
            .Route("product", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/sitemap.xml", "sitemap"));

        CollectionAssert.AreEquivalent(new[] { "https://s.test/p/1", "https://s.test/p/3" }, result.Items.ToArray());
        Assert.AreEqual(1, result.Stats.Duplicates);    // p/1 is in both sitemaps
    }
}
