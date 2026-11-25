using System.Globalization;
using System.Text;

namespace Fufucode.Fui;

// Unified rich-content projector for terminal messages: combines Markdown tables/lists/quotes/links,
// transparent LaTeX math (display + inline), embedded Markdown/HTML/data-URI images and attached TuiImages.
// One instance belongs to one message at one terminal geometry so rendered SIXEL strips stay cached across frames.
internal sealed class RichRows
{
    private readonly List<ScreenRow> _rows = [];
    private readonly Dictionary<(string Latex, bool Display), FormulaRaster?> _formulas = [];
    private readonly Dictionary<string, ImageRaster?> _embeddedImages = new(StringComparer.Ordinal);
    private readonly Dictionary<TuiImage, ImageRaster?> _attachedImages = new(ReferenceEqualityComparer.Instance);
    private const int MaxCachedFormulas = 48;
    private const int MaxCachedImages = 24;

    internal IReadOnlyList<ScreenRow> Rows => _rows;

    internal static bool MayContainMath(string text) => text.Contains('$') || text.Contains(@"\(", StringComparison.Ordinal) ||
        text.Contains(@"\[", StringComparison.Ordinal) || ((text.Contains("```", StringComparison.Ordinal) || text.Contains("~~~", StringComparison.Ordinal)) &&
        (text.Contains("math", StringComparison.OrdinalIgnoreCase) || text.Contains("latex", StringComparison.OrdinalIgnoreCase)));

    internal void Project(string rawText, int width, ImageCells? cell, bool allowMarkdown, bool allowMath,
        Func<string, ResolvedImage?>? resolveImage, IReadOnlyList<TuiImage>? attached = null, Ink baseInk = Ink.Plain)
    {
        _rows.Clear();
        width = Math.Max(1, width);
        var text = TerminalText.Clean(rawText);
        var lines = MarkdownLines.SplitLines(text);
        var fence = default(MarkdownLines.Fence);

        for (var i = 0; i < lines.Count;)
        {
            var line = lines[i].Text;
            var prior = fence;
            var ink = allowMarkdown ? MarkdownLines.Classify(line, ref fence) : ClassifyFenceOnly(line, ref fence, baseInk);

            if (prior.Count == 0 && allowMath && cell is { } mathCell &&
                TryDisplayMath(lines, i, ink, fence, out var latex, out var lastLine) &&
                GetFormula(latex, true, width, mathCell) is { } formula)
            {
                AddFormulaLine("", [new(0, formula)], Ink.Plain);
                i = lastLine + 1;
                fence = default;
                continue;
            }

            if (prior.Count == 0 && allowMarkdown && ink == Ink.CodeFence &&
                SyntaxHighlighter.NormalizeLanguage(fence.Language) == "mermaid" &&
                MarkdownLines.TryClosingFence(lines, i + 1, fence, out var mermaidClose))
            {
                RenderMermaid(lines, i, mermaidClose, width);
                fence = default;
                i = mermaidClose + 1;
                continue;
            }

            if (prior.Count == 0 && allowMarkdown && ink == Ink.Plain)
            {
                var tableRows = new List<MarkdownLines.Row>();
                if (MarkdownTable.TryFormat(lines, i, width, tableRows, out var next))
                {
                    fence = prior;
                    foreach (var row in tableRows)
                        _rows.Add(new(row.Text, row.Ink == Ink.Plain ? baseInk : row.Ink, Spans: row.Spans));
                    i = next;
                    continue;
                }
            }

            if (ink is Ink.Code or Ink.CodeFence)
            {
                if (ink == Ink.Code && prior.Count != 0 && prior.Language.Length != 0)
                    AddStyledWrapped(SyntaxHighlighter.Highlight(line, prior.Language), ink, width);
                else
                    Plain(line, ink, width);
                i++;
                continue;
            }

            if (allowMarkdown && MarkdownLines.IsRule(line))
            {
                _rows.Add(new(new string('─', width), Ink.Quiet));
                i++;
                continue;
            }

            var effectiveLine = line;
            var effectiveInk = ink == Ink.Plain ? baseInk : ink;
            if (allowMarkdown)
            {
                if (MarkdownLines.TryQuote(line, out var quote)) { effectiveLine = quote; effectiveInk = Ink.Quiet; }
                else if (MarkdownLines.TryList(line, out var list)) { effectiveLine = list; effectiveInk = baseInk; }
            }

            RenderProseLine(effectiveLine, effectiveInk, width, cell, allowMarkdown, allowMath, resolveImage);
            i++;
        }

        AppendAttached(attached, width, cell);
    }

