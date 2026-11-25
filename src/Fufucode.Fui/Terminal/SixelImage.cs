using System.Text;
using SkiaSharp;

namespace Fufucode.Fui;

// SIXEL has a transparent background and opaque palette entries. Leave empty pixels unset and
// approximate partial glyph coverage with shades between the terminal's background and foreground.
internal static class SixelImage
{
    private const int MaxPaletteColors = 240;
    private const byte AlphaCutoff = 16;

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

    // Full-color SIXEL with transparent background (P2=1). Semi-transparent edges blend against the
    // terminal background to avoid halos on both dark and light themes. One shared palette covers all
    // cell-height strips so adjacent rows never show color seams.
    internal static string[] EncodeColorStrips(ReadOnlySpan<byte> rgba, int width, int height, ImageCells cell)
    {
        if (width <= 0 || height <= 0 || cell.Height <= 0 || rgba.Length < width * height * 4) return [];
        var pixelCount = width * height;
        var bgR = (cell.Background >> 16) & 255;
        var bgG = (cell.Background >> 8) & 255;
        var bgB = cell.Background & 255;

        var opaque = new bool[pixelCount];
        var rgb = new int[pixelCount];
        var exact = new Dictionary<int, byte>(MaxPaletteColors + 1);
        var exactOverflow = false;
        var hasOpaque = false;

        for (var i = 0; i < pixelCount; i++)
        {
            var p = i * 4;
            var a = rgba[p + 3];
            if (a < AlphaCutoff) continue;
            hasOpaque = true;
            opaque[i] = true;
            int r = rgba[p], g = rgba[p + 1], b = rgba[p + 2];
            if (a < 255)
            {
                var inv = 255 - a;
                r = (r * a + bgR * inv + 127) / 255;
                g = (g * a + bgG * inv + 127) / 255;
                b = (b * a + bgB * inv + 127) / 255;
            }
            var packed = (r << 16) | (g << 8) | b;
            rgb[i] = packed;
            if (!exactOverflow && !exact.ContainsKey(packed))
            {
                if (exact.Count < MaxPaletteColors) exact[packed] = (byte)(exact.Count + 1);
                else exactOverflow = true;
            }
        }

        var stripCount = (height + cell.Height - 1) / cell.Height;
        var strips = new string[stripCount];
        if (!hasOpaque)
        {
            Array.Fill(strips, "");
            return strips;
        }

        int[] palette;
        var indices = new byte[pixelCount];
        if (!exactOverflow)
        {
            palette = new int[exact.Count + 1];
            foreach (var (color, id) in exact) palette[id] = color;
            for (var i = 0; i < pixelCount; i++)
                if (opaque[i]) indices[i] = exact[rgb[i]];
        }
        else
        {
            palette = BuildMedianCutPalette(rgb, opaque);
            MapWithDither(rgb, opaque, width, height, palette, indices);
        }

        for (var s = 0; s < stripCount; s++)
        {
            var top = s * cell.Height;
            var stripHeight = Math.Min(cell.Height, height - top);
            strips[s] = EncodeColorStrip(indices, width, top, stripHeight, palette);
        }
        return strips;
    }

    private static int[] BuildMedianCutPalette(int[] rgb, bool[] opaque)
    {
        const int bins = 32 * 32 * 32;
        var counts = new int[bins];
        var sumR = new long[bins];
        var sumG = new long[bins];
        var sumB = new long[bins];
        for (var i = 0; i < rgb.Length; i++)
        {
            if (!opaque[i]) continue;
            var c = rgb[i];
            var r = (c >> 16) & 255;
            var g = (c >> 8) & 255;
            var b = c & 255;
            var key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
            counts[key]++;
            sumR[key] += r;
            sumG[key] += g;
            sumB[key] += b;
        }

        var entries = new List<ColorBucket>(1024);
        for (var key = 0; key < bins; key++)
        {
            var count = counts[key];
            if (count == 0) continue;
            entries.Add(new(
                (byte)(sumR[key] / count),
                (byte)(sumG[key] / count),
                (byte)(sumB[key] / count),
                count));
        }
        var bucketArray = entries.ToArray();
        var boxes = new List<ColorBox>(MaxPaletteColors) { CreateBox(bucketArray, 0, bucketArray.Length) };

        while (boxes.Count < MaxPaletteColors)
        {
            var best = -1;
            long bestScore = 0;
            for (var i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box.Length < 2 || box.Spread == 0) continue;
                var score = (long)box.Spread * box.Population;
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (best < 0) break;
            var target = boxes[best];
            var channel = target.WidestChannel;
            Array.Sort(bucketArray, target.Start, target.Length, ColorBucketComparer.For(channel));
            var half = target.Population / 2;
            long acc = 0;
            var split = target.Start + 1;
            for (var i = target.Start; i < target.Start + target.Length - 1; i++)
            {
                acc += bucketArray[i].Count;
                split = i + 1;
                if (acc >= half) break;
            }
            boxes[best] = CreateBox(bucketArray, target.Start, split - target.Start);
            boxes.Add(CreateBox(bucketArray, split, target.Start + target.Length - split));
        }

        var palette = new int[boxes.Count + 1];
        for (var i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            long r = 0, g = 0, b = 0, total = 0;
            for (var j = box.Start; j < box.Start + box.Length; j++)
            {
                var e = bucketArray[j];
                r += (long)e.R * e.Count;
                g += (long)e.G * e.Count;
                b += (long)e.B * e.Count;
                total += e.Count;
            }
            var mr = (int)(r / total);
            var mg = (int)(g / total);
            var mb = (int)(b / total);
            palette[i + 1] = (mr << 16) | (mg << 8) | mb;
        }
        return palette;
    }

