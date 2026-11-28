using System.Collections.Concurrent;
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
public sealed record FailedRequest
{
    /// <summary>The request.</summary>
    public required ScrapeRequest Request { get; init; }

    /// <summary>Why it failed.</summary>
    public required FailureReason Reason { get; init; }

    /// <summary>Details of the last failure.</summary>
    public required string Message { get; init; }

    /// <summary>Status code of the last response, if there was one.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Exception of the last attempt, if there was one.</summary>
    public Exception? Exception { get; init; }
}

/// <summary>What kind of trouble an attempt ran into: the groups of <see cref="ScraperStats.Reasons"/>.</summary>
public enum ReasonKind
{
    /// <summary>
    /// The response was recognised as blocked: a blocked status or marker, or the handler said so
    /// (<see cref="RetryRequestException"/> with <c>blocked</c>).
    /// </summary>
    Blocked,
    /// <summary>
    /// The response was not the page: a check failed (a status, an incomplete or empty body, <c>MustContain</c>...), or the handler
    /// asked for a retry.
    /// </summary>
    BadPage,
    /// <summary>The page does not exist: 404, 410 or a not found marker.</summary>
    NotFound,
    /// <summary>No response within <see cref="ScraperOptions.RequestTimeout"/>.</summary>
    Timeout,
    /// <summary>The load threw: a network, TLS or proxy error.</summary>
    LoadError,
    /// <summary>The handler threw an exception of its own.</summary>
    HandlerError,
    /// <summary>The loader or the handler threw <see cref="NonRetryableException"/>.</summary>
    NonRetryable,
    /// <summary>robots.txt of the host disallows the page (<see cref="ScraperOptions.RespectRobotsTxt"/>).</summary>
    DisallowedByRobotsTxt,
    /// <summary>The URL of a request given by a <see cref="Scraper.RequestsNeeded"/> handler is not an absolute http(s) URL.</summary>
    BadUrl,
    /// <summary>
    /// A scraping service refused the load for its limits (<see cref="Loading.RemoteLoadException.RateLimited"/>): retried without an
    /// attempt, up to <see cref="ScraperOptions.RateLimitRetries"/> times.
    /// </summary>
    RateLimited,
}

/// <summary>
/// A group of <see cref="ScraperStats.Reasons"/>: how many times attempts of one label on one host ran into one reason, and how many
/// of those ended their request.
/// </summary>
public sealed record ReasonCount
{
    /// <summary>What kind of trouble.</summary>
    public required ReasonKind Kind { get; init; }

    /// <summary>
    /// The reason, the same for every attempt of the group: the check that failed (<c>status 403</c>, <c>marker "captcha"</c>,
    /// <c>no "price"</c>), the timeout, the kind of a load error (<c>ConnectionError</c>, <c>ProxyTunnelError</c>), the type of the
    /// exception a handler threw, or the message of a <see cref="RetryRequestException"/>, of a <see cref="NonRetryableException"/> or
    /// of a service's refusal for its limits.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>Label of the requests; empty in the "other reasons" group.</summary>
    public required string Label { get; init; }

    /// <summary>Host of the requests, with the port when it is not the default one; empty in the "other reasons" group.</summary>
    public required string Host { get; init; }

    /// <summary>How many times: retries and failures together.</summary>
    public required long Count { get; init; }

    /// <summary>How many of them were the last attempt of a request that failed.</summary>
    public required long Failed { get; init; }

    /// <summary>
    /// The message of the last of them, with the details the reason leaves out: an exception message names the proxy, the IP...
    /// </summary>
    public required string Example { get; init; }

    /// <summary>The URL of the last of them.</summary>
    public required string ExampleUrl { get; init; }

    /// <summary>One line, as the last log line of a run shows it: <c>8711x blocked: marker "captcha" (Item, shop.com; 120 failed)</c>.</summary>
    public override string ToString()
    {
        var where = string.Join(", ", new[] { Label, Host }.Where(x => x.Length > 0));
        if (Failed > 0) where = where.Length > 0 ? $"{where}; {Failed} failed" : $"{Failed} failed";
        return where.Length > 0 ? $"{Count}x {NameOf(Kind)}: {Reason} ({where})" : $"{Count}x {NameOf(Kind)}: {Reason}";
    }

    private static string NameOf(ReasonKind kind) => kind switch
    {
        ReasonKind.Blocked => "blocked",
        ReasonKind.BadPage => "bad page",
        ReasonKind.NotFound => "not found",
        ReasonKind.Timeout => "timeout",
        ReasonKind.LoadError => "load error",
        ReasonKind.HandlerError => "handler error",
        ReasonKind.NonRetryable => "non-retryable",
        ReasonKind.DisallowedByRobotsTxt => "robots.txt",
        ReasonKind.BadUrl => "bad url",
        ReasonKind.RateLimited => "rate limited",
        _ => kind.ToString(),
    };
}

