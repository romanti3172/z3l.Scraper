using System.Text.Encodings.Web;
using System.Text.Json;

namespace z3l.Scraper.Loading;

/// <summary>Pages in a folder, for <see cref="CachingPageLoader"/> and <see cref="SnapshotStore"/>: two files each, the body and its description.</summary>
internal static class PageFiles
{
    /// <summary>Ending of the description of a page; the body has the extension of its content type.</summary>
    public const string MetaSuffix = ".meta.json";

    /// <summary>A name that is a valid file or folder name everywhere: letters, digits, '.', '-' and '_'; anything else becomes '_'.</summary>
    public static string SafeName(string value)
    {
        var name = string.Create(Math.Min(value.Length, 100), value, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                var c = source[i];
                span[i] = char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_';
            }
        });
        name = name.Trim('.');
        if (name.Length == 0) return "_";
        // device names Windows keeps for itself, with any extension
        return ReservedNames.Contains(name.Split('.', 2)[0]) ? "_" + name : name;
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>File extension of a body, so a saved page opens in a browser or an editor.</summary>
    public static string ExtensionOf(string? contentType, bool isText)
    {
        if (string.IsNullOrEmpty(contentType)) return isText ? ".html" : ".bin";

        var type = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (type.Contains("html")) return ".html";
        if (type.Contains("json")) return ".json";
        if (type == "image/svg+xml") return ".svg";
        if (type.Contains("xml")) return ".xml";
        if (type.Contains("javascript")) return ".js";
        return type switch
        {
            "text/css" => ".css",
            "text/csv" => ".csv",
            "application/pdf" => ".pdf",
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ when type.StartsWith("text/", StringComparison.Ordinal) => ".txt",
            _ => isText ? ".txt" : ".bin",
        };
    }

    /// <summary>Writes a file whole or not at all: into a temporary file first, then renamed over the old one.</summary>
    public static async Task WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Reads a file, or null when it is not there. Others may replace or delete it meanwhile.</summary>
    public static async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                var bytes = new byte[stream.Length];
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                return bytes;
            }
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException) when (!File.Exists(path))
        {
            return null;     // Windows refuses to open a file that is being deleted
        }
    }

    /// <summary>A description: an indented JSON object, with letters of any language kept as they are, so a person can read it.</summary>
    public static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    public static void WriteHeaders(Utf8JsonWriter writer, IReadOnlyDictionary<string, string> headers)
    {
        writer.WriteStartObject("headers");
        foreach (var header in headers) writer.WriteString(header.Key, header.Value);
        writer.WriteEndObject();
    }

    public static Dictionary<string, string> ReadHeaders(JsonElement root)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("headers", out var element) && element.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in element.EnumerateObject())
            {
                if (header.Value.ValueKind == JsonValueKind.String) headers[header.Name] = header.Value.GetString()!;
            }
        }
        return headers;
    }
}