    private static void MapWithDither(int[] rgb, bool[] opaque, int width, int height, int[] palette, byte[] indices)
    {
        var paletteCount = palette.Length - 1;
        var pr = new int[paletteCount + 1];
        var pg = new int[paletteCount + 1];
        var pb = new int[paletteCount + 1];
        for (var i = 1; i <= paletteCount; i++)
        {
            pr[i] = (palette[i] >> 16) & 255;
            pg[i] = (palette[i] >> 8) & 255;
            pb[i] = palette[i] & 255;
        }

        var lut = new byte[32768];
        var curR = new int[width + 2];
        var curG = new int[width + 2];
        var curB = new int[width + 2];
        var nextR = new int[width + 2];
        var nextG = new int[width + 2];
        var nextB = new int[width + 2];

        for (var y = 0; y < height; y++)
        {
            Array.Clear(nextR); Array.Clear(nextG); Array.Clear(nextB);
            var rowOffset = y * width;
            for (var x = 0; x < width; x++)
            {
                var i = rowOffset + x;
                if (!opaque[i]) continue;
                var c = rgb[i];
                var r = Math.Clamp(((c >> 16) & 255) + (curR[x + 1] >> 4), 0, 255);
                var g = Math.Clamp(((c >> 8) & 255) + (curG[x + 1] >> 4), 0, 255);
                var b = Math.Clamp((c & 255) + (curB[x + 1] >> 4), 0, 255);

                var key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                var best = lut[key];
                if (best == 0)
                {
                    var bestDist = int.MaxValue;
                    var qr = ((r >> 3) << 3) | 4;
                    var qg = ((g >> 3) << 3) | 4;
                    var qb = ((b >> 3) << 3) | 4;
                    for (var p = 1; p <= paletteCount; p++)
                    {
                        var dr = qr - pr[p];
                        var dg = qg - pg[p];
                        var db = qb - pb[p];
                        var dist = dr * dr * 2 + dg * dg * 4 + db * db;
                        if (dist < bestDist) { bestDist = dist; best = (byte)p; }
                    }
                    lut[key] = best;
                }
                indices[i] = best;

                var er = r - pr[best];
                var eg = g - pg[best];
                var eb = b - pb[best];
                if (x + 1 < width && opaque[i + 1])
                {
                    curR[x + 2] += er * 7; curG[x + 2] += eg * 7; curB[x + 2] += eb * 7;
                }
                if (y + 1 < height)
                {
                    var down = i + width;
                    if (x > 0 && opaque[down - 1])
                    {
                        nextR[x] += er * 3; nextG[x] += eg * 3; nextB[x] += eb * 3;
                    }
                    if (opaque[down])
                    {
                        nextR[x + 1] += er * 5; nextG[x + 1] += eg * 5; nextB[x + 1] += eb * 5;
                    }
                    if (x + 1 < width && opaque[down + 1])
                    {
                        nextR[x + 2] += er; nextG[x + 2] += eg; nextB[x + 2] += eb;
                    }
                }
            }
            (curR, nextR) = (nextR, curR);
            (curG, nextG) = (nextG, curG);
            (curB, nextB) = (nextB, curB);
        }
    }

