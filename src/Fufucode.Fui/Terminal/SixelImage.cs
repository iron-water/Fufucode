using System.Text;
using SkiaSharp;

namespace Fufucode.Fui;

// SIXEL transmits six vertical pixels per printable byte. Define our own small monochrome palette
// on every strip; neither model text nor filenames can become terminal control sequences.
internal static class SixelImage
{
    internal static string Encode(SKColor[] pixels, int width, int top, int height)
    {
        var bytes = new StringBuilder();
        bytes.Append("\x1bP0;1;0q\"1;1;").Append(width).Append(';').Append(height);
        const int tones = 8;
        for (var tone = 0; tone < tones; tone++)
        {
            var grey = 100 * tone / (tones - 1);
            bytes.Append('#').Append(tone).Append(";2;").Append(grey).Append(';').Append(grey).Append(';').Append(grey);
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
                    var luminance = (299 * pixel.Red + 587 * pixel.Green + 114 * pixel.Blue) / 1000;
                    band[((luminance * (tones - 1) + 127) / 255) * width + x] |= (byte)(1 << dy);
                }
            for (var tone = 0; tone < tones; tone++)
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
