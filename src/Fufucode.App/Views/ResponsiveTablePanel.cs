using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using LiveMarkdown.Avalonia;

namespace Fufucode.App.Views;

/// <summary>生成中按自然列宽显示；完成后以有限列宽重新测量单元格，让行高容纳换行。</summary>
public sealed class ResponsiveTablePanel : MarkdownTableGrid
{
    public static readonly StyledProperty<bool> IsStreamingProperty =
        AvaloniaProperty.Register<ResponsiveTablePanel, bool>(nameof(IsStreaming));

    private double[] _widths = [];
    private double[] _heights = [];
    private bool _fitted;

    static ResponsiveTablePanel() => AffectsMeasure<ResponsiveTablePanel>(IsStreamingProperty);

    public bool IsStreaming
    {
        get => GetValue(IsStreamingProperty);
        set => SetValue(IsStreamingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _fitted = !IsStreaming && double.IsFinite(availableSize.Width);
        if (!_fitted || Children.Count == 0) return base.MeasureOverride(availableSize);

        var columns = Children.Max(cell => Grid.GetColumn(cell) + Grid.GetColumnSpan(cell));
        var rows = Children.Max(cell => Grid.GetRow(cell) + Grid.GetRowSpan(cell));
        _widths = new double[columns];
        _heights = new double[rows];
        foreach (var cell in Children)
        {
            cell.Measure(Size.Infinity);
            var span = Grid.GetColumnSpan(cell);
            var width = Math.Max(0, cell.DesiredSize.Width - (span - 1) * ColumnSpacing) / span;
            for (var column = Grid.GetColumn(cell); column < Grid.GetColumn(cell) + span; column++)
                _widths[column] = Math.Max(_widths[column], width);
        }

        var budget = Math.Max(0, availableSize.Width - (columns - 1) * ColumnSpacing);
        FitWidths(_widths, budget);
        foreach (var cell in Children)
        {
            var width = SpannedSize(_widths, Grid.GetColumn(cell), Grid.GetColumnSpan(cell), ColumnSpacing);
            cell.Measure(new Size(width, double.PositiveInfinity));
            var span = Grid.GetRowSpan(cell);
            var height = Math.Max(0, cell.DesiredSize.Height - (span - 1) * RowSpacing) / span;
            for (var row = Grid.GetRow(cell); row < Grid.GetRow(cell) + span; row++)
                _heights[row] = Math.Max(_heights[row], height);
        }
        return new Size(_widths.Sum() + (columns - 1) * ColumnSpacing,
            _heights.Sum() + (rows - 1) * RowSpacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!_fitted) return base.ArrangeOverride(finalSize);
        foreach (var cell in Children)
        {
            var column = Grid.GetColumn(cell);
            var row = Grid.GetRow(cell);
            cell.Arrange(new Rect(
                _widths.Take(column).Sum() + column * ColumnSpacing,
                _heights.Take(row).Sum() + row * RowSpacing,
                SpannedSize(_widths, column, Grid.GetColumnSpan(cell), ColumnSpacing),
                SpannedSize(_heights, row, Grid.GetRowSpan(cell), RowSpacing)));
        }
        return finalSize;
    }

    private static double SpannedSize(double[] sizes, int start, int count, double spacing) =>
        sizes.Skip(start).Take(count).Sum() + (count - 1) * spacing;

    private static void FitWidths(double[] widths, double budget)
    {
        if (widths.Sum() <= budget) return;
        var natural = (double[])widths.Clone();
        var minimum = Math.Min(96, budget / widths.Length);
        for (var index = 0; index < widths.Length; index++) widths[index] = Math.Min(natural[index], minimum);
        var remaining = Math.Max(0, budget - widths.Sum());
        // 短列保留自然宽度；较长列分配余量，避免一个长单元格吞掉整个表格。
        while (remaining > 0.01)
        {
            var weights = natural.Select((width, index) => Math.Sqrt(Math.Max(0, width - widths[index]))).ToArray();
            var total = weights.Sum();
            if (total <= 0) break;
            var used = 0d;
            for (var index = 0; index < widths.Length; index++)
            {
                var extra = Math.Min(natural[index] - widths[index], remaining * weights[index] / total);
                widths[index] += extra;
                used += extra;
            }
            if (used < 0.01) break;
            remaining -= used;
        }
    }
}
