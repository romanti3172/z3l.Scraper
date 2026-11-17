using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RabbitMQ.Client;

namespace z3l.Scraper.RabbitMQ;

/// <summary>
/// What goes through the broker. A task queue holds loads and timesync markers; a worker answers both to the queue in
/// <c>ReplyTo</c> of the message. Message type is in <c>BasicProperties.Type</c>, the task id in <c>MessageId</c> / <c>CorrelationId</c>.
/// </summary>
internal static class Protocol
{
    public const string LoadType = "load";
    public const string TimesyncType = "timesync";
    public const string ResultType = "result";

    /// <summary>Priorities of a task queue: High 2, Normal 1, Low 0; a timesync marker goes with 0, after every task sent before it.</summary>
    public const byte MaxPriority = 2;

    /// <summary>Arguments both sides declare a task queue with (a queue declared with other arguments fails).</summary>
    public static Dictionary<string, object?> TaskQueueArguments() => new() { ["x-max-priority"] = (int)MaxPriority };

    public static byte PriorityOf(ScrapePriority priority) => priority switch
    {
        ScrapePriority.High => 2,
        ScrapePriority.Low => 0,
        _ => 1,
    };

    // ---------------------------------------------------------------- tasks

    public static byte[] EncodeTask(LoadTask task) => JsonSerializer.SerializeToUtf8Bytes(task, ProtocolJson.Default.LoadTask);

    public static LoadTask DecodeTask(ReadOnlyMemory<byte> body) =>
        JsonSerializer.Deserialize(body.Span, ProtocolJson.Default.LoadTask) ?? throw new InvalidDataException("Empty task message");

    public static byte[] EncodeMarker(TimesyncMarker marker) => JsonSerializer.SerializeToUtf8Bytes(marker, ProtocolJson.Default.TimesyncMarker);

    public static TimesyncMarker DecodeMarker(ReadOnlyMemory<byte> body) =>
        JsonSerializer.Deserialize(body.Span, ProtocolJson.Default.TimesyncMarker) ?? throw new InvalidDataException("Empty timesync message");

    // ---------------------------------------------------------------- results

    /// <summary>
    /// A result: brotli of [header length, int32 little endian][header json][page bytes]. Pages are text, so they shrink 5-10 times,
    /// and the bytes do not go through base64.
    /// </summary>
    public static byte[] EncodeResult(LoadResultHeader header, ReadOnlySpan<byte> page)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(header, ProtocolJson.Default.LoadResultHeader);
        var raw = new byte[4 + json.Length + page.Length];
        BinaryPrimitives.WriteInt32LittleEndian(raw, json.Length);
        json.CopyTo(raw, 4);
        page.CopyTo(raw.AsSpan(4 + json.Length));

        using var output = new MemoryStream(raw.Length / 4 + 64);
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(raw);
        }
        return output.ToArray();
    }

    public static (LoadResultHeader Header, byte[] Page) DecodeResult(ReadOnlyMemory<byte> body)
    {
        using var input = new MemoryStream(body.ToArray(), writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        brotli.CopyTo(raw);

        var bytes = raw.GetBuffer().AsSpan(0, (int)raw.Length);
        if (bytes.Length < 4) throw new InvalidDataException("Result message too short");
        var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (jsonLength < 0 || jsonLength > bytes.Length - 4) throw new InvalidDataException("Bad result header length");

        var header = JsonSerializer.Deserialize(bytes.Slice(4, jsonLength), ProtocolJson.Default.LoadResultHeader)
                     ?? throw new InvalidDataException("Empty result header");
        return (header, bytes[(4 + jsonLength)..].ToArray());
    }

    public static string? GetString(IReadOnlyBasicProperties properties, string name) =>
        properties.Headers?.TryGetValue(name, out var value) == true ? value switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string text => text,
            _ => value?.ToString(),
        } : null;
}

/// <summary>A page to load.</summary>
internal sealed record LoadTask
{
    public required Guid Id { get; init; }
    public required string Url { get; init; }
    public string Label { get; init; } = ScrapeLabel.DefaultName;
    public string Method { get; init; } = "GET";
    public string? Body { get; init; }
    public string? ContentType { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
}

/// <summary>A timesync marker: the worker that takes it answers it at once, so every task sent before it is taken by then.</summary>
internal sealed record TimesyncMarker
{
    /// <summary>The loader that sent it (a restarted scraper ignores markers of its earlier life).</summary>
    public required Guid Loader { get; init; }
    public required string Queue { get; init; }
    public required long Number { get; init; }
}

/// <summary>What the worker got. <see cref="Error"/> set: no response (the scraper retries, or fails it when <see cref="NonRetryable"/>).</summary>
internal sealed record LoadResultHeader
{
    public required Guid Id { get; init; }
    public int Status { get; init; }
    public string? Url { get; init; }
    public string? ContentType { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    /// <summary>The page is text in UTF-8 (a browser's page), whatever its Content-Type says.</summary>
    public bool Text { get; init; }
    public string? Error { get; init; }
    public bool NonRetryable { get; init; }
    public string? Worker { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LoadTask))]
[JsonSerializable(typeof(TimesyncMarker))]
[JsonSerializable(typeof(LoadResultHeader))]
internal sealed partial class ProtocolJson : JsonSerializerContext;
