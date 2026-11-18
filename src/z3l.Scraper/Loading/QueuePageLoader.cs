using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace z3l.Scraper.Loading;

/// <summary>A load handed to remote workers or to a service, waiting for its answer.</summary>
public sealed class RemoteLoad
{
    internal RemoteLoad(ScrapeRequest request, CancellationToken cancellationToken)
    {
        Request = request;
        CancellationToken = cancellationToken;
    }

    /// <summary>Id of the load, which its answer comes back with.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>The request.</summary>
    public ScrapeRequest Request { get; }

    /// <summary>Cancelled when the scraper gives the load up: the run stops.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Anything the loader keeps with the load: the queue it went to, the call that loads it.</summary>
    public object? State { get; set; }

    internal TaskCompletionSource<PageResponse> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Base of the loaders that hand pages to someone else and wait for the answers: remote workers behind a queue
/// (<c>RabbitMqPageLoader</c>), a scraping service (<see cref="ServicePageLoader"/>). It is an <see cref="IQueuePageLoader"/>: the
/// scraper gives it every request at once, and <see cref="ScraperOptions.RequestTimeout"/> does not count the time a load waits.
/// <para>
/// It keeps the loads that wait for their answers. A derived class sends a load (<see cref="SendAsync"/>) and, when the answer comes,
/// gives it with <see cref="Answer"/>, <see cref="Fail"/> or <see cref="Lose"/>; an answer for a load that is not waiting any more
/// (lost, cancelled) is late and dropped. While the scraper pauses the loader (its handlers are behind), <see cref="Clock"/> stands
/// still, so that time does not count against a load's deadline, and <see cref="WaitWhilePausedAsync"/> holds what should wait.
/// </para>
/// </summary>
public abstract class QueuePageLoader : IQueuePageLoader, IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, RemoteLoad> _pending = new();
    private readonly PausableClock _clock;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopping;
    private readonly object _pauseLock = new();
    private bool _paused;
    private TaskCompletionSource _resumed = Completed();
    private long _sent, _answered, _failed, _lost, _late;

    /// <summary>Creates the loader.</summary>
    /// <param name="logger">Logger of the loader.</param>
    /// <param name="clock">Time of the deadlines; the system clock when null.</param>
    protected QueuePageLoader(ILogger? logger = null, TimeProvider? clock = null)
    {
        Logger = logger ?? NullLogger.Instance;
        _clock = new PausableClock(clock ?? TimeProvider.System);
        _stopping = _stop.Token;
    }

    /// <summary>Logger of the loader.</summary>
    protected ILogger Logger { get; }

    /// <summary>Time for the deadlines of the loads: it stands still while the loader is paused.</summary>
    protected TimeProvider Clock => _clock;

    /// <summary>Cancelled when the loader is disposed.</summary>
    protected CancellationToken Stopping => _stopping;

