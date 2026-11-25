using System.Buffers.Binary;

namespace Fufucode.Fui;

// Identify the encoded container without allocating or decoding a raster of untrusted dimensions.
internal static class ProjectImage
{
    public static string? MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => null
    };

    public static bool Matches(ReadOnlySpan<byte> data, string mimeType) => mimeType switch
    {
        "image/png" => IsPng(data),
        "image/jpeg" => IsJpeg(data),
        _ => false
    };

    private static bool IsPng(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!data.StartsWith(signature) || data.Length < 33 ||
            BinaryPrimitives.ReadInt32BigEndian(data[8..]) != 13 || !data[12..16].SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadInt32BigEndian(data[16..]) <= 0 || BinaryPrimitives.ReadInt32BigEndian(data[20..]) <= 0)
            return false;

        var hasImageData = false;
        var chunks = data[33..];
        while (chunks.Length >= 12)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(chunks);
            if (length < 0 || length > chunks.Length - 12) return false;
            var type = chunks[4..8];
            if (type.SequenceEqual("IEND"u8)) return hasImageData && length == 0 && chunks.Length == 12;
            if (type.SequenceEqual("IHDR"u8)) return false;
            if (type.SequenceEqual("IDAT"u8) && length != 0) hasImageData = true;
            chunks = chunks[(length + 12)..];
        }
        return false;
    }

    private static bool IsJpeg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8) return false;
        var hasFrame = false;
        var hasScan = false;
        var offset = 2;
        while (offset < data.Length && data[offset++] == 0xff)
        {
            while (offset < data.Length && data[offset] == 0xff) offset++;
            if (offset == data.Length) return false;
            var marker = data[offset++];
            if (marker == 0xd9) return hasScan && offset == data.Length;
            if (marker is 0 or 0xd8 or >= 0xd0 and <= 0xd7 || data.Length - offset < 2) return false;
            if (marker == 0x01) continue;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            if (length < 2 || length > data.Length - offset) return false;
            var segment = data.Slice(offset, length);
            offset += length;
            if (marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc))
            {
                if (length < 8 || segment[7] == 0 || length != 8 + 3 * segment[7] ||
                    BinaryPrimitives.ReadUInt16BigEndian(segment[3..]) == 0 ||
                    BinaryPrimitives.ReadUInt16BigEndian(segment[5..]) == 0) return false;
                hasFrame = true;
            }
            if (marker != 0xda) continue;
            if (!hasFrame || length < 6 || segment[2] == 0 || length != 6 + 2 * segment[2]) return false;
            var scanStart = offset;
            // In compressed scan data FF 00 is a literal FF, and restart markers do not end the scan.
            while (offset < data.Length)
            {
                var next = data[offset..].IndexOf((byte)0xff);
                if (next < 0) return false;
                offset += next;
                while (offset + 1 < data.Length && data[offset + 1] == 0xff) offset++;
                if (offset + 1 >= data.Length || data[offset + 1] is not (0 or >= 0xd0 and <= 0xd7)) break;
                offset += 2;
            }
            if (offset == scanStart) return false;
            hasScan = true;
        }
        return false;
    }
}
