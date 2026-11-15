using System.Text;

namespace z3l.Scraper.Tests;

[TestClass]
public sealed class PageResponseTests
{
    [ClassInitialize]
    public static void RegisterCodePages(TestContext _) => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [TestMethod]
    public void Text_UsesCharsetFromContentType()
    {
        var body = Encoding.GetEncoding("windows-1251").GetBytes("<html>Привет</html>");
        var response = new PageResponse(200, "https://s.test/", body, "text/html; charset=windows-1251");

        Assert.AreEqual("<html>Привет</html>", response.Text);
    }

    [TestMethod]
    public void Text_UsesMetaCharset_WhenHeaderHasNone()
    {
        var body = Encoding.GetEncoding("windows-1251").GetBytes("<html><head><meta charset=\"windows-1251\"></head>Цена</html>");
        var response = new PageResponse(200, "https://s.test/", body, "text/html");

        StringAssert.Contains(response.Text, "Цена");
    }

    [TestMethod]
    public void Text_DefaultsToUtf8_AndSkipsBom()
    {
        var body = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("ü")).ToArray();

        Assert.AreEqual("ü", new PageResponse(200, "https://s.test/", body).Text);
    }
}
