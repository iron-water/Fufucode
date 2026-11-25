using System.Globalization;
using System.Text;

namespace Fufucode.Fui;

internal sealed class FormulaRows
{
    private readonly List<ScreenRow> _rows = [];
    private readonly Dictionary<(string Latex, bool Display), FormulaRaster?> _cache = [];
    internal IReadOnlyList<ScreenRow> Rows => _rows;

    internal static bool MayContainMath(string text) => text.Contains('$') || text.Contains(@"\(", StringComparison.Ordinal) ||
        text.Contains(@"\[", StringComparison.Ordinal) || ((text.Contains("```", StringComparison.Ordinal) || text.Contains("~~~", StringComparison.Ordinal)) &&
        (text.Contains("math", StringComparison.OrdinalIgnoreCase) || text.Contains("latex", StringComparison.OrdinalIgnoreCase)));

    internal void Project(string text, int width, ImageCells cell)
    {
        _rows.Clear();
        var start = 0;
        var fence = default(MarkdownLines.Fence);
        do
        {
            var end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            var line = text[start..end];
            var prior = fence;
            var ink = MarkdownLines.Classify(line, ref fence);
            if (prior.Count == 0 && TryDisplay(text, line, start, end, ink, fence, out var latex, out var last) &&
                Image(latex, true, width, cell) is { } formula)
            {
                AddLine("", [new(0, formula)], Ink.Plain);
                end = last;
                fence = default;
            }
            else if (ink is Ink.Plain or Ink.Heading) Inline(line, ink, width, cell);
            else Plain(line, ink, width);
            start = end + 1;
        } while (start <= text.Length);
    }

    private static bool TryDisplay(string text, string line, int start, int end, Ink ink, MarkdownLines.Fence fence,
        out string latex, out int last)
    {
        latex = ""; last = end;
        if (ink == Ink.Code) return false;
        var body = line.Trim();
        if (ink == Ink.CodeFence)
        {
            if (fence.Count == 0) return false;
            var language = body[fence.Count..].Trim();
            if (!language.Equals("math", StringComparison.OrdinalIgnoreCase) && !language.Equals("latex", StringComparison.OrdinalIgnoreCase)) return false;
            for (var next = end + 1; next < text.Length && next - end <= FormulaRaster.MaxSourceLength + 2;)
            {
                var finish = text.IndexOf('\n', next);
                if (finish < 0) finish = text.Length;
                var closing = text.AsSpan(next, finish - next);
                var after = fence;
                if (MarkdownLines.Classify(closing, ref after) == Ink.CodeFence && after.Count == 0)
                { latex = text[(end + 1)..next].Trim(); last = finish; return true; }
                next = finish + 1;
            }
            return false;
        }
        var close = body.StartsWith("$$", StringComparison.Ordinal) ? "$$" :
            body.StartsWith(@"\[", StringComparison.Ordinal) ? @"\]" : null;
        if (close is null) return false;
        var content = start + line.IndexOf(body, StringComparison.Ordinal) + 2;
        var limit = Math.Min(text.Length - content, FormulaRaster.MaxSourceLength + 2);
        var offset = text.AsSpan(content, limit).IndexOf(close.AsSpan(), StringComparison.Ordinal);
        if (offset < 0) return false;
        var stop = content + offset;
        var tail = text.IndexOf('\n', stop + 2);
        if (tail < 0) tail = text.Length;
        if (!text.AsSpan(stop + 2, tail - stop - 2).Trim().IsEmpty) return false;
        latex = text[content..stop].Trim(); last = tail;
        return true;
    }

    private FormulaRaster? Image(string latex, bool display, int width, ImageCells cell)
    {
        // One instance belongs to one message at one terminal geometry.
        var key = (latex, display);
        if (_cache.TryGetValue(key, out var image)) return image;
        if (_cache.Count >= 48) return null;
        image = FormulaRaster.Create(latex, display, width, cell);
        _cache.Add(key, image);
        return image;
    }

    private void Plain(string text, Ink ink, int width)
    {
        var wrapped = new List<TerminalText.Slice>();
        TerminalText.Wrap(text, width, 0, wrapped);
        foreach (var piece in wrapped) _rows.Add(new(piece.Text, ink));
    }

    private readonly record struct Placed(int Column, FormulaRaster Image);

    private void Inline(string line, Ink ink, int width, ImageCells cell)
    {
        var buffer = new StringBuilder();
        var images = new List<Placed>();
        var column = 0;
        void Flush() { AddLine(buffer.ToString(), images, ink); buffer.Clear(); images.Clear(); column = 0; }
        void Text(ReadOnlySpan<char> part)
        {
            while (!part.IsEmpty)
            {
                var size = StringInfo.GetNextTextElementLength(part);
                var glyph = part[..size];
                var cells = TerminalText.GlyphWidth(glyph);
                if (column > 0 && column + cells > width) Flush();
                if (cells > width) { buffer.Append('…'); column++; }
                else { buffer.Append(glyph); column += cells; }
                part = part[size..];
            }
        }
        var literal = 0;
        var code = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '`')
            {
                var size = 1;
                while (i + size < line.Length && line[i + size] == '`') size++;
                if (code == 0) code = size;
                else if (size == code) code = 0;
                i += size - 1;
                continue;
            }
            if (code != 0 || line[i] is not ('$' or '\\') || Escaped(line, i)) continue;
            var dollar = line[i] == '$';
            if (dollar && ((i > 0 && line[i - 1] == '$') || i + 1 == line.Length || line[i + 1] == '$' || char.IsWhiteSpace(line[i + 1]))) continue;
            if (!dollar && (i + 1 == line.Length || line[i + 1] != '(')) continue;
            var offset = i + (dollar ? 1 : 2);
            var close = dollar ? "$" : @"\)";
            var limit = Math.Min(line.Length - offset, FormulaRaster.MaxSourceLength + close.Length);
            var pos = line.AsSpan(offset, limit).IndexOf(close.AsSpan(), StringComparison.Ordinal);
            if (pos < 1) continue;
            var end = offset + pos;
            if (Escaped(line, end) || (dollar && (char.IsWhiteSpace(line[end - 1]) ||
                end + 1 < line.Length && (char.IsDigit(line[end + 1]) || line[end + 1] == '$')))) continue;
            var image = Image(line[offset..end], false, width, cell);
            if (image is not null)
            {
                Text(line.AsSpan(literal, i - literal));
                if (column > 0 && column + image.Columns > width) Flush();
                images.Add(new(column, image));
                buffer.Append(' ', image.Columns);
                column += image.Columns;
                literal = end + close.Length;
            }
            i = end + close.Length - 1;
        }
        Text(line.AsSpan(literal));
        Flush();
    }

    private static bool Escaped(string text, int index)
    {
        var count = 0;
        while (index > 0 && text[--index] == '\\') count++;
        return (count & 1) != 0;
    }

    private void AddLine(string text, List<Placed> images, Ink ink)
    {
        if (images.Count == 0) { _rows.Add(new(text, ink)); return; }
        var height = images.Max(p => p.Image.Strips.Count);
        for (var row = 0; row < height; row++)
        {
            var spans = new List<ImageStrip>();
            foreach (var item in images)
            {
                var i = row - (height - item.Image.Strips.Count) / 2;
                if ((uint)i < (uint)item.Image.Strips.Count) spans.Add(new(item.Column, item.Image.Strips[i]));
            }
            _rows.Add(new(row == (height - 1) / 2 ? text : "", ink, spans));
        }
    }
}
