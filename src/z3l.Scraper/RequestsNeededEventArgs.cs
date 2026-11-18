namespace z3l.Scraper;

/// <summary>
/// What a <see cref="Scraper.RequestsNeeded"/> handler gets: how many requests are unfinished, and
/// <see cref="Add(string, ScrapeLabel, bool)"/> to give the run more. Valid until the task the handler returned completes.
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
    /// Gives the run a page by its URL, queued as a start request (depth 0). A page already queued in this run is normally
    /// skipped as a duplicate: one URL is loaded once per run, however many times it is given. With
    /// <paramref name="skipDuplicateCheck"/> the page is queued anyway, even when this run has already loaded it: for a source
    /// that gives a page out again, such as a price to check once more.
    /// Returns false when the page is not queued; it still counts as given, see <see cref="Add(ScrapeRequest)"/>. Thread-safe.
    /// </summary>
    /// <param name="url">
    /// Absolute http(s) URL. Null, empty or any other bad URL does not throw: its request fails with <see cref="FailureReason.BadUrl"/>.
    /// </param>
    /// <param name="label">Handler label: a <see cref="PageType"/> or a string; the default label when omitted.</param>
    /// <param name="skipDuplicateCheck">
    /// True: queue the page even if this run already queued it. The page is not remembered then either, so a link to it found
    /// later is queued too. The same as <see cref="ScrapeRequest.SkipDuplicateCheck"/>.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// No handler for the label, a typed handler (its data comes with <see cref="ScrapeRequest.For{TData}(string, ScrapeLabel, TData)"/>),
    /// or the call has finished.
    /// </exception>
    public bool Add(string? url, ScrapeLabel label = default, bool skipDuplicateCheck = false) =>
        Add(new ScrapeRequest(url, label, skipDuplicateCheck));

    /// <summary>
    /// Gives the run pages with one label; returns how many were queued. Pages already queued in this run are skipped, unless
    /// <paramref name="skipDuplicateCheck"/> is true. See <see cref="Add(string, ScrapeLabel, bool)"/>.
    /// </summary>
    public int Add(IEnumerable<string?> urls, ScrapeLabel label = default, bool skipDuplicateCheck = false)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var queued = 0;
        foreach (var url in urls)
        {
            if (Add(url, label, skipDuplicateCheck)) queued++;
        }
        return queued;
    }

    /// <summary>
    /// Gives the run a request, queued as a start request (depth 0). Returns false when it is not queued: a duplicate (unless
    /// <see cref="ScrapeRequest.SkipDuplicateCheck"/> is set), a request over
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
