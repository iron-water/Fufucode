using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// 深度 Trace 检查器：对齐 Langfuse Trace 观测台架构。
/// 支持 [概览 | 输入 Prompt | 输出 Output | 指标 Metrics | 评估 Scores | 属性 Attributes | 原始 JSON] 全维度深度诊断。
/// </summary>
internal sealed class PerfInspectorView : UserControl
{
    private readonly TextBlock _name = new() { FontSize = 13, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    private readonly TextBlock _meta = new() { FontSize = 10 };
    private readonly TextBlock _duration = new() { FontSize = 18, FontWeight = FontWeight.SemiBold };
    private readonly Border _kindChip = new();
    private readonly TextBlock _kindText = new() { FontSize = 9, FontWeight = FontWeight.SemiBold };
    private readonly Border _statusChip;
    private readonly TextBlock _statusText = new() { FontSize = 9.5 };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
    private readonly ContentControl _content = new();
    private readonly ScrollViewer _scroll;
    private SpanRecord? _span;
    private IReadOnlyList<PerfEventRecord> _events = [];
    private string _tab = "概览";

    public PerfInspectorView()
    {
        _name.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        _meta.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        _duration.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        _statusText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppSuccessForeground"));

        _kindChip.Padding = new Thickness(6, 1.5);
        _kindChip.CornerRadius = new CornerRadius(4);
        _kindChip.VerticalAlignment = VerticalAlignment.Center;
        _kindChip.Child = _kindText;
        _kindChip.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppSubtleBackground"));

        _statusChip = new Border
        {
            Padding = new Thickness(7, 2),
            CornerRadius = new CornerRadius(999),
            Child = _statusText,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _statusChip.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppSubtleBackground"));

        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _kindChip, _name, _statusChip, _meta },
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        header.Children.Add(title);
        Grid.SetColumn(_duration, 1);
        header.Children.Add(_duration);

        var tabRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        tabRow.Children.Add(_tabs);

        _scroll = new ScrollViewer
        {
            Content = _content,
            MaxHeight = 340,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        var body = new StackPanel
        {
            Spacing = 12,
            Children = { header, tabRow, Divider(), _scroll },
        };
        var card = new Border
        {
            Padding = new Thickness(16, 14, 16, 16),
            CornerRadius = new CornerRadius(14),
            Child = body,
        };
        card.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppCardBackground"));
        card.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("AppBorderBrush"));
        card.BorderThickness = new Thickness(1);
        Content = card;
        BuildTabs();
        SelectTab("概览");
    }

    public void SetSpan(SpanRecord? span, IReadOnlyList<PerfEventRecord> events)
    {
        _span = span;
        _events = events;
        if (span is null)
        {
            RenderEmpty();
            return;
        }

        var kind = ResolveLangfuseKind(span.Name);
        _kindText.Text = kind;
        _kindText.Foreground = KindBrush(kind);
        _kindChip.IsVisible = true;

        _name.Text = span.Name;
        _duration.Text = PerfPalette.FormatMs(span.DurationMs);
        _meta.Text = $"trace {span.TraceId:x} · span {span.SpanId:x}";
        _statusChip.IsVisible = true;
        var error = span.Status == "Error";
        _statusText.Text = span.Status switch { "Error" => "错误", "Cancelled" => "取消", _ => "成功" };
        _statusText.Bind(TextBlock.ForegroundProperty,
            new DynamicResourceExtension(error ? "AppCriticalForeground" : "AppSuccessForeground"));
        RenderTab();
    }

    private static string ResolveLangfuseKind(string name) => name switch
    {
        var n when n.StartsWith("model.", StringComparison.Ordinal) => "GENERATION",
        var n when n.Contains("stall") || n.Contains("approval") || n.StartsWith("event") => "EVENT",
        var n when n.StartsWith("anomaly") || n.Contains("error") => "SCORE",
        _ => "SPAN",
    };

    private static IBrush KindBrush(string kind) => kind switch
    {
        "GENERATION" => PerfPalette.ModelBrush,
        "EVENT" => PerfPalette.ApprovalBrush,
        "SCORE" => PerfPalette.ErrorBrush,
        _ => PerfPalette.ToolBrush,
    };

    private void BuildTabs()
    {
        _tabs.Children.Clear();
        foreach (var name in new[] { "概览", "输入", "输出", "指标", "评估", "属性", "原始" })
        {
            var tab = new Button
            {
                Content = name,
                FontSize = 10.5,
                Padding = new Thickness(0, 5, 0, 5),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(PerfPalette.Neutral),
            };
            tab.Click += (_, _) => SelectTab(name);
            _tabs.Children.Add(tab);
        }
    }

    private void SelectTab(string name)
    {
        _tab = name;
        foreach (var child in _tabs.Children.OfType<Button>())
        {
            var active = string.Equals(child.Content?.ToString(), name, StringComparison.Ordinal);
            child.Foreground = active
                ? new SolidColorBrush(PerfPalette.Model)
                : new SolidColorBrush(PerfPalette.Neutral);
            child.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
        }
        RenderTab();
    }

    private void RenderTab()
    {
        if (_span is null)
        {
            RenderEmpty();
            return;
        }
        _content.Content = _tab switch
        {
            "输入" => RenderInput(_span),
            "输出" => RenderOutput(_span),
            "指标" => RenderMetrics(_span),
            "评估" => RenderScores(_span),
            "属性" => RenderTags(_span),
            "原始" => RenderRaw(_span),
            _ => RenderSummary(_span),
        };
    }

    private Control RenderSummary(SpanRecord span)
    {
        var panel = new WrapPanel();
        AddField(panel, "观测类型", ResolveLangfuseKind(span.Name));
        AddField(panel, "开始时刻", FormatStart(span));
        AddField(panel, "结束时刻", span.EndUtc.ToLocalTime().ToString("HH:mm:ss.fff"));
        AddField(panel, "执行耗时", PerfPalette.FormatMs(span.DurationMs));
        AddField(panel, "类别归属", span.Category);
        AddField(panel, "执行状态", span.Status);
        AddField(panel, "父 Span ID", span.ParentSpanId == 0 ? "根调用 (root)" : span.ParentSpanId.ToString("x"));
        AddField(panel, "工作线程", span.Tag("thread") ?? "thread-pool");
        AddField(panel, "工具名称", PerfPalette.ToolDisplayName(span.Tag("tool")));
        AddField(panel, "命令简述", span.Tag("command"));
        AddField(panel, "工作目录", span.Tag("cwd"));
        AddField(panel, "退出代码", span.Tag("exitCode"));
        AddField(panel, "超时状态", span.Tag("timedOut"));
        AddField(panel, "首字延迟 TTFT", span.Tag("ttftMs") is { } ttft ? $"{ttft} ms" : null);
        AddField(panel, "流式总帧数", span.Tag("frames"));
        AddField(panel, "最大帧间隔", span.Tag("maxGapMs") is { } gap ? $"{gap} ms" : null);
        AddField(panel, "p95 帧抖动", span.Tag("p95GapMs") is { } p95 ? $"{p95} ms" : null);
        AddField(panel, "输入 Token", span.Tag("promptTokens"));
        AddField(panel, "输出 Token", span.Tag("completionTokens"));
        AddField(panel, "缓存命中 Token", span.Tag("cachedTokens"));
        AddField(panel, "异常详情", span.Error);
        return panel;
    }

    private Control RenderInput(SpanRecord span)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Title("输入 Prompt / 参数 (Input Payload)"));

