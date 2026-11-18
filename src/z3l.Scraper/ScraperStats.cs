using System.Diagnostics;

namespace z3l.Scraper;

/// <summary>Why a request ended without a handled page.</summary>
public enum FailureReason
{
    /// <summary>All retries were used (load errors, broken or blocked pages).</summary>
    RetriesExhausted,
    /// <summary>The page does not exist (404 or a not found marker).</summary>
    NotFound,
    /// <summary>The handler threw.</summary>
    HandlerError,
    /// <summary>The loader or the handler threw <see cref="NonRetryableException"/>.</summary>
    NonRetryable,
    /// <summary>robots.txt of the host disallows the page (<see cref="ScraperOptions.RespectRobotsTxt"/>).</summary>
    DisallowedByRobotsTxt,
    /// <summary>
    /// The URL of a request given by a <see cref="Scraper.RequestsNeeded"/> handler is not an absolute http(s) URL. Such a request is
    /// counted as enqueued and failed at once.
    /// </summary>
    BadUrl,
}

/// <summary>A request that ended without a handled page.</summary>
/// <param name="Request">The request.</param>
/// <param name="Reason">Why it failed.</param>
/// <param name="Message">Details of the last failure.</param>
/// <param name="StatusCode">Status code of the last response, if there was one.</param>
/// <param name="Exception">Exception of the last attempt, if there was one.</param>
public sealed record FailedRequest(ScrapeRequest Request, FailureReason Reason, string Message, int? StatusCode, Exception? Exception);

/// <summary>Live counters of a run. Read them at any time from any thread.</summary>
public sealed class ScraperStats
{
    private readonly Stopwatch _clock = new();
    private long _enqueued, _duplicates, _skipped, _restored, _loads, _succeeded, _failed, _notFound, _retries, _blocked, _items, _inProgress, _queued;

    /// <summary>
    /// Unique requests accepted. A request with a bad URL from <see cref="Scraper.RequestsNeeded"/> is counted here and in
    /// <see cref="Failed"/> at once.
    /// </summary>
    public long Enqueued => Interlocked.Read(ref _enqueued);
    /// <summary>Requests skipped as duplicates.</summary>
    public long Duplicates => Interlocked.Read(ref _duplicates);
    /// <summary>Requests skipped because <see cref="ScraperOptions.MaxRequests"/> was reached.</summary>
    public long SkippedByLimit => Interlocked.Read(ref _skipped);
    /// <summary>Unfinished requests taken over from the state file of an earlier run (<see cref="ScraperOptions.StatePath"/>).</summary>
    public long Restored => Interlocked.Read(ref _restored);
    /// <summary>Load attempts, retries included.</summary>
    public long Loads => Interlocked.Read(ref _loads);
    /// <summary>Requests whose page was handled.</summary>
    public long Succeeded => Interlocked.Read(ref _succeeded);
    /// <summary>Requests that failed (not found included).</summary>
    public long Failed => Interlocked.Read(ref _failed);
    /// <summary>Requests whose page does not exist.</summary>
    public long NotFound => Interlocked.Read(ref _notFound);
    /// <summary>Retries scheduled.</summary>
    public long Retries => Interlocked.Read(ref _retries);
    /// <summary>Responses recognised as blocked.</summary>
    public long Blocked => Interlocked.Read(ref _blocked);
    /// <summary>Items handed to the consumer.</summary>
    public long Items => Interlocked.Read(ref _items);
    /// <summary>Requests being loaded or handled now.</summary>
    public long InProgress => Interlocked.Read(ref _inProgress);
    /// <summary>Requests waiting in the queue (retries waiting for their delay are not counted).</summary>
    public long Queued => Interlocked.Read(ref _queued);
    /// <summary>Requests not finished: queued, in progress or waiting for a retry.</summary>
    public long Pending => Enqueued - Succeeded - Failed;
    /// <summary>Run time.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;
    /// <summary>The run stopped because <see cref="ScraperOptions.MaxRunDuration"/> passed.</summary>
    public bool StoppedByTimeLimit { get; internal set; }

    internal void Start() => _clock.Start();
    internal void Stop() => _clock.Stop();
    internal void AddEnqueued() => Interlocked.Increment(ref _enqueued);
    internal void AddDuplicate() => Interlocked.Increment(ref _duplicates);
    internal void AddSkippedByLimit() => Interlocked.Increment(ref _skipped);
    internal void AddRestored() => Interlocked.Increment(ref _restored);
    internal void AddLoad() => Interlocked.Increment(ref _loads);
    internal void AddSucceeded() => Interlocked.Increment(ref _succeeded);
    internal void AddFailed() => Interlocked.Increment(ref _failed);
    internal void AddNotFound() => Interlocked.Increment(ref _notFound);
    internal void AddRetry() => Interlocked.Increment(ref _retries);
    internal void AddBlocked() => Interlocked.Increment(ref _blocked);
    internal void AddItem() => Interlocked.Increment(ref _items);
    internal void ChangeInProgress(int delta) => Interlocked.Add(ref _inProgress, delta);
    internal void ChangeQueued(int delta) => Interlocked.Add(ref _queued, delta);

    /// <summary>One-line summary.</summary>
    public override string ToString()
    {
        var elapsed = Elapsed;
        var rate = elapsed.TotalMinutes > 0 ? Succeeded / elapsed.TotalMinutes : 0;
        var time = elapsed.TotalDays >= 1 ? $"{(int)elapsed.TotalDays}d {elapsed:hh\\:mm\\:ss}" : $"{elapsed:hh\\:mm\\:ss}";
        var skipped = (SkippedByLimit > 0 ? $", {SkippedByLimit} over limits" : string.Empty)
                      + (Restored > 0 ? $", {Restored} restored" : string.Empty);
        return $"{Succeeded}/{Enqueued} done, {Failed} failed ({NotFound} not found), {Pending} pending " +
               $"({Queued} queued, {InProgress} in progress), {Loads} loads, {Retries} retries, {Blocked} blocked, " +
               $"{Items} items, {Duplicates} duplicates{skipped}, {time}, {rate:F1} pages/min";
    }
}

/// <summary>Everything a run produced.</summary>
/// <param name="Items">Emitted items, of any types, in the order they came.</param>
/// <param name="Failed">Requests that failed.</param>
/// <param name="Stats">Final counters.</param>
public sealed record ScrapeResult(IReadOnlyList<object> Items, IReadOnlyList<FailedRequest> Failed, ScraperStats Stats)
{
    /// <summary>The items of one type (and of types derived from it).</summary>
    public IReadOnlyList<T> ItemsOf<T>() => [.. Items.OfType<T>()];
}
