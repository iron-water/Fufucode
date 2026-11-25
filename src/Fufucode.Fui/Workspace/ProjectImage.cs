using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace Fufucode.Fui;

// Identify and validate image containers safely before decoding untrusted pixel dimensions.
internal static class ProjectImage
{
    public static string? MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" or ".apng" => "image/png",
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" or ".dib" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".ico" => "image/x-icon",
        ".cur" => "image/x-win-bitmap",
        ".svg" or ".svgz" => "image/svg+xml",
        ".tga" => "image/x-tga",
        ".qoi" => "image/qoi",
        ".ppm" => "image/x-portable-pixmap",
        ".pgm" => "image/x-portable-graymap",
        ".pbm" => "image/x-portable-bitmap",
        ".pnm" => "image/x-portable-anymap",
        ".pam" => "image/x-portable-arbitrarymap",
        ".ff" => "image/x-farbfeld",
        ".hdr" => "image/vnd.radiance",
        ".wbmp" => "image/vnd.wap.wbmp",
        ".heic" => "image/heic",
        ".heif" or ".hif" => "image/heif",
        ".avif" => "image/avif",
        ".jxl" => "image/jxl",
        ".jxr" or ".wdp" or ".hdp" => "image/vnd.ms-photo",
        ".dds" => "image/vnd.ms-dds",
        ".dng" => "image/x-adobe-dng",
        ".cr2" or ".cr3" or ".nef" or ".arw" or ".orf" or ".rw2" or ".raf" or ".pef" or ".srw" => "image/x-raw",
        _ => null
    };

    public static bool IsNativeModelMime(string mimeType) => mimeType is
        "image/png" or "image/jpeg" or "image/webp" or "image/gif";

    public static bool Matches(ReadOnlySpan<byte> data, string mimeType) => mimeType switch
    {
        "image/png" => IsPng(data),
        "image/jpeg" => IsJpeg(data),
        _ => HasSignature(data, mimeType) && ImageDecoder.TryDecode(data, mimeType) is not null
    };

    public static bool TryInspect(ReadOnlySpan<byte> data, string mimeType, out int width, out int height,
        out string formatLabel, out DecodedImage? decoded)
    {
        width = height = 0;
        formatLabel = "";
        decoded = null;
        if (mimeType is "image/png" or "image/jpeg")
        {
            if (!Matches(data, mimeType)) return false;
            if (TrySkiaDimensions(data, out width, out height))
            {
                formatLabel = mimeType == "image/png" ? "PNG" : "JPEG";
                return true;
            }
            return false;
        }
        if (!HasSignature(data, mimeType)) return false;
        decoded = ImageDecoder.TryDecode(data, mimeType);
        if (decoded is null) return false;
        width = decoded.Width;
        height = decoded.Height;
        formatLabel = decoded.FormatLabel;
        return true;
    }

    private static bool TrySkiaDimensions(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        try
        {
            using var skData = SKData.CreateCopy(data);
            using var codec = SKCodec.Create(skData);
            if (codec is null || !ImageDecoder.IsSafeDimensions(codec.Info.Width, codec.Info.Height)) return false;
            var swap = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            width = swap ? codec.Info.Height : codec.Info.Width;
            height = swap ? codec.Info.Width : codec.Info.Height;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static bool HasSignature(ReadOnlySpan<byte> data, string mimeType) => mimeType switch
    {
        "image/webp" => data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8),
        "image/gif" => data.Length >= 6 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)),
        "image/bmp" => (data.Length >= 18 && data[0] == (byte)'B' && data[1] == (byte)'M') || ImageDecoder.LooksLikeDib(data),
        "image/tiff" => IsTiffHeader(data),
        "image/x-icon" => data.Length >= 22 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0,
        "image/x-win-bitmap" => ImageDecoder.IsCur(data),
        "image/svg+xml" => ImageDecoder.LooksLikeSvg(data) || (data.Length >= 10 && data[0] == 0x1f && data[1] == 0x8b),
        "image/x-tga" => ImageDecoder.LooksLikeTga(data),
        "image/qoi" => ImageDecoder.IsQoi(data),
        "image/x-portable-pixmap" => ImageDecoder.IsNetpbm(data) && data[1] is (byte)'3' or (byte)'6',
        "image/x-portable-graymap" => ImageDecoder.IsNetpbm(data) && data[1] is (byte)'2' or (byte)'5',
        "image/x-portable-bitmap" => ImageDecoder.IsNetpbm(data) && data[1] is (byte)'1' or (byte)'4',
        "image/x-portable-anymap" => ImageDecoder.IsNetpbm(data),
        "image/x-portable-arbitrarymap" => ImageDecoder.IsNetpbm(data) && data[1] == (byte)'7',
        "image/x-farbfeld" => ImageDecoder.IsFarbfeld(data),
        "image/vnd.radiance" => ImageDecoder.IsHdr(data),
        "image/vnd.wap.wbmp" => data.Length >= 4 && data[0] == 0 && data[1] == 0,
        "image/heic" or "image/heif" => HasIsoBrand(data, "heic", "heix", "hevc", "hevx", "mif1", "msf1", "heim", "heis"),
        "image/avif" => HasIsoBrand(data, "avif", "avis", "mif1", "msf1"),
        "image/jxl" => (data.Length >= 2 && data[0] == 0xff && data[1] == 0x0a) ||
            (data.Length >= 12 && data[..12].SequenceEqual(ImageDecoder.JxlContainerSignature)),
        "image/vnd.ms-photo" => data.Length >= 4 && data[0] == 0x49 && data[1] == 0x49 && data[2] == 0xbc,
        "image/vnd.ms-dds" => data.Length >= 128 && data[..4].SequenceEqual("DDS "u8),
        "image/x-adobe-dng" => IsTiffHeader(data),
        "image/x-raw" => data.Length >= 16,
        _ => false
    };

    private static bool IsTiffHeader(ReadOnlySpan<byte> data) => data.Length >= 8 && (
        (data[0] == 0x49 && data[1] == 0x49 && data[2] == 0x2a && data[3] == 0x00) ||
        (data[0] == 0x4d && data[1] == 0x4d && data[2] == 0x00 && data[3] == 0x2a) ||
        (data[0] == 0x49 && data[1] == 0x49 && data[2] == 0x2b && data[3] == 0x00) ||
        (data[0] == 0x4d && data[1] == 0x4d && data[2] == 0x00 && data[3] == 0x2b));

    private static bool HasIsoBrand(ReadOnlySpan<byte> data, params string[] brands)
    {
        if (data.Length < 16 || !data[4..8].SequenceEqual("ftyp"u8)) return false;
        var boxSize = (int)Math.Min(data.Length, BinaryPrimitives.ReadUInt32BigEndian(data));
        if (boxSize < 12) return false;
        var major = Encoding.ASCII.GetString(data[8..12]);
        if (brands.Contains(major, StringComparer.OrdinalIgnoreCase)) return true;
        for (var pos = 16; pos + 4 <= boxSize; pos += 4)
        {
            var compat = Encoding.ASCII.GetString(data.Slice(pos, 4));
            if (brands.Contains(compat, StringComparer.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

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
