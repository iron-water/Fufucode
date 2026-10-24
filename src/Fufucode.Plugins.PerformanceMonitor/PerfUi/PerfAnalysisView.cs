using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>分析：全局总览、Span 树、共享时间线、结构化检查器四层联动。</summary>
internal sealed class PerfAnalysisView : UserControl
{
    private readonly Grid _root = new() { RowDefinitions = new RowDefinitions("Auto,*") };
    private readonly StackPanel _overview = new() { Spacing = 8 };
    private readonly PerfOverviewStrip _strip = new();
    private readonly PerfSpanTreeView _tree = new();
    private readonly PerfTimelineView _timeline = new();
    private readonly PerfInspectorView _inspector = new();
    private readonly Grid _split = new() { ColumnDefinitions = new ColumnDefinitions("330,*"), ColumnSpacing = 14 };
    private readonly StackPanel _right = new() { Spacing = 14 };
    private readonly Border _timelineCard;
    private readonly TextBlock _timelineTitle = new() { FontSize = 11.5, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _timelineMeta = new() { FontSize = 10 };
    private readonly TextBlock _overviewMeta = new() { FontSize = 10 };
    private readonly TextBlock _empty = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private PerfSnapshot? _snapshot;
    private PerfTurnInfo? _turn;
    private long _selectedTraceId;
    private long _selectedSpanId;
    private bool _narrow;

    public PerfAnalysisView()
    {
        _timelineTitle.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        _timelineMeta.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        _overviewMeta.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        _empty.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextSecondary"));

        var overviewTitle = new TextBlock { Text = "全局概览", FontSize = 13, FontWeight = FontWeight.SemiBold };
        overviewTitle.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        var overviewHead = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        overviewHead.Children.Add(overviewTitle);
        Grid.SetColumn(_overviewMeta, 1);
        overviewHead.Children.Add(_overviewMeta);
        _overview.Children.Add(overviewHead);
        _overview.Children.Add(_strip);
        var overviewCard = new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 12),
            Child = _overview,
        };
        BindCard(overviewCard);

        var timeline = MakeTimelineCard();
        _timelineCard = timeline.Card;
        _right.Children.Add(_timelineCard);
        _right.Children.Add(_inspector);
        _split.Children.Add(_tree);
        Grid.SetColumn(_right, 1);
        _split.Children.Add(_right);

        _root.Children.Add(overviewCard);
        Grid.SetRow(_split, 1);
        _root.Children.Add(_split);
        _root.Children.Add(_empty);
        Grid.SetRow(_empty, 1);
        _empty.Margin = new Thickness(20);

        Content = new ScrollViewer
        {
            Content = _root,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        _strip.TurnSelected += traceId =>
        {
            if (_snapshot?.FindTurn(traceId) is not { } turn) return;
            ShowTurn(turn);
            _timeline.ResetView();
        };
        _tree.SpanSelected += span =>
        {
            _selectedSpanId = span.SpanId;
            _timeline.SelectSpan(span.SpanId);
            _inspector.SetSpan(span, _snapshot?.Events ?? []);
        };
        _timeline.SpanSelected += span =>
        {
            _selectedSpanId = span.SpanId;
            _tree.SelectSpan(span.SpanId, raiseEvent: false);
            _inspector.SetSpan(span, _snapshot?.Events ?? []);
        };
        SizeChanged += (_, _) => UpdateLayoutForWidth();
    }

    public void ShowTrace(long traceId)
    {
        if (_snapshot?.FindTurn(traceId) is not { } turn) return;
        ShowTurn(turn);
        _timeline.ResetView();
    }

