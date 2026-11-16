namespace z3l.Scraper.Validation;

/// <summary>What the response body must look like.</summary>
public enum ExpectedContent
{
    /// <summary>No check of the body format.</summary>
    Any,
    /// <summary>A complete HTML page: contains <c>&lt;/html&gt;</c> (a cut-off page is retried).</summary>
    Html,
    /// <summary>A JSON object or array.</summary>
    Json,
    /// <summary>A file (image, PDF...): the body is not decoded as text and text rules are skipped.</summary>
    Binary,
}

/// <summary>
/// Checks of a loaded page, for one label or for all (<see cref="ResponseRulesCollection.Default"/>).
/// String checks ignore case. Label rules add to the default ones; a value set for a label replaces the default value.
/// </summary>
public sealed class ResponseRules
{
    /// <summary>Retry responses with a status outside 2xx (after the blocked and not found checks).</summary>
    public bool? RequireSuccessStatus { get; set; }

    /// <summary>Format of the body.</summary>
    public ExpectedContent? Content { get; set; }

    /// <summary>Accept an empty body.</summary>
    public bool? AllowEmptyBody { get; set; }

    /// <summary>Status 404 and 410 mean the page does not exist (see <see cref="ScraperOptions.NotFoundRetries"/>).</summary>
    public bool? NotFoundOnStatus404 { get; set; }

    /// <summary>The page is retried unless it contains every one of these strings (a part that is always on a good page).</summary>
    public List<string> MustContain { get; } = [];

    /// <summary>The page is retried if it contains any of these strings.</summary>
    public List<string> MustNotContain { get; } = [];

    /// <summary>The page does not exist if it contains any of these strings ("Product not found").</summary>
    public List<string> NotFoundMarkers { get; } = [];

    /// <summary>
    /// The response was blocked if it contains any of these strings (captcha, "Access denied", "Just a moment...").
    /// The request is retried and the loader is told (<see cref="Loading.IBlockAwarePageLoader"/>).
    /// </summary>
    public List<string> BlockedMarkers { get; } = [];

    /// <summary>Status codes that mean the response was blocked.</summary>
    public HashSet<int> BlockedStatusCodes { get; } = [];

    /// <summary>
    /// Your own check, run after the others. Return <see cref="ValidationResult.Ok"/> to accept the page
    /// or any other result to reject it.
    /// </summary>
    public ResponseCheck? Custom { get; set; }
}

/// <summary>A check of a loaded page.</summary>
/// <param name="request">The request.</param>
/// <param name="response">The response.</param>
/// <param name="text">
/// The page text, read in place without making a string; empty for <see cref="ExpectedContent.Binary"/> and empty bodies.
/// Valid only during the call: copy what you need (<c>text.ToString()</c>).
/// </param>
public delegate ValidationResult ResponseCheck(ScrapeRequest request, PageResponse response, ReadOnlySpan<char> text);

/// <summary>Response rules by label.</summary>
public sealed class ResponseRulesCollection
{
    private readonly Dictionary<string, ResponseRules> _byLabel = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rules for every label.</summary>
    public ResponseRules Default { get; } = new()
    {
        RequireSuccessStatus = true,
        Content = ExpectedContent.Any,
        AllowEmptyBody = false,
        NotFoundOnStatus404 = true,
        BlockedStatusCodes = { 403, 429 },
    };

    /// <summary>Rules for one label (a <see cref="PageType"/> or a string), created on first use.</summary>
    public ResponseRules this[ScrapeLabel label]
    {
        get
        {
            if (!_byLabel.TryGetValue(label.Name, out var rules))
            {
                rules = new ResponseRules();
                _byLabel[label.Name] = rules;
            }
            return rules;
        }
    }

    /// <summary>
    /// What a page type is checked as when its own rules do not say: a file is binary, an API answers JSON.
    /// This beats <see cref="Default"/>, so <c>Default.Content = Html</c> does not break files.
    /// </summary>
    private static ExpectedContent? ContentOfPageType(string label) =>
        label.Equals(nameof(PageType.File), StringComparison.OrdinalIgnoreCase) ? ExpectedContent.Binary
        : label.Equals(nameof(PageType.Api), StringComparison.OrdinalIgnoreCase) ? ExpectedContent.Json
        : null;

    internal EffectiveRules Build(string label)
    {
        _byLabel.TryGetValue(label, out var own);
        var d = Default;

        static string[] Merge(List<string> a, List<string>? b) =>
            [.. a.Concat(b ?? []).Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase)];

        return new EffectiveRules(
            RequireSuccessStatus: own?.RequireSuccessStatus ?? d.RequireSuccessStatus ?? true,
            Content: own?.Content ?? ContentOfPageType(label) ?? d.Content ?? ExpectedContent.Any,
            AllowEmptyBody: own?.AllowEmptyBody ?? d.AllowEmptyBody ?? false,
            NotFoundOnStatus404: own?.NotFoundOnStatus404 ?? d.NotFoundOnStatus404 ?? true,
            MustContain: Merge(d.MustContain, own?.MustContain),
            MustNotContain: Merge(d.MustNotContain, own?.MustNotContain),
            NotFoundMarkers: Merge(d.NotFoundMarkers, own?.NotFoundMarkers),
            BlockedMarkers: Merge(d.BlockedMarkers, own?.BlockedMarkers),
            BlockedStatusCodes: [.. d.BlockedStatusCodes.Concat(own?.BlockedStatusCodes ?? [])],
            Custom: [.. new[] { d.Custom, own?.Custom }.OfType<ResponseCheck>()]);
    }
}

internal sealed record EffectiveRules(
    bool RequireSuccessStatus,
    ExpectedContent Content,
    bool AllowEmptyBody,
    bool NotFoundOnStatus404,
    string[] MustContain,
    string[] MustNotContain,
    string[] NotFoundMarkers,
    string[] BlockedMarkers,
    HashSet<int> BlockedStatusCodes,
    ResponseCheck[] Custom);
