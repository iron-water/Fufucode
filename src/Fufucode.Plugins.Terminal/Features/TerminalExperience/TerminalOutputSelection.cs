using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Fufucode.Plugins.Terminal.Infrastructure;

namespace Fufucode.Plugins.Terminal.Features.TerminalExperience;

/// <summary>One fixed-cell position in a <see cref="TerminalSnapshot"/> (row / column, zero based).</summary>
public readonly record struct TerminalCellPosition(int Row, int Column);

/// <summary>
/// Output selection owned by a terminal tab. The selection is anchored to one concrete
/// <see cref="TerminalSnapshot"/> instance instead of to the live buffer, so the copied text is
/// exactly what was selected at that moment: later output, in-place rewrites or scrollback
/// truncation can never re-use the old indices against new text.
/// </summary>
public sealed class TerminalOutputSelection
{
    private TerminalSnapshot? _snapshot;
    private TerminalCellPosition _anchor;
    private TerminalCellPosition _caret;
    private string? _text;

    /// <summary>Snapshot this selection was captured from; null when nothing is selected.</summary>
    public TerminalSnapshot? Snapshot => _snapshot;

    /// <summary>True when the pointer has selected at least one cell.</summary>
    public bool HasRange => _snapshot is not null && _anchor != _caret;

    /// <summary>Resolved selection text; empty whenever there is no usable range.</summary>
    public string Text => _text ??= _snapshot is null ? string.Empty : TerminalSelectionText.Extract(_snapshot, _anchor, _caret);

    public bool HasText => Text.Length > 0;

    public TerminalCellPosition Anchor => _anchor;
    public TerminalCellPosition Caret => _caret;

    /// <summary>True only when the pane currently renders this very snapshot (highlight is faithful).</summary>
    public bool IsOnSnapshot(TerminalSnapshot? snapshot) => snapshot is not null && ReferenceEquals(_snapshot, snapshot);

    /// <summary>
    /// Selection range after grapheme-cluster expansion and wide-glyph widening; null without a range.
    /// The pane highlights this exact range so the highlight always matches the copied text.
    /// </summary>
    public (TerminalCellPosition Start, TerminalCellPosition End)? ResolvedRange =>
        _snapshot is null || !HasRange ? null : TerminalSelectionText.Resolve(_snapshot, _anchor, _caret);

    /// <summary>Start a fresh selection on the given snapshot (pointer press).</summary>
    public void Begin(TerminalSnapshot snapshot, TerminalCellPosition position)
    {
        _snapshot = snapshot;
        _anchor = position;
        _caret = position;
        _text = null;
    }

    /// <summary>
    /// Extend the caret while dragging. Extension is refused when the pane has already moved on to a
    /// different snapshot, so a stale index can never be applied to newer text.
    /// </summary>
    public bool Extend(TerminalSnapshot snapshot, TerminalCellPosition position)
    {
        if (!ReferenceEquals(_snapshot, snapshot)) return false;
        if (_caret == position) return true;
        _caret = position;
        _text = null;
        return true;
    }

    public void Clear()
    {
        _snapshot = null;
        _anchor = default;
        _caret = default;
        _text = null;
    }
}

/// <summary>Fixed-cell selection to text conversion. Pure; used by both the pane and the self tests.</summary>
public static class TerminalSelectionText
{
    private readonly record struct RowCell(int Column, int TextStart, int TextLength);

    /// <summary>
    /// Extract the selected text. Wide glyphs contribute their full code point once and the empty
    /// continuation cell is skipped, so a wide character is never split. The range is expanded to whole
    /// grapheme clusters (combining marks, variation selectors, emoji ZWJ sequences, regional-indicator
    /// flags) so an endpoint can never cut a cluster in half. Trailing padding is trimmed per line and
    /// trailing blank lines are dropped; interior blank lines are kept.
    /// </summary>
    public static string Extract(TerminalSnapshot snapshot, TerminalCellPosition first, TerminalCellPosition second)
    {
        var lines = snapshot.Lines;
        if (lines.Length == 0) return string.Empty;
        // A zero-length gesture is a click, not a selection: nothing to copy.
        if (first == second) return string.Empty;

        var (start, end) = Resolve(snapshot, first, second);

        var builder = new StringBuilder();
        for (var row = start.Row; row <= end.Row; row++)
        {
            var line = lines[row];
            var from = row == start.Row ? Math.Min(start.Column, line.Length) : 0;
            var to = row == end.Row ? Math.Min(end.Column, line.Length - 1) : line.Length - 1;
            if (row != start.Row) builder.Append('\n');
            builder.Append(RowText(line, from, to));
        }

        return TrimTrailingBlankLines(builder.ToString());
    }