/// <summary>Live counters of a run. Read them at any time from any thread.</summary>
public sealed class ScraperStats
{
    private readonly Stopwatch _clock = new();
    private long _enqueued, _duplicates, _skipped, _outOfScope, _restored, _loads, _fromCache, _fromSavedFiles, _succeeded, _failed, _notFound, _retries, _blocked,
        _rateLimited, _items, _inProgress, _queued;

    /// <summary>
    /// Unique requests accepted. A request with a bad URL from <see cref="Scraper.RequestsNeeded"/> is counted here and in
    /// <see cref="Failed"/> at once.
    /// </summary>
    public long Enqueued => Interlocked.Read(ref _enqueued);
    /// <summary>Requests skipped as duplicates.</summary>
    public long Duplicates => Interlocked.Read(ref _duplicates);
    /// <summary>Requests skipped because <see cref="ScraperOptions.MaxRequests"/> was reached or they were deeper than <see cref="ScraperOptions.MaxDepth"/>.</summary>
    public long SkippedByLimit => Interlocked.Read(ref _skipped);
    /// <summary>
    /// Links found on pages that were skipped as out of the crawl scope (<see cref="ScraperOptions.AllowedHosts"/>,
    /// <see cref="ScraperOptions.UrlFilter"/>), and saved ones a continued run dropped for it.
    /// </summary>
    public long OutOfScope => Interlocked.Read(ref _outOfScope);
    /// <summary>Unfinished requests taken over from the state file of an earlier run (<see cref="ScraperOptions.StatePath"/>).</summary>
    public long Restored => Interlocked.Read(ref _restored);
    /// <summary>Load attempts, retries included, and the loads a scraping service refused for its limits (<see cref="RateLimited"/>).</summary>
    public long Loads => Interlocked.Read(ref _loads);
    /// <summary>
    /// Pages <see cref="Scraper.CachingPageLoader"/> gave instead of the loader, those that turned out not to be the page included
    /// (their requests were loaded then). Not counted in <see cref="Loads"/>.
    /// </summary>
    public long FromCache => Interlocked.Read(ref _fromCache);
    /// <summary>
    /// Files of <see cref="ScrapeRequest.SavePath"/> given instead of a load (<see cref="ScraperOptions.ReuseSavedFiles"/>), those that failed
    /// the checks included (their requests were loaded then). Not counted in <see cref="Loads"/>.
    /// </summary>
    public long FromSavedFiles => Interlocked.Read(ref _fromSavedFiles);
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
    /// <summary>
    /// Loads a scraping service refused for its limits (<see cref="Loading.RemoteLoadException.RateLimited"/>): retried without an
    /// attempt, up to <see cref="ScraperOptions.RateLimitRetries"/> times. They are in <see cref="Loads"/> too, and their retries in
    /// <see cref="Retries"/>.
    /// </summary>
    public long RateLimited => Interlocked.Read(ref _rateLimited);
    /// <summary>Items handed to the consumer.</summary>
    public long Items => Interlocked.Read(ref _items);
    /// <summary>Requests being loaded or handled now.</summary>
    public long InProgress => Interlocked.Read(ref _inProgress);
    /// <summary>
    /// Requests waiting in the queue or in the line of their host (<see cref="ScraperOptions.MaxConcurrencyPerHost"/>,
    /// <see cref="ScraperOptions.HostDelay"/>, throttling); retries waiting for their delay are not counted.
    /// </summary>
    public long Queued => Interlocked.Read(ref _queued);
    /// <summary>Requests not finished: queued, in progress or waiting for a retry.</summary>
    public long Pending => Enqueued - Succeeded - Failed;
    /// <summary>Run time.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;
    /// <summary>The run stopped because <see cref="ScraperOptions.MaxRunDuration"/> passed.</summary>
    public bool StoppedByTimeLimit { get; internal set; }
    /// <summary>
    /// The run stopped because the handler of a label failed <see cref="ScraperOptions.MaxConsecutiveHandlerErrors"/> times in a row on a
    /// host: the log says which one.
    /// </summary>
    public bool StoppedByHandlerErrors { get; internal set; }

