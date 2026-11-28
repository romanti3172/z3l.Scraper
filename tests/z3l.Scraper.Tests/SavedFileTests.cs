using System.Collections.Concurrent;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Tests;

[TestClass]
public sealed class SavedFileTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 2, 3, 0xFF, 0xD9];
    private static readonly byte[] OtherJpeg = [0xFF, 0xD8, 0xFF, 0xE1, 9, 9, 9, 0xFF, 0xD9];
    private const string Url = "https://s.test/1.jpg";

    private static ScrapeRequest Request(string path) => new(Url, PageType.File) { SavePath = path };

    /// <summary>A handler as the users write it: saves the file, unless it is the saved one.</summary>
    private static Scraper SavingScraper(IPageLoader loader, ScraperOptions? options = null, Func<ScrapeContext, bool>? retry = null) =>
        new Scraper(loader, options ?? ScraperTests.FastOptions()).Route(PageType.File, async ctx =>
        {
            if (retry?.Invoke(ctx) == true) throw new RetryRequestException("not the file");
            if (!ctx.Response.FromSavedFile) await File.WriteAllBytesAsync(ctx.Request.SavePath!, ctx.Response.Body);
            ctx.Emit((ctx.Response.FromSavedFile, ctx.Response.Body.Length));
        });

    [TestMethod]
    public async Task ASavedFile_IsGivenInsteadOfALoad()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(folder.Path);
        var path = Path.Combine(folder.Path, "1.jpg");
        File.WriteAllBytes(path, Jpeg);
        var loader = new BytesLoader(OtherJpeg);
        var scraper = SavingScraper(loader);

        var result = await scraper.RunToListAsync(Request(path));

        CollectionAssert.AreEqual(new object[] { (true, Jpeg.Length) }, result.Items.ToArray());
        Assert.IsEmpty(loader.Loads);
        Assert.AreEqual(1, result.Stats.FromSavedFiles);
        Assert.AreEqual(0, result.Stats.Loads);
        Assert.AreEqual(0, result.Stats.Retries);
        StringAssert.Contains(result.Stats.ToString(), "1 saved files");
    }

    [TestMethod]
    public async Task WithoutTheFile_ThePageIsLoadedAndSaved()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(folder.Path);
        var path = Path.Combine(folder.Path, "1.jpg");
        var loader = new BytesLoader(Jpeg);

        var result = await SavingScraper(loader).RunToListAsync(Request(path));

        CollectionAssert.AreEqual(new object[] { (false, Jpeg.Length) }, result.Items.ToArray());
        Assert.HasCount(1, loader.Loads);
        CollectionAssert.AreEqual(Jpeg, File.ReadAllBytes(path));
    }

    [TestMethod]
    [DataRow("html of a proxy", "<html><body>The requested URL could not be retrieved</body></html>")]
    [DataRow("cut jpeg", null)]
    public async Task ASavedFileThatFailsTheChecks_IsDeletedAndLoaded_NoAttemptAndTheLoaderDoesNotHear(string name, string? text)
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(folder.Path);
        var path = Path.Combine(folder.Path, "1.jpg");
        File.WriteAllBytes(path, text != null ? System.Text.Encoding.UTF8.GetBytes(text) : Jpeg[..8]);
        var loader = new BytesLoader(Jpeg);

        var result = await SavingScraper(loader).RunToListAsync(Request(path));

        CollectionAssert.AreEqual(new object[] { (false, Jpeg.Length) }, result.Items.ToArray(), name);
        Assert.HasCount(1, loader.Loads);
        Assert.IsEmpty(loader.Bad);
        Assert.AreEqual(0, result.Stats.Retries);
        CollectionAssert.AreEqual(Jpeg, File.ReadAllBytes(path));
    }

    [TestMethod]
    public async Task ASavedFileTheHandlerAsksToRetry_IsDeletedAndLoaded()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(folder.Path);
        var path = Path.Combine(folder.Path, "1.jpg");
        File.WriteAllBytes(path, OtherJpeg);
        var loader = new BytesLoader(Jpeg);

        var result = await SavingScraper(loader, retry: ctx => ctx.Response.FromSavedFile).RunToListAsync(Request(path));

        CollectionAssert.AreEqual(new object[] { (false, Jpeg.Length) }, result.Items.ToArray());
        Assert.HasCount(1, loader.Loads);
        Assert.IsEmpty(loader.Bad);
    }

    [TestMethod]
    public async Task TheReuseCanBeTurnedOff()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(folder.Path);
        var path = Path.Combine(folder.Path, "1.jpg");
        File.WriteAllBytes(path, OtherJpeg);
        var loader = new BytesLoader(Jpeg);
        var options = ScraperTests.FastOptions();
        options.ReuseSavedFiles = false;

        var result = await SavingScraper(loader, options).RunToListAsync(Request(path));

        CollectionAssert.AreEqual(new object[] { (false, Jpeg.Length) }, result.Items.ToArray());
        Assert.HasCount(1, loader.Loads);
        CollectionAssert.AreEqual(Jpeg, File.ReadAllBytes(path));
    }

    [TestMethod]
    public async Task ALoadedFileThatFailsTheChecks_IsRetried_AndTheLoaderHears()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(folder.Path);
        var path = Path.Combine(folder.Path, "1.jpg");
        var loader = new BytesLoader("<html>proxy</html>"u8.ToArray(), Jpeg);

        var result = await SavingScraper(loader).RunToListAsync(Request(path));

        CollectionAssert.AreEqual(new object[] { (false, Jpeg.Length) }, result.Items.ToArray());
        Assert.HasCount(2, loader.Loads);
        Assert.HasCount(1, loader.Bad);
    }

    /// <summary>Gives the bodies one per load, the last one again and again.</summary>
    private sealed class BytesLoader(params byte[][] bodies) : IBlockAwarePageLoader
    {
        public ConcurrentQueue<string> Loads { get; } = new();
        public ConcurrentQueue<string> Bad { get; } = new();

        public Task<PageResponse> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken)
        {
            Loads.Enqueue(request.Url);
            var body = bodies[Math.Min(Loads.Count, bodies.Length) - 1];
            return Task.FromResult(new PageResponse(200, request.Url, body));
        }

        public void ReportBlocked(ScrapeRequest request, PageResponse response, string reason) => Bad.Enqueue(reason);
        public void ReportBadResponse(ScrapeRequest request, PageResponse response, string reason) => Bad.Enqueue(reason);
    }
}
