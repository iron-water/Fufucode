using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace Fufucode.App.Infrastructure;

/// <summary>
/// 聊天消息的轻量 Markdown 行内渲染器（零依赖）。
/// 用法：在 TextBlock 上写 <c>md:MarkdownTextBlock.Markdown="{Binding Text}"</c>；
/// 不能直接绑定 Inlines，故用附加属性在代码侧重建行内内容。
/// 任何解析异常都回退为原始纯文本，绝不丢消息。
/// </summary>
public sealed class MarkdownTextBlock
{
    public static readonly AttachedProperty<string?> MarkdownProperty =
        AvaloniaProperty.RegisterAttached<MarkdownTextBlock, TextBlock, string?>("Markdown");

    private static readonly AttachedProperty<bool> HooksInstalledProperty =
        AvaloniaProperty.RegisterAttached<MarkdownTextBlock, TextBlock, bool>("HooksInstalled");

    private const string MonoFontKey = "AppMonoFont";
    private const string CodeBackgroundKey = "AppCodeBackground";
    private const string AccentTextKey = "AppAccentText";
    private const string SecondaryTextKey = "AppTextSecondary";

    private static readonly FontFamily FallbackMonoFont = new("Cascadia Mono, Consolas, monospace");

    private static readonly char[] EscapeableChars = ['*', '_', '`', '[', ']', '<', '>', '#', '-', '\\'];

    private MarkdownTextBlock()
    {
    }

    static MarkdownTextBlock()
    {
        MarkdownProperty.Changed.AddClassHandler<TextBlock>(OnMarkdownChanged);
    }

    public static void SetMarkdown(TextBlock target, string? value) => target.SetValue(MarkdownProperty, value);

    public static string? GetMarkdown(TextBlock target) => target.GetValue(MarkdownProperty);

    private static void OnMarkdownChanged(TextBlock target, AvaloniaPropertyChangedEventArgs args)
    {
        EnsureHooks(target);
        Apply(target);
    }

    private static void EnsureHooks(TextBlock target)
    {
        if (target.GetValue(HooksInstalledProperty))
        {
            return;
        }

        target.SetValue(HooksInstalledProperty, true);
        // 主题切换与首次装载时资源才可解析，两者都要重建行内内容，否则颜色会停在解析失败态。
        target.ActualThemeVariantChanged += (sender, _) =>
        {
            if (sender is TextBlock block)
            {
                Apply(block);
            }
        };
        target.Loaded += (sender, _) =>
        {
            if (sender is TextBlock block)
            {
                Apply(block);
            }
        };
    }

    /// <summary>按当前 Markdown 重建 Inlines；失败则退回原始文本。</summary>
    public static void Apply(TextBlock target)
    {
        var raw = GetMarkdown(target) ?? string.Empty;
        var collection = target.Inlines;
        if (collection is null)
        {
            target.Text = raw;
            return;
        }

        try
        {
            var inlines = Parse(target, raw);
            collection.Clear();
            foreach (var inline in inlines)
            {
                collection.Add(inline);
            }
        }
        catch (Exception)
        {
            RestorePlainText(target, collection, raw);
        }
    }

    private static void RestorePlainText(TextBlock target, InlineCollection collection, string raw)
    {
        try
        {
            collection.Clear();
            collection.Add(new Run(raw));
        }
        catch (Exception)
        {
            // 行内集合本身不可用时最后手段走 Text，宁可没有格式也不能丢消息内容。
            target.Text = raw;
        }
    }

    /// <summary>解析入口：块级（围栏/标题/列表）+ 行内（代码/粗体/斜体/链接）。</summary>
    public static IReadOnlyList<Inline> Parse(TextBlock host, string? markdown)
    {
        var result = new List<Inline>();
        if (string.IsNullOrEmpty(markdown))
        {
            return result;
        }

        var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n');
        var baseSize = double.IsFinite(host.FontSize) && host.FontSize > 0 ? host.FontSize : 14;
        var inFence = false;
        var fenceBrush = ResolveBrush(host, CodeBackgroundKey);
        var monoFamily = ResolveFontFamily(host);

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();

            if (IsFenceMarker(trimmed))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                var codeLine = new Run(line)
                {
                    FontFamily = monoFamily ?? FallbackMonoFont,
                    FontSize = Math.Max(11, baseSize - 1.5),
                };
                if (fenceBrush is not null)
                {
                    codeLine.Background = fenceBrush;
                }

                result.Add(codeLine);
                AppendBreak(result, index, lines.Length);
                continue;
            }

