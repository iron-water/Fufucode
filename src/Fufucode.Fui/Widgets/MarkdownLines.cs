namespace Fufucode.Fui;

// Terminal Markdown is a bounded presentation layer: it formats tables, link hints and simple block
// prefixes while keeping the original transcript text untouched. It emits no OSC 8 or executable markup.
internal sealed class MarkdownLines
{
    internal readonly record struct Fence(char Marker, int Count, string Language = "");
    internal readonly record struct SourceLine(string Text, bool Complete);
    internal readonly record struct Row(string Text, Ink Ink, StyledSpanList? Spans = null);
    private readonly List<Row> _rows = [];
    private string _source = "";
    private int _width = -1;

    internal int Count => _rows.Count;
    internal Row this[int index] => _rows[index];

    internal void Project(string source, int width)
    {
        source = TerminalText.Clean(source);
        width = Math.Max(1, width);
        if (source == _source && width == _width) return;
        _source = source;
        _width = width;
        _rows.Clear();

        var lines = SplitLines(source);
        var fence = default(Fence);
        for (var i = 0; i < lines.Count;)
        {
            var line = lines[i].Text;
            var before = fence;
            var ink = Classify(line, ref fence);
            if (before.Count == 0 && ink == Ink.CodeFence &&
                SyntaxHighlighter.NormalizeLanguage(fence.Language) == "mermaid" &&
                TryClosingFence(lines, i + 1, fence, out var close))
            {
                RenderMermaid(lines, i, close, fence, width);
                fence = default;
                i = close + 1;
                continue;
            }
            if (before.Count == 0 && ink == Ink.Plain && MarkdownTable.TryFormat(lines, i, width, _rows, out var next))
            {
                fence = before;
                i = next;
                continue;
            }
            RenderLine(line, ink, before, width);
            i++;
        }
    }

    private static List<SourceLine> SplitLines(string source)
    {
        var lines = new List<SourceLine>();
        for (var start = 0;;)
        {
            var end = source.IndexOf('\n', start);
            if (end < 0)
            {
                lines.Add(new(source[start..], false));
                return lines;
            }
            lines.Add(new(source[start..end], true));
            start = end + 1;
            if (start == source.Length)
            {
                lines.Add(new("", false));
                return lines;
            }
        }
    }

    private void RenderLine(string line, Ink ink, Fence fence, int width)
    {
        if (ink == Ink.Code)
        {
            if (fence.Count != 0 && fence.Language.Length != 0)
                AddStyledWrapped(SyntaxHighlighter.Highlight(line, fence.Language), Ink.Code, width);
            else AddWrapped(line, Ink.Code, width);
            return;
        }
        if (ink == Ink.CodeFence)
        {
            AddWrapped(line, Ink.CodeFence, width);
            return;
        }
        if (IsRule(line))
        {
            _rows.Add(new(new string('─', width), Ink.Quiet));
            return;
        }
        if (TryQuote(line, out var quote)) AddInline(MarkdownTable.FormatInline(quote), Ink.Quiet, width);
        else if (TryList(line, out var list)) AddInline(MarkdownTable.FormatInline(list), Ink.Plain, width);
        else AddInline(MarkdownTable.FormatInline(line), ink, width);
    }

    private void AddWrapped(string text, Ink ink, int width)
    {
        var wrapped = new List<TerminalText.Slice>();
        TerminalText.Wrap(text, width, 0, wrapped);
        foreach (var piece in wrapped) _rows.Add(new(piece.Text, ink));
    }

    private void AddInline(string text, Ink ink, int width)
    {
        var spans = MarkdownLinks.Annotate(text, ink);
        if (spans is null) AddWrapped(text, ink, width);
        else AddStyledWrapped(spans, ink, width);
    }

    private void AddStyledWrapped(IReadOnlyList<StyledSpan> spans, Ink baseInk, int width)
    {
        foreach (var row in StyledText.Wrap(spans, width))
            _rows.Add(new(row.Text, baseInk, row.Spans));
    }

