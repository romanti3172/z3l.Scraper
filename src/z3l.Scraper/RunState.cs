using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace z3l.Scraper;

/// <summary>
/// The state of a run (<see cref="ScraperOptions.StatePath"/>), in two files. The state file holds the requests not finished and how
/// many hashes of the pages seen are saved; the journal next to it (<see cref="SeenPath"/>) holds those hashes (<see cref="KeyHash"/>),
/// 16 bytes each, and a save appends only the new ones, so a long run is not written whole every time.
/// Written with Utf8JsonWriter and read with JsonDocument, no reflection: trimming and AOT safe.
/// </summary>
internal static class RunState
{
    private const string Format = "z3l.Scraper state";
    private const int Version = 2;      // version 1 kept the keys of the pages seen in the state file itself

    /// <summary>A run read back.</summary>
    /// <param name="Stored">Hashes of the pages seen that the journal holds: all of them, or none when the file is of version 1.</param>
    /// <param name="Pending">The requests not finished.</param>
    internal sealed record Saved(long Stored, IReadOnlyCollection<ScrapeRequest> Pending);

    /// <summary>The journal of the hashes of the pages seen, next to the state file.</summary>
    public static string SeenPath(string path) => path + ".seen";

    /// <summary>
    /// Saves the run: appends the hashes seen since the last save to the journal, after the <paramref name="stored"/> ones it holds,
    /// then writes the state file that counts them. The state file is written atomically (a temporary file first, then a rename over
    /// the old one) and only once the journal is on the disk, so hashes appended by a save that failed or by a process that died count
    /// for nothing: the next save writes over them.
    /// </summary>
    public static void Save(string path, long stored, KeyHash[] seen, IReadOnlyCollection<ScrapeRequest> pending, StateDataCodec codec)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        AppendSeen(SeenPath(full), stored, seen);

        var temp = full + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
        {
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("format", Format);
                writer.WriteNumber("version", Version);
                writer.WriteString("savedAt", DateTimeOffset.UtcNow);
                writer.WriteNumber("seenCount", stored + seen.Length);     // the first hashes of the journal; any after them are not saved

                writer.WriteStartArray("pending");
                foreach (var request in pending) WriteRequest(writer, request, codec);
                writer.WriteEndArray();

                writer.WriteEndObject();
            }
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, full, overwrite: true);
    }

    private static void AppendSeen(string path, long stored, KeyHash[] seen)
    {
        // a journal that has hashes to keep must be there: an empty one in its place would lose them
        using var journal = new FileStream(path, stored == 0 ? FileMode.OpenOrCreate : FileMode.Open, FileAccess.Write, FileShare.Read, 65536);
        if (journal.Length < stored * KeyHash.Size)
        {
            throw new InvalidDataException($"{path} lost hashes of the pages seen: it holds {journal.Length / KeyHash.Size} of {stored}");
        }

        journal.Position = stored * KeyHash.Size;
        const int Batch = 4096;
        var buffer = new byte[Math.Min(seen.Length, Batch) * KeyHash.Size];
        for (var done = 0; done < seen.Length;)
        {
            var count = Math.Min(seen.Length - done, Batch);
            for (var i = 0; i < count; i++) seen[done + i].Write(buffer.AsSpan(i * KeyHash.Size));
            journal.Write(buffer, 0, count * KeyHash.Size);
            done += count;
        }
        journal.Flush();
        journal.SetLength(journal.Position);        // cuts what a save that failed left after them
        journal.Flush(flushToDisk: true);
    }

    /// <summary>Deletes the state file and then its journal: a journal left without its state file is written over by the next run.</summary>
    public static void Delete(string path)
    {
        File.Delete(path);
        File.Delete(SeenPath(path));
    }

    /// <summary>Reads a saved run and adds the hashes of the pages seen to <paramref name="seen"/>.</summary>
    /// <exception cref="InvalidDataException">The file is not a state file of a version this library reads, or its journal is short.</exception>
    public static Saved Load(string path, StateDataCodec codec, HashSet<KeyHash> seen)
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
            if (version is not (1 or Version))
            {
                throw new InvalidDataException($"{path} is a state file of version {version}; this z3l.Scraper reads versions 1 and {Version}");
            }

            long stored = 0;
            if (version == 1)
            {
                // the keys themselves: hashed here, and the first save puts the hashes into the journal
                if (root.TryGetProperty("seen", out var keys))
                {
                    foreach (var key in keys.EnumerateArray())
                    {
                        if (key.GetString() is { } value) seen.Add(KeyHash.Of(value));
                    }
                }
            }
            else
            {
                if (!root.TryGetProperty("seenCount", out var count) || !count.TryGetInt64(out stored) || stored < 0)
                {
                    throw new InvalidDataException($"{path} is not a z3l.Scraper state file: no seenCount");
                }
                ReadSeen(path, stored, seen);
            }

            var pending = new List<ScrapeRequest>();
            if (root.TryGetProperty("pending", out var pendingArray))
            {
                foreach (var item in pendingArray.EnumerateArray()) pending.Add(ReadRequest(item, codec));
            }
            return new Saved(stored, pending);
        }
    }

    /// <summary>Reads the first <paramref name="count"/> hashes of the journal: those the state file counts.</summary>
    private static void ReadSeen(string path, long count, HashSet<KeyHash> seen)
    {
        if (count == 0) return;

        var journalPath = SeenPath(Path.GetFullPath(path));
        FileStream journal;
        try
        {
            journal = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        }
        catch (FileNotFoundException)
        {
            throw Short(0);
        }

        using (journal)
        {
            if (journal.Length < count * KeyHash.Size) throw Short(journal.Length / KeyHash.Size);

            seen.EnsureCapacity((int)Math.Min(count, Array.MaxLength));
            var buffer = new byte[64 * 1024];       // a multiple of KeyHash.Size
            for (var left = count * KeyHash.Size; left > 0;)
            {
                var length = (int)Math.Min(buffer.Length, left);
                journal.ReadExactly(buffer, 0, length);
                for (var i = 0; i < length; i += KeyHash.Size) seen.Add(KeyHash.Read(buffer.AsSpan(i)));
                left -= length;
            }
        }

        InvalidDataException Short(long holds) => new(
            $"{journalPath} holds {holds} hashes of the pages seen, and {path} counts {count}: bring that file back, or delete {path} to start the run over");
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
