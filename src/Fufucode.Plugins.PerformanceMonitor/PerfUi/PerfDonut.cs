using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;

namespace Fufucode.Plugins.PerformanceMonitor;

internal readonly record struct PerfDonutSegment(double Value, Color Color);

/// <summary>无第三方图表库的环形图：轨道 + 圆弧段 + 中心大数字。</summary>
internal sealed class PerfDonut : UserControl
{
    private const double Size = 148;
    private const double Thickness = 14;
    private const double Radius = 57;
    private const double Center = Size / 2;

    private readonly Canvas _canvas = new() { Width = Size, Height = Size };
    private readonly TextBlock _value = new()
    {
        FontSize = 24,
        FontWeight = FontWeight.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
    };
    private readonly TextBlock _caption = new()
    {
        FontSize = 10,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        Margin = new Thickness(0, 2, 0, 0),
    };

    public PerfDonut()
    {
        _value.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        _caption.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextSecondary"));

        var center = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _value, _caption },
        };
        var host = new Grid
        {
            Width = Size,
            Height = Size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _canvas, center },
        };
        Content = host;
        Rebuild([]);
    }

    public void SetData(IReadOnlyList<PerfDonutSegment> segments, string value, string caption)
    {
        _value.Text = value;
        _caption.Text = caption;
        Rebuild(segments);
    }

    private void Rebuild(IReadOnlyList<PerfDonutSegment> segments)
    {
        _canvas.Children.Clear();
        var track = new Ellipse
        {
            Width = Radius * 2,
            Height = Radius * 2,
            StrokeThickness = Thickness,
            Stroke = new SolidColorBrush(Color.Parse("#2A333E")),
            Opacity = 0.55,
        };
        Canvas.SetLeft(track, Center - Radius);
        Canvas.SetTop(track, Center - Radius);
        _canvas.Children.Add(track);

        var total = segments.Sum(segment => Math.Max(0, segment.Value));
        if (total <= 0) return;

        var gap = segments.Count > 1 ? 2.2d : 0d;
        var angle = -90d;
        foreach (var segment in segments)
        {
            if (segment.Value <= 0) continue;
            var sweep = segment.Value / total * 360d;
            var drawStart = angle + gap / 2;
            var drawSweep = Math.Max(0.6, sweep - gap);
            var path = new AvaloniaPath
            {
                Data = Arc(Center, Center, Radius, drawStart, drawSweep),
                Stroke = new SolidColorBrush(segment.Color),
                StrokeThickness = Thickness,
                StrokeLineCap = PenLineCap.Round,
            };
            _canvas.Children.Add(path);
            angle += sweep;
        }
    }

    private static Geometry Arc(double cx, double cy, double radius, double startDegrees, double sweepDegrees)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        var start = PointOn(cx, cy, radius, startDegrees);
        var end = PointOn(cx, cy, radius, startDegrees + sweepDegrees);
        context.BeginFigure(start, false);
        context.ArcTo(end, new Size(radius, radius), 0, sweepDegrees > 180,
            SweepDirection.Clockwise);
        return geometry;
    }

    private static Point PointOn(double cx, double cy, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180d;
        return new Point(cx + radius * Math.Cos(radians), cy + radius * Math.Sin(radians));
    }
}
