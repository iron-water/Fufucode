using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>选中 span 的结构化检查器：摘要、全部标签、事件/日志、原始 JSON。</summary>
internal sealed class PerfInspectorView : UserControl
{
    private readonly TextBlock _name = new() { FontSize = 13, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    private readonly TextBlock _meta = new() { FontSize = 10 };
    private readonly TextBlock _duration = new() { FontSize = 18, FontWeight = FontWeight.SemiBold };
    private readonly Border _statusChip;
    private readonly TextBlock _statusText = new() { FontSize = 9.5 };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 16 };
    private readonly ContentControl _content = new();
    private readonly ScrollViewer _scroll;
    private SpanRecord? _span;
    private IReadOnlyList<PerfEventRecord> _events = [];
    private string _tab = "摘要";

    public PerfInspectorView()
    {
        _name.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        _meta.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        _duration.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        _statusText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppSuccessForeground"));
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
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _name, _statusChip, _meta },
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        header.Children.Add(title);
        Grid.SetColumn(_duration, 1);
        header.Children.Add(_duration);

        var raw = TextButton("原始 JSON", () => SelectTab("原始"));
        var tabRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        tabRow.Children.Add(_tabs);
        Grid.SetColumn(raw, 1);
        tabRow.Children.Add(raw);

        _scroll = new ScrollViewer
        {
            Content = _content,
            MaxHeight = 330,
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
        SelectTab("摘要");
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

    private void BuildTabs()
    {
        _tabs.Children.Clear();
        foreach (var name in new[] { "摘要", "属性", "事件", "日志", "原始" })
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
            "属性" => RenderTags(_span),
            "事件" => RenderEvents("事件", errorOnly: false),
            "日志" => RenderEvents("日志", errorOnly: false),
            "原始" => RenderRaw(_span),
            _ => RenderSummary(_span),
        };
    }

    private Control RenderSummary(SpanRecord span)
    {
        var panel = new WrapPanel();
        AddField(panel, "开始", FormatStart(span));
        AddField(panel, "结束", span.EndUtc.ToLocalTime().ToString("HH:mm:ss.fff"));
        AddField(panel, "耗时", PerfPalette.FormatMs(span.DurationMs));
        AddField(panel, "类别", span.Category);
        AddField(panel, "状态", span.Status);
        AddField(panel, "父 span", span.ParentSpanId == 0 ? "根" : span.ParentSpanId.ToString("x"));
        AddField(panel, "线程", span.Tag("thread"));
        AddField(panel, "工具", PerfPalette.ToolDisplayName(span.Tag("tool")));
        AddField(panel, "命令", span.Tag("command"));
        AddField(panel, "工作目录", span.Tag("cwd"));
        AddField(panel, "退出码", span.Tag("exitCode"));
        AddField(panel, "超时", span.Tag("timedOut"));
        AddField(panel, "stdout", span.Tag("stdoutBytes") is { } stdout ? $"{stdout} B" : null);
        AddField(panel, "stderr", span.Tag("stderrBytes") is { } stderr ? $"{stderr} B" : null);
        AddField(panel, "丢弃输出", span.Tag("dropped") is { } dropped ? $"{dropped} B" : null);
        AddField(panel, "启动延迟", span.Tag("startMs") is { } startMs ? $"{startMs} ms" : null);
        AddField(panel, "审批等待", span.Tag("approvalMs") is { } approval ? $"{approval} ms" : null);
        AddField(panel, "TTFT", span.Tag("ttftMs") is { } ttft ? $"{ttft} ms" : null);
        AddField(panel, "流帧数", span.Tag("frames"));
        AddField(panel, "最大帧间隔", span.Tag("maxGapMs") is { } gap ? $"{gap} ms" : null);
        AddField(panel, "p95 帧间隔", span.Tag("p95GapMs") is { } p95 ? $"{p95} ms" : null);
        AddField(panel, "输入 Token", span.Tag("promptTokens"));
        AddField(panel, "输出 Token", span.Tag("completionTokens"));
        AddField(panel, "缓存 Token", span.Tag("cachedTokens"));
        AddField(panel, "错误", span.Error);
        if (!string.IsNullOrEmpty(span.Tag("detail"))) AddField(panel, "详情", span.Tag("detail"));
        return panel;
    }

