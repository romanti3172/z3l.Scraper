using System.Buffers.Binary;
using System.Text;

namespace z3l.Scraper.Validation;

/// <summary>
/// Is a loaded file the file (<see cref="ResponseRules.CheckFileContent"/>): proxies answer 200 with a page of their own (squid
/// "The requested URL could not be retrieved", a stub, a login page) or cut the file off. The first bytes tell the format and its end
/// marker tells the file is whole: JPEG, PNG, GIF, WebP and PDF are checked to the end, AVIF/HEIC, BMP, ICO, ZIP and SVG by their start;
/// text that is not SVG (HTML, JSON, a plain error) is not a file. A file of a format it does not know that is not text passes.
/// </summary>
public static class FileContent
{
    private const int TextProbe = 512;

    /// <summary>What is wrong with the file, or null when it looks right.</summary>
    /// <param name="body">The file.</param>
    public static string? Problem(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0) return "empty file";

        if (body.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            // the end of image marker; some files have padding or a block after it
            return Tail(body, 64).IndexOf((ReadOnlySpan<byte>)[0xFF, 0xD9]) >= 0 ? null : "cut jpeg: no FFD9 at the end";
        }
        if (body.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return Tail(body, 64).IndexOf("IEND"u8) >= 0 ? null : "cut png: no IEND chunk";
        }
        if (body.StartsWith("GIF87a"u8) || body.StartsWith("GIF89a"u8))
        {
            return Tail(body, 16).IndexOf((byte)0x3B) >= 0 ? null : "cut gif: no trailer";
        }
        if (body.Length >= 12 && body.StartsWith("RIFF"u8) && body.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            // the RIFF size is the length of the file without its first 8 bytes
            var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4, 4)) + 8;
            return body.Length >= size ? null : $"cut webp: {body.Length} of {size} bytes";
        }
        if (body.StartsWith("%PDF-"u8))
        {
            return Tail(body, 1024).IndexOf("%%EOF"u8) >= 0 ? null : "cut pdf: no %%EOF at the end";
        }
        if ((body.Length >= 12 && body.Slice(4, 4).SequenceEqual("ftyp"u8))     // avif, heic, mp4
            || body.StartsWith("BM"u8)
            || body.StartsWith((ReadOnlySpan<byte>)[0, 0, 1, 0])               // ico
            || body.StartsWith("PK\x03\x04"u8))
        {
            return null;
        }

        if (!LooksLikeText(body)) return null;
        var text = Text(body);
        if (text.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || (text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && text.Contains("<svg", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        return $"text instead of a file: \"{Shorten(text)}\"";
    }

    private static ReadOnlySpan<byte> Tail(ReadOnlySpan<byte> body, int length) => body[Math.Max(0, body.Length - length)..];

    /// <summary>The first bytes are text: no zero bytes, and under 5% control characters other than tabs and line breaks.</summary>
    private static bool LooksLikeText(ReadOnlySpan<byte> body)
    {
        var head = body[..Math.Min(body.Length, TextProbe)];
        var control = 0;
        foreach (var b in head)
        {
            if (b == 0) return false;
            if (b < 0x20 && b is not (0x09 or 0x0A or 0x0D)) control++;
        }
        return control * 20 < head.Length;
    }

    private static string Text(ReadOnlySpan<byte> body)
    {
        var head = body[..Math.Min(body.Length, TextProbe)];
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) head = head[3..];
        return Encoding.UTF8.GetString(head).Trim();
    }

    private static string Shorten(string text)
    {
        var line = string.Join(' ', text.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length > 80 ? line[..80] + "..." : line;
    }
}
