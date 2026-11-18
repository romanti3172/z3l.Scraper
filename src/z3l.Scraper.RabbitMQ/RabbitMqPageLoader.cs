using System.Text;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using z3l.Scraper.Loading;

namespace z3l.Scraper.RabbitMQ;

/// <summary>Settings of <see cref="RabbitMqPageLoader"/>.</summary>
public sealed class RabbitMqPageLoaderOptions
{
    /// <summary>The broker, for example <c>amqp://user:password@host:5672/vhost</c>. Or set <see cref="ConnectionFactory"/>.</summary>
    public Uri? Uri { get; set; }

    /// <summary>A factory of your own (TLS, several hosts...); <see cref="Uri"/> is not used then.</summary>
    public ConnectionFactory? ConnectionFactory { get; set; }

    /// <summary>
    /// Queue the workers answer to. Null: a queue of this loader only, named by the broker and deleted when the loader closes.
    /// A named queue is durable: answers wait in it when the scraper restarts (they are ignored, the scraper sends its pages again).
    /// </summary>
    public string? ResultQueue { get; set; }

    /// <summary>How often a timesync marker goes to each task queue that has tasks not yet taken by a worker.</summary>
    public TimeSpan TimesyncInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a worker may load a page it took: a task without an answer this long after a timesync marker sent after it came
    /// back is lost (the worker died or hung), and the scraper sends it again. Time in the queue does not count.
    /// </summary>
    public TimeSpan ExecutionTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often lost tasks are looked for (also every time a marker comes back).</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Answers taken from the broker at once: the rest wait there, which keeps the memory in check.</summary>
    public ushort ResultPrefetch { get; set; } = 50;

    /// <summary>Declare the task queues (durable, with priorities). Off when they are declared elsewhere with other settings.</summary>
    public bool DeclareQueues { get; set; } = true;

    /// <summary>Clock for the deadlines (tests).</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;
}

/// <summary>
/// Loads pages through remote workers (<see cref="RabbitMqLoaderWorker"/>): every page is a task in a RabbitMQ queue, chosen by its
/// label, and the worker's answer becomes the <see cref="PageResponse"/>. The scraper sends every page at once (a queue can be of any
/// length), and checks, retries and handles the answers as with a local loader.
/// <para>
/// No timeout counts the time a page waits in the queue: a load is lost only when a worker took it (a timesync marker sent after it
/// came back) and did not answer within <see cref="RabbitMqPageLoaderOptions.ExecutionTimeout"/>. A lost load throws
/// <see cref="RemoteLoadException"/>, and the scraper retries it.
/// </para>
/// </summary>
/// <example>
/// <code>
/// await using var loader = new RabbitMqPageLoader(new RabbitMqPageLoaderOptions { Uri = new("amqp://user:pass@rabbit/") })
///     .Queue("loaders.http", PageType.File)      // files: plain HTTP workers
///     .Queue("loaders.browser");                // everything else: browser workers
/// var scraper = new Scraper(loader);
/// </code>
/// </example>
public sealed class RabbitMqPageLoader : QueuePageLoader
{
    private readonly RabbitMqPageLoaderOptions _options;
    private readonly Guid _instance = Guid.NewGuid();
    private readonly List<TaskQueue> _queues = [];
    private TaskQueue? _defaultQueue;

    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly List<Task> _loops = [];
    private IConnection? _connection;
    private IChannel? _publishChannel;
    private IChannel? _resultChannel;
    private volatile string? _resultQueue;
    private volatile bool _started;

    private long _markers;

    /// <summary>Creates the loader; add task queues with <see cref="Queue"/> before the first load.</summary>
    public RabbitMqPageLoader(RabbitMqPageLoaderOptions options, ILogger? logger = null)
        : base(logger, (options ?? throw new ArgumentNullException(nameof(options))).Clock)
    {
        if (options.Uri == null && options.ConnectionFactory == null) throw new ArgumentException("Set Uri or ConnectionFactory", nameof(options));
        if (options.TimesyncInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "TimesyncInterval must be positive");
        if (options.ExecutionTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "ExecutionTimeout must be positive");
        if (options.CheckInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "CheckInterval must be positive");
        if (options.ResultPrefetch == 0) throw new ArgumentOutOfRangeException(nameof(options), "ResultPrefetch must be 1 or more");
        _options = options;
    }

