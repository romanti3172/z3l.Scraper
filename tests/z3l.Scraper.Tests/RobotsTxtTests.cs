using System.Diagnostics;
using z3l.Scraper.Crawling;

namespace z3l.Scraper.Tests;

[TestClass]
public sealed class RobotsTxtTests
{
    private const string Site = "https://s.test";

    [TestMethod]
    public void LongestMatchWins_AllowWinsATie()
    {
        var robots = RobotsTxt.Parse("""
            User-agent: *
            Disallow: /shop/
            Allow: /shop/public/
            Disallow: /tie
            Allow: /tie
            """);

        Assert.IsFalse(robots.IsAllowed(Site + "/shop/cart"));
        Assert.IsTrue(robots.IsAllowed(Site + "/shop/public/item"));
        Assert.IsTrue(robots.IsAllowed(Site + "/tie"));
        Assert.IsTrue(robots.IsAllowed(Site + "/other"));
    }

    [TestMethod]
    public void NamedGroupBeatsStar_CaseInsensitive()
    {
        var robots = RobotsTxt.Parse("""
            User-agent: *
            Disallow: /

            User-agent: My-Bot
            Disallow: /private
            """);

        Assert.IsTrue(robots.IsAllowed(Site + "/page", "my-bot"));
        Assert.IsFalse(robots.IsAllowed(Site + "/private/x", "MY-BOT"));
        Assert.IsFalse(robots.IsAllowed(Site + "/page", "other-bot"));
        Assert.IsFalse(robots.IsAllowed(Site + "/page"));
    }

    [TestMethod]
    public void ConsecutiveUserAgents_ShareAGroup_RulesBeforeAnyAgentAreIgnored()
    {
        var robots = RobotsTxt.Parse("""
            Disallow: /ignored
            User-agent: a
            User-agent: b
            Disallow: /x
            User-agent: c
            Disallow: /y
            """);

        Assert.IsFalse(robots.IsAllowed(Site + "/x", "a"));
        Assert.IsFalse(robots.IsAllowed(Site + "/x", "b"));
        Assert.IsTrue(robots.IsAllowed(Site + "/x", "c"));
        Assert.IsFalse(robots.IsAllowed(Site + "/y", "c"));
        Assert.IsTrue(robots.IsAllowed(Site + "/ignored", "a"));
    }

    [TestMethod]
    [DataRow("/*.pdf$", "/docs/a.pdf", true)]
    [DataRow("/*.pdf$", "/docs/a.pdf?x=1", false)]
    [DataRow("/*.pdf", "/docs/a.pdf?x=1", true)]
    [DataRow("/search*q=", "/search/?page=2&q=tea", true)]
    [DataRow("/p$", "/p", true)]
    [DataRow("/p$", "/p/1", false)]
    [DataRow("/a*b*c", "/aXXbYYc", true)]
    [DataRow("/a*b*c", "/aXXcYYb", false)]
    [DataRow("/a*$", "/abc", true)]
    [DataRow("*", "/anything", true)]
    [DataRow("/fish", "/fish.html", true)]
    [DataRow("/fish", "/Fish", false)]
    [DataRow("/fish/", "/fish", false)]
    public void Wildcards(string pattern, string path, bool matches)
    {
        Assert.AreEqual(matches, RobotsTxt.Matches(pattern, path));
    }

    [TestMethod]
    public void QueryString_IsPartOfThePath()
    {
        var robots = RobotsTxt.Parse("User-agent: *\nDisallow: /*?sort=");

        Assert.IsFalse(robots.IsAllowed(Site + "/list?sort=price"));
        Assert.IsTrue(robots.IsAllowed(Site + "/list?page=2"));
    }

    [TestMethod]
    public void NonAsciiPathsAndEscapes_CompareEncoded()
    {
        var robots = RobotsTxt.Parse("User-agent: *\nDisallow: /каталог/\nDisallow: /a%2fb");

        Assert.IsFalse(robots.IsAllowed(Site + "/каталог/чай"));
        Assert.IsFalse(robots.IsAllowed(Site + "/a%2Fb"));
        Assert.IsTrue(robots.IsAllowed(Site + "/catalog/"));
    }

    [TestMethod]
    public void CrawlDelay_Sitemaps_EmptyDisallow_Comments()
    {
        var robots = RobotsTxt.Parse("""
            Sitemap: https://s.test/sitemap.xml
            User-agent: *
            Crawl-delay: 2.5
            Disallow:
            Sitemap: https://s.test/news.xml   # second one
            """);

        Assert.AreEqual(TimeSpan.FromSeconds(2.5), robots.CrawlDelay());
        CollectionAssert.AreEqual(new[] { "https://s.test/sitemap.xml", "https://s.test/news.xml" }, robots.Sitemaps.ToArray());
        Assert.IsTrue(robots.IsAllowed(Site + "/anything"));
    }

    [TestMethod]
    public void TextAsABrowserShowsIt_IsUnwrapped()
    {
        const string html = "<html><head></head><body><pre style=\"word-wrap: break-word;\">User-agent: *\nDisallow: /a&amp;b</pre></body></html>";

        Assert.IsFalse(RobotsTxt.Parse(html).IsAllowed(Site + "/a&b"));
    }

    [TestMethod]
    public void RobotsTxtItself_IsAlwaysAllowed()
    {
        Assert.IsTrue(RobotsTxt.Parse("User-agent: *\nDisallow: /").IsAllowed(Site + "/robots.txt"));
    }

    [TestMethod]
    public void EmptyOrGarbage_AllowsAll()
    {
        Assert.IsTrue(RobotsTxt.Parse(null).IsAllowed(Site + "/x"));
        Assert.IsTrue(RobotsTxt.Parse("<html>not a robots file</html>").IsAllowed(Site + "/x"));
        Assert.IsTrue(RobotsTxt.Parse("garbage\nmore: garbage").IsAllowed(Site + "/x"));
        Assert.IsNull(RobotsTxt.Parse("garbage").CrawlDelay());
    }

    [TestMethod]
    public void PathologicalPattern_IsMatchedFast()
    {
        // a site can write any pattern: a regular expression of it could backtrack for minutes
        var robots = RobotsTxt.Parse("User-agent: *\nDisallow: /" + string.Concat(Enumerable.Repeat("*a", 50)) + "b");
        var url = Site + "/" + new string('a', 2000);

        var clock = Stopwatch.StartNew();
        Assert.IsTrue(robots.IsAllowed(url));
        Assert.IsLessThan(1000, clock.ElapsedMilliseconds);
    }
}
