using System.Text;
using SkiaSharp;

namespace Fufucode.Fui;

// SIXEL has a transparent background and opaque palette entries. Leave empty pixels unset and
// approximate partial glyph coverage with shades between the terminal's background and foreground.
internal static class SixelImage
{
    internal static string Encode(SKColor[] pixels, int width, int top, int height, ImageCells cell)
    {
        var bytes = new StringBuilder();
        bytes.Append("\x1bP0;1;0q\"1;1;").Append(width).Append(';').Append(height);
        const int tones = 16;
        int Channel(int shift, int tone)
        {
            var fg = (cell.Foreground >> shift) & 255;
            var bg = (cell.Background >> shift) & 255;
            return ((fg * tone + bg * (tones - 1 - tone)) * 100 + 255 * (tones - 1) / 2) / (255 * (tones - 1));
        }
        for (var tone = 1; tone < tones; tone++)
        {
            bytes.Append('#').Append(tone).Append(";2;").Append(Channel(16, tone)).Append(';')
                .Append(Channel(8, tone)).Append(';').Append(Channel(0, tone));
        }
        var band = new byte[width * tones];
        for (var y = 0; y < height; y += 6)
        {
            if (y != 0) bytes.Append('-');
            Array.Clear(band);
            for (var dy = 0; dy < Math.Min(6, height - y); dy++)
                for (var x = 0; x < width; x++)
                {
                    var pixel = pixels[(top + y + dy) * width + x];
                    var coverage = (pixel.Alpha * (tones - 1) + 127) / 255;
                    if (coverage > 0) band[coverage * width + x] |= (byte)(1 << dy);
                }
            for (var tone = 1; tone < tones; tone++)
            {
                var row = band.AsSpan(tone * width, width);
                var length = row.LastIndexOfAnyExcept((byte)0) + 1;
                if (length == 0) continue;
                bytes.Append('$').Append('#').Append(tone);
                for (var x = 0; x < length;)
                {
                    var count = 1;
                    while (x + count < length && row[x + count] == row[x]) count++;
                    var ch = (char)(63 + row[x]);
                    if (count > 3) bytes.Append('!').Append(count).Append(ch);
                    else bytes.Append(ch, count);
                    x += count;
                }
            }
        }
        return bytes.Append("\x1b\\").ToString();
    }
}
