namespace z3l.Scraper;

/// <summary>
/// What a <see cref="Scraper.RequestsNeeded"/> handler gets: how many requests are unfinished, and <see cref="Add(ScrapeRequest)"/>
/// to give the run more. Valid until the task the handler returned completes.
/// </summary>
public sealed class RequestsNeededEventArgs
{
    private readonly ScraperRun _run;
    private readonly object _lock = new();
    private int _passed;
    private bool _completed;

    internal RequestsNeededEventArgs(ScraperRun run, long pending, CancellationToken cancellationToken)
    {
        _run = run;
        Pending = pending;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Requests not finished when the event was raised: queued, in progress or waiting for a retry, links found on pages included.
    /// 0: the run has nothing else to do.
    /// </summary>
    public long Pending { get; }

    /// <summary>Cancelled when the run stops: <see cref="ScraperOptions.MaxRunDuration"/>, cancellation, or the consumer left the loop.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Gives the run a request, queued as a start request (depth 0). Returns false when it is not queued: a duplicate, a request over
    /// <see cref="ScraperOptions.MaxRequests"/>, or a URL that is not an absolute http(s) URL, which fails with
    /// <see cref="FailureReason.BadUrl"/> instead of throwing. A request passed here counts as given even when it is not queued:
    /// the source is not empty, and the event comes again. Thread-safe.
    /// </summary>
    /// <exception cref="InvalidOperationException">No handler for its label, wrong data for a typed handler, or the call has finished.</exception>
    public bool Add(ScrapeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_lock)
        {
            if (_completed)
            {
                throw new InvalidOperationException(
                    "The RequestsNeeded call has finished: add the requests before the task the handler returned completes");
            }
            _passed++;
            return _run.AddRequest(request);
        }
    }

    /// <summary>Gives the run requests; returns how many were queued. See <see cref="Add(ScrapeRequest)"/>.</summary>
    public int Add(IEnumerable<ScrapeRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var queued = 0;
        foreach (var request in requests)
        {
            if (Add(request)) queued++;
        }
        return queued;
    }

    /// <summary>Ends the call: later <see cref="Add(ScrapeRequest)"/> calls throw. Returns how many requests were passed.</summary>
    internal int Complete()
    {
        lock (_lock)
        {
            _completed = true;
            return _passed;
        }
    }
}
