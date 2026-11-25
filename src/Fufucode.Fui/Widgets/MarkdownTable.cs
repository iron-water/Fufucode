using System.Text;

namespace Fufucode.Fui;

// Small GFM table subset: pipe cells, alignment markers and fixed-width terminal rows.
// It deliberately does not try to reproduce the full GFM grammar.
internal static class MarkdownTable
{
    private const string Gap = " │ ";
    private const int MaxColumns = 32;
    private enum Alignment { Left, Center, Right }

    internal static bool TryFormat(IReadOnlyList<MarkdownLines.SourceLine> lines, int start, int width,
        List<MarkdownLines.Row> rows, out int next)
    {
        next = start;
        if (start + 1 >= lines.Count || !lines[start].Complete || !HasUnescapedPipe(lines[start].Text)) return false;
        if (StartsBlock(lines[start].Text)) return false;
        var header = SplitCells(lines[start].Text).ToArray();
        var delimiters = SplitCells(lines[start + 1].Text);
        if (header.Length == 0 || header.Length != delimiters.Count || header.Length > MaxColumns) return false;
        if (!TryAlignment(delimiters, out var alignment)) return false;

        for (var i = 0; i < header.Length; i++) header[i] = FormatCell(header[i]);
        var rowCells = new List<string[]>();
        string? partial = null;
        var end = start + 2;
        while (end < lines.Count)
        {
            var line = lines[end];
            if (string.IsNullOrWhiteSpace(line.Text) || StartsBlock(line.Text)) break;
            if (!line.Complete && !EndsWithUnescapedPipe(line.Text))
            {
                // A growing last row stays outside the fixed table geometry. Treating it as a
                // cell would either clip the text or move every painted row on each chunk.
                partial = TerminalText.Clean(line.Text);
                end++;
                break;
            }
            rowCells.Add(ParseRow(line.Text, header.Length));
            end++;
        }

        var widths = ColumnWidths(header, rowCells, width, out var available);
        if (widths is null) return false;
        rows.Add(new(RenderRow(header, widths, alignment), Ink.Heading));
        rows.Add(new(string.Join("─┼─", widths.Select(value => new string('─', value))), Ink.Quiet));
        foreach (var cells in rowCells)
            rows.Add(new(RenderRow(cells, widths, alignment), Ink.Plain));
        if (partial is not null)
            rows.Add(new("  " + TerminalText.Clip(partial, Math.Max(1, width - 2)), Ink.Quiet));
        next = end;
        return true;
    }

    internal static string FormatInline(string text)
    {
        var source = TerminalText.Clean(text);
        StringBuilder? result = null;
        var copied = 0;
        for (var i = 0; i < source.Length;)
        {
            if (source[i] == '`')
            {
                i = SkipCodeSpan(source, i);
                continue;
            }
            if (source[i] == '\\')
            {
                i = Math.Min(source.Length, i + 2);
                continue;
            }
            if (source[i] != '[' || IsEscaped(source, i) || i > 0 && source[i - 1] == '!' && !IsEscaped(source, i - 1))
            {
                i++;
                continue;
            }
            var close = FindLabelEnd(source, i);
            if (close < 0 || close + 1 >= source.Length || source[close + 1] != '(')
            {
                i++;
                continue;
            }
            var destinationEnd = FindDestinationEnd(source, close + 2);
            if (destinationEnd < 0)
            {
                i++;
                continue;
            }
            var label = source[(i + 1)..close];
            var destination = source[(close + 2)..destinationEnd].Trim();
            if (destination.Length >= 2 && destination[0] == '<' && destination[^1] == '>')
                destination = destination[1..^1];
            if (destination.Length == 0)
            {
                i++;
                continue;
            }
            var shown = label.Length == 0 ? destination : label;
            var hint = shown.Equals(destination, StringComparison.Ordinal) ? shown : $"{shown} ({destination})";
            result ??= new(source.Length + 16);
            result.Append(source, copied, i - copied).Append(hint);
            i = destinationEnd + 1;
            copied = i;
        }
        return result is null ? source : result.Append(source, copied, source.Length - copied).ToString();
    }

    private static int[]? ColumnWidths(string[] header, List<string[]> data, int width, out int available)
    {
        var columns = header.Length;
        available = width - Gap.Length * (columns - 1);
        if (available < columns) return null;
        var desired = new int[columns];
        for (var column = 0; column < columns; column++)
        {
            desired[column] = Math.Max(1, TerminalText.Width(header[column]));
            foreach (var cells in data)
            {
                desired[column] = Math.Max(desired[column], TerminalText.Width(cells[column]));
            }
        }

        var total = desired.Sum();
        if (total > available)
        {
            var excess = total - available;
            while (excess > 0)
            {
                var largest = desired.Max();
                var next = desired.Where(value => value < largest).DefaultIfEmpty(1).Max();
                var count = desired.Count(value => value == largest);
                var level = Math.Max(1, next);
                var capacity = (largest - level) * count;
                if (capacity <= 0)
                {
                    if (largest <= 1) return null;
                    level = largest - 1;
                    capacity = count;
                }
                if (capacity <= excess)
                {
                    for (var i = 0; i < desired.Length; i++)
                        if (desired[i] == largest) desired[i] = level;
                    excess -= capacity;
                    continue;
                }
                var each = excess / count;
                var remainder = excess % count;
                for (var i = 0; i < desired.Length && excess > 0; i++)
                {
                    if (desired[i] != largest) continue;
                    var cut = Math.Min(desired[i] - level, each + (remainder-- > 0 ? 1 : 0));
                    desired[i] -= cut;
                    excess -= cut;
                }
            }
            total = desired.Sum();
        }

        // Keep the table at its content width. Expanding every column to the viewport turns a
        // two-column result into mostly whitespace and makes unrelated rows hard to scan.
        return desired;
    }