    private static string EncodeColorStrip(byte[] indices, int width, int top, int height, int[] palette)
    {
        Span<byte> remap = stackalloc byte[palette.Length];
        Span<int> localColors = stackalloc int[palette.Length];
        var localCount = 0;
        for (var y = 0; y < height; y++)
        {
            var row = (top + y) * width;
            for (var x = 0; x < width; x++)
            {
                var id = indices[row + x];
                if (id == 0 || remap[id] != 0) continue;
                localCount++;
                remap[id] = (byte)localCount;
                localColors[localCount] = palette[id];
            }
        }
        if (localCount == 0) return "";

        var sb = new StringBuilder(width * Math.Max(1, height / 2) + localCount * 18 + 32);
        sb.Append("\x1bP0;1;0q\"1;1;").Append(width).Append(';').Append(height);
        for (var id = 1; id <= localCount; id++)
        {
            var c = localColors[id];
            var r = (((c >> 16) & 255) * 100 + 127) / 255;
            var g = (((c >> 8) & 255) * 100 + 127) / 255;
            var b = ((c & 255) * 100 + 127) / 255;
            sb.Append('#').Append(id).Append(";2;").Append(r).Append(';').Append(g).Append(';').Append(b);
        }

        var band = new byte[(localCount + 1) * width];
        var touched = new bool[localCount + 1];
        var touchedIds = new int[localCount];
        for (var y = 0; y < height; y += 6)
        {
            if (y != 0) sb.Append('-');
            var touchedCount = 0;
            var bandRows = Math.Min(6, height - y);
            for (var dy = 0; dy < bandRows; dy++)
            {
                var row = (top + y + dy) * width;
                var bit = (byte)(1 << dy);
                for (var x = 0; x < width; x++)
                {
                    var globalId = indices[row + x];
                    if (globalId == 0) continue;
                    var id = remap[globalId];
                    if (!touched[id])
                    {
                        touched[id] = true;
                        touchedIds[touchedCount++] = id;
                    }
                    band[id * width + x] |= bit;
                }
            }
            var firstInBand = true;
            for (var t = 0; t < touchedCount; t++)
            {
                var id = touchedIds[t];
                touched[id] = false;
                var slice = band.AsSpan(id * width, width);
                var length = slice.LastIndexOfAnyExcept((byte)0) + 1;
                if (length == 0) continue;
                if (!firstInBand) sb.Append('$');
                firstInBand = false;
                sb.Append('#').Append(id);
                for (var x = 0; x < length;)
                {
                    var v = slice[x];
                    var count = 1;
                    while (x + count < length && slice[x + count] == v) count++;
                    var ch = (char)(63 + v);
                    if (count > 3) sb.Append('!').Append(count).Append(ch);
                    else sb.Append(ch, count);
                    x += count;
                }
                slice[..length].Clear();
            }
        }
        return sb.Append("\x1b\\").ToString();
    }

    private readonly record struct ColorBucket(byte R, byte G, byte B, int Count);
    private readonly record struct ColorBox(int Start, int Length, long Population, int Spread, int WidestChannel);

    private static ColorBox CreateBox(ColorBucket[] buckets, int start, int length)
    {
        byte minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
        long pop = 0;
        for (var i = start; i < start + length; i++)
        {
            var b = buckets[i];
            if (b.R < minR) minR = b.R; if (b.R > maxR) maxR = b.R;
            if (b.G < minG) minG = b.G; if (b.G > maxG) maxG = b.G;
            if (b.B < minB) minB = b.B; if (b.B > maxB) maxB = b.B;
            pop += b.Count;
        }
        int sr = maxR - minR, sg = maxG - minG, sb = maxB - minB;
        var widest = sg >= sr && sg >= sb ? 1 : sr >= sb ? 0 : 2;
        var spread = Math.Max(sr, Math.Max(sg, sb));
        return new(start, length, pop, spread, widest);
    }

    private sealed class ColorBucketComparer(int channel) : IComparer<ColorBucket>
    {
        private static readonly ColorBucketComparer R = new(0), G = new(1), B = new(2);
        public static ColorBucketComparer For(int channel) => channel switch { 0 => R, 1 => G, _ => B };
        public int Compare(ColorBucket x, ColorBucket y) => channel switch
        {
            0 => x.R.CompareTo(y.R),
            1 => x.G.CompareTo(y.G),
            _ => x.B.CompareTo(y.B)
        };
    }
}
