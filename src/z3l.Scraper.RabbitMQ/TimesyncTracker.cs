namespace z3l.Scraper.RabbitMQ;

/// <summary>
/// Lost loads of one task queue, found by timesync markers instead of timeouts: a queue can be of any length, and a load that
/// waits in it is not lost.
/// <para>
/// A marker goes to the task queue after the tasks; a worker that takes it answers at once. Queues are first in, first out
/// (markers go with the lowest priority), so when marker N comes back every task sent before it has been taken by a worker.
/// Such tasks get a deadline: the time marker N came back plus the execution timeout. A task without a result by its deadline
/// is lost (the worker died or hung), and the scraper sends it again. How long a task waited in the queue does not count.
/// </para>
/// <para>
/// A task is covered by the first marker sent after its publish ended. Its number is read after the publish, and a marker's
/// number is taken before its publish starts, so a marker that may have overtaken the task never covers it.
/// </para>
/// </summary>
internal sealed class TimesyncTracker(TimeProvider clock, TimeSpan executionTimeout)
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Entry> _tasks = [];
    private long _lastMarker;

    private sealed class Entry
    {
        public long CoveredBy = long.MaxValue;     // not published yet: no marker covers it
        public DateTimeOffset? Deadline;
    }

    /// <summary>Tasks without a result.</summary>
    public int Count
    {
        get { lock (_lock) return _tasks.Count; }
    }

    /// <summary>Tasks taken by workers (a marker sent after them came back).</summary>
    public int Taken
    {
        get { lock (_lock) return _tasks.Values.Count(x => x.Deadline != null); }
    }

    /// <summary>Before the publish of a task.</summary>
    public void Add(Guid id)
    {
        lock (_lock) _tasks[id] = new Entry();
    }

    /// <summary>After the publish of a task ended: the next marker covers it.</summary>
    public void Published(Guid id)
    {
        lock (_lock)
        {
            if (_tasks.TryGetValue(id, out var entry)) entry.CoveredBy = _lastMarker + 1;
        }
    }

    /// <summary>The task got its result, failed to publish, or was cancelled. False: it was not here (already lost).</summary>
    public bool Remove(Guid id)
    {
        lock (_lock) return _tasks.Remove(id);
    }

    /// <summary>The number of the next marker, taken before its publish starts; null when no task waits for one.</summary>
    public long? NextMarker()
    {
        lock (_lock)
        {
            if (!_tasks.Values.Any(x => x.Deadline == null)) return null;
            return ++_lastMarker;
        }
    }

    /// <summary>Marker <paramref name="number"/> came back: the tasks it covers were taken by now.</summary>
    /// <returns>How many tasks got their deadline.</returns>
    public int MarkerReturned(long number)
    {
        var deadline = clock.GetUtcNow() + executionTimeout;
        var count = 0;
        lock (_lock)
        {
            foreach (var entry in _tasks.Values)
            {
                if (entry.Deadline == null && entry.CoveredBy <= number)
                {
                    entry.Deadline = deadline;
                    count++;
                }
            }
        }
        return count;
    }

    /// <summary>Removes and returns the tasks past their deadline.</summary>
    public List<Guid> TakeLost()
    {
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            var lost = _tasks.Where(x => x.Value.Deadline < now).Select(x => x.Key).ToList();
            foreach (var id in lost) _tasks.Remove(id);
            return lost;
        }
    }
}
