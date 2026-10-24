using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// 概览：本地化还原 Magpie / Claude 上下文窗口看板。
/// 包含已用上下文/缓存命中、矩形树图（Treemap）、分类图例、按内容层级拆分进度条列表，以及底部会话历史曲线。
/// </summary>
internal sealed class PerfOverviewView : UserControl
{
    private readonly StackPanel _root = new() { Spacing = 14 };

    // 头部用量指标
    private readonly TextBlock _title = new() { Text = "上下文窗口", FontSize = 18, FontWeight = FontWeight.SemiBold };
    private readonly Border _modelBadge = new();
    private readonly TextBlock _modelText = new() { FontSize = 10 };
    private readonly TextBlock _usedTokensText = new() { FontSize = 28, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _usedTokensSub = new() { FontSize = 11 };
    private readonly TextBlock _cacheHitText = new() { FontSize = 28, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _cacheHitSub = new() { FontSize = 11 };

    // 矩形树图与图例
    private readonly PerfTreemapControl _treemap = new();
    private readonly WrapPanel _legendPanel = new() { Margin = new Thickness(0, 4, 0, 0) };

    // 内容明细表格
    private readonly StackPanel _contentPills = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel _contentItemsHost = new() { Spacing = 5 };
    private readonly Button _toggleShowAll = new();

    // 底部会话曲线
    private readonly TextBlock _breadcrumbText = new() { FontSize = 10.5 };
    private readonly TextBlock _roundsCountText = new() { FontSize = 11 };
    private readonly PerfSessionSparkline _sparkline = new();
    private readonly TextBlock _requestsCountText = new() { FontSize = 11 };

    private PerfSnapshot? _snapshot;
    private string _activeCategoryFilter = "all";
    private bool _showAllItems;

    public PerfOverviewView()
    {
        _title.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        _modelText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        _usedTokensText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        _usedTokensSub.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        _cacheHitText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        _cacheHitSub.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        _breadcrumbText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        _roundsCountText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextSecondary"));
        _requestsCountText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextSecondary"));

        // 模型徽章
        _modelBadge.CornerRadius = new CornerRadius(999);
        _modelBadge.Padding = new Thickness(8, 2);
        _modelBadge.VerticalAlignment = VerticalAlignment.Center;
        _modelBadge.Child = _modelText;
        _modelBadge.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppSubtleBackground"));

        var liveDot = new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(Color.Parse("#4ADE80")), VerticalAlignment = VerticalAlignment.Center };
        var liveText = new TextBlock { Text = "实时", FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        liveText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        var liveBadge = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center, Children = { liveDot, liveText } };

        var headRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 10 };
        headRow.Children.Add(_title);
        Grid.SetColumn(_modelBadge, 2);
        headRow.Children.Add(_modelBadge);
        Grid.SetColumn(liveBadge, 3);
        headRow.Children.Add(liveBadge);

        // 指标双列卡片
        var metricsGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        var usedBox = new StackPanel { Spacing = 3 };
        var usedLabel = Text("已用上下文", 11, "AppTextDim");
        usedBox.Children.Add(usedLabel);
        usedBox.Children.Add(_usedTokensText);
        usedBox.Children.Add(_usedTokensSub);

        var cacheBox = new StackPanel { Spacing = 3 };
        var cacheLabel = Text("提示词缓存", 11, "AppTextDim");
        cacheBox.Children.Add(cacheLabel);
        cacheBox.Children.Add(_cacheHitText);
        cacheBox.Children.Add(_cacheHitSub);

        metricsGrid.Children.Add(usedBox);
        Grid.SetColumn(cacheBox, 1);
        metricsGrid.Children.Add(cacheBox);

        var metricsCard = new Border
        {
            Padding = new Thickness(16, 14, 16, 14),
            CornerRadius = new CornerRadius(14),
            Child = metricsGrid,
        };
        BindCard(metricsCard);

        // 树图卡片
        var treemapInner = new StackPanel { Spacing = 10, Children = { _treemap, _legendPanel } };
        var treemapCard = new Border
        {
            Padding = new Thickness(16, 14, 16, 14),
            CornerRadius = new CornerRadius(14),
            Child = treemapInner,
        };
        BindCard(treemapCard);

        // 内容细分卡片
        var contentHead = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 0, 8) };
        var contentTitle = Text("内容", 13, "AppTextPrimary", FontWeight.SemiBold);
        contentHead.Children.Add(contentTitle);

        var pillsScroll = new ScrollViewer
        {
            Content = _contentPills,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 10),
        };

        _toggleShowAll.FontSize = 10.5;
        _toggleShowAll.Padding = new Thickness(8, 4);
        _toggleShowAll.Background = Brushes.Transparent;
        _toggleShowAll.BorderThickness = new Thickness(0);
        _toggleShowAll.HorizontalAlignment = HorizontalAlignment.Center;
        _toggleShowAll.Margin = new Thickness(0, 6, 0, 0);
        _toggleShowAll.Bind(Button.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        _toggleShowAll.Click += (_, _) =>
        {
            _showAllItems = !_showAllItems;
            RenderContentList();
        };

        var contentBody = new StackPanel { Spacing = 6, Children = { contentHead, pillsScroll, _contentItemsHost, _toggleShowAll } };
        var contentCard = new Border
        {
            Padding = new Thickness(16, 14, 16, 14),
            CornerRadius = new CornerRadius(14),
            Child = contentBody,
        };
        BindCard(contentCard);

        // 会话历史卡片（底部）
        var footerTopRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        footerTopRow.Children.Add(_breadcrumbText);
        Grid.SetColumn(_roundsCountText, 1);
        footerTopRow.Children.Add(_roundsCountText);

        var footerBottomRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, Margin = new Thickness(0, 6, 0, 0) };
        var sessionLabel = Text("本会话", 11.5, "AppTextPrimary", FontWeight.SemiBold);
        footerBottomRow.Children.Add(sessionLabel);
        Grid.SetColumn(_sparkline, 1);
        footerBottomRow.Children.Add(_sparkline);
        Grid.SetColumn(_requestsCountText, 2);
        footerBottomRow.Children.Add(_requestsCountText);

        var footerCard = new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(14),
            Child = new StackPanel { Spacing = 4, Children = { footerTopRow, footerBottomRow } },
        };
        BindCard(footerCard);

        _root.Children.Add(headRow);
        _root.Children.Add(metricsCard);
        _root.Children.Add(treemapCard);
        _root.Children.Add(contentCard);
        _root.Children.Add(footerCard);

        Content = new ScrollViewer
        {
            Content = _root,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Padding = new Thickness(16, 12, 16, 16),
        };

        _treemap.CategoryClicked += catKey =>
        {
            _activeCategoryFilter = catKey ?? "all";
            RenderPills();
            RenderContentList();
        };

        _sparkline.PointerPressed += (_, _) =>
        {
            if (_snapshot?.LatestTurn is { } turn) TurnSelected?.Invoke(turn.TraceId);
        };
        _sparkline.Cursor = new Cursor(StandardCursorType.Hand);
    }

    public event Action<long>? TurnSelected;

    public void Render(PerfSnapshot snapshot)
    {
        _snapshot = snapshot;
        var breakdown = snapshot.ContextBreakdown;

        _modelText.Text = string.IsNullOrWhiteSpace(breakdown.ModelName) ? "默认模型" : breakdown.ModelName;

        if (breakdown.TotalTokens > 0)
        {
            _usedTokensText.Text = $"{PerfPalette.FormatTokens(breakdown.TotalTokens)} token";
            _usedTokensSub.Text = breakdown.CapacityTokens is { } cap
                ? $"{PerfPalette.FormatTokens(breakdown.TotalTokens)} / {PerfPalette.FormatTokens(cap)} · {PerfPalette.FormatPercent((double)breakdown.TotalTokens / cap)}"
                : "模型的窗口大小未知";

            _cacheHitText.Text = breakdown.CachedTokens > 0
                ? $"{PerfPalette.FormatPercent(breakdown.CacheHitRate)} 命中"
                : "—— 命中";
            _cacheHitSub.Text = breakdown.CachedTokens > 0
                ? $"{PerfPalette.FormatTokens(breakdown.CachedTokens)} 缓存输入 · {PerfPalette.FormatTokens(breakdown.UncachedTokens)} 未命中"
                : "供应商回答后可见";
        }
        else
        {
            _usedTokensText.Text = "0 token";
            _usedTokensSub.Text = "模型的窗口大小未知";
            _cacheHitText.Text = "—— 命中";
            _cacheHitSub.Text = "供应商回答后可见";
        }

        // 渲染树图
        _treemap.SetData(breakdown.Categories, breakdown.TotalTokens);

        // 渲染图例
        RenderLegend(breakdown);

        // 渲染筛选按钮
        RenderPills();

        // 渲染内容细分列表
        RenderContentList();

        // 渲染底部会话曲线
        RenderFooter(snapshot);
    }

    private void RenderLegend(PerfContextBreakdown breakdown)
    {
        _legendPanel.Children.Clear();
        foreach (var cat in breakdown.Categories)
        {
            if (cat.TotalTokens <= 0) continue;
            var item = new Border
            {
                Padding = new Thickness(6, 3),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 8, 4),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            item.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppSubtleBackground"));

            var dot = new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(cat.Color), VerticalAlignment = VerticalAlignment.Center };
            var text = Text($"{cat.Name} {PerfPalette.FormatTokens(cat.TotalTokens)}", 10.5, "AppTextSecondary");
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { dot, text } };
            item.Child = row;

            var key = cat.Key;
            item.PointerPressed += (_, _) =>
            {
                _activeCategoryFilter = _activeCategoryFilter == key ? "all" : key;
                RenderPills();
                RenderContentList();
            };
            _legendPanel.Children.Add(item);
        }
    }

    private void RenderPills()
    {
        _contentPills.Children.Clear();
        var filterOptions = new (string Key, string Label)[]
        {
            ("all", "全部"),
            ("system", "系统提示词"),
            ("tools", "工具"),
            ("memory", "记忆"),
            ("files", "文件"),
            ("toolResults", "工具结果"),
            ("conversation", "会话内容"),
        };

        foreach (var (key, label) in filterOptions)
        {
            var active = _activeCategoryFilter == key;
            var btn = new Button
            {
                Content = label,
                FontSize = 10.5,
                Padding = new Thickness(10, 4),
                CornerRadius = new CornerRadius(999),
                Background = active ? new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)) : Brushes.Transparent,
                BorderThickness = new Thickness(active ? 1 : 0),
            };
            btn.Bind(Button.ForegroundProperty, new DynamicResourceExtension(active ? "AppTextPrimary" : "AppTextDim"));
            if (active) btn.Bind(Button.BorderBrushProperty, new DynamicResourceExtension("AppBorderBrush"));

            btn.Click += (_, _) =>
            {
                _activeCategoryFilter = key;
                RenderPills();
                RenderContentList();
            };
            _contentPills.Children.Add(btn);
        }
    }

    private void RenderContentList()
    {
        _contentItemsHost.Children.Clear();
        var breakdown = _snapshot?.ContextBreakdown;
        if (breakdown is null || breakdown.AllItems.Count == 0)
        {
            _contentItemsHost.Children.Add(Text("暂无内容项记录", 11, "AppTextDim"));
            _toggleShowAll.IsVisible = false;
            return;
        }

        var filtered = _activeCategoryFilter == "all"
            ? breakdown.AllItems
            : breakdown.AllItems.Where(i => i.CategoryKey == _activeCategoryFilter).ToList();

        if (filtered.Count == 0)
        {
            _contentItemsHost.Children.Add(Text("该类别下暂无条目", 11, "AppTextDim"));
            _toggleShowAll.IsVisible = false;
            return;
        }

        var maxTokens = Math.Max(1, filtered.Max(i => i.Tokens));
        var showCount = _showAllItems ? filtered.Count : Math.Min(8, filtered.Count);

        for (var i = 0; i < showCount; i++)
        {
            var item = filtered[i];
            _contentItemsHost.Children.Add(BuildContentRow(item, maxTokens));
        }

        if (filtered.Count > 8)
        {
            _toggleShowAll.IsVisible = true;
            _toggleShowAll.Content = _showAllItems ? "收起" : $"显示全部 {filtered.Count} 项";
        }
        else
        {
            _toggleShowAll.IsVisible = false;
        }
    }

    private Control BuildContentRow(PerfContextItem item, long maxTokens)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("12,*,Auto,90,54"),
            ColumnSpacing = 8,
            Height = 27,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var dot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(item.Color),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(dot);

        var nameText = new TextBlock
        {
            Text = item.Name,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        nameText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        Grid.SetColumn(nameText, 1);
        row.Children.Add(nameText);

        if (!string.IsNullOrEmpty(item.Tag))
        {
            var tagBadge = new Border
            {
                Padding = new Thickness(5, 1.5),
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = item.Tag,
                    FontSize = 9.5,
                    Foreground = new SolidColorBrush(PerfPalette.Neutral),
                },
            };
            tagBadge.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppSubtleBackground"));
            Grid.SetColumn(tagBadge, 2);
            row.Children.Add(tagBadge);
        }

        // 相对条
        var barTrack = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Border
            {
                Width = Math.Max(2, 90d * item.Tokens / maxTokens),
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(item.Color),
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        };
        barTrack.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppDividerSubtle"));
        Grid.SetColumn(barTrack, 3);
        row.Children.Add(barTrack);

        var valText = new TextBlock
        {
            Text = PerfPalette.FormatTokens(item.Tokens),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        valText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        Grid.SetColumn(valText, 4);
        row.Children.Add(valText);

        return row;
    }

    private void RenderFooter(PerfSnapshot snapshot)
    {
        var sessionName = _snapshot?.AllSpans.FirstOrDefault()?.SessionId ?? "sgline";
        var traceId = snapshot.LatestTurn?.TraceId ?? 0;
        _breadcrumbText.Text = $"Fufucode > {sessionName} > #{traceId:x}";
        _roundsCountText.Text = $"{snapshot.TurnCount} 轮";
        _requestsCountText.Text = $"{snapshot.ModelCalls} 个请求";

        // 更新历史折线图
        var turnTokens = snapshot.Turns.Select(t => (double)(t.PromptTokens + t.CompletionTokens)).ToList();
        _sparkline.SetData(turnTokens);
    }

    private static void BindCard(Border card)
    {
        card.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppCardBackground"));
        card.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("AppBorderBrush"));
        card.BorderThickness = new Thickness(1);
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
}