        var isModel = span.Name.StartsWith("model.", StringComparison.Ordinal);
        if (isModel)
        {
            panel.Children.Add(MessageCard("System 提示词规范", "系统环境规范、工具协议、规划约束与技能定义已在 context.build 注入", PerfPalette.CtxSystemPrompt));
            if (span.Tag("tools") is { } tools)
                panel.Children.Add(MessageCard("Tool Definitions", $"向端点声明了 {tools} 个工具协议结构", PerfPalette.CtxTools));
            panel.Children.Add(MessageCard("User / Conversation", "多轮历史对话消息与当前轮次提示词输入", PerfPalette.CtxConversation));
        }
        else if (span.Name == "tool.process" || span.Name == "tool")
        {
            if (span.Tag("command") is { } cmd)
                panel.Children.Add(MessageCard("Command 命令", cmd, PerfPalette.Tool));
            if (span.Tag("cwd") is { } cwd)
                panel.Children.Add(MessageCard("Working Directory 工作目录", cwd, PerfPalette.Neutral));
            if (span.Tag("timeoutSeconds") is { } timeout)
                panel.Children.Add(MessageCard("Timeout 超时约束", $"{timeout} 秒", PerfPalette.Neutral));
        }
        else if (span.Name == "context.build")
        {
            panel.Children.Add(MessageCard("系统规范字符数", $"{span.Tag("sysChars") ?? "—"} 字符", PerfPalette.CtxSystemPrompt));
            panel.Children.Add(MessageCard("工具契约字符数", $"{span.Tag("toolDefChars") ?? "—"} 字符", PerfPalette.CtxTools));
            panel.Children.Add(MessageCard("用户输入字符数", $"{span.Tag("userChars") ?? "—"} 字符", PerfPalette.CtxConversation));
        }
        else
        {
            panel.Children.Add(Muted("该 span 无特殊输入结构，具体参数见属性或原始 JSON。"));
        }
        return panel;
    }

    private Control RenderOutput(SpanRecord span)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Title("输出结果 (Output Completion / Results)"));

        if (span.Name.StartsWith("model.", StringComparison.Ordinal))
        {
            var frames = span.Tag("frames") ?? "0";
            var textChars = span.Tag("textChars") ?? "—";
            panel.Children.Add(MessageCard("Model Output", $"模型流式生成完成 · 收到 {frames} 帧 · 文本 {textChars} 字符", PerfPalette.Model));
            if (span.Tag("toolCalls") is { } toolCalls && toolCalls != "0")
                panel.Children.Add(MessageCard("Tool Calls", $"模型请求发起了 {toolCalls} 次工具调用", PerfPalette.Tool));
        }
        else if (span.Name == "tool.process" || span.Name == "tool")
        {
            var stdout = span.Tag("stdoutHead");
            var stderr = span.Tag("stderrHead");
            var exitCode = span.Tag("exitCode") ?? "0";
            panel.Children.Add(MessageCard("Exit Code 退出码", exitCode, exitCode == "0" ? PerfPalette.Tool : PerfPalette.Error));
            if (!string.IsNullOrEmpty(stdout))
                panel.Children.Add(OutputBlock($"STDOUT (头部，总长 {span.Tag("stdoutBytes") ?? "—"} 字节)", stdout));
            if (!string.IsNullOrEmpty(stderr))
                panel.Children.Add(OutputBlock($"STDERR (头部，总长 {span.Tag("stderrBytes") ?? "—"} 字节)", stderr));
        }
        else
        {
            panel.Children.Add(Muted("输出详情已记录于日志或工具结果卡片中。"));
        }
        return panel;
    }

    private Control RenderMetrics(SpanRecord span)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Title("Langfuse 关键性能指标 (Key Metrics)"));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 10, ColumnSpacing = 10 };

        var prompt = span.LongTag("promptTokens") ?? 0;
        var completion = span.LongTag("completionTokens") ?? 0;
        var cached = span.LongTag("cachedTokens") ?? 0;
        var total = prompt + completion;

        // 生成速率
        var durationSec = span.DurationMs / 1000.0;
        var tokensPerSec = completion > 0 && durationSec > 0 ? completion / durationSec : 0;
        var ttft = span.LongTag("ttftMs") ?? 0;

        // 粗算预估费用 ($3/1M in, $15/1M out)
        var cost = (prompt * 0.000003) + (completion * 0.000015);

        grid.Children.Add(MetricCard("总 Token", PerfPalette.FormatTokens(total), 0, 0));
        grid.Children.Add(MetricCard("生成速率", tokensPerSec > 0 ? $"{tokensPerSec:0.0} tok/s" : "—", 1, 0));
        grid.Children.Add(MetricCard("预估费用", cost > 0 ? $"${cost:0.0000}" : "—", 2, 0));

        grid.Children.Add(MetricCard("首字延迟 TTFT", ttft > 0 ? $"{ttft} ms" : "—", 0, 1));
        grid.Children.Add(MetricCard("缓存命中率", prompt > 0 ? PerfPalette.FormatPercent(cached / (double)prompt) : "—", 1, 1));
        grid.Children.Add(MetricCard("流式最大帧间隔", span.Tag("maxGapMs") is { } gap ? $"{gap} ms" : "—", 2, 1));

        panel.Children.Add(grid);
        return panel;
    }

    private Control RenderScores(SpanRecord span)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Title("Langfuse 自动化评估与规则评分 (Scores / Evaluations)"));

        var ttft = span.LongTag("ttftMs") ?? 0;
        var maxGap = span.LongTag("maxGapMs") ?? 0;

        panel.Children.Add(ScoreRow("TTFT 响应时延评估", ttft <= 1500 ? "PASS" : "WARN",
            ttft > 0 ? $"{ttft} ms (基准阈值 1500 ms)" : "非流式模型请求", ttft <= 1500 ? PerfPalette.Tool : PerfPalette.Approval));

        panel.Children.Add(ScoreRow("流式抖动与卡顿评估", maxGap <= 1000 ? "PASS" : "STALL",
            maxGap > 0 ? $"最大帧间隔 {maxGap} ms (卡顿阈值 1000 ms)" : "未检测到帧抖动", maxGap <= 1000 ? PerfPalette.Tool : PerfPalette.Error));

        panel.Children.Add(ScoreRow("工具执行健壮性评估", span.Status == "Error" ? "FAIL" : "PASS",
            span.Error ?? "正常执行完毕", span.Status == "Error" ? PerfPalette.Error : PerfPalette.Tool));

        return panel;
    }

    private static Border MetricCard(string label, string value, int col, int row)
    {
        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(Text(label, 10, "AppTextDim"));
        stack.Children.Add(Text(value, 18, "AppTextPrimary", FontWeight.SemiBold));
        var card = new Border
        {
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(10),
            Child = stack,
        };
        card.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppSubtleBackground"));
        Grid.SetColumn(card, col);
        Grid.SetRow(card, row);
        return card;
    }

    private static Border ScoreRow(string name, string score, string detail, Color scoreColor)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 10, Height = 26 };
        grid.Children.Add(Text(name, 11, "AppTextPrimary"));

        var detailText = Text(detail, 10.5, "AppTextDim");
        Grid.SetColumn(detailText, 1);
        grid.Children.Add(detailText);

        var badge = new Border
        {
            Padding = new Thickness(7, 1.5),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(35, scoreColor.R, scoreColor.G, scoreColor.B)),
            Child = new TextBlock
            {
                Text = score,
                FontSize = 9.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(scoreColor),
            },
        };
        Grid.SetColumn(badge, 2);
        grid.Children.Add(badge);

        var border = new Border
        {
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(8),
            Child = grid,
        };
        border.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppSubtleBackground"));
        return border;
    }

    private static Border MessageCard(string role, string content, Color roleColor)
    {
        var stack = new StackPanel { Spacing = 5 };
        var roleBadge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 1.5),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(35, roleColor.R, roleColor.G, roleColor.B)),
            Child = new TextBlock { Text = role, FontSize = 9.5, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(roleColor) },
        };
        stack.Children.Add(roleBadge);
        var text = Text(content, 11, "AppTextPrimary", null, true);
        stack.Children.Add(text);

        var card = new Border
        {
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(10),
            Child = stack,
        };
        card.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppSubtleBackground"));
        return card;
    }

    private Control RenderTags(SpanRecord span)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Title($"全部属性 (Attributes · {span.Tags.Count})"));
        if (span.Tags.Count == 0)
        {
            panel.Children.Add(Muted("该 span 没有额外标签。"));
            return panel;
        }
        foreach (var (key, value) in span.Tags)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("126,*"), ColumnSpacing = 10 };
            var keyText = new TextBlock { Text = key, FontSize = 10, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
            keyText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
            row.Children.Add(keyText);
            var valueText = new TextBlock
            {
                Text = value,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            };
            valueText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
            Grid.SetColumn(valueText, 1);
            row.Children.Add(valueText);
            panel.Children.Add(row);
        }
        return panel;
    }

    private Control RenderRaw(SpanRecord span)
    {
        var raw = new TextBox
        {
            Text = BuildRawJson(span),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 10,
            MinHeight = 220,
            MaxHeight = 320,
        };
        raw.Classes.Add("mono");
        return raw;
    }

    private string BuildRawJson(SpanRecord span)
    {
        var builder = new StringBuilder();
        builder.AppendLine("{");
        Append(builder, "id", span.SpanId.ToString("x"), true);
        Append(builder, "traceId", span.TraceId.ToString("x"), true);
        Append(builder, "parentObservationId", span.ParentSpanId == 0 ? null : span.ParentSpanId.ToString("x"), true);
        Append(builder, "type", ResolveLangfuseKind(span.Name), true);
        Append(builder, "name", span.Name, true);
        Append(builder, "statusMessage", span.Status, true);
        Append(builder, "startTime", FormatStart(span), true);
        Append(builder, "endTime", span.EndUtc.ToString("O", CultureInfo.InvariantCulture), true);
        Append(builder, "duration", Math.Round(span.DurationMs, 2), true);
        Append(builder, "error", span.Error, true);
        builder.AppendLine("  \"metadata\": {");
        for (var index = 0; index < span.Tags.Count; index++)
        {
            var (key, value) = span.Tags[index];
            builder.Append("    ").Append(Json(key)).Append(": ").Append(Json(value));
            builder.AppendLine(index + 1 < span.Tags.Count ? "," : string.Empty);
        }
        builder.AppendLine("  }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string key, double value, bool comma)
        => builder.Append("  \"").Append(key).Append("\": ").Append(value.ToString("0.###", CultureInfo.InvariantCulture))
            .AppendLine(comma ? "," : string.Empty);

    private static void Append(StringBuilder builder, string key, string? value, bool comma)
        => builder.Append("  \"").Append(key).Append("\": ")
            .Append(value is null ? "null" : Json(value))
            .AppendLine(comma ? "," : string.Empty);

    private static string Json(string? value)
        => value is null ? "null" : "\"" + JsonEncodedText.Encode(value) + "\"";

    private static string FormatStart(SpanRecord span)
        => (span.EndUtc - TimeSpan.FromMilliseconds(span.DurationMs)).ToLocalTime().ToString("HH:mm:ss.fff");

    private void RenderEmpty()
    {
        _name.Text = "未选择 span";
        _duration.Text = "—";
        _meta.Text = "在左侧 Span 树或上方时间线中选择一个节点";
        _statusText.Text = "";
        _statusChip.IsVisible = false;
        _kindChip.IsVisible = false;
        _content.Content = Muted("选择一个 span 查看 Langfuse 观测台明细、Prompt 输入、Output 输出与评估分数。");
    }

    private void AddField(Panel panel, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var field = new StackPanel { Width = 280, Spacing = 3, Margin = new Thickness(0, 0, 12, 12) };
        var labelBlock = new TextBlock { Text = label, FontSize = 9.5 };
        labelBlock.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        field.Children.Add(labelBlock);
        var valueBlock = new TextBlock
        {
            Text = value,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 54,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        valueBlock.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        field.Children.Add(valueBlock);
        panel.Children.Add(field);
    }

    private static Control OutputBlock(string label, string text)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(Title(label));
        var block = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 120,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        block.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        panel.Children.Add(block);
        return panel;
    }

    private static TextBlock Title(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeight.SemiBold };
        block.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextSecondary"));
        return block;
    }

    private static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        block.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextSecondary"));
        return block;
    }

    private static TextBlock Text(string text, double size, string brush, FontWeight? weight = null, bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        };
        if (weight is { } val) block.FontWeight = val;
        block.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(brush));
        return block;
    }

    private static Border Divider()
    {
        var border = new Border { Height = 1 };
        border.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppDividerSubtle"));
        return border;
    }
}
