using System.Globalization;
using System.Text;

namespace Fufucode.Fui;

internal sealed class StyledSpanList : IReadOnlyList<StyledSpan>, IEquatable<StyledSpanList>
{
    private readonly StyledSpan[] _items;

    private StyledSpanList(StyledSpan[] items)
    {
        _items = items;
        Text = string.Concat(items.Select(span => span.Text));
    }

    internal static StyledSpanList From(IReadOnlyList<StyledSpan> spans) => new([.. spans]);
    public int Count => _items.Length;
    internal string Text { get; }
    public StyledSpan this[int index] => _items[index];
    public IEnumerator<StyledSpan> GetEnumerator() => ((IEnumerable<StyledSpan>)_items).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public bool Equals(StyledSpanList? other) =>
        other is not null && _items.AsSpan().SequenceEqual(other._items);

    public override bool Equals(object? obj) => obj is StyledSpanList other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var span in _items) hash.Add(span);
        return hash.ToHashCode();
    }
}

internal readonly record struct StyledLine(string Text, StyledSpanList Spans);

// Wraps semantic spans without embedding SGR/OSC bytes in the text. The terminal painter is the only
// layer that turns this metadata into control sequences.
internal static class StyledText
{
    internal static List<StyledLine> Wrap(IReadOnlyList<StyledSpan> spans, int width)
    {
        width = Math.Max(1, width);
        var lines = new List<StyledLine>();
        var text = new StringBuilder();
        var styled = new List<StyledSpan>();
        var segment = new StringBuilder();
        var segmentInk = Ink.Plain;
        string? segmentLink = null;
        var cells = 0;

        void Add(ReadOnlySpan<char> value, Ink ink, string? link)
        {
            if (value.IsEmpty) return;
            text.Append(value);
            if (segment.Length != 0 && (segmentInk != ink || segmentLink != link)) EndSegment();
            segmentInk = ink;
            segmentLink = link;
            segment.Append(value);
        }

        void EndSegment()
        {
            if (segment.Length == 0) return;
            styled.Add(new(segment.ToString(), segmentInk, segmentLink));
            segment.Clear();
        }

        void Flush()
        {
            EndSegment();
            var content = text.ToString();
            lines.Add(new(content, StyledSpanList.From(styled)));
            text.Clear();
            styled.Clear();
            cells = 0;
        }

        foreach (var span in spans)
        {
            if (string.IsNullOrEmpty(span.Text)) continue;
            for (var start = 0; start < span.Text.Length;)
            {
                var length = StringInfo.GetNextTextElementLength(span.Text.AsSpan(start));
                var glyph = span.Text.AsSpan(start, length);
                var glyphWidth = TerminalText.GlyphWidth(glyph);
                if (cells != 0 && cells + glyphWidth > width) Flush();
                if (glyphWidth > width)
                {
                    Add("…", span.Ink, span.Link);
                    cells = 1;
                }
                else
                {
                    Add(glyph, span.Ink, span.Link);
                    cells += glyphWidth;
                }
                start += length;
            }
        }
        if (text.Length != 0 || lines.Count == 0) Flush();
        return lines;
    }
}
