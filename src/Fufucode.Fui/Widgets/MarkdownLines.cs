namespace Fufucode.Fui;

// Style metadata only. The transcript, including fence delimiters, original indentation and arguments,
// remains selectable/searchable as text. No links, embedded escape codes or executable markup.
internal sealed class MarkdownLines
{
    private readonly record struct Fence(char Marker, int Count);
    private readonly record struct LogicalLine(int Start, Ink Ink, Fence Before);
    private readonly List<LogicalLine> _lines = [];
    private readonly List<Ink> _rows = [];

    internal Ink this[int index] => _rows[index];

    internal void Project(string clean, IReadOnlyList<TerminalText.Slice> rows, int kept, bool rebuild)
    {
        var logicalStart = 0;
        var state = default(Fence);
        if (!rebuild && _lines.Count > 0)
        {
            var tail = _lines[^1];
            logicalStart = tail.Start;
            state = tail.Before;
            _lines.RemoveAt(_lines.Count - 1);
        }
        else { _lines.Clear(); _rows.Clear(); }

        // A delta can close a partial opener or turn the previous final line into a heading.
        // Inspect each newly completed logical line once; even a huge code line only needs its prefix to classify.
        var pos = logicalStart;
        while (true)
        {
            var newline = clean.IndexOf('\n', pos);
            var line = clean.AsSpan(pos, (newline < 0 ? clean.Length : newline) - pos);
            var before = state;
            var ink = Classify(line, ref state);
            _lines.Add(new(pos, ink, before));
            if (newline < 0) break;
            pos = newline + 1;
        }

        var first = Math.Min(kept, _rows.Count);
        // Group styles can change for all wrapped rows of the formerly partial final logical line.
        // Two-row reflow still handles character widths; refresh just the stale group metadata if needed.
        if (first > 0)
        {
            var group = LineAt(logicalStart);
            if (_rows[first - 1] != _lines[group].Ink && rows[first - 1].Start >= logicalStart)
                while (first > 0 && rows[first - 1].Start >= logicalStart) first--;
        }
        if (_rows.Count > first) _rows.RemoveRange(first, _rows.Count - first);
        if (first == rows.Count) return;
        var groupIndex = LineAt(rows[first].Start);
        for (var i = first; i < rows.Count; i++)
        {
            while (groupIndex + 1 < _lines.Count && rows[i].Start >= _lines[groupIndex + 1].Start) groupIndex++;
            _rows.Add(_lines[groupIndex].Ink);
        }
    }

    private int LineAt(int start)
    {
        var lo = 0;
        var hi = _lines.Count;
        while (lo + 1 < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_lines[mid].Start <= start) lo = mid; else hi = mid;
        }
        return lo;
    }

    private static Ink Classify(ReadOnlySpan<char> line, ref Fence fence)
    {
        var indent = 0;
        while (indent < line.Length && indent < 4 && line[indent] == ' ') indent++;
        var body = line[indent..];
        if (indent <= 3 && !body.IsEmpty && body[0] is '`' or '~')
        {
            var ch = body[0];
            var size = 1;
            while (size < body.Length && body[size] == ch) size++;
            if (fence.Count == 0 && size >= 3 && (ch != '`' || !body[size..].Contains('`')))
            { fence = new(ch, size); return Ink.CodeFence; }
            if (fence.Marker == ch && size >= fence.Count && body[size..].Trim().IsEmpty)
            { fence = default; return Ink.CodeFence; }
        }
        if (fence.Count > 0 || indent >= 4) return Ink.Code;
        if (body.Length > 0 && body[0] == '#')
        {
            var depth = 1;
            while (depth < body.Length && depth <= 6 && body[depth] == '#') depth++;
            if (depth <= 6 && (depth == body.Length || body[depth] == ' ')) return Ink.Heading;
        }
        return Ink.Plain;
    }
}
