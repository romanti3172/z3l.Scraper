using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace z3l.Scraper.Tests;

/// <summary>Stats.Reasons: why attempts gave no page, grouped by kind, reason, label and host.</summary>
[TestClass]
public sealed class ReasonTests
{
    /// <summary>A group of Stats.Reasons with every property given.</summary>
    internal static ReasonCount Group(ReasonKind kind, string reason, string label, string host, long count, long failed, string example, string exampleUrl) =>
        new() { Kind = kind, Reason = reason, Label = label, Host = host, Count = count, Failed = failed, Example = example, ExampleUrl = exampleUrl };

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Checks_AreGroupedByReasonLabelAndHost()
    {
        var options = ScraperTests.FastOptions();      // MaxRetries 2: three attempts
        options.Rules.Default.BlockedMarkers.Add("captcha");
        options.Rules[PageType.Item].MustContain.Add("class='price'");
        var loader = new FakeLoader()
            .Page("https://a.test/item/1", ScraperTests.Html("captcha"))
            .Responses("https://b.test/item/1", (200, ScraperTests.Html("no price yet")), (200, ScraperTests.Html("<b class='price'>5</b>")))
            .Page("https://b.test/item/2", ScraperTests.Html("<b class='price'>7</b>"));
        var scraper = new Scraper(loader, options).Route(PageType.Item, ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync(
            new ScrapeRequest("https://a.test/item/1", PageType.Item),
            new ScrapeRequest("https://b.test/item/1", PageType.Item),
            new ScrapeRequest("https://b.test/item/2", PageType.Item));

        CollectionAssert.AreEqual(new[]
        {
            Group(ReasonKind.Blocked, "marker \"captcha\"", "Item", "a.test", 3, 1, "marker \"captcha\"", "https://a.test/item/1"),
            Group(ReasonKind.BadPage, "no \"class='price'\"", "Item", "b.test", 1, 0, "no \"class='price'\"", "https://b.test/item/1"),
        }, result.Stats.Reasons.ToArray());
        Assert.AreEqual(3, result.Stats.Retries);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task LoadErrors_AreGroupedByKind_NotByMessage()
    {
        var options = ScraperTests.FastOptions();
        options.MaxRetries = 0;
        var loader = new FakeLoader()
            .Throws("https://s.test/1", new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (10.0.0.1:8080)"))
            .Throws("https://s.test/2", new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (10.0.0.2:8080)"));
        var scraper = new Scraper(loader, options).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync("https://s.test/1", "https://s.test/2");

        var reason = result.Stats.Reasons.Single();
        Assert.AreEqual(ReasonKind.LoadError, reason.Kind);
        Assert.AreEqual("ConnectionError", reason.Reason);
        Assert.AreEqual(2, reason.Count);
        Assert.AreEqual(2, reason.Failed);
        StringAssert.StartsWith(reason.Example, "Connection refused (10.0.0.");
    }

    [TestMethod]
    public void LoadErrorKind_LooksInsideTheException()
    {
        Assert.AreEqual("ProxyTunnelError", ScraperRun.LoadErrorKind(new HttpRequestException(HttpRequestError.ProxyTunnelError, "x")));
        Assert.AreEqual("socket ConnectionRefused",
            ScraperRun.LoadErrorKind(new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused))));
        Assert.AreEqual("IOException", ScraperRun.LoadErrorKind(new IOException("x")));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Timeouts_HandlerErrors_AndMissingPages()
    {
        var options = ScraperTests.FastOptions();
        options.MaxRetries = 0;
        options.RequestTimeout = TimeSpan.FromMilliseconds(50);
        var loader = new DelegateLoader(async (r, token) =>
        {
            if (r.Url.EndsWith("/slow", StringComparison.Ordinal)) await Task.Delay(Timeout.Infinite, token);
            return r.Url.EndsWith("/gone", StringComparison.Ordinal)
                ? PageResponse.FromText(404, r.Url, ScraperTests.Html("gone"))
                : PageResponse.FromText(200, r.Url, ScraperTests.Html("ok"));
        });
        var scraper = new Scraper(loader, options)
            .Route("default", ctx => throw new InvalidOperationException($"no price on {ctx.Request.Url}"));

        var result = await scraper.RunToListAsync("https://s.test/slow", "https://s.test/broken", "https://s.test/gone");

        var reasons = result.Stats.Reasons.ToDictionary(r => r.Kind);
        Assert.HasCount(3, reasons);
        Assert.AreEqual("0.05 s", reasons[ReasonKind.Timeout].Reason);
        Assert.AreEqual(1, reasons[ReasonKind.Timeout].Failed);
        Assert.AreEqual("InvalidOperationException", reasons[ReasonKind.HandlerError].Reason);
        Assert.AreEqual("no price on https://s.test/broken", reasons[ReasonKind.HandlerError].Example);
        Assert.AreEqual(Group(ReasonKind.NotFound, "status 404", "default", "s.test", 2, 1, "status 404", "https://s.test/gone"),
            reasons[ReasonKind.NotFound], "NotFoundRetries 1: two attempts");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task HandlerRetries_AreGroupedByTheirMessage()
    {
        var loader = new FakeLoader().Page("https://s.test/1", ScraperTests.Html("ok")).Page("https://s.test/2", ScraperTests.Html("ok"));
        var scraper = new Scraper(loader, ScraperTests.FastOptions())
            .Route("default", _ => throw new RetryRequestException("prices hidden for bots", blocked: true));

        var result = await scraper.RunToListAsync("https://s.test/1", "https://s.test/2");

        var reason = result.Stats.Reasons.Single();
        Assert.AreEqual(ReasonKind.Blocked, reason.Kind);
        Assert.AreEqual("handler: prices hidden for bots", reason.Reason);
        Assert.AreEqual(6, reason.Count);
        Assert.AreEqual(2, reason.Failed);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task TheLastLogLine_NamesTheTopReasons()
    {
        var logger = new ListLogger();
        var options = ScraperTests.FastOptions();
        options.Rules.Default.BlockedMarkers.Add("captcha");
        var loader = new FakeLoader().Page("https://a.test/1", ScraperTests.Html("captcha")).Page("https://a.test/2", ScraperTests.Html("ok"));
        var scraper = new Scraper(loader, options, logger).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        await scraper.RunToListAsync("https://a.test/1");
        var withReasons = logger.Lines.Last(l => l.StartsWith("Scraping finished", StringComparison.Ordinal));
        await scraper.RunToListAsync("https://a.test/2");
        var without = logger.Lines.Last(l => l.StartsWith("Scraping finished", StringComparison.Ordinal));

        StringAssert.EndsWith(withReasons, ". Top reasons: 3x blocked: marker \"captcha\" (default, a.test; 1 failed)");
        Assert.IsFalse(without.Contains("Top reasons", StringComparison.Ordinal), without);
    }

    [TestMethod]
    public void Groups_AreCapped_TheRestCountsInOtherReasons()
    {
        var stats = new ScraperStats();
        for (var i = 0; i < ScraperStats.MaxReasonGroups + 50; i++)
        {
            stats.AddReason(ReasonKind.BadPage, $"no \"part {i}\"", "Item", "s.test", failed: false, "", "");
        }
        stats.AddReason(ReasonKind.BadPage, "no \"part 0\"", "Item", "s.test", failed: true, "last", "https://s.test/0");      // a group that is there still counts

        var reasons = stats.Reasons;
        Assert.HasCount(ScraperStats.MaxReasonGroups + 1, reasons);
        Assert.AreEqual(Group(ReasonKind.BadPage, ScraperStats.OtherReasons, "", "", 50, 0, "", ""), reasons[0]);
        Assert.AreEqual(Group(ReasonKind.BadPage, "no \"part 0\"", "Item", "s.test", 2, 1, "last", "https://s.test/0"), reasons[1]);
    }

    [TestMethod]
    public void OneLine()
    {
        Assert.AreEqual("8711x blocked: marker \"captcha\" (Item, shop.com; 120 failed)",
            Group(ReasonKind.Blocked, "marker \"captcha\"", "Item", "shop.com", 8711, 120, "", "").ToString());
        Assert.AreEqual("12x timeout: 120 s (Item, shop.com)", Group(ReasonKind.Timeout, "120 s", "Item", "shop.com", 12, 0, "", "").ToString());
        Assert.AreEqual("50x bad page: other reasons", Group(ReasonKind.BadPage, "other reasons", "", "", 50, 0, "", "").ToString());
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task FailedRequests_AreThereAfterAStreamingRun()
    {
        var options = ScraperTests.FastOptions();
        options.NotFoundRetries = 0;
        var loader = new FakeLoader().Page("https://s.test/ok", ScraperTests.Html("ok")).Page("https://s.test/gone", ScraperTests.Html("gone"), status: 404);
        var scraper = new Scraper(loader, options).Route("default", ctx => ctx.Emit(ctx.Request.Url));
        Assert.IsEmpty(scraper.FailedRequests);

        var items = new List<object>();
        await foreach (var item in scraper.RunAsync("https://s.test/ok", "https://s.test/gone")) items.Add(item);

        Assert.HasCount(1, items);
        var failed = scraper.FailedRequests.Single();
        Assert.AreEqual("https://s.test/gone", failed.Request.Url);
        Assert.AreEqual(FailureReason.NotFound, failed.Reason);
    }

    private sealed class ListLogger : ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue(formatter(state, exception));
    }
}
