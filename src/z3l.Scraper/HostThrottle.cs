using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace z3l.Scraper;

/// <summary>
/// The line of each host: spaces the loads of a host and limits how many go at once. A host is spaced by the longest of
/// <see cref="ScraperOptions.HostDelay"/> (each a random 0–50 % longer), its robots.txt Crawl-delay and the delay of AutoThrottle after
/// a 429, a 503 or a block, and it has at most <see cref="ScraperOptions.MaxConcurrencyPerHost"/> loads going.
/// <para>
/// A host that needs none of it costs one dictionary lookup. A request that may not load now joins its host's line, by its priority,
/// and waits there, not in a worker: one pump per host hands requests back to the main queue as the host's slots come, so workers
/// keep loading other hosts meanwhile, and a big site at its limit does not hold back the rest. A request handed back goes first in
/// the main queue and holds its slot on the way; when the host's loads are spaced, the line moves on only when that load starts, so
/// the next slot counts from there however long the request waited for a worker.
/// </para>
/// </summary>
internal sealed class HostThrottle
{
    /// <summary>Delay after the first overload of a host; it doubles with each next one.</summary>
    internal static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(1);

    /// <summary>Under this the delay drops to the base one: the host is back to normal.</summary>
    private static readonly TimeSpan MinDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>States of idle hosts are swept now and then once there are this many, so a crawl of many hosts does not keep them all.</summary>
    private const int SweepAbove = 1000;

    private readonly TimeSpan _maxDelay, _hostDelay;
    private readonly int _maxLoads;
    private readonly bool _countLoads, _everyHost;
    private readonly ScraperStats _stats;
    private readonly Action<ScrapeRequest> _release;
    private readonly ILogger _logger;
    private readonly CancellationToken _token;

    private readonly object _lock = new();
    private readonly Dictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private int _entersSinceSweep;

    /// <param name="options">MaxThrottleDelay, MaxConcurrencyPerHost and HostDelay of the run.</param>
    /// <param name="stats">Counters of the run: a request waiting in a line counts as queued.</param>
    /// <param name="release">Hands a request of a line back to the main queue (marked with <see cref="ScrapeRequest.HasThrottleSlot"/>).</param>
    /// <param name="logger">Logger of the run.</param>
    /// <param name="token">Cancelled when the run stops: the pumps stop, and the requests in the lines stay unfinished.</param>
    public HostThrottle(ScraperOptions options, ScraperStats stats, Action<ScrapeRequest> release, ILogger logger, CancellationToken token)
    {
        _stats = stats;
        _maxDelay = options.MaxThrottleDelay;
        _hostDelay = options.HostDelay;
        _countLoads = options.MaxConcurrencyPerHost != null;
        _maxLoads = options.MaxConcurrencyPerHost ?? int.MaxValue;
        _everyHost = _countLoads || _hostDelay > TimeSpan.Zero;
        _release = release;
        _logger = logger;
        _token = token;
    }

    /// <summary>
    /// True: load now, and call <see cref="Exit"/> when the load is over. False: the request joined the host's line and comes back
    /// through the main queue when its slot comes (marked with <see cref="ScrapeRequest.HasThrottleSlot"/>).
    /// </summary>
    public bool Enter(string host, ScrapeRequest request)
    {
        HostState? state;
        bool load, pump;
        lock (_lock)
        {
            var hasSlot = request.HasThrottleSlot;
            request.HasThrottleSlot = false;
            if (!_hosts.TryGetValue(host, out state))
            {
                if (!_everyHost || hasSlot) return true;
                if (++_entersSinceSweep >= SweepAbove && _hosts.Count >= SweepAbove) Sweep();
                state = _hosts[host] = new HostState();
            }

            var now = Stopwatch.GetTimestamp();
            if (hasSlot)
            {
                // the slot is taken now, however long the request waited for a worker: the next load is spaced from this one.
                // It was counted in the host's loads when the line handed it back
                state.Released--;
                Start(state, now);
                load = true;
            }
            else if (state.LineCount == 0 && state.Loads < _maxLoads && state.NextSlot <= now && (state.Released == 0 || !IsSpaced(state)))
            {
                if (_countLoads) state.Loads++;
                Start(state, now);
                return true;
            }
            else
            {
                state.Join(request);
                _stats.ChangeQueued(1);
                load = false;
            }
            pump = StartPump(state);
        }
        if (pump) _ = PumpAsync(host, state);
        return load;
    }

