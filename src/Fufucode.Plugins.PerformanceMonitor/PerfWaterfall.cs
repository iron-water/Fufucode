using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// 回合瀑布图：每个 span 一行，左侧标签，右侧按耗时在回合时间轴上的位置画条。
/// 子 span 缩进，颜色按类别区分；点击行可在页面中切换 trace（用于跨回合跳转）。
/// </summary>
public sealed class PerfWaterfall : UserControl
{
    private const double LabelWidth = 420;
    private const double RowHeight = 18;
    private readonly Canvas _canvas = new();
    private readonly StackPanel _labels = new() { Spacing = 0 };
    private readonly ScrollViewer _labelScroll;

    public PerfWaterfall()
    {
        _labelScroll = new ScrollViewer
        {
            Content = _labels,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            Width = LabelWidth,
        };
        Content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Children = { _labelScroll, new ScrollViewer { Content = _canvas } },
        };
        Grid.SetColumn(_labelScroll, 0);
        Grid.SetColumn((Control)((Grid)Content).Children[1], 1);
    }

    public void SetSpans(IReadOnlyList<SpanRecord> spans, long traceId)
    {
        _labels.Children.Clear();
        _canvas.Children.Clear();
        if (spans.Count == 0)
        {
            _labels.Children.Add(new TextBlock { Text = "该回合没有数据。", Classes = { "caption" }, Margin = new Thickness(0, 8) });
            return;
        }

        var root = spans.FirstOrDefault(s => s.Name == "turn" && s.ParentSpanId == 0) ?? spans[0];
        var start = spans.Min(s => s.StartTicks);
        var total = Math.Max(1, spans.Max(s => s.EndTicks) - start);
        var width = 900d;
        _canvas.Width = width;
        _canvas.Height = spans.Count * RowHeight;

        // 按父子关系计算缩进深度（防止孤儿 span 造成死循环）。
        var byId = spans.ToDictionary(s => s.SpanId);
        int Depth(SpanRecord span)
        {
            var depth = 0;
            var current = span;
            while (current.ParentSpanId != 0 && byId.TryGetValue(current.ParentSpanId, out var parent) && depth < 16)
            {
                current = parent;
                depth++;
            }
            return depth;
        }

        // 瀑布按开始时间排序；同一起始时间保持稳定顺序。
        var ordered = spans.OrderBy(s => s.StartTicks).ThenBy(s => s.SpanId).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var span = ordered[i];
            var depth = Depth(span);
            var left = (span.StartTicks - start) / (double)total * width;
            var barWidth = Math.Max(2, (span.EndTicks - span.StartTicks) / (double)total * width);
            var color = span.Status == "Error" ? Color.FromRgb(0xE8, 0x5A, 0x5A)
                : span.Status == "Cancelled" ? Color.FromRgb(0x8A, 0x8A, 0x8E)
                : PerfDockPage.CategoryColor(span.Category);
            var bar = new Border
            {
                Width = barWidth,
                Height = RowHeight - 6,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(color),
                Opacity = span == root ? 0.9 : 0.75,
            };
            Canvas.SetLeft(bar, left);
            Canvas.SetTop(bar, i * RowHeight + 3);
            Avalonia.Controls.ToolTip.SetTip(bar, $"{span.Name} · {PerfBarChart.FormatMs(span.DurationMs)} · {span.Status}");
            bar.PointerPressed += (_, _) => DetailRequested?.Invoke(span);
            if (span.TraceId != traceId) bar.Opacity = 0.3;
            _canvas.Children.Add(bar);

            var label = new Border
            {
                Height = RowHeight,
                Padding = new Thickness(4 + depth * 14, 0, 4, 0),
                Child = new TextBlock
                {
                    Text = $"{span.Name}  {PerfBarChart.FormatMs(span.DurationMs)}" +
                           (span.Tag("tool") is { } tool ? $"  {tool}" : ""),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            label.PointerPressed += (_, _) => DetailRequested?.Invoke(span);
            _labels.Children.Add(label);
        }
    }

    /// <summary>点击某个 span 时由页面展示明细。</summary>
    public event Action<SpanRecord>? DetailRequested;
}
