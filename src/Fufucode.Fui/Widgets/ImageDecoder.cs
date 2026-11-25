using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SkiaSharp;
using Svg.Skia;

namespace Fufucode.Fui;

internal sealed record DecodedImage(byte[] Rgba, int Width, int Height, string FormatLabel);

// Safe multi-format decoder: validates dimensions before full allocation, audits SVG XML against
// scripts/DTD/external references, and cascades across SVG, pure-C# decoders (QOI, TGA, Netpbm,
// Farbfeld, CUR), SkiaSharp (+ EXIF orientation) and Windows WIC (TIFF, JXL, HEIC/HEIF, AVIF, JXR, DDS, RAW).
internal static class ImageDecoder
{
    internal const int MaxDimension = 16384;
    internal const long MaxPixels = 64_000_000;
    internal static ReadOnlySpan<byte> JxlContainerSignature => [0, 0, 0, 12, 0x4a, 0x58, 0x4c, 0x20, 0x0d, 0x0a, 0x87, 0x0a];

    internal static DecodedImage? TryDecode(ReadOnlySpan<byte> bytes, string? mimeHint = null,
        int targetMaxWidth = 0, int targetMaxHeight = 0)
    {
        if (bytes.IsEmpty || bytes.Length > ProjectFiles.MaxAttachmentBytes) return null;
        var hint = mimeHint?.ToLowerInvariant();

        if (hint == "image/svg+xml" || LooksLikeSvg(bytes) || (hint == "image/svg+xml+gz") || IsSvgz(bytes, hint))
            return TryDecodeSvg(bytes, targetMaxWidth, targetMaxHeight);

        if (IsQoi(bytes) && TryDecodeQoi(bytes, out var qoi)) return qoi;
        if (IsFarbfeld(bytes) && TryDecodeFarbfeld(bytes, out var ff)) return ff;
        if (IsNetpbm(bytes) && TryDecodeNetpbm(bytes, out var pnm)) return pnm;
        if (IsHdr(bytes) && TryDecodeHdr(bytes, out var hdr)) return hdr;
        if (IsCur(bytes) && TryDecodeCur(bytes, out var cur)) return cur;
        if (hint == "image/bmp" && LooksLikeDib(bytes) && TryDecodeDib(bytes, out var dib)) return dib;

        if (hint == "image/x-tga" && TryDecodeTga(bytes, out var tgaByHint)) return tgaByHint;

        if (TryDecodeSkia(bytes, hint, out var skia)) return skia;

        if (WicImageDecoder.TryDecode(bytes, MaxDimension, MaxPixels, out var wicRgba, out var w, out var h))
            return new(wicRgba, w, h, LabelFor(hint, bytes));

        if (hint != "image/x-tga" && LooksLikeTga(bytes) && TryDecodeTga(bytes, out var tga)) return tga;
        return null;
    }

    internal static byte[]? EncodePng(DecodedImage image, int maxEdge = 4096)
    {
        var (rgba, w, h) = (image.Rgba, image.Width, image.Height);
        if (w <= 0 || h <= 0 || rgba.Length < w * h * 4) return null;
        using var src = CreateBitmapFromRgba(rgba, w, h);
        if (src is null) return null;

        SKBitmap? scaled = null;
        try
        {
            var target = src;
            if (maxEdge > 0 && (w > maxEdge || h > maxEdge))
            {
                var scale = Math.Min((double)maxEdge / w, (double)maxEdge / h);
                var nw = Math.Max(1, (int)Math.Round(w * scale));
                var nh = Math.Max(1, (int)Math.Round(h * scale));
                scaled = src.Resize(new SKImageInfo(nw, nh, SKColorType.Rgba8888, SKAlphaType.Unpremul), SKFilterQuality.High);
                if (scaled is not null) target = scaled;
            }
            using var skImage = SKImage.FromBitmap(target);
            using var data = skImage.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray();
        }
        finally { scaled?.Dispose(); }
    }

    internal static SKBitmap? CreateBitmapFromRgba(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        var byteCount = checked(width * height * 4);
        if (rgba.Length < byteCount) return null;
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        var ptr = bitmap.GetPixels();
        if (ptr == nint.Zero || bitmap.ByteCount < byteCount) { bitmap.Dispose(); return null; }
        unsafe
        {
            rgba[..byteCount].CopyTo(new Span<byte>((void*)ptr, byteCount));
        }
        return bitmap;
    }

    internal static bool IsSafeDimensions(int width, int height) =>
        width is > 0 and <= MaxDimension && height is > 0 and <= MaxDimension && (long)width * height <= MaxPixels;

