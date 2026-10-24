using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Fufucode.Plugins.Sketch;

internal enum SketchTool
{
    Pointer,
    Pen,
    Text,
    Rectangle,
    Eraser,
}

internal sealed class SketchItem
{
    public SketchTool Kind { get; init; }
    public List<Point> Points { get; init; } = [];
    public Rect Bounds { get; init; }
    public string Text { get; init; } = string.Empty;
    public Color Color { get; init; }
    public double Width { get; init; }

    public SketchItem Clone() => new()
    {
        Kind = Kind,
        Points = Points.ToList(),
        Bounds = Bounds,
        Text = Text,
        Color = Color,
        Width = Width,
    };
}

internal sealed class SketchCanvas : Control
{
    private readonly List<SketchItem> _items = [];
    private readonly Stack<List<SketchItem>> _undo = new();
    private readonly Stack<List<SketchItem>> _redo = new();
    private List<Point>? _activePoints;
    private Point _gestureStart;
    private Rect _activeRect;
    private bool _gestureChanged;
    private List<SketchItem>? _gestureSnapshot;

    public SketchTool Tool { get; set; } = SketchTool.Pen;
    public Color InkColor { get; set; } = Colors.White;
    public double StrokeWidth { get; set; } = 4;
    public event Func<Point, System.Threading.Tasks.Task>? TextRequested;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void AddText(Point point, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        BeginMutation();
        _items.Add(new SketchItem
        {
            Kind = SketchTool.Text,
            Bounds = new Rect(point, new Size(360, 36)),
            Text = text.Trim(),
            Color = InkColor,
            Width = StrokeWidth,
        });
        InvalidateVisual();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(CloneItems(_items));
        _items.Clear();
        _items.AddRange(_undo.Pop());
        InvalidateVisual();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(CloneItems(_items));
        _items.Clear();
        _items.AddRange(_redo.Pop());
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#202020")), null, Bounds);

        foreach (var item in _items)
        {
            var brush = new SolidColorBrush(item.Color);
            switch (item.Kind)
            {
                case SketchTool.Pen:
                    DrawStroke(context, item.Points, brush, item.Width);
                    break;
                case SketchTool.Rectangle:
                    context.DrawRectangle(null, new Pen(brush, item.Width), item.Bounds);
                    break;
                case SketchTool.Text:
                    var formatted = new FormattedText(item.Text, System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, Typeface.Default, Math.Max(13, item.Width * 3.2), brush);
                    context.DrawText(formatted, item.Bounds.TopLeft);
                    break;
            }
        }

        if (_activePoints is { Count: > 1 })
            DrawStroke(context, _activePoints, new SolidColorBrush(InkColor), StrokeWidth);
        if (Tool == SketchTool.Rectangle && !_activeRect.Size.Equals(default))
            context.DrawRectangle(null, new Pen(new SolidColorBrush(InkColor), StrokeWidth), _activeRect);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(this);

        if (Tool == SketchTool.Text)
        {
            _ = TextRequested?.Invoke(point);
            e.Handled = true;
            return;
        }

        _gestureStart = point;
        _gestureChanged = false;
        _gestureSnapshot = CloneItems(_items);
        if (Tool == SketchTool.Pen)
        {
            _activePoints = [point];
        }
        else if (Tool == SketchTool.Rectangle)
        {
            _activeRect = new Rect(point, point);
        }
        else if (Tool == SketchTool.Eraser)
        {
            EraseAt(point);
        }
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!ReferenceEquals(e.Pointer.Captured, this)) return;
        var point = e.GetPosition(this);
        switch (Tool)
        {
            case SketchTool.Pen:
                _activePoints?.Add(point);
                break;
            case SketchTool.Rectangle:
                _activeRect = new Rect(
                    Math.Min(_gestureStart.X, point.X),
                    Math.Min(_gestureStart.Y, point.Y),
                    Math.Abs(point.X - _gestureStart.X),
                    Math.Abs(point.Y - _gestureStart.Y));
                break;
            case SketchTool.Eraser:
                EraseAt(point);
                break;
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!ReferenceEquals(e.Pointer.Captured, this)) return;
        e.Pointer.Capture(null);

        if (Tool == SketchTool.Pen && _activePoints is { Count: > 1 })
        {
            BeginMutation();
            _items.Add(new SketchItem { Kind = SketchTool.Pen, Points = _activePoints.ToList(), Color = InkColor, Width = StrokeWidth });
        }
        else if (Tool == SketchTool.Rectangle && _activeRect.Width > 3 && _activeRect.Height > 3)
        {
            BeginMutation();
            _items.Add(new SketchItem { Kind = SketchTool.Rectangle, Bounds = _activeRect, Color = InkColor, Width = StrokeWidth });
        }

        _activePoints = null;
        _activeRect = default;
        _gestureSnapshot = null;
        InvalidateVisual();
        e.Handled = true;
    }

    private void EraseAt(Point point)
    {
        var hit = _items.LastOrDefault(item =>
            item.Kind == SketchTool.Pen && item.Points.Any(candidate => Distance(candidate, point) <= Math.Max(12, StrokeWidth * 2.2))
            || item.Kind == SketchTool.Rectangle && item.Bounds.Inflate(12).Contains(point)
            || item.Kind == SketchTool.Text && item.Bounds.Inflate(12).Contains(point));
        if (hit is null) return;
        if (!_gestureChanged)
        {
            _gestureChanged = true;
            _undo.Push(_gestureSnapshot ?? CloneItems(_items));
            _redo.Clear();
        }
        _items.Remove(hit);
    }

    private void BeginMutation()
    {
        _undo.Push(CloneItems(_items));
        _redo.Clear();
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static List<SketchItem> CloneItems(IEnumerable<SketchItem> source) => source.Select(item => item.Clone()).ToList();

    private static void DrawStroke(DrawingContext context, IReadOnlyList<Point> points, IBrush brush, double width)
    {
        var pen = new Pen(brush, width);
        for (var index = 1; index < points.Count; index++) context.DrawLine(pen, points[index - 1], points[index]);
        if (points.Count == 1) context.DrawEllipse(brush, null, points[0], width / 2, width / 2);
    }
}
