using System.Collections.Concurrent;

namespace z3l.Scraper.Tests;

/// <summary>Scraper.RequestsNeeded: requests given while the run goes, as a big table is read a batch at a time.</summary>
[TestClass]
public sealed class RequestsNeededTests
{
    private static string Url(int n) => $"https://s.test/item/{n}";

    /// <summary>Every page answers (after <paramref name="delay"/>); its handler emits the page URL.</summary>
    private static Scraper ItemScraper(ScraperOptions options, TimeSpan delay = default, Action<ScrapeRequest>? onLoad = null)
    {
        var loader = new DelegateLoader(async (r, token) =>
        {
            onLoad?.Invoke(r);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            return PageResponse.FromText(200, r.Url, ScraperTests.Html("ok"));
        });
        return new Scraper(loader, options).Route("default", ctx => ctx.Emit(ctx.Request.Url));
    }

    /// <summary>A table of <c>count</c> rows read <c>batch</c> at a time, like <c>WHERE id &gt; @last ORDER BY id LIMIT @batch</c>.</summary>
    private sealed class Table(int count, int batch)
    {
        private int _next, _active, _maxActive;

        public ConcurrentQueue<(long Pending, int Given)> Calls { get; } = new();
        public int MaxActive => Volatile.Read(ref _maxActive);

