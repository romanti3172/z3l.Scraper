using System.Collections.Concurrent;
using System.Text;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Tests;

/// <summary>
/// Sessions of <see cref="BlockAwarePageLoader"/>: a client with its cookies, a user agent and a proxy (with a list) that go on while they
/// do well and are thrown away whole after failures, blocks or bad responses: at the first for a session without a good answer, at the
/// second in a row for one with. Fake proxies, a fake clock.
/// </summary>
[TestClass]
public sealed class ProxySessionTests
{
    private const string Site = "shop.test";

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>What a fake proxy does with a load.</summary>
    private enum Proxy { Ok, Refuses, AsksForALogin, Hangs, TooBig, Junk }

    /// <summary>The cookie jar of a session: a number, so a test sees whether two loads shared one.</summary>
    private sealed record Jar(int Number);

    /// <summary>A loader through fake proxies: the proxy's host says what it does; every load and every closed session is recorded.</summary>
    private sealed class FakeSessionLoader : BlockAwarePageLoader
    {
        private int _jars;

        public FakeSessionLoader(ProxyList? proxies, UserAgentList? userAgents = null, ManualClock? clock = null)
            : base(proxies, userAgents ?? Agents(clock ?? (TimeProvider?)proxies?.Clock ?? TimeProvider.System), "UA")
        {
        }

        private static UserAgentList Agents(TimeProvider clock) => new(Enumerable.Range(1, 50).Select(i => $"UA-{i}")) { Clock = clock };

        public UserAgentList AgentList => UserAgents!;

        public void Tolerate(int trustedAfter, int maxInARow)
        {
            SessionTrustedAfter = trustedAfter;
            SessionMaxFailuresInARow = maxInARow;
        }

        public Dictionary<string, Proxy> Behaviour { get; } = [];

        /// <summary>When set, every load waits for it.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public ConcurrentQueue<(string Proxy, string? Agent, int Jar)> Loads { get; } = new();
        public ConcurrentQueue<LoaderSession> Closed { get; } = new();
        public ConcurrentDictionary<string, ProxyList.Entry> Entries { get; } = new();

        public (string Proxy, string? Agent, int Jar) Last => Loads.Last();

        protected override async Task<PageResponse> LoadThroughAsync(ScrapeRequest request, Uri uri, LoaderSession session, string? userAgent,
            CancellationToken cancellationToken)
        {
            session.State ??= new Jar(Interlocked.Increment(ref _jars));
            var host = session.Proxy?.Address.Host ?? "direct";
            if (session.Proxy != null) Entries[host] = session.Proxy;
            Loads.Enqueue((host, userAgent, ((Jar)session.State).Number));
            if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            if (request.Url.EndsWith("/missing", StringComparison.Ordinal)) return Page(404, "no such page");
            if (Behaviour.GetValueOrDefault(host) == Proxy.Hangs) await Task.Delay(Timeout.Infinite, cancellationToken);
            return Behaviour.GetValueOrDefault(host) switch
            {
                Proxy.Refuses => throw new HttpRequestException("Connection refused"),
                Proxy.AsksForALogin => Page(407, "Proxy Authentication Required"),
                Proxy.TooBig => throw new NonRetryableException("Response is more than MaxResponseBytes"),
                Proxy.Junk => Page(200, "Welcome to our free proxy"),
                _ => Page(200, "the shop"),
            };

            PageResponse Page(int status, string text) =>
                new(status, request.Url, Encoding.UTF8.GetBytes($"<html><body>{text}</body></html>"), "text/html");
        }

        protected override void CloseSession(LoaderSession session) => Closed.Enqueue(session);

        public void Close() => CloseSessions();
    }

    private static ProxyList List(ManualClock clock, params string[] hosts) =>
        new(hosts.Select(h => $"{h}:1"), ProxyBlockPolicy.Block10Minutes) { Clock = clock };

    private static string[] Hosts(int count) => [.. Enumerable.Range(1, count).Select(i => $"p{i}.test")];

    private static ScrapeRequest Page(int i) => new($"http://{Site}/p/{i}");

    /// <summary>A load the caller found good: the session goes on.</summary>
    private static async Task<PageResponse> GoodAsync(FakeSessionLoader loader, int i)
    {
        var request = Page(i);
        var response = await loader.LoadAsync(request, CancellationToken.None);
        loader.ReportGoodResponse(request, response);
        return response;
    }

    private static ProxyStatus StatusOf(ProxyList proxies, string host) => proxies.Status().Single(s => s.Address.Host == host);