    public void SetSnapshot(PerfSnapshot snapshot)
    {
        _snapshot = snapshot;
        var turns = snapshot.Turns
            .Concat(snapshot.Startup is { } startup ? new[] { startup } : Array.Empty<PerfTurnInfo>())
            .OrderBy(turn => turn.StartUtc)
            .ToArray();
        _strip.SetData(turns, _selectedTraceId);
        _overviewMeta.Text =
            $"启动 {PerfPalette.FormatMs(snapshot.Startup?.DurationMs ?? 0)} · {snapshot.TurnCount} 轮 · " +
            $"{snapshot.AllSpans.Count:N0} span · {snapshot.StallCount} 次阻塞";
        _empty.IsVisible = snapshot.AllSpans.Count == 0 || turns.Length == 0;
        _split.IsVisible = snapshot.AllSpans.Count > 0 && turns.Length > 0;
        if (snapshot.AllSpans.Count == 0 || turns.Length == 0)
        {
            _empty.Text = "还没有性能数据。运行一个回合后，这里会出现启动、每次命令、模型调用与阻塞。";
            return;
        }

        var current = _selectedTraceId != 0 ? snapshot.FindTurn(_selectedTraceId) : null;
        ShowTurn(current ?? snapshot.LatestSession ?? turns[0]);
        UpdateLayoutForWidth();
    }

    private void ShowTurn(PerfTurnInfo turn)
    {
        var changed = _turn?.TraceId != turn.TraceId;
        _turn = turn;
        _selectedTraceId = turn.TraceId;
        if (changed) _selectedSpanId = 0;
        var strip = _snapshot?.Turns
            .Concat(_snapshot.Startup is { } startup ? new[] { startup } : Array.Empty<PerfTurnInfo>())
            .OrderBy(item => item.StartUtc).ToArray() ?? Array.Empty<PerfTurnInfo>();
        _strip.SetData(strip, turn.TraceId);
        _tree.SetTurn(turn);
        _timeline.SetTurn(turn, _snapshot?.HostSamples ?? [], _selectedSpanId);
        _timelineTitle.Text = turn.IsStartup ? $"启动 · {PerfPalette.FormatMs(turn.DurationMs)}"
            : $"回合 {turn.TimeLabel} · {PerfPalette.FormatMs(turn.DurationMs)}";
        _timelineMeta.Text = turn.IsStartup
            ? $"{turn.Spans.Count:N0} 个 span"
            : $"模型 {turn.ModelCalls} · 工具 {turn.ToolCalls} · 审批 {turn.ApprovalSpans.Count} · 持久化 {turn.PersistSpans.Count}";
        if (_selectedSpanId != 0 && turn.Spans.FirstOrDefault(span => span.SpanId == _selectedSpanId) is { } selected)
        {
            _tree.SelectSpan(selected.SpanId, raiseEvent: false);
            _timeline.SelectSpan(selected.SpanId);
            _inspector.SetSpan(selected, _snapshot?.Events ?? []);
        }
        else
        {
            _selectedSpanId = 0;
            _inspector.SetSpan(null, _snapshot?.Events ?? []);
        }
    }

    private (Border Card, StackPanel Body) MakeTimelineCard()
    {
        var body = new StackPanel { Spacing = 8 };
        var left = new StackPanel
        {
            Spacing = 2,
            Children = { _timelineTitle, _timelineMeta },
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var fit = TextButton("适应", () => _timeline.ResetView());
        var latest = TextButton("最新", () =>
        {
            if (_snapshot?.LatestSession is { } turn) ShowTurn(turn);
        });
        actions.Children.Add(fit);
        actions.Children.Add(latest);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(left);
        Grid.SetColumn(actions, 1);
        head.Children.Add(actions);
        body.Children.Add(head);
        body.Children.Add(_timeline);
        var card = new Border
        {
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(6),
            Child = body,
        };
        BindCard(card);
        return (card, body);
    }

    private void UpdateLayoutForWidth()
    {
        var narrow = Bounds.Width is > 0 and < 900;
        if (narrow == _narrow) return;
        _narrow = narrow;
        _split.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "330,*");
        _split.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "Auto");
        Grid.SetColumn(_tree, 0);
        Grid.SetRow(_tree, 0);
        Grid.SetColumn(_right, narrow ? 0 : 1);
        Grid.SetRow(_right, narrow ? 1 : 0);
        _right.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        _tree.SetCompact(narrow);
    }

    private static void BindCard(Border card)
    {
        card.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppCardBackground"));
        card.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("AppBorderBrush"));
        card.BorderThickness = new Thickness(1);
    }

    private static Button TextButton(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 10,
            Padding = new Thickness(7, 4),
        };
        button.Click += (_, _) => action();
        return button;
    }
}
