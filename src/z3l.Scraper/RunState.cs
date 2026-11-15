using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace z3l.Scraper;

/// <summary>
/// The state file of a run (<see cref="ScraperOptions.StatePath"/>): the keys of the pages seen and the requests not finished.
/// Written with Utf8JsonWriter and read with JsonDocument, no reflection: trimming and AOT safe.
/// </summary>
internal static class RunState
{
    private const string Format = "z3l.Scraper state";
    private const int Version = 1;

    internal sealed record Snapshot(IReadOnlyCollection<string> Seen, IReadOnlyCollection<ScrapeRequest> Pending);

    /// <summary>Writes the file atomically: a temporary file first, then a rename over the old one.</summary>
    public static void Save(string path, Snapshot snapshot, StateDataCodec codec)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = full + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
        {
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("format", Format);
                writer.WriteNumber("version", Version);
                writer.WriteString("savedAt", DateTimeOffset.UtcNow);

                writer.WriteStartArray("seen");
                foreach (var key in snapshot.Seen) writer.WriteStringValue(key);
                writer.WriteEndArray();

                writer.WriteStartArray("pending");
                foreach (var request in snapshot.Pending) WriteRequest(writer, request, codec);
                writer.WriteEndArray();

                writer.WriteEndObject();
            }
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, full, overwrite: true);
    }

    /// <exception cref="InvalidDataException">The file is not a state file of this version.</exception>
    public static Snapshot Load(string path, StateDataCodec codec)
    {
        using var stream = File.OpenRead(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stream);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} is not a z3l.Scraper state file: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != Format)
            {
                throw new InvalidDataException($"{path} is not a z3l.Scraper state file");
            }
            var version = root.TryGetProperty("version", out var v) && v.TryGetInt32(out var number) ? number : 0;
            if (version != Version)
            {
                throw new InvalidDataException($"{path} is a state file of version {version}; this z3l.Scraper reads version {Version}");
            }

            var seen = new List<string>();
            if (root.TryGetProperty("seen", out var seenArray))
            {
                foreach (var key in seenArray.EnumerateArray())
                {
                    if (key.GetString() is { } value) seen.Add(value);
                }
            }

            var pending = new List<ScrapeRequest>();
            if (root.TryGetProperty("pending", out var pendingArray))
            {
                foreach (var item in pendingArray.EnumerateArray()) pending.Add(ReadRequest(item, codec));
            }
            return new Snapshot(seen, pending);
        }
    }

    private static void WriteRequest(Utf8JsonWriter writer, ScrapeRequest request, StateDataCodec codec)
    {
        writer.WriteStartObject();
        writer.WriteString("url", request.Url);
        writer.WriteString("label", request.Label);
        if (request.Method != HttpMethod.Get) writer.WriteString("method", request.Method.Method);
        if (request.Body != null) writer.WriteString("body", request.Body);
        if (request.ContentType != null) writer.WriteString("contentType", request.ContentType);
        if (request.Headers is { Count: > 0 } headers)
        {
            writer.WriteStartObject("headers");
            foreach (var header in headers) writer.WriteString(header.Key, header.Value);
            writer.WriteEndObject();
        }
        if (request.Priority != ScrapePriority.Normal) writer.WriteNumber("priority", (int)request.Priority);
        if (request.MaxRetries is { } maxRetries) writer.WriteNumber("maxRetries", maxRetries);
        if (request.UniqueKey != null) writer.WriteString("uniqueKey", request.UniqueKey);
        if (request.SkipDuplicateCheck) writer.WriteBoolean("skipDuplicateCheck", true);
        if (request.Depth != 0) writer.WriteNumber("depth", request.Depth);
        if (request.Data != null)
        {
            writer.WritePropertyName("data");
            codec.Write(writer, request.Label, request.Data);
        }
        writer.WriteEndObject();
    }

    private static ScrapeRequest ReadRequest(JsonElement item, StateDataCodec codec)
    {
        var label = item.GetProperty("label").GetString()!;
        var request = new ScrapeRequest(item.GetProperty("url").GetString()!, label)
        {
            Method = item.TryGetProperty("method", out var method) ? new HttpMethod(method.GetString()!) : HttpMethod.Get,
            Body = item.TryGetProperty("body", out var body) ? body.GetString() : null,
            ContentType = item.TryGetProperty("contentType", out var contentType) ? contentType.GetString() : null,
            Headers = item.TryGetProperty("headers", out var headers)
                ? headers.EnumerateObject().ToDictionary(h => h.Name, h => h.Value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                : null,
            Priority = item.TryGetProperty("priority", out var priority) ? (ScrapePriority)priority.GetInt32() : ScrapePriority.Normal,
            MaxRetries = item.TryGetProperty("maxRetries", out var maxRetries) ? maxRetries.GetInt32() : null,
            UniqueKey = item.TryGetProperty("uniqueKey", out var uniqueKey) ? uniqueKey.GetString() : null,
            SkipDuplicateCheck = item.TryGetProperty("skipDuplicateCheck", out var skip) && skip.GetBoolean(),
            // "userData": files of 0.1.0-preview.3
            Data = item.TryGetProperty("data", out var data) || item.TryGetProperty("userData", out data) ? codec.Read(label, data) : null,
        };
        request.Depth = item.TryGetProperty("depth", out var depth) ? depth.GetInt32() : 0;
        return request;
    }
}