    // ------------------------------------------------------------------ a session goes on while it does well

    [TestMethod]
    public async Task ASessionThatDidWell_TakesTheNextLoad_WithItsProxyUserAgentAndCookies()
    {
        var proxies = List(new ManualClock(), Hosts(100));
        var loader = new FakeSessionLoader(proxies);

        for (var i = 0; i < 10; i++) await GoodAsync(loader, i);

        Assert.HasCount(1, loader.Loads.Distinct(), "one proxy, one user agent, one cookie jar");
        Assert.AreEqual(("p1.test", "UA-1", 1), loader.Loads.First());
        Assert.AreEqual(10, StatusOf(proxies, "p1.test").Loads);
        Assert.AreEqual(1, StatusOf(proxies, "p1.test").Sessions);
        Assert.AreEqual(0, StatusOf(proxies, "p2.test").Loads, "no new proxy while the session does well");
        Assert.IsEmpty(loader.Closed);
        Assert.AreEqual((0, 0, 1), loader.Sessions);
    }

    [TestMethod]
    public async Task AFreeSessionGoesBeforeANewProxy_AsManySessionsAsLoadsAtOnce()
    {
        var proxies = List(new ManualClock(), Hosts(10));
        var loader = new FakeSessionLoader(proxies);

        for (var round = 0; round < 3; round++)
        {
            loader.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var loads = Enumerable.Range(0, 3).Select(i => GoodAsync(loader, round * 3 + i)).ToArray();
            while (loader.Loads.Count < (round + 1) * 3) await Task.Delay(5);
            Assert.AreEqual((3, 0, 0), loader.Sessions, "one session serves one load at a time");
            loader.Gate.SetResult();
            await Task.WhenAll(loads);
        }

        Assert.HasCount(3, loader.Loads.Select(l => l.Proxy).Distinct(), "three loads at once: three sessions on three proxies");
        Assert.HasCount(3, loader.Loads.Select(l => l.Agent).Distinct());
        Assert.HasCount(3, loader.Loads.Select(l => l.Jar).Distinct());
        CollectionAssert.AreEquivalent(new[] { "p1.test", "p2.test", "p3.test" }, loader.Loads.Select(l => l.Proxy).Distinct().ToArray());
        Assert.AreEqual((0, 0, 3), loader.Sessions);
        Assert.AreEqual(3, proxies.Status().Sum(s => s.Sessions));
    }

    [TestMethod]
    public async Task OneProxyForEveryLoadAtOnce_SessionsShareIt()
    {
        var proxies = List(new ManualClock(), "gateway.test");      // a rotating gateway: one address, any number of sessions
        var loader = new FakeSessionLoader(proxies) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };

        var loads = Enumerable.Range(0, 3).Select(i => GoodAsync(loader, i)).ToArray();
        while (loader.Loads.Count < 3) await Task.Delay(5);
        Assert.AreEqual(3, StatusOf(proxies, "gateway.test").Sessions);
        loader.Gate.SetResult();
        await Task.WhenAll(loads);

