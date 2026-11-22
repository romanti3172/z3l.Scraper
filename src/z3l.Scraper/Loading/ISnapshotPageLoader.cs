namespace z3l.Scraper.Loading;

/// <summary>Why a loaded page was not taken (<see cref="PageSnapshot.Kind"/>).</summary>
public enum SnapshotKind
{
    /// <summary>
    /// Recognised as blocked: a blocked status code or marker, or the handler threw <see cref="RetryRequestException"/> with
    /// <c>blocked</c>.
    /// </summary>
    Blocked,

    /// <summary>
    /// Not the page: a status outside 2xx, an empty or cut-off body, the wrong format, <c>MustContain</c>, <c>MustNotContain</c> or a custom
    /// check; or the handler threw <see cref="RetryRequestException"/>.
    /// </summary>
    Retry,

    /// <summary>The page looks missing: 404 or 410, or a not found marker.</summary>
    NotFound,

    /// <summary>The handler threw <see cref="NonRetryableException"/> or any other exception.</summary>
    HandlerError,
}

/// <summary>A loaded page that was not taken, given to <see cref="ISnapshotPageLoader"/>.</summary>
public sealed record PageSnapshot
{
    /// <summary>The request; its <see cref="ScrapeRequest.Attempts"/> tells which attempt brought the page.</summary>
    public required ScrapeRequest Request { get; init; }

    /// <summary>The page. Copy what you keep: the scraper lets the page go when the call ends.</summary>
    public required PageResponse Response { get; init; }

    /// <summary>Why the page was not taken.</summary>
    public required SnapshotKind Kind { get; init; }

    /// <summary>What the checks or the handler said.</summary>
    public required string Reason { get; init; }

    /// <summary>What the handler threw, if it threw.</summary>
    public Exception? Exception { get; init; }

    /// <summary>The page came from <see cref="Scraper.CachingPageLoader"/>, not from the loader.</summary>
    public required bool FromCache { get; init; }
}

/// <summary>
/// Saves the loaded pages that were not taken (blocked, missing, broken, or those whose handler threw) to show what the site answered.
/// Set one in <see cref="Scraper.SnapshotPageLoader"/>. <see cref="SnapshotPageLoader"/> saves them in a folder; implement this interface to
/// save them elsewhere (a database...).
/// <para>
/// It is not the loader of a label: the scraper calls it for the pages of every loader. Called from several threads at once.
/// </para>
/// </summary>
public interface ISnapshotPageLoader
{
    /// <summary>
    /// A loaded page was not taken: save it, if you want to. Called for every such attempt, retries included; a load that brought no page
    /// (a network error, a timeout) has no snapshot. Its request is retried or failed after the call, as always.
    /// </summary>
    ValueTask SaveAsync(PageSnapshot snapshot, CancellationToken cancellationToken);
}
