namespace z3l.Scraper.Loading;

/// <summary>
/// A remote load gave no page: the worker or the service had no response, the service failed, or the load was lost (no answer in
/// time). The scraper retries it.
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
}