    internal void AppendAttached(IReadOnlyList<TuiImage>? attached, int width, ImageCells? cell)
    {
        if (cell is not { } pixels || attached is not { Count: > 0 }) return;
        foreach (var item in attached)
        {
            var raster = GetAttachedImage(item, width, pixels);
            if (raster is not null) AddImageRows(raster);
            else _rows.Add(new(TerminalText.Clip($"[图片不可读：{item.Label}]", width), Ink.Quiet));
        }
    }

    private void RenderProseLine(string line, Ink ink, int width, ImageCells? cell, bool allowMarkdown,
        bool allowMath, Func<string, ResolvedImage?>? resolveImage)
    {
        var refs = MarkdownImages.MayContainImage(line) ? MarkdownImages.Extract(line) : null;
        if (refs is { Count: > 0 })
        {
            RenderLineWithImages(line, refs, ink, width, cell, allowMarkdown, allowMath, resolveImage);
            return;
        }

        line = MarkdownImages.CollapseIncompleteDataUri(line);
        var formatted = allowMarkdown ? MarkdownTable.FormatInline(line) : line;
        if (allowMath && cell is { } pixels && MayContainInlineMath(formatted))
            InlineMath(formatted, ink, width, pixels);
        else
            AddInline(formatted, ink, width);
    }

    private void RenderLineWithImages(string line, List<MarkdownImageRef> refs, Ink ink, int width, ImageCells? cell,
        bool allowMarkdown, bool allowMath, Func<string, ResolvedImage?>? resolveImage)
    {
        var textBuilder = new StringBuilder(line.Length);
        var blocks = new List<ImageRaster>();
        var copied = 0;

        foreach (var item in refs)
        {
            textBuilder.Append(line, copied, item.Start - copied);
            ImageRaster? raster = null;
            if (cell is { } pixels)
                raster = GetEmbeddedImage(item.Target, width, pixels, resolveImage);

            if (raster is not null)
            {
                blocks.Add(raster);
            }
            else
            {
                textBuilder.Append(MarkdownImages.FormatFallback(item));
            }
            copied = item.Start + item.Length;
        }
        textBuilder.Append(line, copied, line.Length - copied);

        var remaining = MarkdownImages.CollapseIncompleteDataUri(textBuilder.ToString());
        if (blocks.Count == 0 || !string.IsNullOrWhiteSpace(remaining))
        {
            var formatted = allowMarkdown ? MarkdownTable.FormatInline(remaining) : remaining;
            if (allowMath && cell is { } pixels && MayContainInlineMath(formatted))
                InlineMath(formatted, ink, width, pixels);
            else
                AddInline(formatted, ink, width);
        }
        foreach (var raster in blocks) AddImageRows(raster);
    }

    private void AddImageRows(ImageRaster raster)
    {
        foreach (var strip in raster.Strips)
        {
            if (strip.Length == 0) _rows.Add(new("", Ink.Plain));
            else _rows.Add(new("", Ink.Plain, [new ImageStrip(0, strip)]));
        }
    }

    private ImageRaster? GetEmbeddedImage(string target, int width, ImageCells cell, Func<string, ResolvedImage?>? resolveImage)
    {
        if (_embeddedImages.TryGetValue(target, out var cached)) return cached;
        if (_embeddedImages.Count >= MaxCachedImages) return null;

        ImageRaster? raster = null;
        if (MarkdownImages.IsDataUri(target))
        {
            if (MarkdownImages.TryDecodeDataUri(target, out var mime, out var bytes))
                raster = ImageRaster.Create(bytes, mime, width, cell, maxRows: 16);
        }
        else if (!MarkdownImages.IsRemoteUrl(target) && resolveImage is not null)
        {
            var resolved = resolveImage(target);
            if (resolved is not null)
                raster = ImageRaster.Create(resolved.Bytes, resolved.MimeType, width, cell, maxRows: 16,
                    predecoded: resolved.MimeType == "image/svg+xml" ? null : resolved.Decoded);
        }
        _embeddedImages[target] = raster;
        return raster;
    }

    private ImageRaster? GetAttachedImage(TuiImage item, int width, ImageCells cell)
    {
        if (_attachedImages.TryGetValue(item, out var cached)) return cached;
        if (_attachedImages.Count >= MaxCachedImages) return null;

        var bytes = item.Bytes ?? item.LoadBytes?.Invoke();
        var raster = bytes is { Length: > 0 }
            ? ImageRaster.Create(bytes, item.MimeType, width, cell, item.MaxRows)
            : null;
        _attachedImages[item] = raster;
        return raster;
    }

