namespace z3l.Scraper.RabbitMQ;

/// <summary>
/// A clock that stands still while the loader is paused: answers wait in the broker (or in the client's buffer) then, so that
/// time must not count against a task's deadline.
/// </summary>
internal sealed class PausableClock(TimeProvider inner) : TimeProvider
{
    private readonly object _lock = new();
    private TimeSpan _pausedTotal;
    private DateTimeOffset? _pausedSince;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            var now = inner.GetUtcNow();
            var paused = _pausedTotal + (_pausedSince is { } since ? now - since : TimeSpan.Zero);
            return now - paused;
        }
    }

    public void Pause()
    {
        lock (_lock) _pausedSince ??= inner.GetUtcNow();
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (_pausedSince is not { } since) return;
            _pausedTotal += inner.GetUtcNow() - since;
            _pausedSince = null;
        }
    }
}
