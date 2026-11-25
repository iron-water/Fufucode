using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Fufucode.Core.Backend;

/// <summary>A single search, shared by indexed, explicit-file and uncached-directory scans.</summary>
internal sealed class GrepSearchPage
{
    private const long FileLimit = 5 * 1024 * 1024;
    private const int ExcerptChars = 512;
    private readonly string _pattern;
    private readonly StringComparison _comparison;
    private readonly Regex? _regex;
    private readonly int _maxMatches, _context, _offset;
    private readonly List<Group> _groups = [];
    private int _matches, _skippedGroups, _skippedLargeFiles;
    private long _skippedBytes;
    public bool Done { get; private set; }

    private sealed class Group(string file)
    {
        public string File { get; } = file;
        public int Matches;
        public readonly StringBuilder Body = new();
        public void Line(int number, bool match, string text) =>
            Body.Append(number).Append(match ? ": " : "- ").AppendLine(text);
    }

    public GrepSearchPage(string pattern, bool caseSensitive, int maxMatches, int context, int offset)
    {
        _pattern = pattern;
        _comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (pattern.AsSpan().IndexOfAny(@"\.^$|?*+()[]{}") >= 0)
            _regex = new Regex(pattern, RegexOptions.Compiled | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromSeconds(5));
        (_maxMatches, _context, _offset) = (maxMatches, context, offset);
    }

    // Keep only one window of matches, even for a very large file offset. Windows end on
    // file boundaries; ranking within a window is stable when it is revisited for pagination.
    public void Scan(string path, string relative, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_matches == _maxMatches)
        {
            if (_offset - _skippedGroups < _groups.Count) { Done = true; return; }
            _skippedGroups += _groups.Count;
            _matches = 0;
            _groups.Clear();
        }
        try
        {
            var size = new FileInfo(path).Length;
            if (size > FileLimit) { _skippedLargeFiles++; _skippedBytes += size; return; }

            Group? group = null;
            Queue<(int Number, string Text)>? before = null;
            int number = 0, after = 0;
            foreach (var line in File.ReadLines(path))
            {
                ct.ThrowIfCancellationRequested();
                number++;
                if (_matches < _maxMatches && Find(line, out var index, out var length))
                {
                    if (group is null) _groups.Add(group = new(relative));
                    while (before is not null && before.TryDequeue(out var row)) group.Line(row.Number, false, row.Text);
                    group.Line(number, true, MatchExcerpt(line, index, length));
                    group.Matches++;
                    _matches++;
                    after = _context;
                }
                else if (after > 0)
                {
                    group!.Line(number, false, ContextExcerpt(line));
                    after--;
                }
                else if (_context > 0)
                {
                    before ??= new(_context);
                    if (before.Count == _context) before.Dequeue();
                    before.Enqueue((number, ContextExcerpt(line)));
                }
                // Drain only the last hit's trailing context, not the rest of the file.
                if (_matches == _maxMatches && after == 0) break;
            }
        }
        catch (IOException) { } // file removed/locked during enumeration
        catch (UnauthorizedAccessException) { }
    }

    private bool Find(string line, out int index, out int length)
    {
        if (_regex is null)
        {
            index = line.IndexOf(_pattern, _comparison);
            length = _pattern.Length;
            return index >= 0;
        }
        var matches = _regex.EnumerateMatches(line);
        if (matches.MoveNext()) { (index, length) = (matches.Current.Index, matches.Current.Length); return true; }
        index = length = 0;
        return false;
    }

    private static string ContextExcerpt(string text) => text.Length <= ExcerptChars ? text : Slice(text, 0, ExcerptChars) + "…";

    private static string MatchExcerpt(string text, int index, int length)
    {
        if (text.Length <= ExcerptChars) return text;
        if (length > ExcerptChars)
            return Slice(text, index, ExcerptChars / 2) + "…" + Slice(text, index + length - ExcerptChars / 2, ExcerptChars / 2) + " [长匹配已节选]";
        var start = Math.Clamp(index - (ExcerptChars - length) / 2, 0, text.Length - ExcerptChars);
        return (start > 0 ? "…" : "") + Slice(text, start, ExcerptChars) + (start + ExcerptChars < text.Length ? "…" : "");
    }

    private static string Slice(string text, int start, int length)
    {
        var end = start + length;
        if (start > 0 && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1])) start--;
        if (end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end++;
        return text[start..end];
    }

    public string Format()
    {
        var output = new StringBuilder();
        if (_groups.Count == 0 && _skippedGroups == 0) output.Append("未找到匹配项。");
        else if (_offset - _skippedGroups >= _groups.Count)
            output.Append($"[offset {_offset} 超出结果范围；共 {_skippedGroups + _groups.Count} 个命中文件]");
        else
        {
            foreach (var group in _groups.OrderByDescending(g => g.Matches).ThenBy(g => g.File, StringComparer.Ordinal).Skip(_offset - _skippedGroups))
                output.Append("## ").Append(group.File).Append(" (").Append(group.Matches).AppendLine(" 处命中)").Append(group.Body);
            if (Done) output.Append($"[已达到本页命中上限；使用 offset: {_skippedGroups + _groups.Count} 检查后续文件。当前文件可收窄 pattern 或提高 max_matches。]");
            else if (_matches == _maxMatches) output.Append("[已达到本页命中上限；当前文件可收窄 pattern 或提高 max_matches。]");
        }
        if (_skippedLargeFiles > 0)
            output.Append($"\n[另有 {_skippedLargeFiles} 个超过 {FileLimit / (1024 * 1024)}MB 的文件未参与检索（合计 {_skippedBytes / (1024 * 1024)}MB）]");
        return output.ToString().TrimEnd();
    }
}
