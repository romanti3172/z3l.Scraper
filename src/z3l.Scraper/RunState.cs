using System.Globalization;
using System.Text.Json;

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

    /// <summary>UserData has to survive a restart with its type, so only plain values are allowed.</summary>
    public static void CheckUserData(object? value)
    {
        if (value is null or string or bool or int or long or double or decimal or Guid or DateTime or DateTimeOffset or JsonElement) return;
        throw new NotSupportedException(
            $"With ScraperOptions.StatePath, UserData must be a string, a number, a bool, a Guid, a DateTime, a DateTimeOffset or a JsonElement, " +
            $"because it is saved to the state file; got {value.GetType().Name}. Put an object in as JSON: JsonSerializer.SerializeToElement(value).");
    }

    /// <summary>Writes the file atomically: a temporary file first, then a rename over the old one.</summary>
    public static void Save(string path, Snapshot snapshot)
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
                foreach (var request in snapshot.Pending) WriteRequest(writer, request);
                writer.WriteEndArray();

                writer.WriteEndObject();
            }
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, full, overwrite: true);
    }

    /// <exception cref="InvalidDataException">The file is not a state file of this version.</exception>
    public static Snapshot Load(string path)
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
                foreach (var item in pendingArray.EnumerateArray()) pending.Add(ReadRequest(item));
            }
            return new Snapshot(seen, pending);
        }
    }

    private static void WriteRequest(Utf8JsonWriter writer, ScrapeRequest request)
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
        if (request.UserData != null)
        {
            writer.WritePropertyName("userData");
            WriteUserData(writer, request.UserData);
        }
        writer.WriteEndObject();
    }

    private static ScrapeRequest ReadRequest(JsonElement item)
    {
        var request = new ScrapeRequest(item.GetProperty("url").GetString()!, item.GetProperty("label").GetString())
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
            UserData = item.TryGetProperty("userData", out var userData) ? ReadUserData(userData) : null,
        };
        request.Depth = item.TryGetProperty("depth", out var depth) ? depth.GetInt32() : 0;
        return request;
    }

    /// <summary>A value with its type tag, so an int comes back as an int and not as a double or a JsonElement.</summary>
    private static void WriteUserData(Utf8JsonWriter writer, object value)
    {
        writer.WriteStartObject();
        switch (value)
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
            default: throw new NotSupportedException($"UserData of type {value.GetType().Name} cannot be saved");
        }
        writer.WriteEndObject();
    }

    private static object? ReadUserData(JsonElement element)
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
            var type => throw new InvalidDataException($"Unknown UserData type \"{type}\" in the state file"),
        };
    }
}