/// <summary>
/// <see cref="ScrapeRequest.Data"/> in the state file. Plain values are written with a type tag, so an int comes back as an int.
/// Data of a typed handler (<c>Route&lt;TData&gt;</c>) is written as JSON by <see cref="ScraperOptions.StateJson"/>
/// and read back as the handler's type.
/// </summary>
internal sealed class StateDataCodec(Func<string, ScrapeRoute?> findRoute, IJsonTypeInfoResolver? resolver)
{
    private readonly JsonSerializerOptions? _json = resolver == null ? null : new JsonSerializerOptions { TypeInfoResolver = resolver };
    private readonly ConcurrentDictionary<Type, JsonTypeInfo> _typeInfos = new();

    /// <summary>Fails at once, when the request is queued, rather than when the state is saved.</summary>
    public void Check(string label, object? data)
    {
        if (IsPlain(data)) return;
        TypeInfo(DataType(label, data!));
    }

    public void Write(Utf8JsonWriter writer, string label, object data)
    {
        writer.WriteStartObject();
        switch (data)
        {
            case string s: writer.WriteString("t", "string"); writer.WriteString("v", s); break;
            case bool b: writer.WriteString("t", "bool"); writer.WriteBoolean("v", b); break;
            case int i: writer.WriteString("t", "int"); writer.WriteNumber("v", i); break;
            case long l: writer.WriteString("t", "long"); writer.WriteNumber("v", l); break;
            case double d: writer.WriteString("t", "double"); writer.WriteString("v", d.ToString("R", CultureInfo.InvariantCulture)); break;
            case decimal m: writer.WriteString("t", "decimal"); writer.WriteString("v", m.ToString(CultureInfo.InvariantCulture)); break;
            case Guid g: writer.WriteString("t", "guid"); writer.WriteString("v", g); break;
            case DateTime dt: writer.WriteString("t", "datetime"); writer.WriteString("v", dt.ToString("O", CultureInfo.InvariantCulture)); break;
            case DateTimeOffset dto: writer.WriteString("t", "datetimeoffset"); writer.WriteString("v", dto.ToString("O", CultureInfo.InvariantCulture)); break;
            case JsonElement json: writer.WriteString("t", "json"); writer.WritePropertyName("v"); json.WriteTo(writer); break;
            default:
                writer.WriteString("t", "typed");
                writer.WritePropertyName("v");
                JsonSerializer.Serialize(writer, data, TypeInfo(DataType(label, data)));
                break;
        }
        writer.WriteEndObject();
    }

    public object? Read(string label, JsonElement element)
    {
        var value = element.GetProperty("v");
        return element.GetProperty("t").GetString() switch
        {
            "string" => value.GetString(),
            "bool" => value.GetBoolean(),
            "int" => value.GetInt32(),
            "long" => value.GetInt64(),
            "double" => double.Parse(value.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture),
            "decimal" => decimal.Parse(value.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture),
            "guid" => value.GetGuid(),
            "datetime" => DateTime.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            "datetimeoffset" => DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            "json" => value.Clone(),
            "typed" => value.Deserialize(TypeInfo(findRoute(label)?.DataType
                       ?? throw new InvalidDataException($"The state file has typed data for \"{label}\", and its handler takes no data: register it with Route<TData>"))),
            var type => throw new InvalidDataException($"Unknown data type \"{type}\" in the state file"),
        };
    }

    private static bool IsPlain(object? value) =>
        value is null or string or bool or int or long or double or decimal or Guid or DateTime or DateTimeOffset or JsonElement;

    private Type DataType(string label, object data) => findRoute(label)?.DataType ?? throw new NotSupportedException(
        $"With ScraperOptions.StatePath, data of \"{label}\" ({TypeName(data.GetType())}) must be a string, a number, a bool, a Guid, a DateTime(Offset) " +
        $"or a JsonElement, or the label needs a typed handler, Route<{TypeName(data.GetType())}>, so the data can be saved as JSON");

    /// <summary>List&lt;Int32&gt;, not List`1.</summary>
    internal static string TypeName(Type type) => type.IsGenericType
        ? $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>"
        : type.Name;

    private JsonTypeInfo TypeInfo(Type type) => _typeInfos.GetOrAdd(type, t =>
    {
        if (_json == null)
        {
            throw new NotSupportedException(
                $"With ScraperOptions.StatePath, typed data ({t.Name}) is saved as JSON: set ScraperOptions.StateJson to a JsonSerializerContext " +
                $"with [JsonSerializable(typeof({TypeName(t)}))], or to new DefaultJsonTypeInfoResolver() in an app that is not trimmed");
        }
        try
        {
            return _json.GetTypeInfo(t);
        }
        catch (NotSupportedException ex)
        {
            throw new NotSupportedException($"ScraperOptions.StateJson cannot save {TypeName(t)}: add [JsonSerializable(typeof({TypeName(t)}))] to its context", ex);
        }
    });
}
