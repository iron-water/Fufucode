using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Fufucode.Plugins.PerformanceMonitor;

internal sealed class PerfSpanNode
{
    public required SpanRecord Span { get; init; }
    public PerfSpanNode? Parent { get; set; }
    public List<PerfSpanNode> Children { get; } = [];
    public int Depth { get; set; }
    public bool IsExpanded { get; set; }
    public string DisplayName { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

internal sealed class PerfSpanTreeRow
{
    public required PerfSpanNode Node { get; init; }
    public SpanRecord Span => Node.Span;
    public string Name => Node.DisplayName;
    public string Detail => Node.Detail;
    public string Duration => PerfPalette.FormatMs(Span.DurationMs);
    public string Status => Span.Name.Contains("stall", StringComparison.OrdinalIgnoreCase)
        ? "warn"
        : Span.Status switch { "Error" => "err", "Cancelled" => "cancel", _ => "ok" };
    public IBrush StatusBrush => Span.Name.Contains("stall", StringComparison.OrdinalIgnoreCase)
        ? PerfPalette.ApprovalBrush
        : Span.Status switch
        {
            "Error" => PerfPalette.ErrorBrush,
            "Cancelled" => PerfPalette.NeutralBrush,
            _ => PerfPalette.ToolBrush,
        };
    public string Chevron => Node.Children.Count == 0 ? string.Empty : Node.IsExpanded ? "▾" : "▸";
    public double Indent => Node.Depth * 12;

    public string LangfuseKind => Span.Name switch
    {
        var n when n.StartsWith("model.", StringComparison.Ordinal) => "GEN",
        var n when n.Contains("stall") || n.Contains("approval") || n.StartsWith("event") => "EVENT",
        var n when n.StartsWith("anomaly") || n.Contains("error") => "SCORE",
        _ => "SPAN",
    };

    public IBrush KindBrush => LangfuseKind switch
    {
        "GEN" => PerfPalette.ModelBrush,
        "EVENT" => PerfPalette.ApprovalBrush,
        "SCORE" => PerfPalette.ErrorBrush,
        _ => PerfPalette.ToolBrush,
    };
}

/// <summary>把一次 trace 展开成可按层级折叠的 Span 行；过滤时保留命中节点及其祖先。</summary>
internal static class PerfTreeBuilder
{
    public static List<PerfSpanNode> Build(PerfTurnInfo turn, Func<SpanRecord, bool>? filter)
    {
        var spans = turn.Spans;
        if (spans.Count == 0) return [];

        var byId = spans.GroupBy(span => span.SpanId).ToDictionary(group => group.Key, group => group.First());
        var root = turn.Root is not null && byId.ContainsKey(turn.Root.SpanId)
            ? byId[turn.Root.SpanId]
            : spans.FirstOrDefault(span => span.ParentSpanId == 0) ?? spans[0];

        var nodes = new Dictionary<long, PerfSpanNode>();
        foreach (var span in spans)
        {
            nodes.TryAdd(span.SpanId, new PerfSpanNode
            {
                Span = span,
                DisplayName = DisplayName(turn, span),
                Detail = Detail(span),
            });
        }

        var rootNode = nodes[root.SpanId];
        foreach (var span in spans)
        {
            if (span.SpanId == root.SpanId) continue;
            if (!nodes.TryGetValue(span.SpanId, out var node)) continue;
            var parent = span.ParentSpanId != 0 && nodes.TryGetValue(span.ParentSpanId, out var found) &&
                         !ReferenceEquals(found, node)
                ? found
                : rootNode;
            node.Parent = parent;
            parent.Children.Add(node);
        }

        foreach (var node in nodes.Values)
            node.Children.Sort((a, b) => a.Span.StartTicks.CompareTo(b.Span.StartTicks));

        SetDepth(rootNode, 0, new HashSet<long>());
        if (filter is not null) ApplyFilter(rootNode, filter);
        return [rootNode];
    }

    private static void SetDepth(PerfSpanNode node, int depth, HashSet<long> visited)
    {
        if (!visited.Add(node.Span.SpanId) || depth > 64) return;
        node.Depth = depth;
        if (!node.IsExpanded && depth <= 3) node.IsExpanded = true;
        foreach (var child in node.Children) SetDepth(child, depth + 1, visited);
    }

    private static bool ApplyFilter(PerfSpanNode node, Func<SpanRecord, bool> filter)
    {
        var children = node.Children.ToArray();
        var kept = new List<PerfSpanNode>(children.Length);
        var self = filter(node.Span);
        foreach (var child in children)
        {
            if (self)
            {
                child.IsExpanded = true;
                kept.Add(child);
                ExpandAll(child);
                continue;
            }
            if (ApplyFilter(child, filter)) kept.Add(child);
        }
        node.Children.Clear();
        node.Children.AddRange(kept);
        if (kept.Count > 0) node.IsExpanded = true;
        return self || kept.Count > 0;
    }

    private static void ExpandAll(PerfSpanNode node)
    {
        node.IsExpanded = true;
        foreach (var child in node.Children) ExpandAll(child);
    }

    private static string DisplayName(PerfTurnInfo turn, SpanRecord span)
    {
        if (span.SpanId == turn.Root?.SpanId) return turn.IsStartup ? "启动" : $"轮次 {turn.TimeLabel}";
        if (span.Name == "tool" || span.Name is "tool.process" or "tool.glob" or "tool.approval" ||
            span.Name == "tool.batch")
        {
            return span.Name switch
            {
                "tool.batch" => "工具批",
                "tool.approval" => "审批",
                _ => PerfPalette.ToolDisplayName(span.Tag("tool")),
            };
        }
        if (span.Name is "model.request" or "model.http" or "model.stream") return span.Name;
        return span.Name;
    }

    private static string Detail(SpanRecord span) => span.Name switch
    {
        "tool" => span.Tag("tool") ?? string.Empty,
        "tool.process" => span.Tag("command") ?? string.Empty,
        "tool.glob" => span.Tag("tool") ?? string.Empty,
        "tool.approval" => span.Tag("tool") ?? string.Empty,
        "tool.batch" => span.Tag("count") is { } count ? $"{count} 个工具" : string.Empty,
        "model.stream" => span.Tag("ttftMs") is { } ttft ? $"TTFT {ttft}ms" : string.Empty,
        "model.http" => span.Tag("mode") ?? string.Empty,
        "context.build" => span.Tag("messages") is { } messages ? $"{messages} 条消息" : string.Empty,
        "ui.stall" => span.Tag("detail") ?? string.Empty,
        "error.unhandled" => span.Tag("detail") ?? span.Error ?? string.Empty,
        "startup" => span.Tag("milestones") is { } milestones ? $"{milestones} 个里程碑" : string.Empty,
        _ => string.Empty,
    };
}

/// <summary>虚拟化 Span 树：搜索、折叠、选中联动；所有行用一个 ListBox 渲染。</summary>
internal sealed class PerfSpanTreeView : UserControl
{
    private readonly ListBox _list = new() { Background = Brushes.Transparent };
    private readonly TextBox _search = new()
    {
        PlaceholderText = "过滤 span / 命令 / 工具…",
        MinWidth = 116,
        Height = 28,
        FontSize = 11,
    };
    private readonly TextBlock _count = new() { FontSize = 10 };
    private readonly StackPanel _rows = new();
    private readonly Dictionary<long, bool> _overrides = new();
    private PerfTurnInfo? _turn;
    private List<PerfSpanTreeRow> _visible = [];
    private long _selectedSpanId;
    private bool _rebuilding;

    public PerfSpanTreeView()
    {
        _count.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextSecondary"));
        _search.TextChanged += (_, _) => RebuildRows();
        _list.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || _list.SelectedItem is not PerfSpanTreeRow row) return;
            _selectedSpanId = row.Span.SpanId;
            SpanSelected?.Invoke(row.Span);
        };
        _list.ItemTemplate = new FuncDataTemplate<PerfSpanTreeRow>((row, _) => BuildRow(row), true);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 8 };
        var title = new TextBlock { Text = "Span 树", FontSize = 13, FontWeight = FontWeight.SemiBold };
        title.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        header.Children.Add(title);
        Grid.SetColumn(_count, 1);
        header.Children.Add(_count);
        var searchHost = new Border { Child = _search, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(searchHost, 3);
        header.Children.Add(searchHost);

        var columnHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("18,*,66,48"),
            ColumnSpacing = 7,
            Margin = new Thickness(8, 4, 8, 4),
        };
        AddHeader(columnHeader, "名称", 0);
        AddHeader(columnHeader, "耗时", 2);
        AddHeader(columnHeader, "状态", 3);