    private static bool TryQuote(string line, out string quote)
    {
        quote = "";
        var indent = 0;
        while (indent < line.Length && indent < 3 && line[indent] == ' ') indent++;
        if (indent >= line.Length || line[indent] != '>') return false;
        var result = new System.Text.StringBuilder(line.Length + 2).Append(line, 0, indent);
        var pos = indent;
        while (pos < line.Length)
        {
            var spaces = 0;
            while (pos + spaces < line.Length && spaces < 3 && line[pos + spaces] == ' ') spaces++;
            if (pos + spaces >= line.Length || line[pos + spaces] != '>')
            {
                pos += spaces;
                break;
            }
            pos += spaces + 1;
            result.Append("│ ");
            if (pos < line.Length && line[pos] == ' ') pos++;
        }
        result.Append(line, pos, line.Length - pos);
        quote = result.ToString();
        return true;
    }

    private static bool TryList(string line, out string list)
    {
        list = "";
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ') indent++;
        if (indent >= 4 || indent >= line.Length) return false;
        var marker = line[indent];
        if (marker is '-' or '+' or '*' && indent + 1 < line.Length && line[indent + 1] == ' ')
        {
            list = line[..indent] + "•" + line[(indent + 1)..];
            return true;
        }
        var digits = indent;
        while (digits < line.Length && digits - indent < 9 && char.IsAsciiDigit(line[digits])) digits++;
        if (digits == indent || digits + 1 >= line.Length || line[digits] is not ('.' or ')') ||
            line[digits + 1] != ' ') return false;
        var body = line[(digits + 1)..].TrimStart(' ');
        list = line[..indent] + line[indent..digits] + ". " + body;
        return true;
    }

    private static bool IsRule(string line)
    {
        var value = line.Trim();
        if (value.Length < 3 || value[0] is not ('-' or '_' or '*')) return false;
        var marker = value[0];
        var count = 0;
        foreach (var ch in value)
        {
            if (ch == marker) count++;
            else if (ch is not (' ' or '\t')) return false;
        }
        return count >= 3;
    }

    internal static Ink Classify(ReadOnlySpan<char> line, ref Fence fence)
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
            {
                var info = body[size..].Trim();
                var split = info.IndexOfAny(' ', '\t');
                var language = (split < 0 ? info : info[..split]).ToString().Trim('{', '}');
                fence = new(ch, size, language);
                return Ink.CodeFence;
            }
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

    private static bool TryClosingFence(IReadOnlyList<SourceLine> lines, int start, Fence fence, out int close)
    {
        close = -1;
        for (var i = start; i < lines.Count; i++)
        {
            var body = lines[i].Text.TrimStart();
            var size = 0;
            while (size < body.Length && body[size] == fence.Marker) size++;
            if (size >= fence.Count && body[size..].Trim().Length == 0)
            {
                close = i;
                return true;
            }
        }
        return false;
    }

    private void RenderMermaid(IReadOnlyList<SourceLine> lines, int open, int close, Fence fence, int width)
    {
        var source = string.Join('\n', lines.Skip(open + 1).Take(close - open - 1).Select(line => line.Text));
        if (MermaidDiagram.TryRender(source, width, out var diagram, out var reason))
        {
            foreach (var line in diagram)
            {
                var trimmed = line.TrimStart();
                var ink = trimmed.StartsWith("flowchart", StringComparison.Ordinal) ||
                    trimmed.StartsWith("graph", StringComparison.Ordinal) ? Ink.Heading : Ink.Assistant;
                _rows.Add(new(TerminalText.Clip(line, width), ink));
            }
            return;
        }

        _rows.Add(new(TerminalText.Clip("此 Mermaid 子集不支持 · " + reason, width), Ink.Error));
        AddWrapped(lines[open].Text, Ink.CodeFence, width);
        for (var i = open + 1; i < close; i++) AddWrapped(lines[i].Text, Ink.Code, width);
        AddWrapped(lines[close].Text, Ink.CodeFence, width);
    }
}
