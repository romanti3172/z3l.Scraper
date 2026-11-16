using System.Collections.Concurrent;
using z3l.Scraper.Loading;

namespace z3l.Scraper.Tests;

/// <summary>Serves pages from a dictionary; a page may be a list of responses given one per attempt.</summary>
internal sealed class FakeLoader : IBlockAwarePageLoader
{
    private readonly ConcurrentDictionary<string, Queue<Func<ScrapeRequest, PageResponse>>> _pages = new();
    private int _active;

    public ConcurrentQueue<string> Loaded { get; } = new();
    public ConcurrentQueue<string> Blocked { get; } = new();
    public int MaxActive;
    public TimeSpan Delay { get; set; }

    public FakeLoader Page(string url, string html, int status = 200) => Responses(url, (status, html));

    /// <summary>One response per attempt; the last one repeats.</summary>
    public FakeLoader Responses(string url, params (int Status, string Html)[] responses)
    {
        var queue = new Queue<Func<ScrapeRequest, PageResponse>>();
        foreach (var (status, html) in responses) queue.Enqueue(_ => PageResponse.FromText(status, url, html, "text/html; charset=utf-8"));
        _pages[url] = queue;
        return this;
    }

    public FakeLoader Throws(string url, Exception exception)
    {
        _pages[url] = new Queue<Func<ScrapeRequest, PageResponse>>([_ => throw exception]);
        return this;
    }

    public async Task<PageResponse> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _active);
        InterlockedMax(ref MaxActive, active);
        try
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            Loaded.Enqueue(request.Url);

            if (!_pages.TryGetValue(request.Url, out var queue)) return PageResponse.FromText(404, request.Url, "<html>missing</html>");
            Func<ScrapeRequest, PageResponse> next;
            lock (queue) next = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return next(request);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public void ReportBlocked(ScrapeRequest request, PageResponse response, string reason) => Blocked.Enqueue(request.Url);

    public ConcurrentQueue<string> Bad { get; } = new();
    public ConcurrentQueue<string> Good { get; } = new();

    public void ReportBadResponse(ScrapeRequest request, PageResponse response, string reason) => Bad.Enqueue($"{request.Url} {reason}");

    public void ReportGoodResponse(ScrapeRequest request, PageResponse response) => Good.Enqueue(request.Url);

    public ConcurrentQueue<string> TimedOut { get; } = new();

    public void ReportTimeout(ScrapeRequest request) => TimedOut.Enqueue(request.Url);

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}