/// <summary>底部本会话 Token 趋势折线图</summary>
internal sealed class PerfSessionSparkline : Control
{
    private IReadOnlyList<double> _points = [];

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<PerfSessionSparkline, IBrush?>(nameof(LineBrush));

    public PerfSessionSparkline()
    {
        Height = 22;
        ClipToBounds = true;
        this.Bind(LineBrushProperty, new DynamicResourceExtension("AppAccentText"));
    }

    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    public void SetData(IReadOnlyList<double> points)
    {
        _points = points;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 10 || height <= 6) return;

        var brush = LineBrush ?? new SolidColorBrush(PerfPalette.Model);
        var pen = new Pen(brush, 1.5);

        if (_points.Count < 2)
        {
            // 单点或无数据：一条基准细线 + 尾部圆点
            context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)), 1),
                new Point(0, height / 2), new Point(width, height / 2));
            context.DrawEllipse(brush, null, new Point(width - 4, height / 2), 3, 3);
            return;
        }

        var max = Math.Max(1, _points.Max());
        var min = 0.0;
        var range = Math.Max(1, max - min);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i < _points.Count; i++)
            {
                var x = (width - 8) * (i / (double)(_points.Count - 1)) + 4;
                var y = height - 4 - ((_points[i] - min) / range * (height - 8));
                if (i == 0) ctx.BeginFigure(new Point(x, y), false);
                else ctx.LineTo(new Point(x, y));
            }
        }
        context.DrawGeometry(null, pen, geometry);

        // 终点高亮实心圆点
        var lastX = width - 4;
        var lastY = height - 4 - ((_points[^1] - min) / range * (height - 8));
        context.DrawEllipse(brush, null, new Point(lastX, lastY), 3.5, 3.5);
    }
}