        _rows.Children.Add(header);
        _rows.Children.Add(columnHeader);
        _rows.Children.Add(_list);
        Content = new Border
        {
            Padding = new Thickness(12, 12, 12, 12),
            CornerRadius = new CornerRadius(6),
            Child = _rows,
        };
        if (Content is Border border)
        {
            border.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppCardBackground"));
            border.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("AppBorderBrush"));
            border.BorderThickness = new Thickness(1);
        }
        _list.MinHeight = 180;
        _list.MaxHeight = 560;
    }

    public event Action<SpanRecord>? SpanSelected;

    public void SetCompact(bool compact)
    {
        _list.MaxHeight = compact ? 300 : 560;
    }

    public void SetTurn(PerfTurnInfo turn)
    {
        var changed = _turn?.TraceId != turn.TraceId;
        _turn = turn;
        if (changed) _selectedSpanId = 0;
        RebuildRows();
    }

    public void SelectSpan(long spanId, bool raiseEvent = true)
    {
        _selectedSpanId = spanId;
        if (_turn is null) return;
        var node = FindNode(spanId);
        if (node is not null)
        {
            for (var parent = node.Parent; parent is not null; parent = parent.Parent)
                _overrides[parent.Span.SpanId] = true;
            RebuildRows(raiseEvent);
        }
    }

    private PerfSpanNode? FindNode(long spanId)
    {
        if (_turn is null) return null;
        var roots = PerfTreeBuilder.Build(_turn, null);
        var stack = new Stack<PerfSpanNode>(roots);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Span.SpanId == spanId) return node;
            foreach (var child in node.Children) stack.Push(child);
        }
        return null;
    }

    private void RebuildRows(bool raiseEvent = false)
    {
        if (_turn is null) return;
        var filter = string.IsNullOrWhiteSpace(_search.Text) ? null : new Func<SpanRecord, bool>(Matches);
        // 用旧树保留用户的展开/折叠选择；搜索时强制展开命中路径。
        var roots = PerfTreeBuilder.Build(_turn, filter);
        ApplyOverrides(roots);
        var rows = new List<PerfSpanTreeRow>();
        foreach (var root in roots) Flatten(root, rows);
        _visible = rows;

        _rebuilding = true;
        _list.ItemsSource = rows;
        var selected = rows.FirstOrDefault(row => row.Span.SpanId == _selectedSpanId);
        _list.SelectedItem = selected;
        if (selected is not null) _list.ScrollIntoView(selected);
        _rebuilding = false;
        _count.Text = $"{rows.Count:N0} / {_turn.Spans.Count:N0}";
        if (raiseEvent && selected is not null) SpanSelected?.Invoke(selected.Span);
    }

    private void ApplyOverrides(IEnumerable<PerfSpanNode> roots)
    {
        var stack = new Stack<PerfSpanNode>(roots);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (_overrides.TryGetValue(node.Span.SpanId, out var expanded)) node.IsExpanded = expanded;
            foreach (var child in node.Children) stack.Push(child);
        }
    }

    private bool Matches(SpanRecord span)
    {
        var query = _search.Text?.Trim() ?? string.Empty;
        if (query.Length == 0) return true;
        if (span.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var (key, value) in span.Tags)
            if (key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                value.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void Flatten(PerfSpanNode node, List<PerfSpanTreeRow> rows)
    {
        rows.Add(new PerfSpanTreeRow { Node = node });
        if (!node.IsExpanded) return;
        foreach (var child in node.Children) Flatten(child, rows);
    }

    private Control BuildRow(PerfSpanTreeRow row)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("18,*,66,48"),
            ColumnSpacing = 7,
            Margin = new Thickness(6 + row.Indent, 3, 6, 3),
        };
        var chevron = new Button
        {
            Content = row.Chevron,
            Width = 16,
            Height = 16,
            Padding = new Thickness(0),
            FontSize = 10,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsVisible = row.Node.Children.Count > 0,
            Foreground = new SolidColorBrush(PerfPalette.Neutral),
        };
        chevron.Click += (_, _) =>
        {
            _overrides[row.Span.SpanId] = !row.Node.IsExpanded;
            row.Node.IsExpanded = !row.Node.IsExpanded;
            _selectedSpanId = row.Span.SpanId;
            RebuildRows();
        };
        grid.Children.Add(chevron);

        var nameCell = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 5 };
        var kindBadge = new Border
        {
            Padding = new Thickness(4, 1),
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = row.LangfuseKind,
                FontSize = 8,
                FontWeight = FontWeight.SemiBold,
                Foreground = row.KindBrush,
            },
        };
        kindBadge.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppSubtleBackground"));
        nameCell.Children.Add(kindBadge);

        var name = new TextBlock
        {
            Text = row.Name,
            FontSize = 11,
            MaxWidth = 130,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        name.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        Grid.SetColumn(name, 1);
        nameCell.Children.Add(name);
        if (row.Detail.Length > 0)
        {
            var detail = new TextBlock
            {
                Text = row.Detail,
                FontSize = 9,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            detail.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
            Grid.SetColumn(detail, 2);
            nameCell.Children.Add(detail);
        }
        Grid.SetColumn(nameCell, 1);
        grid.Children.Add(nameCell);

        var duration = new TextBlock
        {
            Text = row.Duration,
            FontSize = 9.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        duration.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextSecondary"));
        Grid.SetColumn(duration, 2);
        grid.Children.Add(duration);

        var status = new TextBlock
        {
            Text = row.Status,
            FontSize = 9,
            Foreground = row.StatusBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(status, 3);
        grid.Children.Add(status);
        return grid;
    }

    private static void AddHeader(Grid grid, string text, int column)
    {
        var block = new TextBlock { Text = text, FontSize = 9 };
        block.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        if (column > 0) block.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }
}
