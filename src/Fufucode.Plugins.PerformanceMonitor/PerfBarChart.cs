using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// 耗时分布横向条形：每个条目一行名称 + 按最大耗时等比例缩放的条 + 数值。
/// 纯 Avalonia 控件搭建，无第三方图表库；用于概览中的“模型/工具耗时构成”。
/// </summary>
public sealed class PerfBarChart : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 6 };
    private readonly TextBlock _empty = new()
    {
        Text = "暂无数据：运行一个回合后在这里查看耗时分布。",
        TextWrapping = TextWrapping.Wrap,
        Classes = { "caption" },
    };

    public PerfBarChart()
    {
        Content = new StackPanel { Spacing = 8, Children = { _empty, _rows } };
    }

    /// <summary>用一组 (标签, 数值ms) 刷新图表；最大值决定条长。</summary>
    public void SetData(IReadOnlyList<(string Label, double Value, Color Color)> items)
    {
        _rows.Children.Clear();
        _empty.IsVisible = items.Count == 0;
        if (items.Count == 0) return;
        var max = Math.Max(1, items.Max(item => item.Value));
        foreach (var (label, value, color) in items)
        {
            var bar = new Border
            {
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Left,
                MinWidth = 2,
                Width = Math.Max(2, 360 * value / max),
            };
            var valueText = new TextBlock
            {
                Text = FormatMs(value),
                Classes = { "mono" },
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            var labelText = new TextBlock
            {
                Text = label,
                Width = 140,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _rows.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { labelText, bar, valueText },
            });
        }
    }

    public static string FormatMs(double ms) => ms switch
    {
        >= 60_000 => $"{ms / 60_000:0.0} 分",
        >= 1_000 => $"{ms / 1_000:0.00} s",
        >= 10 => $"{ms:0} ms",
        _ => $"{ms:0.0} ms",
    };
}
