using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using z3l.Scraper.Diagnostics;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Tests;

/// <summary>
/// PublishMetrics and PublishTraces: nothing by default; the meter and the activity source "z3l.Scraper" when they are on. One test at a
/// time: the meter is the process's, and the tests read what it measures.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TelemetryTests
{
    private const string Name = "z3l.Scraper";

    /// <summary>Everything the meter "z3l.Scraper" measures while this lives.</summary>
    private sealed class Measurements : IDisposable
    {
        private readonly MeterListener _listener = new();

        public Measurements()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == Name) listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.Start();
        }

        public ConcurrentQueue<(string Instrument, double Value, Dictionary<string, object?> Tags)> All { get; } = new();

        /// <summary>Reads the gauges now.</summary>
        public void Observe() => _listener.RecordObservableInstruments();

        /// <summary>The values of an instrument whose tags hold all of <paramref name="tags"/>.</summary>
        public double[] Of(string instrument, params (string Key, object? Value)[] tags) =>
        [
            .. All.Where(m => m.Instrument == instrument && tags.All(t => m.Tags.TryGetValue(t.Key, out var value) && Equals(value, t.Value)))
                .Select(m => m.Value),
        ];

        public double Sum(string instrument, params (string Key, object? Value)[] tags) => Of(instrument, tags).Sum();

        public void Dispose() => _listener.Dispose();

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags) copy[tag.Key] = tag.Value;
            All.Enqueue((instrument.Name, value, copy));
        }
    }

    /// <summary>Keeps every span of the activity sources whose names start with "z3l.Scraper".</summary>
    private static ActivityListener Spans(ConcurrentQueue<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith(Name, StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static ScraperOptions Options(string name, bool metrics = true, bool traces = false)
    {
        var options = ScraperTests.FastOptions();
        options.PublishMetrics = metrics;
        options.PublishTraces = traces;
        options.Name = name;
        return options;
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>A scraping service that gives every page at once, for 1.5 credits.</summary>
    private sealed class CreditService() : ServicePageLoader(new ServicePageLoaderOptions())
    {
        protected override Task<PageResponse> CallAsync(HttpClient client, ScrapeRequest request, int level, CancellationToken cancellationToken)
        {
            AddCredits(1.5);
            return Task.FromResult(PageResponse.FromText(200, request.Url, ScraperTests.Html("ok")));
        }
    }

    /// <summary>The same, for a run that publishes nothing.</summary>
    private sealed class QuietService() : ServicePageLoader(new ServicePageLoaderOptions())
    {
        protected override Task<PageResponse> CallAsync(HttpClient client, ScrapeRequest request, int level, CancellationToken cancellationToken)
        {
            AddCredits(1);
            return Task.FromResult(PageResponse.FromText(200, request.Url, ScraperTests.Html("ok")));
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ByDefault_NothingIsPublished()
    {
        using var measurements = new Measurements();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Spans(spans);
        var label = Unique("off");
        var options = ScraperTests.FastOptions();
        await using var quiet = new QuietService();

        var local = await new Scraper(new FakeLoader().Page("https://off.test/", ScraperTests.Html("x")), options)
            .Route(label, ctx => ctx.Emit("item"))
            .RunToListAsync(new ScrapeRequest("https://off.test/", label));
        var remote = await new Scraper(quiet, ScraperTests.FastOptions())
            .Route(label, ctx => ctx.Emit("item"))
            .RunToListAsync(new ScrapeRequest("https://off.test/remote", label));
        measurements.Observe();

        Assert.IsFalse(options.PublishMetrics);
        Assert.IsFalse(options.PublishTraces);
        Assert.IsNull(options.Name);
        Assert.HasCount(1, local.Items);
        Assert.HasCount(1, remote.Items);
        Assert.IsFalse(measurements.All.Any(m => m.Tags.Values.Contains(label)), "no measurement of the runs");
        Assert.IsFalse(measurements.All.Any(m => m.Tags.Values.Contains(nameof(QuietService))), "nor of their loaders");
        Assert.IsFalse(spans.Any(s => s.OperationName.EndsWith(label, StringComparison.Ordinal)), "no span");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Metrics_CountRequestsLoadsRetriesAndItems_ByLabelAndHost()
    {
        using var measurements = new Measurements();
        var name = Unique("metrics");
        var loader = new FakeLoader()
            .Page("https://a.test/", ScraperTests.Html("root"))
            .Page("https://a.test/missing", "", 404)
            .Responses("https://a.test/flaky", (500, ""), (200, ScraperTests.Html("ok")));
        var scraper = new Scraper(loader, Options(name))
            .Route("root", ctx =>
            {
                ctx.Enqueue("https://a.test/missing", "page");
                ctx.Enqueue("https://a.test/flaky", "page");
                ctx.Enqueue("https://a.test/flaky", "page");
                ctx.Emit("root");
            })
            .Route("page", ctx =>
            {
                ctx.Emit("first");
                ctx.Emit("second");
            });

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://a.test/", "root"));
        var mine = ("z3l.scraper.name", (object?)name);
        var host = ("z3l.scraper.host", (object?)"a.test");

        Assert.HasCount(3, result.Items);
        Assert.AreEqual(1, measurements.Sum("z3l.scraper.requests.enqueued", mine, ("z3l.scraper.label", "root")));
        Assert.AreEqual(2, measurements.Sum("z3l.scraper.requests.enqueued", mine, ("z3l.scraper.label", "page")));
        Assert.AreEqual(1, measurements.Sum("z3l.scraper.requests.skipped", mine, ("z3l.scraper.reason", "duplicate")));
        Assert.AreEqual(2, measurements.Sum("z3l.scraper.requests.finished", mine, host, ("z3l.scraper.result", "succeeded")));
        Assert.AreEqual(1, measurements.Sum("z3l.scraper.requests.finished", mine, host, ("z3l.scraper.result", "not_found")));
        Assert.AreEqual(2, measurements.Sum("z3l.scraper.loads", mine, host, ("z3l.scraper.result", "ok")));
        Assert.AreEqual(2, measurements.Sum("z3l.scraper.loads", mine, host, ("z3l.scraper.result", "not_found")), "the 404 and its retry");
        Assert.AreEqual(1, measurements.Sum("z3l.scraper.loads", mine, host, ("z3l.scraper.result", "bad_page")));
        Assert.AreEqual(1, measurements.Sum("z3l.scraper.retries", mine, host, ("z3l.scraper.reason", "not_found")));
        Assert.AreEqual(1, measurements.Sum("z3l.scraper.retries", mine, host, ("z3l.scraper.reason", "bad_page")));
        Assert.AreEqual(3, measurements.Sum("z3l.scraper.items", mine));
        Assert.HasCount(5, measurements.Of("z3l.scraper.load.duration", mine, host), "a time for every load");
        Assert.HasCount(2, measurements.Of("z3l.scraper.handler.duration", mine, ("z3l.scraper.result", "ok")));
        Assert.IsTrue(measurements.Of("z3l.scraper.load.duration", mine).All(seconds => seconds >= 0));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Gauges_ReadTheLiveRun_AndLeaveItWhenItIsOver()
    {
        using var measurements = new Measurements();
        var name = Unique("gauges");
        var inHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = Options(name);
        options.MaxConcurrency = 1;
        var scraper = new Scraper(new FakeLoader().Page("https://g.test/1", ScraperTests.Html("1")).Page("https://g.test/2", ScraperTests.Html("2")), options)
            .Route("default", async _ =>
            {
                inHandler.TrySetResult();
                await release.Task;
            });

        var run = scraper.RunToListAsync(new ScrapeRequest("https://g.test/1"), new ScrapeRequest("https://g.test/2"));
        await inHandler.Task;
        measurements.Observe();
        release.SetResult();
        await run;
        var during = measurements.All.Count;
        measurements.Observe();
        var mine = ("z3l.scraper.name", (object?)name);

        Assert.AreEqual(1, measurements.Sum("z3l.scraper.requests.active", mine));
        Assert.AreEqual(1, measurements.Sum("z3l.scraper.requests.queued", mine));
        Assert.AreEqual(2, measurements.Sum("z3l.scraper.requests.pending", mine));
        Assert.IsFalse(measurements.All.Skip(during).Any(m => Equals(m.Tags.GetValueOrDefault("z3l.scraper.name"), name)), "a run that is over is not read");
    }

    [TestMethod]
    public void HostsPastTheLimit_AreTaggedOther()
    {
        var metrics = new RunMetrics(ScraperTelemetry.Instruments, new ScraperStats(), null);

        for (var i = 0; i < RunMetrics.MaxHosts; i++) Assert.AreEqual($"h{i}.test", metrics.HostTagOf($"h{i}.test"));

        Assert.AreEqual(RunMetrics.OtherHosts, metrics.HostTagOf("late.test"));
        Assert.AreEqual("h7.test", metrics.HostTagOf("h7.test"), "a host seen before keeps its name");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Traces_ASpanPerAttempt_TheHandlerUnderIt_AndWhatItCallsUnderThat()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Spans(spans);
        using var own = new ActivitySource("z3l.Scraper.Tests");
        var label = Unique("traces");
        var loader = new FakeLoader().Responses("https://b.test/p", (500, ""), (200, ScraperTests.Html("ok")));
        var scraper = new Scraper(loader, Options(Unique("traces"), metrics: false, traces: true))
            .Route(label, ctx =>
            {
                using var dbCall = own.StartActivity("db call");
                ctx.Emit("item");
            });

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://b.test/p", label));
        var attempts = spans.Where(s => s.OperationName == $"scrape {label}").OrderBy(s => s.StartTimeUtc).ToArray();
        var handler = spans.Single(s => s.OperationName == $"handle {label}");
        var call = spans.Single(s => s.OperationName == "db call" && s.ParentSpanId == handler.SpanId);

        Assert.HasCount(1, result.Items);
        Assert.HasCount(2, attempts);
        Assert.AreEqual(ActivityStatusCode.Error, attempts[0].Status, "a 500 is no page");
        Assert.AreEqual("bad_page", attempts[0].GetTagItem("z3l.scraper.result"));
        Assert.AreEqual(500, attempts[0].GetTagItem("http.response.status_code"));
        Assert.AreEqual(1, attempts[0].GetTagItem("z3l.scraper.attempt"));
        Assert.AreEqual("ok", attempts[1].GetTagItem("z3l.scraper.result"));
        Assert.AreEqual(ActivityStatusCode.Unset, attempts[1].Status);
        Assert.AreEqual(2, attempts[1].GetTagItem("z3l.scraper.attempt"));
        Assert.AreEqual("https://b.test/p", attempts[1].GetTagItem("url.full"));
        Assert.AreEqual("b.test", attempts[1].GetTagItem("server.address"));
        Assert.AreEqual(attempts[1].SpanId, handler.ParentSpanId);
        Assert.AreEqual(handler.TraceId, call.TraceId);
        Assert.AreNotEqual(attempts[0].TraceId, attempts[1].TraceId, "every attempt is a trace of its own");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Traces_OfARunStartedInASpan_AreLinkedToIt_NotUnderIt()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Spans(spans);
        using var own = new ActivitySource("z3l.Scraper.Tests");
        var label = Unique("linked");
        var scraper = new Scraper(new FakeLoader().Page("https://l.test/", ScraperTests.Html("x")), Options(Unique("linked"), metrics: false, traces: true))
            .Route(label, ctx => ctx.Emit("item"));

        using (var crawl = own.StartActivity("crawl"))
        {
            await scraper.RunToListAsync(new ScrapeRequest("https://l.test/", label));
            var attempt = spans.Single(s => s.OperationName == $"scrape {label}");

            Assert.IsNotNull(crawl);
            Assert.AreEqual(default, attempt.ParentSpanId);
            Assert.AreNotEqual(crawl.TraceId, attempt.TraceId);
            Assert.AreEqual(crawl.SpanId, attempt.Links.Single().Context.SpanId);
            Assert.AreSame(crawl, Activity.Current, "the run does not change the caller's span");
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AServiceLoader_OfARunWithMetrics_CountsItsLoadsAndCredits()
    {
        using var measurements = new Measurements();
        await using var service = new CreditService();
        var loaderTag = ("z3l.scraper.loader", (object?)nameof(CreditService));

        var result = await new Scraper(service, Options(Unique("service")))
            .Route("default", ctx => ctx.Emit("item"))
            .RunToListAsync(new ScrapeRequest("https://c.test/1"), new ScrapeRequest("https://c.test/2"));
        measurements.Observe();

        Assert.HasCount(2, result.Items);
        Assert.AreEqual(2, measurements.Sum("z3l.scraper.remote.sent", loaderTag));
        Assert.AreEqual(2, measurements.Sum("z3l.scraper.remote.results", loaderTag, ("z3l.scraper.result", "answered")));
        Assert.AreEqual(3.0, measurements.Sum("z3l.scraper.service.credits", loaderTag));
        Assert.AreEqual(0, measurements.Sum("z3l.scraper.remote.pending", loaderTag), "nothing waits after the run");
        Assert.IsNotEmpty(measurements.Of("z3l.scraper.remote.pending", loaderTag), "the loader is observed");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AProxyList_ThatPublishes_CountsItsReports_AndItsProxiesByState()
    {
        using var measurements = new Measurements();
        var list = new ProxyList(["a.test:1", "b.test:1", "c.test:1"]) { MaxConsecutiveFailures = 1 };
        list.PublishMetrics();

        var a = await list.AcquireAsync("shop.test", CancellationToken.None);
        list.ReportSuccess(a);
        list.ReportFailure(a, "no answer");                 // one failure in a row is enough here: a rests
        var b = await list.AcquireAsync("shop.test", CancellationToken.None);
        list.ReportBlocked(b, "shop.test");                 // b rests for the shop
        measurements.Observe();
        var result = (string value) => ("z3l.scraper.result", (object?)value);
        var state = (string value) => ("z3l.scraper.state", (object?)value);

        Assert.AreEqual(1, measurements.Sum("z3l.scraper.proxy.results", result("success")));
        Assert.AreEqual(1, measurements.Sum("z3l.scraper.proxy.results", result("failure")));
        Assert.AreEqual(1, measurements.Sum("z3l.scraper.proxy.results", result("blocked")));
        Assert.AreEqual(1, measurements.Sum("z3l.scraper.proxies", state("alive")));
        Assert.AreEqual(1, measurements.Sum("z3l.scraper.proxies", state("resting")));
        Assert.AreEqual(1, measurements.Sum("z3l.scraper.proxies", state("resting_for_site")));
        GC.KeepAlive(list);
    }
}
