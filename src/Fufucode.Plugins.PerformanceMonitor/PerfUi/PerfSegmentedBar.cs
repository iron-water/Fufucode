using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Fufucode.Plugins.PerformanceMonitor;

internal readonly record struct PerfBarSegment(double Value, Color Color);

/// <summary>按比例分段的横条；用于上下文窗口、阻塞构成等。</summary>
internal sealed class PerfSegmentedBar : UserControl
{
    private readonly Grid _segments = new() { ColumnSpacing = 1 };
    private readonly Border _frame;

    public PerfSegmentedBar(double height = 10)
    {
        _frame = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            ClipToBounds = true,
            Child = _segments,
        };
        _frame.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppDividerSubtle"));
        Content = _frame;
    }

    public void SetData(IReadOnlyList<PerfBarSegment> segments)
    {
        _segments.Children.Clear();
        _segments.ColumnDefinitions.Clear();
        var total = segments.Sum(segment => Math.Max(0, segment.Value));
        if (total <= 0) return;

        foreach (var segment in segments)
        {
            if (segment.Value <= 0) continue;
            _segments.ColumnDefinitions.Add(new ColumnDefinition(segment.Value / total, GridUnitType.Star));
            var bar = new Border
            {
                Background = new SolidColorBrush(segment.Color),
                CornerRadius = new CornerRadius(2),
            };
            Grid.SetColumn(bar, _segments.Children.Count);
            _segments.Children.Add(bar);
        }
    }
}