    private void RenderMermaid(IReadOnlyList<MarkdownLines.SourceLine> lines, int open, int close, int width)
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
        Plain(lines[open].Text, Ink.CodeFence, width);
        for (var i = open + 1; i < close; i++) Plain(lines[i].Text, Ink.Code, width);
        Plain(lines[close].Text, Ink.CodeFence, width);
    }

    private static Ink ClassifyFenceOnly(string line, ref MarkdownLines.Fence fence, Ink baseInk)
    {
        var indent = 0;
        while (indent < line.Length && indent < 4 && line[indent] == ' ') indent++;
        var body = line.AsSpan(indent);
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
        return fence.Count > 0 ? Ink.Code : baseInk;
    }

    private static bool TryDisplayMath(IReadOnlyList<MarkdownLines.SourceLine> lines, int startLine, Ink ink,
        MarkdownLines.Fence fence, out string latex, out int lastLine)
    {
        latex = "";
        lastLine = startLine;
        if (ink == Ink.Code) return false;
        var line = lines[startLine].Text;
        var body = line.Trim();
        if (ink == Ink.CodeFence)
        {
            if (fence.Count == 0) return false;
            var language = body[fence.Count..].Trim();
            if (!language.Equals("math", StringComparison.OrdinalIgnoreCase) &&
                !language.Equals("latex", StringComparison.OrdinalIgnoreCase)) return false;
            var sb = new StringBuilder();
            for (var i = startLine + 1; i < lines.Count && sb.Length <= FormulaRaster.MaxSourceLength + 2; i++)
            {
                var after = fence;
                if (MarkdownLines.Classify(lines[i].Text, ref after) == Ink.CodeFence && after.Count == 0)
                {
                    latex = sb.ToString().Trim();
                    lastLine = i;
                    return true;
                }
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(lines[i].Text);
            }
            return false;
        }

        var close = body.StartsWith("$$", StringComparison.Ordinal) ? "$$" :
            body.StartsWith(@"\[", StringComparison.Ordinal) ? @"\]" : null;
        if (close is null) return false;

        var firstContent = body[2..];
        var sameLineClose = firstContent.IndexOf(close, StringComparison.Ordinal);
        if (sameLineClose >= 0)
        {
            if (!firstContent.AsSpan(sameLineClose + 2).Trim().IsEmpty) return false;
            latex = firstContent[..sameLineClose].Trim();
            lastLine = startLine;
            return true;
        }

        var acc = new StringBuilder(firstContent);
        for (var i = startLine + 1; i < lines.Count && acc.Length <= FormulaRaster.MaxSourceLength + 2; i++)
        {
            var cur = lines[i].Text;
            var closePos = cur.IndexOf(close, StringComparison.Ordinal);
            if (closePos >= 0)
            {
                if (!cur.AsSpan(closePos + 2).Trim().IsEmpty) return false;
                if (acc.Length > 0) acc.Append('\n');
                acc.Append(cur, 0, closePos);
                latex = acc.ToString().Trim();
                lastLine = i;
                return true;
            }
            if (acc.Length > 0) acc.Append('\n');
            acc.Append(cur);
        }
        return false;
    }

    private static bool MayContainInlineMath(string text) =>
        text.Contains('$') || text.Contains(@"\(", StringComparison.Ordinal);

    private FormulaRaster? GetFormula(string latex, bool display, int width, ImageCells cell)
    {
        var key = (latex, display);
        if (_formulas.TryGetValue(key, out var image)) return image;
        if (_formulas.Count >= MaxCachedFormulas) return null;
        image = FormulaRaster.Create(latex, display, width, cell);
        _formulas.Add(key, image);
        return image;
    }

    private void AddInline(string text, Ink ink, int width)
    {
        var spans = MarkdownLinks.Annotate(text, ink);
        if (spans is null) Plain(text, ink, width);
        else AddStyledWrapped(spans, ink, width);
    }

    private void AddStyledWrapped(IReadOnlyList<StyledSpan> spans, Ink baseInk, int width)
    {
        foreach (var row in StyledText.Wrap(spans, width))
            _rows.Add(new(row.Text, baseInk, Spans: row.Spans));
    }

    private void Plain(string text, Ink ink, int width)
    {
        var wrapped = new List<TerminalText.Slice>();
        TerminalText.Wrap(text, width, 0, wrapped);
        foreach (var piece in wrapped) _rows.Add(new(piece.Text, ink));
    }

    private readonly record struct PlacedFormula(int Column, FormulaRaster Image);

    private void InlineMath(string line, Ink ink, int width, ImageCells cell)
    {
        var buffer = new StringBuilder();
        var images = new List<PlacedFormula>();
        var column = 0;
        void Flush() { AddFormulaLine(buffer.ToString(), images, ink); buffer.Clear(); images.Clear(); column = 0; }
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
            var image = GetFormula(line[offset..end], false, width, cell);
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

    private void AddFormulaLine(string text, List<PlacedFormula> images, Ink ink)
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