    private Control RenderTags(SpanRecord span)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Title($"标签 · {span.Tags.Count}"));
        if (span.Tags.Count == 0)
        {
            panel.Children.Add(Muted("该 span 没有额外标签。"));
            return panel;
        }
        foreach (var (key, value) in span.Tags)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("116,*"), ColumnSpacing = 10 };
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

    private Control RenderEvents(string title, bool errorOnly)
    {
        var panel = new StackPanel { Spacing = 7 };
        panel.Children.Add(Title($"{title} · 选中时间范围"));
        var items = _events.Where(item => _span is not null &&
            item.TimestampTicks >= _span.StartTicks && item.TimestampTicks <= _span.EndTicks).ToArray();
        if (items.Length == 0)
        {
            panel.Children.Add(Muted("没有落在该 span 时间范围内的事件。"));
            return panel;
        }
        foreach (var item in items.Take(80))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("76,64,1*,Auto"), ColumnSpacing = 8 };
            var time = new TextBlock { Text = item.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff"), FontSize = 9.5 };
            time.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
            row.Children.Add(time);
            var kind = new TextBlock { Text = item.Kind, FontSize = 9.5 };
            kind.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextSecondary"));
            Grid.SetColumn(kind, 1);
            row.Children.Add(kind);
            var name = new TextBlock { Text = item.Name, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis };
            name.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
            Grid.SetColumn(name, 2);
            row.Children.Add(name);
            var value = new TextBlock { Text = item.Value.ToString("0.##", CultureInfo.InvariantCulture), FontSize = 9.5 };
            value.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
            Grid.SetColumn(value, 3);
            row.Children.Add(value);
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
        Append(builder, "traceId", span.TraceId, true);
        Append(builder, "spanId", span.SpanId, true);
        Append(builder, "parentSpanId", span.ParentSpanId, true);
        Append(builder, "name", span.Name, true);
        Append(builder, "status", span.Status, true);
        Append(builder, "startTicks", span.StartTicks, true);
        Append(builder, "endTicks", span.EndTicks, true);
        Append(builder, "durationMs", Math.Round(span.DurationMs, 3), true);
        Append(builder, "endUtc", span.EndUtc.ToString("O", CultureInfo.InvariantCulture), true);
        Append(builder, "error", span.Error, true);
        builder.AppendLine("  \"tags\": [");
        for (var index = 0; index < span.Tags.Count; index++)
        {
            var (key, value) = span.Tags[index];
            builder.Append("    { \"key\": ").Append(Json(key))
                .Append(", \"value\": ").Append(Json(value)).Append(" }");
            builder.AppendLine(index + 1 < span.Tags.Count ? "," : string.Empty);
        }
        builder.AppendLine("  ],");
        builder.AppendLine("  \"events\": [");
        var spanEvents = _events.Where(item => item.TimestampTicks >= span.StartTicks &&
                                               item.TimestampTicks <= span.EndTicks).Take(200).ToArray();
        for (var index = 0; index < spanEvents.Length; index++)
        {
            var item = spanEvents[index];
            builder.Append("    { \"kind\": ").Append(Json(item.Kind))
                .Append(", \"name\": ").Append(Json(item.Name))
                .Append(", \"value\": ").Append(item.Value.ToString("0.###", CultureInfo.InvariantCulture))
                .Append(", \"detail\": ").Append(Json(item.Detail))
                .Append(" }");
            builder.AppendLine(index + 1 < spanEvents.Length ? "," : string.Empty);
        }
        builder.AppendLine("  ]");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string key, long value, bool comma)
        => builder.Append("  \"").Append(key).Append("\": ").Append(value.ToString(CultureInfo.InvariantCulture))
            .AppendLine(comma ? "," : string.Empty);

    private static void Append(StringBuilder builder, string key, double value, bool comma)
        => builder.Append("  \"").Append(key).Append("\": ").Append(value.ToString("0.###", CultureInfo.InvariantCulture))
            .AppendLine(comma ? "," : string.Empty);

    private static void Append(StringBuilder builder, string key, string? value, bool comma)
        => builder.Append("  \"").Append(key).Append("\": ").Append(Json(value))
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
        _content.Content = Muted("选择一个 span 查看命令、调用、阻塞与原始数据。");
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

    private static Border Divider()
    {
        var border = new Border { Height = 1 };
        border.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppDividerSubtle"));
        return border;
    }

    private static Button TextButton(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 10,
            Padding = new Thickness(8, 5),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => action();
        return button;
    }
}