            var headingLevel = ReadHeadingLevel(trimmed, out var headingBody);
            if (headingLevel > 0)
            {
                var heading = new Bold();
                foreach (var headingInline in ParseInlines(host, headingBody, baseSize, monoFamily))
                {
                    headingInline.FontSize = HeadingSize(headingLevel, baseSize);
                    heading.Inlines.Add(headingInline);
                }

                result.Add(heading);
                AppendBreak(result, index, lines.Length);
                continue;
            }

            if (TryReadListItem(line, trimmed, out var indent, out var bulletBody))
            {
                var marker = new Run(indent + "• ");
                var markerBrush = ResolveBrush(host, SecondaryTextKey);
                if (markerBrush is not null)
                {
                    marker.Foreground = markerBrush;
                }

                result.Add(marker);
                foreach (var itemInline in ParseInlines(host, bulletBody, baseSize, monoFamily))
                {
                    result.Add(itemInline);
                }

                AppendBreak(result, index, lines.Length);
                continue;
            }

            foreach (var inline in ParseInlines(host, line, baseSize, monoFamily))
            {
                result.Add(inline);
            }

            AppendBreak(result, index, lines.Length);
        }

        return result;
    }

    private static void AppendBreak(List<Inline> result, int lineIndex, int lineCount)
    {
        // 末行不留多余空行，否则气泡底部高度会随消息行数虚增。
        if (lineIndex < lineCount - 1)
        {
            result.Add(new LineBreak());
        }
    }

    private static bool IsFenceMarker(string trimmed) =>
        trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);

    private static double HeadingSize(int level, double baseSize) => level switch
    {
        1 => Math.Max(baseSize + 6, 20),
        2 => baseSize + 4,
        3 => baseSize + 2,
        _ => baseSize + 1,
    };

    private static int ReadHeadingLevel(string trimmed, out string body)
    {
        body = string.Empty;
        if (trimmed.Length == 0 || trimmed[0] != '#')
        {
            return 0;
        }

        var level = 0;
        while (level < trimmed.Length && trimmed[level] == '#')
        {
            level++;
        }

        if (level > 6)
        {
            return 0;
        }

        if (level == trimmed.Length)
        {
            // 纯 "###" 行按空正文标题处理。
            return level;
        }

        if (trimmed[level] != ' ' && trimmed[level] != '\t')
        {
            return 0;
        }

        body = trimmed[(level + 1)..].Trim();
        return level;
    }

    private static bool TryReadListItem(string line, string trimmed, out string indent, out string body)
    {
        indent = string.Empty;
        body = string.Empty;

        if (trimmed.Length == 0)
        {
            return false;
        }

        var leading = line.Length - trimmed.Length;
        if (leading > 0)
        {
            // 用全角空格近似层级：TextBlock 行内没有左边距可用。
            indent = new string('　', Math.Min(leading / 2 + 1, 4));
        }

        var marker = trimmed[0];
        if (marker != '-' && marker != '*' && marker != '+')
        {
            return false;
        }

        if (trimmed.Length < 2 || trimmed[1] != ' ')
        {
            return false;
        }

        body = trimmed[2..];
        return true;
    }

    private static IEnumerable<Inline> ParseInlines(
        TextBlock host, string text, double baseSize, FontFamily? monoFamily)
    {
        var result = new List<Inline>();
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        var buffer = new StringBuilder();
        var index = 0;

        void Flush()
        {
            if (buffer.Length > 0)
            {
                result.Add(new Run(buffer.ToString()));
                buffer.Clear();
            }
        }

        while (index < text.Length)
        {
            var ch = text[index];

            if (ch == '\\' && index + 1 < text.Length && Array.IndexOf(EscapeableChars, text[index + 1]) >= 0)
            {
                buffer.Append(text[index + 1]);
                index += 2;
                continue;
            }

            if (ch == '`')
            {
                var close = text.IndexOf('`', index + 1);
                if (close > index)
                {
                    Flush();
                    result.Add(MakeInlineCode(text[(index + 1)..close], baseSize, monoFamily, host));
                    index = close + 1;
                    continue;
                }
            }

            if (ch == '[')
            {
                var bracket = FindClosingBracket(text, index);
                if (bracket > index && bracket + 1 < text.Length && text[bracket + 1] == '(')
                {
                    var paren = text.IndexOf(')', bracket + 2);
                    if (paren > bracket)
                    {
                        Flush();
                        var label = text[(index + 1)..bracket];
                        var url = text[(bracket + 2)..paren].Trim();
                        result.Add(MakeLink(host, label, url, baseSize));
                        index = paren + 1;
                        continue;
                    }
                }
            }

            if (ch == '*' || ch == '_')
            {
                var strong = index + 1 < text.Length && text[index + 1] == ch;
                var markerLength = strong ? 2 : 1;

                if (!IsIntraword(text, index, ch))
                {
                    var close = FindEmphasisClose(text, index + markerLength, ch, markerLength);
                    if (close >= 0)
                    {
                        Flush();
                        var inner = text[(index + markerLength)..close];
                        Span span = strong ? new Bold() : new Italic();
                        span.Inlines.AddRange(ParseInlines(host, inner, baseSize, monoFamily));
                        result.Add(span);
                        index = close + markerLength;
                        continue;
                    }
                }
            }

            buffer.Append(ch);
            index++;
        }

        Flush();
        return result;
    }

    /// <summary>下划线用于 snake_case 时不做斜体；星号无此约束。</summary>
    private static bool IsIntraword(string text, int index, char marker)
    {
        if (marker != '_')
        {
            return false;
        }

        return index > 0 && (char.IsLetterOrDigit(text[index - 1]) || text[index - 1] == '_');
    }

    private static int FindEmphasisClose(string text, int start, char marker, int markerLength)
    {
        var searchFrom = start;
        while (searchFrom < text.Length)
        {
            var candidate = text.IndexOf(marker, searchFrom);
            if (candidate < 0)
            {
                return -1;
            }

            if (markerLength == 2)
            {
                if (candidate + 1 < text.Length && text[candidate + 1] == marker && candidate > 0 &&
                    !char.IsWhiteSpace(text[candidate - 1]))
                {
                    return candidate;
                }

                searchFrom = candidate + 1;
                continue;
            }

            if (candidate > 0 && !char.IsWhiteSpace(text[candidate - 1]))
            {
                return candidate;
            }

            searchFrom = candidate + 1;
        }

        return -1;
    }

    private static int FindClosingBracket(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '[')
            {
                depth++;
            }
            else if (text[i] == ']')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static Run MakeInlineCode(string code, double baseSize, FontFamily? monoFamily, TextBlock host)
    {
        var run = new Run(code)
        {
            FontFamily = monoFamily ?? FallbackMonoFont,
            FontSize = Math.Max(11, baseSize - 1),
        };

        var background = ResolveBrush(host, CodeBackgroundKey);
        if (background is not null)
        {
            run.Background = background;
        }

        return run;
    }

    /// <summary>
    /// 链接用 InlineUIContainer 承载可点击 TextBlock：Avalonia 12 无 Hyperlink 行内类型，
    /// 纯 Run 拿不到指针事件。
    /// </summary>
    private static Inline MakeLink(TextBlock host, string label, string url, double baseSize)
    {
        var text = new TextBlock
        {
            Text = string.IsNullOrEmpty(label) ? url : label,
            FontSize = baseSize,
            FontFamily = host.FontFamily,
            TextDecorations = TextDecorations.Underline,
            Cursor = new Cursor(StandardCursorType.Hand),
            Foreground = ResolveBrush(host, AccentTextKey) ?? host.Foreground,
            TextWrapping = TextWrapping.WrapWithOverflow,
        };

        text.PointerPressed += (_, args) =>
        {
            if (TryOpenInBrowser(url))
            {
                args.Handled = true;
            }
        };

        return new InlineUIContainer(text);
    }

    /// <summary>只放行 http/https，模型输出的 Markdown 链接属于不可信输入。</summary>
    internal static bool IsSafeUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
        (string.Equals(parsed!.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(parsed.Scheme, "https", StringComparison.OrdinalIgnoreCase));

    private static bool TryOpenInBrowser(string url)
    {
        if (!IsSafeUrl(url))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static FontFamily? ResolveFontFamily(TextBlock host)
    {
        if (host.TryGetResource(MonoFontKey, host.ActualThemeVariant, out var value) && value is FontFamily family)
        {
            return family;
        }

        // 游离元素（自测构造、模板实例化早期）拿不到作用域资源，退回应用级资源根。
        var app = Application.Current;
        if (app is not null &&
            app.TryGetResource(MonoFontKey, app.ActualThemeVariant, out var appValue) &&
            appValue is FontFamily appFamily)
        {
            return appFamily;
        }

        return null;
    }

    private static IBrush? ResolveBrush(TextBlock host, string key)
    {
        if (host.TryGetResource(key, host.ActualThemeVariant, out var value) && value is IBrush brush)
        {
            return brush;
        }

        var app = Application.Current;
        if (app is not null &&
            app.TryGetResource(key, app.ActualThemeVariant, out var appValue) &&
            appValue is IBrush appBrush)
        {
            return appBrush;
        }

        return null;
    }
}
