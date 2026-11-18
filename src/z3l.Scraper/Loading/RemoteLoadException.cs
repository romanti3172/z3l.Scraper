namespace z3l.Scraper.Loading;

/// <summary>
/// A remote load gave no page: the worker or the service had no response, the service failed or refused the call, or the load was
/// lost (no answer in time). The scraper retries it.
/// </summary>
public sealed class RemoteLoadException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RemoteLoadException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with its cause.</summary>
    public RemoteLoadException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// The service refused the call for its limits (too many calls at once or per minute): the page was not tried. The scraper loads
    /// it again without spending one of its attempts, up to <see cref="ScraperOptions.RateLimitRetries"/> times, and counts the
    /// refusal in <see cref="ScraperStats.RateLimited"/>. The message is the reason <see cref="ScraperStats.Reasons"/> groups such
    /// refusals by: keep it the same for alike ones.
    /// </summary>
    public bool RateLimited { get; init; }

    /// <summary>
    /// How long the service asks to wait before the next call, its <c>Retry-After</c>: the retry waits that long at least, up to
    /// <see cref="ScraperOptions.MaxRetryDelay"/>. Null: the retry delay alone.
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }
}
