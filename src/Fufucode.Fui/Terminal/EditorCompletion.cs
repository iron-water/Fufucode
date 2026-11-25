using System.Buffers;
using System.Globalization;
using System.Text;

namespace Fufucode.Fui;

internal enum FuiCompletionKind
{
    Word,
    Emoji
}

internal readonly record struct CompletionToken(int Start, int End, string Query);

internal sealed class FuiCompletionPopup(
    FuiCompletionKind kind, string title, int start, int end, string query, IReadOnlyList<FuiChoice> items)
{
    private int _selected;

    internal FuiCompletionKind Kind { get; } = kind;
    internal int Start { get; } = start;
    internal int End { get; } = end;
    internal string Query { get; } = query;
    internal IReadOnlyList<FuiChoice> Items { get; } = items;
    internal string Title { get; } = title;
    internal FuiChoice? Current => Items.Count == 0 ? null : Items[Math.Clamp(_selected, 0, Items.Count - 1)];
    internal FuiOverlay View => new(Title, Query, Items, Math.Clamp(_selected, 0, Math.Max(0, Items.Count - 1)),
        Items.Count == 0 ? "没有本地候选" : null,
        "Enter / Tab 接受 · ↑↓ 选择 · Esc 关闭", FuiOverlayKind.Picker);

    internal void Move(int step)
    {
        if (Items.Count == 0) return;
        _selected = (int)Math.Clamp((long)_selected + step, 0, Items.Count - 1);
    }
}

internal static class FuiCompletionCatalog
{
    private const int MaxWordCandidates = 40;

    internal static bool TryWordToken(string text, int cursor, out CompletionToken token)
    {
        token = default;
        cursor = Math.Clamp(cursor, 0, text.Length);
        var starts = Boundaries(text);
        var index = BoundaryIndex(starts, cursor);
        var startIndex = index;
        while (startIndex > 0 && IsTokenGlyph(text.AsSpan(starts[startIndex - 1], starts[startIndex] - starts[startIndex - 1])))
            startIndex--;
        var endIndex = index;
        while (endIndex + 1 < starts.Length &&
               IsTokenGlyph(text.AsSpan(starts[endIndex], starts[endIndex + 1] - starts[endIndex])))
            endIndex++;

        var start = starts[startIndex];
        var end = starts[endIndex];
        var query = text[start..cursor];
        token = new(start, end, query);
        return true;
    }

    internal static IReadOnlyList<FuiChoice> WordChoices(CompletionToken token, IEnumerable<string> transcript,
        IEnumerable<string> history, IEnumerable<string> projectPaths)
    {
        var candidates = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        AddSource(transcript, "对话", 2);
        AddSource(history, "输入历史", 1);
        foreach (var path in projectPaths)
        {
            var normalized = path.Replace('\\', '/').Trim();
            if (normalized.Length == 0) continue;
            Add(normalized, "项目路径", 0);
        }

        var query = token.Query;
        return candidates.Values
            .Select(candidate => (Candidate: candidate, Match: Match(candidate.Value, query)))
            .Where(item => item.Match is not null && !string.Equals(item.Candidate.Value, query, StringComparison.Ordinal))
            .OrderBy(item => item.Match)
            .ThenBy(item => item.Candidate.Priority)
            .ThenBy(item => item.Candidate.Value.Length)
            .ThenByDescending(item => item.Candidate.Count)
            .ThenBy(item => item.Candidate.Value, StringComparer.OrdinalIgnoreCase)
            .Take(MaxWordCandidates)
            .Select(item => new FuiChoice(item.Candidate.Value, item.Candidate.Value, item.Candidate.Source))
            .ToArray();

        void AddSource(IEnumerable<string> lines, string source, int priority)
        {
            foreach (var line in lines)
                foreach (var word in Tokens(line))
                    Add(word, source, priority);
        }

        void Add(string value, string source, int priority)
        {
            value = TrimCandidate(value);
            if (value.Length < 2 || value.Any(char.IsControl)) return;
            if (candidates.TryGetValue(value, out var existing))
            {
                candidates[value] = existing with
                {
                    Count = existing.Count + 1,
                    Source = priority < existing.Priority ? source : existing.Source,
                    Priority = Math.Min(priority, existing.Priority)
                };
                return;
            }
            candidates[value] = new(value, source, priority, 1);
        }
    }

    internal static IReadOnlyList<FuiChoice> EmojiChoices(string query) =>
        EmojiShortcodes.Suggest(query)
            .Select(item => new FuiChoice(item.Glyph, $"{item.Glyph}  :{item.Name}:", "Emoji 短码"))
            .ToArray();

    private static int? Match(string candidate, string query)
    {
        if (query.Length == 0) return 0;
        if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 0;
        var slash = Math.Max(candidate.LastIndexOf('/'), candidate.LastIndexOf('\\'));
        if (slash >= 0 && candidate.AsSpan(slash + 1).StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (candidate.Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
        return null;
    }

    private static IEnumerable<string> Tokens(string text)
    {
        var starts = Boundaries(text);
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i + 1 < starts.Length; i++)
        {
            var glyph = text.AsSpan(starts[i], starts[i + 1] - starts[i]);
            if (IsTokenGlyph(glyph))
            {
                builder.Append(glyph);
            }
            else if (builder.Length != 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }
        if (builder.Length != 0) yield return builder.ToString();
    }

    private static bool IsTokenGlyph(ReadOnlySpan<char> glyph)
    {
        if (glyph.IsEmpty) return false;
        if (Rune.DecodeFromUtf16(glyph, out var rune, out _) != OperationStatus.Done) return false;
        if (Rune.IsLetterOrDigit(rune) || rune.Value is '_' or '@' or '/' or '\\' or '.' or '-') return true;
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }

    private static string TrimCandidate(string value) =>
        value.Trim().TrimStart('.', '/', '\\', '-').TrimEnd('.', '/', '\\', '-');

    private static int[] Boundaries(string text)
    {
        var starts = StringInfo.ParseCombiningCharacters(text);
        return [.. starts, text.Length];
    }

    private static int BoundaryIndex(int[] starts, int cursor)
    {
        var found = Array.BinarySearch(starts, cursor);
        return found >= 0 ? found : Math.Max(0, ~found - 1);
    }

    private sealed record Candidate(string Value, string Source, int Priority, int Count);
}
