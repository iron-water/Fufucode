using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace Fufucode.Fui;

// Identify and validate image containers safely before decoding untrusted pixel dimensions.
internal static class ProjectImage
{
    internal const int PreviewWidth = 96, PreviewHeight = 64;
    private const int MaxSourceDimension = 32768;
    private const long MaxSourcePixels = 64_000_000;

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

    internal static ProjectImagePreview CreatePreview(ReadOnlySpan<byte> data, string mimeType)
    {
        if (!Matches(data, mimeType)) throw new InvalidDataException("图片内容与声明的格式不符。");
        try
        {
            using var stream = new SKMemoryStream(data.ToArray());
            using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("图片编码无法解码。");
            var source = codec.Info;
            if (source.Width <= 0 || source.Height <= 0 || source.Width > MaxSourceDimension ||
                source.Height > MaxSourceDimension || (long)source.Width * source.Height > MaxSourcePixels)
                throw new InvalidDataException("图片尺寸过大，无法安全生成预览。");

            var desiredScale = MathF.Min(PreviewWidth / (float)source.Width, PreviewHeight / (float)source.Height);
            var scaled = codec.GetScaledDimensions(desiredScale);
            var width = Math.Clamp(scaled.Width, 1, source.Width);
            var height = Math.Clamp(scaled.Height, 1, source.Height);
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            // Prefer the codec's supported downscale. Small inputs and codecs that cannot scale
            // fall back to a full decode, still bounded by the source-pixel guard above.
            using var decoded = SKBitmap.Decode(codec, info) ??
                ((long)source.Width * source.Height <= 16_000_000 ? SKBitmap.Decode(data.ToArray()) : null) ??
                throw new InvalidDataException("图片像素无法完整解码。");
            return CreateThumbnail(decoded, source.Width, source.Height);
        }
        catch (Exception error) when (error is not (InvalidDataException or OutOfMemoryException or OperationCanceledException))
        {
            throw new InvalidDataException("图片无法解码，未加入附件。", error);
        }
    }

    internal static ProjectImagePreview CreatePreview(DecodedImage image)
    {
        if (!ImageDecoder.IsSafeDimensions(image.Width, image.Height) ||
            image.Rgba.Length < checked(image.Width * image.Height * 4))
            throw new InvalidDataException("图片尺寸或像素数据无效，无法安全生成预览。");
        using var bitmap = ImageDecoder.CreateBitmapFromRgba(image.Rgba, image.Width, image.Height)
            ?? throw new InvalidDataException("图片像素无法生成预览。");
        return CreateThumbnail(bitmap, image.Width, image.Height);
    }

    private static ProjectImagePreview CreateThumbnail(SKBitmap decoded, int sourceWidth, int sourceHeight)
    {
        using var thumbnail = new SKBitmap(new SKImageInfo(PreviewWidth, PreviewHeight,
            SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(thumbnail))
        {
            canvas.Clear(SKColors.Transparent);
            var fit = MathF.Min(PreviewWidth / (float)decoded.Width, PreviewHeight / (float)decoded.Height);
            var w = Math.Min(PreviewWidth, Math.Max(1, (int)MathF.Round(decoded.Width * fit)));
            var h = Math.Min(PreviewHeight, Math.Max(1, (int)MathF.Round(decoded.Height * fit)));
            var destination = new SKRect((PreviewWidth - w) / 2f, (PreviewHeight - h) / 2f,
                (PreviewWidth + w) / 2f, (PreviewHeight + h) / 2f);
            using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            canvas.DrawBitmap(decoded, destination, paint);
            canvas.Flush();
        }
        return new(PreviewWidth, PreviewHeight, thumbnail.Pixels, sourceWidth, sourceHeight);
    }

    // Clipboard images are converted to the same PNG path used by project attachments. Only PNG,
    // CF_DIBV5 and CF_DIB are accepted; the system clipboard is opened read-only and never cleared.
    internal static byte[]? ReadWindowsClipboardPng(int maxBytes)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("剪贴板图片当前只支持 Windows。");
        return WindowsClipboard.ReadPng(maxBytes);
    }

    internal static byte[]? DecodeClipboardFormats(byte[]? png, byte[]? dibV5, byte[]? dib, int maxBytes) =>
        WindowsClipboard.DecodeClipboardFormats(png, dibV5, dib, maxBytes);

