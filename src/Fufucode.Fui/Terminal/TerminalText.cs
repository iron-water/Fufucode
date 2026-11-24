using System.Globalization;
using System.Text;

namespace Fufucode.Fui;

// All dynamic strings cross this boundary before they reach the terminal. Never measure SGR text.
internal static class TerminalText
{
    internal readonly record struct Slice(int Start, string Text);

    internal static string Clean(string text) => Clean(text, 0, out _);

    internal static string Clean(string text, int cursor, out int cleanCursor, bool expandTabs = true)
    {
        cursor = Math.Clamp(cursor, 0, text.Length);
        StringBuilder? result = null;
        var copied = 0;
        cleanCursor = cursor;
        for (var i = 0; i < text.Length;)
        {
            var start = i++;
            var c = text[start];
            var replacement = "";
            switch (c)
            {
                case '\x1b':
                    if (i < text.Length)
                    {
                        switch (text[i])
                        {
                            case '[': i = SkipCsi(text, i + 1); break;
                            case ']': i = SkipString(text, i + 1, osc: true); break;
                            case 'P' or 'X' or '^' or '_': i = SkipString(text, i + 1, osc: false); break;
                            default:
                                while (i < text.Length && text[i] is >= ' ' and <= '/') i++;
                                if (i < text.Length && text[i] is >= '0' and <= '~') i++;
                                break;
                        }
                    }
                    break;
                case '\u009b': i = SkipCsi(text, i); break;
                case '\u009d': i = SkipString(text, i, osc: true); break;
                case '\u0090' or '\u0098' or '\u009e' or '\u009f': i = SkipString(text, i, osc: false); break;
                case '\r': if (i < text.Length && text[i] == '\n') i++; replacement = "\n"; break;
                case '\t': if (!expandTabs) continue; replacement = "    "; break;
                case '\u2028' or '\u2029': replacement = "\n"; break;
                case '\n': continue;
                default:
                    if (char.IsHighSurrogate(c) && i < text.Length && char.IsLowSurrogate(text[i])) { i++; continue; }
                    if (char.IsSurrogate(c)) replacement = "\ufffd";
                    else if (!ShouldStrip(c)) continue;
                    break;
            }

            result ??= new(text.Length);
            result.Append(text, copied, start - copied);
            if (cursor > start) cleanCursor += (cursor >= i ? replacement.Length : 0) - (Math.Min(cursor, i) - start);
            result.Append(replacement);
            copied = i;
        }

        return result is null ? text : result.Append(text, copied, text.Length - copied).ToString();
    }

    private static bool ShouldStrip(char c) => char.IsControl(c) || c is '\u00ad' or '\u061c' or '\u200b' or
        '\u200e' or '\u200f' or >= '\u202a' and <= '\u202e' or >= '\u2060' and <= '\u206f' or '\ufeff';

    private static int SkipCsi(string text, int i)
    {
        while (i < text.Length && text[i] is >= ' ' and <= '?') i++;
        return i < text.Length && text[i] is >= '@' and <= '~' ? i + 1 : i;
    }

    private static int SkipString(string text, int i, bool osc)
    {
        while (i < text.Length)
        {
            var c = text[i++];
            if (c == '\u009c' || (osc && c == '\a')) break;
            if (c == '\x1b' && i < text.Length && text[i] == '\\') { i++; break; }
        }
        return i;
    }

    internal static string SingleLine(string text) => Clean(text).Replace('\n', ' ');

    internal static int Width(ReadOnlySpan<char> text)
    {
        var cells = 0;
        while (!text.IsEmpty)
        {
            var length = StringInfo.GetNextTextElementLength(text);
            cells += GlyphWidth(text[..length]);
            text = text[length..];
        }
        return cells;
    }

