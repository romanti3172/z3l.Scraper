namespace z3l.Scraper;

/// <summary>Kinds of pages most scrapers have. Use them as labels, or use your own strings.</summary>
public enum PageType
{
    /// <summary>The start page of a site or a section.</summary>
    Root,
    /// <summary>A category: links to subcategories or listings.</summary>
    Category,
    /// <summary>A list of items, usually with pages.</summary>
    Listing,
    /// <summary>One item: a product, an ad, a profile.</summary>
    Item,
    /// <summary>More about an item: reviews, specifications, a tab loaded separately.</summary>
    ItemDetails,
    /// <summary>A JSON API (ajax lists, search). Checked as JSON unless its rules say otherwise.</summary>
    Api,
    /// <summary>A file: an image, a PDF. Checked as binary unless its rules say otherwise.</summary>
    File,
    /// <summary>A sitemap.</summary>
    Sitemap,
}

/// <summary>
/// Label of a request: it picks the handler and the response rules. A <see cref="PageType"/> or any string converts to it,
/// so every method that takes a label takes both. Labels ignore case; the default one is "default".
/// </summary>
public readonly struct ScrapeLabel : IEquatable<ScrapeLabel>
{
    /// <summary>Name of the default label.</summary>
    public const string DefaultName = "default";

    private readonly string? _name;

    /// <summary>A label with this name.</summary>
    public ScrapeLabel(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
    }

    /// <summary>The default label.</summary>
    public static ScrapeLabel Default => default;

    /// <summary>The label name: the <see cref="PageType"/> name or the string.</summary>
    public string Name => _name ?? DefaultName;

    /// <summary>A label from a string.</summary>
    public static implicit operator ScrapeLabel(string name) => new(name);

    /// <summary>A label from a page type.</summary>
    public static implicit operator ScrapeLabel(PageType type) => new(NameOf(type));

    internal static string NameOf(PageType type) => type switch
    {
        PageType.Root => nameof(PageType.Root),
        PageType.Category => nameof(PageType.Category),
        PageType.Listing => nameof(PageType.Listing),
        PageType.Item => nameof(PageType.Item),
        PageType.ItemDetails => nameof(PageType.ItemDetails),
        PageType.Api => nameof(PageType.Api),
        PageType.File => nameof(PageType.File),
        PageType.Sitemap => nameof(PageType.Sitemap),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown page type"),
    };

    /// <inheritdoc />
    public bool Equals(ScrapeLabel other) => string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ScrapeLabel other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Name);

    /// <summary>Same label, case ignored.</summary>
    public static bool operator ==(ScrapeLabel left, ScrapeLabel right) => left.Equals(right);

    /// <summary>Different labels.</summary>
    public static bool operator !=(ScrapeLabel left, ScrapeLabel right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => Name;
}