    private static string RenderRow(string[] cells, int[] widths, Alignment[] alignment)
    {
        var rendered = new string[widths.Length];
        for (var i = 0; i < widths.Length; i++)
        {
            var text = TerminalText.Clip(cells[i], widths[i]);
            var padding = Math.Max(0, widths[i] - TerminalText.Width(text));
            var left = alignment[i] switch
            {
                Alignment.Right => padding,
                Alignment.Center => padding / 2,
                _ => 0
            };
            rendered[i] = new string(' ', left) + text + new string(' ', padding - left);
        }
        return string.Join(Gap, rendered);
    }

    private static string[] ParseRow(string line, int count)
    {
        var parsed = SplitCells(line);
        var cells = new string[count];
        for (var i = 0; i < count; i++) cells[i] = i < parsed.Count ? FormatCell(parsed[i]) : "";
        return cells;
    }

    private static string FormatCell(string text)
    {
        var clean = TerminalText.Clean(text).Replace('\n', ' ').Trim();
        return FormatInline(clean.Replace("\\|", "|", StringComparison.Ordinal));
    }

    private static bool TryAlignment(List<string> cells, out Alignment[] alignment)
    {
        alignment = new Alignment[cells.Count];
        for (var i = 0; i < cells.Count; i++)
        {
            var value = cells[i].Trim();
            var left = value.StartsWith(':');
            var right = value.EndsWith(':');
            var dashes = value.Length - (left ? 1 : 0) - (right ? 1 : 0);
            if (dashes < 1 || value.Trim(':').Any(ch => ch != '-')) return false;
            alignment[i] = left && right ? Alignment.Center : right ? Alignment.Right : Alignment.Left;
        }
        return true;
    }

    private static List<string> SplitCells(string line)
    {
        var cells = new List<string>();
        var start = 0;
        var pipe = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] != '|' || IsEscaped(line, i)) continue;
            pipe = true;
            cells.Add(line[start..i]);
            start = i + 1;
        }
        if (!pipe) return cells;
        cells.Add(line[start..]);
        if (cells.Count > 0 && string.IsNullOrWhiteSpace(cells[0]) && StartsWithPipe(line)) cells.RemoveAt(0);
        if (cells.Count > 0 && string.IsNullOrWhiteSpace(cells[^1]) && EndsWithUnescapedPipe(line)) cells.RemoveAt(cells.Count - 1);
        return cells;
    }

    private static bool StartsWithPipe(string line)
    {
        foreach (var ch in line)
        {
            if (char.IsWhiteSpace(ch)) continue;
            return ch == '|';
        }
        return false;
    }

    private static bool HasUnescapedPipe(string line)
    {
        for (var i = 0; i < line.Length; i++)
            if (line[i] == '|' && !IsEscaped(line, i)) return true;
        return false;
    }

    private static bool EndsWithUnescapedPipe(string line)
    {
        var end = line.Length - 1;
        while (end >= 0 && char.IsWhiteSpace(line[end])) end--;
        return end >= 0 && line[end] == '|' && !IsEscaped(line, end);
    }

    private static int SkipCodeSpan(string text, int start)
    {
        var size = 1;
        while (start + size < text.Length && text[start + size] == '`') size++;
        for (var i = start + size; i < text.Length;)
        {
            if (text[i] != '`') { i++; continue; }
            var run = 1;
            while (i + run < text.Length && text[i + run] == '`') run++;
            if (run == size) return i + run;
            i += run;
        }
        return text.Length;
    }

    private static int FindLabelEnd(string text, int open)
    {
        var depth = 0;
        for (var i = open + 1; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == '[') depth++;
            else if (text[i] == ']' && depth-- == 0) return i;
        }
        return -1;
    }

    private static int FindDestinationEnd(string text, int start)
    {
        var depth = 1;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (char.IsWhiteSpace(text[i])) return -1;
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static bool IsEscaped(string text, int index)
    {
        var slashes = 0;
        while (index > 0 && text[--index] == '\\') slashes++;
        return (slashes & 1) != 0;
    }

    private static bool StartsBlock(string line)
    {
        var value = line.TrimStart();
        if (value.Length == 0) return false;
        if (value.StartsWith("```", StringComparison.Ordinal) || value.StartsWith("~~~", StringComparison.Ordinal)) return true;
        if (value[0] == '#')
        {
            var depth = 0;
            while (depth < value.Length && depth < 6 && value[depth] == '#') depth++;
            if (depth > 0 && (depth == value.Length || value[depth] == ' ')) return true;
        }
        if (value[0] == '>') return true;
        if (value.Length > 1 && value[0] is '-' or '+' or '*' && value[1] == ' ') return true;
        var digits = 0;
        while (digits < value.Length && digits < 9 && char.IsAsciiDigit(value[digits])) digits++;
        if (digits > 0 && digits + 1 < value.Length && value[digits] is '.' or ')' && value[digits + 1] == ' ') return true;
        return IsRule(value);
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
}
