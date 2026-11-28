namespace z3l.Scraper.Validation;

/// <summary>Verdict on a loaded page.</summary>
public enum ValidationOutcome
{
    /// <summary>The page is good: it goes to the handler.</summary>
    Ok,
    /// <summary>The page is broken or incomplete: load it again.</summary>
    Retry,
    /// <summary>The response was blocked (captcha, 403...): load it again and tell the loader.</summary>
    Blocked,
    /// <summary>The page does not exist.</summary>
    NotFound,
}

/// <summary>Verdict on a loaded page with the reason.</summary>
public readonly record struct ValidationResult(ValidationOutcome Outcome, string? Reason = null)
{
    /// <summary>The page is good.</summary>
    public static ValidationResult Ok { get; } = new(ValidationOutcome.Ok);

    /// <summary>Load the page again.</summary>
    public static ValidationResult Retry(string reason) => new(ValidationOutcome.Retry, reason);

    /// <summary>The response was blocked.</summary>
    public static ValidationResult Blocked(string reason) => new(ValidationOutcome.Blocked, reason);

    /// <summary>The page does not exist; retried <see cref="ScraperOptions.NotFoundRetries"/> times (sites sometimes answer 404 by mistake).</summary>
    public static ValidationResult NotFound(string reason) => new(ValidationOutcome.NotFound, reason);

    /// <summary>The page does not exist; with <paramref name="final"/> it fails at once, without the not found retries.</summary>
    public static ValidationResult NotFound(string reason, bool final) => new(ValidationOutcome.NotFound, reason) { Final = final };

    /// <summary>A not found verdict that is sure (a not found marker on the page): no retries.</summary>
    public bool Final { get; init; }
}