    /// <inheritdoc />
    public bool Paused
    {
        get
        {
            lock (_pauseLock) return _paused;
        }
        set
        {
            lock (_pauseLock)
            {
                if (_paused == value) return;
                _paused = value;
                if (value)
                {
                    _clock.Pause();     // answers wait now: the deadlines stand still
                    _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                else
                {
                    _clock.Resume();
                    _resumed.TrySetResult();
                }
            }
            OnPausedChanged(value);
        }
    }

    /// <summary>Loads that wait for their answers.</summary>
    public int Pending => _pending.Count;

    /// <inheritdoc />
    public async Task<PageResponse> LoadAsync(ScrapeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_stopping.IsCancellationRequested, this);

        var load = new RemoteLoad(request, cancellationToken);
        Accept(load);
        _pending[load.Id] = load;

        await using var cancel = cancellationToken.Register(static state =>
        {
            var (loader, load) = ((QueuePageLoader, RemoteLoad))state!;
            if (loader.TryRemove(load.Id, out _)) load.Result.TrySetCanceled(load.CancellationToken);
        }, (this, load)).ConfigureAwait(false);

        try
        {
            await SendAsync(load, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _sent);
        }
        catch
        {
            TryRemove(load.Id, out _);     // not sent: the scraper retries it as a load error
            throw;
        }
        return await load.Result.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Takes a load before it waits: picks where it goes and keeps that in <see cref="RemoteLoad.State"/>. Throw here when it cannot
    /// go anywhere (<see cref="NonRetryableException"/>): nothing waits for it yet.
    /// </summary>
    protected virtual void Accept(RemoteLoad load)
    {
    }

    /// <summary>
    /// Sends a load; its answer comes later through <see cref="Answer"/>, <see cref="Fail"/> or <see cref="Lose"/>. An exception here
    /// means it was not sent, and the scraper retries it.
    /// </summary>
    protected abstract Task SendAsync(RemoteLoad load, CancellationToken cancellationToken);

    /// <summary>A load left the waiting ones: answered, failed, lost, cancelled or not sent.</summary>
    protected virtual void OnRemoved(RemoteLoad load)
    {
    }

    /// <summary>The scraper paused or resumed the loader.</summary>
    protected virtual void OnPausedChanged(bool paused)
    {
    }

    /// <summary>The answer of a load: the scraper gets the page. False when the load is not waiting any more: the answer is late.</summary>
    protected bool Answer(Guid id, PageResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!TryRemove(id, out var load))
        {
            Interlocked.Increment(ref _late);
            return false;
        }
        Interlocked.Increment(ref _answered);
        load.Result.TrySetResult(response);
        return true;
    }

    /// <summary>
    /// The load failed where it went, with no page: the scraper retries it, or fails it at once on <see cref="NonRetryableException"/>.
    /// False when the load is not waiting any more.
    /// </summary>
    protected bool Fail(Guid id, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!TryRemove(id, out var load))
        {
            Interlocked.Increment(ref _late);
            return false;
        }
        Interlocked.Increment(ref _failed);
        load.Result.TrySetException(error);
        return true;
    }

    /// <summary>The load is lost, no answer came in time: it throws <see cref="RemoteLoadException"/>, and the scraper retries it.</summary>
    protected bool Lose(Guid id, string reason)
    {
        if (!TryRemove(id, out var load)) return false;
        Interlocked.Increment(ref _lost);
        load.Result.TrySetException(new RemoteLoadException(reason));
        return true;
    }

    /// <summary>A load that waits for its answer.</summary>
    protected bool TryGetPending(Guid id, [MaybeNullWhen(false)] out RemoteLoad load) => _pending.TryGetValue(id, out load);

    /// <summary>Waits while the loader is paused.</summary>
    protected async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task resumed;
            lock (_pauseLock)
            {
                if (!_paused) return;
                resumed = _resumed.Task;
            }
            await resumed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryRemove(Guid id, [MaybeNullWhen(false)] out RemoteLoad load)
    {
        if (!_pending.TryRemove(id, out load)) return false;
        try
        {
            OnRemoved(load);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "OnRemoved threw");
        }
        return true;
    }

    /// <summary>Sent, answered, failed where they went, lost, late answers, waiting; paused.</summary>
    public override string ToString() =>
        $"sent {Interlocked.Read(ref _sent)}, answered {Interlocked.Read(ref _answered)}, failed remotely {Interlocked.Read(ref _failed)}, " +
        $"lost {Interlocked.Read(ref _lost)}, late answers {Interlocked.Read(ref _late)}, pending {Pending}{(Paused ? ", paused" : "")}";

    /// <summary>Stops the loader (<see cref="Stopping"/>, <see cref="DisposeAsyncCore"/>); loads that wait for an answer are cancelled.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_stopping.IsCancellationRequested) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        await DisposeAsyncCore().ConfigureAwait(false);
        foreach (var id in _pending.Keys)
        {
            if (TryRemove(id, out var load)) load.Result.TrySetCanceled();
        }
        _stop.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Stops what the derived class runs (its loops, its connections); <see cref="Stopping"/> is cancelled already.</summary>
    protected virtual ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }
}
