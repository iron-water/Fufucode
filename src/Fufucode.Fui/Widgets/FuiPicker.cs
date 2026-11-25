namespace Fufucode.Fui;

internal sealed record FuiPickerOptions(
    Func<FuiChoice, string?>? Detail = null,
    Func<FuiChoice, bool>? GroupHeader = null,
    bool PreserveOrder = false);

/// <summary>A navigation-only selection model. Acceptance and cancellation belong to the controller.</summary>
internal sealed class FuiPicker
{
    private readonly record struct SearchItem(FuiChoice Choice, string Label, string Value, string Detail);
    private readonly string _title;
    private readonly FuiPickerOptions _options;
    private readonly Composer _query = new();
    private readonly FuiSessionTree? _tree;
    private FuiChoice[] _choices;
    private SearchItem[] _index;
    private IReadOnlyList<FuiChoice> _matches = [];
    private string? _filteredQuery;
    private int _selected;

    internal FuiPicker(string title, IReadOnlyList<FuiChoice> choices, string initialQuery = "",
        string? currentTreeSession = null, FuiPickerOptions? options = null)
    {
        _title = title;
        _options = options ?? new();
        _choices = [.. choices];
        _index = Index(_choices);
        _query.Replace(TerminalText.SingleLine(initialQuery));
        if (currentTreeSession is not null) _tree = new(_choices, currentTreeSession);
    }

    public FuiOverlay View
    {
        get
        {
            if (BrowsingTree)
                return new(_title, _query.Text, _tree!.Rows, _tree.Selected, _tree.Rows.Count == 0 ? "暂无选项" : null);
            Filter();
            NormalizeSelection(_selected);
            var detail = _matches.Count == 0 || IsGroupHeader(_matches[_selected])
                ? null
                : _options.Detail?.Invoke(_matches[_selected]);
            return new(_title, _query.Text, _matches, _selected,
                _matches.Count != 0 ? detail : _choices.Length == 0 ? "暂无选项" : "没有匹配项");
        }
    }

    public FuiChoice? Current
    {
        get
        {
            var view = View;
            return view.Items.Count == 0 || IsGroupHeader(view.Items[view.Selected])
                ? null
                : view.Items[view.Selected];
        }
    }

    private bool BrowsingTree => _tree is not null && string.IsNullOrWhiteSpace(_query.Text);

    internal void ReplaceChoices(IReadOnlyList<FuiChoice> choices)
    {
        var selected = Current?.Value;
        _choices = [.. choices];
        _index = Index(_choices);
        _filteredQuery = null;
        Filter(selected);
    }

    public void Edit(ConsoleKeyInfo key, int pageSize)
    {
        long step = key.Key switch
        {
            ConsoleKey.UpArrow => -1, ConsoleKey.DownArrow => 1,
            ConsoleKey.PageUp => -Math.Max(1, pageSize), ConsoleKey.PageDown => Math.Max(1, pageSize),
            ConsoleKey.Home => -int.MaxValue, ConsoleKey.End => int.MaxValue, _ => 0
        };
        if (BrowsingTree)
        {
            if (step != 0) { _tree!.Move(step); return; }
            if (key.Modifiers == 0 && key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow)
            {
                _tree!.Traverse(key.Key == ConsoleKey.RightArrow);
                return;
            }
        }
        if (step != 0)
        {
            Filter();
            MoveSelection(step);
            return;
        }
        if (key.Key is ConsoleKey.Enter or ConsoleKey.Escape || (key.Key == ConsoleKey.J && key.Modifiers.HasFlag(ConsoleModifiers.Control))) return;
        // Query is one line; embedded newlines in pasted text separate terms but cannot accept a choice.
        if (key.Key == 0 && key.KeyChar is '\n' or '\r' or '\t') key = new(' ', (ConsoleKey)0, false, false, false);
        _query.Edit(key);
    }

