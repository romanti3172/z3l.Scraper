using System.Collections.Concurrent;
using System.Text;
using z3l.FastHtmlParser;
using static z3l.Scraper.Tests.ScraperTests;

namespace z3l.Scraper.Tests;

/// <summary>A page, its text and its parsed tags must go as soon as the handler is done with them.</summary>
/// <remarks>
/// Not in parallel with other tests: the forced full collections stop every thread of the process, and the stress test loads
/// the CPU, which would push the timing of other tests (request timeouts, throttling) off.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class MemoryTests
{
    /// <summary>A new page on every load, so nothing outside the scraper keeps it.</summary>
    private static PageResponse BigPage(string url) => PageResponse.FromText(200, url,
        Html(string.Concat(Enumerable.Range(0, 3000).Select(i => $"<div class='card'><a href='/p/{i}'>item {i}</a></div>"))), "text/html");

    /// <summary>Weak references to what handlers saw.</summary>
    private sealed class Tracker
    {
        private readonly ConcurrentQueue<(string What, WeakReference Target)> _targets = new();

        public int Count => _targets.Count;

        public void Track(ScrapeContext ctx)
        {
            Add($"{ctx.Request.Url} response", ctx.Response);
            Add($"{ctx.Request.Url} text", ctx.Response.Text);
            Add($"{ctx.Request.Url} parser", ctx.Html);
        }

        public void Add(string what, object target) => _targets.Enqueue((what, new WeakReference(target)));

        /// <summary>What is still alive after full collections. A worker finishes a page a moment after its items go out, so it tries for a while.</summary>
        public async Task<string> AliveAsync()
        {
            for (var attempt = 0; ; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var alive = _targets.Where(t => t.Target.IsAlive).Select(t => t.What).ToList();
                if (alive.Count == 0 || attempt == 100) return string.Join(", ", alive);
                await Task.Delay(20);
            }
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task HandledPagesGoWhileTheRunGoesOn()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new DelegateLoader(async (r, ct) =>
        {
            if (r.Url.EndsWith("/last", StringComparison.Ordinal)) await gate.Task.WaitAsync(ct);
            return BigPage(r.Url);
        });
        var tracker = new Tracker();
        var scraper = new Scraper(loader, FastOptions()).Route("default", ctx =>
        {
            tracker.Track(ctx);
            ctx.Emit(ctx.Html.FindAll(HtmlTag.A).Count());
        });

        var requests = Enumerable.Range(0, 10).Select(i => new ScrapeRequest($"https://s.test/{i}")).Append(new ScrapeRequest("https://s.test/last"));
        var items = 0;
        string? aliveDuringRun = null;
        await foreach (var _ in scraper.RunAsync(requests))
        {
            if (++items == 10)
            {
                aliveDuringRun = await tracker.AliveAsync();     // the last page is still loading: the run is not over
                gate.SetResult();
            }
        }

        Assert.AreEqual(11, items);
        Assert.AreEqual(33, tracker.Count);
        Assert.AreEqual("", aliveDuringRun);
        Assert.AreEqual("", await tracker.AliveAsync());
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task APageGoesBeforeItsItemsReachASlowConsumer()
    {
        var tracker = new Tracker();
        var options = FastOptions();
        options.ItemBufferSize = 1;
        var scraper = new Scraper(new DelegateLoader((r, _) => Task.FromResult(BigPage(r.Url))), options).Route("default", ctx =>
        {
            tracker.Track(ctx);
            foreach (var link in ctx.Html.XPathValues("//a/@href").Take(5)) ctx.Emit(link);
        });

        await using var items = scraper.RunAsync(new ScrapeRequest("https://s.test/1")).GetAsyncEnumerator();
        Assert.IsTrue(await items.MoveNextAsync());
        while (scraper.Stats.Items < 2) await Task.Delay(10);   // the second item fills the buffer: the worker waits with three more
        var alive = await tracker.AliveAsync();

        var count = 1;
        while (await items.MoveNextAsync()) count++;
        Assert.AreEqual(5, count);
        Assert.AreEqual("", alive);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AKeptContextDoesNotKeepThePage_AndKeptTagsStopWorking()
    {
        ScrapeContext? keptContext = null;
        ParserTag? keptTag = null;
        var tracker = new Tracker();
        var scraper = new Scraper(new DelegateLoader((r, _) => Task.FromResult(BigPage(r.Url))), FastOptions()).Route("default", ctx =>
        {
            keptContext = ctx;                       // handlers should not do this, but it happens
            keptTag = ctx.Html.Find(HtmlTag.A);
            tracker.Add("response", ctx.Response);
            ctx.Emit("done");
        });

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/1"));

        Assert.AreEqual("done", result.Items.Single());
        Assert.AreEqual("", await tracker.AliveAsync());
        StringAssert.Contains(Assert.ThrowsExactly<InvalidOperationException>(() => keptContext!.Response).Message, "handler has finished");
        Assert.ThrowsExactly<InvalidOperationException>(() => keptContext!.Html);
        Assert.ThrowsExactly<ObjectDisposedException>(() => keptTag!.Text());     // the parser was disposed with the page
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task PagesAreReadInPlace_WithoutAString_AndTheirTextGoesBackToThePool()
    {
        var html = Html(string.Concat(Enumerable.Range(0, 500).Select(i => $"<div class='card'><a href='/p/{i}'>товар {i}</a></div>")));
        var loader = new DelegateLoader((r, _) => Task.FromResult(new PageResponse(200, r.Url, Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8")));
        var options = FastOptions();
        options.Rules.Default.BlockedMarkers.Add("captcha");
        options.Rules.Default.MustContain.Add("товар 499");
        var scraper = new Scraper(loader, options).Route("default", ctx =>
        {
            Assert.AreEqual(500, ctx.Html.FindAll(HtmlTag.A).Count());
            Assert.AreEqual("товар 7", ctx.Html.XPathValue("//a[@href='/p/7']"));
            ctx.Emit(ctx.Response);
        });

        var response = (PageResponse)(await scraper.RunToListAsync(new ScrapeRequest("https://s.test/1"))).Items.Single();

        Assert.IsFalse(response.HasTextString, "the checks and the parser read the text in place");
        Assert.IsFalse(response.HasPooledText, "the text went back to the pool when the handler ended");
        Assert.AreEqual(html, response.Text);     // a kept response still has its text
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task PooledTextIsNotMixedUpBetweenPages()
    {
        // pages of many sizes, so buffers of every pool bucket are taken and given back while other pages are read
        static string Page(int n) => Html($"<h1>{n}</h1>" + string.Concat(Enumerable.Range(0, n * 37 % 3000).Select(i => $"<p>{n}-{i}</p>")) + $"<h2>{n}</h2>");
        var loader = new DelegateLoader(async (r, _) =>
        {
            await Task.Yield();
            var n = int.Parse(r.Url[(r.Url.LastIndexOf('/') + 1)..]);
            return n % 5 == 0
                ? new PageResponse(200, r.Url, Encoding.UTF8.GetBytes($$"""{"id":{{n}},"text":"{{new string('ж', n * 13 % 5000)}}"}"""), "application/json")
                : new PageResponse(200, r.Url, Encoding.UTF8.GetBytes(Page(n)), "text/html");
        });
        var options = FastOptions();
        options.MaxConcurrency = 16;
        var scraper = new Scraper(loader, options).Route("default", async ctx =>
        {
            var n = int.Parse(ctx.Request.Url[(ctx.Request.Url.LastIndexOf('/') + 1)..]);
            await Task.Yield();
            if (n % 5 == 0)
            {
                var json = ctx.Json<JsonPage>()!;
                ctx.Emit(json.Id == n && json.Text.Length == n * 13 % 5000 && json.Text.All(c => c == 'ж') ? "ok" : $"bad json {n}");
                return;
            }
            var h1 = ctx.Html.XPathValue("//h1");
            await Task.Yield();     // other pages take and return buffers meanwhile
            var h2 = ctx.Html.XPathValue("//h2");
            var paragraphs = ctx.Html.FindAll(HtmlTag.P).Count();
            ctx.Emit(h1 == $"{n}" && h2 == $"{n}" && paragraphs == n * 37 % 3000 ? "ok" : $"bad page {n}: {h1}/{h2}/{paragraphs}");
        });

        var result = await scraper.RunToListAsync(Enumerable.Range(1, 400).Select(n => new ScrapeRequest($"https://s.test/{n}")));

        Assert.IsEmpty(result.Failed);
        Assert.HasCount(400, result.Items);
        Assert.AreEqual("", string.Join("; ", result.ItemsOf<string>().Where(i => i != "ok")));
    }

    private sealed record JsonPage(int Id, string Text);

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ParserObjectsCannotLeaveTheHandler()
    {
        var scraper = new Scraper(new DelegateLoader((r, _) => Task.FromResult(BigPage(r.Url))), FastOptions())
            .Route("default", ctx =>
            {
                switch (ctx.Request.Url[^1])
                {
                    case '1': ctx.Emit(ctx.Html.Find(HtmlTag.A)!); break;
                    case '2': ctx.Emit(ctx.Html.FindAll(HtmlTag.A)); break;
                    case '3': ctx.Emit(ctx.Html); break;
                    case '4': ctx.Emit(ctx); break;
                    case '5': ctx.Enqueue("/next", "item", ctx.Html.Find(HtmlTag.A)!); break;
                }
            })
            .Route<ParserTag>("item", (ctx, tag) => ctx.Emit(tag.Text()!));

        var result = await scraper.RunToListAsync(Enumerable.Range(1, 5).Select(i => new ScrapeRequest($"https://s.test/{i}")));

        Assert.IsEmpty(result.Items);
        Assert.HasCount(5, result.Failed);
        foreach (var failed in result.Failed)
        {
            Assert.AreEqual(FailureReason.HandlerError, failed.Reason, failed.Request.Url);
            StringAssert.Contains(failed.Message, "belong to the page", failed.Request.Url);
        }
    }
}
