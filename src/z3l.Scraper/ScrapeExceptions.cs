namespace z3l.Scraper;

/// <summary>
/// Thrown by a handler to load the page again: the page passed the response rules but is not the real one
/// (a soft block, prices hidden for bots, data missing). The retry counts against <see cref="ScraperOptions.MaxRetries"/>;
/// items the handler emitted before the exception are dropped.
/// </summary>
public class RetryRequestException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why the page is retried.</param>
    /// <param name="blocked">The page was a block: the loader is told, as for a blocked response.</param>
    public RetryRequestException(string message, bool blocked = false) : base(message)
    {
        Blocked = blocked;
    }

    /// <summary>Creates the exception with the exception that caused it.</summary>
    public RetryRequestException(string message, Exception? innerException, bool blocked = false) : base(message, innerException)
    {
        Blocked = blocked;
    }

    /// <summary>The page was a block (captcha, ban page): the loader is told so it can change the proxy or the session.</summary>
    public bool Blocked { get; }
}

/// <summary>
/// Thrown by a loader or a handler when another attempt cannot help (a bad URL, a response too big,
/// a page of the wrong kind): the request fails at once with <see cref="FailureReason.NonRetryable"/>,
/// even when <see cref="ScraperOptions.RetryOnHandlerError"/> is on.
/// </summary>
public class NonRetryableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public NonRetryableException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with the exception that caused it.</summary>
    public NonRetryableException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
