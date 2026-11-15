using z3l.Scraper.Loading;

namespace z3l.Scraper.Tests;

/// <summary>A loader made of a function, for tests that compute pages instead of listing them.</summary>
internal sealed class DelegateLoader(Func<ScrapeRequest, CancellationToken, Task<PageResponse>> load) : IPageLoader
{
    public Task<PageResponse> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken) => load(request, cancellationToken);
}
