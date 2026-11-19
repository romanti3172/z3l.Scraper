using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using z3l.Scraper.Loading;
using z3l.Scraper.Validation;

namespace z3l.Scraper.RabbitMQ;

/// <summary>Settings of <see cref="RabbitMqLoaderWorker"/>.</summary>
public sealed class RabbitMqLoaderWorkerOptions
{
    /// <summary>The broker, for example <c>amqp://user:password@host:5672/vhost</c>. Or set <see cref="ConnectionFactory"/>.</summary>
    public Uri? Uri { get; set; }

    /// <summary>A factory of your own (TLS, several hosts...); <see cref="Uri"/> is not used then.</summary>
    public ConnectionFactory? ConnectionFactory { get; set; }

    /// <summary>The task queue to take pages from (the one the scraper's loader sends them to).</summary>
    public string Queue { get; set; } = "";

    /// <summary>
    /// Pages loaded at once. The worker takes no more tasks from the broker than this, so a task it has taken is a task it is
    /// loading, which is what timesync relies on.
    /// </summary>
    public ushort Concurrency { get; set; } = 8;

    /// <summary>Id of this worker in the answers (the scraper sees it in the <see cref="RabbitMqPageLoader.WorkerHeader"/> header).</summary>
    public string WorkerId { get; set; } = $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>Time for one load; a slower one is answered as an error, and the scraper retries it.</summary>
    public TimeSpan LoadTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Largest answer the broker takes (<c>max_message_size</c>, 16 MiB by default in RabbitMQ 4); a larger page is answered as an
    /// error that is not retried, instead of being dropped by the broker and sent again and again.
    /// </summary>
    public int MaxMessageSize { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// Checks the worker runs itself so that its loader can rest a proxy that got a block page or a broken one (a loader that
    /// rotates proxies: <see cref="IBlockAwarePageLoader"/>, such as <see cref="HttpPageLoader"/> with a proxy list). The scraper
    /// checks every page again with its own rules. Null: no checks here, the loader only sees connection failures.
    /// </summary>
    public ResponseRulesCollection? Rules { get; set; }

    /// <summary>Declare the task queue (durable, with priorities). Off when it is declared elsewhere with other settings.</summary>
    public bool DeclareQueue { get; set; } = true;
}

/// <summary>
/// Takes pages from a RabbitMQ task queue, loads them with any <see cref="IPageLoader"/> (<see cref="HttpPageLoader"/>, a browser...)
/// and answers to the scraper. Timesync markers are answered at once. A task is acknowledged after its answer is sent, so the task of a
/// worker that died goes to another one.
/// </summary>
/// <example>
/// <code>
/// var proxies = ProxyList.FromFile("proxies.txt");
/// await using var http = new HttpPageLoader(new HttpPageLoaderOptions { Proxies = proxies });
/// await using var worker = new RabbitMqLoaderWorker(http, new RabbitMqLoaderWorkerOptions { Uri = new("amqp://user:pass@rabbit/"), Queue = "loaders.http" });
/// await worker.RunAsync(stoppingToken);
/// </code>
/// </example>
public sealed class RabbitMqLoaderWorker : IAsyncDisposable
{
    private readonly IPageLoader _loader;
    private readonly RabbitMqLoaderWorkerOptions _options;
    private readonly ILogger _logger;
    private readonly Dictionary<string, EffectiveRules> _rules = new(StringComparer.OrdinalIgnoreCase);
    private IConnection? _connection;
    private IChannel? _channel;
    private long _loaded, _failed, _markers;

