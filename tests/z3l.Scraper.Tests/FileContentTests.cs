using System.Collections.Concurrent;
using z3l.Scraper.Loading;
using z3l.Scraper.Validation;

namespace z3l.Scraper.Tests;

[TestClass]
public sealed class FileContentTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 2, 3, 0xFF, 0xD9];
    private static readonly byte[] Png =
        [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82];
    private static readonly byte[] Squid =
        "<!DOCTYPE html>\n<html><head><title>ERROR: The requested URL could not be retrieved</title></head><body></body></html>"u8.ToArray();

    public static IEnumerable<object[]> Good =>
    [
        ["jpeg", Jpeg],
        ["jpeg with padding after FFD9", (byte[])[.. Jpeg, 0, 0, 0, 0]],
        ["png", Png],
        ["gif", "GIF89a\x01\x00\x01\x00\x00\x00\x00;"u8.ToArray()],
        ["webp", (byte[])[.. "RIFF"u8, 8, 0, 0, 0, .. "WEBPVP8 "u8]],
        ["pdf", "%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF\n"u8.ToArray()],
        ["avif", (byte[])[0, 0, 0, 0x1C, .. "ftypavif"u8, 0, 0, 0, 0]],
        ["svg", "<?xml version=\"1.0\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"u8.ToArray()],
        ["zip", (byte[])[.. "PK\x03\x04"u8, 20, 0, 0, 0]],
        ["unknown binary", (byte[])[0, 1, 2, 3, 200, 201, 202]],
    ];

    public static IEnumerable<object[]> Bad =>
    [
        ["cut jpeg", Jpeg[..10], "cut jpeg"],
        ["cut png", Png[..12], "cut png"],
        ["cut gif", "GIF89a\x01\x00\x01\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00"u8.ToArray(), "cut gif"],
        ["cut webp", (byte[])[.. "RIFF"u8, 0xFF, 0, 0, 0, .. "WEBPVP8 "u8], "cut webp"],
        ["cut pdf", "%PDF-1.7\n1 0 obj\n"u8.ToArray(), "cut pdf"],
        ["html of a proxy", Squid, "text instead of a file"],
        ["json", "{\"error\":\"not found\"}"u8.ToArray(), "text instead of a file"],
        ["plain text", "Access denied"u8.ToArray(), "text instead of a file"],
        ["empty", Array.Empty<byte>(), "empty file"],
    ];

    [TestMethod]
    [DynamicData(nameof(Good))]
    public void AFileThatLooksRight_Passes(string name, byte[] body) =>
        Assert.IsNull(FileContent.Problem(body), name);

    [TestMethod]
    [DynamicData(nameof(Bad))]
    public void APageOrACutFile_IsAProblem(string name, byte[] body, string problem) =>
        StringAssert.StartsWith(FileContent.Problem(body), problem, name);

    [TestMethod]
    public void TheProblemShowsTheBeginningOfTheText() =>
        StringAssert.Contains(FileContent.Problem(Squid), "ERROR: The requested URL could not be retriev");

    [TestMethod]
    public async Task APageOfAProxyInsteadOfAFile_IsRetried_AndItsLoaderHearsOfIt()
    {
        var loader = new FileLoader(_ => Squid);
        loader.Next("https://s.test/1.jpg", Jpeg);
        var scraper = new Scraper(loader, ScraperTests.FastOptions())
            .Route(PageType.File, ctx => ctx.Emit(ctx.Response.Body.Length));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/1.jpg", PageType.File));

        CollectionAssert.AreEqual(new object[] { Jpeg.Length }, result.Items.ToArray());
        Assert.AreEqual(2, loader.Loads["https://s.test/1.jpg"]);
        StringAssert.StartsWith(loader.Bad.Single(), "text instead of a file");
    }

    [TestMethod]
    public async Task AFileThatIsAlwaysCut_FailsAfterTheRetries()
    {
        var loader = new FileLoader(_ => Jpeg[..10]);
        var options = ScraperTests.FastOptions();
        options.MaxRetries = 2;
        var scraper = new Scraper(loader, options).Route(PageType.File, ctx => ctx.Emit(1));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/1.jpg", PageType.File));

        Assert.IsEmpty(result.Items);
        StringAssert.Contains(result.Failed.Single().Message, "cut jpeg");
    }

    [TestMethod]
    public async Task TheCheckCanBeTurnedOff_ForFilesThatAreText()
    {
        var loader = new FileLoader(_ => "id;name\n1;a\n"u8.ToArray());
        var options = ScraperTests.FastOptions();
        options.Rules[PageType.File].CheckFileContent = false;
        var scraper = new Scraper(loader, options).Route(PageType.File, ctx => ctx.Emit(ctx.Response.Body.Length));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/export.csv", PageType.File));

        CollectionAssert.AreEqual(new object[] { 12 }, result.Items.ToArray());
    }

    [TestMethod]
    public async Task OtherLabels_AreNotChecked()
    {
        var loader = new FileLoader(_ => "<html><body>page</body></html>"u8.ToArray());
        var scraper = new Scraper(loader, ScraperTests.FastOptions()).Route(PageType.Item, ctx => ctx.Emit(ctx.Response.Text));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/page", PageType.Item));

        Assert.HasCount(1, result.Items);
    }

    /// <summary>Gives the body of the url, or a queued one for its next load; tells the bad responses it hears of.</summary>
    private sealed class FileLoader(Func<string, byte[]> body) : IBlockAwarePageLoader
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<byte[]>> _next = new();
        public ConcurrentDictionary<string, int> Loads { get; } = new();
        public ConcurrentQueue<string> Bad { get; } = new();

        /// <summary>The body of the second load of the url (the first is the default one).</summary>
        public void Next(string url, byte[] next) => _next.GetOrAdd(url, _ => new()).Enqueue(next);

        public Task<PageResponse> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken)
        {
            var n = Loads.AddOrUpdate(request.Url, 1, (_, x) => x + 1);
            var bytes = n > 1 && _next.TryGetValue(request.Url, out var queue) && queue.TryDequeue(out var next) ? next : body(request.Url);
            return Task.FromResult(new PageResponse(200, request.Url, bytes));
        }

        public void ReportBlocked(ScrapeRequest request, PageResponse response, string reason) { }
        public void ReportBadResponse(ScrapeRequest request, PageResponse response, string reason) => Bad.Enqueue(reason);
    }
}
