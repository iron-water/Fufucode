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
    public static readonly StyledProperty<IBrush?> CardStrokeProperty = AvaloniaProperty.Register<GraphCanvas, IBrush?>(nameof(CardStroke));
    public static readonly StyledProperty<IBrush?> CardHoverProperty = AvaloniaProperty.Register<GraphCanvas, IBrush?>(nameof(CardHover));
    public static readonly StyledProperty<IBrush?> DimInkProperty = AvaloniaProperty.Register<GraphCanvas, IBrush?>(nameof(DimInk));
    public static readonly StyledProperty<IBrush?> PillProperty = AvaloniaProperty.Register<GraphCanvas, IBrush?>(nameof(Pill));
    public static readonly StyledProperty<IBrush?> FocusVisualProperty = AvaloniaProperty.Register<GraphCanvas, IBrush?>(nameof(FocusVisual));
    public static readonly StyledProperty<IBrush?> InfoInkProperty = AvaloniaProperty.Register<GraphCanvas, IBrush?>(nameof(InfoInk));
    public static readonly StyledProperty<IBrush?> WarningInkProperty = AvaloniaProperty.Register<GraphCanvas, IBrush?>(nameof(WarningInk));
    public static readonly StyledProperty<CornerRadius> CardCornerProperty = AvaloniaProperty.Register<GraphCanvas, CornerRadius>(nameof(CardCorner), new(6));
    public static readonly StyledProperty<CornerRadius> GroupCornerProperty = AvaloniaProperty.Register<GraphCanvas, CornerRadius>(nameof(GroupCorner), new(8));
    public IBrush? CardStroke { get => GetValue(CardStrokeProperty); set => SetValue(CardStrokeProperty, value); }
    public IBrush? CardHover { get => GetValue(CardHoverProperty); set => SetValue(CardHoverProperty, value); }
    public IBrush? DimInk { get => GetValue(DimInkProperty); set => SetValue(DimInkProperty, value); }
    public IBrush? Pill { get => GetValue(PillProperty); set => SetValue(PillProperty, value); }
    public IBrush? FocusVisual { get => GetValue(FocusVisualProperty); set => SetValue(FocusVisualProperty, value); }
    public IBrush? InfoInk { get => GetValue(InfoInkProperty); set => SetValue(InfoInkProperty, value); }
    public IBrush? WarningInk { get => GetValue(WarningInkProperty); set => SetValue(WarningInkProperty, value); }
    public CornerRadius CardCorner { get => GetValue(CardCornerProperty); set => SetValue(CardCornerProperty, value); }
    public CornerRadius GroupCorner { get => GetValue(GroupCornerProperty); set => SetValue(GroupCornerProperty, value); }
    private string? _hoverId;
    // Store-style entrance: modules that were not in the previous scene fade and rise into place.
    private GraphScene? _lastScene;
    private System.Collections.Generic.HashSet<string> _entering = new(StringComparer.Ordinal);
    private double _reveal = 1;
    private IDisposable? _revealAnimation;
    private readonly GraphWorkspaceViewModel _vm;
    private readonly bool _mini;
    private readonly GraphViewportState _viewport;
    private Point? _panStart, _marqueeStart;
    private Point _lastPointer;
    private Rect? _marquee;
    private bool _subscribed, _disposed, _constraining;
    private IPointer? _dragPointer;
    private static readonly Cursor PanCursor = new(StandardCursorType.Hand);
    private static readonly Cursor DragCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor ModuleCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor SelectionCursor = new(StandardCursorType.Cross);
    internal bool IsSpatialFocusMode { get; private set; }
    internal string? FocusedModuleId { get; private set; }

    static GraphCanvas() => AffectsRender<GraphCanvas>(InkProperty, SecondaryInkProperty, SurfaceProperty,
        StrokeProperty, AccentProperty, SelectedSurfaceProperty, PageSurfaceProperty, CardStrokeProperty, CardHoverProperty,
        DimInkProperty, PillProperty, FocusVisualProperty, InfoInkProperty, WarningInkProperty, CardCornerProperty, GroupCornerProperty);
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
        Bind(CardStrokeProperty, new DynamicResourceExtension("AppCardStroke"));
        Bind(CardHoverProperty, new DynamicResourceExtension("AppCardHover"));
        Bind(DimInkProperty, new DynamicResourceExtension("AppTextDim"));
        Bind(PillProperty, new DynamicResourceExtension("AccentFillColorDefaultBrush"));
        Bind(FocusVisualProperty, new DynamicResourceExtension("SystemControlFocusVisualPrimaryBrush"));
        Bind(InfoInkProperty, new DynamicResourceExtension("AppInfoForeground"));
        Bind(WarningInkProperty, new DynamicResourceExtension("AppWarningForeground"));
        Bind(CardCornerProperty, new DynamicResourceExtension("AppCornerCard"));
        Bind(GroupCornerProperty, new DynamicResourceExtension("OverlayCornerRadius"));
        PointerExited += (_, _) => SetHover(null);
        PointerTouchPadGestureMagnify += (_, e) =>
        {
            _viewport.Zoom(Math.Exp(e.Delta.Y), e.GetPosition(this));
            e.Handled = true;
        };
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => { EndPointerGesture(); Unsubscribe(); };
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
        RevealNewModules();
        FitIfNeeded();
        KeepModuleVisible();
        InvalidateVisual();
    }
    private void RevealNewModules()
    {
        var scene = _vm.Scene;
        if (ReferenceEquals(scene, _lastScene)) return;
        var previous = _lastScene;
        _lastScene = scene;
        // The first scene after attaching is already where the user expects it; only later changes animate.
        if (previous == null || Views.Motion.Reduced || Integration.MemoryProfile.DisableVisualEffects) return;
        var before = previous.Nodes.Select(n => n.Module.Id).Concat(previous.Groups.Select(g => g.Module.Id))
            .ToHashSet(StringComparer.Ordinal);
        var entering = scene.Nodes.Select(n => n.Module.Id).Concat(scene.Groups.Select(g => g.Module.Id))
            .Where(id => !before.Contains(id)).ToHashSet(StringComparer.Ordinal);
        if (entering.Count == 0) return;
        _revealAnimation?.Dispose();
        _entering = entering;
        _reveal = 0;
        _revealAnimation = Views.Motion.AnimateValue(0, 1, Views.Motion.Slow, value => { _reveal = value; InvalidateVisual(); },
            () => { _entering.Clear(); _revealAnimation = null; }, TopLevel.GetTopLevel(this));
    }
    private IDisposable? Entering(DrawingContext context, string id)
    {
        if (_reveal >= 1 || !_entering.Contains(id)) return null;
        var opacity = context.PushOpacity(_reveal);
        var rise = context.PushTransform(Matrix.CreateTranslation(0, 12 * (1 - _reveal)));
        return new Pop(rise, opacity);
    }
    private sealed class Pop(IDisposable inner, IDisposable outer) : IDisposable
    {
        public void Dispose() { inner.Dispose(); outer.Dispose(); }
    }
    internal bool IsRevealingForTesting => _reveal < 1 && _entering.Count > 0;
    private void OnViewportChanged(object? sender, PropertyChangedEventArgs e)
    { FitIfNeeded(); KeepModuleVisible(); InvalidateVisual(); }
    private void FitIfNeeded() { if (!_viewport.Positioned && Bounds.Width > 0 && Bounds.Height > 0) Fit(); }
    internal void Fit() { _viewport.Fit(_vm.Scene.Bounds, Bounds.Size); KeepModuleVisible(); }
    internal void Zoom(double factor) => _viewport.Zoom(factor, new Point(Bounds.Width / 2, Bounds.Height / 2));
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    { base.OnSizeChanged(e); FitIfNeeded(); KeepModuleVisible(); }

    // Constrain the actual canvas, including gaps between nodes, rather than the scene/window bounding box.
    // A correction is made only when every module would be lost, so ordinary anchored zoom is unchanged.
    private void KeepModuleVisible()
    {
        if (_constraining || Bounds.Width < 1 || Bounds.Height < 1) return;
        var boxes = _vm.Scene.Nodes.Select(node => node.Bounds).ToArray();
        if (boxes.Length == 0)
            boxes = _vm.Scene.Groups.Select(group => new Rect(group.Bounds.TopLeft,
                new Size(Math.Min(260, group.Bounds.Width), Math.Min(32, group.Bounds.Height)))).ToArray();
        if (boxes.Length == 0) return;
        var best = new Vector(double.PositiveInfinity, double.PositiveInfinity);
        foreach (var box in boxes)
        {
            var screen = new Rect(box.X * _viewport.Scale + _viewport.X, box.Y * _viewport.Scale + _viewport.Y,
                box.Width * _viewport.Scale, box.Height * _viewport.Scale);
            var width = Math.Min(48, Math.Min(screen.Width, Bounds.Width));
            var height = Math.Min(32, Math.Min(screen.Height, Bounds.Height));
            var dx = screen.Right < width ? width - screen.Right :
                screen.Left > Bounds.Width - width ? Bounds.Width - width - screen.Left : 0;
            var dy = screen.Bottom < height ? height - screen.Bottom :
                screen.Top > Bounds.Height - height ? Bounds.Height - height - screen.Top : 0;
            if (dx == 0 && dy == 0) return;
            var move = new Vector(dx, dy);
            if (move.SquaredLength < best.SquaredLength) best = move;
        }
        _constraining = true;
        try { _viewport.Pan(best.X, best.Y); }
        finally { _constraining = false; }
    }

    private void UpdatePointerCursor(Point point, KeyModifiers modifiers = KeyModifiers.None)
    {
        Cursor = _panStart != null ? DragCursor : _marqueeStart != null ? SelectionCursor :
            _vm.Scene.Hit(_viewport.ToWorld(point)) != null ? ModuleCursor :
            modifiers.HasFlag(KeyModifiers.Shift) ? SelectionCursor : PanCursor;
    }
    private void EndPointerGesture()
    {
        var pointer = _dragPointer;
        _dragPointer = null;
        _panStart = _marqueeStart = null;
        _marquee = null;
        pointer?.Capture(null);
        UpdatePointerCursor(_lastPointer);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(PageSurface ?? Brushes.Transparent, new Rect(Bounds.Size));
        using (context.PushTransform(Matrix.CreateScale(_viewport.Scale, _viewport.Scale) *
            Matrix.CreateTranslation(_viewport.X, _viewport.Y)))
        {
            var groupCorner = GroupCorner.TopLeft;
            foreach (var group in _vm.Scene.Groups)
            {
                var id = group.Module.Id;
                using var entering = Entering(context, id);
                var selected = _vm.SelectedModuleIds.Contains(id);
                context.DrawRectangle(null, new Pen(Stroke, 1), new RoundedRect(group.Bounds, groupCorner));
                // The header row is the group's hit target, so it carries hover and selection.
                var header = new Rect(group.Bounds.X + 1, group.Bounds.Y + 1, group.Bounds.Width - 2, 30);
                var fill = selected ? SelectedSurface : _hoverId == id ? CardHover : null;
                if (fill != null) context.DrawRectangle(fill, null, new RoundedRect(header, groupCorner - 1, groupCorner - 1, 0, 0));
                if (selected) SelectionPill(context, header);
                var nameWidth = group.Bounds.Width - 24 - Badge(context, id, new Point(group.Bounds.Right - 12, group.Bounds.Y + 8));
                Text(context, group.Module.Name, group.Bounds.TopLeft + new Vector(12, 7), nameWidth, 12, true, ink: SecondaryInk);
                if (IsSpatialFocusMode && FocusedModuleId == id) FocusRing(context, group.Bounds, groupCorner);
            }
            for (var i = 0; i < _vm.Scene.Edges.Count; i++)
            {
                var edge = _vm.Scene.Edges[i];
                // A relation appears with whichever endpoint is new.
                using var entering = Entering(context, _entering.Contains(edge.Relation.SourceId) ? edge.Relation.SourceId : edge.Relation.TargetId);
                DrawEdge(context, edge, i);
            }
            var corner = CardCorner.TopLeft;
            var dashed = new DashStyle(new[] { 4d, 3d }, 0);
            foreach (var node in _vm.Scene.Nodes)
            {
                var id = node.Module.Id;
                using var entering = Entering(context, id);
                var selected = _vm.SelectedModuleIds.Contains(id);
                var fill = selected ? SelectedSurface : _hoverId == id ? CardHover : Surface;
                // References live outside the current scope: a dashed outline marks them as borrowed.
                var pen = node.IsReference ? new Pen(Stroke, 1, dashed) : new Pen(CardStroke, 1);
                context.DrawRectangle(fill, pen, new RoundedRect(node.Bounds, corner));
                if (selected) SelectionPill(context, node.Bounds);
                var titleX = 12.0;
                if (node.IsReference)
                {
                    DrawIcon(context, "Icon.Open", new Rect(node.Bounds.X + 12, node.Bounds.Y + 13, 12, 12), DimInk);
                    titleX += 18;
                }
                var badgeWidth = Badge(context, id, new Point(node.Bounds.Right - 12, node.Bounds.Y + 12));
                Text(context, node.Module.Name, node.Bounds.TopLeft + new Vector(titleX, 10),
                    node.Bounds.Width - titleX - 12 - badgeWidth, 14, true);
                Text(context, GraphLabels.ModuleSummary(node.Module.Summary), node.Bounds.TopLeft + new Vector(12, 36),
                    node.Bounds.Width - 24, 12, false, 34);
                if (IsSpatialFocusMode && FocusedModuleId == id) FocusRing(context, node.Bounds, corner);
            }
            if (_marquee is { } marquee)
            {
                IBrush? wash = Accent is ISolidColorBrush accent ? new SolidColorBrush(accent.Color, 0.12) : null;
                context.DrawRectangle(wash, new Pen(Accent, 1 / _viewport.Scale), marquee);
            }
        }
    }
    /// <summary>Same 3×16 accent pill as list selection, inset inside the left edge.</summary>
    private void SelectionPill(DrawingContext context, Rect bounds) =>
        context.DrawRectangle(Pill ?? Accent, null, new RoundedRect(
            new Rect(bounds.X + 2, bounds.Center.Y - 8, 3, 16), 1.5));
    /// <summary>Fluent focus visual: solid primary stroke offset outside the element.</summary>
    private void FocusRing(DrawingContext context, Rect bounds, double corner)
    {
        var offset = 3 / _viewport.Scale;
        context.DrawRectangle(null, new Pen(FocusVisual ?? Ink, 2 / _viewport.Scale),
            new RoundedRect(bounds.Inflate(offset), corner + offset));
    }
    /// <summary>Draws the module's work state right-aligned to <paramref name="topRight"/>; returns the width used.</summary>
    private double Badge(DrawingContext context, string moduleId, Point topRight)
    {
        if (_vm.ModuleActivity(moduleId) is not { } activity) return 0;
        var ink = activity.Kind == GraphActivityKind.Attention ? WarningInk : InfoInk;
        var label = Measure(activity.Label, 120, 12, false, 16, ink);
        var textX = topRight.X - label.Width;
        context.DrawText(label, new Point(textX, topRight.Y));
        context.DrawEllipse(ink, null, new Point(textX - 7, topRight.Y + label.Height / 2), 3, 3);
        return label.Width + 18;
    }
    private void DrawIcon(DrawingContext context, string key, Rect target, IBrush? brush)
    {
        if (brush == null || !this.TryFindResource(key, ActualThemeVariant, out var value) || value is not Geometry geometry) return;
        var bounds = geometry.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var scale = Math.Min(target.Width / bounds.Width, target.Height / bounds.Height);
        using (context.PushTransform(Matrix.CreateTranslation(-bounds.X, -bounds.Y) * Matrix.CreateScale(scale, scale) *
            Matrix.CreateTranslation(target.X, target.Y)))
            context.DrawGeometry(brush, null, geometry);
    }
    private void SetHover(string? id)
    {
        if (_hoverId == id) return;
        _hoverId = id;
        InvalidateVisual();
    }
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
        // Relations of the selection step forward; the rest recede to tertiary ink.
        var related = _vm.SelectedModuleIds.Contains(edge.Relation.SourceId) || _vm.SelectedModuleIds.Contains(edge.Relation.TargetId);
        var ink = related ? SecondaryInk : DimInk ?? SecondaryInk;
        var width = related ? 1.6 : 1.2;
        context.DrawGeometry(null, new Pen(ink, width, flow ? new DashStyle(new[] { 5d, 3d }, 0) : null), line);
        var direction = new Vector(to.X - last.X, to.Y - last.Y);
        if (direction.Length > 0)
        {
            direction /= direction.Length;
            var normal = new Vector(-direction.Y, direction.X);
            var head = new StreamGeometry();
            using (var path = head.Open())
            {
                path.BeginFigure(to, true);
                path.LineTo(to - direction * 8 + normal * 4);
                path.LineTo(to - direction * 8 - normal * 4);
                path.EndFigure(true);
            }
            context.DrawGeometry(ink, null, head);
        }
        if (!_mini)
        {
            var center = new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2 - 15 - index % 3 * 14);
            var label = Measure(edge.Label, 210, 12, false, 16, related ? SecondaryInk : DimInk);
            var labelBounds = new Rect(center.X - label.Width / 2 - 6, center.Y, label.Width + 12, label.Height + 2);
            context.DrawRectangle(PageSurface ?? Surface ?? Brushes.Transparent, null, new RoundedRect(labelBounds, 4));
            context.DrawText(label, labelBounds.TopLeft + new Vector(6, 1));
        }
    }
    private FormattedText Measure(string value, double width, double size, bool strong, double height = 20, IBrush? ink = null)
    {
        var font = TextElement.GetFontFamily(this);
        return new FormattedText(value.Replace('\r', ' ').Replace('\n', ' '), CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface(font, FontStyle.Normal, strong ? FontWeight.SemiBold : FontWeight.Normal),
            size, ink ?? (strong ? Ink : SecondaryInk))
        { MaxTextWidth = Math.Max(1, width), MaxTextHeight = height, Trimming = TextTrimming.CharacterEllipsis };
    }
    private void Text(DrawingContext context, string value, Point point, double width, double size, bool strong, double height = 20, IBrush? ink = null)
    {
        if (!string.IsNullOrEmpty(value) && width > 8) context.DrawText(Measure(value, width, size, strong, height, ink), point);
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
                var items = new System.Collections.Generic.List<MenuItem>();
                if (!_mini) items.Add(Menu("属性", () => _vm.RevealPanel(GraphWorkspaceViewModel.PropertiesPageId)));
                items.Add(Menu("展开", () => _vm.DrillInto(hit)));
                items.Add(Menu("打开会话", () => _vm.OpenChat(hit, null)));
                if (_mini) items.Add(Menu("在主图打开", () => _vm.NavigateToModule(hit)));
                ContextMenu = new ContextMenu { ItemsSource = items };
            }
            else ContextMenu = new ContextMenu { ItemsSource = new[]
            { Menu("展开整图", _vm.ExpandAll), Menu("适应", Fit) } };
            return;
        }
        if (!properties.IsLeftButtonPressed && !properties.IsMiddleButtonPressed) return;
        if (properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && hit == null)
        { _marqueeStart = _viewport.ToWorld(point); _dragPointer = e.Pointer; e.Pointer.Capture(this); }
        else if (properties.IsLeftButtonPressed && hit != null)
        {
            var additive = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0;
            FocusedModuleId = hit;
            if (e.ClickCount >= 2 && !additive) _vm.DrillInto(hit);
            else _vm.SelectModule(hit, additive);
        }
        else { _panStart = point; _dragPointer = e.Pointer; e.Pointer.Capture(this); }
        UpdatePointerCursor(point, e.KeyModifiers);
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
            SetHover(id);
            var module = _vm.Snapshot.Modules.FirstOrDefault(m => m.Id == id);
            var activity = id == null ? null : _vm.ModuleActivity(id);
            ToolTip.SetTip(this, module == null ? null : module.Name + Environment.NewLine + GraphLabels.ModuleSummary(module.Summary) +
                (activity == null ? "" : Environment.NewLine + activity.Detail));
        }
        _lastPointer = point;
        UpdatePointerCursor(point, e.KeyModifiers);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_marquee is { } box)
            _vm.SelectModules(_vm.Scene.Nodes.Where(n => box.Intersects(n.Bounds)).Select(n => n.Module.Id),
                e.KeyModifiers.HasFlag(KeyModifiers.Control));
        _lastPointer = e.GetPosition(this);
        EndPointerGesture();
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    { base.OnPointerCaptureLost(e); EndPointerGesture(); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            _viewport.Zoom(Math.Pow(1.14, e.Delta.Y), e.GetPosition(this));
        else
        {
            // Wheel deltas do not identify mouse versus touchpad; both follow the same pan contract.
            var delta = e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Delta.X == 0
                ? new Vector(e.Delta.Y, 0) : e.Delta;
            _viewport.Pan(delta.X * 48, delta.Y * 48);
        }
        _lastPointer = e.GetPosition(this);
        UpdatePointerCursor(_lastPointer, e.KeyModifiers);
        e.Handled = true;
    }
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (e.NavigationMethod == NavigationMethod.Tab) EnterSpatialFocus();
    }
    protected override void OnLostFocus(FocusChangedEventArgs e)
    { base.OnLostFocus(e); IsSpatialFocusMode = false; EndPointerGesture(); }
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
            { _vm.SelectModule(selected); e.Handled = true; }
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
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _revealAnimation?.Dispose();
        EndPointerGesture(); Unsubscribe();
    }
}
