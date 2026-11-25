using System.Globalization;
using System.Text;

namespace Fufucode.Fui;

internal sealed record FuiPickerOptions(
    Func<FuiChoice, string?>? Detail = null,
    Func<FuiChoice, bool>? GroupHeader = null,
    bool PreserveOrder = false,
    string? Footer = null,
    FuiOverlayKind Kind = FuiOverlayKind.Picker,
    string? EmptyMessage = null);

/// <summary>A navigation-only selection model. Acceptance and cancellation belong to the controller.</summary>
internal sealed class FuiPicker
{
    private const int Exact = 0;
    private const int DetailExact = 4;
    private const int Initials = 8;
    private const int DetailInitials = 9;
    private const int Subsequence = 10;
    private const int DetailSubsequence = 11;
    private const int BodyExact = 12;
    private const int BodyInitials = 13;
    private const int BodySubsequence = 14;
    private const int CategoryCount = BodySubsequence + 1;
    private const int BreakBucketCount = 8;
    private const int SpreadBucketCount = 4;
    private const int LinearStartBuckets = 32;
    private const int StartBucketCount = 64;
    private const int BucketCount = CategoryCount * BreakBucketCount * SpreadBucketCount * StartBucketCount;

    private readonly record struct SearchItem(FuiChoice Choice, SearchField Label, SearchField Value, SearchField Detail,
        SearchField SearchText);
    private readonly record struct QueryTerm(string[] Graphemes, bool CanAbbreviate);
    private readonly record struct TokenMatch(int Category, int Start, int Breaks);
    private readonly string _title;
    private readonly FuiPickerOptions _options;
    private readonly Composer _query = new();
    private readonly FuiSessionTree? _tree;
    private FuiChoice[] _choices;
    private SearchItem[] _index;
    private readonly List<FuiChoice>?[] _buckets = new List<FuiChoice>?[BucketCount];
    private readonly List<int> _usedBuckets = [];
    private IReadOnlyList<FuiChoice> _matches = [];
    private int[] _lastMatchingIndices = [];
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
                _matches.Count != 0 ? detail : _choices.Length == 0 ? _options.EmptyMessage ?? "暂无选项" : "没有匹配项",
                _options.Footer, _options.Kind);
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
        var previousQuery = _filteredQuery;
        _filteredQuery = query;
        _selected = 0;
        var terms = PrepareTerms(query);
        if (terms.Length == 0)
        {
            _matches = _choices;
            _lastMatchingIndices = [];
            NormalizeSelection(0, preferredValue);
            return;
        }

        foreach (var bucket in _usedBuckets) _buckets[bucket]!.Clear();
        _usedBuckets.Clear();

        // Appending an ASCII suffix to the final token can only remove candidates. Re-rank the
        // preceding matches instead of rescanning a stable catalog of tens of thousands of items.
        List<int>? matchedIndices = null;
        var ordered = _options.PreserveOrder ? new List<FuiChoice>() : null;
        if (CanNarrow(previousQuery, query) && _lastMatchingIndices.Length != 0)
        {
            foreach (var index in _lastMatchingIndices) AddCandidate(index);
        }
        else
        {
            for (var index = 0; index < _index.Length; index++) AddCandidate(index);
        }

        var result = ordered ?? new List<FuiChoice>(matchedIndices?.Count ?? 0);
        for (var i = 0; i < _buckets.Length; i++)
        {
            if (_buckets[i] is { Count: > 0 } bucket) result.AddRange(bucket);
        }
        _matches = result;
        _lastMatchingIndices = matchedIndices?.ToArray() ?? [];
        NormalizeSelection(0, preferredValue);

        void AddCandidate(int index)
        {
            if (IsGroupHeader(_index[index].Choice)) return;
            var bucketIndex = Rank(_index[index], terms);
            if (bucketIndex < 0) return;
            (matchedIndices ??= []).Add(index);
            if (ordered is not null)
            {
                ordered.Add(_index[index].Choice);
                return;
            }
            var bucket = _buckets[bucketIndex];
            if (bucket is null)
            {
                bucket = [];
                _buckets[bucketIndex] = bucket;
            }
            if (bucket.Count == 0) _usedBuckets.Add(bucketIndex);
            bucket.Add(_index[index].Choice);
        }
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
            index[i] = new(choice, new(TerminalText.SingleLine(choice.Label)),
                new(TerminalText.SingleLine(choice.Value)), new(TerminalText.SingleLine(choice.Detail ?? "")),
                new(TerminalText.SingleLine(choice.SearchText ?? "")));
        }
        return index;
    }

    private static bool CanNarrow(string? previous, string query)
    {
        if (previous is not { Length: > 0 } || query.Length <= previous.Length ||
            !query.StartsWith(previous, StringComparison.Ordinal) || !IsAsciiWord(previous[^1])) return false;
        for (var i = previous.Length; i < query.Length; i++)
            if (!IsAsciiWord(query[i])) return false;
        return true;
    }

    private static bool IsAsciiWord(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static QueryTerm[] PrepareTerms(string query)
    {
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var terms = new QueryTerm[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            var graphemes = ToGraphemes(tokens[i]);
            var abbreviate = graphemes.Length is >= 2 and <= 16;
            if (abbreviate)
            {
                foreach (var grapheme in graphemes)
                {
                    var rune = FirstRune(grapheme);
                    if (rune.Value > 0x7f || !Rune.IsLetterOrDigit(rune))
                    {
                        abbreviate = false;
                        break;
                    }
                }
            }
            terms[i] = new(graphemes, abbreviate);
        }
        return terms;
    }

    private static int Rank(SearchItem item, QueryTerm[] terms)
    {
        var maxCategory = 0;
        var sumCategories = 0;
        var maxBreaks = 0;
        var maxStart = 0;
        foreach (var term in terms)
        {
            var match = MatchTerm(item, term);
            if (match is not { } value) return -1;
            maxCategory = Math.Max(maxCategory, value.Category);
            sumCategories += value.Category;
            maxBreaks = Math.Max(maxBreaks, value.Breaks);
            maxStart = Math.Max(maxStart, value.Start);
        }

        var spread = Math.Min(SpreadBucketCount - 1, sumCategories - maxCategory);
        var startBucket = MatchStartBucket(maxStart);
        var breakBucket = Math.Min(BreakBucketCount - 1, maxBreaks);
        return ((maxCategory * BreakBucketCount + breakBucket) * SpreadBucketCount + spread) *
            StartBucketCount + startBucket;
    }

    private static int MatchStartBucket(int start)
    {
        if (start < LinearStartBuckets) return start;
        var bucket = LinearStartBuckets;
        var distance = start - LinearStartBuckets + 1;
        while (distance > 1 && bucket < StartBucketCount - 1)
        {
            distance = (distance + 1) / 2;
            bucket++;
        }
        return bucket;
    }

    private static TokenMatch? MatchTerm(SearchItem item, QueryTerm term)
    {
        TokenMatch? best = null;
        Consider(Direct(item.Label, term, Exact));
        Consider(Direct(item.Value, term, Exact));
        Consider(Direct(item.Detail, term, DetailExact));
        Consider(Direct(item.SearchText, term, BodyExact));
        if (best is not null) return best;

        Consider(InitialsMatch(item.Label, term), Initials);
        Consider(InitialsMatch(item.Value, term), Initials);
        Consider(InitialsMatch(item.Detail, term), DetailInitials);
        Consider(InitialsMatch(item.SearchText, term), BodyInitials);
        if (best is not null) return best;

        Consider(SubsequenceMatch(item.Label, term), Subsequence);
        Consider(SubsequenceMatch(item.Value, term), Subsequence);
        Consider(SubsequenceMatch(item.Detail, term), DetailSubsequence);
        Consider(SubsequenceMatch(item.SearchText, term), BodySubsequence);
        return best;

        void Consider(TokenMatch? candidate, int? category = null)
        {
            if (candidate is not { } value) return;
            if (category is { } fixedCategory)
                value = new(fixedCategory, value.Start, value.Breaks);
            if (best is not { } current || Better(value, current)) best = value;
        }

        static bool Better(TokenMatch candidate, TokenMatch current) =>
            candidate.Category != current.Category ? candidate.Category < current.Category :
            candidate.Breaks != current.Breaks ? candidate.Breaks < current.Breaks :
            candidate.Start < current.Start;
    }

    private static TokenMatch? Direct(SearchField field, QueryTerm term, int categoryBase)
    {
        var query = term.Graphemes;
        var text = field.Graphemes;
        if (query.Length == 0 || query.Length > text.Length) return null;
        if (MatchesAt(text, query, 0))
            return new(query.Length == text.Length ? categoryBase : categoryBase + 1, 0, 0);

        var boundary = Find(text, query, field.Boundaries, 1);
        if (boundary >= 0) return new(categoryBase + 2, boundary, 0);
        var contains = Find(text, query, null, 0);
        return contains < 0 ? null : new(categoryBase + 3, contains, 0);
    }

    private static TokenMatch? InitialsMatch(SearchField field, QueryTerm term)
    {
        if (!term.CanAbbreviate || term.Graphemes.Length > field.BoundaryIndices.Length) return null;
        TokenMatch? best = null;
        foreach (var firstBoundary in field.BoundaryIndices)
        {
            if (!Same(field.Graphemes[firstBoundary], term.Graphemes[0])) continue;
            var boundaryOrdinal = Array.BinarySearch(field.BoundaryIndices, firstBoundary);
            var nextOrdinal = boundaryOrdinal + 1;
            var breaks = 0;
            var complete = true;
            for (var queryIndex = 1; queryIndex < term.Graphemes.Length; queryIndex++)
            {
                while (nextOrdinal < field.BoundaryIndices.Length &&
                    !Same(field.Graphemes[field.BoundaryIndices[nextOrdinal]], term.Graphemes[queryIndex]))
                    nextOrdinal++;
                if (nextOrdinal >= field.BoundaryIndices.Length) { complete = false; break; }
                breaks += nextOrdinal - boundaryOrdinal - 1;
                boundaryOrdinal = nextOrdinal;
                nextOrdinal++;
            }
            if (!complete) continue;
            var candidate = new TokenMatch(0, firstBoundary, breaks);
            if (best is not { } current || candidate.Breaks < current.Breaks ||
                candidate.Breaks == current.Breaks && candidate.Start < current.Start) best = candidate;
        }
        return best;
    }

    private static TokenMatch? SubsequenceMatch(SearchField field, QueryTerm term)
    {
        if (!term.CanAbbreviate || term.Graphemes.Length > field.Graphemes.Length) return null;
        TokenMatch? best = null;
        for (var start = 0; start < field.Graphemes.Length; start++)
        {
            if (!Same(field.Graphemes[start], term.Graphemes[0])) continue;
            var previous = start;
            var breaks = 0;
            var complete = true;
            for (var queryIndex = 1; queryIndex < term.Graphemes.Length; queryIndex++)
            {
                var next = previous + 1;
                while (next < field.Graphemes.Length && !Same(field.Graphemes[next], term.Graphemes[queryIndex])) next++;
                if (next >= field.Graphemes.Length) { complete = false; break; }
                breaks += next - previous - 1;
                previous = next;
            }
            if (!complete) continue;
            var candidate = new TokenMatch(0, start, breaks);
            if (best is not { } current || candidate.Breaks < current.Breaks ||
                candidate.Breaks == current.Breaks && candidate.Start < current.Start) best = candidate;
        }
        return best;
    }

    private static int Find(string[] text, string[] query, bool[]? boundaries, int minimumStart)
    {
        for (var start = minimumStart; start + query.Length <= text.Length; start++)
        {
            if (boundaries is not null && !boundaries[start]) continue;
            if (MatchesAt(text, query, start)) return start;
        }
        return -1;
    }

    private static bool MatchesAt(string[] text, string[] query, int start)
    {
        for (var i = 0; i < query.Length; i++)
            if (!Same(text[start + i], query[i])) return false;
        return true;
    }

    private static bool Same(string left, string right) => StringComparer.OrdinalIgnoreCase.Equals(left, right);

    private static string[] ToGraphemes(string text)
    {
        var starts = StringInfo.ParseCombiningCharacters(text);
        var result = new string[starts.Length];
        for (var i = 0; i < starts.Length; i++)
        {
            var end = i + 1 < starts.Length ? starts[i + 1] : text.Length;
            result[i] = text[starts[i]..end].Normalize(NormalizationForm.FormC);
        }
        return result;
    }

    private static Rune FirstRune(string grapheme)
    {
        foreach (var rune in grapheme.EnumerateRunes()) return rune;
        return default;
    }

    private sealed class SearchField
    {
        public SearchField(string text)
        {
            Graphemes = ToGraphemes(text);
            var boundaries = new List<int>();
            for (var i = 0; i < Graphemes.Length; i++)
                if (IsBoundary(Graphemes, i)) boundaries.Add(i);
            BoundaryIndices = boundaries.ToArray();
            Boundaries = new bool[Graphemes.Length];
            foreach (var index in BoundaryIndices) Boundaries[index] = true;
        }

        public string[] Graphemes { get; }
        public bool[] Boundaries { get; }
        public int[] BoundaryIndices { get; }

        private static bool IsBoundary(string[] graphemes, int index)
        {
            if (index == 0) return true;
            var previous = FirstRune(graphemes[index - 1]);
            var current = FirstRune(graphemes[index]);
            if (!Rune.IsLetterOrDigit(current)) return false;
            if (!Rune.IsLetterOrDigit(previous)) return true;
            if (Rune.IsUpper(current) && (Rune.IsLower(previous) || Rune.IsDigit(previous))) return true;
            if (Rune.IsUpper(previous) && Rune.IsUpper(current) &&
                index + 1 < graphemes.Length && Rune.IsLower(FirstRune(graphemes[index + 1]))) return true;
            return Rune.IsDigit(previous) != Rune.IsDigit(current);
        }
    }
}
