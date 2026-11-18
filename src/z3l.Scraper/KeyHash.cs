using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace z3l.Scraper;

/// <summary>
/// Hash of a request key (<see cref="ScrapeRequest.GetUniqueKey()"/>): the first 16 bytes of the SHA-256 of the key in UTF-8. The set of
/// the pages seen and the state file keep it instead of the key, so a page takes 16 bytes there whatever the length of its URL and body.
/// 128 bits, because nothing checks the key behind a hash: two keys with one hash would skip a page as a duplicate, unnoticed.
/// </summary>
/// <remarks>Two <see cref="ulong"/>s, not an <see cref="UInt128"/>: that one is aligned to 16 bytes, and a set entry of it takes 8 more.</remarks>
internal readonly record struct KeyHash(ulong High, ulong Low)
{
    /// <summary>Bytes of a hash in the state file.</summary>
    public const int Size = 16;

    /// <summary>The hash of a key; computed outside of any lock, the SHA-256 of a long POST body takes a while.</summary>
    public static KeyHash Of(string key)
    {
        var max = Encoding.UTF8.GetMaxByteCount(key.Length);
        byte[]? rented = null;
        var utf8 = max <= 1024 ? stackalloc byte[max] : (rented = ArrayPool<byte>.Shared.Rent(max));
        try
        {
            var length = Encoding.UTF8.GetBytes(key, utf8);
            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(utf8[..length], digest);
            return Read(digest);
        }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Reads the 16 bytes written by <see cref="Write"/>.</summary>
    public static KeyHash Read(ReadOnlySpan<byte> bytes) =>
        new(BinaryPrimitives.ReadUInt64BigEndian(bytes), BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));

    /// <summary>Writes the 16 bytes in the order of the SHA-256, the same on every machine.</summary>
    public void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, High);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], Low);
    }

    /// <summary>The bits are spread evenly already; the random seed of <see cref="HashCode"/> keeps a site from filling one bucket on purpose.</summary>
    public override int GetHashCode() => HashCode.Combine(Low);

    /// <summary>The 16 bytes in lowercase hex.</summary>
    public override string ToString() => $"{High:x16}{Low:x16}";
}
