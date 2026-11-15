using System.Text;

namespace z3l.Scraper.Tests;

[TestClass]
public sealed class PageResponseTests
{
    [ClassInitialize]
    public static void RegisterCodePages(TestContext _) => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static byte[] Cp1251(string text) => Encoding.GetEncoding("windows-1251").GetBytes(text);

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
