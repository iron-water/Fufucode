using CSharpMath.Atom;
using CSharpMath.SkiaSharp;
using SkiaSharp;

namespace Fufucode.Fui;

internal readonly record struct ImageCells(int Width, int Height, int Foreground = 0xFFFFFF, int Background = 0x181818)
{
    internal static readonly ImageCells Default = new(8, 16);
}

// The math font, glyph metrics, fraction rules and scalable delimiters all come from CSharpMath.
// Keep the encoded image in cell-height strips so scrolling can expose part of a tall expression.
internal sealed class FormulaRaster(string[] strips, int columns)
{
    internal int Columns => columns;
    internal IReadOnlyList<string> Strips => strips;

    internal const int MaxSourceLength = 512;
    internal static FormulaRaster? Create(string latex, bool display, int availableColumns, ImageCells cell)
    {
        if (latex.Length is 0 or > MaxSourceLength || availableColumns < 1) return null;
        try
        {
            var painter = new MathPainter
            {
                FontSize = cell.Height * (display ? 1.25f : .92f),
                LineStyle = display ? LineStyle.Display : LineStyle.Text,
                TextColor = new SKColor(0xFF000000u | (uint)cell.Foreground), DisplayErrorInline = false, LaTeX = latex
            };
            if (painter.ErrorMessage is not null) return null;
            var bounds = painter.Measure();
            if (!(bounds.Width > 0 && bounds.Height > 0 && float.IsFinite(bounds.Width) && float.IsFinite(bounds.Height))) return null;
            const int margin = 2;
            var maxWidth = Math.Min(2048, availableColumns * (long)cell.Width);
            var scale = Math.Min(1f, (maxWidth - margin * 2) / bounds.Width);
            if (scale < .65f || bounds.Height * scale > 512) return null;
            var paddingY = display ? margin : 1;
            var width = (int)MathF.Ceiling(bounds.Width * scale) + margin * 2;
            var height = (int)MathF.Ceiling(bounds.Height * scale) + paddingY * 2;
            using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Translate(margin, paddingY);
                canvas.Scale(scale);
                painter.Draw(canvas, -bounds.Left, -bounds.Top);
                canvas.Flush();
            }
            var pixels = bitmap.Pixels;
            var strips = new string[(height + cell.Height - 1) / cell.Height];
            for (var i = 0; i < strips.Length; i++)
                strips[i] = SixelImage.Encode(pixels, width, i * cell.Height, Math.Min(cell.Height, height - i * cell.Height), cell);
            return new(strips, (width + cell.Width - 1) / cell.Width);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException))
        {
            // Invalid TeX, an unavailable native renderer, or an unrenderable font uses the source view.
            return null;
        }
    }
}
