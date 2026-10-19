using System;
using System.Collections.Generic;

namespace Fufucode.App.Infrastructure;

/// <summary>内联 diff 的一行。Kind 为 '+'(新增)、'-'(删除)、' '(上下文)、'…'(截断提示)。</summary>
public sealed record DiffLine(char Kind, string Text);

/// <summary>
/// 行级 LCS diff，只服务于界面的内联预览。
///
/// 刻意保持简单：不做字符级 diff、不做 hunk 折叠。
/// 行数超过上限时退化为"整段替换"，避免在大文件上做 O(n²) 的表格。
/// </summary>
public static class LineDiff
{
    private const int MaxLcsLines = 1200;
    private const int MaxOutputLines = 600;

    public static IReadOnlyList<DiffLine> Compute(string? before, string? after)
    {
        var oldLines = SplitLines(before);
        var newLines = SplitLines(after);

        if (oldLines.Length > MaxLcsLines || newLines.Length > MaxLcsLines)
            return Truncated(oldLines, newLines);

        // 经典 LCS 表；行数受上限保护，内存可接受。
        var table = new int[oldLines.Length + 1, newLines.Length + 1];
        for (var i = oldLines.Length - 1; i >= 0; i--)
        {
            for (var j = newLines.Length - 1; j >= 0; j--)
            {
                table[i, j] = oldLines[i] == newLines[j]
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var result = new List<DiffLine>();
        int x = 0, y = 0;
        while (x < oldLines.Length && y < newLines.Length)
        {
            if (oldLines[x] == newLines[y])
            {
                result.Add(new DiffLine(' ', newLines[y]));
                x++; y++;
            }
            else if (table[x + 1, y] >= table[x, y + 1])
            {
                result.Add(new DiffLine('-', oldLines[x]));
                x++;
            }
            else
            {
                result.Add(new DiffLine('+', newLines[y]));
                y++;
            }

            if (result.Count >= MaxOutputLines) { result.Add(new DiffLine('…', "（差异过长，已截断）")); return result; }
        }

        while (x < oldLines.Length) result.Add(new DiffLine('-', oldLines[x++]));
        while (y < newLines.Length) result.Add(new DiffLine('+', newLines[y++]));
        return result;
    }

    private static IReadOnlyList<DiffLine> Truncated(string[] before, string[] after)
    {
        var result = new List<DiffLine>();
        foreach (var line in before) result.Add(new DiffLine('-', line));
        foreach (var line in after) result.Add(new DiffLine('+', line));
        if (result.Count > MaxOutputLines)
        {
            result.RemoveRange(MaxOutputLines, result.Count - MaxOutputLines);
            result.Add(new DiffLine('…', "（文件过大，已截断）"));
        }
        return result;
    }

    private static string[] SplitLines(string? text) =>
        string.IsNullOrEmpty(text) ? [] : text.Replace("\r\n", "\n").Split('\n');
}