        public async Task GiveAsync(RequestsNeededEventArgs e)
        {
            InterlockedMax(ref _maxActive, Interlocked.Increment(ref _active));
            await Task.Delay(1, e.CancellationToken);       // the database answers
            var given = 0;
            for (; given < batch && _next < count; given++) e.Add(Url(_next++));
            Calls.Enqueue((e.Pending, given));
            Interlocked.Decrement(ref _active);
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task GivesEveryRow_InBatches_AndTheRunEndsWhenTheTableIsDone()
    {
        var options = ScraperTests.FastOptions();
        options.RequestsNeededBelow = 5;
        var table = new Table(count: 25, batch: 10);
        var scraper = ItemScraper(options);
        scraper.RequestsNeeded += table.GiveAsync;

        var result = await scraper.RunToListAsync();

        CollectionAssert.AreEquivalent(Enumerable.Range(0, 25).Select(Url).ToArray(), result.Items.Cast<string>().ToArray());
        Assert.AreEqual((0L, 0), table.Calls.Last(), "the run ends after a call that gives nothing when nothing is left");
        Assert.AreEqual(0, scraper.Stats.Pending);
        Assert.AreEqual(1, table.MaxActive, "calls never overlap");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task UnfinishedRequests_StayUnderTheLimitPlusOneBatch()
    {
        var options = ScraperTests.FastOptions();
        options.MaxConcurrency = 4;
        options.RequestsNeededBelow = 20;
        var table = new Table(count: 300, batch: 10);
        long maxPending = 0;
        Scraper scraper = null!;
        scraper = ItemScraper(options, TimeSpan.FromMilliseconds(1), onLoad: _ => InterlockedMax(ref maxPending, scraper.Stats.Pending));
        scraper.RequestsNeeded += table.GiveAsync;

        var result = await scraper.RunToListAsync();

        Assert.HasCount(300, result.Items);
        Assert.IsTrue(table.Calls.All(c => c.Pending < 20), "raised only below the limit");
        Assert.IsLessThanOrEqualTo(29L, maxPending, "at most RequestsNeededBelow - 1 + one batch are unfinished");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task NothingGivenInTheMiddle_TheNextCallComesWhenNothingIsLeft()
    {
        var options = ScraperTests.FastOptions();
        options.RequestsNeededBelow = 10;
        var rows = new Queue<int[]>([[0, 1, 2, 3, 4], [], [5, 6, 7]]);      // the second read finds nothing, the third finds new rows
        var calls = new ConcurrentQueue<(long Pending, int Given)>();
        var scraper = ItemScraper(options, TimeSpan.FromMilliseconds(20));
        scraper.RequestsNeeded += e =>
        {
            var batch = rows.Count > 0 ? rows.Dequeue() : [];
            foreach (var n in batch) e.Add(Url(n));
            calls.Enqueue((e.Pending, batch.Length));
            return Task.CompletedTask;
        };

        var result = await scraper.RunToListAsync();

        Assert.HasCount(8, result.Items);
        var list = calls.ToArray();
        var dry = Array.FindIndex(list, c => c.Given == 0 && c.Pending > 0);
        Assert.IsGreaterThanOrEqualTo(0, dry, "a call in the middle of the run gave nothing");
        Assert.AreEqual(0, list[dry + 1].Pending, "the next call came when nothing was left");
        Assert.AreEqual((0L, 0), list[^1]);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task OnlyDuplicatesGiven_IsNotTheEnd()
    {
        var options = ScraperTests.FastOptions();
        options.RequestsNeededBelow = 0;                                    // raised only when nothing is left
        var rows = new Queue<string[]>([[Url(1)], [Url(1)], [Url(2)]]);
        var calls = 0;
        var scraper = ItemScraper(options);
        scraper.RequestsNeeded += e =>
        {
            calls++;
            if (rows.Count > 0) e.Add(rows.Dequeue().Select(url => new ScrapeRequest(url)));
            return Task.CompletedTask;
        };

        var result = await scraper.RunToListAsync();

        CollectionAssert.AreEqual(new[] { Url(1), Url(2) }, result.Items.Cast<string>().ToArray());
        Assert.AreEqual(4, calls, "the duplicate counts as given, so the table is read again");
        Assert.AreEqual(1, scraper.Stats.Duplicates);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task BadUrl_FailsItsRequest_AndTheRunGoesOn()
    {
        var failed = new ConcurrentQueue<FailedRequest>();
        var given = false;
        var scraper = ItemScraper(ScraperTests.FastOptions());
        scraper.RequestFailed += failed.Enqueue;
        scraper.RequestsNeeded += e =>
        {
            if (!given)
            {
                given = true;
                Assert.IsFalse(e.Add("not a url"));
                Assert.IsFalse(e.Add("mailto:someone@s.test"));
                Assert.IsTrue(e.Add(Url(1)));
            }
            return Task.CompletedTask;
        };

        var result = await scraper.RunToListAsync();

        CollectionAssert.AreEqual(new[] { Url(1) }, result.Items.Cast<string>().ToArray());
        Assert.HasCount(2, result.Failed);
        Assert.IsTrue(result.Failed.All(f => f.Reason == FailureReason.BadUrl));
        Assert.HasCount(2, failed);
        Assert.AreEqual(2, scraper.Stats.Failed);
        Assert.AreEqual(0, scraper.Stats.Pending);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task HandlerException_EndsTheRunWithIt()
    {
        var calls = 0;
        var scraper = ItemScraper(ScraperTests.FastOptions());
        scraper.RequestsNeeded += async e =>
        {
            await Task.Yield();
            if (++calls == 2) throw new InvalidOperationException("the database is down");
            e.Add(Url(calls));
        };

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scraper.RunToListAsync());

        Assert.AreEqual("the database is down", error.Message);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AddAfterTheCall_Throws()
    {
        RequestsNeededEventArgs? kept = null;
        var scraper = ItemScraper(ScraperTests.FastOptions());
        scraper.RequestsNeeded += e =>
        {
            kept ??= e;
            return Task.CompletedTask;
        };

        await scraper.RunToListAsync();

        Assert.ThrowsExactly<InvalidOperationException>(() => kept!.Add(Url(1)));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MaxRequests_StopsTheEvent()
    {
        var options = ScraperTests.FastOptions();
        options.MaxRequests = 5;
        options.RequestsNeededBelow = 0;
        var calls = 0;
        var next = 0;
        var scraper = ItemScraper(options);
        scraper.RequestsNeeded += e =>                                     // a table without end
        {
            calls++;
            for (var i = 0; i < 3; i++) e.Add(Url(next++));
            return Task.CompletedTask;
        };

        var result = await scraper.RunToListAsync();

        Assert.HasCount(5, result.Items);
        Assert.AreEqual(2, calls);
        Assert.AreEqual(1, scraper.Stats.SkippedByLimit);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MaxRunDuration_StopsTheRun_AndCancelsTheCallInProgress()
    {
        var options = ScraperTests.FastOptions();
        options.MaxRunDuration = TimeSpan.FromMilliseconds(300);
        var calls = 0;
        var cancelled = false;
        var scraper = ItemScraper(options);
        scraper.RequestsNeeded += async e =>
        {
            if (++calls == 1)
            {
                e.Add(Url(1));
                e.Add(Url(2));
                return;
            }
            try
            {
                await Task.Delay(Timeout.Infinite, e.CancellationToken);    // the database never answers
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }
        };

        var result = await scraper.RunToListAsync();

        Assert.IsTrue(result.Stats.StoppedByTimeLimit);
        Assert.IsTrue(cancelled);
        Assert.HasCount(2, result.Items);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task StartRequests_AndTheEvent_Together()
    {
        var table = new Table(count: 3, batch: 2);
        var scraper = ItemScraper(ScraperTests.FastOptions());
        scraper.RequestsNeeded += table.GiveAsync;

        var result = await scraper.RunToListAsync(new ScrapeRequest("https://s.test/start"));

        CollectionAssert.AreEquivalent(new[] { "https://s.test/start", Url(0), Url(1), Url(2) }, result.Items.Cast<string>().ToArray());
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task TwoHandlers_AreCalledInTurn_WithOneArguments()
    {
        var order = new ConcurrentQueue<string>();
        var fromA = new Queue<int>([1]);
        var fromB = new Queue<int>([2, 3]);
        RequestsNeededEventArgs? seenByA = null;
        var same = true;
        var scraper = ItemScraper(ScraperTests.FastOptions());
        scraper.RequestsNeeded += e =>
        {
            order.Enqueue("a");
            seenByA = e;
            if (fromA.Count > 0) e.Add(Url(fromA.Dequeue()));
            return Task.CompletedTask;
        };
        scraper.RequestsNeeded += async e =>
        {
            await Task.Yield();
            order.Enqueue("b");
            same &= ReferenceEquals(seenByA, e);
            if (fromB.Count > 0) e.Add(Url(fromB.Dequeue()));
        };

        var result = await scraper.RunToListAsync();

        CollectionAssert.AreEquivalent(new[] { Url(1), Url(2), Url(3) }, result.Items.Cast<string>().ToArray());
        Assert.IsTrue(same, "both handlers of a call get one arguments object");
        var calls = order.ToArray();
        Assert.AreEqual(0, calls.Length % 2);
        for (var i = 0; i < calls.Length; i += 2) CollectionAssert.AreEqual(new[] { "a", "b" }, calls[i..(i + 2)]);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task WithStatePath_AStoppedRunIsContinued_AndTheStateIsDeletedWhenTheTableIsDone()
    {
        var directory = Path.Combine(Path.GetTempPath(), "z3l-scraper-tests", Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(directory, "run.json");
        try
        {
            var table = new Table(count: 10, batch: 10);

            // first run: slow pages, stopped by time with requests from the table not done
            var options = ScraperTests.FastOptions();
            options.StatePath = statePath;
            options.MaxConcurrency = 1;
            options.MaxRunDuration = TimeSpan.FromMilliseconds(300);
            var first = ItemScraper(options, TimeSpan.FromMilliseconds(100));
            first.RequestsNeeded += table.GiveAsync;
            var firstItems = (await first.RunToListAsync()).Items.Cast<string>().ToArray();
            Assert.IsTrue(File.Exists(statePath), "the stopped run must leave its state");

            // second run: the table has given every row; the saved requests are done, then the run ends
            options = ScraperTests.FastOptions();
            options.StatePath = statePath;
            var second = ItemScraper(options);
            second.RequestsNeeded += table.GiveAsync;
            var secondItems = (await second.RunToListAsync()).Items.Cast<string>().ToArray();

            CollectionAssert.AreEquivalent(Enumerable.Range(0, 10).Select(Url).ToArray(), firstItems.Union(secondItems).ToArray());
            Assert.IsGreaterThan(0, second.Stats.Restored);
            Assert.IsFalse(File.Exists(statePath));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [TestMethod]
    public async Task NegativeLimit_IsRejected()
    {
        var options = ScraperTests.FastOptions();
        options.RequestsNeededBelow = -1;

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => ItemScraper(options).RunToListAsync());
    }

    private static void InterlockedMax(ref long target, long value)
    {
        long current;
        while ((current = Interlocked.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}