    private static bool TryDecodeSkia(ReadOnlySpan<byte> bytes, string? mimeHint, out DecodedImage? result)
    {
        result = null;
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is null) return false;
            var srcInfo = codec.Info;
            if (!IsSafeDimensions(srcInfo.Width, srcInfo.Height)) return false;
            var dstInfo = new SKImageInfo(srcInfo.Width, srcInfo.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var rgba = new byte[checked(dstInfo.Width * dstInfo.Height * 4)];
            unsafe
            {
                fixed (byte* ptr = rgba)
                {
                    var status = codec.GetPixels(dstInfo, (nint)ptr);
                    if (status is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return false;
                }
            }
            var (w, h) = (dstInfo.Width, dstInfo.Height);
            if (codec.EncodedOrigin != SKEncodedOrigin.TopLeft)
                rgba = ApplyOrigin(rgba, w, h, codec.EncodedOrigin, out w, out h);
            result = new(rgba, w, h, LabelForSkia(codec.EncodedFormat, mimeHint, bytes));
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static byte[] ApplyOrigin(byte[] src, int w, int h, SKEncodedOrigin origin, out int outW, out int outH)
    {
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        outW = swap ? h : w;
        outH = swap ? w : h;
        var dst = new byte[src.Length];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (nx, ny) = origin switch
                {
                    SKEncodedOrigin.TopRight => (w - 1 - x, y),
                    SKEncodedOrigin.BottomRight => (w - 1 - x, h - 1 - y),
                    SKEncodedOrigin.BottomLeft => (x, h - 1 - y),
                    SKEncodedOrigin.LeftTop => (y, x),
                    SKEncodedOrigin.RightTop => (h - 1 - y, x),
                    SKEncodedOrigin.RightBottom => (h - 1 - y, w - 1 - x),
                    SKEncodedOrigin.LeftBottom => (y, w - 1 - x),
                    _ => (x, y)
                };
                var si = (y * w + x) * 4;
                var di = (ny * outW + nx) * 4;
                dst[di] = src[si]; dst[di + 1] = src[si + 1]; dst[di + 2] = src[si + 2]; dst[di + 3] = src[si + 3];
            }
        return dst;
    }

    private static DecodedImage? TryDecodeSvg(ReadOnlySpan<byte> raw, int targetMaxWidth, int targetMaxHeight)
    {
        try
        {
            var xmlBytes = UnpackAndAuditSvg(raw);
            if (xmlBytes is null) return null;

            using var svg = new SKSvg();
            using var stream = new MemoryStream(xmlBytes, writable: false);
            var picture = svg.Load(stream);
            if (picture is null) return null;
            var rect = picture.CullRect;
            if (!(rect.Width > 0 && rect.Height > 0 && float.IsFinite(rect.Width) && float.IsFinite(rect.Height))) return null;

            double maxW = targetMaxWidth > 0 ? Math.Min(2048, targetMaxWidth) : 2048;
            double maxH = targetMaxHeight > 0 ? Math.Min(2048, targetMaxHeight) : 2048;
            var fitScale = Math.Min(maxW / rect.Width, maxH / rect.Height);
            // Keep crisp vector sharpness: downscale large SVGs, gently upscale tiny icons (<= 4x).
            var scale = targetMaxWidth > 0
                ? Math.Min(fitScale, Math.Max(1.0, Math.Min(4.0, 200.0 / Math.Max(rect.Width, rect.Height))))
                : Math.Min(1.0, fitScale);
            if (scale <= 0) return null;

            var width = Math.Max(1, (int)Math.Ceiling(rect.Width * scale));
            var height = Math.Max(1, (int)Math.Ceiling(rect.Height * scale));
            if (!IsSafeDimensions(width, height)) return null;

            using var premul = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(premul))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Scale((float)scale);
                canvas.Translate(-rect.Left, -rect.Top);
                canvas.DrawPicture(picture);
                canvas.Flush();
            }
            var rgba = new byte[checked(width * height * 4)];
            var unpremulInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            unsafe
            {
                fixed (byte* ptr = rgba)
                {
                    if (!premul.PeekPixels().ReadPixels(unpremulInfo, (nint)ptr, width * 4, 0, 0)) return null;
                }
            }
            return new(rgba, width, height, "SVG");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    internal static byte[]? UnpackAndAuditSvg(ReadOnlySpan<byte> raw)
    {
        try
        {
            byte[] xmlBytes;
            if (raw.Length >= 2 && raw[0] == 0x1f && raw[1] == 0x8b)
            {
                using var compressed = new MemoryStream(raw.ToArray(), writable: false);
                using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[16384];
                int read;
                while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > ProjectFiles.MaxAttachmentBytes) return null;
                    output.Write(buffer, 0, read);
                }
                xmlBytes = output.ToArray();
            }
            else xmlBytes = raw.ToArray();
            return AuditSvgXml(xmlBytes) ? xmlBytes : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }

