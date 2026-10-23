using System;
using System.Collections.Generic;
using System.Linq;
using Fufucode.Core;

namespace Fufucode.App.Infrastructure;

/// <summary>
/// 词级 diff：把一段等量替换的 +/- 行按词切开做一次 LCS，得到每行里真正变了的片段。
///
/// 只服务于界面强调。行数或词数超过上限时直接放弃（返回空表），
/// 整行都按改动处理——在超长行上做词级比对既慢又看不出收益。
/// </summary>
public static class WordDiff
{
    private const int MaximumPairs = 400;
    private const int MaximumTokens = 400;
    private const string Separators = " \t,;:(){}[]<>=!&|+-*/%\"'`";

    /// <summary>
    /// 返回 行号 → 强调区间。键是 <paramref name="removed"/> 与 <paramref name="added"/> 中行的原始下标。
    /// </summary>
    public static Dictionary<int, (int Start, int Length)>? Pair(DiffHunk hunk,
        IReadOnlyList<int> removed, IReadOnlyList<int> added)
    {
        if (removed.Count != added.Count || removed.Count == 0 || removed.Count > MaximumPairs) return null;
        var result = new Dictionary<int, (int, int)>(removed.Count * 2);
        for (var i = 0; i < removed.Count; i++)
        {
            var oldText = hunk.Lines[removed[i]].Text;
            var newText = hunk.Lines[added[i]].Text;
            if (!Emphasis(oldText, newText, out var oldRange, out var newRange)) continue;
            if (oldRange.Length > 0) result[removed[i]] = oldRange;
            if (newRange.Length > 0) result[added[i]] = newRange;
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>把两行拆成词与分隔符交替的片段，比对后各自返回首个到末个变化片段的区间。</summary>
    internal static bool Emphasis(string oldText, string newText, out (int Start, int Length) oldRange,
        out (int Start, int Length) newRange)
    {
        oldRange = default;
        newRange = default;
        if (oldText.Length == 0 || newText.Length == 0) return false;
        if (oldText.Length + newText.Length > 4000) return false;

        var oldTokens = Tokenize(oldText);
        var newTokens = Tokenize(newText);
        if (oldTokens.Count > MaximumTokens || newTokens.Count > MaximumTokens) return false;

        // 完全相同的行没有强调价值（等量替换里偶尔会出现）。
        if (oldText == newText) return false;

        var table = new int[oldTokens.Count + 1, newTokens.Count + 1];
        for (var i = oldTokens.Count - 1; i >= 0; i--)
            for (var j = newTokens.Count - 1; j >= 0; j--)
                table[i, j] = oldTokens[i].Text == newTokens[j].Text
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);

        int firstOld = -1, lastOld = -1, firstNew = -1, lastNew = -1;
        int x = 0, y = 0;
        // 只取第一处连续差异。LCS 会把一次替换拆成"删一个 + 加一个"，两块紧挨着就算同一处；
        // 中间夹了相同片段才说明是两处改动，后面的不再强调。
        // 删除优先与最近的未配对新增成对，这样左右两侧的强调都落在真正改变的那段词上。
        var block = new List<(int Old, int New)>();
        var captured = false;

        void Mark(int oldIndex, int newIndex)
        {
            if (oldIndex >= 0) block.Add((oldIndex, -1));
            else
            {
                // 加到第一个还没有新增的删除上，形成 删/加 配对，而不是在尾部追加。
                var at = block.FindIndex(entry => entry.Old >= 0 && entry.New < 0);
                if (at < 0) block.Add((-1, newIndex));
                else block[at] = (block[at].Old, newIndex);
            }
        }

        void Flush()
        {
            if (block.Count == 0) return;
            if (!captured)
            {
                firstOld = block.Where(e => e.Old >= 0).Select(e => e.Old).DefaultIfEmpty(-1).First();
                lastOld = block.Where(e => e.Old >= 0).Select(e => e.Old).DefaultIfEmpty(-1).Last();
                firstNew = block.Where(e => e.New >= 0).Select(e => e.New).DefaultIfEmpty(-1).First();
                lastNew = block.Where(e => e.New >= 0).Select(e => e.New).DefaultIfEmpty(-1).Last();
                captured = true;
            }
            block.Clear();
        }

        while (x < oldTokens.Count && y < newTokens.Count)
        {
            if (oldTokens[x].Text == newTokens[y].Text) { Flush(); x++; y++; }
            else if (table[x + 1, y] > table[x, y + 1]) { Mark(x++, -1); }
            else { Mark(-1, y++); }
        }
        while (x < oldTokens.Count) Mark(x++, -1);
        while (y < newTokens.Count) Mark(-1, y++);
        Flush();

        if (firstOld < 0 && firstNew < 0) return false;
        if (firstOld >= 0) oldRange = Range(oldTokens, firstOld, lastOld);
        if (firstNew >= 0) newRange = Range(newTokens, firstNew, lastNew);
        return oldRange.Length > 0 || newRange.Length > 0;
    }

    private static (int Start, int Length) Range(List<Token> tokens, int from, int to)
    {
        var start = tokens[from].Start;
        var end = tokens[to].Start + tokens[to].Text.Length;
        return (start, end - start);
    }

    internal readonly record struct Token(string Text, int Start);

    /// <summary>按"词 / 空白+标点"交替切分，保证拼接回去与原文逐字相同。</summary>
    internal static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>(text.Length / 2 + 1);
        var index = 0;
        while (index < text.Length)
        {
            var start = index;
            var separator = IsSeparator(text[index]);
            while (index < text.Length && IsSeparator(text[index]) == separator) index++;
            tokens.Add(new Token(text[start..index], start));
        }
        return tokens;
    }

    private static bool IsSeparator(char ch) => char.IsWhiteSpace(ch) || Separators.Contains(ch);
}