    /// <summary>
    /// Clamp, then snap both endpoints to whole grapheme clusters using the row's real text elements,
    /// then map those boundaries back onto cells (wide-glyph owners keep their continuation cell).
    /// Cluster boundaries come from <see cref="StringInfo.ParseCombiningCharacters"/> over the row text,
    /// which implements the full UAX#29 grapheme rules (combining marks, ZWJ emoji sequences and
    /// paired regional indicators) instead of a hand-written approximation.
    /// Expansion is confined to one row, so a real line break stays a hard boundary.
    /// </summary>
    public static (TerminalCellPosition Start, TerminalCellPosition End) Resolve(TerminalSnapshot snapshot, TerminalCellPosition first, TerminalCellPosition second)
    {
        var lines = snapshot.Lines;
        var (start, end) = Order(first, second);
        start = Clamp(snapshot, start);
        end = Clamp(snapshot, end);

        var startLine = lines[start.Row];
        var endLine = lines[end.Row];
        start = start with { Column = ClusterFirstColumn(startLine, start.Column) };

        var endColumn = ClusterLastColumn(endLine, end.Column);
        // Keep a wide glyph whole inside the range: its empty continuation cell carries no text but
        // belongs to the same cell pair for highlighting.
        if (endColumn + 1 < endLine.Length && endLine[endColumn + 1].Text.Length == 0) endColumn++;
        end = end with { Column = Math.Min(endColumn, Math.Max(0, endLine.Length - 1)) };
        return (start, end);
    }

    /// <summary>First cell column of the grapheme cluster that owns <paramref name="column"/>.</summary>
    private static int ClusterFirstColumn(TerminalCell[] line, int column)
    {
        var cells = RowCells(line);
        var index = CellIndexAtOrBefore(cells, column);
        if (index < 0) return column;
        var boundaries = ClusterBoundaries(line, cells);
        if (boundaries.Length == 0) return column;
        var clusterStart = LastBoundaryAtOrBefore(boundaries, cells[index].TextStart);
        var owner = CellIndexAtTextIndex(cells, clusterStart);
        return owner < 0 ? column : cells[owner].Column;
    }

    /// <summary>Last cell column of the grapheme cluster that owns <paramref name="column"/>.</summary>
    private static int ClusterLastColumn(TerminalCell[] line, int column)
    {
        var cells = RowCells(line);
        var index = CellIndexAtOrBefore(cells, column);
        if (index < 0) return column;
        var boundaries = ClusterBoundaries(line, cells);
        if (boundaries.Length == 0) return column;
        var cell = cells[index];
        var clusterEnd = BoundaryAfter(boundaries, cell.TextStart + cell.TextLength - 1);
        var last = CellIndexAtTextIndex(cells, clusterEnd - 1);
        return last < 0 ? column : cells[last].Column;
    }

    private static List<RowCell> RowCells(TerminalCell[] line)
    {
        var cells = new List<RowCell>(line.Length);
        var textIndex = 0;
        for (var column = 0; column < line.Length; column++)
        {
            var text = line[column].Text;
            if (text.Length == 0) continue; // wide-glyph continuation cell carries no text of its own
            cells.Add(new RowCell(column, textIndex, text.Length));
            textIndex += text.Length;
        }
        return cells;
    }

    /// <summary>Whole row as text, skipping continuation cells; cluster boundaries are computed on this.</summary>
    private static int[] ClusterBoundaries(TerminalCell[] line, List<RowCell> cells)
    {
        if (cells.Count == 0) return Array.Empty<int>();
        var builder = new StringBuilder();
        foreach (var cell in cells) builder.Append(line[cell.Column].Text);
        var text = builder.ToString();
        return text.Length == 0 ? Array.Empty<int>() : StringInfo.ParseCombiningCharacters(text);
    }

    private static int LastBoundaryAtOrBefore(int[] boundaries, int textIndex)
    {
        var result = 0;
        foreach (var boundary in boundaries)
        {
            if (boundary > textIndex) break;
            result = boundary;
        }
        return result;
    }

    private static int BoundaryAfter(int[] boundaries, int textIndex)
    {
        foreach (var boundary in boundaries) if (boundary > textIndex) return boundary;
        return textIndex + 1;
    }

    /// <summary>Index of the cell that owns <paramref name="column"/> (a continuation cell resolves to its owner).</summary>
    private static int CellIndexAtOrBefore(List<RowCell> cells, int column)
    {
        for (var i = cells.Count - 1; i >= 0; i--) if (cells[i].Column <= column) return i;
        return -1;
    }

    private static int CellIndexAtTextIndex(List<RowCell> cells, int textIndex)
    {
        for (var i = 0; i < cells.Count; i++)
            if (cells[i].TextStart <= textIndex && textIndex < cells[i].TextStart + cells[i].TextLength) return i;
        return cells.Count - 1;
    }

    private static string RowText(TerminalCell[] line, int from, int to)
    {
        if (from > to) return string.Empty;
        var builder = new StringBuilder();
        for (var column = from; column <= to; column++)
        {
            var text = line[column].Text;
            if (text.Length > 0) builder.Append(text);
        }
        return builder.ToString().TrimEnd(' ');
    }

    private static (TerminalCellPosition Start, TerminalCellPosition End) Order(TerminalCellPosition a, TerminalCellPosition b) =>
        a.Row < b.Row || (a.Row == b.Row && a.Column <= b.Column) ? (a, b) : (b, a);

    private static TerminalCellPosition Clamp(TerminalSnapshot snapshot, TerminalCellPosition position)
    {
        var row = Math.Clamp(position.Row, 0, snapshot.Lines.Length - 1);
        var column = Math.Clamp(position.Column, 0, Math.Max(0, snapshot.Lines[row].Length - 1));
        return new TerminalCellPosition(row, column);
    }

    private static string TrimTrailingBlankLines(string text)
    {
        var end = text.Length;
        while (end > 0 && text[end - 1] == '\n') end--;
        return text[..end];
    }
}
