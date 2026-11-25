using System.Text;
using System.Text.RegularExpressions;

namespace Fufucode.Fui;

internal sealed record MarkdownLinkTarget(string Label, string Url, int Line);

internal static class MarkdownLinks
{
    private static readonly Regex BareUrl = new(
        """(?<![A-Za-z0-9_@])(?:https?://|www\.)[^\s<>"'`\x00-\x1f\x7f]+""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static IReadOnlyList<MarkdownLinkTarget> Extract(IEnumerable<TuiLine> lines)
    {
        var targets = new List<MarkdownLinkTarget>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var transcriptLine = 0;
        foreach (var line in lines)
        {
            transcriptLine++;
            if (line.Kind != "assistant") continue;
            var source = TerminalText.Clean(line.Text);
            var masked = MaskCode(source);
            var starts = new List<(int Index, int Length, string Label, string Url)>();
            for (var index = 0; index < masked.Length; index++)
            {
                if (masked[index] != '[' || IsEscaped(masked, index) ||
                    index > 0 && masked[index - 1] == '!' && !IsEscaped(masked, index - 1)) continue;
                var labelEnd = FindLabelEnd(masked, index);
                if (labelEnd - index > 512) continue;
                if (labelEnd < 0 || labelEnd + 1 >= masked.Length || masked[labelEnd + 1] != '(') continue;
                if (!TryLinkDestination(masked, labelEnd + 2, out var destinationEnd, out var linkEnd)) continue;
                var destination = source[(labelEnd + 2)..destinationEnd].Trim();
                if (destination.Length >= 2 && destination[0] == '<' && destination[^1] == '>')
                    destination = destination[1..^1];
                destination = NormalizeUrl(destination);
                if (!TuiHyperlinkPolicy.IsSafeUrl(destination)) continue;
                var label = source[(index + 1)..labelEnd].Trim();
                starts.Add((index, linkEnd + 1 - index, label.Length == 0 ? destination : label, destination));
            }
            foreach (Match match in BareUrl.Matches(masked))
            {
                var url = NormalizeUrl(TrimUrl(source.AsSpan(match.Index, match.Length)));
                if (url.Length == 0 || !TuiHyperlinkPolicy.IsSafeUrl(url)) continue;
                if (starts.Any(item => match.Index >= item.Index && match.Index < item.Index + item.Length)) continue;
                starts.Add((match.Index, url.Length, url, url));
            }
            foreach (var item in starts.OrderBy(item => item.Index))
                if (seen.Add(item.Url)) targets.Add(new(item.Label, item.Url, transcriptLine));
        }
        return targets;
    }

    internal static IReadOnlyList<FuiChoice> Choices(IEnumerable<TuiLine> lines) =>
        Extract(lines).Select(target =>
        {
            var label = string.Equals(target.Label, target.Url, StringComparison.Ordinal)
                ? target.Url
                : target.Label + "  " + target.Url;
            return new FuiChoice(target.Url, label, target.Url);
        }).ToArray();

    // Adds hyperlink metadata to visible URLs without changing the string. Markdown destinations have
    // already been converted to `label (url)` by the formatter, so the URL remains a separate target.
    internal static IReadOnlyList<StyledSpan>? Annotate(string text, Ink ink)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var masked = MaskInlineCode(text);
        var matches = BareUrl.Matches(masked);
        if (matches.Count == 0) return null;
        var spans = new List<StyledSpan>();
        var copied = 0;
        foreach (Match match in matches)
        {
            var url = TrimUrl(text.AsSpan(match.Index, match.Length));
            if (url.Length == 0 || !TuiHyperlinkPolicy.IsSafeUrl(url)) continue;
            if (match.Index > copied) Add(text[copied..match.Index], ink, null);
            Add(url, Ink.Link, NormalizeUrl(url));
            copied = match.Index + url.Length;
        }
        if (copied == 0) return null;
        if (copied < text.Length) Add(text[copied..], ink, null);
        return spans;

        void Add(string value, Ink style, string? link)
        {
            if (value.Length == 0) return;
            if (spans.Count != 0 && spans[^1].Ink == style && spans[^1].Link == link)
                spans[^1] = spans[^1] with { Text = spans[^1].Text + value };
            else spans.Add(new(value, style, link));
        }
    }

    private static string MaskCode(string source)
    {
        var masked = source.ToCharArray();
        var fence = default(MarkdownLines.Fence);
        for (var lineStart = 0; lineStart <= source.Length;)
        {
            var newline = source.IndexOf('\n', lineStart);
            var end = newline < 0 ? source.Length : newline;
            var line = source.AsSpan(lineStart, end - lineStart);
            var ink = MarkdownLines.Classify(line, ref fence);
            if (ink is Ink.Code or Ink.CodeFence)
                masked.AsSpan(lineStart, end - lineStart).Fill(' ');
            else MaskInlineCode(masked.AsSpan(lineStart, end - lineStart));
            if (newline < 0) break;
            lineStart = newline + 1;
        }
        return new string(masked);
    }

    private static string MaskInlineCode(string text)
    {
        var chars = text.ToCharArray();
        MaskInlineCode(chars);
        return new string(chars);
    }

    private static void MaskInlineCode(Span<char> text)
    {
        for (var i = 0; i < text.Length;)
        {
            if (text[i] != '`') { i++; continue; }
            var marker = i;
            var size = 1;
            while (i + size < text.Length && text[i + size] == '`') size++;
            var close = FindRun(text, i + size, size);
            if (close < 0)
            {
                i += size;
                continue;
            }
            text[marker..(close + size)].Fill(' ');
            i = close + size;
        }
    }

    private static int FindRun(ReadOnlySpan<char> text, int start, int size)
    {
        for (var i = start; i < text.Length;)
        {
            if (text[i] != '`') { i++; continue; }
            var run = 1;
            while (i + run < text.Length && text[i + run] == '`') run++;
            if (run == size) return i;
            i += run;
        }
        return -1;
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

    private static bool TryLinkDestination(string text, int start, out int destinationEnd, out int linkEnd)
    {
        destinationEnd = -1;
        linkEnd = -1;
        if (start >= text.Length) return false;
        if (text[start] == '<')
        {
            var angle = text.IndexOf('>', start + 1);
            if (angle < 0 || text.AsSpan(start + 1, angle - start - 1).ContainsAny('\r', '\n')) return false;
            destinationEnd = angle + 1;
            var close = SkipWhitespace(text, angle + 1);
            if (close >= text.Length || text[close] != ')') return false;
            linkEnd = close;
            return true;
        }

        var depth = 1;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (char.IsWhiteSpace(text[i]))
            {
                if (destinationEnd < 0) destinationEnd = i;
                var title = SkipWhitespace(text, i);
                if (title >= text.Length) return false;
                if (text[title] == ')')
                {
                    linkEnd = title;
                    return destinationEnd > start;
                }
                if (text[title] is not ('"' or '\'')) return false;
                var quote = text[title];
                var end = text.IndexOf(quote, title + 1);
                if (end < 0) return false;
                var close = SkipWhitespace(text, end + 1);
                if (close >= text.Length || text[close] != ')') return false;
                linkEnd = close;
                return destinationEnd > start;
            }
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0)
            {
                if (destinationEnd < 0) destinationEnd = i;
                linkEnd = i;
                return destinationEnd > start;
            }
        }
        return false;
    }

    private static int SkipWhitespace(string text, int start)
    {
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        return start;
    }

    private static bool IsEscaped(string text, int index)
    {
        var slashes = 0;
        while (index > 0 && text[--index] == '\\') slashes++;
        return (slashes & 1) != 0;
    }

    private static string TrimUrl(ReadOnlySpan<char> candidate)
    {
        var end = candidate.Length;
        while (end > 0 && candidate[end - 1] is '.' or ',' or ';' or ':' or '!' or '?' or ']' or '}')
            end--;
        while (end > 0 && candidate[end - 1] == ')' &&
            candidate[..end].Count(')') > candidate[..end].Count('(')) end--;
        return candidate[..end].ToString();
    }

    private static string NormalizeUrl(string url) =>
        url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;
}
