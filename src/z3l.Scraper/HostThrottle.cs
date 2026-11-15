using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace z3l.Scraper;

/// <summary>
/// Spaces the loads of hosts that are overloaded (429, 503, blocks) or ask for it (robots.txt Crawl-delay).
/// A host that needs no spacing costs one dictionary lookup. A throttled host gets a line: requests wait in it,
/// not in a worker, and one pump per host hands them back to the main queue one slot at a time, so workers keep
/// loading other hosts meanwhile.
/// </summary>
internal sealed class HostThrottle(TimeSpan maxDelay, Action<ScrapeRequest> release, ILogger logger, CancellationToken token)
{
    /// <summary>Delay after the first overload of a host; it doubles with each next one.</summary>
    internal static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(1);

    /// <summary>Under this the delay drops to the base one: the host is back to normal.</summary>
    private static readonly TimeSpan MinDelay = TimeSpan.FromMilliseconds(100);

    private readonly object _lock = new();
    private readonly Dictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True: load now. False: the request joined the host's line and comes back through the main queue when its slot comes
    /// (marked with <see cref="ScrapeRequest.HasThrottleSlot"/>).
    /// </summary>
    public bool Enter(string host, ScrapeRequest request)
    {
        lock (_lock)
        {
            if (request.HasThrottleSlot)
            {
                request.HasThrottleSlot = false;
                return true;
            }
            if (!_hosts.TryGetValue(host, out var state)) return true;

            var now = Stopwatch.GetTimestamp();
            if (state.Line.Count == 0 && state.NextSlot <= now)
            {
                state.NextSlot = now + Ticks(state.Delay);
                return true;
            }

            state.Line.Enqueue(request);
            if (!state.Pumping)
            {
                state.Pumping = true;
                _ = PumpAsync(host, state);
            }
            return false;
        }
    }

    /// <summary>The host answered 429 / 503 or blocked: double its delay and pause it (as long as Retry-After asks, if longer).</summary>
    public void OnOverloaded(string host, TimeSpan? retryAfter, string reason)
    {
        TimeSpan delay;
        lock (_lock)
        {
            var state = GetOrAdd(host);
            var next = state.Delay < FirstDelay ? FirstDelay : state.Delay * 2;
            delay = state.Delay = next > maxDelay ? maxDelay : next;

            var pause = retryAfter is { } asked && asked > delay ? (asked > maxDelay ? maxDelay : asked) : delay;
            var until = Stopwatch.GetTimestamp() + Ticks(pause);
            if (until > state.NextSlot) state.NextSlot = until;
            if (state.LoggedDelay == delay) return;
            state.LoggedDelay = delay;
        }
        logger.LogInformation("{Host} is overloaded ({Reason}): one load per {Delay} s", host, reason, delay.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>A good load: the delay shrinks back towards the base one.</summary>
    public void OnSuccess(string host)
    {
        lock (_lock)
        {
            if (!_hosts.TryGetValue(host, out var state) || state.Delay <= state.BaseDelay) return;

            var next = state.Delay * 0.9;
            state.Delay = next < state.BaseDelay + MinDelay ? state.BaseDelay : next;
            if (state.Delay == TimeSpan.Zero && state.Line.Count == 0 && !state.Pumping && state.NextSlot <= Stopwatch.GetTimestamp())
            {
                _hosts.Remove(host);    // back to normal: no bookkeeping for this host any more
            }
        }
    }

    /// <summary>The smallest delay of a host (robots.txt Crawl-delay).</summary>
    public void SetBaseDelay(string host, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return;
        if (delay > maxDelay) delay = maxDelay;
        lock (_lock)
        {
            var state = GetOrAdd(host);
            if (state.BaseDelay == delay) return;
            state.BaseDelay = delay;
            if (state.Delay < delay) state.Delay = delay;
        }
    }

    /// <summary>Current delay between loads of a host, zero when it is not throttled.</summary>
    public TimeSpan Delay(string host)
    {
        lock (_lock) return _hosts.TryGetValue(host, out var state) ? state.Delay : TimeSpan.Zero;
    }

    private async Task PumpAsync(string host, HostState state)
    {
        try
        {
            while (true)
            {
                ScrapeRequest? next = null;
                TimeSpan wait;
                lock (_lock)
                {
                    if (state.Line.Count == 0)
                    {
                        state.Pumping = false;
                        return;
                    }
                    var now = Stopwatch.GetTimestamp();
                    if (state.NextSlot <= now)
                    {
                        next = state.Line.Dequeue();
                        next.HasThrottleSlot = true;
                        state.NextSlot = now + Ticks(state.Delay);
                        wait = TimeSpan.Zero;
                    }
                    else
                    {
                        wait = Stopwatch.GetElapsedTime(now, state.NextSlot);
                    }
                }

                if (next != null) release(next);
                else await Task.Delay(wait, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_lock) state.Pumping = false;     // the run stops: the waiting requests stay unfinished
        }
        catch (Exception ex)
        {
            // never strand requests in the line: the run would wait for them forever
            List<ScrapeRequest> stranded;
            lock (_lock)
            {
                state.Pumping = false;
                stranded = [.. state.Line];
                state.Line.Clear();
            }
            logger.LogError(ex, "Throttle of {Host} failed, {Count} waiting requests go back to the queue", host, stranded.Count);
            foreach (var request in stranded)
            {
                request.HasThrottleSlot = true;
                release(request);
            }
        }
    }

    private HostState GetOrAdd(string host)
    {
        if (!_hosts.TryGetValue(host, out var state))
        {
            state = new HostState();
            _hosts[host] = state;
        }
        return state;
    }

    private static long Ticks(TimeSpan delay) => (long)(delay.TotalSeconds * Stopwatch.Frequency);

    private sealed class HostState
    {
        public TimeSpan Delay;
        public TimeSpan BaseDelay;
        public TimeSpan LoggedDelay;
        public long NextSlot;
        public bool Pumping;
        public Queue<ScrapeRequest> Line { get; } = new();
    }
}
