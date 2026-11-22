using z3l.Scraper.Validation;

namespace z3l.Scraper;

/// <summary>
/// One page through its handler, from <see cref="Scraper.ParseAsync(ScrapeRequest, CancellationToken)"/>: the page, what the
/// response checks said, what the handler emitted, and the pages it found, which were not loaded.
/// </summary>
public sealed record ParseResult
{
    /// <summary>The request of the page, its URL normalized.</summary>
    public required ScrapeRequest Request { get; init; }

    /// <summary>The page: status, final URL, headers, text.</summary>
    public required PageResponse Response { get; init; }

    /// <summary>
    /// What the response checks said. The handler runs only when they pass (<see cref="ValidationOutcome.Ok"/>), as in a run;
    /// otherwise <see cref="Items"/> and <see cref="Found"/> are empty.
    /// </summary>
    public required ValidationResult Validation { get; init; }

    /// <summary>What the handler emitted, also before it threw (a run drops those).</summary>
    public required IReadOnlyList<object> Items { get; init; }

    /// <summary>
    /// The pages the handler queued, in its order, with their labels and data, as a run would take them: duplicates, URLs that are
    /// not http(s) and links out of the crawl scope (<see cref="ScraperOptions.AllowedHosts"/>, <see cref="ScraperOptions.UrlFilter"/>)
    /// are left out (<c>Enqueue</c> returned false for them), and the URLs are without
    /// <see cref="ScraperOptions.IgnoredQueryParameters"/>. None of them was loaded. The limits of a run
    /// (<see cref="ScraperOptions.MaxDepth"/>, <see cref="ScraperOptions.MaxRequests"/>) do not apply.
    /// </summary>
    public required IReadOnlyList<ScrapeRequest> Found { get; init; }

    /// <summary>
    /// What the handler threw, <see cref="RetryRequestException"/> and <see cref="NonRetryableException"/> included; null when it
    /// finished.
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>The page passed the checks and its handler finished without throwing: a run would take it.</summary>
    public bool Succeeded => Validation.Outcome == ValidationOutcome.Ok && Exception is null;

    /// <summary>The emitted items of one type.</summary>
    public IReadOnlyList<T> ItemsOf<T>() => [.. Items.OfType<T>()];
}