    /// <summary>A load that <see cref="Enter"/> let go is over (the loader answered or threw): its slot is free for the next one.</summary>
    public void Exit(string host)
    {
        if (!_countLoads) return;
        HostState? state;
        bool pump;
        lock (_lock)
        {
            if (!_hosts.TryGetValue(host, out state)) return;
            state.Loads--;
            pump = StartPump(state);
            if (!pump) ForgetIfIdle(host, state);
        }
        if (pump) _ = PumpAsync(host, state);
    }

    /// <summary>The host answered 429 / 503 or blocked: double its delay and pause it (as long as Retry-After asks, if longer).</summary>
    public void OnOverloaded(string host, TimeSpan? retryAfter, string reason)
    {
        TimeSpan delay;
        lock (_lock)
        {
            var state = GetOrAdd(host);
            var next = state.Delay < FirstDelay ? FirstDelay : state.Delay * 2;
            delay = state.Delay = next > _maxDelay ? _maxDelay : next;

            var pause = retryAfter is { } asked && asked > delay ? (asked > _maxDelay ? _maxDelay : asked) : delay;
            var until = Stopwatch.GetTimestamp() + Ticks(pause);
            if (until > state.NextSlot) state.NextSlot = until;
            if (state.LoggedDelay == delay) return;
            state.LoggedDelay = delay;
        }
        _logger.LogInformation("{Host} is overloaded ({Reason}): one load per {Delay} s", host, reason, delay.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>A good load: the delay shrinks back towards the base one.</summary>
    public void OnSuccess(string host)
    {
        lock (_lock)
        {
            if (!_hosts.TryGetValue(host, out var state) || state.Delay <= state.BaseDelay) return;

            var next = state.Delay * 0.9;
            state.Delay = next < state.BaseDelay + MinDelay ? state.BaseDelay : next;
            ForgetIfIdle(host, state);      // back to normal: no bookkeeping for this host any more
        }
    }

    /// <summary>The smallest delay of a host (robots.txt Crawl-delay).</summary>
    public void SetBaseDelay(string host, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return;
        if (delay > _maxDelay) delay = _maxDelay;
        lock (_lock)
        {
            var state = GetOrAdd(host);
            if (state.BaseDelay == delay) return;
            state.BaseDelay = delay;
            if (state.Delay < delay) state.Delay = delay;
        }
    }

    /// <summary>Hosts that have a state now: spaced, limited or waiting.</summary>
    internal int HostCount
    {
        get
        {
            lock (_lock) return _hosts.Count;
        }
    }

    /// <summary>Current delay of AutoThrottle and Crawl-delay between loads of a host, zero when it is not throttled.</summary>
    public TimeSpan Delay(string host)
    {
        lock (_lock) return _hosts.TryGetValue(host, out var state) ? state.Delay : TimeSpan.Zero;
    }

    /// <summary>
    /// <see cref="ScraperOptions.HostDelay"/> made a random 0–50 % longer, so the loads of a host do not come like a clock and it still
    /// gets no more than one load per HostDelay.
    /// </summary>
    internal static TimeSpan Jittered(TimeSpan delay) =>
        delay <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromTicks(delay.Ticks + (long)(delay.Ticks * 0.5 * Random.Shared.NextDouble()));

    /// <summary>Under the lock: the host's loads are spaced, so its line hands out one request at a time.</summary>
    private bool IsSpaced(HostState state) => state.Delay > TimeSpan.Zero || _hostDelay > TimeSpan.Zero;

    /// <summary>Under the lock: a load of the host starts now; the next one may start after the longest of the host's delays.</summary>
    private void Start(HostState state, long now)
    {
        var delay = Jittered(_hostDelay);
        if (state.Delay > delay) delay = state.Delay;
        if (delay > TimeSpan.Zero) state.NextSlot = Math.Max(state.NextSlot, now + Ticks(delay));
    }

    /// <summary>
    /// Under the lock: true when the host's line has requests the host may load and nobody moves it: no pump, a free slot, and, when
    /// its loads are spaced, no request out with the slot.
    /// </summary>
    private bool StartPump(HostState state)
    {
        if (state.Pumping || state.LineCount == 0 || state.Loads >= _maxLoads || (state.Released > 0 && IsSpaced(state))) return false;
        state.Pumping = true;
        return true;
    }

    /// <summary>
    /// Waits for the host's next slot and hands the first requests of its line back to the main queue: one when its loads are spaced
    /// (its load starting starts the pump again), as many as it has free slots when they are not.
    /// </summary>
    private async Task PumpAsync(string host, HostState state)
    {
        try
        {
            while (true)
            {
                List<ScrapeRequest>? next = null;
                TimeSpan wait = default;
                lock (_lock)
                {
                    var now = Stopwatch.GetTimestamp();
                    if (state.LineCount == 0 || state.Loads >= _maxLoads || (state.Released > 0 && IsSpaced(state)))
                    {
                        // nothing to hand out now: an Exit, a request starting with the slot or a new request starts the pump again
                        state.Pumping = false;
                        ForgetIfIdle(host, state);
                        return;
                    }
                    if (state.NextSlot <= now)
                    {
                        next = [];
                        var spaced = IsSpaced(state);
                        do
                        {
                            var request = state.Line!.Dequeue();
                            _stats.ChangeQueued(-1);
                            request.HasThrottleSlot = true;
                            state.Released++;
                            if (_countLoads) state.Loads++;
                            next.Add(request);
                        }
                        while (!spaced && state.LineCount > 0 && state.Loads < _maxLoads);
                        state.Pumping = false;
                    }
                    else
                    {
                        wait = Stopwatch.GetElapsedTime(now, state.NextSlot);
                    }
                }

                if (next != null)
                {
                    foreach (var request in next) _release(request);
                    return;
                }
                await Task.Delay(wait, _token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_lock) state.Pumping = false;     // the run stops: the waiting requests stay unfinished
        }
        catch (Exception ex)
        {
            // never strand requests in the line: the run would wait for them forever
            List<ScrapeRequest> stranded = [];
            lock (_lock)
            {
                state.Pumping = false;
                while (state.LineCount > 0)
                {
                    var request = state.Line!.Dequeue();
                    _stats.ChangeQueued(-1);
                    request.HasThrottleSlot = true;
                    state.Released++;
                    if (_countLoads) state.Loads++;
                    stranded.Add(request);
                }
            }
            _logger.LogError(ex, "Throttle of {Host} failed, {Count} waiting requests go back to the queue", host, stranded.Count);
            foreach (var request in stranded) _release(request);
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

    /// <summary>Under the lock: a host with nothing going, nothing waiting and no delay of its own needs no state any more.</summary>
    private void ForgetIfIdle(string host, HostState state)
    {
        if (IsIdle(state, Stopwatch.GetTimestamp())) _hosts.Remove(host);
    }

    private static bool IsIdle(HostState state, long now) =>
        state.LineCount == 0 && !state.Pumping && state.Released == 0 && state.Loads == 0 && state.Delay == TimeSpan.Zero
        && state.BaseDelay == TimeSpan.Zero && state.NextSlot <= now;

    /// <summary>Under the lock: forgets the hosts that are idle, and whose last slot has passed.</summary>
    private void Sweep()
    {
        _entersSinceSweep = 0;
        var now = Stopwatch.GetTimestamp();
        foreach (var (host, state) in _hosts)
        {
            if (IsIdle(state, now)) _hosts.Remove(host);
        }
    }

    private static long Ticks(TimeSpan delay) => (long)(delay.TotalSeconds * Stopwatch.Frequency);

    private sealed class HostState
    {
        public TimeSpan Delay;          // of AutoThrottle, at least BaseDelay
        public TimeSpan BaseDelay;      // robots.txt Crawl-delay
        public TimeSpan LoggedDelay;
        public long NextSlot;           // Stopwatch timestamp: the earliest start of the next load
        public bool Pumping;

        /// <summary>Loads going and requests handed back with a slot, counted with MaxConcurrencyPerHost only.</summary>
        public int Loads;

        /// <summary>Requests the line handed its slots to, on their way to a worker.</summary>
        public int Released;

        private long _sequence;

        /// <summary>Requests waiting for a slot, by priority and then in the order they came; made when the first one waits.</summary>
        public PriorityQueue<ScrapeRequest, (int Priority, long Sequence)>? Line;

        public int LineCount => Line?.Count ?? 0;

        public void Join(ScrapeRequest request) => (Line ??= new()).Enqueue(request, ((int)request.Priority, _sequence++));
    }
}
