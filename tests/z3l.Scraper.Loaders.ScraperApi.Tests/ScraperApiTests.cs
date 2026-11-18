using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Web;
using Microsoft.Extensions.Logging;
using z3l.Scraper.Loading;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace z3l.Scraper.Loaders.ScraperApi.Tests;

/// <summary>ScraperApiPageLoader against a fake ScraperAPI that answers as its documentation says.</summary>
[TestClass]
public sealed class ScraperApiTests
{
    private const string Page = "https://shop.test/p/1";
    private const string Key = "test-key-7f3a";

    /// <summary>A call as ScraperAPI got it, with the names of its parameters in their order.</summary>
    private sealed record Call(HttpMethod Method, string[] Names, Dictionary<string, string> Query, Dictionary<string, string> Headers, string? Body,
        string? ContentType);

    /// <summary>The fake service: keeps every call and answers with <c>answer</c>.</summary>
    private sealed class FakeScraperApi(Func<Call, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public ConcurrentQueue<Call> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var call = new Call(
                request.Method,
                [.. query.AllKeys.Select(k => k!)],
                query.AllKeys.ToDictionary(k => k!, k => query[k]!),
                request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase),
                request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Content?.Headers.ContentType?.ToString());
            Calls.Enqueue(call);
            return answer(call);
        }
    }

    /// <summary>A page as ScraperAPI gives it: in UTF-8, with the site's headers and the cost.</summary>
    private static HttpResponseMessage Answer(string html, HttpStatusCode status = HttpStatusCode.OK, double cost = 1, string? finalUrl = null,
        string contentType = "text/html; charset=utf-8")
    {
        var answer = new HttpResponseMessage(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(html)) };
        answer.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        answer.Headers.TryAddWithoutValidation("sa-credit-cost", cost.ToString(CultureInfo.InvariantCulture));
        answer.Headers.TryAddWithoutValidation("Set-Cookie", "session=abc; Path=/");
        if (finalUrl != null) answer.Headers.TryAddWithoutValidation("sa-final-url", finalUrl);
        return answer;
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string text) => new(status) { Content = new StringContent(text) };

    /// <summary>
    /// The 500 after 70 seconds of tries, not billed. With <c>sa-statuscode</c> when <paramref name="siteStatus"/> is given: the async
    /// API's results carry that header; the documentation does not show it on this API's 500.
    /// </summary>
    private static HttpResponseMessage Failed(int? siteStatus = null)
    {
        var answer = Error(HttpStatusCode.InternalServerError, "Request failed. You will not be charged for this request.");
        if (siteStatus is { } status) answer.Headers.TryAddWithoutValidation("sa-statuscode", status.ToString(CultureInfo.InvariantCulture));
        return answer;
    }

    private static ScraperApiPageLoaderOptions Options(FakeScraperApi api) => new() { ApiKey = Key, HttpClient = new HttpClient(api) };

    private static ScraperOptions FastScraper() => new() { RetryDelay = TimeSpan.Zero, ProgressInterval = null, MaxRetries = 2 };

    private static string Html(string body) => $"<html><body>{body}</body></html>";

    private static FailedRequest Failure(ScrapeResult result, string path) =>
        result.Failed.Single(f => f.Request.Url.EndsWith(path, StringComparison.Ordinal));

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task APage_IsAsTheApiGaveIt()
    {
        var api = new FakeScraperApi(_ => Answer(Html("<h1>Lamp</h1>"), finalUrl: "https://shop.test/p/1?moved"));
        await using var loader = new ScraperApiPageLoader(Options(api));

        var page = await loader.LoadAsync(new ScrapeRequest(Page), CancellationToken.None);

        var call = api.Calls.Single();
        Assert.AreEqual(HttpMethod.Get, call.Method);
        CollectionAssert.AreEqual(new[] { "api_key", "url" }, call.Names, "no browser, standard proxies, no headers: the key and the page only");
        Assert.AreEqual(Key, call.Query["api_key"]);
        Assert.AreEqual(Page, call.Query["url"]);
        Assert.IsEmpty(call.Headers);

        Assert.AreEqual(200, page.StatusCode);
        Assert.AreEqual("https://shop.test/p/1?moved", page.Url, "the final URL from sa-final-url");
        StringAssert.Contains(page.Text, "Lamp");
        Assert.AreEqual("session=abc; Path=/", page.Headers["Set-Cookie"]);
        Assert.AreEqual("text/html; charset=utf-8", page.ContentType);
        Assert.AreEqual("1", page.Headers["sa-credit-cost"], "the API's own headers stay");
        Assert.AreEqual(1, loader.Credits);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task APostAndItsHeaders_GoToTheSite()
    {
        var api = new FakeScraperApi(_ => Answer(Html("ok")));
        var options = Options(api);
        options.CountryCode = "DE";
        await using var loader = new ScraperApiPageLoader(options);
        var request = new ScrapeRequest("https://shop.test/search")
        {
            Method = HttpMethod.Post,
            Body = "q=lamp",
            Headers = new Dictionary<string, string>
            {
                ["X-Token"] = "t1",
                ["Cookie"] = "a=1; b=2",
                ["Accept-Encoding"] = "gzip, br",
                ["Host"] = "shop.test",
            },
        };

        await loader.LoadAsync(request, CancellationToken.None);

        var call = api.Calls.Single();
        Assert.AreEqual(HttpMethod.Post, call.Method);
        Assert.AreEqual("q=lamp", call.Body);
        Assert.AreEqual("application/x-www-form-urlencoded", call.ContentType);
        Assert.AreEqual("true", call.Query["keep_headers"]);
        Assert.AreEqual("t1", call.Headers["X-Token"]);
        Assert.AreEqual("a=1; b=2", call.Headers["Cookie"], "cookies go as a header");
        Assert.IsFalse(call.Headers.ContainsKey("Accept-Encoding"), "the API's answer comes uncompressed");
        Assert.IsFalse(call.Headers.ContainsKey("Host"), "the call goes to the API");
        Assert.AreEqual("de", call.Query["country_code"]);
        Assert.AreEqual("url", call.Names[^1], "the page's URL goes last");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ABlockedPage_GoesUpToUltraPremium()
    {
        var api = new FakeScraperApi(call => call.Query.ContainsKey("ultra_premium")
            ? Answer(Html("the page"), cost: 30)
            : Answer(Html("captcha"), cost: call.Query.ContainsKey("premium") ? 10 : 1));
        var options = Options(api);
        options.Levels.Add(ScraperApiProxy.UltraPremium);
        await using var loader = new ScraperApiPageLoader(options);
        var scraperOptions = FastScraper();
        scraperOptions.Rules.Default.BlockedMarkers.Add("captcha");
        var request = new ScrapeRequest(Page) { Headers = new Dictionary<string, string> { ["X-Token"] = "t1" } };
        var scraper = new Scraper(loader, scraperOptions).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync(request);

        Assert.HasCount(1, result.Items);
        var calls = api.Calls.ToArray();
        Assert.HasCount(3, calls, "standard, premium, ultra premium");
        Assert.IsFalse(calls[0].Query.ContainsKey("premium"));
        Assert.AreEqual("true", calls[1].Query["premium"]);
        Assert.AreEqual("t1", calls[1].Headers["X-Token"]);
        var ultra = calls[2];
        Assert.AreEqual("true", ultra.Query["ultra_premium"]);
        Assert.IsFalse(ultra.Query.ContainsKey("premium"), "premium and ultra_premium do not go together");
        Assert.IsFalse(ultra.Query.ContainsKey("keep_headers"), "ultra premium proxies take no headers");
        Assert.IsFalse(ultra.Headers.ContainsKey("X-Token"));
        Assert.AreEqual(41, loader.Credits);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task A500WithTheSitesRefusal_GoesToPremiumProxies()
    {
        var api = new FakeScraperApi(call => call.Query.ContainsKey("premium") ? Answer(Html("the page"), cost: 10) : Failed(siteStatus: 403));
        await using var loader = new ScraperApiPageLoader(Options(api));
        var scraper = new Scraper(loader, FastScraper()).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var first = await scraper.RunToListAsync(Page);
        var second = await scraper.RunToListAsync("https://shop.test/p/2");

        Assert.HasCount(1, first.Items);
        Assert.HasCount(1, second.Items);
        CollectionAssert.AreEqual(new[] { false, true, true }, api.Calls.Select(c => c.Query.ContainsKey("premium")).ToArray(),
            "the 500 is not billed; the host goes on with premium proxies");
        Assert.AreEqual(20, loader.Credits);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    [DataRow(0, DisplayName = "no reason given")]
    [DataRow(500, DisplayName = "the site's own error")]
    [DataRow(504, DisplayName = "the site did not answer")]
    public async Task A500_IsRetriedAtTheSameLevel(int siteStatus)
    {
        var attempts = 0;
        var api = new FakeScraperApi(_ => Interlocked.Increment(ref attempts) == 1 ? Failed(siteStatus == 0 ? null : siteStatus) : Answer(Html("the page")));
        await using var loader = new ScraperApiPageLoader(Options(api));
        var scraper = new Scraper(loader, FastScraper()).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync(Page);

        Assert.HasCount(1, result.Items);
        Assert.HasCount(2, api.Calls);
        Assert.IsFalse(api.Calls.Any(c => c.Query.ContainsKey("premium")), "premium proxies do not mend a timeout or the site's own error");
        Assert.AreEqual(1, loader.Credits, "the 500 is not billed");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Statuses_MeanWhatTheDocsSay()
    {
        var busy = 0;
        var api = new FakeScraperApi(call => call.Query["url"] switch
        {
            "https://shop.test/gone" => Answer("Not Found", HttpStatusCode.NotFound),
            "https://shop.test/key" => Error(HttpStatusCode.Unauthorized, $"Unauthorized request, key {call.Query["api_key"]}"),
            "https://shop.test/credits" => Error(HttpStatusCode.Forbidden, "You have exhausted the API Credits available in this monthly cycle."),
            "https://shop.test/bad" => Error(HttpStatusCode.BadRequest, $"Error, malformed request: ?api_key={call.Query["api_key"]}&url=..."),
            "https://shop.test/busy" when Interlocked.Increment(ref busy) == 1 => Error(HttpStatusCode.TooManyRequests, "Too many simultaneous requests."),
            _ => Answer(Html("ok")),
        });
        await using var loader = new ScraperApiPageLoader(Options(api));
        var scraper = new Scraper(loader, FastScraper()).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync(
            "https://shop.test/gone", "https://shop.test/key", "https://shop.test/credits", "https://shop.test/bad", "https://shop.test/busy");

        CollectionAssert.AreEqual(new[] { "https://shop.test/busy" }, result.Items.ToArray(), "429 is retried");
        Assert.AreEqual(FailureReason.NotFound, Failure(result, "/gone").Reason);
        foreach (var path in new[] { "/key", "/credits", "/bad" })
        {
            var failure = Failure(result, path);
            Assert.AreEqual(FailureReason.NonRetryable, failure.Reason, path);
            Assert.IsFalse(failure.Message.Contains(Key, StringComparison.Ordinal), $"{path}: the key does not go to the logs");
            Assert.AreEqual(1, api.Calls.Count(c => c.Query["url"] == "https://shop.test" + path), $"{path} is not called again");
        }
        StringAssert.Contains(Failure(result, "/credits").Message, "exhausted the API Credits");
        StringAssert.Contains(Failure(result, "/bad").Message, "api_key=***");
    }

    [TestMethod]
    public async Task TheKeyGoesFirst_ThePageLast_AndConfigureAddsParameters()
    {
        var options = Options(new FakeScraperApi(_ => Answer("")));
        options.RenderJs = true;
        options.WaitForSelector = "div.price";
        options.Configure = (_, level, parameters) =>
        {
            parameters["device_type"] = "mobile";
            if (level == 0) parameters["session_number"] = "42";     // sessions do not go with premium proxies
        };
        await using var loader = new ScraperApiPageLoader(options);
        var request = new ScrapeRequest("https://shop.test/p?id=1&color=red");

        var standard = HttpUtility.ParseQueryString(loader.CallUri(request, ScraperApiProxy.Standard, 0).Query);
        var premium = loader.CallUri(request, ScraperApiProxy.Premium, 1);

        CollectionAssert.AreEqual(new[] { "api_key", "render", "wait_for_selector", "device_type", "session_number", "url" }, standard.AllKeys);
        Assert.AreEqual("div.price", standard["wait_for_selector"]);
        Assert.AreEqual("https://shop.test/p?id=1&color=red", standard["url"]);
        Assert.StartsWith("https://api.scraperapi.com/?api_key=test-key-7f3a&render=true&", premium.AbsoluteUri);
        StringAssert.Contains(premium.AbsoluteUri, "&premium=true&");
        Assert.IsFalse(premium.AbsoluteUri.Contains("session_number", StringComparison.Ordinal));
        Assert.EndsWith("&url=https%3A%2F%2Fshop.test%2Fp%3Fid%3D1%26color%3Dred", premium.AbsoluteUri, "the page's URL goes escaped, and last");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task APage_IsReadAsUtf8_WhateverItsMetaSays()
    {
        const string html = "<html><head><meta charset=\"windows-1251\"></head><body>Лампа</body></html>";
        var api = new FakeScraperApi(_ => Answer(html, contentType: "text/html"));
        await using var loader = new ScraperApiPageLoader(Options(api));

        var page = await loader.LoadAsync(new ScrapeRequest(Page), CancellationToken.None);

        Assert.AreEqual("text/html; charset=utf-8", page.ContentType);
        StringAssert.Contains(page.Text, "Лампа", "ScraperAPI gives every page in UTF-8");
        Assert.AreEqual("text/html; charset=ISO-8859-2", ScraperApiPageLoader.Utf8("text/html; charset=ISO-8859-2"), "a charset given stays");
        Assert.AreEqual("application/json", ScraperApiPageLoader.Utf8("application/json"));
        Assert.AreEqual("image/png", ScraperApiPageLoader.Utf8("image/png"));
        Assert.IsNull(ScraperApiPageLoader.Utf8(null));
    }

    [TestMethod]
    public async Task ACallTimeoutUnder70Seconds_IsWarnedAbout()
    {
        var shortCalls = new Warnings();
        var options = Options(new FakeScraperApi(_ => Answer("")));
        options.CallTimeout = TimeSpan.FromSeconds(30);
        await using var warned = new ScraperApiPageLoader(options, shortCalls);
        var defaults = new Warnings();
        await using var quiet = new ScraperApiPageLoader(Options(new FakeScraperApi(_ => Answer(""))), defaults);

        StringAssert.Contains(shortCalls.Lines.Single(), "bills a call given up before that");
        Assert.IsEmpty(defaults.Lines);
    }

    [TestMethod]
    public async Task Mistakes_Throw()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ScraperApiPageLoader(new ScraperApiPageLoaderOptions()));
        var noLevels = new ScraperApiPageLoaderOptions { ApiKey = "k" };
        noLevels.Levels.Clear();
        Assert.ThrowsExactly<ArgumentException>(() => new ScraperApiPageLoader(noLevels));

        await using var loader = new ScraperApiPageLoader(Options(new FakeScraperApi(_ => Answer(""))));
        await Assert.ThrowsExactlyAsync<NonRetryableException>(() => loader.LoadAsync(new ScrapeRequest(Page) { Method = HttpMethod.Delete }, CancellationToken.None));
    }

    /// <summary>Keeps the warnings of the loader.</summary>
    private sealed class Warnings : ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Lines.Enqueue(formatter(state, exception));
        }
    }
}
