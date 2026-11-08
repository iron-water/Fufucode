using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace Fufucode.App.Features.Graph;

/// <summary>A native Avalonia surface with a single tab stop and explicit spatial focus mode.</summary>
internal sealed class GraphCanvas : Control, IDisposable
{
    public static readonly StyledProperty<IBrush?> InkProperty = AvaloniaProperty.Register<GraphCanvas, IBrush?>(nameof(Ink));
    public static readonly StyledProperty<IBrush?> SecondaryInkProperty = AvaloniaProperty.Register<GraphCanvas, IBrush?>(nameof(SecondaryInk));
    public static readonly StyledProperty<IBrush?> SurfaceProperty = AvaloniaProperty.Register<GraphCanvas, IBrush?>(nameof(Surface));
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<GraphCanvas, IBrush?>(nameof(Stroke));
    public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<GraphCanvas, IBrush?>(nameof(Accent));
    public static readonly StyledProperty<IBrush?> SelectedSurfaceProperty = AvaloniaProperty.Register<GraphCanvas, IBrush?>(nameof(SelectedSurface));
    public static readonly StyledProperty<IBrush?> PageSurfaceProperty = AvaloniaProperty.Register<GraphCanvas, IBrush?>(nameof(PageSurface));
    public IBrush? Ink { get => GetValue(InkProperty); set => SetValue(InkProperty, value); }
    public IBrush? SecondaryInk { get => GetValue(SecondaryInkProperty); set => SetValue(SecondaryInkProperty, value); }
    public IBrush? Surface { get => GetValue(SurfaceProperty); set => SetValue(SurfaceProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public IBrush? SelectedSurface { get => GetValue(SelectedSurfaceProperty); set => SetValue(SelectedSurfaceProperty, value); }
    public IBrush? PageSurface { get => GetValue(PageSurfaceProperty); set => SetValue(PageSurfaceProperty, value); }
    private readonly GraphWorkspaceViewModel _vm;
    private readonly bool _mini;
    private readonly GraphViewportState _viewport;
    private Point? _panStart, _marqueeStart;
    private Point _lastPointer;
    private Rect? _marquee;
    private bool _subscribed, _disposed;
    internal bool IsSpatialFocusMode { get; private set; }
    internal string? FocusedModuleId { get; private set; }

    static GraphCanvas() => AffectsRender<GraphCanvas>(InkProperty, SecondaryInkProperty, SurfaceProperty,
        StrokeProperty, AccentProperty, SelectedSurfaceProperty, PageSurfaceProperty);
    internal GraphCanvas(GraphWorkspaceViewModel vm, bool mini = false)
    {
        _vm = vm;
        _mini = mini;
        _viewport = mini ? vm.MiniMapViewport : vm.MainViewport;
        Focusable = true;
        ClipToBounds = true;
        MinHeight = mini ? 120 : 180;
        AutomationProperties.SetAutomationId(this, mini ? "GraphMiniMapCanvas" : "GraphCanvas");
        AutomationProperties.SetName(this, mini ? "极简 Graph" : "项目图");
        Bind(InkProperty, new DynamicResourceExtension("AppTextPrimary"));
        Bind(SecondaryInkProperty, new DynamicResourceExtension("AppTextSecondary"));
        Bind(SurfaceProperty, new DynamicResourceExtension("AppCardBackground"));
        Bind(StrokeProperty, new DynamicResourceExtension("AppBorderBrush"));
        Bind(AccentProperty, new DynamicResourceExtension("AppAccent"));
        Bind(SelectedSurfaceProperty, new DynamicResourceExtension("AppListSelected"));
        Bind(PageSurfaceProperty, new DynamicResourceExtension("AppMicaPageBackground"));
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }
    private void Subscribe()
    {
        if (_disposed || _subscribed) return;
        _subscribed = true;
        _vm.SceneChanged += OnSceneChanged;
        _viewport.PropertyChanged += OnViewportChanged;
        OnSceneChanged();
        FitIfNeeded();
    }
    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        _vm.SceneChanged -= OnSceneChanged;
        _viewport.PropertyChanged -= OnViewportChanged;
    }
    private void OnSceneChanged()
    {
        if (FocusedModuleId != null && _vm.Scene.FindBounds(FocusedModuleId) == null) FocusedModuleId = null;
        FitIfNeeded();
        InvalidateVisual();
    }
    private void OnViewportChanged(object? sender, PropertyChangedEventArgs e) { FitIfNeeded(); InvalidateVisual(); }
    private void FitIfNeeded() { if (!_viewport.Positioned && Bounds.Width > 0 && Bounds.Height > 0) Fit(); }
    internal void Fit() => _viewport.Fit(_vm.Scene.Bounds, Bounds.Size);
    internal void Zoom(double factor) => _viewport.Zoom(factor, new Point(Bounds.Width / 2, Bounds.Height / 2));
    protected override void OnSizeChanged(SizeChangedEventArgs e) { base.OnSizeChanged(e); FitIfNeeded(); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(PageSurface ?? Brushes.Transparent, new Rect(Bounds.Size));
        using (context.PushTransform(Matrix.CreateScale(_viewport.Scale, _viewport.Scale) *
            Matrix.CreateTranslation(_viewport.X, _viewport.Y)))
        {
            foreach (var group in _vm.Scene.Groups)
            {
                var selected = _vm.SelectedModuleIds.Contains(group.Module.Id);
                context.DrawRectangle(null, new Pen(selected ? Accent : Stroke, selected ? 2 : 1), group.Bounds, 5, 5);
                Text(context, group.Module.Name, group.Bounds.TopLeft + new Vector(12, 7), group.Bounds.Width - 24, 14, true);
                if (IsSpatialFocusMode && FocusedModuleId == group.Module.Id) FocusRing(context, group.Bounds);
            }
            for (var i = 0; i < _vm.Scene.Edges.Count; i++) DrawEdge(context, _vm.Scene.Edges[i], i);
            foreach (var node in _vm.Scene.Nodes)
            {
                var selected = _vm.SelectedModuleIds.Contains(node.Module.Id);
                context.DrawRectangle(Surface, new Pen(selected ? Accent : Stroke, selected ? 2 : 1), node.Bounds, 5, 5);
                if (selected) context.DrawRectangle(SelectedSurface, null, node.Bounds.Deflate(1), 4, 4);
                Text(context, (node.IsReference ? "↗ " : "") + node.Module.Name,
                    node.Bounds.TopLeft + new Vector(12, 11), node.Bounds.Width - 24, 14, true);
                Text(context, GraphLabels.ModuleSummary(node.Module.Summary), node.Bounds.TopLeft + new Vector(12, 36), node.Bounds.Width - 24, 12, false, 30);
                if (IsSpatialFocusMode && FocusedModuleId == node.Module.Id) FocusRing(context, node.Bounds);
            }
            if (_marquee is { } marquee)
                context.DrawRectangle(null, new Pen(Accent, 1.5 / _viewport.Scale), marquee);
        }
    }
    private void FocusRing(DrawingContext context, Rect bounds) =>
        context.DrawRectangle(null, new Pen(Accent, 2 / _viewport.Scale,
            new DashStyle(new[] { 3d, 2d }, 0)), bounds.Inflate(4), 6, 6);
    private void DrawEdge(DrawingContext context, GraphEdge edge, int index)
    {
        var right = edge.Target.Center.X >= edge.Source.Center.X;
        var from = new Point(right ? edge.Source.Right : edge.Source.Left, edge.Source.Center.Y);
        var to = new Point(right ? edge.Target.Left : edge.Target.Right, edge.Target.Center.Y);
        if (edge.Relation.SourceId == edge.Relation.TargetId)
        {
            from = edge.Source.TopRight - new Vector(30, 0);
            to = edge.Source.TopLeft + new Vector(30, 0);
        }
        var delta = Math.Max(32, Math.Abs(to.X - from.X) / 2);
        var first = from + new Vector(right ? delta : -delta, 0);
        var last = to - new Vector(right ? delta : -delta, 0);
        if (edge.Relation.SourceId == edge.Relation.TargetId)
        { first = from - new Vector(0, 44); last = to - new Vector(0, 44); }
        var line = new StreamGeometry();
        using (var path = line.Open())
        {
            path.BeginFigure(from, false);
            path.CubicBezierTo(first, last, to);
            path.EndFigure(false);
        }
        var flow = edge.Relation.Kind is "dataflow" or "data-flow" or "data_flow";
        context.DrawGeometry(null, new Pen(SecondaryInk, 1.4,
            flow ? new DashStyle(new[] { 5d, 3d }, 0) : null), line);
        var direction = new Vector(to.X - last.X, to.Y - last.Y);
        if (direction.Length > 0)
        {
            direction /= direction.Length;
            var normal = new Vector(-direction.Y, direction.X);
            context.DrawLine(new Pen(SecondaryInk, 1.4), to, to - direction * 8 + normal * 4);
            context.DrawLine(new Pen(SecondaryInk, 1.4), to, to - direction * 8 - normal * 4);
        }
        if (!_mini)
        {
            var center = new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2 - 15 - index % 3 * 14);
            var label = Measure(edge.Label, 210, 11, false, 17);
            var labelBounds = new Rect(center.X - label.Width / 2 - 4, center.Y, label.Width + 8, label.Height);
            context.FillRectangle(PageSurface ?? Surface ?? Brushes.Transparent, labelBounds);
            context.DrawText(label, labelBounds.TopLeft + new Vector(4, 0));
        }
    }
    private FormattedText Measure(string value, double width, double size, bool strong, double height = 20)
    {
        var font = TextElement.GetFontFamily(this);
        return new FormattedText(value.Replace('\r', ' ').Replace('\n', ' '), CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface(font, FontStyle.Normal, strong ? FontWeight.SemiBold : FontWeight.Normal),
            size, strong ? Ink : SecondaryInk)
        { MaxTextWidth = Math.Max(1, width), MaxTextHeight = height, Trimming = TextTrimming.CharacterEllipsis };
    }
    private void Text(DrawingContext context, string value, Point point, double width, double size, bool strong, double height = 20)
    {
        if (!string.IsNullOrEmpty(value)) context.DrawText(Measure(value, width, size, strong, height), point);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        _lastPointer = point;
        var properties = e.GetCurrentPoint(this).Properties;
        var hit = _vm.Scene.Hit(_viewport.ToWorld(point));
        Focus(NavigationMethod.Pointer);
        if (properties.IsRightButtonPressed)
        {
            if (hit != null)
            {
                _vm.SelectModule(hit);
                ContextMenu = new ContextMenu { ItemsSource = new[]
                {
                    Menu("属性", () => _vm.RevealPanel(GraphWorkspaceViewModel.PropertiesPageId)),
                    Menu("展开", () => _vm.DrillInto(hit)), Menu("打开会话", () => _vm.OpenChat(hit, null)),
                    Menu("添加邮件", _vm.BeginTicket, _vm.CanEdit)
                } };
            }
            else ContextMenu = new ContextMenu { ItemsSource = new[]
            { Menu("展开整图", _vm.ExpandAll), Menu("适应", Fit) } };
            return;
        }
        if (!properties.IsLeftButtonPressed && !properties.IsMiddleButtonPressed) return;
        if (properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && hit == null)
        { _marqueeStart = _viewport.ToWorld(point); e.Pointer.Capture(this); }
        else if (properties.IsLeftButtonPressed && hit != null)
        {
            var additive = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0;
            if (e.ClickCount >= 2 && !additive) _vm.DrillInto(hit);
            else if (_mini && !additive) _vm.NavigateToModule(hit);
            else _vm.SelectModule(hit, additive);
        }
        else { _panStart = point; e.Pointer.Capture(this); }
        e.Handled = true;
    }
    private static MenuItem Menu(string title, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = title, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_panStart != null)
        {
            _viewport.Pan(point.X - _lastPointer.X, point.Y - _lastPointer.Y);
            _lastPointer = point;
            e.Handled = true;
        }
        else if (_marqueeStart is { } start)
        {
            var end = _viewport.ToWorld(point);
            _marquee = new Rect(start, end).Normalize();
            InvalidateVisual(); e.Handled = true;
        }
        else
        {
            var id = _vm.Scene.Hit(_viewport.ToWorld(point));
            var module = _vm.Snapshot.Modules.FirstOrDefault(m => m.Id == id);
            ToolTip.SetTip(this, module == null ? null : module.Name + Environment.NewLine + GraphLabels.ModuleSummary(module.Summary));
        }
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_marquee is { } box)
            _vm.SelectModules(_vm.Scene.Nodes.Where(n => box.Intersects(n.Bounds)).Select(n => n.Module.Id),
                e.KeyModifiers.HasFlag(KeyModifiers.Control));
        _panStart = _marqueeStart = null; _marquee = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    { base.OnPointerCaptureLost(e); _panStart = _marqueeStart = null; _marquee = null; InvalidateVisual(); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _viewport.Zoom(Math.Pow(1.14, e.Delta.Y), e.GetPosition(this));
        e.Handled = true;
    }
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (e.NavigationMethod == NavigationMethod.Tab) EnterSpatialFocus();
    }
    protected override void OnLostFocus(FocusChangedEventArgs e)
    { base.OnLostFocus(e); IsSpatialFocusMode = false; InvalidateVisual(); }
    private void EnterSpatialFocus()
    {
        IsSpatialFocusMode = true;
        FocusedModuleId = _vm.Scene.FindBounds(_vm.SelectedModuleId ?? "") != null
            ? _vm.SelectedModuleId : _vm.Scene.Nodes.FirstOrDefault()?.Module.Id ?? _vm.Scene.Groups.FirstOrDefault()?.Module.Id;
        EnsureFocusVisible(); InvalidateVisual();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) || e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (e.Key == Key.Tab)
        {
            if (!IsSpatialFocusMode && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { EnterSpatialFocus(); e.Handled = true; }
            else { IsSpatialFocusMode = false; InvalidateVisual(); } // Let Avalonia move to the next tab stop.
            return;
        }
        if (e.Key == Key.Escape && IsSpatialFocusMode)
        { IsSpatialFocusMode = false; InvalidateVisual(); e.Handled = true; return; }
        if (IsSpatialFocusMode)
        {
            if (e.Key == Key.Enter && FocusedModuleId is { } selected)
            { if (_mini) _vm.NavigateToModule(selected); else _vm.SelectModule(selected); e.Handled = true; }
            else if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
            { MoveSpatialFocus(e.Key); e.Handled = true; }
            return;
        }
        switch (e.Key)
        {
            case Key.Up: _vm.GoUp(); break;
            case Key.Down: _vm.GoDeeper(); break;
            case Key.Home: _vm.GoTop(); break;
            case Key.End: _vm.GoBottom(); break;
            case Key.Left: _vm.MoveHistory(-1); break;
            case Key.Right: _vm.MoveHistory(1); break;
            case Key.Add: case Key.OemPlus: Zoom(1.2); break;
            case Key.Subtract: case Key.OemMinus: Zoom(1 / 1.2); break;
            default: return;
        }
        e.Handled = true;
    }
    private void MoveSpatialFocus(Key key)
    {
        var items = _vm.Scene.Nodes.Select(n => (n.Module.Id, n.Bounds))
            .Concat(_vm.Scene.Groups.Select(g => (g.Module.Id, g.Bounds))).ToArray();
        var current = items.FirstOrDefault(p => p.Id == FocusedModuleId);
        if (current.Id == null) { EnterSpatialFocus(); return; }
        var direction = key switch
        { Key.Left => new Vector(-1, 0), Key.Right => new Vector(1, 0), Key.Up => new Vector(0, -1), _ => new Vector(0, 1) };
        var candidates = items.Where(p => p.Id != current.Id).Select(p =>
        {
            var delta = p.Bounds.Center - current.Bounds.Center;
            var along = delta.X * direction.X + delta.Y * direction.Y;
            var across = Math.Abs(delta.X * direction.Y - delta.Y * direction.X);
            return (p.Id, Along: along, Score: along + across * 3);
        }).Where(p => p.Along > 1).OrderBy(p => p.Score).ToArray();
        if (candidates.Length > 0) FocusedModuleId = candidates[0].Id;
        EnsureFocusVisible(); InvalidateVisual();
    }
    private void EnsureFocusVisible()
    {
        if (FocusedModuleId is not { } id || _vm.Scene.FindBounds(id) is not { } box) return;
        var center = box.Center;
        var x = center.X * _viewport.Scale + _viewport.X;
        var y = center.Y * _viewport.Scale + _viewport.Y;
        if (x < 24 || x > Bounds.Width - 24 || y < 24 || y > Bounds.Height - 24)
            _viewport.Pan(Bounds.Width / 2 - x, Bounds.Height / 2 - y);
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Unsubscribe(); }
}
