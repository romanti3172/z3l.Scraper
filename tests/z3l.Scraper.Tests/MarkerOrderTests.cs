using System.Collections.Concurrent;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Tests;

/// <summary>The markers decide before the status, and they are looked for in a file that came as text.</summary>
[TestClass]
public sealed class MarkerOrderTests
{
    private const string S3Missing =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Error><Code>AccessDenied</Code><Message>Access Denied</Message><RequestId>42H3</RequestId></Error>";
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 2, 3, 0xFF, 0xD9];

    [TestMethod]
    public async Task AFileS3DoesNotHave_403AccessDenied_FailsAtOnce_AndNoProxyIsBlamed()
    {
        var loader = new StatusLoader(_ => (403, System.Text.Encoding.UTF8.GetBytes(S3Missing)));
        var options = ScraperTests.FastOptions();
        options.Rules[PageType.File].NotFoundMarkers.Add("<Code>AccessDenied</Code>");
        var scraper = new Scraper(loader, options).Route(PageType.File, ctx => ctx.Emit(1));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://files.test/gone.jpg", PageType.File));

        Assert.IsEmpty(result.Items);
        Assert.AreEqual(FailureReason.NotFound, result.Failed.Single().Reason);
        Assert.AreEqual(1, loader.Loads);
        Assert.IsEmpty(loader.Blocked);
        Assert.AreEqual(0, result.Stats.Blocked);
    }

    [TestMethod]
    public async Task Without_TheMarker_A403IsStillABlock()
    {
        var loader = new StatusLoader(n => n == 1 ? (403, System.Text.Encoding.UTF8.GetBytes(S3Missing)) : (200, Jpeg));
        var scraper = new Scraper(loader, ScraperTests.FastOptions()).Route(PageType.File, ctx => ctx.Emit(ctx.Response.Body.Length));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://files.test/1.jpg", PageType.File));

        CollectionAssert.AreEqual(new object[] { Jpeg.Length }, result.Items.ToArray());
        Assert.HasCount(1, loader.Blocked);
        StringAssert.Contains(loader.Blocked.Single(), "status 403");
    }

    [TestMethod]
    public async Task ABinaryFile_IsNotReadForTheMarkers()
    {
        // the bytes of a real file that happen to hold the marker: a file is no page
        var body = (byte[])[.. Jpeg[..^2], .. "<Code>AccessDenied</Code>"u8, 0xFF, 0xD9];
        var loader = new StatusLoader(_ => (200, body));
        var options = ScraperTests.FastOptions();
        options.Rules[PageType.File].NotFoundMarkers.Add("<Code>AccessDenied</Code>");
        var scraper = new Scraper(loader, options).Route(PageType.File, ctx => ctx.Emit(ctx.Response.Body.Length));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://files.test/1.jpg", PageType.File));

        CollectionAssert.AreEqual(new object[] { body.Length }, result.Items.ToArray());
    }

    [TestMethod]
    public async Task ABlockMarker_InAFileThatCameAsText_IsABlock()
    {
        var loader = new StatusLoader(n => n == 1 ? (200, "<html><title>Just a moment...</title></html>"u8.ToArray()) : (200, Jpeg));
        var options = ScraperTests.FastOptions();
        options.Rules.Default.BlockedMarkers.Add("Just a moment...");
        var scraper = new Scraper(loader, options).Route(PageType.File, ctx => ctx.Emit(ctx.Response.Body.Length));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://files.test/1.jpg", PageType.File));

        CollectionAssert.AreEqual(new object[] { Jpeg.Length }, result.Items.ToArray());
        StringAssert.Contains(loader.Blocked.Single(), "marker");
    }

    [TestMethod]
    public async Task APageThatSaysItIsNotThere_With403_IsNotFound()
    {
        var loader = new StatusLoader(_ => (403, "<html><body>Product not found</body></html>"u8.ToArray()));
        var options = ScraperTests.FastOptions();
        options.Rules[PageType.Item].NotFoundMarkers.Add("Product not found");
        var scraper = new Scraper(loader, options).Route(PageType.Item, ctx => ctx.Emit(1));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/p/1", PageType.Item));

        Assert.AreEqual(FailureReason.NotFound, result.Failed.Single().Reason);
        Assert.IsEmpty(loader.Blocked);
    }

    [TestMethod]
    public async Task ABlockMarker_BeatsANotFoundMarker()
    {
        var loader = new StatusLoader(n => (200, n == 1
            ? "<html><body>captcha - Product not found</body></html>"u8.ToArray()
            : "<html><body>the product</body></html>"u8.ToArray()));
        var options = ScraperTests.FastOptions();
        options.Rules.Default.BlockedMarkers.Add("captcha");
        options.Rules[PageType.Item].NotFoundMarkers.Add("Product not found");
        var scraper = new Scraper(loader, options).Route(PageType.Item, ctx => ctx.Emit(1));

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/p/1", PageType.Item));

        CollectionAssert.AreEqual(new object[] { 1 }, result.Items.ToArray());
        Assert.HasCount(1, loader.Blocked);
    }

    /// <summary>Answers the n-th load (from 1) with a status and a body; tells the blocks it hears of.</summary>
    private sealed class StatusLoader(Func<int, (int Status, byte[] Body)> answer) : IBlockAwarePageLoader
    {
        private int _loads;
        public int Loads => _loads;
        public ConcurrentQueue<string> Blocked { get; } = new();

        public Task<PageResponse> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken)
        {
            var (status, body) = answer(Interlocked.Increment(ref _loads));
            return Task.FromResult(new PageResponse(status, request.Url, body));
        }

        public void ReportBlocked(ScrapeRequest request, PageResponse response, string reason) => Blocked.Enqueue(reason);
    }
}