    /// <summary>
    /// A task queue for pages with these labels; without labels, the queue of every other label. Pages of a label that no queue
    /// takes fail at once.
    /// </summary>
    /// <exception cref="InvalidOperationException">The loader has started, a label has a queue already, or there are two queues without labels.</exception>
    public RabbitMqPageLoader Queue(string name, params ScrapeLabel[] labels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(labels);
        if (_started) throw new InvalidOperationException("Queues are added before the first load");

        var queue = _queues.Find(x => x.Name == name);
        if (queue == null)
        {
            queue = new TaskQueue(name, new TimesyncTracker(Clock, _options.ExecutionTimeout));
            _queues.Add(queue);
        }

        if (labels.Length == 0)
        {
            if (_defaultQueue != null && _defaultQueue != queue) throw new InvalidOperationException($"Queue \"{_defaultQueue.Name}\" already takes the other labels");
            _defaultQueue = queue;
        }
        foreach (var label in labels)
        {
            if (_queues.Find(x => x.Labels.Contains(label.Name)) is { } other && other != queue)
            {
                throw new InvalidOperationException($"Label \"{label}\" goes to queue \"{other.Name}\" already");
            }
            queue.Labels.Add(label.Name);
        }
        return this;
    }

    /// <inheritdoc />
    protected override void Accept(RemoteLoad load)
    {
        var queue = QueueOf(load.Request.Label)
                    ?? throw new NonRetryableException($"No RabbitMQ queue takes label \"{load.Request.Label}\": add one with Queue(name, label) or Queue(name)");
        load.State = queue;
        queue.Tracker.Add(load.Id);
    }

    /// <inheritdoc />
    protected override async Task SendAsync(RemoteLoad load, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var queue = (TaskQueue)load.State!;
        await PublishTaskAsync(queue, load.Id, load.Request, cancellationToken).ConfigureAwait(false);
        queue.Tracker.Published(load.Id);
    }

    /// <inheritdoc />
    protected override void OnRemoved(RemoteLoad load) => ((TaskQueue)load.State!).Tracker.Remove(load.Id);

    private TaskQueue? QueueOf(string label) => _queues.Find(x => x.Labels.Contains(label)) ?? _defaultQueue;

