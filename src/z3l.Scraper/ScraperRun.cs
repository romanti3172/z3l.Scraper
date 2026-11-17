using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
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
internal sealed class ScraperRun : IAsyncDisposable
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
    // _unsaved holds, with a state file only, the requests not finished plus the finished ones whose items the consumer has not taken:
    // if the process dies, all of them are done again (at least once)
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ScrapeRequest, byte> _unsaved = new(ReferenceEqualityComparer.Instance);
    private int _accepted;

    private readonly ConcurrentDictionary<string, EffectiveRules> _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<FailedRequest> _failed = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pending;
    private int _requestLimitLogged, _depthLimitLogged;

    private readonly object _rateLock = new();
    private readonly long _rateInterval;     // Stopwatch ticks between load starts, 0 for no limit
    private long _nextRateSlot;

    private readonly HostThrottle _throttle;
    private readonly RobotsCache? _robots;
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
        _throttle = new HostThrottle(_options.MaxThrottleDelay, Push, scraper.Logger, _stop.Token);
        if (_options.RespectRobotsTxt)
        {
            _robots = new RobotsCache(scraper.Loader, _options, scraper.Logger, _stop.Token);
        }
        _statePath = string.IsNullOrWhiteSpace(_options.StatePath) ? null : _options.StatePath;
        if (_statePath != null) _codec = new StateDataCodec(scraper.FindRoute, _options.StateJson);
        _queueLoaders = [.. scraper.Loaders.OfType<IQueuePageLoader>()];
        _hasLocalLoader = scraper.Loaders.Any(x => x is not IQueuePageLoader);
        if (_queueLoaders.Length > 0) _handlerSlots = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
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
        if (Volatile.Read(ref _pending) == 0) _drained.TrySetResult();

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

            var finished = await Task.WhenAny(_drained.Task, timeLimit).ConfigureAwait(false);
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
            Logger.LogInformation("Scraping finished: {Stats}", Stats);
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
    /// False for a duplicate, a request over the limits, or a bad URL from a handler; a bad start URL throws.
    /// </summary>
    public bool Enqueue(ScrapeRequest request, ScrapeRequest? parent)
    {
        if (!UrlHelper.TryNormalize(request.Url, out var url))
        {
            if (parent != null) return false;
            throw new ArgumentException($"A start request needs an absolute http(s) URL: \"{request.Url}\"", nameof(request));
        }
        var route = _scraper.FindRoute(request.Label)
                    ?? throw new InvalidOperationException($"No handler for label \"{request.Label}\": register it with Route(\"{request.Label}\", handler) or add FallbackRoute");
        if (route.DataType != null && !route.DataType.IsInstanceOfType(request.Data))
        {
            throw Scraper.WrongData(request.Label, route.DataType, request.Data);     // where the mistake is, not later in the handler
        }
        ScrapeContext.ThrowIfPageBound(request.Data, nameof(request));
        _codec?.Check(request.Label, request.Data);

        var depth = parent == null ? 0 : parent.Depth + 1;
        // own copy: the caller may reuse its instance
        request = request with { Url = url, Attempts = 0, NotFoundCount = 0, Depth = depth, HasThrottleSlot = false };
        var key = request.SkipDuplicateCheck ? null : request.GetUniqueKey();

        lock (_seen)
        {
            if (key != null && _seen.Contains(key))
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

            if (key != null) _seen.Add(key);
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
    private void Finish()
    {
        if (Interlocked.Decrement(ref _pending) == 0) _drained.TrySetResult();
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
                Fail(request, FailureReason.DisallowedByRobotsTxt, "disallowed by robots.txt", null, null);
                return;
            }
            if (robots.CrawlDelay(_options.RobotsUserAgent) is { } crawlDelay) _throttle.SetBaseDelay(host, crawlDelay);
        }

        // a throttled host: the request waits in the host's line and comes back through the queue when its slot comes
        if (!_throttle.Enter(host, request)) return;

        await WaitForRateSlotAsync(token).ConfigureAwait(false);

        var loader = _scraper.LoaderFor(request.Label);
        var queued = loader is IQueuePageLoader;
        request.Attempts++;
        Stats.AddLoad();

        PageResponse? response = null;
        var inHandlerSlot = false;
        try
        {
            ValidationResult verdict;
            Exception? loadError = null;

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                // a queue loader's load may wait in its queue for any time; the loader finds lost loads itself
                if (_options.RequestTimeout != Timeout.InfiniteTimeSpan && !queued) timeout.CancelAfter(_options.RequestTimeout);
                try
                {
                    response = await loader.LoadAsync(request, timeout.Token).ConfigureAwait(false)
                               ?? throw new InvalidOperationException($"{loader.GetType().Name}.LoadAsync returned null");
                    verdict = ResponseValidator.Validate(request, response, GetRules(request.Label));
                }
                catch (NonRetryableException ex) when (!token.IsCancellationRequested)
                {
                    Fail(request, FailureReason.NonRetryable, ex.Message, response?.StatusCode, ex);
                    return;
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    loadError = ex;
                    var timedOut = ex is OperationCanceledException && !queued;
                    if (timedOut) ReportTimeout(loader, request);
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

            if (_options.AutoThrottle && response != null)
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
                    var (handled, items) = await HandleAsync(loader, request, response!, host, token).ConfigureAwait(false);
                    // the handler is done with the page (its parser is disposed): the text goes back to the pool,
                    // and the page must not wait in memory for a slow consumer
                    response!.ReleaseText();
                    response = null;
                    if (handled) await DeliverAsync(request, items, token).ConfigureAwait(false);
                    break;

                case ValidationOutcome.NotFound:
                    request.NotFoundCount++;
                    if (request.NotFoundCount <= _options.NotFoundRetries)
                    {
                        ScheduleRetry(request, verdict.Reason!, null);
                    }
                    else
                    {
                        Fail(request, FailureReason.NotFound, verdict.Reason!, response?.StatusCode, null);
                    }
                    break;

                case ValidationOutcome.Blocked:
                    ReportBlocked(loader, request, response, verdict.Reason!);
                    RetryOrFail(request, $"blocked: {verdict.Reason}", response, null);
                    break;

                default:
                    // an answer that is not the page (429 / 503 are the site overloaded, the throttle's business)
                    if (response != null && loadError == null && response.StatusCode is not (429 or 503))
                    {
                        ReportBadResponse(loader, request, response, verdict.Reason!);
                    }
                    RetryOrFail(request, verdict.Reason!, response, loadError);
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
    private async Task<(bool Handled, List<object>? Items)> HandleAsync(IPageLoader loader, ScrapeRequest request, PageResponse response, string host, CancellationToken token)
    {
        var handler = _scraper.FindRoute(request.Label)!.Handler;
        var context = new ScrapeContext(this, request, response, token);
        try
        {
            await handler(context).ConfigureAwait(false);
            ReportGoodResponse(loader, request, response);
            return (true, context.Complete());
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            context.Complete();     // items of a failed handler are dropped: the page is retried or failed as a whole
            switch (ex)
            {
                case RetryRequestException retry:
                    if (retry.Blocked)
                    {
                        ReportBlocked(loader, request, response, retry.Message);
                        if (_options.AutoThrottle) _throttle.OnOverloaded(host, null, $"handler: {retry.Message}");
                    }
                    else
                    {
                        ReportBadResponse(loader, request, response, $"handler: {retry.Message}");
                    }
                    RetryOrFail(request, $"handler asked to retry: {retry.Message}", response, retry);
                    break;
                case NonRetryableException nonRetryable:
                    Fail(request, FailureReason.NonRetryable, nonRetryable.Message, response.StatusCode, nonRetryable);
                    break;
                default:
                    if (_options.RetryOnHandlerError)
                    {
                        Logger.LogDebug(ex, "Handler \"{Label}\" failed on {Url}", request.Label, request.Url);
                        RetryOrFail(request, $"handler error: {ex.Message}", response, ex, FailureReason.HandlerError);
                    }
                    else
                    {
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

    private void RetryOrFail(ScrapeRequest request, string reason, PageResponse? response, Exception? error, FailureReason reasonWhenOut = FailureReason.RetriesExhausted)
    {
        var maxRetries = request.MaxRetries ?? _options.MaxRetries;
        if (request.Attempts <= maxRetries)
        {
            ScheduleRetry(request, reason, RetryAfter(response));
        }
        else
        {
            Fail(request, reasonWhenOut, $"{request.Attempts} attempts, last: {reason}", response?.StatusCode, error);
        }
    }

    private void ScheduleRetry(ScrapeRequest request, string reason, TimeSpan? retryAfter)
    {
        Stats.AddRetry();

        var delay = RetryDelay(request.Attempts);
        if (retryAfter is { } serverDelay)
        {
            // the server knows best, within MaxRetryDelay
            var capped = serverDelay < _options.MaxRetryDelay ? serverDelay : _options.MaxRetryDelay;
            if (capped > delay) delay = capped;
        }
        Logger.LogDebug("Retry {Attempt} of {Url} in {DelayMs} ms: {Reason}", request.Attempts, request.Url, (int)delay.TotalMilliseconds, reason);

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
        Stats.AddFailed();
        if (reason == FailureReason.NotFound) Stats.AddNotFound();

        var failed = new FailedRequest(request, reason, message, status, error);
        _failed.Enqueue(failed);

        // a missing or disallowed page is usual business; a handler error needs its stack trace
        var level = reason is FailureReason.NotFound or FailureReason.DisallowedByRobotsTxt ? LogLevel.Information : LogLevel.Warning;
        Logger.Log(level, reason == FailureReason.HandlerError ? error : null,
            "Failed {Url} ({Label}): {Reason}, {Message}", request.Url, request.Label, reason, message);

        _scraper.RaiseRequestFailed(failed);
        Forget(request);
        Finish();
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
        var snapshot = RunState.Load(path, _codec!);
        lock (_seen)
        {
            foreach (var key in snapshot.Seen) _seen.Add(key);
        }

        foreach (var saved in snapshot.Pending)
        {
            if (!UrlHelper.TryNormalize(saved.Url, out var url))
            {
                Logger.LogWarning("Skipped a request with a bad URL in the state file: {Url}", saved.Url);
                continue;
            }
            if (_scraper.FindRoute(saved.Label) == null)
            {
                throw new InvalidOperationException($"The state file {path} has requests with label \"{saved.Label}\", and there is no handler for it");
            }

            // taken over as accepted: no duplicate check (its key is in the seen list) and no limits (an earlier run accepted it)
            var request = saved with { Url = url };
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

        Logger.LogInformation("Continuing the run saved in {Path}: {Pending} requests to finish, {Seen} pages seen", path, Stats.Restored, snapshot.Seen.Count);
    }

    private RunState.Snapshot TakeSnapshot()
    {
        lock (_seen)
        {
            return new RunState.Snapshot([.. _seen], [.. _unsaved.Keys]);
        }
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
            var snapshot = TakeSnapshot();
            await Task.Run(() => RunState.Save(_statePath!, snapshot, _codec!)).ConfigureAwait(false);
            Logger.LogDebug("State saved to {Path}: {Pending} pending, {Seen} seen", _statePath, snapshot.Pending.Count, snapshot.Seen.Count);
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
            File.Delete(_statePath!);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The state file {Path} of the finished run could not be deleted", _statePath);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await IgnoreCancellation(Completion).ConfigureAwait(false);
        _stop.Dispose();
    }
}