    internal static byte[] DibToPng(ReadOnlySpan<byte> dib, int maxBytes)
    {
        if (dib.Length > maxBytes) throw new IOException($"剪贴板图片超过 {maxBytes / 1024 / 1024} MiB。");
        if (dib.Length < 40) throw new InvalidDataException("剪贴板 DIB 数据不完整。");
        var header = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        if (header is not (40 or 52 or 56 or 108 or 124))
            throw new InvalidDataException("剪贴板 DIB 头格式不受支持。");

        var width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        var height = Math.Abs((long)signedHeight);
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(dib[12..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        if (width <= 0 || height is 0 or > MaxSourceDimension || width > MaxSourceDimension ||
            planes != 1 || bits is not (24 or 32) || compression is not (0 or 3) ||
            (long)width * height > MaxSourcePixels)
            throw new InvalidDataException("剪贴板 DIB 的尺寸、位深或压缩格式不受支持。");

        var maskBytes = compression == 3 && header == 40 ? 12 : 0;
        if (maskBytes != 0 && bits == 32 && dib.Length >= 40 + 16 &&
            BinaryPrimitives.ReadUInt32LittleEndian(dib[(40 + 12)..]) != 0) maskBytes = 16;
        var dibPixelOffset = checked((int)header + maskBytes);
        var pixelOffset = checked(14 + dibPixelOffset);
        var stride = checked(((width * bits + 31) / 32) * 4);
        var pixels = checked((long)stride * height);
        if (dibPixelOffset > dib.Length || pixels > dib.Length - dibPixelOffset)
            throw new InvalidDataException("剪贴板 DIB 像素数据不完整。");

        var file = new byte[checked(14 + dib.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(file, 0x4d42);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), pixelOffset);
        dib.CopyTo(file.AsSpan(14));

        using var bitmap = SKBitmap.Decode(file) ?? throw new InvalidDataException("剪贴板 DIB 像素无法解码。");
        // Some Windows apps publish opaque BGRA as CF_DIB with an all-zero alpha channel.
        var colors = bitmap.Pixels;
        if (bits == 32 && !colors.Any(color => color.Alpha != 0))
        {
            for (var i = 0; i < colors.Length; i++)
                colors[i] = new SKColor(colors[i].Red, colors[i].Green, colors[i].Blue, 255);
            bitmap.Pixels = colors;
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100) ??
            throw new InvalidDataException("剪贴板 DIB 无法编码为 PNG。");
        var png = encoded.ToArray();
        if (png.Length > maxBytes) throw new IOException($"剪贴板图片超过 {maxBytes / 1024 / 1024} MiB。");
        return png;
    }

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

    private static class WindowsClipboard
    {
        private const uint CfDib = 8, CfDibV5 = 17;

        internal static byte[]? ReadPng(int maxBytes)
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                if (!OpenClipboard(0))
                {
                    if (attempt < 5) { Thread.Sleep(20); continue; }
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开 Windows 剪贴板；它可能正被其他程序占用。");
                }
                try
                {
                    var png = RegisterClipboardFormatW("PNG");
                    var encoded = png != 0 && TryRead(png, maxBytes, out var clipboardPng) ? clipboardPng : null;
                    if (encoded is not null && Matches(encoded, "image/png")) return encoded;
                    var dibV5 = TryRead(CfDibV5, maxBytes, out var vector) ? vector : null;
                    var dib = TryRead(CfDib, maxBytes, out var bitmap) ? bitmap : null;
                    return DecodeClipboardFormats(encoded, dibV5, dib, maxBytes);
                }
                finally { CloseClipboard(); }
            }
            return null;
        }

        internal static byte[]? DecodeClipboardFormats(byte[]? png, byte[]? dibV5, byte[]? dib, int maxBytes)
        {
            foreach (var bytes in new[] { png, dibV5, dib })
                if (bytes is { Length: > 0 } && bytes.Length > maxBytes)
                    throw new IOException($"剪贴板图片超过 {maxBytes / 1024 / 1024} MiB。");
            if (png is { Length: > 0 })
            {
                if (Matches(png, "image/png")) return png;
                if (dibV5 is not { Length: > 0 } && dib is not { Length: > 0 })
                    throw new InvalidDataException("剪贴板 PNG 数据无效。");
            }
            if (dibV5 is { Length: > 0 }) return DibToPng(dibV5, maxBytes);
            if (dib is { Length: > 0 }) return DibToPng(dib, maxBytes);
            return null;
        }

        private static bool TryRead(uint format, int maxBytes, out byte[] bytes)
        {
            bytes = [];
            var handle = GetClipboardData(format);
            if (handle == 0) return false;
            var size = GlobalSize(handle);
            if (size == 0) throw new InvalidDataException("剪贴板图片数据为空。");
            if ((ulong)size > (uint)maxBytes) throw new IOException($"剪贴板图片超过 {maxBytes / 1024 / 1024} MiB。");
            var pointer = GlobalLock(handle);
            if (pointer == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 Windows 剪贴板图片。");
            try
            {
                bytes = new byte[checked((int)size)];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                return true;
            }
            finally { GlobalUnlock(handle); }
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint owner);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern nint GetClipboardData(uint format);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterClipboardFormatW(string format);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern nuint GlobalSize(nint memory);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint memory);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(nint memory);
    }
}
