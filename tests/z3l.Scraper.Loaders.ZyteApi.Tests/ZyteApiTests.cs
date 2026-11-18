using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace z3l.Scraper.Loaders.ZyteApi.Tests;

/// <summary>ZyteApiPageLoader against a fake Zyte API that answers as its documentation says.</summary>
[TestClass]
public sealed class ZyteApiTests
{
    private const string Page = "https://shop.test/p/1";

    /// <summary>A call as Zyte API got it.</summary>
    private sealed record Call(HttpMethod Method, Uri Uri, string? Authorization, string? ContentType, JsonObject Json)
    {
        public string Url => (string)Json["url"]!;

        public bool Browser => Json["browserHtml"]?.GetValue<bool>() == true;

        /// <summary>The method that goes to the site.</summary>
        public string SiteMethod => (string?)Json["httpRequestMethod"] ?? "GET";
    }

    /// <summary>The fake service: keeps every call and answers with <c>answer</c>.</summary>
    private sealed class FakeZyte(Func<Call, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public ConcurrentQueue<Call> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            var call = new Call(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), request.Content.Headers.ContentType?.MediaType, json);
            Calls.Enqueue(call);
            return answer(call);
        }
    }

    /// <summary>
    /// A page as Zyte API gives it: 200 with the site's status, the final URL and the site's headers, and the body in Base64 or the
    /// browser's HTML, whichever the call asked for.
    /// </summary>
    private static HttpResponseMessage Answer(Call call, string html, int status = 200, string? url = null, byte[]? body = null,
        string contentType = "text/html; charset=utf-8")
    {
        var json = new JsonObject { ["url"] = url ?? call.Url, ["statusCode"] = status };
        if (call.Browser) json["browserHtml"] = html;
        else json["httpResponseBody"] = Convert.ToBase64String(body ?? Encoding.UTF8.GetBytes(html));
        if (call.Json["httpResponseHeaders"]?.GetValue<bool>() == true)
        {
            json["httpResponseHeaders"] = new JsonArray(
                Header("Content-Type", contentType),
                Header("Set-Cookie", "session=abc; Path=/"),
                Header("Set-Cookie", "theme=dark; Path=/"),
                Header("Content-Encoding", "gzip"),
                Header("Content-Length", "42"));
        }
        return Json(HttpStatusCode.OK, json, "application/json");
    }

    private static JsonObject Header(string name, string value) => new() { ["name"] = name, ["value"] = value };

    /// <summary>An error of Zyte API, as its reference describes it: application/problem+json with type, title, status and detail.</summary>
    private static HttpResponseMessage Problem(int status, string type, string detail = "What went wrong.") =>
        Json((HttpStatusCode)status, new JsonObject { ["type"] = type, ["title"] = "A problem", ["status"] = status, ["detail"] = detail },
            "application/problem+json");

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode json, string mediaType) =>
        new(status) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, mediaType) };

    private static ZyteApiPageLoaderOptions Options(FakeZyte zyte) => new() { ApiKey = "test-key", HttpClient = new HttpClient(zyte) };

    private static ScraperOptions FastScraper() => new() { RetryDelay = TimeSpan.Zero, ProgressInterval = null, MaxRetries = 2 };

    private static string Html(string body) => $"<html><body>{body}</body></html>";

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task APage_IsAsTheSiteGaveIt()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var windows1251 = Encoding.GetEncoding(1251);
        var html = Html("<h1>Лампа</h1>");
        var zyte = new FakeZyte(call => Answer(call, html, url: "https://shop.test/p/1?moved", body: windows1251.GetBytes(html),
            contentType: "text/html; charset=windows-1251"));
        await using var loader = new ZyteApiPageLoader(Options(zyte));

        var page = await loader.LoadAsync(new ScrapeRequest(Page), CancellationToken.None);

        var call = zyte.Calls.Single();
        Assert.AreEqual(HttpMethod.Post, call.Method);
        Assert.AreEqual("https://api.zyte.com/v1/extract", call.Uri.AbsoluteUri);
        Assert.AreEqual("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("test-key:")), call.Authorization,
            "the key is the user name, with no password");
        Assert.AreEqual("application/json", call.ContentType);
        CollectionAssert.AreEqual(new[] { "url", "httpResponseBody", "httpResponseHeaders" }, call.Json.Select(p => p.Key).ToArray(),
            "an HTTP request first, the cheaper tiers; GET is the default method; the headers tell the charset");
        Assert.AreEqual(Page, call.Url);
        Assert.IsTrue(call.Json["httpResponseBody"]!.GetValue<bool>());

        Assert.AreEqual(200, page.StatusCode);
        Assert.AreEqual("https://shop.test/p/1?moved", page.Url, "the URL after the redirects");
        CollectionAssert.AreEqual(windows1251.GetBytes(html), page.Body, "the bytes as the site sent them");
        StringAssert.Contains(page.Text, "Лампа", "decoded with the site's charset");
        Assert.AreEqual("text/html; charset=windows-1251", page.ContentType);
        Assert.AreEqual("session=abc; Path=/, theme=dark; Path=/", page.Headers["Set-Cookie"]);
        Assert.IsFalse(page.Headers.ContainsKey("Content-Encoding"), "Zyte API decompresses the body");
        Assert.IsFalse(page.Headers.ContainsKey("Content-Length"));
        Assert.AreEqual(0, loader.Credits, "Zyte API does not tell the price");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task APostItsHeadersAndCookies_GoToTheSite()
    {
        var zyte = new FakeZyte(call => Answer(call, Html("ok")));
        var options = Options(zyte);
        options.Geolocation = " de ";
        await using var loader = new ZyteApiPageLoader(options);
        var request = new ScrapeRequest("https://shop.test/search")
        {
            Method = HttpMethod.Post,
            Body = "q=lamp",
            Headers = new Dictionary<string, string> { ["X-Token"] = "t1", ["Cookie"] = "a=1; b=2", ["Referer"] = "https://shop.test/" },
        };

        await loader.LoadAsync(request, CancellationToken.None);

        var json = zyte.Calls.Single().Json;
        Assert.AreEqual("POST", (string?)json["httpRequestMethod"]);
        Assert.AreEqual("q=lamp", (string?)json["httpRequestText"], "UTF-8 text, not Base64");
        Assert.AreEqual(
            """[{"name":"Content-Type","value":"application/x-www-form-urlencoded"},{"name":"X-Token","value":"t1"},{"name":"Referer","value":"https://shop.test/"}]""",
            json["customHttpRequestHeaders"]!.ToJsonString(), "Zyte API sets no Cookie header");
        Assert.AreEqual("""[{"name":"a","value":"1","domain":"shop.test"},{"name":"b","value":"2","domain":"shop.test"}]""",
            json["requestCookies"]!.ToJsonString());
        Assert.AreEqual("DE", (string?)json["geolocation"]);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ABlockedPage_GoesToTheBrowser_AndItsHostStaysThere()
    {
        var zyte = new FakeZyte(call => Answer(call, Html(call.Browser ? "<h1>Lamp</h1>" : "captcha")));
        await using var loader = new ZyteApiPageLoader(Options(zyte));
        var scraperOptions = FastScraper();
        scraperOptions.Rules.Default.BlockedMarkers.Add("captcha");
        var request = new ScrapeRequest(Page)
        {
            Headers = new Dictionary<string, string> { ["X-Token"] = "t1", ["Referer"] = "https://shop.test/", ["Cookie"] = "a=1" },
        };
        var scraper = new Scraper(loader, scraperOptions).Route("default", ctx => ctx.Emit(ctx.Response.Text));

        var first = await scraper.RunToListAsync(request);
        var second = await scraper.RunToListAsync("https://shop.test/p/2");

        StringAssert.Contains((string)first.Items.Single(), "<h1>Lamp</h1>", "the browser's HTML is the page");
        Assert.HasCount(1, second.Items);
        CollectionAssert.AreEqual(new[] { false, true, true }, zyte.Calls.Select(c => c.Browser).ToArray(), "the host goes on in the browser");
        var browser = zyte.Calls.ElementAt(1).Json;
        Assert.IsFalse(browser.ContainsKey("httpResponseBody"), "a browser request asks for no HTTP body (422)");
        Assert.IsFalse(browser.ContainsKey("customHttpRequestHeaders"), "a browser sends no header but the Referer");
        Assert.AreEqual("https://shop.test/", (string?)browser["requestHeaders"]?["referer"]);
        Assert.AreEqual("""[{"name":"a","value":"1","domain":"shop.test"}]""", browser["requestCookies"]!.ToJsonString());
        Assert.IsTrue(browser["httpResponseHeaders"]!.GetValue<bool>());
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ABan_GoesToTheBrowser_ButFailuresOfZyteApiAndOfTheSiteDoNot()
    {
        var failures = new ConcurrentQueue<HttpResponseMessage>([Problem(500, "/server/timed-out"), Problem(521, "/download/internal-error")]);
        var broken = 0;
        var zyte = new FakeZyte(call => call.Url switch
        {
            "https://banned.test/p" when !call.Browser => Problem(520, "/download/temporary-error"),
            "https://slow.test/p" when failures.TryDequeue(out var failure) => failure,
            "https://broken.test/p" when Interlocked.Increment(ref broken) == 1 => Answer(call, Html("Internal Server Error"), status: 500),
            _ => Answer(call, Html("the page")),
        });
        await using var loader = new ZyteApiPageLoader(Options(zyte));
        var scraper = new Scraper(loader, FastScraper()).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var banned = await scraper.RunToListAsync("https://banned.test/p");
        var slow = await scraper.RunToListAsync("https://slow.test/p");
        var site = await scraper.RunToListAsync("https://broken.test/p");

        Assert.HasCount(1, banned.Items);
        Assert.HasCount(1, slow.Items);
        Assert.HasCount(1, site.Items);
        CollectionAssert.AreEqual(new[] { false, true }, BrowserCalls("https://banned.test/p"), "520: a ban Zyte API could not avoid in time");
        CollectionAssert.AreEqual(new[] { false, false, false }, BrowserCalls("https://slow.test/p"),
            "a browser does not mend Zyte API's timeout (500) or its download error (521)");
        CollectionAssert.AreEqual(new[] { false, false }, BrowserCalls("https://broken.test/p"), "nor the site's own 500");

        bool[] BrowserCalls(string url) => [.. zyte.Calls.Where(c => c.Url == url).Select(c => c.Browser)];
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ARequestABrowserCannotSend_GoesOverHttp_AndLeavesItsHostAlone()
    {
        var posts = 0;
        var zyte = new FakeZyte(call =>
            Answer(call, Html(call.SiteMethod == "POST" && Interlocked.Increment(ref posts) == 1 ? "captcha" : "the page")));
        await using var loader = new ZyteApiPageLoader(Options(zyte));
        var scraperOptions = FastScraper();
        scraperOptions.Rules.Default.BlockedMarkers.Add("captcha");
        var scraper = new Scraper(loader, scraperOptions).Route("default", ctx => ctx.Emit(ctx.Request.Url));

        var search = await scraper.RunToListAsync(new ScrapeRequest("https://shop.test/search") { Method = HttpMethod.Post, Body = "q=lamp" });
        var item = await scraper.RunToListAsync(Page);

        Assert.HasCount(1, search.Items);
        Assert.HasCount(1, item.Items);
        var calls = zyte.Calls.ToArray();
        Assert.HasCount(3, calls);
        Assert.IsTrue(calls.Take(2).All(c => !c.Browser && c.SiteMethod == "POST" && (string?)c.Json["httpRequestText"] == "q=lamp"),
            "a browser sends GET only: at the browser level the POST goes over HTTP");
        Assert.IsFalse(calls[2].Browser, "an HTTP request got through at the browser level: the host stays on HTTP");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task NoBrowser_KeepsARequestOnHttp()
    {
        var zyte = new FakeZyte(call => call.Url.EndsWith("/api/price", StringComparison.Ordinal)
            ? Answer(call, """{"price": 10}""", contentType: "application/json")
            : Answer(call, Html(call.Browser ? "the page" : "captcha")));
        var options = Options(zyte);
        options.NoBrowser = request => request.Label == "api";
        await using var loader = new ZyteApiPageLoader(options);
        var scraperOptions = FastScraper();
        scraperOptions.Rules.Default.BlockedMarkers.Add("captcha");
        var scraper = new Scraper(loader, scraperOptions)
            .Route("default", ctx => ctx.Emit(ctx.Request.Url))
            .Route("api", ctx => ctx.Emit(ctx.Response.Text));

        var item = await scraper.RunToListAsync(Page);
        var api = await scraper.RunToListAsync(new ScrapeRequest("https://shop.test/api/price", "api"));

        Assert.HasCount(1, item.Items);
        Assert.IsTrue(zyte.Calls.ElementAt(1).Browser, "the host went on in the browser");
        Assert.AreEqual("""{"price": 10}""", api.Items.Single(), "JSON in a browser comes wrapped in HTML");
        Assert.IsFalse(zyte.Calls.Last().Browser);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Statuses_MeanWhatZyteApiSays()
    {
        var first = new ConcurrentDictionary<string, bool>();
        var zyte = new FakeZyte(call => call.Url switch
        {
            "https://shop.test/gone" => Answer(call, "Not Found", status: 404),
            "https://shop.test/key" => Problem(401, "/auth/key-not-found", "The API key test-key is not known."),
            "https://shop.test/suspended" => Problem(403, "/auth/account-suspended"),
            "https://shop.test/bad" => Problem(400, "/request/invalid", "geolocation: 'XX' is not a country (key test-key)"),
            "https://shop.test/both" => Problem(422, "/request/unprocessable"),
            "https://unreachable.test/" => Problem(421, "/website/domain-unreachable"),
            "https://forbidden.test/" => Problem(451, "/download/domain-forbidden"),
            "https://shop.test/busy" when first.TryAdd(call.Url, true) => Problem(429, "/limits/over-user-limit"),
            "https://shop.test/overloaded" when first.TryAdd(call.Url, true) => Problem(503, "/limits/over-global-limit"),
            _ => Answer(call, Html("ok")),
        });
        await using var loader = new ZyteApiPageLoader(Options(zyte));
        var scraper = new Scraper(loader, FastScraper()).Route("default", ctx => ctx.Emit(ctx.Request.Url));
        string[] failing =
        [
            "https://shop.test/key", "https://shop.test/suspended", "https://shop.test/bad", "https://shop.test/both",
            "https://unreachable.test/", "https://forbidden.test/",
        ];

        var requests = failing.Concat(["https://shop.test/gone", "https://shop.test/busy", "https://shop.test/overloaded"])
            .Select(url => new ScrapeRequest(url))
            .ToArray();

        var result = await scraper.RunToListAsync(requests);

        CollectionAssert.AreEquivalent(new[] { "https://shop.test/busy", "https://shop.test/overloaded" }, result.Items.ToArray(),
            "the limits of Zyte API (429, 503) are retried");
        Assert.AreEqual(FailureReason.NotFound, FailureOf("https://shop.test/gone").Reason, "the site's 404 comes in a 200 of Zyte API");
        foreach (var url in failing)
        {
            Assert.AreEqual(FailureReason.NonRetryable, FailureOf(url).Reason, url);
            Assert.AreEqual(1, zyte.Calls.Count(c => c.Url == url), $"{url} is not called again");
        }
        Assert.IsFalse(result.Failed.Any(f => f.Message.Contains("test-key", StringComparison.Ordinal)), "the key does not go to the logs");
        StringAssert.Contains(FailureOf("https://shop.test/bad").Message, "400 /request/invalid");
        StringAssert.Contains(FailureOf("https://shop.test/bad").Message, "'XX' is not a country", "Zyte API's detail tells what is wrong");
        StringAssert.Contains(FailureOf("https://shop.test/suspended").Message, "suspended");

        FailedRequest FailureOf(string url) => result.Failed.Single(f => f.Request.Url == url);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AnAnswerWithoutThePage_IsRetried()
    {
        var answers = new ConcurrentQueue<Func<Call, HttpResponseMessage>>(
        [
            call => Json(HttpStatusCode.OK, new JsonObject { ["url"] = call.Url, ["statusCode"] = 200 }, "application/json"),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>a gateway</html>") },
            call => Answer(call, Html("the page")),
        ]);
        var zyte = new FakeZyte(call => answers.TryDequeue(out var next) ? next(call) : Answer(call, Html("again")));
        await using var loader = new ZyteApiPageLoader(Options(zyte));
        var scraper = new Scraper(loader, FastScraper()).Route("default", ctx => ctx.Emit(ctx.Response.Text));

        var result = await scraper.RunToListAsync(Page);

        StringAssert.Contains((string)result.Items.Single(), "the page");
        Assert.HasCount(3, zyte.Calls);
        Assert.IsTrue(zyte.Calls.All(c => !c.Browser), "Zyte API's own trouble is no ban");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AHeadRequest_ComesWithoutABody()
    {
        var zyte = new FakeZyte(call => Json(HttpStatusCode.OK,
            new JsonObject { ["url"] = call.Url, ["statusCode"] = 200, ["httpResponseHeaders"] = new JsonArray(Header("Content-Type", "text/html")) },
            "application/json"));
        await using var loader = new ZyteApiPageLoader(Options(zyte));

        var page = await loader.LoadAsync(new ScrapeRequest(Page) { Method = HttpMethod.Head }, CancellationToken.None);

        Assert.AreEqual("HEAD", zyte.Calls.Single().SiteMethod);
        Assert.AreEqual(200, page.StatusCode);
        Assert.IsEmpty(page.Body);
        Assert.AreEqual("text/html", page.ContentType);
    }

    [TestMethod]
    public void Configure_SetsAnyFieldOfTheCall()
    {
        var options = Options(new FakeZyte(call => Answer(call, "")));
        options.Configure = (_, level, json) =>
        {
            json["ipType"] = level == 1 ? "residential" : "datacenter";
            json["actions"] = new JsonArray(new JsonObject { ["action"] = "scrollBottom" });
            json.Remove("httpResponseHeaders");
        };
        var loader = new ZyteApiPageLoader(options);

        var bytes = loader.CallBody(new ScrapeRequest("https://shop.test/поиск?q=лампа&sort=<new>"), 1, ZyteApiRequestType.Browser);

        StringAssert.Contains(Encoding.UTF8.GetString(bytes), "\"url\":\"https://shop.test/поиск?q=лампа&sort=<new>\"", "a call is no web page: nothing escaped");
        var json = JsonNode.Parse(bytes)!.AsObject();
        Assert.IsTrue(json["browserHtml"]!.GetValue<bool>());
        Assert.AreEqual("residential", (string?)json["ipType"]);
        Assert.AreEqual("scrollBottom", (string?)json["actions"]![0]!["action"]);
        Assert.IsFalse(json.ContainsKey("httpResponseHeaders"));
        Assert.AreEqual("https://api.zyte.com/v1/extract", options.Endpoint.AbsoluteUri);
    }

    [TestMethod]
    public async Task Mistakes_Throw()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ZyteApiPageLoader(new ZyteApiPageLoaderOptions()));
        var noLevels = new ZyteApiPageLoaderOptions { ApiKey = "k" };
        noLevels.Levels.Clear();
        Assert.ThrowsExactly<ArgumentException>(() => new ZyteApiPageLoader(noLevels));

        var zyte = new FakeZyte(call => Answer(call, ""));
        await using var loader = new ZyteApiPageLoader(Options(zyte));
        await Assert.ThrowsExactlyAsync<NonRetryableException>(() =>
            loader.LoadAsync(new ScrapeRequest(Page) { Method = new HttpMethod("PROPFIND") }, CancellationToken.None));
        Assert.IsTrue(zyte.Calls.IsEmpty, "Zyte API sends GET, POST, PUT, DELETE, OPTIONS, TRACE, PATCH and HEAD only");
    }
}