        Assert.HasCount(3, loader.Loads.Select(l => l.Jar).Distinct(), "every session has its own cookies");
        Assert.HasCount(3, loader.Loads.Select(l => l.Agent).Distinct());
        StringAssert.Contains(proxies.ToString(), "1 in sessions");
    }

    // ------------------------------------------------------------------ a failure throws it away whole

    [TestMethod]
    public async Task NoAnswer_ThrowsTheSessionAway_TheNextLoadOpensANewOneOnAnotherProxy()
    {
        var proxies = List(new ManualClock(), "a.test", "b.test");
        var loader = new FakeSessionLoader(proxies) { Behaviour = { ["a.test"] = Proxy.Refuses } };

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => loader.LoadAsync(Page(1), CancellationToken.None));
        await GoodAsync(loader, 2);
        await GoodAsync(loader, 3);

        Assert.AreEqual("a.test", loader.Closed.Single().Proxy!.Address.Host);
        CollectionAssert.AreEqual(new[] { ("a.test", "UA-1", 1), ("b.test", "UA-2", 2), ("b.test", "UA-2", 2) }, loader.Loads.ToArray());
        Assert.AreEqual(1, StatusOf(proxies, "a.test").Failures);
        Assert.AreEqual(0, StatusOf(proxies, "a.test").Sessions);
    }

    [TestMethod]
    public async Task TwoBlocksInARow_ThrowTheSessionAway_AndTheProxyComesBackFromItsRestWithANewOne()
    {
        var clock = new ManualClock();
        var proxies = List(clock, "a.test");
        var loader = new FakeSessionLoader(proxies);
        await GoodAsync(loader, 1);
        await GoodAsync(loader, 2);

        async Task BlockedAsync(int i)
        {
            var request = Page(i);
            var blocked = await loader.LoadAsync(request, CancellationToken.None);
            loader.ReportBlocked(request, blocked, "status 403");         // the scraper's checks, after the load
            loader.ReportDone(request, blocked);
        }

        await BlockedAsync(3);
        Assert.IsEmpty(loader.Closed, "a session with good answers is forgiven one block");
        Assert.AreEqual(0, StatusOf(proxies, "a.test").BlockedSites, "and its proxy does not rest for it");
        await BlockedAsync(4);

        Assert.HasCount(1, loader.Closed);
        Assert.AreEqual(4, loader.Closed.Single().Loads);
        Assert.AreEqual((0, 0, 0), loader.Sessions);
        Assert.AreEqual(2, StatusOf(proxies, "a.test").Blocks);
        Assert.AreEqual(1, StatusOf(proxies, "a.test").BlockedSites);

        clock.Now = clock.Now.AddMinutes(11);                             // the rest is over
        await GoodAsync(loader, 5);

        Assert.AreEqual(("a.test", "UA-2", 2), loader.Last, "the same proxy, but another user agent and an empty cookie jar");
    }

    [TestMethod]
    public async Task ABadResponse_ThrowsTheSessionAway()
    {
        var proxies = List(new ManualClock(), "a.test", "b.test");
        var loader = new FakeSessionLoader(proxies) { Behaviour = { ["a.test"] = Proxy.Junk } };

        var request = Page(1);
        var junk = await loader.LoadAsync(request, CancellationToken.None);
        loader.ReportBadResponse(request, junk, "no marker");
        await GoodAsync(loader, 2);
        await GoodAsync(loader, 3);

        Assert.AreEqual("a.test", loader.Closed.Single().Proxy!.Address.Host);
        Assert.AreEqual(("b.test", "UA-2", 2), loader.Last);
        Assert.AreEqual(1, StatusOf(proxies, "a.test").BadResponses);
    }

    [TestMethod]
    public async Task ATimeout_ThrowsTheSessionAway_OneFailureOfTheProxy()
    {
        var proxies = List(new ManualClock(), "a.test", "b.test");
        var loader = new FakeSessionLoader(proxies) { Behaviour = { ["a.test"] = Proxy.Hangs } };

        var request = Page(1);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => loader.LoadAsync(request, timeout.Token));
        }
        loader.ReportTimeout(request);                                    // the scraper tells it was a timeout
        await GoodAsync(loader, 2);

        Assert.AreEqual("a.test", loader.Closed.Single().Proxy!.Address.Host);
        Assert.AreEqual(1, StatusOf(proxies, "a.test").Failures);
        Assert.AreEqual(("b.test", "UA-2", 2), loader.Last);
    }

    [TestMethod]
    public async Task A407_ThrowsTheSessionAway_ATooBigPageKeepsIt()
    {
        var proxies = List(new ManualClock(), "a.test", "b.test", "c.test");
        var loader = new FakeSessionLoader(proxies) { Behaviour = { ["a.test"] = Proxy.AsksForALogin, ["b.test"] = Proxy.TooBig } };

        var login = await loader.LoadAsync(Page(1), CancellationToken.None);
        Assert.AreEqual(407, login.StatusCode);
        await Assert.ThrowsExactlyAsync<NonRetryableException>(() => loader.LoadAsync(Page(2), CancellationToken.None));
        await Assert.ThrowsExactlyAsync<NonRetryableException>(() => loader.LoadAsync(Page(3), CancellationToken.None));

        Assert.AreEqual("a.test", loader.Closed.Single().Proxy!.Address.Host);
        Assert.AreEqual(1, StatusOf(proxies, "a.test").Failures);
        CollectionAssert.AreEqual(new[] { ("b.test", "UA-2", 2), ("b.test", "UA-2", 2) }, loader.Loads.Skip(1).ToArray(),
            "the page's fault, not the session's");
        Assert.AreEqual(0, StatusOf(proxies, "b.test").Failures);
    }

    // ------------------------------------------------------------------ the verdict

    [TestMethod]
    public async Task AnAnswerKeepsItsSessionUntilTheVerdict()
    {
        var proxies = List(new ManualClock(), "a.test", "b.test", "c.test");
        var loader = new FakeSessionLoader(proxies);

        var first = Page(1);
        var answer = await loader.LoadAsync(first, CancellationToken.None);
        Assert.AreEqual((0, 1, 0), loader.Sessions);
        var second = Page(2);
        var other = await loader.LoadAsync(second, CancellationToken.None);    // a's answer may still turn out blocked: not a
        loader.ReportDone(first, answer);                                      // a 404, a 429, a failed handler: no block, no bad page
        await GoodAsync(loader, 3);

        CollectionAssert.AreEqual(new[] { "a.test", "b.test", "a.test" }, loader.Loads.Select(l => l.Proxy).ToArray());
        loader.ReportDone(second, other);
        Assert.AreEqual((0, 0, 2), loader.Sessions);
        Assert.IsEmpty(loader.Closed);
    }

    [TestMethod]
    public async Task AnAnswerNobodyReports_FreesItsSessionAfterTheVerdictTimeout()
    {
        var clock = new ManualClock();
        var proxies = List(clock, "a.test", "b.test", "c.test");
        var loader = new FakeSessionLoader(proxies);

        await loader.LoadAsync(Page(1), CancellationToken.None);
        clock.Now = clock.Now.Add(loader.VerdictTimeout).AddSeconds(1);
        await loader.LoadAsync(Page(2), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "a.test", "a.test" }, loader.Loads.Select(l => l.Proxy).ToArray());
    }

    [TestMethod]
    public async Task ABlockOfAnOlderAnswer_ClosesTheSessionWhenItsLoadNowEnds()
    {
        var proxies = List(new ManualClock(), "a.test", "b.test");
        var loader = new FakeSessionLoader(proxies);
        var first = Page(1);
        var old = await loader.LoadAsync(first, CancellationToken.None);
        loader.ReportDone(first, old);

        loader.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = Page(2);
        var loading = loader.LoadAsync(second, CancellationToken.None);       // the same session, busy
        while (loader.Loads.Count < 2) await Task.Delay(5);
        loader.ReportBlocked(first, old, "the handler saw a captcha late");
        Assert.IsEmpty(loader.Closed, "not while it loads");

        loader.Gate.SetResult();
        var answer = await loading;
        loader.ReportDone(first, old);                                        // a stale verdict changes nothing
        loader.ReportGoodResponse(second, answer);
        loader.Gate = null;
        await GoodAsync(loader, 3);

        Assert.AreEqual("a.test", loader.Closed.Single().Proxy!.Address.Host);
        Assert.AreEqual(("b.test", "UA-2", 2), loader.Last);
        Assert.AreEqual(1, proxies.Status().Sum(s => s.Sessions));
    }

    [TestMethod]
    public async Task ASessionWhoseProxyRestsForTheSite_WaitsForOtherSites()
    {
        var proxies = List(new ManualClock(), "a.test", "b.test");
        var loader = new FakeSessionLoader(proxies);
        await GoodAsync(loader, 1);
        proxies.ReportBlocked(loader.Entries["a.test"], "other.test");

        var request = new ScrapeRequest("http://other.test/1");
        var response = await loader.LoadAsync(request, CancellationToken.None);
        loader.ReportGoodResponse(request, response);
        await GoodAsync(loader, 2);

        CollectionAssert.AreEqual(new[] { ("a.test", "UA-1", 1), ("b.test", "UA-2", 2), ("b.test", "UA-2", 2) }, loader.Loads.ToArray());
        Assert.IsEmpty(loader.Closed, "a's session stays for other sites");
        Assert.AreEqual((0, 0, 2), loader.Sessions);
    }

    [TestMethod]
    public async Task ClosingTheLoader_ClosesEverySession()
    {
        var proxies = List(new ManualClock(), "a.test", "b.test");
        var loader = new FakeSessionLoader(proxies);
        await loader.LoadAsync(Page(1), CancellationToken.None);              // waits for its verdict on a
        await GoodAsync(loader, 2);                                           // free on b
        loader.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loading = GoodAsync(loader, 3);                                   // b loads again
        while (loader.Loads.Count < 3) await Task.Delay(5);

        loader.Close();
        loader.Gate.SetResult();
        await loading;

        Assert.HasCount(2, loader.Closed, "each session once, the loading one too");
        Assert.AreEqual((0, 0, 0), loader.Sessions);
        Assert.AreEqual(0, proxies.Status().Sum(s => s.Sessions));
    }

    // ------------------------------------------------------------------ with the scraper

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Scraper_ThrowsAwayASessionThatGotItsFirstPageWrong_AndForgivesOneBlockToAGoodOne()
    {
        var proxies = List(new ManualClock(), "junk.test", "good.test", "spare.test");
        var loader = new FakeSessionLoader(proxies) { Behaviour = { ["junk.test"] = Proxy.Junk } };
        var options = ScraperTests.FastOptions();
        options.MaxConcurrency = 1;
        options.NotFoundRetries = 0;
        options.Rules.Default.MustContain.Add("the shop");
        var scraper = new Scraper(loader, options).Route("default", ctx =>
        {
            if (ctx.Request.Url.EndsWith("/5", StringComparison.Ordinal) && ctx.Request.Attempts == 1)
            {
                throw new RetryRequestException("captcha", blocked: true);      // a block only the handler sees
            }
            ctx.Emit(ctx.Request.Url);
        });

        ScrapeRequest[] requests = [Page(1), new($"http://{Site}/missing"), .. Enumerable.Range(2, 9).Select(Page)];
        var result = await scraper.RunToListAsync(requests);

        // junk: a bad first page, thrown away; good: its 404 is the site's answer, and the handler's block on /5 is forgiven
        Assert.HasCount(10, result.Items);
        Assert.HasCount(1, result.Failed);
        Assert.AreEqual("junk.test", loader.Closed.Single().Proxy!.Address.Host);
        Assert.HasCount(1, loader.Loads.Where(l => l.Proxy == "good.test").Distinct(), "one session for good");
        Assert.AreEqual(12, loader.Loads.Count(l => l.Proxy == "good.test"), "/1 again, /missing, /2 to /10, /5 again");
        Assert.AreEqual(0, StatusOf(proxies, "spare.test").Loads);
        Assert.AreEqual((0, 0, 1), loader.Sessions, "the scraper gave every answer its verdict");
    }

    // ------------------------------------------------------------------ how many failures a session survives

    private static async Task FailAsync(FakeSessionLoader loader, int i)
    {
        loader.Behaviour["a.test"] = Proxy.Refuses;
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => loader.LoadAsync(Page(i), CancellationToken.None));
        loader.Behaviour["a.test"] = Proxy.Ok;
    }

    [TestMethod]
    public async Task FirstLoadFails_TheSessionIsThrownAway()
    {
        var loader = new FakeSessionLoader(List(new ManualClock(), "a.test", "b.test"));

        await FailAsync(loader, 1);

        Assert.HasCount(1, loader.Closed);
    }

    [TestMethod]
    public async Task OneGoodAnswerAndAFailure_TheSessionLives_ASecondFailureInARowThrowsItAway()
    {
        var proxies = List(new ManualClock(), "a.test", "b.test");
        var loader = new FakeSessionLoader(proxies);
        await GoodAsync(loader, 1);

        await FailAsync(loader, 2);
        Assert.IsEmpty(loader.Closed);
        Assert.AreEqual(1, StatusOf(proxies, "a.test").Failures, "counted");
        await GoodAsync(loader, 3);
        Assert.AreEqual(("a.test", "UA-1", 1), loader.Last, "the same session goes on");

        await FailAsync(loader, 4);
        Assert.IsEmpty(loader.Closed, "a good answer between starts the count again");
        await FailAsync(loader, 5);
        Assert.HasCount(1, loader.Closed, "the second failure in a row");
        Assert.AreEqual(3, StatusOf(proxies, "a.test").Failures);
    }

    [TestMethod]
    public async Task TheToleranceIsSettable()
    {
        var loader = new FakeSessionLoader(List(new ManualClock(), "a.test", "b.test"));
        loader.Tolerate(trustedAfter: 3, maxInARow: 3);
        await GoodAsync(loader, 1);
        await GoodAsync(loader, 2);
        await FailAsync(loader, 3);
        Assert.HasCount(1, loader.Closed, "two good answers are not enough here");

        await GoodAsync(loader, 4);
        await GoodAsync(loader, 5);
        await GoodAsync(loader, 6);
        loader.Behaviour["b.test"] = Proxy.Refuses;
        for (var i = 7; i <= 8; i++) await Assert.ThrowsExactlyAsync<HttpRequestException>(() => loader.LoadAsync(Page(i), CancellationToken.None));
        Assert.HasCount(1, loader.Closed, "two in a row are forgiven");
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => loader.LoadAsync(Page(9), CancellationToken.None));
        Assert.HasCount(2, loader.Closed, "the third is not");
    }

    // ------------------------------------------------------------------ without proxies

    [TestMethod]
    public async Task WithoutProxies_ASessionKeepsItsUserAgentAndCookies_ANewSessionHasNewOnes()
    {
        var clock = new ManualClock();
        var loader = new FakeSessionLoader(null, clock: clock);

        for (var i = 1; i <= 3; i++) await GoodAsync(loader, i);
        Assert.HasCount(1, loader.Loads.Distinct(), "the user agent never changes while the session lives");
        Assert.AreEqual(("direct", "UA-1", 1), loader.Last);

        for (var i = 4; i <= 5; i++)
        {
            var request = Page(i);
            var response = await loader.LoadAsync(request, CancellationToken.None);
            loader.ReportBlocked(request, response, "captcha");
        }
        await GoodAsync(loader, 6);

        Assert.HasCount(1, loader.Closed);
        Assert.AreEqual(("direct", "UA-2", 2), loader.Last, "a new session: another user agent, an empty cookie jar");
        Assert.AreEqual(1, loader.AgentList.Status().Single(a => a.UserAgent == "UA-1").BlockedSites);
    }

    // ------------------------------------------------------------------ user agents rest as proxies do

    [TestMethod]
    public async Task TheUserAgentOfASessionThrownAwayForABlock_RestsForThatSite()
    {
        var clock = new ManualClock();
        var agents = new UserAgentList(["UA-1", "UA-2"]) { Clock = clock, BlockPolicy = ProxyBlockPolicy.Block10Minutes };
        var loader = new FakeSessionLoader(List(clock, Hosts(10)), agents);

        async Task<string?> BlockedFirstLoadAsync(string site)
        {
            var request = new ScrapeRequest($"http://{site}/1");
            var response = await loader.LoadAsync(request, CancellationToken.None);
            loader.ReportBlocked(request, response, "403");
            return loader.Last.Agent;
        }

        Assert.AreEqual("UA-1", await BlockedFirstLoadAsync(Site));
        clock.Now = clock.Now.AddMinutes(1);
        Assert.AreEqual("UA-2", await BlockedFirstLoadAsync(Site), "UA-1 rests for the site");
        Assert.AreEqual("UA-1", await BlockedFirstLoadAsync(Site), "both rest: the one that comes back first");
        Assert.AreEqual("UA-2", await BlockedFirstLoadAsync("other.test"), "UA-2 rests for shop.test only");

        var status = agents.Status().Single(a => a.UserAgent == "UA-1");
        Assert.AreEqual(2, status.Blocks);
        Assert.AreEqual(1, status.BlockedSites);
        Assert.AreEqual(0, status.Sessions);
        Assert.AreEqual(2, agents.Status().Single(a => a.UserAgent == "UA-2").BlockedSites);
        StringAssert.Contains(agents.ToString(), "2 user agents");

        clock.Now = clock.Now.AddMinutes(11);
        Assert.AreEqual(0, agents.Status().Sum(a => a.BlockedSites), "the rest is over");
    }

    [TestMethod]
    public async Task AUserAgentWithABadResponse_GoesAfterTheCleanOnesForThatSite()
    {
        var clock = new ManualClock();
        var agents = new UserAgentList(["UA-1", "UA-2", "UA-3"]) { Clock = clock };
        var loader = new FakeSessionLoader(List(clock, Hosts(10)), agents);

        var request = Page(1);
        var response = await loader.LoadAsync(request, CancellationToken.None);
        loader.ReportBadResponse(request, response, "no marker");             // UA-1: one bad response, not a rest

        loader.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = Enumerable.Range(2, 3).Select(i => GoodAsync(loader, i)).ToArray();
        while (loader.Loads.Count < 4) await Task.Delay(5);
        loader.Gate.SetResult();
        await Task.WhenAll(loads);

        CollectionAssert.AreEqual(new[] { "UA-1", "UA-2", "UA-3", "UA-2" }, loader.Loads.Select(l => l.Agent).ToArray(),
            "a clean user agent goes first, even one another session has");
        Assert.AreEqual(0, agents.Status().Single(a => a.UserAgent == "UA-1").BlockedSites);
        Assert.AreEqual(1, agents.Status().Single(a => a.UserAgent == "UA-1").BadResponses);
    }
}