    // Terminal columns, not UTF-16 code units. Ambiguous-width characters follow the usual narrow policy.
    internal static int GlyphWidth(ReadOnlySpan<char> glyph)
    {
        if (glyph.Length == 1 && glyph[0] == '\t') return 4;
        if (glyph.Length == 1 && glyph[0] is >= ' ' and <= '~') return 1;
        var cells = 0;
        foreach (var rune in glyph.EnumerateRunes())
        {
            var n = rune.Value;
            if (n is 0xfe0f or 0x20e3) { if (cells > 0) cells = 2; continue; } // emoji presentation / keycap
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or
                UnicodeCategory.Format or UnicodeCategory.Control) continue;
            var wide = n is >= 0x1100 and <= 0x115f or 0x231a or 0x231b or 0x2329 or 0x232a or
                >= 0x23e9 and <= 0x23ec or 0x23f0 or 0x23f3 or 0x25fd or 0x25fe or 0x2614 or 0x2615 or
                >= 0x2648 and <= 0x2653 or 0x267f or 0x2693 or 0x26a1 or 0x26aa or 0x26ab or 0x26bd or 0x26be or
                0x26c4 or 0x26c5 or 0x26ce or 0x26d4 or 0x26ea or 0x26f2 or 0x26f3 or 0x26f5 or 0x26fa or 0x26fd or
                0x2705 or 0x270a or 0x270b or 0x2728 or 0x274c or 0x274e or >= 0x2753 and <= 0x2755 or 0x2757 or
                0x2795 or 0x2796 or 0x2797 or 0x27b0 or 0x27bf or 0x2b1b or 0x2b1c or 0x2b50 or 0x2b55 or
                >= 0x2e80 and <= 0xa4cf and not 0x303f or >= 0xa960 and <= 0xa97c or >= 0xac00 and <= 0xd7a3 or
                >= 0xf900 and <= 0xfaff or >= 0xfe10 and <= 0xfe19 or >= 0xfe30 and <= 0xfe6f or
                >= 0xff01 and <= 0xff60 or >= 0xffe0 and <= 0xffe6 or >= 0x16fe0 and <= 0x18dff or
                >= 0x1aff0 and <= 0x1b2ff or >= 0x1f1e6 and <= 0x1f1ff or >= 0x1f200 and <= 0x1f251 or
                >= 0x1f300 and <= 0x1faff or >= 0x20000 and <= 0x3fffd;
            cells = Math.Max(cells, wide ? 2 : 1);
        }
        return cells;
    }

    internal static string Clip(string text, int limit, bool tail = false)
    {
        if (limit <= 0) return "";
        if (!tail)
        {
            if (IndexAtColumn(text, limit) == text.Length) return text;
            var pos = IndexAtColumn(text, limit - 1);
            return text[..pos] + "…";
        }
        if (Width(text) <= limit) return text;
        var starts = StringInfo.ParseCombiningCharacters(text);
        var end = text.Length;
        var remaining = limit - 1;
        for (var i = starts.Length - 1; i >= 0; i--)
        {
            var width = GlyphWidth(text.AsSpan(starts[i], end - starts[i]));
            if (width > remaining) break;
            remaining -= width;
            end = starts[i];
        }
        return "…" + text[end..];
    }

    internal static int IndexAtColumn(ReadOnlySpan<char> text, int column)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var length = StringInfo.GetNextTextElementLength(text[pos..]);
            var width = GlyphWidth(text.Slice(pos, length));
            if (width > column) break;
            column -= width;
            pos += length;
        }
        return pos;
    }

    // start is a known grapheme/row boundary. Keep starts so the editor cursor and streaming suffix share the same wrap.
    internal static void Wrap(string text, int columns, int start, List<Slice> rows)
    {
        columns = Math.Max(1, columns);
        var rowStart = start;
        var cells = 0;
        string? substitute = null;
        for (var i = start; i < text.Length;)
        {
            if (text[i] == '\n') { Emit(i); rowStart = ++i; continue; }
            var length = StringInfo.GetNextTextElementLength(text.AsSpan(i));
            var width = GlyphWidth(text.AsSpan(i, length));
            if (cells > 0 && cells + width > columns) { Emit(i); rowStart = i; }
            if (width > columns) substitute = "…"; // a two-cell glyph in a one-cell viewport
            cells += Math.Min(width, columns);
            i += length;
        }
        Emit(text.Length);

        void Emit(int end)
        {
            rows.Add(new(rowStart, substitute ?? text[rowStart..end]));
            cells = 0;
            substitute = null;
        }
    }
}