    // ---------------------------------------------------------------- start

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_started) return;
        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started) return;
            ObjectDisposedException.ThrowIf(Stopping.IsCancellationRequested, this);
            if (_queues.Count == 0) throw new InvalidOperationException("Add a task queue with Queue(name) first");

            var factory = _options.ConnectionFactory ?? new ConnectionFactory { Uri = _options.Uri! };
            factory.AutomaticRecoveryEnabled = true;
            _connection = await factory.CreateConnectionAsync("z3l.Scraper loader", cancellationToken).ConfigureAwait(false);
            _connection.QueueNameChangedAfterRecoveryAsync += (_, e) =>
            {
                if (e.NameBefore == _resultQueue)
                {
                    Logger.LogWarning("Result queue is {Queue} after the connection came back: answers to the old one are lost, timesync finds them", e.NameAfter);
                    _resultQueue = e.NameAfter;
                }
                return Task.CompletedTask;
            };

            _publishChannel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), cancellationToken).ConfigureAwait(false);
            // answers are handled one at a time, in the order they came: a marker is never handled before the results that came before it
            _resultChannel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: false, publisherConfirmationTrackingEnabled: false,
                    consumerDispatchConcurrency: 1), cancellationToken).ConfigureAwait(false);

            if (_options.DeclareQueues)
            {
                foreach (var queue in _queues)
                {
                    await _publishChannel.QueueDeclareAsync(queue.Name, durable: true, exclusive: false, autoDelete: false,
                        arguments: Protocol.TaskQueueArguments(), cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }

            var declared = _options.ResultQueue is { Length: > 0 } named
                ? await _resultChannel.QueueDeclareAsync(named, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await _resultChannel.QueueDeclareAsync("", durable: false, exclusive: true, autoDelete: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            _resultQueue = declared.QueueName;

            await _resultChannel.BasicQosAsync(0, _options.ResultPrefetch, false, cancellationToken).ConfigureAwait(false);
            var consumer = new AsyncEventingBasicConsumer(_resultChannel);
            consumer.ReceivedAsync += OnAnswerAsync;
            await _resultChannel.BasicConsumeAsync(_resultQueue, autoAck: false, consumer, cancellationToken).ConfigureAwait(false);

            var token = Stopping;
            foreach (var queue in _queues) _loops.Add(Task.Run(() => TimesyncLoopAsync(queue, token), CancellationToken.None));
            _loops.Add(Task.Run(() => CheckLoopAsync(token), CancellationToken.None));

            Logger.LogInformation("RabbitMQ loader: task queues {Queues}, answers to {ResultQueue}", string.Join(", ", _queues.Select(x => x.Name)), _resultQueue);
            _started = true;
        }
        catch
        {
            await CloseAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _startLock.Release();
        }
    }

    // ---------------------------------------------------------------- tasks and markers

    private async Task PublishTaskAsync(TaskQueue queue, Guid id, ScrapeRequest request, CancellationToken cancellationToken)
    {
        var task = new LoadTask
        {
            Id = id,
            Url = request.Url,
            Label = request.Label,
            Method = request.Method.Method,
            Body = request.Body,
            ContentType = request.ContentType,
            Headers = request.Headers is { Count: > 0 } headers ? new Dictionary<string, string>(headers) : null,
        };
        var properties = new BasicProperties
        {
            MessageId = id.ToString("N"),
            Type = Protocol.LoadType,
            ReplyTo = _resultQueue,
            Priority = Protocol.PriorityOf(request.Priority),
            DeliveryMode = DeliveryModes.Persistent,
            ContentType = "application/json",
        };
        await _publishChannel!.BasicPublishAsync("", queue.Name, mandatory: false, properties, Protocol.EncodeTask(task), cancellationToken).ConfigureAwait(false);
    }

    private async Task TimesyncLoopAsync(TaskQueue queue, CancellationToken token)
    {
        using var timer = new PeriodicTimer(_options.TimesyncInterval);
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            if (queue.Tracker.NextMarker() is not { } number) continue;     // every task is taken already
            try
            {
                var marker = new TimesyncMarker { Loader = _instance, Queue = queue.Name, Number = number };
                var properties = new BasicProperties
                {
                    MessageId = $"timesync:{number}",
                    Type = Protocol.TimesyncType,
                    ReplyTo = _resultQueue,
                    Priority = 0,
                    ContentType = "application/json",
                };
                await _publishChannel!.BasicPublishAsync("", queue.Name, mandatory: false, properties, Protocol.EncodeMarker(marker), token).ConfigureAwait(false);
                Interlocked.Increment(ref _markers);
                Logger.LogDebug("Timesync {Number} sent to {Queue}", number, queue.Name);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Logger.LogWarning("Timesync {Number} was not sent to {Queue} ({Message}): the next one covers its tasks", number, queue.Name, ex.Message);
            }
        }
    }

    // ---------------------------------------------------------------- answers

    private async Task OnAnswerAsync(object sender, BasicDeliverEventArgs delivery)
    {
        await WaitWhilePausedAsync(Stopping).ConfigureAwait(false);

        var markerCame = false;
        try
        {
            if (delivery.BasicProperties.Type == Protocol.TimesyncType)
            {
                markerCame = OnMarker(Protocol.DecodeMarker(delivery.Body));
            }
            else
            {
                OnResult(delivery.Body);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "A bad message in {Queue} was dropped", _resultQueue);
        }

        try
        {
            await _resultChannel!.BasicAckAsync(delivery.DeliveryTag, multiple: false, delivery.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!Stopping.IsCancellationRequested)
        {
            Logger.LogWarning("An answer was not acknowledged ({Message}): the broker sends it again", ex.Message);
        }

        if (markerCame) _ = CheckLostAsync();
    }

    private bool OnMarker(TimesyncMarker marker)
    {
        if (marker.Loader != _instance) return false;     // a marker of an earlier run with the same result queue
        var queue = _queues.Find(x => x.Name == marker.Queue);
        if (queue == null) return false;

        var taken = queue.Tracker.MarkerReturned(marker.Number);
        Logger.LogDebug("Timesync {Number} of {Queue} came back: {Taken} tasks are taken by workers", marker.Number, queue.Name, taken);
        return true;
    }

    private void OnResult(ReadOnlyMemory<byte> body)
    {
        var (header, page) = Protocol.DecodeResult(body);
        bool waited;
        if (header.Error != null)
        {
            var message = header.Worker != null ? $"{header.Error} (worker {header.Worker})" : header.Error;
            waited = Fail(header.Id, header.NonRetryable ? new NonRetryableException(message) : new RemoteLoadException(message));
        }
        else
        {
            var headers = new Dictionary<string, string>(header.Headers ?? [], StringComparer.OrdinalIgnoreCase);
            if (header.Worker != null) headers[WorkerHeader] = header.Worker;
            var url = header.Url ?? string.Empty;
            waited = Answer(header.Id, header.Text
                ? PageResponse.FromText(header.Status, url, Encoding.UTF8.GetString(page), header.ContentType, headers)
                : new PageResponse(header.Status, url, page, header.ContentType, headers));
        }
        if (!waited) Logger.LogDebug("An answer for {Url} came after its task was lost or cancelled: ignored", header.Url);
    }

    /// <summary>Response header with the id of the worker that loaded the page.</summary>
    public const string WorkerHeader = "X-Z3l-Worker";

    // ---------------------------------------------------------------- lost tasks

    private async Task CheckLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(_options.CheckInterval);
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            await CheckLostAsync().ConfigureAwait(false);
        }
    }

    private async Task CheckLostAsync()
    {
        if (!await _checkLock.WaitAsync(0).ConfigureAwait(false)) return;     // a check is running
        try
        {
            // answers not read yet may be the ones a deadline waits for: the scraper is paused, or they are on their way
            if (Paused) return;
            if (await _resultChannel!.MessageCountAsync(_resultQueue!, Stopping).ConfigureAwait(false) > 0) return;

            foreach (var queue in _queues)
            {
                foreach (var id in queue.Tracker.TakeLost())
                {
                    Lose(id, $"lost: a worker took it from {queue.Name} and did not answer in {_options.ExecutionTimeout.TotalMinutes:0.#} min");
                }
            }
        }
        catch (Exception ex) when (!Stopping.IsCancellationRequested)
        {
            Logger.LogWarning("Looking for lost tasks failed: {Message}", ex.Message);
        }
        finally
        {
            _checkLock.Release();
        }
    }

    // ---------------------------------------------------------------- status, close

    /// <summary>Sent, answered, failed by workers, lost, late answers, waiting; timesync markers sent, waiting (taken by workers) per queue.</summary>
    public override string ToString() =>
        $"{base.ToString()}, timesync sent {Interlocked.Read(ref _markers)}, " +
        $"queues: {string.Join(", ", _queues.Select(x => $"{x.Name} {x.Tracker.Count} ({x.Tracker.Taken} taken)"))}";

    /// <summary>Stops the loops and closes the connection; the loads that wait for an answer are cancelled then.</summary>
    protected override async ValueTask DisposeAsyncCore()
    {
        try
        {
            await Task.WhenAll(_loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        await CloseAsync().ConfigureAwait(false);
    }

    private async Task CloseAsync()
    {
        foreach (var disposable in new IAsyncDisposable?[] { _resultChannel, _publishChannel, _connection })
        {
            if (disposable == null) continue;
            try { await disposable.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Logger.LogDebug("Closing the RabbitMQ connection: {Message}", ex.Message); }
        }
        _resultChannel = null;
        _publishChannel = null;
        _connection = null;
    }

    private sealed class TaskQueue(string name, TimesyncTracker tracker)
    {
        public string Name { get; } = name;
        public HashSet<string> Labels { get; } = new(StringComparer.OrdinalIgnoreCase);
        public TimesyncTracker Tracker { get; } = tracker;
    }
}
