using System.Text;
using Microsoft.Extensions.Time.Testing;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace z3l.Scraper.RabbitMQ.Tests;

[TestClass]
public sealed class TimesyncTrackerTests
{
    private static readonly TimeSpan Timeout5 = TimeSpan.FromMinutes(5);

    private static (FakeTimeProvider Clock, TimesyncTracker Tracker) Create()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 16, 1, 0, 0, TimeSpan.Zero));
        return (clock, new TimesyncTracker(clock, Timeout5));
    }

    private static Guid Publish(TimesyncTracker tracker)
    {
        var id = Guid.NewGuid();
        tracker.Add(id);
        tracker.Published(id);
        return id;
    }

    [TestMethod]
    public void TimeInTheQueueDoesNotCount_TheDeadlineIsTheReturnOfTheMarkerPlusTheTimeout()
    {
        // 01:00 a burst of tasks, 01:02 marker 1, the queue takes 30 minutes: the marker comes back at 01:32
        var (clock, tracker) = Create();
        var tasks = Enumerable.Range(0, 3).Select(_ => Publish(tracker)).ToList();
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.AreEqual(1L, tracker.NextMarker());
        clock.Advance(TimeSpan.FromMinutes(30));

        Assert.AreEqual(3, tracker.MarkerReturned(1));
        Assert.IsEmpty(tracker.TakeLost(), "taken a moment ago: not lost though they were sent 32 minutes ago");

        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Assert.IsEmpty(tracker.TakeLost(), "01:36:59, the deadline is 01:37");

        clock.Advance(TimeSpan.FromSeconds(2));
        CollectionAssert.AreEquivalent(tasks, tracker.TakeLost());
        Assert.AreEqual(0, tracker.Count);
    }

    [TestMethod]
    public void AMarkerCoversOnlyTasksPublishedBeforeItsNumberWasTaken()
    {
        var (_, tracker) = Create();
        var before = Publish(tracker);
        Assert.AreEqual(1L, tracker.NextMarker());
        var after = Publish(tracker);              // may be behind marker 1 in the queue

        Assert.AreEqual(1, tracker.MarkerReturned(1));
        Assert.AreEqual(1, tracker.Taken);

        Assert.AreEqual(2L, tracker.NextMarker());
        Assert.AreEqual(1, tracker.MarkerReturned(2), "the next marker covers it");
        Assert.AreEqual(2, tracker.Taken);
        _ = before;
        _ = after;
    }

    [TestMethod]
    public void ATaskStillBeingPublished_IsNotCovered()
    {
        var (_, tracker) = Create();
        var id = Guid.NewGuid();
        tracker.Add(id);                            // publish not finished yet
        Assert.AreEqual(1L, tracker.NextMarker());
        Assert.AreEqual(0, tracker.MarkerReturned(1));

        tracker.Published(id);
        Assert.AreEqual(2L, tracker.NextMarker());
        Assert.AreEqual(1, tracker.MarkerReturned(2));
    }

    [TestMethod]
    public void NoMarkerWhenEveryTaskIsTaken()
    {
        var (_, tracker) = Create();
        Assert.IsNull(tracker.NextMarker(), "no tasks");

        Publish(tracker);
        Assert.AreEqual(1L, tracker.NextMarker());
        tracker.MarkerReturned(1);
        Assert.IsNull(tracker.NextMarker(), "the only task is taken already");
    }

    [TestMethod]
    public void AnAnsweredTask_IsNeverLost_AndADeadlineIsSetOnce()
    {
        var (clock, tracker) = Create();
        var answered = Publish(tracker);
        var slow = Publish(tracker);
        tracker.NextMarker();
        tracker.MarkerReturned(1);

        Assert.IsTrue(tracker.Remove(answered));
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.AreEqual(0, tracker.MarkerReturned(1), "a marker that comes twice does not move the deadline");

        clock.Advance(TimeSpan.FromMinutes(3));
        CollectionAssert.AreEqual(new[] { slow }, tracker.TakeLost());
        Assert.IsFalse(tracker.Remove(slow), "already lost");
    }

    [TestMethod]
    public void TimeWhileThePauseLasts_DoesNotCountAgainstTheDeadline()
    {
        var wall = new FakeTimeProvider(new DateTimeOffset(2026, 11, 16, 1, 0, 0, TimeSpan.Zero));
        var clock = new PausableClock(wall);
        var tracker = new TimesyncTracker(clock, Timeout5);
        var id = Publish(tracker);
        tracker.NextMarker();
        tracker.MarkerReturned(1);                  // deadline: 5 minutes of running time

        wall.Advance(TimeSpan.FromMinutes(3));
        clock.Pause();
        wall.Advance(TimeSpan.FromMinutes(20));     // the scraper's handlers were behind, answers waited in the broker
        Assert.IsEmpty(tracker.TakeLost());
        clock.Resume();

        wall.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(59));
        Assert.IsEmpty(tracker.TakeLost(), "4:59 of running time");
        wall.Advance(TimeSpan.FromSeconds(2));
        CollectionAssert.AreEqual(new[] { id }, tracker.TakeLost());
    }

    [TestMethod]
    public void Results_GoThroughTheBrokerAsTheyWere()
    {
        var page = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<p>Привет, мир</p>", 1000)));
        var header = new LoadResultHeader
        {
            Id = Guid.NewGuid(), Status = 200, Url = "https://s.test/p", ContentType = "text/html; charset=utf-8",
            Headers = new() { ["X-A"] = "1" }, Text = true, Worker = "w1",
        };

        var message = Protocol.EncodeResult(header, page);
        var (decoded, body) = Protocol.DecodeResult(message);

        Assert.IsLessThan(page.Length / 5, message.Length, "brotli shrinks a page");
        Assert.AreEqual(header.Id, decoded.Id);
        Assert.AreEqual("1", decoded.Headers!["X-A"]);
        Assert.IsTrue(decoded.Text);
        CollectionAssert.AreEqual(page, body);
    }
}
