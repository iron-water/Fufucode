using System;
using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Fufucode.App.Views;

/// <summary>
/// 工具输出的只读呈现：识别统一 diff 与 ANSI SGR 颜色，其余按普通文本显示。
///
/// 只做展示，不改原文：复制出去的仍是后端返回的原始输出。
/// 行数过大时退回纯文本，避免为几万行生成几万个 Run 拖慢渲染。
/// </summary>
public sealed class ToolOutputView : SelectableTextBlock
{
    /// <summary>超过此行数不再逐行着色。</summary>
    private const int MaxRichLines = 4000;

    private static readonly IBrush AddedBrush = Brush.Parse("#4CAF50");
    private static readonly IBrush RemovedBrush = Brush.Parse("#E57373");
    private static readonly IBrush HunkBrush = Brush.Parse("#64B5F6");
    private static readonly IBrush HeaderBrush = Brush.Parse("#9E9E9E");

    public static readonly StyledProperty<string?> OutputProperty =
        AvaloniaProperty.Register<ToolOutputView, string?>(nameof(Output));

    public string? Output
    {
        get => GetValue(OutputProperty);
        set => SetValue(OutputProperty, value);
    }

    public ToolOutputView()
    {
        TextWrapping = TextWrapping.Wrap;
        Classes.Add("mono");
        FontSize = 12;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OutputProperty) Rebuild();
    }

    private void Rebuild()
    {
        var text = Output ?? string.Empty;
        Inlines ??= new InlineCollection();
        Inlines.Clear();
        if (text.Length == 0) return;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > MaxRichLines || (!HasAnsi(text) && !LooksLikeDiff(lines)))
        {
            Inlines.Add(new Run(text));
            return;
        }

        var diff = LooksLikeDiff(lines);
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0) Inlines.Add(new LineBreak());
            AppendLine(lines[index], diff);
        }
    }

    private void AppendLine(string line, bool diff)
    {
        if (!diff)
        {
            AppendAnsiLine(line);
            return;
        }

        var brush = line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)
            ? HeaderBrush
            : line.StartsWith("@@", StringComparison.Ordinal)
                ? HunkBrush
                : line.StartsWith('+') ? AddedBrush
                : line.StartsWith('-') ? RemovedBrush
                : null;

        // 显式把 Foreground 设为 null 会丢掉继承色（上下文行会变透明），所以只在有颜色时赋值。
        var run = new Run(line);
        if (brush is not null) run.Foreground = brush;
        Inlines?.Add(run);
    }

    /// <summary>解析一行里的 SGR 序列；未识别的转义原样保留，避免吞掉真实输出。</summary>
    private void AppendAnsiLine(string line)
    {
        if (!HasAnsi(line))
        {
            Inlines?.Add(new Run(line));
            return;
        }

        var buffer = new StringBuilder();
        IBrush? brush = null;
        var bold = false;

        void Flush()
        {
            if (buffer.Length == 0) return;
            var run = new Run(buffer.ToString());
            if (brush is not null) run.Foreground = brush;
            if (bold) run.FontWeight = FontWeight.Bold;
            Inlines?.Add(run);
            buffer.Clear();
        }

        for (var index = 0; index < line.Length; index++)
        {
            var ch = line[index];
            if (ch != '\u001b' || index + 1 >= line.Length || line[index + 1] != '[')
            {
                buffer.Append(ch);
                continue;
            }

            var end = line.IndexOf('m', index + 2);
            if (end < 0)
            {
                buffer.Append(ch);
                continue;
            }

            // 先把控制码之前的文本按当前颜色刷出，再切换颜色。
            Flush();
            var codes = line[(index + 2)..end].Split(';');
            foreach (var code in codes)
            {
                if (!int.TryParse(code, out var value)) continue;
                switch (value)
                {
                    case 0: brush = null; bold = false; break;
                    case 1: bold = true; break;
                    case 22: bold = false; break;
                    case 30 or 90: brush = Brushes.Gray; break;
                    case 31 or 91: brush = RemovedBrush; break;
                    case 32 or 92: brush = AddedBrush; break;
                    case 33 or 93: brush = Brush.Parse("#E0B252"); break;
                    case 34 or 94: brush = HunkBrush; break;
                    case 35 or 95: brush = Brush.Parse("#C792EA"); break;
                    case 36 or 96: brush = Brush.Parse("#4DD0E1"); break;
                    case 37 or 97: brush = Brushes.White; break;
                }
            }

            // 控制码本身不显示，但已刷出的片段要保留它之前的颜色。
            index = end;
        }

        Flush();
    }

    private static bool HasAnsi(string text) => text.Contains("\u001b[", StringComparison.Ordinal);

    /// <summary>
    /// 判定是否像统一 diff：必须同时出现 @@ 或 +++/--- 头，
    /// 避免把普通输出里以 +/- 开头的行误判成 diff。
    /// </summary>
    private static bool LooksLikeDiff(IReadOnlyList<string> lines)
    {
        var hasHeader = false;
        var hasHunk = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal)) hasHunk = true;
            else if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)) hasHeader = true;
        }
        return hasHeader || hasHunk;
    }
}
