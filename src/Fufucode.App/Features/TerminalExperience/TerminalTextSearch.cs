using System;
using System.Collections.Generic;
using System.Text;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.Features.TerminalExperience;

/// <summary>One hit inside the bounded snapshot; cells from <see cref="StartColumn"/> to <see cref="EndColumn"/> inclusive.</summary>
public readonly record struct TerminalSearchMatch(int Row, int StartColumn, int EndColumn);

/// <summary>
/// Per-tab output search over the bounded <see cref="TerminalSnapshot"/>.
/// It only ever scans a snapshot handed to it on demand (never on every render) and only stores the
/// list of hits, so no full copy of the output is retained. Results remember which snapshot they came
/// from: navigation re-runs the query against the live snapshot before moving, so a stale match index
/// is never applied to newer text.
/// </summary>
public sealed class TerminalSearchSession
{
    private readonly List<TerminalSearchMatch> _matches = new();
    private TerminalSnapshot? _snapshot;
    private int _index = -1;

    public string Query { get; private set; } = string.Empty;
    public bool MatchCase { get; private set; }
    public bool HasQuery => Query.Length > 0;
    public IReadOnlyList<TerminalSearchMatch> Matches => _matches;
    public int Count => _matches.Count;
    public int CurrentIndex => _index;

    public TerminalSearchMatch? Current => _index >= 0 && _index < _matches.Count ? _matches[_index] : null;

    /// <summary>True when the stored hits still belong to the supplied snapshot (highlight is faithful).</summary>
    public bool IsCurrent(TerminalSnapshot? snapshot) => snapshot is not null && ReferenceEquals(_snapshot, snapshot);

    /// <summary>Run a fresh search against the supplied bounded snapshot. <paramref name="fromEnd"/> starts at the last hit.</summary>
    public int Run(TerminalSnapshot snapshot, string query, bool matchCase, bool fromEnd = false)
    {
        _snapshot = snapshot;
        Query = query ?? string.Empty;
        MatchCase = matchCase;
        _matches.Clear();
        _index = -1;
        if (Query.Length > 0) Collect(snapshot, Query, matchCase);
        if (_matches.Count > 0) _index = fromEnd ? _matches.Count - 1 : 0;
        return _matches.Count;
    }

    /// <summary>
    /// Re-run the query when the snapshot changed since the last search, keeping the view anchored near
    /// the previous hit. Returns the match count after the refresh.
    /// </summary>
    public int EnsureCurrent(TerminalSnapshot snapshot)
    {
        if (ReferenceEquals(_snapshot, snapshot)) return _matches.Count;
        if (Query.Length == 0) { Run(snapshot, Query, MatchCase); return 0; }
        var previousRow = Current?.Row;
        Run(snapshot, Query, MatchCase);
        if (previousRow is { } row && _matches.Count > 0)
        {
            var nearest = _matches.FindIndex(match => match.Row >= row);
            if (nearest >= 0) _index = nearest;
        }
        return _matches.Count;
    }

    /// <summary>Advance (direction &gt; 0) or rewind (direction &lt; 0) one hit, wrapping.</summary>
    public TerminalSearchMatch? Move(int direction)
    {
        if (_matches.Count == 0) return null;
        _index = _index < 0 ? 0 : (_index + direction + _matches.Count) % _matches.Count;
        return Current;
    }

    public void Clear()
    {
        _snapshot = null;
        _matches.Clear();
        _index = -1;
        Query = string.Empty;
        MatchCase = false;
    }

    /// <summary>Short hit counter for the find bar, or the explicit "no match" feedback.</summary>
    public string StatusText() =>
        Query.Length == 0 ? string.Empty : _matches.Count == 0 ? "无匹配" : $"{_index + 1}/{_matches.Count}";

    private void Collect(TerminalSnapshot snapshot, string query, bool matchCase)
    {
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var row = 0; row < snapshot.Lines.Length; row++)
        {
            var line = snapshot.Lines[row];
            var text = new StringBuilder();
            var columnOfChar = new List<int>();
            for (var column = 0; column < line.Length; column++)
            {
                var cell = line[column].Text;
                if (cell.Length == 0) continue; // wide-glyph continuation carries no text
                text.Append(cell);
                for (var i = 0; i < cell.Length; i++) columnOfChar.Add(column);
            }
            if (text.Length == 0) continue;
            var lineText = text.ToString();
            for (var start = 0; start + query.Length <= lineText.Length; )
            {
                var hit = lineText.IndexOf(query, start, comparison);
                if (hit < 0) break;
                _matches.Add(new TerminalSearchMatch(row, columnOfChar[hit], columnOfChar[hit + query.Length - 1]));
                start = hit + query.Length;
            }
        }
    }
}
