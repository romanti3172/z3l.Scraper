using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using z3l.Scraper.Crawling;
using z3l.Scraper.Loading;
using z3l.Scraper.Validation;

namespace z3l.Scraper;

/// <summary>An item on its way to the consumer, with its page when the run keeps a state file.</summary>
internal readonly record struct Emitted(object Item, ScrapeRequest? Request);

/// <summary>
/// State of one run. Every accepted request is "pending" until it is handled or failed; retries, host lines and delays keep it
/// pending. A handler queues new pages before its own request finishes, so pending reaches zero only when all the work is done.
/// </summary>
internal sealed class ScraperRun : IAsyncDisposable, IScrapeHost
{
    private readonly Scraper _scraper;
    private readonly ScraperOptions _options;
    private readonly CancellationTokenSource _stop;

    // requests of local loaders wait for one of MaxConcurrency workers; those of queue loaders are sent at once
    private readonly Lane _local = new(), _queued = new();
    private long _sequence;

    private sealed class Lane
    {
        public readonly object Lock = new();
        public readonly PriorityQueue<ScrapeRequest, (int Priority, long Sequence)> Requests = new();
        public readonly SemaphoreSlim Available = new(0);
    }

    // guarded by lock (_seen): what was seen, what is pending, how many were accepted (one consistent picture for the state file).
    // _seen keeps the hashes of the request keys, 16 bytes a page whatever its URL and body.
    // _unsaved holds, with a state file only, the requests not finished plus the finished ones whose items the consumer has not taken:
    // if the process dies, all of them are done again (at least once)
    // _seenUnsaved holds, with a state file only, the hashes seen since the last save: the next save appends them to the journal
    private readonly HashSet<KeyHash> _seen = new();
    private readonly List<KeyHash> _seenUnsaved = new();
    private readonly ConcurrentDictionary<ScrapeRequest, byte> _unsaved = new(ReferenceEqualityComparer.Instance);
    private int _accepted;
    private long _seenStored;       // hashes the journal holds; changed by the saves, under _saveLock

    private readonly ConcurrentDictionary<string, EffectiveRules> _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<FailedRequest> _failed = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pending;
    private int _requestLimitLogged, _depthLimitLogged, _scopeLogged;
    private readonly CrawlScope _scope;

    private readonly object _rateLock = new();
    private readonly long _rateInterval;     // Stopwatch ticks between load starts, 0 for no limit
    private long _nextRateSlot;

    private readonly HostThrottle _throttle;
    private readonly RobotsCache? _robots;
    private readonly ICachingPageLoader? _cache;
    private readonly ISnapshotPageLoader? _snapshots;
    private readonly string? _statePath;
    private readonly StateDataCodec? _codec;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private readonly Channel<Emitted> _items;

    // queue loaders: every request is sent at once, MaxConcurrency handlers take the pages, and the loaders are paused
    // while more than MaxLoadedNotHandled pages wait for a handler
    private readonly IQueuePageLoader[] _queueLoaders;
    private readonly bool _hasLocalLoader;
    private readonly SemaphoreSlim? _handlerSlots;
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
    private readonly object _pauseLock = new();
    private int _waitingForHandler;
    private bool _loaderPaused;

    // Scraper.RequestsNeeded: one loop calls its handlers when the run is low on requests, and it decides when the run is over
    private readonly Func<RequestsNeededEventArgs, Task>? _requestsNeeded;
    private readonly int _requestsNeededBelow;
    private readonly Channel<bool>? _refillWake;        // capacity 1, a write to a full one is dropped: a signal that wakes the loop
    private bool _sourceDry;                            // a call in the middle of the run added nothing: the next comes when nothing is left
    private bool _sourceOver;                           // no more calls: a call with nothing left added nothing, or MaxRequests was reached
    private ExceptionDispatchInfo? _requestsNeededError;

