using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using z3l.Scraper.Loading;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace z3l.Scraper.Loaders.ZenRows.Tests;

/// <summary>ZenRowsPageLoader against a fake ZenRows that answers as its documentation and its OpenAPI description say.</summary>
[TestClass]
public sealed class ZenRowsTests
{
    private const string Page = "https://shop.test/p/1";

    /// <summary>A call as ZenRows got it.</summary>
    private sealed record Call(HttpMethod Method, Dictionary<string, string> Query, Dictionary<string, string> Headers, string? Body, string? ContentType)
    {
        public bool Has(string parameter) => Query.ContainsKey(parameter);
    }

    /// <summary>The fake service: keeps every call and answers with <c>answer</c>.</summary>
    private sealed class FakeZenRows(Func<Call, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public ConcurrentQueue<Call> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var call = new Call(
                request.Method,
                query.AllKeys.ToDictionary(k => k!, k => query[k]!),
                request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase),
                request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Content?.Headers.ContentType?.ToString());
            Calls.Enqueue(call);
            return answer(call);
        }
    }

    /// <summary>A page as ZenRows gives it: the site's headers with the Zr- prefix, its own headers, the credits it charged.</summary>
    private static HttpResponseMessage Answer(string html, HttpStatusCode status = HttpStatusCode.OK, int credits = 1, string? finalUrl = null)
    {
        var answer = new HttpResponseMessage(status) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
        answer.Headers.TryAddWithoutValidation("X-Request-Credits", credits.ToString(CultureInfo.InvariantCulture));
        answer.Headers.TryAddWithoutValidation("X-Request-Cost", (credits * 0.0004).ToString(CultureInfo.InvariantCulture));
        answer.Headers.TryAddWithoutValidation("X-Request-Id", "67fa4e35647515d8ad61bb3ee041e1bb");
        answer.Headers.TryAddWithoutValidation("Concurrency-Limit", "20");
        answer.Headers.TryAddWithoutValidation("Concurrency-Remaining", "17");
        answer.Headers.TryAddWithoutValidation("Zr-Content-Type", "text/html; charset=utf-8");
        answer.Headers.TryAddWithoutValidation("Zr-Content-Encoding", "gzip");
        answer.Headers.TryAddWithoutValidation("Zr-Set-Cookie", "session=abc; Path=/");
        answer.Headers.TryAddWithoutValidation("Zr-Cookies", "session=abc");
        if (finalUrl != null) answer.Headers.TryAddWithoutValidation("Zr-Final-Url", finalUrl);
        return answer;
    }

    /// <summary>An error of ZenRows itself as its OpenAPI description gives it: a problem document (RFC 9457), not billed.</summary>
    private static HttpResponseMessage Problem(int status, string code, string detail = "…")
    {
        var json = JsonSerializer.Serialize(new
        {
            code,
            status,
            title = $"An error ({code})",
            detail,
            type = $"https://docs.zenrows.com/api-error-codes#{code}",
            instance = "/v1",
        });
        var answer = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/problem+json") };
        answer.Headers.TryAddWithoutValidation("X-Request-Credits", "0");
        answer.Headers.TryAddWithoutValidation("X-Request-Cost", "0");
        answer.Headers.TryAddWithoutValidation("X-Request-Id", "67fa4e35647515d8ad61bb3ee041e1bc");
        return answer;
    }

    private static ZenRowsPageLoaderOptions Options(FakeZenRows zenRows) => new() { ApiKey = "test-key", HttpClient = new HttpClient(zenRows) };

    private static ScraperOptions FastScraper() => new() { RetryDelay = TimeSpan.Zero, ProgressInterval = null, MaxRetries = 2 };

    private static string Html(string body) => $"<html><body>{body}</body></html>";

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task APage_IsAsTheSiteGaveIt()
    {
        var zenRows = new FakeZenRows(_ => Answer(Html("<h1>Lamp</h1>"), finalUrl: "https://shop.test/p/1?moved"));
        await using var loader = new ZenRowsPageLoader(Options(zenRows));

        var page = await loader.LoadAsync(new ScrapeRequest(Page), CancellationToken.None);

        var call = zenRows.Calls.Single();
        Assert.AreEqual(HttpMethod.Get, call.Method);
        Assert.AreEqual("test-key", call.Query["apikey"], "the Fetch API takes the key in the query only");
        Assert.AreEqual(Page, call.Query["url"]);
        Assert.AreEqual("true", call.Query["original_status"], "the site's status, not ZenRows' own");
        Assert.IsFalse(call.Has("js_render"), "no browser by default: a fifth of the price");
        Assert.IsFalse(call.Has("premium_proxy"));
        Assert.IsFalse(call.Has("custom_headers"), "no headers to send");

        Assert.AreEqual(200, page.StatusCode);
        Assert.AreEqual("https://shop.test/p/1?moved", page.Url);
        StringAssert.Contains(page.Text, "Lamp");
        Assert.AreEqual("session=abc; Path=/", page.Headers["Set-Cookie"], "the site's headers lose their Zr- prefix");
        Assert.AreEqual("text/html; charset=utf-8", page.ContentType);
        Assert.IsFalse(page.Headers.ContainsKey("Content-Encoding"), "ZenRows gives the body decoded");
        Assert.AreEqual("67fa4e35647515d8ad61bb3ee041e1bb", page.Headers["X-Request-Id"], "ZenRows' own headers stay");
        Assert.AreEqual("https://shop.test/p/1?moved", page.Headers["Zr-Final-Url"]);
        Assert.AreEqual("session=abc", page.Headers["Zr-Cookies"]);
        Assert.AreEqual(1, loader.Credits, "X-Request-Credits, not X-Request-Cost in dollars");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task APostAndItsHeaders_GoToTheSite()
    {
        var zenRows = new FakeZenRows(_ => Answer(Html("ok")));
        var options = Options(zenRows);
        options.RenderJs = true;
        options.WaitFor = "div.results";
        await using var loader = new ZenRowsPageLoader(options);
        var request = new ScrapeRequest("https://shop.test/search")
        {
            Method = HttpMethod.Post,
            Body = "{\"q\":\"lamp\"}",
            Headers = new Dictionary<string, string>
            {
                ["X-Token"] = "t1", ["Cookie"] = "a=1; b=2", ["Content-Type"] = "application/json", ["Accept-Encoding"] = "br",
            },
        };

        await loader.LoadAsync(request, CancellationToken.None);

        var call = zenRows.Calls.Single();
        Assert.AreEqual(HttpMethod.Post, call.Method);
        Assert.AreEqual("{\"q\":\"lamp\"}", call.Body);
        Assert.AreEqual("application/json", call.ContentType, "a Content-Type header goes with the body");
        Assert.AreEqual("true", call.Query["custom_headers"]);
        Assert.AreEqual("t1", call.Headers["X-Token"], "the request's headers go as they are");
        Assert.AreEqual("a=1; b=2", call.Headers["Cookie"], "its cookies with them");
        Assert.IsFalse(call.Headers.ContainsKey("Accept-Encoding"), "that is the encoding of ZenRows' answer, not the site's");
        Assert.IsFalse(call.Has("js_render"), "ZenRows renders GET pages only");
        Assert.IsFalse(call.Has("wait_for"));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    [DataRow(403)]
    [DataRow(429)]
    [DataRow(503)]
    [DataRow(422)]
    [DataRow(200)]
    public async Task APageTheSiteRefused_GoesToPremiumProxies(int status)
    {
        // RESP001 with the site's status (original_status), or 422 when ZenRows gives none
        var zenRows = new FakeZenRows(call => call.Has("premium_proxy") ? Answer(Html("the page"), credits: 10) : Problem(status, "RESP001"));
        await using var loader = new ZenRowsPageLoader(Options(zenRows));
        var scraper = new Scraper(loader, FastScraper()).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var first = await scraper.RunToListAsync(Page);
        var second = await scraper.RunToListAsync("https://shop.test/p/2");

        Assert.HasCount(1, first.Items);
        Assert.HasCount(1, second.Items);
        CollectionAssert.AreEqual(new[] { false, true, true }, zenRows.Calls.Select(c => c.Has("premium_proxy")).ToArray(),
            "RESP001 is not billed; the host goes on with premium proxies");
        Assert.AreEqual(20, loader.Credits);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task TheSitesOwnError_OrATimeout_IsRetriedAtTheSameLevel()
    {
        var attempts = 0;
        var zenRows = new FakeZenRows(_ => Interlocked.Increment(ref attempts) switch
        {
            1 => Problem(500, "RESP001"),     // the site's own 500, as original_status gives it
            2 => Problem(504, "CTX0002"),     // ZenRows gave the page up after 3 minutes
            3 => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>Bad gateway</html>", Encoding.UTF8, "text/html") },
            _ => Answer(Html("the page")),
        });
        await using var loader = new ZenRowsPageLoader(Options(zenRows));
        var scraper = new Scraper(loader, FastScraper()).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync(new ScrapeRequest(Page) { MaxRetries = 3 });

        Assert.HasCount(1, result.Items);
        Assert.HasCount(4, zenRows.Calls);
        Assert.IsFalse(zenRows.Calls.Any(c => c.Has("premium_proxy")), "premium proxies mend neither a site's own 500 nor a timeout");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ADomainThatNeedsMore_GoesUpTheLevels_AndFailsAtTheTop()
    {
        // ZenRows loads the domain only with JavaScript and premium proxies (REQS002): it refuses the call at once, and does not bill it
        Func<Call, HttpResponseMessage> needsBoth = call =>
            call.Has("js_render") && call.Has("premium_proxy") ? Answer(Html("the page"), credits: 25) : Problem(400, "REQS002");
        var twoLevels = new FakeZenRows(needsBoth);
        await using var withoutBrowser = new ZenRowsPageLoader(Options(twoLevels));
        var threeLevels = new FakeZenRows(needsBoth);
        var options = Options(threeLevels);
        options.Levels.Add(ZenRowsLevel.Protected);
        await using var withBrowser = new ZenRowsPageLoader(options);

        var failed = await new Scraper(withoutBrowser, FastScraper()).Route("default", ctx => ctx.Emit(ctx.Request.Url)).RunToListAsync(Page);
        var loaded = await new Scraper(withBrowser, FastScraper()).Route("default", ctx => ctx.Emit(ctx.Request.Url)).RunToListAsync(Page);

        Assert.AreEqual(FailureReason.NonRetryable, failed.Failed.Single().Reason, "no level has what the domain needs");
        StringAssert.Contains(failed.Failed.Single().Message, "REQS002");
        Assert.HasCount(2, twoLevels.Calls);
        Assert.HasCount(1, loaded.Items);
        Assert.HasCount(3, threeLevels.Calls, "standard, premium, protected");
        Assert.AreEqual(25, withBrowser.Credits);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ABlockedPage_GoesUpToTheProtectedLevel()
    {
        var zenRows = new FakeZenRows(call => call.Has("js_render")
            ? Answer(Html("the page"), credits: 25)
            : Answer(Html("captcha"), credits: call.Has("premium_proxy") ? 10 : 1));
        var options = Options(zenRows);
        options.Levels.Add(ZenRowsLevel.Protected);
        options.CountryCode = "DE";
        await using var loader = new ZenRowsPageLoader(options);
        var scraperOptions = FastScraper();
        scraperOptions.Rules.Default.BlockedMarkers.Add("captcha");
        var request = new ScrapeRequest(Page) { Headers = new Dictionary<string, string> { ["X-Token"] = "t1" } };
        var scraper = new Scraper(loader, scraperOptions).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync(request);

        Assert.HasCount(1, result.Items);
        var calls = zenRows.Calls.ToArray();
        Assert.HasCount(3, calls, "standard, premium, protected");
        Assert.IsFalse(calls[0].Has("proxy_country"), "ZenRows takes a country with premium proxies only");
        Assert.AreEqual("de", calls[1].Query["proxy_country"]);
        Assert.AreEqual("true", calls[2].Query["js_render"], "the protected level renders JavaScript");
        Assert.AreEqual("true", calls[2].Query["premium_proxy"]);
        Assert.IsTrue(calls.All(c => c.Headers["X-Token"] == "t1" && c.Query["custom_headers"] == "true"), "every level takes the request's headers");
        Assert.AreEqual(36, loader.Credits, "a blocked page is billed: ZenRows got a page");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Statuses_MeanWhatZenRowsSays()
    {
        var zenRows = new FakeZenRows(call => call.Query["url"] switch
        {
            "https://shop.test/gone" => Problem(404, "RESP002"),
            "https://shop.test/key" => Problem(401, "AUTH002", "Invalid API key test-key"),
            "https://shop.test/spent" => Problem(402, "AUTH004"),
            "https://shop.test/bad" => Problem(400, "REQS004", "Invalid params: apikey=test-key&js_render=maybe"),
            "https://shop.test/big" => Problem(413, "RESP005"),
            "https://shop.test/busy" when !BusyOnce() => Problem(429, "AUTH006"),
            _ => Answer(Html("ok")),
        });
        await using var loader = new ZenRowsPageLoader(Options(zenRows));
        var scraper = new Scraper(loader, FastScraper()).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var result = await scraper.RunToListAsync("https://shop.test/gone", "https://shop.test/key", "https://shop.test/spent", "https://shop.test/bad",
            "https://shop.test/big", "https://shop.test/busy");

        FailedRequest Failed(string path) => result.Failed.Single(f => f.Request.Url.EndsWith(path, StringComparison.Ordinal));
        CollectionAssert.AreEqual(new[] { "https://shop.test/busy" }, result.Items.ToArray(), "429 is retried");
        Assert.AreEqual(FailureReason.NotFound, Failed("/gone").Reason);
        foreach (var path in new[] { "/key", "/spent", "/bad", "/big" })
        {
            Assert.AreEqual(FailureReason.NonRetryable, Failed(path).Reason, path);
            Assert.DoesNotContain("test-key", Failed(path).Message, "an error may repeat the key; the message does not");
            Assert.AreEqual(1, zenRows.Calls.Count(c => c.Query["url"].EndsWith(path, StringComparison.Ordinal)), $"{path} is not called again");
        }
        StringAssert.Contains(Failed("/key").Message, "AUTH002");
        StringAssert.Contains(Failed("/bad").Message, "js_render=maybe", "a bad call says what is wrong");
    }

    private int _busy;

    private bool BusyOnce() => Interlocked.Increment(ref _busy) > 1;

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task TheSitesPageWithAnErrorStatus_ComesAsItIs()
    {
        // with allowed_status_codes ZenRows gives the site's page for those statuses, and bills it
        var zenRows = new FakeZenRows(_ => Answer(Html("<p>Server error, try later</p>"), HttpStatusCode.InternalServerError));
        var options = Options(zenRows);
        options.Configure = (_, _, parameters) => parameters["allowed_status_codes"] = "500,503";
        await using var loader = new ZenRowsPageLoader(options);

        var page = await loader.LoadAsync(new ScrapeRequest(Page), CancellationToken.None);

        Assert.AreEqual("500,503", zenRows.Calls.Single().Query["allowed_status_codes"]);
        Assert.AreEqual(500, page.StatusCode);
        StringAssert.Contains(page.Text, "try later");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AJsonPageOfTheSite_IsAPage()
    {
        var zenRows = new FakeZenRows(_ =>
        {
            var answer = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":\"ERR0001\",\"state\":\"down\"}", Encoding.UTF8, "application/json") };
            answer.Headers.TryAddWithoutValidation("Zr-Content-Type", "application/json");
            return answer;
        });
        await using var loader = new ZenRowsPageLoader(Options(zenRows));

        var page = await loader.LoadAsync(new ScrapeRequest("https://api.shop.test/status"), CancellationToken.None);

        Assert.AreEqual(200, page.StatusCode, "a 2xx is ZenRows' error only as application/problem+json");
        StringAssert.Contains(page.Text, "down");
    }

    [TestMethod]
    public void TheCall_HasTheKeyAndTheParametersOfTheLevel()
    {
        var seen = new List<string>();
        var options = Options(new FakeZenRows(_ => Answer("")));
        options.RenderJs = true;
        options.WaitFor = "div.price";
        options.CountryCode = " FR ";
        options.Configure = (_, level, parameters) =>
        {
            seen.AddRange(parameters.Keys);
            parameters["session_id"] = $"{100 + level}";
        };
        var loader = new ZenRowsPageLoader(options);

        var uri = loader.CallUri(new ScrapeRequest("https://shop.test/p?id=1&x=a b"), ZenRowsLevel.Premium, 1);

        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.StartsWith("https://api.zenrows.com/v1/?apikey=test-key&", uri.AbsoluteUri);
        Assert.AreEqual("https://shop.test/p?id=1&x=a b", query["url"], "the page's URL is encoded");
        Assert.AreEqual("true", query["js_render"]);
        Assert.AreEqual("div.price", query["wait_for"]);
        Assert.AreEqual("true", query["premium_proxy"]);
        Assert.AreEqual("fr", query["proxy_country"]);
        Assert.AreEqual("101", query["session_id"]);
        Assert.DoesNotContain("apikey", seen, "Configure does not see the key");
    }

    [TestMethod]
    public void TheAutoLevel_LeavesTheBrowserAndTheProxiesToZenRows()
    {
        var options = Options(new FakeZenRows(_ => Answer("")));
        options.Levels.Clear();
        options.Levels.Add(ZenRowsLevel.Auto);
        options.RenderJs = true;
        options.WaitFor = "div.price";
        options.CountryCode = "DE";
        var loader = new ZenRowsPageLoader(options);

        var parameters = loader.ParametersOf(new ScrapeRequest(Page), ZenRowsLevel.Auto, 0);

        Assert.AreEqual("auto", parameters["mode"]);
        Assert.IsFalse(parameters.ContainsKey("js_render"), "ZenRows refuses js_render and premium_proxy with mode=auto");
        Assert.IsFalse(parameters.ContainsKey("premium_proxy"));
        Assert.IsFalse(parameters.ContainsKey("wait_for"));
        Assert.AreEqual("de", parameters["proxy_country"], "a country goes with mode=auto");
    }

    [TestMethod]
    public async Task Defaults_AndMistakes()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(190), new ZenRowsPageLoaderOptions().CallTimeout, "ZenRows works on a call for up to 3 minutes");
        Assert.ThrowsExactly<ArgumentException>(() => new ZenRowsPageLoader(new ZenRowsPageLoaderOptions()));
        var noLevels = new ZenRowsPageLoaderOptions { ApiKey = "k" };
        noLevels.Levels.Clear();
        Assert.ThrowsExactly<ArgumentException>(() => new ZenRowsPageLoader(noLevels));

        await using var loader = new ZenRowsPageLoader(Options(new FakeZenRows(_ => Answer(""))));
        await Assert.ThrowsExactlyAsync<NonRetryableException>(() => loader.LoadAsync(new ScrapeRequest(Page) { Method = HttpMethod.Delete }, CancellationToken.None));
    }
}
