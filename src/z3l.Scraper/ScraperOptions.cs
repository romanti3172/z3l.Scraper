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

    /// <summary>Retry the request when the handler throws (by default the request fails at once).</summary>
    public bool RetryOnHandlerError { get; set; }

    /// <summary>
    /// Items the consumer has not taken yet; when the buffer is full, handlers wait in <c>EmitAsync</c>
    /// so a slow consumer slows the scraping down instead of filling the memory.
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
    }
}