    /// <summary>Creates a worker; <see cref="RunAsync"/> starts it.</summary>
    public RabbitMqLoaderWorker(IPageLoader loader, RabbitMqLoaderWorkerOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Uri == null && options.ConnectionFactory == null) throw new ArgumentException("Set Uri or ConnectionFactory", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Queue)) throw new ArgumentException("Set Queue", nameof(options));
        if (options.Concurrency == 0) throw new ArgumentOutOfRangeException(nameof(options), "Concurrency must be 1 or more");
        if (options.LoadTimeout <= TimeSpan.Zero && options.LoadTimeout != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(options), "LoadTimeout must be positive");
        _loader = loader;
        _options = options;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Takes and loads pages until <paramref name="cancellationToken"/> is cancelled; the connection comes back by itself when it drops.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var factory = _options.ConnectionFactory ?? new ConnectionFactory { Uri = _options.Uri! };
        factory.AutomaticRecoveryEnabled = true;
        _connection = await factory.CreateConnectionAsync($"z3l.Scraper worker {_options.WorkerId}", cancellationToken).ConfigureAwait(false);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true,
                consumerDispatchConcurrency: _options.Concurrency), cancellationToken).ConfigureAwait(false);

        if (_options.DeclareQueue)
        {
            await _channel.QueueDeclareAsync(_options.Queue, durable: true, exclusive: false, autoDelete: false,
                arguments: Protocol.TaskQueueArguments(), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        await _channel.BasicQosAsync(0, _options.Concurrency, false, cancellationToken).ConfigureAwait(false);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += (_, delivery) => OnTaskAsync(delivery, stop.Token);
        var tag = await _channel.BasicConsumeAsync(_options.Queue, autoAck: false, consumer, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Worker {Worker} takes pages from {Queue}, {Concurrency} at once", _options.WorkerId, _options.Queue, _options.Concurrency);

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        // tasks being loaded are not acknowledged: when the channel closes, the broker gives them to another worker
        try { await _channel.BasicCancelAsync(tag, cancellationToken: CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug("Cancelling the consumer: {Message}", ex.Message); }
        await stop.CancelAsync().ConfigureAwait(false);
        await CloseAsync().ConfigureAwait(false);
    }

    private async Task OnTaskAsync(BasicDeliverEventArgs delivery, CancellationToken token)
    {
        var channel = _channel!;
        var properties = delivery.BasicProperties;
        var replyTo = properties.ReplyTo;
        try
        {
            byte[] answer;
            string type;
            if (properties.Type == Protocol.TimesyncType)
            {
                answer = delivery.Body.ToArray();     // the marker goes back as it came
                type = Protocol.TimesyncType;
                Interlocked.Increment(ref _markers);
            }
            else
            {
                var task = Protocol.DecodeTask(delivery.Body);
                answer = await LoadAsync(task, token).ConfigureAwait(false);
                type = Protocol.ResultType;
            }

            if (string.IsNullOrEmpty(replyTo))
            {
                _logger.LogWarning("A task without ReplyTo in {Queue} was dropped", _options.Queue);
            }
            else
            {
                var reply = new BasicProperties { Type = type, CorrelationId = properties.MessageId, ContentEncoding = type == Protocol.ResultType ? "br" : null };
                await channel.BasicPublishAsync("", replyTo, mandatory: false, reply, answer, token).ConfigureAwait(false);
            }
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // stopping: not acknowledged, the broker gives the task to another worker
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            _logger.LogError(ex, "A bad task in {Queue} was dropped", _options.Queue);
            await RejectAsync(channel, delivery.DeliveryTag, requeue: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // the answer was not sent: the task goes back to the queue
            _logger.LogWarning("The answer to {Queue} was not sent ({Message}): the task goes back to the queue", replyTo, ex.Message);
            await RejectAsync(channel, delivery.DeliveryTag, requeue: true).ConfigureAwait(false);
        }
    }

    private async Task RejectAsync(IChannel channel, ulong tag, bool requeue)
    {
        try { await channel.BasicNackAsync(tag, multiple: false, requeue, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug("Nack failed: {Message}", ex.Message); }
    }

    /// <summary>Loads the page and makes the answer: the page, or an error the scraper retries (or fails, when not retryable).</summary>
    private async Task<byte[]> LoadAsync(LoadTask task, CancellationToken token)
    {
        var request = new ScrapeRequest(task.Url, task.Label)
        {
            Method = new HttpMethod(task.Method),
            Body = task.Body,
            ContentType = task.ContentType,
            Headers = task.Headers,
        };

        PageResponse response;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            if (_options.LoadTimeout != Timeout.InfiniteTimeSpan) timeout.CancelAfter(_options.LoadTimeout);
            try
            {
                response = await _loader.LoadAsync(request, timeout.Token).ConfigureAwait(false)
                           ?? throw new InvalidOperationException($"{_loader.GetType().Name}.LoadAsync returned null");
            }
            catch (NonRetryableException ex)
            {
                return Error(task, ex.Message, nonRetryable: true);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                (_loader as IBlockAwarePageLoader)?.ReportTimeout(request);
                return Error(task, $"timeout after {_options.LoadTimeout.TotalSeconds:0.###} s", nonRetryable: false);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                return Error(task, $"load error: {ex.Message}", nonRetryable: false);
            }
        }

        try
        {
            var text = response.MadeFromText;
            var page = text ? Encoding.UTF8.GetBytes(response.Text) : response.Body;
            CheckForTheLoader(request, response);

            var header = new LoadResultHeader
            {
                Id = task.Id,
                Status = response.StatusCode,
                Url = response.Url,
                ContentType = response.ContentType,
                Headers = response.Headers.Count > 0 ? new Dictionary<string, string>(response.Headers) : null,
                Text = text,
                Worker = _options.WorkerId,
            };
            var answer = Protocol.EncodeResult(header, page);
            if (answer.Length > _options.MaxMessageSize)
            {
                return Error(task, $"the answer is {answer.Length} bytes, more than MaxMessageSize {_options.MaxMessageSize}", nonRetryable: true);
            }
            Interlocked.Increment(ref _loaded);
            return answer;
        }
        finally
        {
            response.ReleaseText();
            try { (_loader as IBlockAwarePageLoader)?.ReportDone(request, response); }     // a proxy session goes on to the next load
            catch (Exception ex) { _logger.LogWarning("ReportDone of {Url} failed: {Message}", request.Url, ex.Message); }
        }
    }

    /// <summary>The worker's own checks: a loader that rotates proxies hears about blocks and broken pages.</summary>
    private void CheckForTheLoader(ScrapeRequest request, PageResponse response)
    {
        if (_options.Rules == null || _loader is not IBlockAwarePageLoader blockAware) return;

        EffectiveRules rules;
        lock (_rules)
        {
            if (!_rules.TryGetValue(request.Label, out rules!)) _rules[request.Label] = rules = _options.Rules.Build(request.Label);
        }

        try
        {
            var verdict = ResponseValidator.Validate(request, response, rules);
            switch (verdict.Outcome)
            {
                case ValidationOutcome.Ok:
                    blockAware.ReportGoodResponse(request, response);
                    break;
                case ValidationOutcome.Blocked:
                    blockAware.ReportBlocked(request, response, verdict.Reason!);
                    break;
                case ValidationOutcome.Retry when response.StatusCode is not (429 or 503):
                    blockAware.ReportBadResponse(request, response, verdict.Reason!);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Checking {Url} for the loader failed: {Message}", request.Url, ex.Message);
        }
    }

    private byte[] Error(LoadTask task, string message, bool nonRetryable)
    {
        Interlocked.Increment(ref _failed);
        _logger.LogDebug("{Url}: {Message}", task.Url, message);
        return Protocol.EncodeResult(new LoadResultHeader { Id = task.Id, Url = task.Url, Error = message, NonRetryable = nonRetryable, Worker = _options.WorkerId }, []);
    }

    /// <summary>Pages loaded, errors answered, timesync markers answered.</summary>
    public override string ToString() =>
        $"{_options.WorkerId} on {_options.Queue}: loaded {Interlocked.Read(ref _loaded)}, errors {Interlocked.Read(ref _failed)}, timesync {Interlocked.Read(ref _markers)}";

    private async Task CloseAsync()
    {
        foreach (var disposable in new IAsyncDisposable?[] { _channel, _connection })
        {
            if (disposable == null) continue;
            try { await disposable.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug("Closing the RabbitMQ connection: {Message}", ex.Message); }
        }
        _channel = null;
        _connection = null;
    }

    /// <summary>Closes the connection if <see cref="RunAsync"/> did not.</summary>
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
