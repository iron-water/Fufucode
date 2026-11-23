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
                // 未闭合的围栏（流式中）：先看语言；图表类要整段留给 Markdown 渲染器，
                // 否则闭合的一瞬间会从代码块切换成图表，产生一次跳变。
                var openLanguage = FirstLine(afterFence);
                if (IsRichFence(openLanguage)) AddPlain(segments, remaining[fenceStart..]);
                else AddCode(segments, OpenFenceBody(afterFence), openLanguage);
                break;
            }

            var code = afterFence[..fenceEnd];
            var language = ExtractLanguage(ref code);

            // Mermaid 围栏不能当普通代码块切走：它是图表，必须整段交给 Markdown 渲染器。
            if (IsRichFence(language))
            {
                var blockEnd = fenceStart + Fence.Length + fenceEnd + Fence.Length;
                AddPlain(segments, remaining[fenceStart..blockEnd]);
                remaining = remaining[blockEnd..];
                continue;
            }

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

    /// <summary>
    /// 流式中尚未闭合的围栏：与闭合后保持同一形状，避免闭合瞬间代码块缩一截。
    /// 语言行不算代码；末行若只是正在到达的闭合反引号（1–3 个）也先不显示。
    /// </summary>
    private static string OpenFenceBody(string afterFence)
    {
        var newline = afterFence.IndexOfAny(new[] { '\n', '\r' });
        if (newline < 0) return string.Empty; // 还在输入语言名
        var body = afterFence[(newline + 1)..];
        var lastBreak = body.LastIndexOf('\n');
        var lastLine = (lastBreak < 0 ? body : body[(lastBreak + 1)..]).TrimEnd('\r');
        if (lastLine.Trim().Length > 0 && lastLine.Trim().Trim('`').Length == 0)
            body = lastBreak < 0 ? string.Empty : body[..lastBreak];
        return body;
    }

    /// <summary>这些语言由 Markdown 渲染器处理（图表等），不按代码块切分。</summary>
    private static bool IsRichFence(string language) =>
        language.Equals("mermaid", StringComparison.OrdinalIgnoreCase);

    private static string FirstLine(string text)
    {
        var newline = text.IndexOfAny(new[] { (char)10, (char)13 });
        return (newline < 0 ? text : text[..newline]).Trim();
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
