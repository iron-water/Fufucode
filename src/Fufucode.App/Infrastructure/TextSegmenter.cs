using System;
using System.Collections.Generic;
using System.Text;

namespace Fufucode.App.Infrastructure;

/// <summary>
/// 把含 ``` 代码围栏的文本拆成普通段与代码段，供时间线分别渲染（代码块带复制与折叠）。
/// </summary>
public static class TextSegmenter
{
    private const string Fence = "```";

    public static IReadOnlyList<TextSegment> Split(string? text)
    {
        var segments = new List<TextSegment>();
        if (string.IsNullOrEmpty(text))
        {
            return segments;
        }

        var remaining = text;
        while (remaining.Length > 0)
        {
            var fenceStart = remaining.IndexOf(Fence, StringComparison.Ordinal);
            if (fenceStart < 0)
            {
                AddPlain(segments, remaining);
                break;
            }

            if (fenceStart > 0)
            {
                AddPlain(segments, remaining[..fenceStart]);
            }

            var afterFence = remaining[(fenceStart + Fence.Length)..];
            var fenceEnd = afterFence.IndexOf(Fence, StringComparison.Ordinal);
            if (fenceEnd < 0)
            {
                // 未闭合的围栏（流式中）：当作代码段展示，等后续 delta 补齐
                AddCode(segments, afterFence, string.Empty);
                break;
            }

            var code = afterFence[..fenceEnd];
            var language = ExtractLanguage(ref code);
            AddCode(segments, code, language);
            remaining = afterFence[(fenceEnd + Fence.Length)..];
        }

        return segments;
    }

    private static void AddPlain(List<TextSegment> segments, string text)
    {
        if (text.Length > 0)
        {
            segments.Add(new TextSegment(false, text, string.Empty));
        }
    }

    private static void AddCode(List<TextSegment> segments, string code, string language)
    {
        if (code.Length > 0)
        {
            segments.Add(new TextSegment(true, code.Trim('\r', '\n'), language));
        }
    }

    private static string ExtractLanguage(ref string code)
    {
        var newlineIndex = code.IndexOfAny(new[] { '\n', '\r' });
        if (newlineIndex <= 0)
        {
            return string.Empty;
        }

        var language = code[..newlineIndex].Trim();
        code = code[(newlineIndex + 1)..];
        return language;
    }

    /// <summary>计算文本行数，用于决定是否折叠长输出。</summary>
    public static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var lines = 1;
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                lines++;
            }
        }
        return lines;
    }
}

/// <summary>文本段：普通文本或代码块。</summary>
public sealed record TextSegment(bool IsCode, string Text, string Language);
