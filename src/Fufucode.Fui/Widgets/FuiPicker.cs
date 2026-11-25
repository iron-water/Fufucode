namespace Fufucode.Fui;

/// <summary>A navigation-only selection model. Acceptance and cancellation belong to the controller.</summary>
internal sealed class FuiPicker
{
    private readonly record struct SearchItem(FuiChoice Choice, string Label, string Value, string Detail);
    private readonly string _title;
    private readonly FuiChoice[] _choices;
    private readonly SearchItem[] _index;
    private readonly Composer _query = new();
    private readonly FuiSessionTree? _tree;
    private IReadOnlyList<FuiChoice> _matches = [];
    private string? _filteredQuery;
    private int _selected;

    internal FuiPicker(string title, IReadOnlyList<FuiChoice> choices, string initialQuery = "", string? currentTreeSession = null)
    {
        _title = title;
        _choices = [.. choices];
        _index = new SearchItem[_choices.Length];
        for (var i = 0; i < _choices.Length; i++)
        {
            var c = _choices[i];
            _index[i] = new(c, TerminalText.SingleLine(c.Label), TerminalText.SingleLine(c.Value), TerminalText.SingleLine(c.Detail ?? ""));
        }
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
            return new(_title, _query.Text, _matches, _selected, _matches.Count != 0 ? null :
                _choices.Length == 0 ? "暂无选项" : "没有匹配项");
        }
    }

    public FuiChoice? Current
    {
        get
        {
            var view = View;
            return view.Items.Count == 0 ? null : view.Items[view.Selected];
        }
    }

    private bool BrowsingTree => _tree is not null && string.IsNullOrWhiteSpace(_query.Text);

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
            _selected = (int)Math.Clamp(_selected + step, 0, Math.Max(0, _matches.Count - 1));
            return;
        }
        if (key.Key is ConsoleKey.Enter or ConsoleKey.Escape || (key.Key == ConsoleKey.J && key.Modifiers.HasFlag(ConsoleModifiers.Control))) return;
        // Query is one line; embedded newlines in pasted text separate terms but cannot accept a choice.
        if (key.Key == 0 && key.KeyChar is '\n' or '\r' or '\t') key = new(' ', (ConsoleKey)0, false, false, false);
        _query.Edit(key);
    }

    private void Filter()
    {
        var query = _query.Text;
        if (query == _filteredQuery) return;
        _filteredQuery = query;
        _selected = 0;
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) { _matches = _choices; return; }

        // Constant number of stable buckets, no N log N sort. Batch-filter a pasted query once on the next View/Current.
        var buckets = new List<FuiChoice>?[5];
        foreach (var item in _index)
        {
            var rank = 0;
            foreach (var term in terms)
            {
                rank = Math.Max(rank, Rank(item, term));
                if (rank < buckets.Length) continue;
                break;
            }
            if (rank < buckets.Length) (buckets[rank] ??= []).Add(item.Choice);
        }
        var result = new List<FuiChoice>();
        foreach (var bucket in buckets) if (bucket is not null) result.AddRange(bucket);
        _matches = result;
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
