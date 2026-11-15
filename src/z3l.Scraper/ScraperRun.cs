using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using z3l.Scraper.Loading;
using z3l.Scraper.Validation;

namespace z3l.Scraper;

/// <summary>
/// State of one run. Every accepted request is "pending" until it is handled or failed; retries keep it pending.
/// A handler queues new pages before its own request finishes, so pending reaches zero only when all the work is done.
/// </summary>
internal sealed class ScraperRun<TItem> : IAsyncDisposable
{
    private readonly Scraper<TItem> _scraper;
    private readonly ScraperOptions _options;
    private readonly CancellationTokenSource _stop;

    private readonly object _queueLock = new();
    private readonly PriorityQueue<ScrapeRequest, (int Priority, long Sequence)> _queue = new();
    private long _sequence;
    private readonly SemaphoreSlim _available = new(0);

    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, EffectiveRules> _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<FailedRequest> _failed = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pending;

    private readonly Channel<TItem> _items;

    public ScraperRun(Scraper<TItem> scraper, CancellationToken cancellationToken)
    {
        _scraper = scraper;
        _options = scraper.Options;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _items = Channel.CreateBounded<TItem>(new BoundedChannelOptions(_options.ItemBufferSize)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    public ScraperStats Stats { get; } = new();
    public ILogger Logger => _scraper.Logger;
    public ChannelReader<TItem> Items => _items.Reader;
    public IEnumerable<FailedRequest> FailedRequests => _failed;
    public Task Completion { get; private set; } = Task.CompletedTask;

    public void Start(IEnumerable<ScrapeRequest> startRequests)
    {
        Stats.Start();
        foreach (var request in startRequests)
        {
            Enqueue(request);
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
            var workers = Enumerable.Range(0, _options.MaxConcurrency).Select(_ => Task.Run(() => WorkerAsync(token))).ToArray();
            var progress = ProgressAsync(token);
            var timeLimit = Task.Delay(_options.MaxRunDuration ?? Timeout.InfiniteTimeSpan, token);

            var finished = await Task.WhenAny(_drained.Task, timeLimit).ConfigureAwait(false);
            if (finished == timeLimit && timeLimit.IsCompletedSuccessfully)
            {
                Stats.StoppedByTimeLimit = true;
                Logger.LogWarning("Stopped after MaxRunDuration {Duration}: {Pending} requests not finished", _options.MaxRunDuration, Stats.Pending);
            }

            await _stop.CancelAsync().ConfigureAwait(false);
            await IgnoreCancellation(Task.WhenAll(workers)).ConfigureAwait(false);
            await IgnoreCancellation(progress).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
            Logger.LogError(ex, "Scraper run failed");
        }
        finally
        {
            Stats.Stop();
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

    /// <summary>Accepts a new request. False for a duplicate.</summary>
    public bool Enqueue(ScrapeRequest request)
    {
        if (_scraper.FindRoute(request.Label) == null)
        {
            throw new InvalidOperationException($"No handler for label \"{request.Label}\": register it with Route(\"{request.Label}\", handler) or add FallbackRoute");
        }

        request = request with { Attempts = 0, NotFoundCount = 0 };   // own copy: the caller may reuse its instance

        if (!request.SkipDuplicateCheck)
        {
            var key = request.GetUniqueKey();
            lock (_seen)
            {
                if (!_seen.Add(key))
                {
                    Stats.AddDuplicate();
                    return false;
                }
            }
        }

        Interlocked.Increment(ref _pending);
        Stats.AddEnqueued();
        Push(request);
        return true;
    }

    private void Push(ScrapeRequest request)
    {
        lock (_queueLock)
        {
            _queue.Enqueue(request, ((int)request.Priority, _sequence++));
        }
        Stats.ChangeQueued(1);
        _available.Release();
    }

    private ScrapeRequest Pop()
    {
        lock (_queueLock)
        {
            Stats.ChangeQueued(-1);
            return _queue.Dequeue();
        }
    }

    private void Finish()
    {
        if (Interlocked.Decrement(ref _pending) == 0) _drained.TrySetResult();
    }

    // ---------------------------------------------------------------- processing

    private async Task WorkerAsync(CancellationToken token)
    {
        while (true)
        {
            await _available.WaitAsync(token).ConfigureAwait(false);
            var request = Pop();

            Stats.ChangeInProgress(1);
            try
            {
                await ProcessAsync(request, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;     // the run is stopping, the request stays unfinished
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
    }

    private async Task ProcessAsync(ScrapeRequest request, CancellationToken token)
    {
        request.Attempts++;
        Stats.AddLoad();

        PageResponse? response = null;
        ValidationResult verdict;
        Exception? loadError = null;

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            if (_options.RequestTimeout != Timeout.InfiniteTimeSpan) timeout.CancelAfter(_options.RequestTimeout);
            try
            {
                response = await _scraper.Loader.LoadAsync(request, timeout.Token).ConfigureAwait(false)
                           ?? throw new InvalidOperationException($"{_scraper.Loader.GetType().Name}.LoadAsync returned null");
                verdict = ResponseValidator.Validate(request, response, GetRules(request.Label));
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                loadError = ex;
                verdict = ex is OperationCanceledException
                    ? ValidationResult.Retry($"timeout after {_options.RequestTimeout}")
                    : ValidationResult.Retry($"load error: {ex.Message}");
            }
        }

        var status = response?.StatusCode;

        switch (verdict.Outcome)
        {
            case ValidationOutcome.Ok:
                await HandleAsync(request, response!, token).ConfigureAwait(false);
                break;

            case ValidationOutcome.NotFound:
                request.NotFoundCount++;
                if (request.NotFoundCount <= _options.NotFoundRetries)
                {
                    ScheduleRetry(request, verdict.Reason!);
                }
                else
                {
                    Stats.AddNotFound();
                    Fail(request, FailureReason.NotFound, verdict.Reason!, status, null);
                }
                break;

            case ValidationOutcome.Blocked:
                Stats.AddBlocked();
                if (_scraper.Loader is IBlockAwarePageLoader blockAware && response != null)
                {
                    try { blockAware.ReportBlocked(request, response, verdict.Reason!); }
                    catch (Exception ex) { Logger.LogError(ex, "ReportBlocked threw"); }
                }
                RetryOrFail(request, $"blocked: {verdict.Reason}", status, null);
                break;

            default:
                RetryOrFail(request, verdict.Reason!, status, loadError);
                break;
        }
    }

    private async Task HandleAsync(ScrapeRequest request, PageResponse response, CancellationToken token)
    {
        var handler = _scraper.FindRoute(request.Label)!;
        var context = new ScrapeContext<TItem>(this, request, response, token);
        try
        {
            await handler(context).ConfigureAwait(false);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Logger.LogWarning(ex, "Handler \"{Label}\" failed on {Url}", request.Label, request.Url);
            if (_options.RetryOnHandlerError)
            {
                RetryOrFail(request, $"handler error: {ex.Message}", response.StatusCode, ex, FailureReason.HandlerError);
            }
            else
            {
                Fail(request, FailureReason.HandlerError, ex.Message, response.StatusCode, ex);
            }
            return;
        }

        Stats.AddSucceeded();
        Finish();
    }

    private void RetryOrFail(ScrapeRequest request, string reason, int? status, Exception? error, FailureReason reasonWhenOut = FailureReason.RetriesExhausted)
    {
        var maxRetries = request.MaxRetries ?? _options.MaxRetries;
        if (request.Attempts <= maxRetries)
        {
            ScheduleRetry(request, reason);
        }
        else
        {
            Fail(request, reasonWhenOut, $"{request.Attempts} attempts, last: {reason}", status, error);
        }
    }

    private void ScheduleRetry(ScrapeRequest request, string reason)
    {
        Stats.AddRetry();
        var delay = RetryDelay(request.Attempts);
        Logger.LogDebug("Retry {Attempt} of {Url} in {DelayMs} ms: {Reason}", request.Attempts, request.Url, (int)delay.TotalMilliseconds, reason);

        if (delay <= TimeSpan.Zero)
        {
            Push(request);
            return;
        }

        var token = _stop.Token;
        _ = Task.Delay(delay, token).ContinueWith(
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

    private void Fail(ScrapeRequest request, FailureReason reason, string message, int? status, Exception? error)
    {
        Stats.AddFailed();
        var failed = new FailedRequest(request, reason, message, status, error);
        _failed.Enqueue(failed);
        Logger.LogWarning("Failed {Url} ({Label}): {Reason}, {Message}", request.Url, request.Label, reason, message);
        _scraper.RaiseRequestFailed(failed);
        Finish();
    }

    private EffectiveRules GetRules(string label) => _rules.GetOrAdd(label, _options.Rules.Build);

    public async ValueTask EmitAsync(TItem item, CancellationToken token)
    {
        await _items.Writer.WriteAsync(item, token).ConfigureAwait(false);
        Stats.AddItem();
    }

    private async Task ProgressAsync(CancellationToken token)
    {
        if (_options.ProgressInterval is not { } interval || interval <= TimeSpan.Zero) return;

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            Logger.LogInformation("Scraping: {Stats}", Stats);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await IgnoreCancellation(Completion).ConfigureAwait(false);
        _stop.Dispose();
    }
}
