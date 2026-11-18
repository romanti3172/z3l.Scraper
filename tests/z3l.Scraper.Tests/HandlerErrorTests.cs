using z3l.FastHtmlParser;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Tests;

/// <summary>ScraperOptions.MaxConsecutiveHandlerErrors: a broken handler stops the run.</summary>
[TestClass]
public sealed class HandlerErrorTests
{
    private static string Html(string body) => ScraperTests.Html(body);

    /// <summary>One worker, so the pages are handled in the order they were queued.</summary>
    private static ScraperOptions Options(int maxConsecutiveHandlerErrors)
    {
        var options = ScraperTests.FastOptions();
        options.MaxConcurrency = 1;
        options.MaxConsecutiveHandlerErrors = maxConsecutiveHandlerErrors;
        return options;
    }

    private static string Titled(int n) => Html($"<h1>P{n}</h1>");

    /// <summary>The page after a redesign: the title the handler looks for is gone.</summary>
    private static readonly string Redesigned = Html("<div class='title'>P</div>");

    /// <summary>Pages https://{host}/p/0 ... /p/{count - 1}.</summary>
    private static FakeLoader Pages(FakeLoader loader, string host, int count, Func<int, string> page)
    {
        for (var i = 0; i < count; i++) loader.Page($"https://{host}/p/{i}", page(i));
        return loader;
    }

    private static ScrapeRequest[] Requests(string host, int count, string label = "item") =>
        [.. Enumerable.Range(0, count).Select(i => new ScrapeRequest($"https://{host}/p/{i}", label))];

    /// <summary>A handler with a selector that breaks on a page without its title: it throws NullReferenceException.</summary>
    private static Scraper TitleScraper(IPageLoader loader, ScraperOptions options) =>
        new Scraper(loader, options).FallbackRoute(ctx => ctx.Emit(ctx.Html.Find(HtmlTag.H1).Text()!.Trim()));

    [TestMethod]
    public async Task BrokenHandler_StopsTheRun_BeforeMorePagesAreLoaded()
    {
        var loader = Pages(new FakeLoader(), "s.test", 20, _ => Redesigned);

        var result = await TitleScraper(loader, Options(3)).RunToListAsync(Requests("s.test", 20));

        Assert.IsTrue(result.Stats.StoppedByHandlerErrors);
        Assert.HasCount(3, result.Failed);
        Assert.IsTrue(result.Failed.All(failed => failed.Reason == FailureReason.HandlerError && failed.Exception is NullReferenceException));
        Assert.HasCount(3, loader.Loaded);
    }

    [TestMethod]
    public async Task PageTheHandlerTakes_StartsTheCountAgain()
    {
        // every other page is odd: the handler is not broken
        var loader = Pages(new FakeLoader(), "s.test", 20, n => n % 2 == 0 ? Redesigned : Titled(n));

        var result = await TitleScraper(loader, Options(2)).RunToListAsync(Requests("s.test", 20));

        Assert.IsFalse(result.Stats.StoppedByHandlerErrors);
        Assert.HasCount(10, result.Items);
        Assert.HasCount(10, result.Failed);
        Assert.HasCount(20, loader.Loaded);
    }

    [TestMethod]
    public async Task ErrorsAreCounted_ByLabelAndHost()
    {
        var loader = new FakeLoader()
            .Page("https://a.test/p/0", Redesigned)
            .Page("https://a.test/r/0", Titled(0))
            .Page("https://b.test/p/0", Titled(0))
            .Page("https://a.test/p/1", Redesigned)
            .Page("https://a.test/p/2", Titled(2));

        // the pages another label or another host took do not start the count of item on a.test again
        var result = await TitleScraper(loader, Options(2)).RunToListAsync(
            new ScrapeRequest("https://a.test/p/0", "item"),
            new ScrapeRequest("https://a.test/r/0", "review"),
            new ScrapeRequest("https://b.test/p/0", "item"),
            new ScrapeRequest("https://a.test/p/1", "item"),
            new ScrapeRequest("https://a.test/p/2", "item"));

        Assert.IsTrue(result.Stats.StoppedByHandlerErrors);
        Assert.HasCount(2, result.Items);
        Assert.HasCount(2, result.Failed);
        Assert.IsFalse(loader.Loaded.Contains("https://a.test/p/2"));
    }

    [TestMethod]
    public async Task RetryRequestAndNonRetryable_AreNotHandlerErrors()
    {
        var loader = Pages(new FakeLoader(), "s.test", 6, Titled);
        var options = Options(1);
        options.MaxRetries = 0;
        var scraper = new Scraper(loader, options).FallbackRoute(ctx =>
        {
            if (ctx.Request.Url.EndsWith('0') || ctx.Request.Url.EndsWith('2')) throw new RetryRequestException("prices hidden for bots");
            throw new NonRetryableException("age gate");
        });

        var result = await scraper.RunToListAsync(Requests("s.test", 6));

        Assert.IsFalse(result.Stats.StoppedByHandlerErrors);
        Assert.HasCount(6, result.Failed);
        Assert.HasCount(6, loader.Loaded);
    }

    [TestMethod]
    public async Task RetriedHandlerErrors_CountEveryAttempt()
    {
        var loader = Pages(new FakeLoader(), "s.test", 1, _ => Redesigned);
        var options = Options(3);
        options.RetryOnHandlerError = true;
        options.MaxRetries = 10;

        var result = await TitleScraper(loader, options).RunToListAsync(Requests("s.test", 1));

        Assert.IsTrue(result.Stats.StoppedByHandlerErrors);
        Assert.HasCount(3, loader.Loaded);
        Assert.AreEqual(1, result.Stats.Pending);       // stopped in the middle of its retries
    }

    [TestMethod]
    public async Task WithAStateFile_TheRunContinuedAfterTheFix_DoesTheFailedPagesAgain()
    {
        using var folder = new TempFolder();
        var options = Options(2);
        options.StatePath = Path.Combine(folder.Path, "run.json");

        var broken = await TitleScraper(Pages(new FakeLoader(), "s.test", 5, _ => Redesigned), options).RunToListAsync(Requests("s.test", 5));
        Assert.IsTrue(broken.Stats.StoppedByHandlerErrors);
        Assert.HasCount(2, broken.Failed);
        Assert.IsTrue(File.Exists(options.StatePath));

        // the handler fixed: it takes the new markup
        var fixedScraper = new Scraper(Pages(new FakeLoader(), "s.test", 5, _ => Redesigned), options)
            .FallbackRoute(ctx => ctx.Emit(ctx.Request.Url));
        var result = await fixedScraper.RunToListAsync(Requests("s.test", 5));

        CollectionAssert.AreEquivalent(Requests("s.test", 5).Select(request => request.Url).ToArray(), result.Items.ToArray());
        Assert.AreEqual(5, result.Stats.Restored);
        Assert.IsFalse(File.Exists(options.StatePath));
    }

    [TestMethod]
    public async Task NoLimit_ByDefault()
    {
        var loader = Pages(new FakeLoader(), "s.test", 10, _ => Redesigned);
        var options = ScraperTests.FastOptions();
        options.MaxConcurrency = 1;

        var result = await TitleScraper(loader, options).RunToListAsync(Requests("s.test", 10));

        Assert.IsFalse(result.Stats.StoppedByHandlerErrors);
        Assert.HasCount(10, result.Failed);
    }

    [TestMethod]
    public async Task Zero_IsRefused()
    {
        var scraper = TitleScraper(new FakeLoader(), Options(1));
        scraper.Options.MaxConsecutiveHandlerErrors = 0;

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => scraper.RunToListAsync(Requests("s.test", 1)));
    }
}