    private void Filter(string? preferredValue = null)
    {
        var query = _query.Text;
        if (query == _filteredQuery) return;
        _filteredQuery = query;
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
        {
            _matches = _choices;
            NormalizeSelection(0, preferredValue);
            return;
        }

        // Generic pickers keep their five stable relevance buckets. Grouped rosters preserve
        // their source order so running jobs and their agent groups do not jump while filtering.
        var buckets = _options.PreserveOrder ? null : new List<FuiChoice>?[5];
        var ordered = _options.PreserveOrder ? new List<FuiChoice>() : null;
        foreach (var item in _index)
        {
            if (IsGroupHeader(item.Choice)) continue;
            var rank = 0;
            foreach (var term in terms)
            {
                rank = Math.Max(rank, Rank(item, term));
                if (rank < 5) continue;
                break;
            }
            if (rank >= 5) continue;
            if (ordered is not null) ordered.Add(item.Choice);
            else (buckets![rank] ??= []).Add(item.Choice);
        }

        if (ordered is not null)
        {
            _matches = ordered;
        }
        else
        {
            var result = new List<FuiChoice>();
            foreach (var bucket in buckets!) if (bucket is not null) result.AddRange(bucket);
            _matches = result;
        }
        NormalizeSelection(0, preferredValue);
    }

    private void MoveSelection(long step)
    {
        if (_matches.Count == 0) return;
        if (step == int.MaxValue)
        {
            var last = LastSelectable();
            if (last >= 0) _selected = last;
            return;
        }
        if (step == -int.MaxValue)
        {
            var first = FirstSelectable();
            if (first >= 0) _selected = first;
            return;
        }

        var direction = step < 0 ? -1 : 1;
        for (var remaining = Math.Abs(step); remaining > 0; remaining--)
        {
            var next = FindSelectable(_selected, direction);
            if (next < 0) break;
            _selected = next;
        }
    }

    private void NormalizeSelection(int preferred, string? preferredValue = null)
    {
        if (_matches.Count == 0)
        {
            _selected = 0;
            return;
        }
        if (preferredValue is not null)
        {
            for (var i = 0; i < _matches.Count; i++)
            {
                if (!IsSelectable(_matches[i]) || !string.Equals(_matches[i].Value, preferredValue, StringComparison.Ordinal))
                    continue;
                _selected = i;
                return;
            }
        }
        var index = Math.Clamp(preferred, 0, _matches.Count - 1);
        if (IsSelectable(_matches[index]))
        {
            _selected = index;
            return;
        }
        var forward = FindSelectable(index, 1);
        if (forward >= 0)
        {
            _selected = forward;
            return;
        }
        var backward = FindSelectable(index, -1);
        _selected = backward >= 0 ? backward : index;
    }

    private int FirstSelectable() => FindSelectable(-1, 1);

    private int LastSelectable() => FindSelectable(_matches.Count, -1);

    private int FindSelectable(int from, int direction)
    {
        for (var i = from + direction; i >= 0 && i < _matches.Count; i += direction)
            if (IsSelectable(_matches[i])) return i;
        return -1;
    }

    private bool IsSelectable(FuiChoice choice) => !IsGroupHeader(choice);

    private bool IsGroupHeader(FuiChoice choice) => _options.GroupHeader?.Invoke(choice) == true;

    private static SearchItem[] Index(IReadOnlyList<FuiChoice> choices)
    {
        var index = new SearchItem[choices.Count];
        for (var i = 0; i < choices.Count; i++)
        {
            var choice = choices[i];
            index[i] = new(choice, TerminalText.SingleLine(choice.Label),
                TerminalText.SingleLine(choice.Value), TerminalText.SingleLine(choice.Detail ?? ""));
        }
        return index;
    }

    private static int Rank(SearchItem item, string term)
    {
        const StringComparison cmp = StringComparison.OrdinalIgnoreCase;
        if (item.Label.Equals(term, cmp) || item.Value.Equals(term, cmp)) return 0;
        if (item.Label.StartsWith(term, cmp) || item.Value.StartsWith(term, cmp)) return 1;
        if (item.Label.Contains(term, cmp) || item.Value.Contains(term, cmp)) return 2;
        if (item.Detail.Contains(term, cmp)) return 3;
        // Abbreviations like "ncs" can select NativeChatSession; non-abbreviations stay literal.
        if (term.Length >= 2 && IsAbbreviation(term) && (IsSubsequence(term, item.Label) || IsSubsequence(term, item.Value))) return 4;
        return 5;
    }

    private static bool IsAbbreviation(ReadOnlySpan<char> term)
    {
        foreach (var c in term) if (c is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z')) return false;
        return true;
    }

    private static bool IsSubsequence(ReadOnlySpan<char> term, ReadOnlySpan<char> text)
    {
        foreach (var c in term)
        {
            var index = text.IndexOf(new ReadOnlySpan<char>(in c), StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            text = text[(index + 1)..];
        }
        return true;
    }
}
