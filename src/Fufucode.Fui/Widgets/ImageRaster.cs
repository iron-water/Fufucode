using SkiaSharp;

namespace Fufucode.Fui;

// Decodes any supported raster/vector format, downscales in premultiplied space to avoid
// dark alpha fringes, and encodes into cell-height transparent full-color SIXEL strips.
internal sealed class ImageRaster(string[] strips, int columns, int sourceWidth, int sourceHeight, string formatLabel)
{
    internal const int MaxDisplayWidth = 960;
    internal const int MaxDisplayHeight = 420;

    internal int Columns => columns;
    internal int SourceWidth => sourceWidth;
    internal int SourceHeight => sourceHeight;
    internal string FormatLabel => formatLabel;
    internal IReadOnlyList<string> Strips => strips;

    internal static ImageRaster? Create(ReadOnlySpan<byte> bytes, string? mimeHint, int availableColumns,
        ImageCells cell, int maxRows = 16, DecodedImage? predecoded = null)
    {
        if (bytes.IsEmpty || availableColumns < 1 || maxRows < 1 || cell.Width < 2 || cell.Height < 4) return null;
        var maxW = (int)Math.Min(MaxDisplayWidth, availableColumns * (long)cell.Width);
        var maxH = (int)Math.Min(MaxDisplayHeight, maxRows * (long)cell.Height);
        if (maxW < 1 || maxH < 1) return null;

        var decoded = predecoded ?? ImageDecoder.TryDecode(bytes, mimeHint, maxW, maxH);
        if (decoded is null) return null;

        var rgba = decoded.Rgba;
        var w = decoded.Width;
        var h = decoded.Height;
        if (w > maxW || h > maxH)
        {
            var scale = Math.Min((double)maxW / w, (double)maxH / h);
            var nw = Math.Max(1, (int)Math.Round(w * scale));
            var nh = Math.Max(1, (int)Math.Round(h * scale));
            var resized = ResizePremultiplied(rgba, w, h, nw, nh);
            if (resized is null) return null;
            rgba = resized;
            w = nw;
            h = nh;
        }

        var strips = SixelImage.EncodeColorStrips(rgba, w, h, cell);
        if (strips.Length == 0) return null;
        return new(strips, (w + cell.Width - 1) / cell.Width, decoded.Width, decoded.Height, decoded.FormatLabel);
    }

    private static byte[]? ResizePremultiplied(byte[] srcRgba, int srcW, int srcH, int dstW, int dstH)
    {
        try
        {
            var byteCount = checked(srcW * srcH * 4);
            var premulInfo = new SKImageInfo(srcW, srcH, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var premulSrc = new SKBitmap(premulInfo);
            var dstPtr = premulSrc.GetPixels();
            if (dstPtr == nint.Zero || premulSrc.ByteCount < byteCount) return null;
            unsafe
            {
                var dstSpan = new Span<byte>((void*)dstPtr, byteCount);
                for (var i = 0; i < byteCount; i += 4)
                {
                    var a = srcRgba[i + 3];
                    if (a == 255)
                    {
                        dstSpan[i] = srcRgba[i];
                        dstSpan[i + 1] = srcRgba[i + 1];
                        dstSpan[i + 2] = srcRgba[i + 2];
                        dstSpan[i + 3] = 255;
                    }
                    else if (a == 0)
                    {
                        dstSpan[i] = dstSpan[i + 1] = dstSpan[i + 2] = dstSpan[i + 3] = 0;
                    }
                    else
                    {
                        dstSpan[i] = (byte)((srcRgba[i] * a + 127) / 255);
                        dstSpan[i + 1] = (byte)((srcRgba[i + 1] * a + 127) / 255);
                        dstSpan[i + 2] = (byte)((srcRgba[i + 2] * a + 127) / 255);
                        dstSpan[i + 3] = a;
                    }
                }
            }

            var resizedInfo = new SKImageInfo(dstW, dstH, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var resized = premulSrc.Resize(resizedInfo, SKFilterQuality.High);
            if (resized is null) return null;

            var outRgba = new byte[checked(dstW * dstH * 4)];
            var unpremulInfo = new SKImageInfo(dstW, dstH, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            unsafe
            {
                fixed (byte* ptr = outRgba)
                {
                    if (!resized.PeekPixels().ReadPixels(unpremulInfo, (nint)ptr, dstW * 4, 0, 0)) return null;
                }
            }
            return outRgba;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
