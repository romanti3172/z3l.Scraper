using z3l.Scraper.Validation;

namespace z3l.Scraper;

/// <summary>Scraper settings. Change them before <c>RunAsync</c>; a running scrape uses the values it started with.</summary>
public sealed class ScraperOptions
{
    /// <summary>Requests loaded and handled at the same time.</summary>
    public int MaxConcurrency { get; set; } = 8;

    /// <summary>Retries after a failed load, a broken page or a blocked response (the first attempt is not counted).</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>Retries of a page that looks missing (404, not found marker): sites sometimes answer 404 by mistake.</summary>
    public int NotFoundRetries { get; set; } = 1;

    /// <summary>Delay before the first retry; each next retry waits <see cref="RetryBackoffFactor"/> times longer.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Growth of the retry delay.</summary>
    public double RetryBackoffFactor { get; set; } = 2;

    /// <summary>Longest retry delay.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Time for one load; a slower load is cancelled and retried. <see cref="Timeout.InfiniteTimeSpan"/> for no limit.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Stop after this time and return what was scraped. Null for no limit.</summary>
    public TimeSpan? MaxRunDuration { get; set; }

    /// <summary>
    /// Accept at most this many requests in a run (start requests included); later ones are skipped and counted in
    /// <see cref="ScraperStats.SkippedByLimit"/>. Handy for trying the selectors on the first pages of a big site. Null for no limit.
    /// </summary>
    public int? MaxRequests { get; set; }

    /// <summary>
    /// Spreads the loads evenly: at most this many loads (retries included) start per minute, whatever
    /// <see cref="MaxConcurrency"/> is. Null for no limit.
    /// </summary>
    public int? MaxRequestsPerMinute { get; set; }

    /// <summary>
    /// Retry the request when the handler throws (by default the request fails at once).
    /// <see cref="RetryRequestException"/> is always retried and <see cref="NonRetryableException"/> never is.
    /// </summary>
    public bool RetryOnHandlerError { get; set; }

    /// <summary>
    /// Items the consumer has not taken yet; when the buffer is full, workers wait before handing over more
    /// items, so a slow consumer slows the scraping down instead of filling the memory.
    /// </summary>
    public int ItemBufferSize { get; set; } = 1000;

    /// <summary>How often progress is logged. Null to turn it off.</summary>
    public TimeSpan? ProgressInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Checks of loaded pages by label.</summary>
    public ResponseRulesCollection Rules { get; } = new();

    internal void Validate()
    {
        if (MaxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(MaxConcurrency), MaxConcurrency, "Must be 1 or more");
        if (MaxRetries < 0) throw new ArgumentOutOfRangeException(nameof(MaxRetries), MaxRetries, "Must be 0 or more");
        if (NotFoundRetries < 0) throw new ArgumentOutOfRangeException(nameof(NotFoundRetries), NotFoundRetries, "Must be 0 or more");
        if (ItemBufferSize < 1) throw new ArgumentOutOfRangeException(nameof(ItemBufferSize), ItemBufferSize, "Must be 1 or more");
        if (RetryBackoffFactor < 1) throw new ArgumentOutOfRangeException(nameof(RetryBackoffFactor), RetryBackoffFactor, "Must be 1 or more");
        if (RetryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RetryDelay), RetryDelay, "Must not be negative");
        if (MaxRetryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MaxRetryDelay), MaxRetryDelay, "Must not be negative");
        if (RequestTimeout <= TimeSpan.Zero && RequestTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout), RequestTimeout, "Must be positive or Timeout.InfiniteTimeSpan");
        }
        if (MaxRunDuration is { } duration && (duration <= TimeSpan.Zero || duration.TotalMilliseconds > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRunDuration), duration, "Must be positive and under 24 days");
        }
        if (MaxRequests is < 1) throw new ArgumentOutOfRangeException(nameof(MaxRequests), MaxRequests, "Must be 1 or more");
        if (MaxRequestsPerMinute is < 1) throw new ArgumentOutOfRangeException(nameof(MaxRequestsPerMinute), MaxRequestsPerMinute, "Must be 1 or more");
    }
}