    /// <summary>
    /// Why attempts did not give a page, most frequent first: every retry and every failure is counted once, in a group of its kind,
    /// reason, label and host. Live during the run; the last log line of a run names the first five. Up to 1000 groups: past them a
    /// new group is counted in an "other reasons" group of its kind.
    /// </summary>
    public IReadOnlyList<ReasonCount> Reasons =>
    [
        .. _reasons
            .Select(pair =>
            {
                var last = Volatile.Read(ref pair.Value.Last);
                return new ReasonCount
                {
                    Kind = pair.Key.Kind,
                    Reason = pair.Key.Reason,
                    Label = pair.Key.Label,
                    Host = pair.Key.Host,
                    Count = Interlocked.Read(ref pair.Value.Count),
                    Failed = Interlocked.Read(ref pair.Value.Failed),
                    Example = last?.Message ?? "",
                    ExampleUrl = last?.Url ?? "",
                };
            })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Kind)
            .ThenBy(x => x.Reason, StringComparer.Ordinal)
            .ThenBy(x => x.Label, StringComparer.Ordinal)
            .ThenBy(x => x.Host, StringComparer.Ordinal),
    ];

    internal const int MaxReasonGroups = 1000;
    internal const string OtherReasons = "other reasons";

    private readonly ConcurrentDictionary<(ReasonKind Kind, string Reason, string Label, string Host), ReasonTally> _reasons = new();
    private int _reasonGroups;

    private sealed class ReasonTally
    {
        public long Count, Failed;
        public ReasonExample? Last;
    }

    private sealed record ReasonExample(string Message, string Url);

    /// <summary>Counts an attempt that gave no page in its group; past <see cref="MaxReasonGroups"/> a new group goes to "other reasons".</summary>
    internal void AddReason(ReasonKind kind, string reason, string label, string host, bool failed, string example, string url)
    {
        var key = (kind, reason, label, host);
        if (!_reasons.TryGetValue(key, out var tally))
        {
            if (Volatile.Read(ref _reasonGroups) >= MaxReasonGroups) key = (kind, OtherReasons, "", "");
            var added = new ReasonTally();
            tally = _reasons.GetOrAdd(key, added);
            if (ReferenceEquals(tally, added)) Interlocked.Increment(ref _reasonGroups);
        }
        Interlocked.Increment(ref tally.Count);
        if (failed) Interlocked.Increment(ref tally.Failed);
        Volatile.Write(ref tally.Last, new ReasonExample(example, url));
    }

    internal void Start() => _clock.Start();
    internal void Stop() => _clock.Stop();
    internal void AddEnqueued() => Interlocked.Increment(ref _enqueued);
    internal void AddDuplicate() => Interlocked.Increment(ref _duplicates);
    internal void AddSkippedByLimit() => Interlocked.Increment(ref _skipped);
    internal void AddOutOfScope() => Interlocked.Increment(ref _outOfScope);
    internal void AddRestored() => Interlocked.Increment(ref _restored);
    internal void AddLoad() => Interlocked.Increment(ref _loads);
    internal void AddFromCache() => Interlocked.Increment(ref _fromCache);
    internal void AddFromSavedFile() => Interlocked.Increment(ref _fromSavedFiles);
    internal void AddSucceeded() => Interlocked.Increment(ref _succeeded);
    internal void AddFailed() => Interlocked.Increment(ref _failed);
    internal void AddNotFound() => Interlocked.Increment(ref _notFound);
    internal void AddRetry() => Interlocked.Increment(ref _retries);
    internal void AddBlocked() => Interlocked.Increment(ref _blocked);
    internal void AddRateLimited() => Interlocked.Increment(ref _rateLimited);
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
                      + (OutOfScope > 0 ? $", {OutOfScope} out of scope" : string.Empty)
                      + (Restored > 0 ? $", {Restored} restored" : string.Empty);
        var cached = (FromCache > 0 ? $", {FromCache} from cache" : string.Empty)
                     + (FromSavedFiles > 0 ? $", {FromSavedFiles} saved files" : string.Empty);
        var limited = RateLimited > 0 ? $", {RateLimited} rate limited" : string.Empty;
        return $"{Succeeded}/{Enqueued} done, {Failed} failed ({NotFound} not found), {Pending} pending " +
               $"({Queued} queued, {InProgress} in progress), {Loads} loads{cached}, {Retries} retries, {Blocked} blocked{limited}, " +
               $"{Items} items, {Duplicates} duplicates{skipped}, {time}, {rate:F1} pages/min";
    }
}

/// <summary>Everything a run produced.</summary>
public sealed record ScrapeResult
{
    /// <summary>Emitted items, of any types, in the order they came.</summary>
    public required IReadOnlyList<object> Items { get; init; }

    /// <summary>Requests that failed.</summary>
    public required IReadOnlyList<FailedRequest> Failed { get; init; }

    /// <summary>Final counters.</summary>
    public required ScraperStats Stats { get; init; }

    /// <summary>The items of one type (and of types derived from it).</summary>
    public IReadOnlyList<T> ItemsOf<T>() => [.. Items.OfType<T>()];
}
