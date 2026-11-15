using System.Collections.Concurrent;
using System.Diagnostics;
using z3l.FastHtmlParser;

namespace z3l.Scraper.Tests;

[TestClass]
public sealed class StressTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// 5000 pages linking to random pages, 32 workers: every 10th page fails on its first load, every 97th does not exist.
    /// Checks that the bookkeeping adds up exactly: nothing lost, nothing done twice, the run ends by itself.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task RandomGraph_EveryRequestIsAccountedFor(int seed)
    {
        const int pageCount = 5000;
        var random = new Random(seed);
        var links = Enumerable.Range(0, pageCount).Select(_ => Enumerable.Range(0, 3).Select(_ => random.Next(1, pageCount)).ToArray()).ToArray();
        var loads = new ConcurrentDictionary<int, int>();

        var loader = new DelegateLoader(async (request, token) =>
        {
            var id = int.Parse(request.Url[(request.Url.LastIndexOf('/') + 1)..]);
            loads.AddOrUpdate(id, 1, (_, n) => n + 1);
            if (id % 7 == 0) await Task.Yield();

            if (id % 97 == 0) return PageResponse.FromText(404, request.Url, "<html>gone</html>");
            if (id % 10 == 0 && request.Attempts == 1) return PageResponse.FromText(500, request.Url, "<html>oops</html>");
            var html = "<html><body>" + string.Concat(links[id].Select(l => $"<a href=\"/n/{l}\">{l}</a>")) + "</body></html>";
            return PageResponse.FromText(200, request.Url, html);
        });

        var options = ScraperTests.FastOptions();
        options.MaxConcurrency = 32;
        var scraper = new Scraper(loader, options).Route("default", ctx =>
        {
            ctx.Enqueue(ctx.Html.XPathValues("//a/@href"));
            ctx.Emit(int.Parse(ctx.Request.Url[(ctx.Request.Url.LastIndexOf('/') + 1)..]));
        });

        var clock = Stopwatch.StartNew();
        var result = await scraper.RunToListAsync(["https://s.test/n/1"], TestContext.CancellationToken);
        clock.Stop();

        // what should have been reached: pages that do not exist are not followed
        var reachable = new HashSet<int> { 1 };
        var queue = new Queue<int>([1]);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (id % 97 == 0) continue;
            foreach (var next in links[id]) if (reachable.Add(next)) queue.Enqueue(next);
        }
        var missing = reachable.Count(id => id % 97 == 0);
        var flaky = reachable.Count(id => id % 10 == 0 && id % 97 != 0);

        var stats = result.Stats;
        TestContext.WriteLine($"{stats} in {clock.ElapsedMilliseconds} ms");
        Assert.AreEqual(reachable.Count, stats.Enqueued, stats.ToString());
        Assert.AreEqual(reachable.Count - missing, stats.Succeeded, stats.ToString());
        Assert.AreEqual(missing, stats.Failed, stats.ToString());
        Assert.AreEqual(missing + flaky, stats.Retries, stats.ToString());
        Assert.AreEqual(reachable.Count + missing + flaky, stats.Loads, stats.ToString());
        Assert.AreEqual(0, stats.Pending);
        Assert.AreEqual(0, stats.InProgress);
        Assert.AreEqual(0, stats.Queued);

        CollectionAssert.AreEquivalent(reachable.Where(id => id % 97 != 0).ToArray(), result.Items.ToArray());   // each page once
        Assert.IsTrue(loads.All(kv => kv.Value == (kv.Key % 97 == 0 || kv.Key % 10 == 0 ? 2 : 1)), "a page was loaded more often than its failures explain");
    }

    /// <summary>A slow consumer must slow the scraper down instead of letting items pile up.</summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task SlowConsumer_HoldsWorkersBack()
    {
        var loader = new DelegateLoader((r, _) => Task.FromResult(PageResponse.FromText(200, r.Url, "<html>x</html>")));
        var options = ScraperTests.FastOptions();
        options.ItemBufferSize = 5;
        options.MaxConcurrency = 4;
        var scraper = new Scraper(loader, options).Route("default", ctx => ctx.Emit(1));
        var urls = Enumerable.Range(0, 200).Select(i => (ScrapeRequest)$"https://s.test/{i}");

        var maxAhead = 0L;
        var taken = 0;
        await foreach (var _ in scraper.RunAsync(urls, TestContext.CancellationToken))
        {
            taken++;
            maxAhead = Math.Max(maxAhead, scraper.Stats.Succeeded - taken);
            if (taken % 20 == 0) await Task.Delay(20, TestContext.CancellationToken);
        }

        Assert.AreEqual(200, taken);
        // buffer + one item per worker waiting to be written + a little race in reading the counters
        Assert.IsLessThanOrEqualTo(options.ItemBufferSize + options.MaxConcurrency + 2, maxAhead);
    }
}
