using System.Runtime.InteropServices;
using System.Text;

namespace z3l.Scraper.Tests;

[TestClass]
public sealed class PageResponseTests
{
    [ClassInitialize]
    public static void RegisterCodePages(TestContext _) => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static byte[] Cp1251(string text) => Encoding.GetEncoding("windows-1251").GetBytes(text);

    [TestMethod]
    public void FromText_MakesTheBytesOnlyWhenAsked_AndOnce()
    {
        var response = PageResponse.FromText(200, "https://s.test/", "<html>Привет</html>");

        Assert.IsTrue(response.HasBody);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("<html>Привет</html>"), response.Body);
        Assert.AreSame(response.Body, response.Body);
        Assert.IsFalse(PageResponse.FromText(200, "https://s.test/", "").HasBody);
        Assert.IsEmpty(PageResponse.FromText(204, "https://s.test/", null!).Body);
    }

    [TestMethod]
    public void TextMemory_DecodesIntoThePool_WithoutAString_AndTextWorksAfterTheRelease()
    {
        var response = new PageResponse(200, "https://s.test/", Cp1251("<html>Привет</html>"), "text/html; charset=windows-1251");

        Assert.AreEqual("<html>Привет</html>", response.TextMemory.ToString());
        Assert.IsTrue(response.HasPooledText);
        Assert.IsFalse(response.HasTextString);
        Assert.IsTrue(response.TextMemory.Equals(response.TextMemory), "decoded once");

        response.ReleaseText();
        Assert.IsFalse(response.HasPooledText);
        Assert.AreEqual("<html>Привет</html>", response.Text);
        Assert.AreEqual("<html>Привет</html>", response.TextMemory.ToString());     // after the release: the string
        Assert.IsFalse(response.HasPooledText);
    }

    [TestMethod]
    public void TextMemory_SkipsByteOrderMarks_LikeText()
    {
        byte[][] bodies =
        [
            [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes("<p>é</p>")],
            [.. Encoding.Unicode.Preamble, .. Encoding.Unicode.GetBytes("<p>é</p>")],
            [.. Encoding.BigEndianUnicode.Preamble, .. Encoding.BigEndianUnicode.GetBytes("<p>é</p>")],
            [.. Encoding.UTF8.Preamble],
        ];
        foreach (var body in bodies)
        {
            var expected = new PageResponse(200, "https://s.test/", body, "text/html").Text;
            Assert.AreEqual(expected, new PageResponse(200, "https://s.test/", body, "text/html").TextMemory.ToString());
        }
    }

    [TestMethod]
    public void TextMemory_OfTextResponsesAndHugePages_IsTheString()
    {
        var fromText = PageResponse.FromText(200, "https://s.test/", "<p>x</p>");
        Assert.IsTrue(MemoryMarshal.TryGetString(fromText.TextMemory, out var text, out _, out _));
        Assert.AreSame(fromText.Text, text);

        var huge = new PageResponse(200, "https://s.test/", Encoding.ASCII.GetBytes(new string('a', 5 * 1024 * 1024)), "text/plain");
        Assert.AreEqual(5 * 1024 * 1024, huge.TextMemory.Length);
        Assert.IsFalse(huge.HasPooledText, "the pool does not keep huge arrays");
        Assert.IsTrue(huge.HasTextString);
    }

    [TestMethod]
    public async Task FromText_EmptyText_IsAnEmptyBodyForTheChecks()
    {
        var scraper = new Scraper(new DelegateLoader((r, _) => Task.FromResult(PageResponse.FromText(200, r.Url, ""))), ScraperTests.FastOptions())
            .Route("default", ctx => ctx.Emit("handled"));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/empty"));

        Assert.IsEmpty(result.Items);
        StringAssert.Contains(result.Failed.Single().Message, "empty body");
    }

    [TestMethod]
    public void Text_UsesCharsetFromContentType()
    {
        var response = new PageResponse(200, "https://s.test/", Cp1251("<html>Привет</html>"), "text/html; charset=windows-1251");

        Assert.AreEqual("<html>Привет</html>", response.Text);
    }

    [TestMethod]
    [DataRow("cp1251")]
    [DataRow("CP-1251")]
    [DataRow("win-1251")]
    [DataRow("\"windows-1251\"")]
    public void Text_UnderstandsCharsetAliasesSitesUse(string charset)
    {
        var response = new PageResponse(200, "https://s.test/", Cp1251("<html>Цена</html>"), $"text/html; charset={charset}");

        StringAssert.Contains(response.Text, "Цена");
    }

    [TestMethod]
    public void Text_UsesMetaCharset_WhenHeaderHasNone()
    {
        var response = new PageResponse(200, "https://s.test/", Cp1251("<html><head><meta charset=\"windows-1251\"></head>Цена</html>"), "text/html");

        StringAssert.Contains(response.Text, "Цена");
    }

    [TestMethod]
    public void Text_UsesHttpEquivMeta()
    {
        var html = "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=koi8-r\"></head>Чай</html>";
        var response = new PageResponse(200, "https://s.test/", Encoding.GetEncoding("koi8-r").GetBytes(html));

        StringAssert.Contains(response.Text, "Чай");
    }

    [TestMethod]
    public void Text_UsesXmlDeclaration()
    {
        var xml = "<?xml version=\"1.0\" encoding=\"windows-1251\"?><urlset><loc>Чай</loc></urlset>";
        var response = new PageResponse(200, "https://s.test/sitemap.xml", Cp1251(xml), "application/xml");

        StringAssert.Contains(response.Text, "Чай");
    }

    [TestMethod]
    public void Text_DoesNotSniffJson()
    {
        // a JSON text that mentions a charset must not switch the decoding
        var json = "{\"note\":\"charset=windows-1251\",\"name\":\"Zürich\"}";
        var response = new PageResponse(200, "https://s.test/api", Encoding.UTF8.GetBytes(json), "application/json");

        StringAssert.Contains(response.Text, "Zürich");
    }

    [TestMethod]
    public void Text_DecodesLatin1AsWindows1252_LikeBrowsers()
    {
        var response = new PageResponse(200, "https://s.test/", [(byte)'1', (byte)'0', 0x80], "text/html; charset=iso-8859-1");

        Assert.AreEqual("10€", response.Text);
    }

    [TestMethod]
    public void Text_DefaultsToUtf8_AndSkipsBom()
    {
        var body = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("ü")).ToArray();

        Assert.AreEqual("ü", new PageResponse(200, "https://s.test/", body).Text);
    }

    [TestMethod]
    public void Text_BomWinsOverDeclaredCharset()
    {
        var body = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Чай")).ToArray();

        Assert.AreEqual("Чай", new PageResponse(200, "https://s.test/", body, "text/html; charset=windows-1251").Text);
    }

    [TestMethod]
    public void Text_UnknownCharset_FallsBackToUtf8()
    {
        var response = new PageResponse(200, "https://s.test/", Encoding.UTF8.GetBytes("Чай"), "text/html; charset=no-such-charset");

        Assert.AreEqual("Чай", response.Text);
    }
}
