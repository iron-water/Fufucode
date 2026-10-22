using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>概览：只讲用户能理解的时长、Token、上下文、工具和轮次，不出现内部 span 名称。</summary>
internal sealed class PerfOverviewView : UserControl
{
    private readonly StackPanel _root = new() { Spacing = 14 };
    private readonly WrapPanel _kpis = new();
    private readonly Grid _donuts = new() { ColumnDefinitions = new("*,*"), ColumnSpacing = 14 };
    private readonly Grid _lower = new() { ColumnDefinitions = new("*,*"), ColumnSpacing = 14 };
    private readonly Border _tokenCard;
    private readonly Border _timeCard;
    private readonly Border _contextCard;
    private readonly Border _toolsCard;
    private readonly StackPanel _tokenBody;
    private readonly StackPanel _timeBody;
    private readonly StackPanel _contextBody;
    private readonly StackPanel _toolsBody;
    private readonly Border _turnCard;
    private readonly StackPanel _turnBody;
    private readonly PerfDonut _tokenDonut = new();
    private readonly PerfDonut _timeDonut = new();
    private readonly PerfSegmentedBar _contextBar = new(10);
    private PerfSnapshot? _snapshot;
    private bool _narrow;

    public PerfOverviewView()
    {
        (_tokenCard, _tokenBody) = MakeCard("Token 构成");
        (_timeCard, _timeBody) = MakeCard("时间花在哪");
        (_contextCard, _contextBody) = MakeCard("上下文窗口");
        (_toolsCard, _toolsBody) = MakeCard("工具使用");
        (_turnCard, _turnBody) = MakeCard("轮次时间线");

        _donuts.Children.Add(_tokenCard);
        Grid.SetColumn(_timeCard, 1);
        _donuts.Children.Add(_timeCard);
        _lower.Children.Add(_contextCard);
        Grid.SetColumn(_toolsCard, 1);
        _lower.Children.Add(_toolsCard);

        _root.Children.Add(_kpis);
        _root.Children.Add(_donuts);
        _root.Children.Add(_lower);
        _root.Children.Add(_turnCard);
        Content = new ScrollViewer
        {
            Content = _root,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        SizeChanged += (_, _) => UpdateColumns();
    }

    public event Action<long>? TurnSelected;

    public void Render(PerfSnapshot snapshot)
    {
        _snapshot = snapshot;
        RenderKpis(snapshot);
        RenderToken(snapshot);
        RenderTime(snapshot);
        RenderContext(snapshot);
        RenderTools(snapshot);
        RenderTurns(snapshot);
        UpdateColumns();
    }

    private void UpdateColumns()
    {
        var narrow = Bounds.Width is > 0 and < 860;
        if (narrow == _narrow) return;
        _narrow = narrow;
        _donuts.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,*");
        _donuts.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "Auto");
        Grid.SetColumn(_tokenCard, 0);
        Grid.SetRow(_tokenCard, 0);
        Grid.SetColumn(_timeCard, narrow ? 0 : 1);
        Grid.SetRow(_timeCard, narrow ? 1 : 0);
        _lower.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,*");
        _lower.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "Auto");
        Grid.SetColumn(_contextCard, 0);
        Grid.SetRow(_contextCard, 0);
        Grid.SetColumn(_toolsCard, narrow ? 0 : 1);
        Grid.SetRow(_toolsCard, narrow ? 1 : 0);
    }

    private void RenderKpis(PerfSnapshot snapshot)
    {
        _kpis.Children.Clear();
        _kpis.Children.Add(Kpi("总会话时长", PerfPalette.FormatDuration(snapshot.WallDuration),
            $"{snapshot.TurnCount} 轮 · 最近轮次 {PerfPalette.FormatMs(snapshot.LatestTurn?.DurationMs ?? 0)}"));
        _kpis.Children.Add(Kpi("总 Token", PerfPalette.FormatTokens(snapshot.TotalTokens),
            $"{PerfPalette.FormatPercent(snapshot.CacheHitRate)} 来自缓存"));
        _kpis.Children.Add(Kpi("平均轮次", PerfPalette.FormatMs(snapshot.AverageTurnMs),
            $"共 {snapshot.TurnCount} 轮"));
        _kpis.Children.Add(Kpi("工具调用", snapshot.ToolCalls.ToString("N0"),
            $"{snapshot.ModelCalls:N0} 次模型调用 · {snapshot.ErrorCount} 个错误"));
    }

    private void RenderToken(PerfSnapshot snapshot)
    {
        _tokenBody.Children.Clear();
        var cached = snapshot.CachedTokens;
        var prompt = snapshot.AllSpans.Sum(span => span.LongTag("promptTokens") ?? 0);
        var completion = snapshot.AllSpans.Sum(span => span.LongTag("completionTokens") ?? 0);
        var uncached = Math.Max(0, prompt - cached);
        var total = cached + uncached + completion;
        if (total <= 0)
        {
            _tokenBody.Children.Add(Empty("运行一次模型调用后显示 Token 构成。"));
            return;
        }

        _tokenDonut.SetData(
        [
            new PerfDonutSegment(cached, PerfPalette.Tool),
            new PerfDonutSegment(uncached, PerfPalette.Model),
            new PerfDonutSegment(completion, PerfPalette.Ui),
        ], PerfPalette.FormatPercent(snapshot.CacheHitRate), "缓存命中");

        var legend = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        legend.Children.Add(Legend(PerfPalette.Tool, "缓存输入", cached, cached / total));
        legend.Children.Add(Legend(PerfPalette.Model, "未命中输入", uncached, uncached / total));
        legend.Children.Add(Legend(PerfPalette.Ui, "输出（含思考）", completion, completion / total));
        _tokenBody.Children.Add(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 18,
            Children = { _tokenDonut, legend },
        });
    }

    private void RenderTime(PerfSnapshot snapshot)
    {
        _timeBody.Children.Clear();
        var model = snapshot.TotalModelMs;
        var tools = snapshot.TotalToolMs;
        var total = model + tools;
        if (total <= 0)
        {
            _timeBody.Children.Add(Empty("运行一个回合后显示模型与工具的耗时构成。"));
            return;
        }

        _timeDonut.SetData(
        [
            new PerfDonutSegment(model, PerfPalette.Model),
            new PerfDonutSegment(tools, PerfPalette.Tool),
        ], PerfPalette.FormatCompactMs(total), "总耗时");

        var legend = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        legend.Children.Add(Legend(PerfPalette.Model, $"模型调用 · {snapshot.ModelCalls:N0} 次", model, model / (double)total));
        legend.Children.Add(Legend(PerfPalette.Tool, $"工具执行 · {snapshot.ToolCalls:N0} 次", tools, tools / (double)total));
        _timeBody.Children.Add(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 18,
            Children = { _timeDonut, legend },
        });
    }

    private void RenderContext(PerfSnapshot snapshot)
    {
        _contextBody.Children.Clear();
        var context = snapshot.Context;
        if (!context.HasData)
        {
            _contextBody.Children.Add(Empty("运行一次模型调用后显示最近请求的上下文用量。"));
            return;
        }

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(Text(PerfPalette.FormatTokens(context.PromptTokens), 22, "AppTextPrimary", FontWeight.SemiBold));
        var trailing = Text($"缓存 {PerfPalette.FormatTokens(context.CachedTokens)} · 未命中 {PerfPalette.FormatTokens(context.UncachedTokens)}",
            10, "AppTextSecondary");
        Grid.SetColumn(trailing, 1);
        header.Children.Add(trailing);
        _contextBody.Children.Add(header);

        _contextBar.SetData(
        [
            new PerfBarSegment(context.CachedTokens, PerfPalette.Tool),
            new PerfBarSegment(context.UncachedTokens, PerfPalette.Model),
        ]);
        _contextBody.Children.Add(_contextBar);
        _contextBody.Children.Add(Text("最近一次请求的输入 Token；上限由模型或端点决定，本页不做猜测。",
            10, "AppTextDim", null, true));
    }

    private void RenderTools(PerfSnapshot snapshot)
    {
        _toolsBody.Children.Clear();
        if (snapshot.Tools.Count == 0)
        {
            _toolsBody.Children.Add(Empty("还没有工具调用记录。"));
            return;
        }

        var max = Math.Max(1, snapshot.Tools.Max(tool => tool.Count));
        foreach (var tool in snapshot.Tools.Take(6))
            _toolsBody.Children.Add(ToolRow(tool, max));
    }

    private void RenderTurns(PerfSnapshot snapshot)
    {
        _turnBody.Children.Clear();
        var turns = snapshot.Turns.TakeLast(3).ToArray();
        if (turns.Length == 0)
        {
            _turnBody.Children.Add(Empty("还没有完成的回合。"));
            return;
        }

        foreach (var turn in turns)
            _turnBody.Children.Add(TurnBlock(turn));
    }

    private Control TurnBlock(PerfTurnInfo turn)
    {
        var body = new StackPanel { Spacing = 8 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        header.Children.Add(Text(turn.TimeLabel, 11.5, "AppTextPrimary"));
        var duration = Text(PerfPalette.FormatMs(turn.DurationMs), 11, "AppTextSecondary");
        Grid.SetColumn(duration, 1);
        header.Children.Add(duration);
        var chip = StatusChip(turn.Errors > 0 ? "有错误" : turn.Outcome, turn.Errors > 0);
        Grid.SetColumn(chip, 3);
        header.Children.Add(chip);
        body.Children.Add(header);

        var bar = new PerfSegmentedBar(4);
        var other = Math.Max(0, turn.DurationMs - turn.ModelMs - turn.ToolMs);
        bar.SetData(
        [
            new PerfBarSegment(turn.ModelMs, PerfPalette.Model),
            new PerfBarSegment(turn.ToolMs, PerfPalette.Tool),
            new PerfBarSegment(other, PerfPalette.Neutral),
        ]);
        body.Children.Add(bar);

        foreach (var span in turn.SignificantChildren.Take(4))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("10,*,Auto") };
            var dot = new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = new SolidColorBrush(turn.Errors > 0 ? PerfPalette.Error : PerfPalette.CategoryColor(span.Category)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(dot);
            var name = span.Name == "model.request" || span.Name == "model.stream" || span.Name == "model.http"
                ? turn.Root?.Tag("model") ?? "模型调用"
                : span.Name.StartsWith("tool", StringComparison.Ordinal)
                    ? PerfPalette.ToolDisplayName(span.Tag("tool"))
                    : span.Name;
            var label = Text(name, 11, "AppTextSecondary");
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var value = Text(PerfPalette.FormatMs(span.DurationMs), 10.5, "AppTextDim");
            Grid.SetColumn(value, 2);
            row.Children.Add(value);
            body.Children.Add(row);
        }

        if (turn.SignificantChildren.Count > 4)
            body.Children.Add(Text($"+ {turn.SignificantChildren.Count - 4} 个模型/工具节点", 10, "AppTextDim"));

        var card = new Border
        {
            Padding = new Thickness(11, 10),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 0, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = body,
        };
        card.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppSubtleBackground"));
        card.PointerPressed += (_, _) => TurnSelected?.Invoke(turn.TraceId);
        return card;
    }

    private static Grid ToolRow(PerfToolUsage tool, int max)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("10,1*,46,90,56"),
            ColumnSpacing = 9,
            Height = 24,
        };
        row.Children.Add(new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = new SolidColorBrush(PerfPalette.Tool),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var name = Text(tool.Name, 11.5, "AppTextPrimary");
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        var count = Text(tool.Count.ToString("N0"), 11, "AppTextSecondary");
        count.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(count, 2);
        row.Children.Add(count);

        var track = new Border
        {
            Height = 5,
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Border
            {
                Width = Math.Max(2, 90d * tool.Count / max),
                Height = 5,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(PerfPalette.Tool),
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        };
        track.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppDividerSubtle"));
        Grid.SetColumn(track, 3);
        row.Children.Add(track);

        var avg = Text(PerfPalette.FormatCompactMs(tool.AverageMs), 10.5, "AppTextDim");
        avg.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(avg, 4);
        row.Children.Add(avg);
        return row;
    }

    private static Control Legend(Color color, string label, long value, double fraction)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("12,*,Auto"), ColumnSpacing = 8 };
        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(dot);
        var labelText = Text(label, 11.5, "AppTextSecondary");
        Grid.SetColumn(labelText, 1);
        grid.Children.Add(labelText);
        var valueText = Text($"{PerfPalette.FormatTokens(value)}  {PerfPalette.FormatPercent(fraction)}",
            11, "AppTextPrimary");
        valueText.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(valueText, 2);
        grid.Children.Add(valueText);
        return grid;
    }

    private static Border Kpi(string label, string value, string sub)
    {
        var body = new StackPanel { Spacing = 5 };
        body.Children.Add(Text(label, 10.5, "AppTextSecondary"));
        body.Children.Add(Text(value, 23, "AppTextPrimary", FontWeight.SemiBold));
        body.Children.Add(Text(sub, 10.5, "AppTextDim", null, true));
        var border = new Border
        {
            Width = 214,
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(0, 0, 10, 10),
            Child = body,
        };
        border.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppCardBackground"));
        border.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("AppBorderBrush"));
        border.BorderThickness = new Thickness(1);
        return border;
    }

    private static (Border Card, StackPanel Body) MakeCard(string title)
    {
        var content = new StackPanel { Spacing = 12 };
        var outer = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Text(title, 13, "AppTextPrimary", FontWeight.SemiBold),
                content,
            },
        };
        var card = new Border
        {
            Padding = new Thickness(16, 14, 16, 16),
            CornerRadius = new CornerRadius(14),
            Child = outer,
        };
        card.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppCardBackground"));
        card.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("AppBorderBrush"));
        card.BorderThickness = new Thickness(1);
        return (card, content);
    }

    private static TextBlock Empty(string text)
    {
        var block = Text(text, 11.5, "AppTextSecondary", null, true);
        block.Margin = new Thickness(0, 6, 0, 6);
        return block;
    }

    private static Border StatusChip(string text, bool error)
    {
        var block = Text(text, 9.5, error ? "AppCriticalForeground" : "AppSuccessForeground");
        var chip = new Border
        {
            Padding = new Thickness(7, 2),
            CornerRadius = new CornerRadius(999),
            Child = block,
            VerticalAlignment = VerticalAlignment.Center,
        };
        chip.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppSubtleBackground"));
        return chip;
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
        if (weight is { } value) block.FontWeight = value;
        block.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(brush));
        return block;
    }
}
