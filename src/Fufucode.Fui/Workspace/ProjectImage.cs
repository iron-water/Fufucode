using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Fufucode.Fui;

// Identify the encoded container without allocating or decoding a raster of untrusted dimensions.
internal static class ProjectImage
{
    internal const int PreviewWidth = 96, PreviewHeight = 64;
    private const int MaxSourceDimension = 32768;
    private const long MaxSourcePixels = 64_000_000;

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
            return new(PreviewWidth, PreviewHeight, thumbnail.Pixels, source.Width, source.Height);
        }
        catch (Exception error) when (error is not (InvalidDataException or OutOfMemoryException or OperationCanceledException))
        {
            throw new InvalidDataException("图片无法解码，未加入附件。", error);
        }
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
