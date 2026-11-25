using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Fufucode.Core.Memory;

/// <summary>一条记忆：Markdown 列表项的正文与记录日期（用户手写的条目可能没有日期）。</summary>
public sealed record MemoryEntry(string Text, DateOnly? Date);

/// <summary>forget 的结果：Removed 为已删除条目；匹配多条时不删除，Candidates 列出候选。</summary>
public sealed record MemoryForgetResult(IReadOnlyList<MemoryEntry> Removed, IReadOnlyList<MemoryEntry> Candidates);

/// <summary>
/// 用户级记忆：<c>&lt;dataRoot&gt;/memory/MEMORY.md</c> 中的 Markdown 列表，每行一条。
/// 只存代码和环境推不出来的东西（用户要求记住的事、偏好、更正、意图）；
/// 文件就是唯一真源，用户可以直接编辑，非列表行原样保留。
/// 是否注入会话、是否提供工具由 tool.memory 插件决定，本类只管格式。
/// </summary>
public sealed partial class MemoryNotebook
{
    public const string PluginId = "tool.memory";
    public const int MaxEntryChars = 500;
    /// <summary>注入会话的字符预算；超出时保留最新条目，其余提示去读文件。</summary>
    public const int PromptBudgetChars = 6_000;

    private static readonly object Gate = new();

    public MemoryNotebook(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) throw new ArgumentException("数据根目录为空。", nameof(dataRoot));
        FilePath = Path.Combine(dataRoot, "memory", "MEMORY.md");
    }

    public string FilePath { get; }

    public IReadOnlyList<MemoryEntry> Read()
    {
        lock (Gate) return ReadLines().Select(Parse).OfType<MemoryEntry>().ToList();
    }

    /// <summary>追加一条；正文相同（忽略大小写与首尾空白）的已有条目直接返回，不重复写入。</summary>
    public MemoryEntry Add(string text, DateOnly? today = null)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0) throw new ArgumentException("记忆内容为空。", nameof(text));
        if (normalized.Length > MaxEntryChars)
            throw new ArgumentException($"一条记忆最多 {MaxEntryChars} 个字符，请拆成多条或精简。", nameof(text));
        lock (Gate)
        {
            var lines = ReadLines();
            var existing = lines.Select(Parse).OfType<MemoryEntry>()
                .FirstOrDefault(entry => string.Equals(entry.Text, normalized, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;
            var entry = new MemoryEntry(normalized, today ?? DateOnly.FromDateTime(DateTime.Now));
            if (lines.Count == 0) lines.AddRange(["# 记忆", ""]);
            lines.Add(Format(entry));
            WriteLines(lines);
            return entry;
        }
    }

    /// <summary>按原文精确匹配，否则按唯一片段匹配；匹配多条时不删除。</summary>
    public MemoryForgetResult Forget(string query)
    {
        var needle = Normalize(query);
        if (needle.Length == 0) return new([], []);
        lock (Gate)
        {
            var lines = ReadLines();
            var parsed = lines.Select((line, index) => (Index: index, Entry: Parse(line)))
                .Where(item => item.Entry is not null).ToList();
            var exact = parsed.Where(item => string.Equals(item.Entry!.Text, needle, StringComparison.OrdinalIgnoreCase)).ToList();
            var matches = exact.Count > 0
                ? exact
                : parsed.Where(item => item.Entry!.Text.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) return new([], []);
            if (exact.Count == 0 && matches.Count > 1) return new([], matches.Select(item => item.Entry!).ToList());
            foreach (var index in matches.Select(item => item.Index).OrderByDescending(i => i)) lines.RemoveAt(index);
            WriteLines(lines);
            return new(matches.Select(item => item.Entry!).ToList(), []);
        }
    }

    /// <summary>会话 System 段；没有任何条目时返回 null，不占上下文。</summary>
    public string? BuildPromptSection()
    {
        var entries = Read();
        if (entries.Count == 0) return null;
        var kept = new List<string>();
        var used = 0;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var line = Format(entries[i]);
            if (used + line.Length + 1 > PromptBudgetChars && kept.Count > 0) break;
            kept.Add(line);
            used += line.Length + 1;
        }
        kept.Reverse();
        var builder = new StringBuilder();
        builder.AppendLine($"## 记忆（用户级，跨会话与项目；文件：{FilePath}）");
        builder.AppendLine("以下是用户要你记住的偏好、更正与意图。用户本轮的直接指令优先；涉及代码或环境的事实以当前实测为准，冲突时如实告诉用户。");
        if (kept.Count < entries.Count)
            builder.AppendLine($"（篇幅所限只列出最新 {kept.Count} 条，较早的 {entries.Count - kept.Count} 条在上述文件中。）");
        foreach (var line in kept) builder.AppendLine(line);
        return builder.ToString().TrimEnd();
    }

    private static string Normalize(string? text) =>
        WhitespaceRun().Replace(text ?? string.Empty, " ").Trim();

    private static string Format(MemoryEntry entry) =>
        entry.Date is { } date ? $"- {date:yyyy-MM-dd} {entry.Text}" : $"- {entry.Text}";

    private static MemoryEntry? Parse(string line)
    {
        var match = EntryLine().Match(line);
        if (!match.Success) return null;
        var text = match.Groups["text"].Value.Trim();
        if (text.Length == 0) return null;
        DateOnly? date = DateOnly.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
        return new MemoryEntry(text, date);
    }

    private List<string> ReadLines()
    {
        try { return File.Exists(FilePath) ? File.ReadAllLines(FilePath).ToList() : []; }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    private void WriteLines(List<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(false));
        File.Move(temp, FilePath, overwrite: true);
    }

    [GeneratedRegex(@"^\s*[-*]\s+(?:(?<date>\d{4}-\d{2}-\d{2})\s+)?(?<text>.*)$")]
    private static partial Regex EntryLine();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