    public ScraperRun(Scraper scraper, CancellationToken cancellationToken)
    {
        _scraper = scraper;
        _options = scraper.Options;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _items = Channel.CreateBounded<Emitted>(new BoundedChannelOptions(_options.ItemBufferSize)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        if (_options.MaxRequestsPerMinute is { } perMinute)
        {
            _rateInterval = Stopwatch.Frequency * 60 / perMinute;
        }
        _scope = new CrawlScope(_options);
        _throttle = new HostThrottle(_options, Stats, Push, scraper.Logger, _stop.Token);
        if (_options.RespectRobotsTxt)
        {
            _robots = new RobotsCache(scraper.Loader, _options, scraper.Logger, _stop.Token);
        }
        _cache = scraper.CachingPageLoader;
        _snapshots = scraper.SnapshotPageLoader;
        _statePath = string.IsNullOrWhiteSpace(_options.StatePath) ? null : _options.StatePath;
        if (_statePath != null) _codec = new StateDataCodec(scraper.FindRoute, _options.StateJson);
        _queueLoaders = [.. scraper.Loaders.OfType<IQueuePageLoader>()];
        _hasLocalLoader = scraper.Loaders.Any(x => x is not IQueuePageLoader);
        if (_queueLoaders.Length > 0) _handlerSlots = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);

        _requestsNeeded = scraper.RequestsNeededHandlers;
        if (_requestsNeeded != null)
        {
            _requestsNeededBelow = _options.RequestsNeededBelow;
            _refillWake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
            });
        }
    }

    public ScraperStats Stats { get; } = new();
    public ILogger Logger => _scraper.Logger;
    public ChannelReader<Emitted> Items => _items.Reader;
    public IEnumerable<FailedRequest> FailedRequests => _failed;
    public Task Completion { get; private set; } = Task.CompletedTask;

    public void Start(IEnumerable<ScrapeRequest> startRequests)
    {
        Stats.Start();
        if (_statePath != null && File.Exists(_statePath)) RestoreState(_statePath);

        foreach (var request in startRequests)
        {
            ArgumentNullException.ThrowIfNull(request, nameof(startRequests));
            Enqueue(request, parent: null);
        }
        PendingChanged(Volatile.Read(ref _pending));

        Completion = RunAsync();
    }

    private async Task RunAsync()
    {
        Exception? error = null;
        try
        {
            var token = _stop.Token;
            var workers = new List<Task>();
            if (_hasLocalLoader) workers.AddRange(Enumerable.Range(0, _options.MaxConcurrency).Select(_ => Task.Run(() => WorkerAsync(token))));
            if (_queueLoaders.Length > 0) workers.Add(Task.Run(() => DispatchAsync(token)));
            var progress = ProgressAsync(token);
            var saver = _statePath != null ? SaveLoopAsync(token) : Task.CompletedTask;
            var timeLimit = Task.Delay(_options.MaxRunDuration ?? Timeout.InfiniteTimeSpan, token);
            // ends only when a RequestsNeeded handler throws (or the run stops)
            var refill = _refillWake != null ? Task.Run(() => RefillLoopAsync(token)) : null;

            var finished = await (refill == null ? Task.WhenAny(_drained.Task, timeLimit) : Task.WhenAny(_drained.Task, timeLimit, refill))
                .ConfigureAwait(false);
            if (finished == timeLimit && timeLimit.IsCompletedSuccessfully)
            {
                Stats.StoppedByTimeLimit = true;
                Logger.LogWarning("Stopped after MaxRunDuration {Duration}: {Pending} requests not finished", _options.MaxRunDuration, Stats.Pending);
            }

            await _stop.CancelAsync().ConfigureAwait(false);
            await IgnoreCancellation(Task.WhenAll(workers)).ConfigureAwait(false);
            await IgnoreCancellation(Task.WhenAll(_inFlight.Keys)).ConfigureAwait(false);
            if (_queueLoaders.Length > 0) SetLoaderPaused(false);
            await IgnoreCancellation(progress).ConfigureAwait(false);
            await IgnoreCancellation(saver).ConfigureAwait(false);
            if (refill != null) await IgnoreCancellation(refill).ConfigureAwait(false);
            _requestsNeededError?.Throw();      // a RequestsNeeded handler threw: the run fails with its exception
        }
        catch (Exception ex)
        {
            error = ex;
            Logger.LogError(ex, "Scraper run failed");
        }
        finally
        {
            Stats.Stop();
            if (_statePath != null) await FinishStateAsync().ConfigureAwait(false);
            var reasons = Stats.Reasons;
            if (reasons.Count == 0) Logger.LogInformation("Scraping finished: {Stats}", Stats);
            else Logger.LogInformation("Scraping finished: {Stats}. Top reasons: {Reasons}", Stats, string.Join("; ", reasons.Take(5)));
            _items.Writer.TryComplete(error);
        }
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    // ---------------------------------------------------------------- queue

    /// <summary>
    /// Accepts a new request: start requests come without a parent, a handler's requests with the request it handles.
    /// False for a duplicate, a request over the limits, or a bad URL or one out of the crawl scope from a handler; a bad start URL throws.
    /// </summary>
    public bool Enqueue(ScrapeRequest request, ScrapeRequest? parent)
    {
        if (!UrlHelper.TryNormalizeUri(request.Url, out var uri))
        {
            if (parent != null) return false;
            throw new ArgumentException($"A start request needs an absolute http(s) URL: \"{request.Url}\"", nameof(request));
        }
        _scraper.RouteOf(request);       // where the mistake is, not later in the handler
        _codec?.Check(request.Label, request.Data);

        uri = _scope.Clean(uri);
        // the scope is for the links found on pages: start requests and those of RequestsNeeded are taken as given.
        // A link out of it is not marked as seen: nothing to remember, and the hash is not even made
        if (parent != null && !_scope.Allows(uri))
        {
            Stats.AddOutOfScope();
            if (Interlocked.Exchange(ref _scopeLogged, 1) == 0)
            {
                Logger.LogInformation("Links out of the crawl scope (AllowedHosts, UrlFilter) are skipped, the first: {Url} on {Page}", uri.AbsoluteUri, parent.Url);
            }
            return false;
        }

        var depth = parent == null ? 0 : parent.Depth + 1;
        // own copy: the caller may reuse its instance
        request = request with { Url = uri.AbsoluteUri, Attempts = 0, NotFoundCount = 0, RateLimitCount = 0, Depth = depth, HasThrottleSlot = false,
            SkipCache = false };
        // hashed before the lock: the SHA-256 of a long POST body takes a while
        KeyHash? key = request.SkipDuplicateCheck ? null : KeyHash.Of(request.GetUniqueKey());

        lock (_seen)
        {
            if (key.HasValue && _seen.Contains(key.Value))
            {
                Stats.AddDuplicate();
                return false;
            }
            // a request over the limits is not marked as seen: found again at a smaller depth it is taken
            if (_options.MaxDepth is { } maxDepth && depth > maxDepth)
            {
                Stats.AddSkippedByLimit();
                if (Interlocked.Exchange(ref _depthLimitLogged, 1) == 0) Logger.LogInformation("MaxDepth {MaxDepth}: deeper links are skipped", maxDepth);
                return false;
            }
            if (_options.MaxRequests is { } max && _accepted >= max)
            {
                Stats.AddSkippedByLimit();
                if (Interlocked.Exchange(ref _requestLimitLogged, 1) == 0) Logger.LogInformation("MaxRequests {MaxRequests} reached: new requests are skipped", max);
                return false;
            }

            if (key.HasValue)
            {
                _seen.Add(key.Value);
                if (_statePath != null) _seenUnsaved.Add(key.Value);
            }
            _accepted++;
            if (_statePath != null) _unsaved.TryAdd(request, 0);
            Interlocked.Increment(ref _pending);
        }

        Stats.AddEnqueued();
        Push(request);
        return true;
    }

    private void Push(ScrapeRequest request)
    {
        var lane = _scraper.LoaderFor(request.Label) is IQueuePageLoader ? _queued : _local;
        var sequence = Interlocked.Increment(ref _sequence);
        // a request its host's line handed a slot to goes first: the line waits for its load to start
        var priority = request.HasThrottleSlot ? -1 : (int)request.Priority;
        lock (lane.Lock)
        {
            lane.Requests.Enqueue(request, (priority, sequence));
        }
        Stats.ChangeQueued(1);
        lane.Available.Release();
    }

    private ScrapeRequest Pop(Lane lane)
    {
        lock (lane.Lock)
        {
            Stats.ChangeQueued(-1);
            return lane.Requests.Dequeue();
        }
    }

    /// <summary>The work on the request is over (handled or failed).</summary>
    private void Finish() => PendingChanged(Interlocked.Decrement(ref _pending));

    /// <summary>Unfinished requests went down to <paramref name="pending"/>: the run may be over, or low on requests.</summary>
    private void PendingChanged(int pending)
    {
        if (_refillWake == null)
        {
            if (pending == 0) _drained.TrySetResult();
            return;
        }
        // with RequestsNeeded handlers the loop that calls them decides when the run is over
        if (pending == 0 || (pending < _requestsNeededBelow && !Volatile.Read(ref _sourceDry) && !Volatile.Read(ref _sourceOver)))
        {
            _refillWake.Writer.TryWrite(true);
        }
    }

    /// <summary>The request no longer needs to be in the state file: it failed, or the consumer took all its items.</summary>
    private void Forget(ScrapeRequest request)
    {
        if (_statePath != null) _unsaved.TryRemove(request, out _);
    }

    /// <summary>The consumer asked for the next item, so it is done with the previous one.</summary>
    public void Acknowledge(ScrapeRequest? request)
    {
        if (request != null && Interlocked.Decrement(ref request.UnackedItems) == 0) Forget(request);
    }

    // ---------------------------------------------------------------- Scraper.RequestsNeeded

    /// <summary>
    /// A request given by a RequestsNeeded handler, queued as a start request. A URL that is not an absolute http(s) URL fails the
    /// request instead of throwing: a source of many rows may hold a broken one.
    /// </summary>
    internal bool AddRequest(ScrapeRequest request)
    {
        if (UrlHelper.TryNormalize(request.Url, out _)) return Enqueue(request, parent: null);

        Stats.AddEnqueued();        // accepted and failed at once, so Pending stays right
        Note(new Trouble(ReasonKind.BadUrl, "not an absolute http(s) URL", request.Url), request, failed: true);
        RecordFailure(new FailedRequest(request, FailureReason.BadUrl, $"not an absolute http(s) URL: \"{request.Url}\"", null, null));
        return false;
    }

    /// <summary>
    /// Calls the RequestsNeeded handlers when the run is low on requests, one call at a time, and ends the run when nothing is left
    /// and a call gives nothing, or when nothing is left after MaxRequests. An exception of a handler ends the run with it.
    /// </summary>
    private async Task RefillLoopAsync(CancellationToken token)
    {
        try
        {
            var wake = _refillWake!.Reader;
            while (true)
            {
                await wake.ReadAsync(token).ConfigureAwait(false);
                while (await RefillAsync(token).ConfigureAwait(false)) { }
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            _requestsNeededError = ExceptionDispatchInfo.Capture(ex);     // RunAsync ends the run with it
        }
    }

    /// <summary>One step of the loop: raises the event if the run needs requests. False: nothing to do until the next signal.</summary>
    private async Task<bool> RefillAsync(CancellationToken token)
    {
        var pending = Volatile.Read(ref _pending);
        var idle = pending == 0;

        if (!_sourceOver && _options.MaxRequests is { } max && Volatile.Read(ref _accepted) >= max)
        {
            Volatile.Write(ref _sourceOver, true);
            Logger.LogDebug("MaxRequests {MaxRequests} reached: RequestsNeeded is not raised any more", max);
        }
        if (_sourceOver)
        {
            if (idle) _drained.TrySetResult();
            return false;
        }
        if (!idle && (pending >= _requestsNeededBelow || Volatile.Read(ref _sourceDry))) return false;

        var given = await RaiseRequestsNeededAsync(pending, token).ConfigureAwait(false);
        if (given > 0)
        {
            Volatile.Write(ref _sourceDry, false);
            return true;        // still low on requests? the next call follows at once
        }
        if (idle && Volatile.Read(ref _pending) == 0)
        {
            Volatile.Write(ref _sourceOver, true);
            Logger.LogDebug("RequestsNeeded gave nothing and nothing is left: the run ends");
            _drained.TrySetResult();
            return false;
        }
        Volatile.Write(ref _sourceDry, true);     // nothing for now: the next call comes when nothing is left
        return Volatile.Read(ref _pending) == 0;
    }

    /// <summary>Calls the handlers of RequestsNeeded one after another with one arguments object; returns how many requests they gave.</summary>
    private async Task<int> RaiseRequestsNeededAsync(int pending, CancellationToken token)
    {
        var args = new RequestsNeededEventArgs(this, pending, token);
        int given;
        try
        {
            foreach (var handler in _requestsNeeded!.GetInvocationList())
            {
                if (((Func<RequestsNeededEventArgs, Task>)handler)(args) is { } call) await call.ConfigureAwait(false);
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);       // the run is stopping: not a failure of the handler
        }
        finally
        {
            given = args.Complete();
        }
        Logger.LogDebug("RequestsNeeded with {Pending} unfinished: {Given} requests given", pending, given);
        return given;
    }

    // ---------------------------------------------------------------- processing

    private async Task WorkerAsync(CancellationToken token)
    {
        while (true)
        {
            await _local.Available.WaitAsync(token).ConfigureAwait(false);
            await ProcessOneAsync(Pop(_local), token).ConfigureAwait(false);
        }
    }

    /// <summary>Queue loaders: every request is started at once, its load waits in the loader's queue.</summary>
    private async Task DispatchAsync(CancellationToken token)
    {
        while (true)
        {
            await _queued.Available.WaitAsync(token).ConfigureAwait(false);
            var work = ProcessOneAsync(Pop(_queued), token);
            if (work.IsCompleted) continue;

            _inFlight.TryAdd(work, 0);
            _ = work.ContinueWith(static (task, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(task, out _),
                _inFlight, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>Processes a request; never throws: a request the run stops on stays unfinished.</summary>
    private async Task ProcessOneAsync(ScrapeRequest request, CancellationToken token)
    {
        Stats.ChangeInProgress(1);
        try
        {
            await ProcessAsync(request, token).ConfigureAwait(false);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // the run is stopping: whatever the request threw, it stays unfinished
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Internal error on {Url}", request.Url);
            Note(new Trouble(ReasonKind.HandlerError, $"internal error: {ex.GetType().Name}", ex.Message), request, failed: true);
            Fail(request, FailureReason.HandlerError, $"internal error: {ex.Message}", null, ex);
        }
        finally
        {
            Stats.ChangeInProgress(-1);
        }
    }

    /// <summary>A queue loader's page waits for one of MaxConcurrency handler slots; too many waiting pause the loader.</summary>
    private async Task EnterHandlerAsync(CancellationToken token)
    {
        ChangeWaitingForHandler(1);
        try
        {
            await _handlerSlots!.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            ChangeWaitingForHandler(-1);
        }
    }

    private void ChangeWaitingForHandler(int delta)
    {
        lock (_pauseLock)
        {
            _waitingForHandler += delta;
            // paused above the limit, resumed at half of it: the loader does not flap on every page
            if (!_loaderPaused && _waitingForHandler > _options.MaxLoadedNotHandled) SetLoaderPaused(true);
            else if (_loaderPaused && _waitingForHandler <= _options.MaxLoadedNotHandled / 2) SetLoaderPaused(false);
        }
    }

    private void SetLoaderPaused(bool paused)
    {
        lock (_pauseLock)
        {
            if (_loaderPaused == paused) return;
            _loaderPaused = paused;
            Logger.LogDebug(paused ? "{Waiting} loaded pages wait for handlers: the loader is paused" : "Handlers caught up ({Waiting} waiting): the loader goes on",
                _waitingForHandler);
            foreach (var loader in _queueLoaders)
            {
                try { loader.Paused = paused; }
                catch (Exception ex) { Logger.LogError(ex, "Setting Paused of the loader threw"); }
            }
        }
    }

    private async Task ProcessAsync(ScrapeRequest request, CancellationToken token)
    {
        var uri = new Uri(request.Url);
        var host = uri.Authority;

        // a request back from its host's line with a slot was checked on its way in, and goes straight to Enter:
        // the line waits for it there, so nothing on the way may end it
        if (_robots != null && !request.HasThrottleSlot)
        {
            var robots = await _robots.GetAsync(uri).ConfigureAwait(false);
            if (!robots.IsAllowed(uri, _options.RobotsUserAgent))
            {
                Note(new Trouble(ReasonKind.DisallowedByRobotsTxt, "disallowed", "disallowed by robots.txt"), request, failed: true);
                Fail(request, FailureReason.DisallowedByRobotsTxt, "disallowed by robots.txt", null, null);
                return;
            }
            if (robots.CrawlDelay(_options.RobotsUserAgent) is { } crawlDelay) _throttle.SetBaseDelay(host, crawlDelay);
        }

        // a kept page needs no load: it skips the host's line and the rate limit. The cache is asked once per request (a request back
        // from its host's line asked on its way in), so the retries of a request go to its loader
        PageResponse? cached = null;
        if (_cache != null && !request.SkipCache && !request.HasThrottleSlot)
        {
            request.SkipCache = true;
            cached = await LoadFromCacheAsync(request, token).ConfigureAwait(false);
        }
        var fromCache = cached != null;

        // a host spaced or at its limit: the request waits in the host's line and comes back through the queue when its slot comes.
        // A load let go here gives its slot back as soon as the loader answers (Exit)
        if (!fromCache && !_throttle.Enter(host, request)) return;

        if (!fromCache) await WaitForRateSlotAsync(token).ConfigureAwait(false);

        var loader = _scraper.LoaderFor(request.Label);
        var queued = loader is IQueuePageLoader;
        request.Attempts++;
        if (fromCache) Stats.AddFromCache();
        else Stats.AddLoad();

        PageResponse? response = cached;
        var inHandlerSlot = false;
        try
        {
            ValidationResult verdict;
            Exception? loadError = null;
            Trouble? loadTrouble = null;

            if (fromCache)
            {
                verdict = ResponseValidator.Validate(request, response!, GetRules(request.Label));
                if (verdict.Outcome != ValidationOutcome.Ok)
                {
                    // the kept page is not the page any more (the rules changed since it was kept): it was no attempt
                    await SnapshotAsync(request, response, KindOf(verdict.Outcome), verdict.Reason!, null, fromCache, token).ConfigureAwait(false);
                    await LoadAgainAsync(request, $"the kept page failed the checks: {verdict.Reason}", token).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                // a queue loader's load may wait in its queue for any time; the loader finds lost loads itself
                if (_options.RequestTimeout != Timeout.InfiniteTimeSpan && !queued) timeout.CancelAfter(_options.RequestTimeout);
                try
                {
                    try
                    {
                        response = await loader.LoadAsync(request, timeout.Token).ConfigureAwait(false)
                                   ?? throw new InvalidOperationException($"{loader.GetType().Name}.LoadAsync returned null");
                    }
                    finally
                    {
                        _throttle.Exit(host);       // the host's slot: the handler does not hold it
                    }
                    verdict = ResponseValidator.Validate(request, response, GetRules(request.Label));
                }
                catch (NonRetryableException ex) when (!token.IsCancellationRequested)
                {
                    Note(new Trouble(ReasonKind.NonRetryable, Short(ex.Message), ex.Message), request, failed: true);
                    Fail(request, FailureReason.NonRetryable, ex.Message, response?.StatusCode, ex);
                    return;
                }
                catch (RemoteLoadException ex) when (ex.RateLimited && !token.IsCancellationRequested)
                {
                    // no page for a handler: the retry is scheduled at once, without a handler slot
                    RetryRateLimited(request, ex);
                    return;
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    loadError = ex;
                    var timedOut = ex is OperationCanceledException && !queued;
                    if (timedOut) ReportTimeout(loader, request);
                    loadTrouble = LoadTrouble(ex, timedOut);
                    verdict = timedOut
                        ? ValidationResult.Retry($"timeout after {_options.RequestTimeout.TotalSeconds:0.###} s")
                        : ValidationResult.Retry($"load error: {ex.Message}");
                }
            }

            if (queued)
            {
                await EnterHandlerAsync(token).ConfigureAwait(false);
                inHandlerSlot = true;
            }

            // a kept page is no answer of the host: it does not change the host's pace
            if (_options.AutoThrottle && response != null && !fromCache)
            {
                if (verdict.Outcome == ValidationOutcome.Blocked || response.StatusCode is 429 or 503)
                {
                    _throttle.OnOverloaded(host, RetryAfter(response), verdict.Reason ?? $"status {response.StatusCode}");
                }
                else if (verdict.Outcome == ValidationOutcome.Ok)
                {
                    _throttle.OnSuccess(host);
                }
            }

            switch (verdict.Outcome)
            {
                case ValidationOutcome.Ok:
                    var (handled, items) = await HandleAsync(loader, request, response!, host, fromCache, token).ConfigureAwait(false);
                    // a page its handler took is kept while it is still here (a page from the cache is kept already)
                    if (handled && !fromCache) await StoreInCacheAsync(request, response!, token).ConfigureAwait(false);
                    // the handler is done with the page (its parser is disposed): the text goes back to the pool,
                    // and the page must not wait in memory for a slow consumer
                    response!.ReleaseText();
                    response = null;
                    if (handled) await DeliverAsync(request, items, token).ConfigureAwait(false);
                    break;

                case ValidationOutcome.NotFound:
                    await SnapshotAsync(request, response, SnapshotKind.NotFound, verdict.Reason!, null, fromCache, token).ConfigureAwait(false);
                    request.NotFoundCount++;
                    Note(new Trouble(ReasonKind.NotFound, verdict.Reason!), request, failed: request.NotFoundCount > _options.NotFoundRetries);
                    if (request.NotFoundCount <= _options.NotFoundRetries)
                    {
                        ScheduleRetry(request, verdict.Reason!, null, request.Attempts);
                    }
                    else
                    {
                        Fail(request, FailureReason.NotFound, verdict.Reason!, response?.StatusCode, null);
                    }
                    break;

                case ValidationOutcome.Blocked:
                    await SnapshotAsync(request, response, SnapshotKind.Blocked, verdict.Reason!, null, fromCache, token).ConfigureAwait(false);
                    ReportBlocked(loader, request, response, verdict.Reason!);
                    RetryOrFail(request, new Trouble(ReasonKind.Blocked, verdict.Reason!), $"blocked: {verdict.Reason}", response, null);
                    break;

                default:
                    // a load that brought no page has nothing to show
                    await SnapshotAsync(request, response, SnapshotKind.Retry, verdict.Reason!, loadError, fromCache, token).ConfigureAwait(false);
                    // an answer that is not the page (429 / 503 are the site overloaded, the throttle's business)
                    if (response != null && loadError == null && response.StatusCode is not (429 or 503))
                    {
                        ReportBadResponse(loader, request, response, verdict.Reason!);
                    }
                    RetryOrFail(request, loadTrouble ?? new Trouble(ReasonKind.BadPage, verdict.Reason!), verdict.Reason!, response, loadError);
                    break;
            }
        }
        finally
        {
            response?.ReleaseText();    // a page that is retried, failed or abandoned
            if (inHandlerSlot) _handlerSlots!.Release();
        }
    }

    /// <summary>Runs the handler. Handled: its items; not handled: the request is already retried or failed.</summary>
    private async Task<(bool Handled, List<object>? Items)> HandleAsync(IPageLoader loader, ScrapeRequest request, PageResponse response, string host,
        bool fromCache, CancellationToken token)
    {
        var handler = _scraper.FindRoute(request.Label)!.Handler;
        var context = new ScrapeContext(this, request, response, token);
        try
        {
            await handler(context).ConfigureAwait(false);
            if (!fromCache) ReportGoodResponse(loader, request, response);     // the loader hears about its own pages only
            NoteHandlerSuccess(request.Label, host);
            return (true, context.Complete());
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            context.Complete();     // items of a failed handler are dropped: the page is retried or failed as a whole
            switch (ex)
            {
                case RetryRequestException retry:
                    await SnapshotAsync(request, response, retry.Blocked ? SnapshotKind.Blocked : SnapshotKind.Retry, $"handler: {retry.Message}", retry,
                        fromCache, token).ConfigureAwait(false);
                    if (fromCache)
                    {
                        // the handler does not want the kept page: it was no attempt
                        await LoadAgainAsync(request, $"the handler asked to retry the kept page: {retry.Message}", token).ConfigureAwait(false);
                        break;
                    }
                    if (retry.Blocked)
                    {
                        ReportBlocked(loader, request, response, retry.Message);
                        if (_options.AutoThrottle) _throttle.OnOverloaded(host, null, $"handler: {retry.Message}");
                    }
                    else
                    {
                        ReportBadResponse(loader, request, response, $"handler: {retry.Message}");
                    }
                    RetryOrFail(request, new Trouble(retry.Blocked ? ReasonKind.Blocked : ReasonKind.BadPage, $"handler: {Short(retry.Message)}", retry.Message),
                        $"handler asked to retry: {retry.Message}", response, retry);
                    break;
                case NonRetryableException nonRetryable:
                    await SnapshotAsync(request, response, SnapshotKind.HandlerError, nonRetryable.Message, nonRetryable, fromCache, token).ConfigureAwait(false);
                    Note(new Trouble(ReasonKind.NonRetryable, Short(nonRetryable.Message), nonRetryable.Message), request, failed: true);
                    Fail(request, FailureReason.NonRetryable, nonRetryable.Message, response.StatusCode, nonRetryable);
                    break;
                default:
                    await SnapshotAsync(request, response, SnapshotKind.HandlerError, ex.Message, ex, fromCache, token).ConfigureAwait(false);
                    NoteHandlerError(request, host, ex);
                    var trouble = new Trouble(ReasonKind.HandlerError, ex.GetType().Name, ex.Message);
                    if (_options.RetryOnHandlerError)
                    {
                        Logger.LogDebug(ex, "Handler \"{Label}\" failed on {Url}", request.Label, request.Url);
                        RetryOrFail(request, trouble, $"handler error: {ex.Message}", response, ex, FailureReason.HandlerError);
                    }
                    else
                    {
                        Note(trouble, request, failed: true);
                        Fail(request, FailureReason.HandlerError, ex.Message, response.StatusCode, ex);
                    }
                    break;
            }
            return (false, null);
        }
        finally
        {
            context.Complete();     // also when the run stops: the parser is disposed before the page text is released
        }
    }

    /// <summary>Hands the items of a handled page to the consumer and finishes the request.</summary>
    private async Task DeliverAsync(ScrapeRequest request, List<object>? items, CancellationToken token)
    {
        if (items is not { Count: > 0 })
        {
            Forget(request);
        }
        else
        {
            // with a state file the page stays there until the consumer has taken all its items
            var tracked = _statePath != null ? request : null;
            if (tracked != null) Volatile.Write(ref tracked.UnackedItems, items.Count);
            foreach (var item in items)
            {
                await _items.Writer.WriteAsync(new Emitted(item, tracked), token).ConfigureAwait(false);
                Stats.AddItem();
            }
        }

        Stats.AddSucceeded();
        Finish();
    }

    private void ReportBlocked(IPageLoader loader, ScrapeRequest request, PageResponse? response, string reason)
    {
        Stats.AddBlocked();
        if (loader is IBlockAwarePageLoader blockAware && response != null)
        {
            try { blockAware.ReportBlocked(request, response, reason); }
            catch (Exception ex) { Logger.LogError(ex, "ReportBlocked threw"); }
        }
    }

    private void ReportBadResponse(IPageLoader loader, ScrapeRequest request, PageResponse response, string reason)
    {
        if (loader is not IBlockAwarePageLoader blockAware) return;
        try { blockAware.ReportBadResponse(request, response, reason); }
        catch (Exception ex) { Logger.LogError(ex, "ReportBadResponse threw"); }
    }

    private void ReportTimeout(IPageLoader loader, ScrapeRequest request)
    {
        if (loader is not IBlockAwarePageLoader blockAware) return;
        try { blockAware.ReportTimeout(request); }
        catch (Exception ex) { Logger.LogError(ex, "ReportTimeout threw"); }
    }

    private void ReportGoodResponse(IPageLoader loader, ScrapeRequest request, PageResponse response)
    {
        if (loader is not IBlockAwarePageLoader blockAware) return;
        try { blockAware.ReportGoodResponse(request, response); }
        catch (Exception ex) { Logger.LogError(ex, "ReportGoodResponse threw"); }
    }

    // ---------------------------------------------------------------- cache and snapshots

    /// <summary>The page the cache keeps for the request, or null; a cache that throws is logged and the request is loaded.</summary>
    private async Task<PageResponse?> LoadFromCacheAsync(ScrapeRequest request, CancellationToken token)
    {
        try
        {
            return await _cache!.LoadAsync(request, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Logger.LogWarning(ex, "The cache could not give {Url}: it is loaded", request.Url);
            return null;
        }
    }

    /// <summary>Offers the cache a page its handler took; the page is still here (its text is not released yet).</summary>
    private async Task StoreInCacheAsync(ScrapeRequest request, PageResponse response, CancellationToken token)
    {
        if (_cache == null) return;
        try
        {
            await _cache.StoreAsync(request, response, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Logger.LogWarning(ex, "The cache could not keep {Url}", request.Url);
        }
    }

    /// <summary>
    /// A page from the cache was not the page: the cache forgets it, and the request goes to its loader at once, as if the cache had not
    /// had it. The kept page was no attempt, so it takes no retry and waits for no retry delay.
    /// </summary>
    private async Task LoadAgainAsync(ScrapeRequest request, string reason, CancellationToken token)
    {
        Logger.LogDebug("{Url} is loaded again: {Reason}", request.Url, reason);
        try
        {
            await _cache!.RemoveAsync(request, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Logger.LogWarning(ex, "The cache could not forget {Url}", request.Url);
        }
        request.Attempts--;
        Push(request);
    }

    /// <summary>Gives a page that was not taken to the snapshots; a load that brought no page has none.</summary>
    private async Task SnapshotAsync(ScrapeRequest request, PageResponse? response, SnapshotKind kind, string reason, Exception? exception, bool fromCache,
        CancellationToken token)
    {
        if (_snapshots == null || response == null) return;
        try
        {
            await _snapshots.SaveAsync(new PageSnapshot(request, response, kind, reason, exception, fromCache), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Logger.LogWarning(ex, "The snapshot of {Url} could not be saved", request.Url);
        }
    }

    private static SnapshotKind KindOf(ValidationOutcome outcome) => outcome switch
    {
        ValidationOutcome.Blocked => SnapshotKind.Blocked,
        ValidationOutcome.NotFound => SnapshotKind.NotFound,
        _ => SnapshotKind.Retry,
    };

    private void RetryOrFail(ScrapeRequest request, Trouble trouble, string reason, PageResponse? response, Exception? error,
        FailureReason reasonWhenOut = FailureReason.RetriesExhausted)
    {
        var maxRetries = request.MaxRetries ?? _options.MaxRetries;
        Note(trouble, request, failed: request.Attempts > maxRetries);
        if (request.Attempts <= maxRetries)
        {
            // the server's time: the Retry-After of the response, or of the service that had no page
            ScheduleRetry(request, reason, RetryAfter(response) ?? (error as RemoteLoadException)?.RetryAfter, request.Attempts);
        }
        else
        {
            var refused = request.RateLimitCount > 0 ? $", {request.RateLimitCount} more loads refused for the service's limits" : "";
            Fail(request, reasonWhenOut, $"{request.Attempts} attempts{refused}, last: {reason}", response?.StatusCode, error);
        }
    }

    /// <summary>
    /// A scraping service refused the load for its limits: the page was not tried. It is loaded again after the service's Retry-After,
    /// and the retry takes none of its attempts, up to <see cref="ScraperOptions.RateLimitRetries"/> refusals of the request; past them
    /// a refusal is an attempt like a load error, so the request ends even when the service refuses every call.
    /// </summary>
    private void RetryRateLimited(ScrapeRequest request, RemoteLoadException refusal)
    {
        Stats.AddRateLimited();
        var trouble = new Trouble(ReasonKind.RateLimited, Short(refusal.Message), refusal.Message);
        var reason = $"rate limited: {refusal.Message}";
        if (request.RateLimitCount >= _options.RateLimitRetries)
        {
            RetryOrFail(request, trouble, reason, null, refusal);
            return;
        }

        request.Attempts--;
        request.RateLimitCount++;
        Note(trouble, request, failed: false);
        // the wait grows with the refusals of the request, not with its attempts
        ScheduleRetry(request, reason, refusal.RetryAfter, request.RateLimitCount);
    }

    /// <summary>
    /// Loads the request again after the retry delay of <paramref name="step"/> (its attempts, or its refusals for a service's limits),
    /// or after the server's time when that is longer, up to <see cref="ScraperOptions.MaxRetryDelay"/>.
    /// </summary>
    private void ScheduleRetry(ScrapeRequest request, string reason, TimeSpan? retryAfter, int step)
    {
        Stats.AddRetry();

        var delay = RetryDelay(step);
        if (retryAfter is { } serverDelay)
        {
            // the server knows best, within MaxRetryDelay
            var capped = serverDelay < _options.MaxRetryDelay ? serverDelay : _options.MaxRetryDelay;
            if (capped > delay) delay = capped;
        }
        Logger.LogDebug("{Url} is loaded again in {DelayMs} ms, after {Attempts} attempts: {Reason}", request.Url, (int)delay.TotalMilliseconds,
            request.Attempts, reason);

        if (delay <= TimeSpan.Zero)
        {
            Push(request);
            return;
        }

        _ = Task.Delay(delay, _stop.Token).ContinueWith(
            static (task, state) => { if (task.IsCompletedSuccessfully) ((Action)state!)(); },
            () => Push(request),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal TimeSpan RetryDelay(int attempts)
    {
        if (_options.RetryDelay <= TimeSpan.Zero) return TimeSpan.Zero;

        var ms = _options.RetryDelay.TotalMilliseconds * Math.Pow(_options.RetryBackoffFactor, Math.Max(0, attempts - 1));
        ms = Math.Min(ms, _options.MaxRetryDelay.TotalMilliseconds);
        ms *= 0.8 + Random.Shared.NextDouble() * 0.4;     // jitter, so retries of many pages do not hit the site at once
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>Retry-After header: seconds or an HTTP date.</summary>
    internal static TimeSpan? RetryAfter(PageResponse? response)
    {
        if (response == null || !response.Headers.TryGetValue("Retry-After", out var value) || string.IsNullOrWhiteSpace(value)) return null;

        value = value.Trim();
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return TimeSpan.FromSeconds(seconds);
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    /// <summary>Keeps loads <see cref="ScraperOptions.MaxRequestsPerMinute"/> apart: each load takes the next free slot.</summary>
    private Task WaitForRateSlotAsync(CancellationToken token)
    {
        if (_rateInterval == 0) return Task.CompletedTask;

        long wait;
        lock (_rateLock)
        {
            var now = Stopwatch.GetTimestamp();
            var slot = Math.Max(now, _nextRateSlot);
            _nextRateSlot = slot + _rateInterval;
            wait = slot - now;
        }
        return wait <= 0 ? Task.CompletedTask : Task.Delay(Stopwatch.GetElapsedTime(0, wait), token);
    }

    private void Fail(ScrapeRequest request, FailureReason reason, string message, int? status, Exception? error)
    {
        RecordFailure(new FailedRequest(request, reason, message, status, error));
        ForgetFailed(request, reason);
        Finish();
    }

    /// <summary>Counts, keeps, logs and reports a failed request.</summary>
    private void RecordFailure(FailedRequest failed)
    {
        Stats.AddFailed();
        if (failed.Reason == FailureReason.NotFound) Stats.AddNotFound();
        _failed.Enqueue(failed);

        // a missing or disallowed page is usual business; a handler error needs its stack trace
        var level = failed.Reason is FailureReason.NotFound or FailureReason.DisallowedByRobotsTxt ? LogLevel.Information : LogLevel.Warning;
        Logger.Log(level, failed.Reason == FailureReason.HandlerError ? failed.Exception : null,
            "Failed {Url} ({Label}): {Reason}, {Message}", failed.Request.Url, failed.Request.Label, failed.Reason, failed.Message);

        _scraper.RaiseRequestFailed(failed);
    }

    // ---------------------------------------------------------------- reasons

    /// <summary>
    /// Why an attempt gave no page, as <see cref="ScraperStats.Reasons"/> groups it: a reason that is the same for alike attempts, and
    /// the message of this one.
    /// </summary>
    private readonly record struct Trouble(ReasonKind Kind, string Reason, string Example)
    {
        public Trouble(ReasonKind kind, string reason) : this(kind, reason, reason) { }
    }

    /// <summary>Counts an attempt that gave no page in its group of <see cref="ScraperStats.Reasons"/>; failed: it was the last one.</summary>
    private void Note(Trouble trouble, ScrapeRequest request, bool failed)
    {
        var host = Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ? uri.Authority : "";
        Stats.AddReason(trouble.Kind, trouble.Reason, request.Label, host, failed, trouble.Example, request.Url);
    }

    /// <summary>A load that threw: a timeout, or a load error by its kind, not by its message (which names the proxy, the IP...).</summary>
    private Trouble LoadTrouble(Exception error, bool timedOut) => timedOut
        ? new Trouble(ReasonKind.Timeout, string.Create(CultureInfo.InvariantCulture, $"{_options.RequestTimeout.TotalSeconds:0.###} s"), error.Message)
        : new Trouble(ReasonKind.LoadError, LoadErrorKind(error), error.Message);

    /// <summary>
    /// The kind of a load error, the same for every load that failed alike: what <see cref="HttpRequestException"/> says went wrong
    /// (ConnectionError, ProxyTunnelError, SecureConnectionError...), the socket error, or the type of the exception.
    /// </summary>
    internal static string LoadErrorKind(Exception error)
    {
        for (var e = error; e != null; e = e.InnerException)
        {
            switch (e)
            {
                case HttpRequestException { HttpRequestError: not HttpRequestError.Unknown } http:
                    return http.HttpRequestError.ToString();
                case SocketException socket:
                    return $"socket {socket.SocketErrorCode}";
            }
        }
        return error.GetType().Name;
    }

    /// <summary>The first line of a message, at most 100 characters: a reason made of a message stays short.</summary>
    private static string Short(string message)
    {
        var line = message.AsSpan();
        var end = line.IndexOfAny('\r', '\n');
        if (end >= 0) line = line[..end];
        line = line.Trim();
        return line.Length <= 100 ? line.ToString() : string.Concat(line[..99], "…");
    }

    // ---------------------------------------------------------------- broken handlers

    /// <summary>
    /// Handler errors in a row of one label on one host (<see cref="ScraperOptions.MaxConsecutiveHandlerErrors"/>), and the requests they
    /// failed: when the handler turns out broken, those go back to the unfinished ones, so the run continued after the fix does them again.
    /// </summary>
    private sealed class HandlerErrors
    {
        public int InARow;
        public bool Broken;
        public readonly List<ScrapeRequest> Failed = [];
    }

    // guarded by lock (_handlerErrors); a label and a host have an entry only while their handler errors go on
    private readonly Dictionary<string, HandlerErrors> _handlerErrors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Label and host in one key: a host has no '@', so the last one splits them.</summary>
    private static string HandlerKey(string label, string host) => $"{label}@{host}";

    /// <summary>The handler of the label took a page of the host: its errors in a row start again.</summary>
    private void NoteHandlerSuccess(string label, string host)
    {
        if (_options.MaxConsecutiveHandlerErrors == null) return;
        lock (_handlerErrors)
        {
            var key = HandlerKey(label, host);
            if (_handlerErrors.TryGetValue(key, out var errors) && !errors.Broken) _handlerErrors.Remove(key);
        }
    }

    /// <summary>
    /// The handler threw an exception of its own. After <see cref="ScraperOptions.MaxConsecutiveHandlerErrors"/> of them in a row of its label on
    /// the host the handler is broken: the run stops at once, before more pages are loaded only to fail.
    /// </summary>
    private void NoteHandlerError(ScrapeRequest request, string host, Exception error)
    {
        if (_options.MaxConsecutiveHandlerErrors is not { } max) return;
        int count;
        lock (_handlerErrors)
        {
            var key = HandlerKey(request.Label, host);
            if (!_handlerErrors.TryGetValue(key, out var errors)) _handlerErrors[key] = errors = new HandlerErrors();
            count = ++errors.InARow;
            if (errors.Broken || count < max) return;

            errors.Broken = true;
            // the requests it failed so far left the state when they failed: they go back, the next run does them again
            if (_statePath != null)
            {
                foreach (var failed in errors.Failed) _unsaved.TryAdd(failed, 0);
            }
            errors.Failed.Clear();
        }

        Stats.StoppedByHandlerErrors = true;
        Logger.LogError(error, "The handler of \"{Label}\" failed {Count} times in a row on {Host}, the last time on {Url}: the run stops (MaxConsecutiveHandlerErrors)",
            request.Label, count, host, request.Url);
        _ = _stop.CancelAsync();      // the workers stop taking requests now; the run ends as after MaxRunDuration
    }

    /// <summary>
    /// A failed request leaves the state file, unless its handler is broken: then the next run does it again. A request failed by a handler
    /// that may still turn out broken is remembered until the handler takes a page.
    /// </summary>
    private void ForgetFailed(ScrapeRequest request, FailureReason reason)
    {
        if (reason == FailureReason.HandlerError && _options.MaxConsecutiveHandlerErrors != null && _statePath != null
            && Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            lock (_handlerErrors)
            {
                if (_handlerErrors.TryGetValue(HandlerKey(request.Label, uri.Authority), out var errors))
                {
                    if (errors.Broken) return;
                    errors.Failed.Add(request);
                }
                Forget(request);      // under the lock: a handler found broken meanwhile puts it back
                return;
            }
        }
        Forget(request);
    }

    private EffectiveRules GetRules(string label) => _rules.GetOrAdd(label, _options.Rules.Build);

    private async Task ProgressAsync(CancellationToken token)
    {
        if (_options.ProgressInterval is not { } interval || interval <= TimeSpan.Zero) return;

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            Logger.LogInformation("Scraping: {Stats}", Stats);
        }
    }

    // ---------------------------------------------------------------- state file

    private void RestoreState(string path)
    {
        RunState.Saved run;
        lock (_seen)
        {
            run = RunState.Load(path, _codec!, _seen);
            // a file of version 1 kept the keys themselves: the first save puts their hashes into the journal
            if (run.Stored == 0) _seenUnsaved.AddRange(_seen);
        }
        _seenStored = run.Stored;

        var outOfScope = 0;
        foreach (var saved in run.Pending)
        {
            if (!UrlHelper.TryNormalizeUri(saved.Url, out var uri))
            {
                Logger.LogWarning("Skipped a request with a bad URL in the state file: {Url}", saved.Url);
                continue;
            }
            if (_scraper.FindRoute(saved.Label) == null)
            {
                throw new InvalidOperationException($"The state file {path} has requests with label \"{saved.Label}\", and there is no handler for it");
            }
            // found on a page: a scope narrowed since the earlier run drops it, and the next save leaves it out
            if (saved.Depth > 0 && !_scope.Allows(_scope.Clean(uri)))
            {
                Stats.AddOutOfScope();
                outOfScope++;
                continue;
            }

            // taken over as accepted: no duplicate check (its key is in the seen list) and no limits (an earlier run accepted it)
            var request = saved with { Url = uri.AbsoluteUri };
            lock (_seen)
            {
                _accepted++;
                _unsaved.TryAdd(request, 0);
                Interlocked.Increment(ref _pending);
            }
            Stats.AddEnqueued();
            Stats.AddRestored();
            Push(request);
        }

        if (outOfScope > 0)
        {
            Logger.LogInformation("{Count} requests of the state file {Path} are out of the crawl scope now (AllowedHosts, UrlFilter): they are dropped", outOfScope, path);
        }
        Logger.LogInformation("Continuing the run saved in {Path}: {Pending} requests to finish, {Seen} pages seen", path, Stats.Restored, _seen.Count);
    }

    private async Task SaveLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(_options.StateSaveInterval);
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            await SaveStateAsync().ConfigureAwait(false);
        }
    }

    private async Task SaveStateAsync()
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // one consistent picture: the pages seen and the requests not finished at one moment
            KeyHash[] seen;
            ScrapeRequest[] pending;
            lock (_seen)
            {
                seen = [.. _seenUnsaved];
                pending = [.. _unsaved.Keys];
            }
            var stored = _seenStored;
            await Task.Run(() => RunState.Save(_statePath!, stored, seen, pending, _codec!)).ConfigureAwait(false);

            // in the journal now; the hashes seen meanwhile wait for the next save
            lock (_seen) _seenUnsaved.RemoveRange(0, seen.Length);
            _seenStored = stored + seen.Length;
            Logger.LogDebug("State saved to {Path}: {Pending} pending, {Seen} seen", _statePath, pending.Length, _seenStored);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The state could not be saved to {Path}", _statePath);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// The workers stopped: save what is left to do, pages whose items the consumer has not taken yet included.
    /// The file is deleted by <see cref="CompleteState"/> once the consumer has taken everything.
    /// </summary>
    private async Task FinishStateAsync()
    {
        if (_unsaved.IsEmpty)
        {
            DeleteState();
            return;
        }
        await SaveStateAsync().ConfigureAwait(false);
        if (Volatile.Read(ref _pending) > 0)
        {
            Logger.LogInformation("{Pending} requests not finished are saved in {Path}: the next run continues them", Volatile.Read(ref _pending), _statePath);
        }
    }

    /// <summary>The consumer finished the loop: when nothing is left to do or to take, the state file is not needed any more.</summary>
    public void CompleteState()
    {
        if (_statePath != null && _unsaved.IsEmpty) DeleteState();
    }

    private void DeleteState()
    {
        try
        {
            RunState.Delete(_statePath!);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The state file {Path} of the finished run or its journal could not be deleted", _statePath);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await IgnoreCancellation(Completion).ConfigureAwait(false);
        _stop.Dispose();
    }
}
