using System.Globalization;

namespace z3l.Scraper.Loading;

/// <summary>
/// Saves the loaded pages that were not taken in a folder, to show what the site answered: a captcha, "Product not found", a cut-off page,
/// the page a handler choked on. Set it in <see cref="Scraper.SnapshotPageLoader"/>:
/// <code>scraper.SnapshotPageLoader = new SnapshotPageLoader { Path = "snapshots", MaxPerKind = 20 };</code>
/// Pages go to <c>{Path}/{label}/{kind}</c>, the kind being <c>blocked</c>, <c>retry</c>, <c>not-found</c> or <c>handler-error</c>
/// (<see cref="SnapshotKind"/>), two files each: the body (<c>.html</c>, <c>.json</c>...) and its description (<c>.meta.json</c>: the request,
/// the reason, the status, the headers, what the handler threw). Files of earlier runs stay.
/// <para>
/// To save the pages elsewhere (a database...) with the same labels, filter and limits, override <see cref="WriteAsync"/>; or implement
/// <see cref="ISnapshotPageLoader"/> yourself.
/// </para>
/// </summary>
public class SnapshotPageLoader : ISnapshotPageLoader
{
    private const string Format = "z3l.Scraper snapshot";
    private const int Version = 1;

    private readonly object _lock = new();
    private readonly Dictionary<string, int> _perKind = new(StringComparer.OrdinalIgnoreCase);
    private int _saved;
    private long _sequence;

    /// <summary>Folder of the snapshots, made when the first one is saved. Not needed when <see cref="WriteAsync"/> is overridden.</summary>
    public string? Path { get; set; }

    /// <summary>
    /// Labels whose pages are saved (a <see cref="PageType"/> or a string); empty, the default, for every label.
    /// </summary>
    public HashSet<ScrapeLabel> Labels { get; } = [];

    /// <summary>Which of those snapshots to save, for example only <see cref="SnapshotKind.Blocked"/>; null, the default, saves them all.</summary>
    public Func<PageSnapshot, bool>? Filter { get; set; }

    /// <summary>
    /// Snapshots to save at most for each label and kind (item/blocked, item/not-found...) while this object lives; then the next ones of
    /// that label and kind are not saved. 0, the default, for no limit.
    /// </summary>
    public int MaxPerKind { get; set; }

    /// <summary>Snapshots to save at most while this object lives. 0, the default, for no limit.</summary>
    public int MaxSnapshots { get; set; }

    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <inheritdoc />
    public async ValueTask SaveAsync(PageSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Accepts(snapshot.Request) || (Filter != null && !Filter(snapshot))) return;

        var group = $"{snapshot.Request.Label}/{FolderName(snapshot.Kind)}";
        if (!TryTakePlace(group)) return;
        try
        {
            await WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            GivePlaceBack(group);
            throw;
        }
    }

    /// <summary>The label of the request is one of <see cref="Labels"/>, or there are none.</summary>
    protected bool Accepts(ScrapeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Labels.Count == 0 || Labels.Contains(new ScrapeLabel(request.Label));
    }

    /// <summary>Name of the folder of a kind: <c>blocked</c>, <c>retry</c>, <c>not-found</c> or <c>handler-error</c>.</summary>
    public static string FolderName(SnapshotKind kind) => kind switch
    {
        SnapshotKind.Blocked => "blocked",
        SnapshotKind.Retry => "retry",
        SnapshotKind.NotFound => "not-found",
        SnapshotKind.HandlerError => "handler-error",
        _ => kind.ToString().ToLowerInvariant(),
    };

    // ---------------------------------------------------------------- limits

    private bool TryTakePlace(string group)
    {
        if (MaxPerKind <= 0 && MaxSnapshots <= 0) return true;
        lock (_lock)
        {
            if (MaxSnapshots > 0 && _saved >= MaxSnapshots) return false;
            _perKind.TryGetValue(group, out var count);
            if (MaxPerKind > 0 && count >= MaxPerKind) return false;
            _perKind[group] = count + 1;
            _saved++;
            return true;
        }
    }

    private void GivePlaceBack(string group)
    {
        if (MaxPerKind <= 0 && MaxSnapshots <= 0) return;
        lock (_lock)
        {
            if (_perKind.TryGetValue(group, out var count) && count > 0) _perKind[group] = count - 1;
            if (_saved > 0) _saved--;
        }
    }

    // ---------------------------------------------------------------- storage: a folder

    /// <summary>
    /// Saves a snapshot that passed the labels, the filter and the limits. The folder writes <c>{Path}/{label}/{kind}/{time}-{number}</c>
    /// with the extension of the body, and its description next to it.
    /// </summary>
    /// <param name="snapshot">The snapshot; its response is valid only during the call.</param>
    /// <param name="cancellationToken">Cancelled when the run stops.</param>
    protected virtual async ValueTask WriteAsync(PageSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var root = Path is { Length: > 0 } path ? path : throw new InvalidOperationException("Set SnapshotPageLoader.Path: the folder of the snapshots");
        var request = snapshot.Request;
        var response = snapshot.Response;
        var folder = System.IO.Path.Combine(root, PageFiles.SafeName(request.Label.ToLowerInvariant()), FolderName(snapshot.Kind));
        Directory.CreateDirectory(folder);

        var savedAt = Clock.GetUtcNow();
        var name = string.Create(CultureInfo.InvariantCulture, $"{savedAt:yyyyMMdd-HHmmss-fff}-{Interlocked.Increment(ref _sequence):D4}");
        var body = response.Body;
        var bodyFile = name + PageFiles.ExtensionOf(response.ContentType, response.MadeFromText);
        await PageFiles.WriteAsync(System.IO.Path.Combine(folder, bodyFile), body, cancellationToken).ConfigureAwait(false);

        var meta = PageFiles.Json(writer =>
        {
            writer.WriteString("format", Format);
            writer.WriteNumber("version", Version);
            writer.WriteString("kind", FolderName(snapshot.Kind));
            writer.WriteString("reason", snapshot.Reason);
            writer.WriteString("label", request.Label);
            writer.WriteString("method", request.Method.Method);
            writer.WriteString("requestUrl", request.Url);
            if (request.ParentUrl != null) writer.WriteString("parentUrl", request.ParentUrl);
            if (request.Body != null) writer.WriteString("requestBody", request.Body);
            writer.WriteNumber("attempt", request.Attempts);
            writer.WriteNumber("depth", request.Depth);
            writer.WriteNumber("status", response.StatusCode);
            writer.WriteString("url", response.Url);
            writer.WriteString("contentType", response.ContentType);
            writer.WriteBoolean("fromCache", snapshot.FromCache);
            writer.WriteString("savedAt", savedAt);
            writer.WriteString("body", bodyFile);
            writer.WriteNumber("length", body.Length);
            PageFiles.WriteHeaders(writer, response.Headers);
            if (snapshot.Exception != null) writer.WriteString("exception", snapshot.Exception.ToString());
        });
        await PageFiles.WriteAsync(System.IO.Path.Combine(folder, name + PageFiles.MetaSuffix), meta, cancellationToken).ConfigureAwait(false);
    }
}
