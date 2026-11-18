namespace z3l.Scraper.Loading;

/// <summary>
/// Keeps the pages the handlers took and gives them back instead of loading them again; set one in
/// <see cref="Scraper.CachingPageLoader"/>. <see cref="CachingPageLoader"/> keeps the pages in a folder; implement this interface to keep
/// them elsewhere (a database...).
/// <para>
/// It is not the loader of a label: the scraper asks it once per request, before the first load, whatever loader the label has
/// (<see cref="Scraper.UseLoader"/> and queue loaders included). A page it gives skips the host throttle, the rate limit and the loader,
/// is checked with the rules of its label like any page, and the loader hears nothing about it. Called from several threads at once.
/// </para>
/// </summary>
public interface ICachingPageLoader
{
    /// <summary>The kept page of the request, or null: then the request goes to its loader.</summary>
    ValueTask<PageResponse?> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The page passed the checks and its handler took it: keep it, if you want to. Copy what you keep: the scraper lets the page go when
    /// the call ends. Pages that came from this cache are not offered again.
    /// </summary>
    ValueTask StoreAsync(ScrapeRequest request, PageResponse response, CancellationToken cancellationToken);

    /// <summary>
    /// A kept page turned out not to be the page: it failed the checks (the rules changed since it was kept) or its handler asked for a
    /// retry. Forget it; the request goes to its loader, and the kept page does not count as an attempt.
    /// </summary>
    ValueTask RemoveAsync(ScrapeRequest request, CancellationToken cancellationToken);
}