    internal static bool AuditSvgXml(byte[] bytes)
    {
        try
        {
            var rawText = Encoding.UTF8.GetString(bytes);
            // Allow standard <!DOCTYPE svg ...> headers from Inkscape/Graphviz/Illustrator while
            // unconditionally blocking inline entity declarations (XXE / billion-laughs).
            if (rawText.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase)) return false;

            using var reader = XmlReader.Create(new MemoryStream(bytes, writable: false), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersInDocument = 4_000_000,
                IgnoreProcessingInstructions = true
            });
            var doc = XDocument.Load(reader);
            if (doc.Root is null || !doc.Root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var element in doc.Descendants())
            {
                var tag = element.Name.LocalName;
                if (tag.Equals("script", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("foreignObject", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("iframe", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("audio", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("video", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("canvas", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("embed", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("object", StringComparison.OrdinalIgnoreCase))
                    return false;
                foreach (var attr in element.Attributes())
                {
                    if (attr.IsNamespaceDeclaration) continue;
                    var local = attr.Name.LocalName;
                    var val = attr.Value;
                    if (local.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                        val.Contains("javascript:", StringComparison.OrdinalIgnoreCase) ||
                        val.Contains("vbscript:", StringComparison.OrdinalIgnoreCase))
                        return false;
                    if ((local.Equals("href", StringComparison.OrdinalIgnoreCase) ||
                         local.Equals("src", StringComparison.OrdinalIgnoreCase)) &&
                        !val.TrimStart().StartsWith('#'))
                        return false;
                    if (ContainsExternalUrl(val)) return false;
                }
                if (tag.Equals("style", StringComparison.OrdinalIgnoreCase) &&
                    (ContainsExternalUrl(element.Value) || element.Value.Contains("@import", StringComparison.OrdinalIgnoreCase)))
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is XmlException or IOException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static bool ContainsExternalUrl(string value)
    {
        var index = 0;
        while ((index = value.IndexOf("url(", index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var start = index + 4;
            while (start < value.Length && char.IsWhiteSpace(value[start])) start++;
            var end = value.IndexOf(')', start);
            if (end < 0) return true;
            var target = value[start..end].Trim('\'', '"', ' ', '\t', '\r', '\n');
            if (!target.StartsWith('#')) return true;
            index = end + 1;
        }
        return false;
    }

    private static bool IsSvgz(ReadOnlySpan<byte> bytes, string? hint) =>
        bytes.Length >= 10 && bytes[0] == 0x1f && bytes[1] == 0x8b && hint == "image/svg+xml";

    internal static bool LooksLikeSvg(ReadOnlySpan<byte> bytes)
    {
        var span = bytes;
        if (span.Length >= 3 && span[0] == 0xef && span[1] == 0xbb && span[2] == 0xbf) span = span[3..];
        var i = 0;
        while (i < span.Length && i < 256 && span[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        if (i >= span.Length || span[i] != (byte)'<') return false;
        var head = Encoding.UTF8.GetString(span[i..Math.Min(span.Length, i + 4096)]);
        return head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsQoi(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 22 && bytes[..4].SequenceEqual("qoif"u8);

    private static bool TryDecodeQoi(ReadOnlySpan<byte> data, out DecodedImage? result)
    {
        result = null;
        if (!IsQoi(data)) return false;
        var w = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
        var h = BinaryPrimitives.ReadInt32BigEndian(data[8..]);
        var channels = data[12];
        var colorspace = data[13];
        if (!IsSafeDimensions(w, h) || channels is not (3 or 4) || colorspace is not (0 or 1)) return false;

        var pixelCount = w * h;
        var rgba = new byte[checked(pixelCount * 4)];
        Span<uint> index = stackalloc uint[64];
        byte r = 0, g = 0, b = 0, a = 255;
        var pos = 14;
        var limit = data.Length - 8;
        var run = 0;

        for (var i = 0; i < pixelCount; i++)
        {
            if (run > 0) run--;
            else if (pos < limit)
            {
                var b1 = data[pos++];
                if (b1 == 0xfe)
                {
                    if (pos + 3 > limit) return false;
                    r = data[pos++]; g = data[pos++]; b = data[pos++];
                }
                else if (b1 == 0xff)
                {
                    if (pos + 4 > limit) return false;
                    r = data[pos++]; g = data[pos++]; b = data[pos++]; a = data[pos++];
                }
                else if ((b1 & 0xc0) == 0x00)
                {
                    var c = index[b1 & 0x3f];
                    r = (byte)(c >> 24); g = (byte)(c >> 16); b = (byte)(c >> 8); a = (byte)c;
                }
                else if ((b1 & 0xc0) == 0x40)
                {
                    r = (byte)(r + ((b1 >> 4) & 3) - 2);
                    g = (byte)(g + ((b1 >> 2) & 3) - 2);
                    b = (byte)(b + (b1 & 3) - 2);
                }
                else if ((b1 & 0xc0) == 0x80)
                {
                    if (pos >= limit) return false;
                    var b2 = data[pos++];
                    var vg = (b1 & 0x3f) - 32;
                    r = (byte)(r + vg - 8 + ((b2 >> 4) & 0x0f));
                    g = (byte)(g + vg);
                    b = (byte)(b + vg - 8 + (b2 & 0x0f));
                }
                else
                {
                    run = b1 & 0x3f;
                }
                var hash = (r * 3 + g * 5 + b * 7 + a * 11) & 63;
                index[hash] = ((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | a;
            }
            else return false;

            var p = i * 4;
            rgba[p] = r; rgba[p + 1] = g; rgba[p + 2] = b; rgba[p + 3] = a;
        }
        result = new(rgba, w, h, "QOI");
        return true;
    }

    internal static bool IsFarbfeld(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 16 && bytes[..8].SequenceEqual("farbfeld"u8);

    private static bool TryDecodeFarbfeld(ReadOnlySpan<byte> data, out DecodedImage? result)
    {
        result = null;
        if (!IsFarbfeld(data)) return false;
        var w = BinaryPrimitives.ReadInt32BigEndian(data[8..]);
        var h = BinaryPrimitives.ReadInt32BigEndian(data[12..]);
        if (!IsSafeDimensions(w, h)) return false;
        var expected = 16L + (long)w * h * 8;
        if (data.Length < expected) return false;
        var rgba = new byte[checked(w * h * 4)];
        var src = 16;
        for (var i = 0; i < rgba.Length; i += 4, src += 8)
        {
            rgba[i] = data[src];
            rgba[i + 1] = data[src + 2];
            rgba[i + 2] = data[src + 4];
            rgba[i + 3] = data[src + 6];
        }
        result = new(rgba, w, h, "FARBFELD");
        return true;
    }

    internal static bool IsCur(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 22 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 2 && bytes[3] == 0 &&
        BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) is >= 1 and <= 256;

    private static bool TryDecodeCur(ReadOnlySpan<byte> data, out DecodedImage? result)
    {
        result = null;
        if (!IsCur(data)) return false;
        var ico = data.ToArray();
        ico[2] = 1; // Re-tag CUR container as ICO in memory.
        if (TryDecodeSkia(ico, "image/x-icon", out var skia) && skia is not null)
        {
            result = skia with { FormatLabel = "CUR" };
            return true;
        }
        if (WicImageDecoder.TryDecode(ico, MaxDimension, MaxPixels, out var rgba, out var w, out var h))
        {
            result = new(rgba, w, h, "CUR");
            return true;
        }
        return false;
    }

    internal static bool IsNetpbm(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 7 && bytes[0] == (byte)'P' && bytes[1] is >= (byte)'1' and <= (byte)'7' &&
        bytes[2] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'#';

    private static bool TryDecodeNetpbm(ReadOnlySpan<byte> data, out DecodedImage? result)
    {
        result = null;
        if (!IsNetpbm(data)) return false;
        var magic = (char)data[1];
        if (magic == '7') return TryDecodePam(data, out result);

        var pos = 2;
        if (!ReadPnmInt(data, ref pos, out var w) || !ReadPnmInt(data, ref pos, out var h) || !IsSafeDimensions(w, h))
            return false;
        var maxVal = 1;
        if (magic is '2' or '3' or '5' or '6')
        {
            if (!ReadPnmInt(data, ref pos, out maxVal) || maxVal is < 1 or > 65535) return false;
        }
        if (magic is '4' or '5' or '6')
        {
            if (pos >= data.Length) return false;
            if (data[pos] == (byte)'\r' && pos + 1 < data.Length && data[pos + 1] == (byte)'\n') pos += 2;
            else pos++; // single whitespace after header
        }

        var rgba = new byte[checked(w * h * 4)];
        var label = magic switch { '1' or '4' => "PBM", '2' or '5' => "PGM", _ => "PPM" };
        var twoByte = maxVal > 255;

        switch (magic)
        {
            case '1':
                for (var i = 0; i < w * h; i++)
                {
                    if (!ReadPbmBit(data, ref pos, out var bit)) return false;
                    var v = bit == 0 ? (byte)255 : (byte)0;
                    var p = i * 4; rgba[p] = v; rgba[p + 1] = v; rgba[p + 2] = v; rgba[p + 3] = 255;
                }
                break;
            case '4':
                var rowBytes = (w + 7) / 8;
                if (pos + (long)rowBytes * h > data.Length) return false;
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                    {
                        var bit = (data[pos + y * rowBytes + (x >> 3)] >> (7 - (x & 7))) & 1;
                        var v = bit == 0 ? (byte)255 : (byte)0;
                        var p = (y * w + x) * 4; rgba[p] = v; rgba[p + 1] = v; rgba[p + 2] = v; rgba[p + 3] = 255;
                    }
                break;
            case '2':
                for (var i = 0; i < w * h; i++)
                {
                    if (!ReadPnmInt(data, ref pos, out var raw)) return false;
                    var v = ScaleSample(raw, maxVal);
                    var p = i * 4; rgba[p] = v; rgba[p + 1] = v; rgba[p + 2] = v; rgba[p + 3] = 255;
                }
                break;
            case '5':
                var gStep = twoByte ? 2 : 1;
                if (pos + (long)w * h * gStep > data.Length) return false;
                for (var i = 0; i < w * h; i++, pos += gStep)
                {
                    var raw = twoByte ? BinaryPrimitives.ReadUInt16BigEndian(data[pos..]) : data[pos];
                    var v = ScaleSample(raw, maxVal);
                    var p = i * 4; rgba[p] = v; rgba[p + 1] = v; rgba[p + 2] = v; rgba[p + 3] = 255;
                }
                break;
            case '3':
                for (var i = 0; i < w * h; i++)
                {
                    if (!ReadPnmInt(data, ref pos, out var r) || !ReadPnmInt(data, ref pos, out var g) ||
                        !ReadPnmInt(data, ref pos, out var b)) return false;
                    var p = i * 4;
                    rgba[p] = ScaleSample(r, maxVal); rgba[p + 1] = ScaleSample(g, maxVal);
                    rgba[p + 2] = ScaleSample(b, maxVal); rgba[p + 3] = 255;
                }
                break;
            case '6':
                var rgbStep = twoByte ? 6 : 3;
                if (pos + (long)w * h * rgbStep > data.Length) return false;
                for (var i = 0; i < w * h; i++, pos += rgbStep)
                {
                    var p = i * 4;
                    if (twoByte)
                    {
                        rgba[p] = ScaleSample(BinaryPrimitives.ReadUInt16BigEndian(data[pos..]), maxVal);
                        rgba[p + 1] = ScaleSample(BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]), maxVal);
                        rgba[p + 2] = ScaleSample(BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 4)..]), maxVal);
                    }
                    else
                    {
                        rgba[p] = ScaleSample(data[pos], maxVal);
                        rgba[p + 1] = ScaleSample(data[pos + 1], maxVal);
                        rgba[p + 2] = ScaleSample(data[pos + 2], maxVal);
                    }
                    rgba[p + 3] = 255;
                }
                break;
            default: return false;
        }
        result = new(rgba, w, h, label);
        return true;
    }

    private static bool TryDecodePam(ReadOnlySpan<byte> data, out DecodedImage? result)
    {
        result = null;
        int w = 0, h = 0, depth = 0, maxVal = 0;
        var pos = 2;
        var endHeader = false;
        while (pos < data.Length)
        {
            var nl = data[pos..].IndexOf((byte)'\n');
            if (nl < 0) return false;
            var line = Encoding.ASCII.GetString(data.Slice(pos, nl)).Trim();
            pos += nl + 1;
            if (line.Length == 0 || line[0] == '#') continue;
            if (line == "ENDHDR") { endHeader = true; break; }
            var sep = line.IndexOfAny([' ', '\t']);
            if (sep <= 0) continue;
            var key = line[..sep];
            var val = line[(sep + 1)..].Trim();
            if (key == "WIDTH" && int.TryParse(val, CultureInfo.InvariantCulture, out var pw)) w = pw;
            else if (key == "HEIGHT" && int.TryParse(val, CultureInfo.InvariantCulture, out var ph)) h = ph;
            else if (key == "DEPTH" && int.TryParse(val, CultureInfo.InvariantCulture, out var pd)) depth = pd;
            else if (key == "MAXVAL" && int.TryParse(val, CultureInfo.InvariantCulture, out var pm)) maxVal = pm;
        }
        if (!endHeader || !IsSafeDimensions(w, h) || depth is < 1 or > 4 || maxVal is < 1 or > 65535) return false;
        var twoByte = maxVal > 255;
        var sampleBytes = twoByte ? 2 : 1;
        if (pos + (long)w * h * depth * sampleBytes > data.Length) return false;
        var rgba = new byte[checked(w * h * 4)];
        for (var i = 0; i < w * h; i++)
        {
            var p = i * 4;
            switch (depth)
            {
                case 1:
                    var g = ReadPamSample(data, ref pos, twoByte, sampleBytes, maxVal);
                    rgba[p] = g; rgba[p + 1] = g; rgba[p + 2] = g; rgba[p + 3] = 255;
                    break;
                case 2:
                    var ga = ReadPamSample(data, ref pos, twoByte, sampleBytes, maxVal);
                    rgba[p] = ga; rgba[p + 1] = ga; rgba[p + 2] = ga;
                    rgba[p + 3] = ReadPamSample(data, ref pos, twoByte, sampleBytes, maxVal);
                    break;
                case 3:
                    rgba[p] = ReadPamSample(data, ref pos, twoByte, sampleBytes, maxVal);
                    rgba[p + 1] = ReadPamSample(data, ref pos, twoByte, sampleBytes, maxVal);
                    rgba[p + 2] = ReadPamSample(data, ref pos, twoByte, sampleBytes, maxVal);
                    rgba[p + 3] = 255;
                    break;
                case 4:
                    rgba[p] = ReadPamSample(data, ref pos, twoByte, sampleBytes, maxVal);
                    rgba[p + 1] = ReadPamSample(data, ref pos, twoByte, sampleBytes, maxVal);
                    rgba[p + 2] = ReadPamSample(data, ref pos, twoByte, sampleBytes, maxVal);
                    rgba[p + 3] = ReadPamSample(data, ref pos, twoByte, sampleBytes, maxVal);
                    break;
            }
        }
        result = new(rgba, w, h, "PAM");
        return true;
    }

    private static byte ReadPamSample(ReadOnlySpan<byte> data, ref int pos, bool twoByte, int sampleBytes, int maxVal)
    {
        var raw = twoByte ? BinaryPrimitives.ReadUInt16BigEndian(data[pos..]) : data[pos];
        pos += sampleBytes;
        return ScaleSample(raw, maxVal);
    }

    private static byte ScaleSample(int value, int maxVal) =>
        (byte)Math.Clamp((value * 255 + maxVal / 2) / maxVal, 0, 255);

    private static void SkipPnmSpace(ReadOnlySpan<byte> data, ref int pos)
    {
        while (pos < data.Length)
        {
            var b = data[pos];
            if (b == (byte)'#')
            {
                while (pos < data.Length && data[pos] != (byte)'\n') pos++;
            }
            else if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') pos++;
            else break;
        }
    }

    private static bool ReadPnmInt(ReadOnlySpan<byte> data, ref int pos, out int value)
    {
        SkipPnmSpace(data, ref pos);
        value = 0;
        var start = pos;
        while (pos < data.Length && data[pos] is >= (byte)'0' and <= (byte)'9')
        {
            value = value * 10 + (data[pos++] - (byte)'0');
            if (value > 1_000_000) return false;
        }
        return pos > start;
    }

    private static bool ReadPbmBit(ReadOnlySpan<byte> data, ref int pos, out int bit)
    {
        SkipPnmSpace(data, ref pos);
        if (pos < data.Length && data[pos] is (byte)'0' or (byte)'1') { bit = data[pos++] - (byte)'0'; return true; }
        bit = 0;
        return false;
    }

    internal static bool LooksLikeTga(ReadOnlySpan<byte> data)
    {
        if (data.Length < 18) return false;
        var colorMapType = data[1];
        var imageType = data[2];
        if (colorMapType is not (0 or 1)) return false;
        if (imageType is not (1 or 2 or 3 or 9 or 10 or 11)) return false;
        var w = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
        var h = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        var bpp = data[16];
        return IsSafeDimensions(w, h) && bpp is 8 or 15 or 16 or 24 or 32;
    }

    private static bool TryDecodeTga(ReadOnlySpan<byte> data, out DecodedImage? result)
    {
        result = null;
        if (!LooksLikeTga(data)) return false;
        var idLength = data[0];
        var colorMapType = data[1];
        var imageType = data[2];
        var cmapFirst = BinaryPrimitives.ReadUInt16LittleEndian(data[3..]);
        var cmapLen = BinaryPrimitives.ReadUInt16LittleEndian(data[5..]);
        var cmapBits = data[7];
        var w = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
        var h = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        var bpp = data[16];
        var desc = data[17];
        var bytesPerPixel = (bpp + 7) / 8;
        var cmapEntryBytes = (cmapBits + 7) / 8;
        var pos = 18 + idLength;
        if (pos > data.Length) return false;

        byte[]? cmap = null;
        if (colorMapType == 1)
        {
            if (cmapEntryBytes is < 2 or > 4 || pos + cmapLen * cmapEntryBytes > data.Length) return false;
            cmap = new byte[cmapLen * 4];
            for (var i = 0; i < cmapLen; i++)
            {
                UnpackTgaColor(data.Slice(pos + i * cmapEntryBytes, cmapEntryBytes), cmapBits,
                    out cmap[i * 4], out cmap[i * 4 + 1], out cmap[i * 4 + 2], out cmap[i * 4 + 3]);
            }
            pos += cmapLen * cmapEntryBytes;
        }
        var mapped = imageType is 1 or 9;
        var grayscale = imageType is 3 or 11;
        var rle = imageType is 9 or 10 or 11;
        if (mapped && (cmap is null || bpp != 8)) return false;
        if (grayscale && bpp is not (8 or 16)) return false;
        if (!mapped && !grayscale && bpp is not (15 or 16 or 24 or 32)) return false;

        var pixelCount = w * h;
        var rgba = new byte[checked(pixelCount * 4)];
        var topOrigin = (desc & 0x20) != 0;
        var rightOrigin = (desc & 0x10) != 0;
        var anyNonZeroAlpha = false;

        void PutPixel(int linear, byte r, byte g, byte b, byte a)
        {
            var sx = linear % w;
            var sy = linear / w;
            var dx = rightOrigin ? w - 1 - sx : sx;
            var dy = topOrigin ? sy : h - 1 - sy;
            var p = (dy * w + dx) * 4;
            rgba[p] = r; rgba[p + 1] = g; rgba[p + 2] = b; rgba[p + 3] = a;
        }

        var written = 0;
        while (written < pixelCount)
        {
            if (!rle)
            {
                if (!ReadTgaPixel(data, ref pos, bytesPerPixel, mapped, grayscale, bpp, cmap, cmapFirst, cmapLen,
                    ref anyNonZeroAlpha, out var r, out var g, out var b, out var a)) return false;
                PutPixel(written++, r, g, b, a);
            }
            else
            {
                if (pos >= data.Length) return false;
                var header = data[pos++];
                var count = (header & 0x7f) + 1;
                if (written + count > pixelCount) return false;
                if ((header & 0x80) != 0)
                {
                    if (!ReadTgaPixel(data, ref pos, bytesPerPixel, mapped, grayscale, bpp, cmap, cmapFirst, cmapLen,
                        ref anyNonZeroAlpha, out var r, out var g, out var b, out var a)) return false;
                    for (var k = 0; k < count; k++) PutPixel(written++, r, g, b, a);
                }
                else
                {
                    for (var k = 0; k < count; k++)
                    {
                        if (!ReadTgaPixel(data, ref pos, bytesPerPixel, mapped, grayscale, bpp, cmap, cmapFirst, cmapLen,
                            ref anyNonZeroAlpha, out var r, out var g, out var b, out var a)) return false;
                        PutPixel(written++, r, g, b, a);
                    }
                }
            }
        }
        if ((bpp == 32 || bpp == 16) && !anyNonZeroAlpha && (desc & 0x0f) == 0)
            for (var i = 3; i < rgba.Length; i += 4) rgba[i] = 255;

        result = new(rgba, w, h, "TGA");
        return true;
    }

    private static bool ReadTgaPixel(ReadOnlySpan<byte> data, ref int offset, int bytesPerPixel,
        bool mapped, bool grayscale, int bpp, byte[]? cmap, int cmapFirst, int cmapLen, ref bool anyNonZeroAlpha,
        out byte r, out byte g, out byte b, out byte a)
    {
        r = g = b = 0; a = 255;
        if (offset + bytesPerPixel > data.Length) return false;
        var slice = data.Slice(offset, bytesPerPixel);
        offset += bytesPerPixel;
        if (mapped)
        {
            var idx = slice[0] - cmapFirst;
            if ((uint)idx >= (uint)cmapLen) return false;
            r = cmap![idx * 4]; g = cmap[idx * 4 + 1]; b = cmap[idx * 4 + 2]; a = cmap[idx * 4 + 3];
        }
        else if (grayscale)
        {
            r = g = b = slice[0];
            a = bytesPerPixel == 2 ? slice[1] : (byte)255;
        }
        else UnpackTgaColor(slice, bpp, out r, out g, out b, out a);
        if (a != 0) anyNonZeroAlpha = true;
        return true;
    }

    internal static bool LooksLikeDib(ReadOnlySpan<byte> data)
    {
        if (data.Length < 40) return false;
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (headerSize is not (40 or 52 or 56 or 108 or 124) || headerSize > (uint)data.Length) return false;
        var w = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        var h = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[8..]));
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
        var bpp = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        return planes == 1 && bpp is 1 or 4 or 8 or 16 or 24 or 32 && IsSafeDimensions(w, h);
    }

    internal static byte[]? WrapDibAsBmp(ReadOnlySpan<byte> dib)
    {
        if (!LooksLikeDib(dib)) return null;
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        var bpp = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
        uint paletteEntries = colorsUsed != 0 ? colorsUsed : bpp <= 8 ? 1u << bpp : 0u;
        uint maskBytes = headerSize == 40 && compression == 3 ? 12u : 0u;
        var pixelOffset = 14u + headerSize + maskBytes + paletteEntries * 4u;
        if (pixelOffset > 14u + (uint)dib.Length) return null;
        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), pixelOffset);
        dib.CopyTo(bmp.AsSpan(14));
        return bmp;
    }

    private static bool TryDecodeDib(ReadOnlySpan<byte> dib, out DecodedImage? result)
    {
        result = null;
        var bmp = WrapDibAsBmp(dib);
        if (bmp is null) return false;
        if (TryDecodeSkia(bmp, "image/bmp", out var skia) && skia is not null)
        {
            result = skia with { FormatLabel = "DIB" };
            return true;
        }
        if (WicImageDecoder.TryDecode(bmp, MaxDimension, MaxPixels, out var rgba, out var w, out var h))
        {
            result = new(rgba, w, h, "DIB");
            return true;
        }
        return false;
    }

    internal static bool IsHdr(ReadOnlySpan<byte> data) =>
        (data.Length >= 11 && data[..10].SequenceEqual("#?RADIANCE"u8)) ||
        (data.Length >= 7 && data[..6].SequenceEqual("#?RGBE"u8));

    private static bool TryDecodeHdr(ReadOnlySpan<byte> data, out DecodedImage? result)
    {
        result = null;
        if (!IsHdr(data)) return false;
        var pos = 0;
        var foundBlank = false;
        while (pos < data.Length)
        {
            var nl = data[pos..].IndexOf((byte)'\n');
            if (nl < 0) return false;
            var line = Encoding.ASCII.GetString(data.Slice(pos, nl)).TrimEnd('\r');
            pos += nl + 1;
            if (line.Length == 0) { foundBlank = true; break; }
        }
        if (!foundBlank || pos >= data.Length) return false;
        var resNl = data[pos..].IndexOf((byte)'\n');
        if (resNl < 0) return false;
        var resLine = Encoding.ASCII.GetString(data.Slice(pos, resNl)).Trim();
        pos += resNl + 1;
        var tokens = resLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 4 || tokens[0] != "-Y" || tokens[2] != "+X" ||
            !int.TryParse(tokens[1], CultureInfo.InvariantCulture, out var h) ||
            !int.TryParse(tokens[3], CultureInfo.InvariantCulture, out var w) ||
            !IsSafeDimensions(w, h))
            return false;

        var rgba = new byte[checked(w * h * 4)];
        var scan = new byte[w * 4];
        for (var y = 0; y < h; y++)
        {
            if (pos + 4 > data.Length) return false;
            var isNewRle = w is >= 8 and <= 0x7fff &&
                data[pos] == 2 && data[pos + 1] == 2 && (data[pos + 2] & 0x80) == 0 &&
                ((data[pos + 2] << 8) | data[pos + 3]) == w;
            if (isNewRle)
            {
                pos += 4;
                for (var ch = 0; ch < 4; ch++)
                {
                    var x = 0;
                    while (x < w)
                    {
                        if (pos >= data.Length) return false;
                        var count = data[pos++];
                        if (count > 128)
                        {
                            var run = count - 128;
                            if (run == 0 || x + run > w || pos >= data.Length) return false;
                            var v = data[pos++];
                            for (var k = 0; k < run; k++) scan[(x + k) * 4 + ch] = v;
                            x += run;
                        }
                        else
                        {
                            if (count == 0 || x + count > w || pos + count > data.Length) return false;
                            for (var k = 0; k < count; k++) scan[(x + k) * 4 + ch] = data[pos++];
                            x += count;
                        }
                    }
                }
            }
            else
            {
                var needed = w * 4;
                if (pos + needed > data.Length) return false;
                data.Slice(pos, needed).CopyTo(scan);
                pos += needed;
            }
            var rowOffset = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                var si = x * 4;
                var e = scan[si + 3];
                var di = rowOffset + si;
                if (e == 0) { rgba[di] = rgba[di + 1] = rgba[di + 2] = 0; rgba[di + 3] = 255; continue; }
                var scale = MathF.ScaleB(1f, e - 136);
                rgba[di] = ToneMapHdr(scan[si] * scale);
                rgba[di + 1] = ToneMapHdr(scan[si + 1] * scale);
                rgba[di + 2] = ToneMapHdr(scan[si + 2] * scale);
                rgba[di + 3] = 255;
            }
        }
        result = new(rgba, w, h, "HDR");
        return true;
    }

    private static byte ToneMapHdr(float linear)
    {
        var mapped = linear / (1f + linear);
        var srgb = MathF.Pow(Math.Clamp(mapped, 0f, 1f), 1f / 2.2f);
        return (byte)Math.Clamp((int)MathF.Round(srgb * 255f), 0, 255);
    }

    private static void UnpackTgaColor(ReadOnlySpan<byte> slice, int bits, out byte r, out byte g, out byte b, out byte a)
    {
        if (bits is 15 or 16)
        {
            var v = BinaryPrimitives.ReadUInt16LittleEndian(slice);
            r = (byte)(((v >> 10) & 31) * 255 / 31);
            g = (byte)(((v >> 5) & 31) * 255 / 31);
            b = (byte)((v & 31) * 255 / 31);
            a = bits == 16 && (v & 0x8000) == 0 ? (byte)0 : (byte)255;
            return;
        }
        b = slice[0]; g = slice[1]; r = slice[2];
        a = slice.Length == 4 ? slice[3] : (byte)255;
    }

    private static string LabelForSkia(SKEncodedImageFormat format, string? mimeHint, ReadOnlySpan<byte> bytes) => format switch
    {
        SKEncodedImageFormat.Png => "PNG",
        SKEncodedImageFormat.Jpeg => "JPEG",
        SKEncodedImageFormat.Webp => "WEBP",
        SKEncodedImageFormat.Gif => "GIF",
        SKEncodedImageFormat.Bmp => "BMP",
        SKEncodedImageFormat.Ico => "ICO",
        SKEncodedImageFormat.Wbmp => "WBMP",
        SKEncodedImageFormat.Dng => "DNG",
        SKEncodedImageFormat.Heif => "HEIF",
        SKEncodedImageFormat.Avif => "AVIF",
        _ => LabelFor(mimeHint, bytes)
    };

    private static string LabelFor(string? mimeHint, ReadOnlySpan<byte> bytes) => mimeHint switch
    {
        "image/png" => "PNG",
        "image/jpeg" => "JPEG",
        "image/webp" => "WEBP",
        "image/gif" => "GIF",
        "image/bmp" => "BMP",
        "image/tiff" => "TIFF",
        "image/x-icon" => "ICO",
        "image/x-win-bitmap" => "CUR",
        "image/svg+xml" => "SVG",
        "image/heic" => "HEIC",
        "image/heif" => "HEIF",
        "image/avif" => "AVIF",
        "image/jxl" => "JXL",
        "image/vnd.ms-photo" => "JXR",
        "image/vnd.ms-dds" => "DDS",
        "image/qoi" => "QOI",
        "image/x-tga" => "TGA",
        "image/x-portable-pixmap" => "PPM",
        "image/x-portable-graymap" => "PGM",
        "image/x-portable-bitmap" => "PBM",
        "image/x-portable-anymap" => "PNM",
        "image/x-portable-arbitrarymap" => "PAM",
        "image/x-farbfeld" => "FARBFELD",
        "image/vnd.radiance" => "HDR",
        "image/x-adobe-dng" => "DNG",
        "image/x-raw" => "RAW",
        _ => SniffLabel(bytes)
    };

    private static string SniffLabel(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71) return "PNG";
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8) return "JPEG";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return "WEBP";
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8))) return "GIF";
        if (bytes.Length >= 4 && ((bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2a && bytes[3] == 0x00) ||
            (bytes[0] == 0x4d && bytes[1] == 0x4d && bytes[2] == 0x00 && bytes[3] == 0x2a))) return "TIFF";
        if (bytes.Length >= 4 && bytes[..4].SequenceEqual("DDS "u8)) return "DDS";
        if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0x0a) return "JXL";
        if (bytes.Length >= 12 && bytes[..12].SequenceEqual(JxlContainerSignature)) return "JXL";
        if (bytes.Length >= 12 && bytes[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(bytes[8..12]);
            if (brand is "avif" or "avis") return "AVIF";
            if (brand is "heic" or "heix" or "hevc" or "hevx") return "HEIC";
            return "HEIF";
        }
        if (bytes.Length >= 4 && bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0xbc) return "JXR";
        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M') return "BMP";
        return "IMAGE";
    }
}
