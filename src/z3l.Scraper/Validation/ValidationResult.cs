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

    /// <summary>The page does not exist.</summary>
    public static ValidationResult NotFound(string reason) => new(ValidationOutcome.NotFound, reason);
}
