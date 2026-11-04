using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.Views;

internal sealed class DockRegionView : UserControl
{
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    // 页签栏不显示横向滚动条：滚动条滑块的基线会和选中态的强调描边抢同一条位置。
    // 滚轮横向浏览仍可用（TabScrollForTesting / PointerWheel 处理器）。
    private readonly ScrollViewer _tabScroll = new()
    {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        BringIntoViewOnFocusChange = false
    };
    private string? _selectedId;
    // Fluent 的选中态改用左上描边表达；其它皮肤保持原有指示条以外的样式，不注入该描边类。
    private bool _accentStroke;
    private readonly Grid _pages = new();
    private readonly TextBlock _empty = new() { Text = "暂无页面", Margin = new Thickness(14, 12), IsVisible = false };
    private readonly Border _dropTarget = new() { BorderThickness = new Thickness(2), IsVisible = false, IsHitTestVisible = false };
    private readonly Border _headerRule = new();
    private readonly Dictionary<string, Border> _strokes = new();
    private IDisposable? _strokeAnimation;
    private int _strokeGeneration;
    private IDisposable? _tabScrollAnimation;
    private int _tabScrollGeneration;
    private readonly Dictionary<string, (Grid Row, Button Tab)> _buttons = new();
    private WorkbenchLayoutState? _state;
    private readonly DockPanel _header = new() { Height = 40, LastChildFill = true };
    private readonly Grid _root = new() { RowDefinitions = new RowDefinitions("40,*") };
    private double _rowHeight = 40;
    private readonly MenuItem _floatRegionItem = new() { Header = "浮动整栏" };
    private readonly ContextMenu _regionMenu = new();
    private Button? _hideButton;
    internal ContextMenu RegionMenuForTesting => _regionMenu;
    private DockZone _zone;
    public DockZone Zone
    {
        get => _zone;
        set
        {
            _zone = value;
            // 收起键指向折叠方向：左区 ←、右区 →、底区 ↓（此前统一 ChevronDown，
            // 左/右区点击后视觉上仍朝下，语义错位）。
            var icon = value switch
            {
                DockZone.Left => "ChevronLeft",
                DockZone.Right => "ChevronRight",
                _ => "ChevronDown",
            };
            if (_hideButton?.Content is PathIcon path)
            {
                if (Application.Current?.TryGetResource("Icon." + icon, null, out var geometry) == true)
                    path.Data = geometry as Geometry;
                path.Bind(PathIcon.DataProperty, new DynamicResourceExtension("Icon." + icon));
            }
        }
    }
    public string? FloatingId { get; set; }
    public Func<string, string?>? BadgeText { get; set; }
    private string Label(WorkbenchPage page) => BadgeText?.Invoke(page.Id) is { Length: > 0 } badge ? page.Title + " · " + badge : page.Title;
    internal void RefreshBadges()
    {
        foreach (var page in Catalog)
            if (_buttons.TryGetValue(page.Id, out var entry))
            { entry.Tab.Content = Label(page); }
    }
    public IReadOnlyCollection<WorkbenchPage> Catalog { get; set; } = Array.Empty<WorkbenchPage>();
    public event Action<string, DockZone>? OpenRequested;
    public event Action<string, DockZone>? MoveRequested;
    public event Action<string>? SelectRequested;
    public event Action<string>? CloseRequested;
    public event Action<string>? PinRequested;
    public event Action<DockZone>? HideRequested;
    // 拖拽位置用屏幕坐标上报：跨窗口（浮动栏 → 主窗停靠区）也能落到同一套命中逻辑。
    public event Action<string, PixelPoint>? PageDragged;
    public event Action<PixelPoint?>? DragPositionChanged;
    public event Action<string, int>? ReorderRequested;
    public event Action<string>? FloatPageRequested;
    public event Action? FloatRegionRequested;

    public DockRegionView()
    {
        _empty.Classes.Add("dockEmpty");
        AutomationProperties.SetName(_empty, "暂无页面，可用打开功能页按钮添加页面");
        DetachedFromVisualTree += (_, _) =>
        {
            _strokeGeneration++; _strokeAnimation?.Dispose(); _strokeAnimation = null;
            _tabScrollGeneration++; _tabScrollAnimation?.Dispose(); _tabScrollAnimation = null;
        };
        _dropTarget.Classes.Add("dockDropTarget");
        _header.Classes.Add("dockHeader");
        _headerRule.Classes.Add("dockHeaderRule");
        var add = WorkbenchUi.IconButton("Add", "打开功能页", showToolTip: false);
        var hide = WorkbenchUi.IconButton("ChevronDown", "隐藏此区域（页面与进程保留）");
        _hideButton = hide;
        // 整栏浮动保留右键菜单入口；栏头不再放单独按钮——页签拖到非停靠区即可变成独立窗口。
        var regionMenu = _regionMenu;
        _floatRegionItem.Click += (_, _) => FloatRegionRequested?.Invoke();
        regionMenu.Items.Add(_floatRegionItem);
        // 右键落到页签行之外的栏头空白时打开整栏菜单（浮动整栏/停靠回原栏）。
        // 页签自身有 ContextMenu，原生右键会先打开页签菜单；栏头空白没有菜单宿主，
        // 因此这里显式接管。ContextRequested 只在部分输入路径触发，PointerReleased
        // 兜底可保证鼠标、触控与无头合成的右键都能打开同一个入口。
        bool OverTabRow(object? source)
        {
            if (source is not Visual visual) return false;
            var ancestors = visual.GetVisualAncestors().Prepend(visual).ToArray();
            return _buttons.Values.Any(item => ancestors.Contains(item.Row));
        }
        void OpenRegionMenu()
        {
            if (!regionMenu.IsOpen) regionMenu.Open(_header);
        }
        _header.AddHandler(ContextRequestedEvent, (_, e) =>
        {
            if (OverTabRow(e.Source)) return;
            OpenRegionMenu();
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);
        _header.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Right || OverTabRow(e.Source)) return;
            OpenRegionMenu();
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        add.Classes.Add("toolButton"); hide.Classes.Add("toolButton");
        add.Margin = new Thickness(0, 0, 2, 0); hide.Margin = new Thickness(0, 0, 6, 0);
        add.VerticalAlignment = hide.VerticalAlignment = VerticalAlignment.Center;
        hide.Click += (_, _) => HideRequested?.Invoke(Zone);
        var header = _header;
        DockPanel.SetDock(hide, Dock.Right); header.Children.Add(hide);
        DockPanel.SetDock(add, Dock.Right); header.Children.Add(add);
        _tabScroll.Content = _tabs;
        AutomationProperties.SetName(_tabScroll, "页签栏，可用滚轮横向浏览");
        _tabScroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            var delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
            if (ScrollTabs(delta)) e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        header.Children.Add(_tabScroll);
        var root = _root;
        root.Children.Add(header); root.Children.Add(_headerRule); Grid.SetRow(_pages, 1); root.Children.Add(_pages);
        Grid.SetRow(_empty, 1); root.Children.Add(_empty);
        Grid.SetRowSpan(_dropTarget, 2); root.Children.Add(_dropTarget);
        Content = root;
        add.Click += (_, _) =>
        {
            var menu = new MenuFlyout();
            foreach (var page in Catalog)
            {
                var item = new MenuItem { Header = Label(page) };
                var icon = new PathIcon { Width = 16, Height = 16 };
                icon.Bind(PathIcon.DataProperty, new DynamicResourceExtension("Icon." + page.Icon));
                item.Icon = icon;
                item.Click += (_, _) => OpenRequested?.Invoke(page.Id, Zone);
                menu.Items.Add(item);
            }
            menu.ShowAt(add);
        };
    }

    internal bool IsDropTarget => _dropTarget.IsVisible;
    internal ScrollViewer TabScrollForTesting => _tabScroll;
    internal bool ScrollTabs(double delta)
    {
        var maximum = Math.Max(0, _tabScroll.Extent.Width - _tabScroll.Viewport.Width);
        if (maximum <= 0 || delta == 0) return false;
        // DESK-1：一次滚轮/触摸格步进从 48 提到 72（约 6 个字宽），长页签列表不用反复滚。
        _tabScrollGeneration++;
        _tabScrollAnimation?.Dispose(); _tabScrollAnimation = null;
        _tabScroll.Offset = new Vector(Math.Clamp(_tabScroll.Offset.X - delta * 72, 0, maximum), 0);
        return true;
    }
    internal void SetDropTarget(bool active) => _dropTarget.IsVisible = active;

    /// <summary>页面被拖入本栏后短暂淡入，避免内容在布局动画结束时直接闪现。</summary>
    internal void AnimatePageArrival()
    {
        if (_selectedId is null || Motion.Reduced) return;
        if (Catalog.FirstOrDefault(page => page.Id == _selectedId)?.Content is not { } content) return;
        content.Opacity = 0.35;
        _ = Motion.AnimateValue(0.35, 1, Motion.Fast,
            value => content.Opacity = value, () => content.Opacity = 1);
    }

    internal bool CancelDrag()
    {
        var canceled = false;
        foreach (var tab in _buttons.Values.Select(entry => entry.Tab).OfType<DockTabButton>()) canceled |= tab.CancelDrag();
        return canceled;
    }

    public void ReleasePages(WorkbenchLayoutState state)
    {
        foreach (var control in _pages.Children.ToArray())
            if (!Catalog.Any(page => ReferenceEquals(page.Content, control) && PageIds(state).Contains(page.Id)))
                _pages.Children.Remove(control);
    }

    private List<string> PageIds(WorkbenchLayoutState state) => FloatingId is null ? state.Pages[Zone] :
        state.Floating.FirstOrDefault(item => item.Id == FloatingId)?.Pages ?? [];
    private string? SelectedPage(WorkbenchLayoutState state) => FloatingId is null ? state.Selected[Zone] :
        state.Floating.FirstOrDefault(item => item.Id == FloatingId)?.Selected;

    internal void DetachPage(Control content) => _pages.Children.Remove(content);
    internal void ApplyAppearance(WorkbenchAppearance settings)
    {
        _tabs.Height = settings.RowHeight;
        _rowHeight = settings.RowHeight;
        _header.Height = settings.RowHeight;
        _root.RowDefinitions[0].Height = new GridLength(settings.RowHeight);
        var accentChanged = _accentStroke != (settings.SkinResolution.Skin == Features.Skins.SkinId.Fluent);
        _accentStroke = settings.SkinResolution.Skin == Features.Skins.SkinId.Fluent;
        foreach (var entry in _buttons.Values)
        {
            entry.Tab.Height = entry.Tab.MinHeight = _accentStroke ? settings.RowHeight : settings.RowHeight - 2;
            Grid.SetColumnSpan(entry.Tab, _accentStroke ? 2 : 1);
            entry.Tab.Classes.Set("fluentDockTab", _accentStroke);
            entry.Row.Classes.Set("fluentDockRow", _accentStroke);
        }
        Classes.Set("fluentDock", _accentStroke);
        _headerRule.IsVisible = !_accentStroke;
        if (accentChanged) UpdateStroke(_selectedId, animate: false);
    }

    // The row owns the contour, above both the tab presenter and its close button.
    // One cancellable sequence means rapid selections always fade the currently visible contour
    // before lighting the latest destination; no delayed callback can resurrect an old selection.
    private void UpdateStroke(string? selected, bool animate)
    {
        _strokeAnimation?.Dispose();
        _strokeAnimation = null;
        var generation = ++_strokeGeneration;
        var target = _accentStroke && selected is not null ? _strokes.GetValueOrDefault(selected) : null;
        if (!animate || Motion.Reduced)
        {
            foreach (var stroke in _strokes.Values) stroke.Opacity = ReferenceEquals(stroke, target) ? 1 : 0;
            return;
        }
        var fading = _strokes.Values.Where(stroke => stroke.Opacity > 0).Select(stroke => (Stroke: stroke, From: stroke.Opacity)).ToArray();
        void FadeIn()
        {
            if (generation != _strokeGeneration || target is null) return;
            _strokeAnimation = Motion.AnimateValue(0, 1, Motion.Fast, value => target.Opacity = value);
        }
        if (fading.Length == 0) { FadeIn(); return; }
        _strokeAnimation = Motion.AnimateValue(0, 1, Motion.Fast,
            value => { foreach (var item in fading) item.Stroke.Opacity = item.From * (1 - value); }, FadeIn);
    }

    public void Render(WorkbenchLayoutState state)
    {
        _state = state;
        _floatRegionItem.Header = FloatingId is null ? "浮动整栏" : "停靠回原栏";
        var ids = PageIds(state).Where(id => Catalog.Any(page => page.Id == id)).ToList();
        _empty.IsVisible = ids.Count == 0;
        var selected = ids.Contains(SelectedPage(state)!) ? SelectedPage(state) : ids.FirstOrDefault();
        foreach (var id in _buttons.Keys.Except(ids).ToArray())
        { _tabs.Children.Remove(_buttons[id].Row); _buttons.Remove(id); _strokes.Remove(id); }
        for (var index = 0; index < ids.Count; index++)
        {
            var id = ids[index];
            var page = Catalog.First(p => p.Id == id);
            if (!_buttons.TryGetValue(id, out var entry))
            {
                // DESK-1：关闭按钮瘦身后列宽同步收紧（28→22），页签更紧凑、滑动手感更顺。
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,22"), MinWidth = 76, MaxWidth = 180 };
                row.Classes.Add("dockTabRow");
                var tab = new DockTabButton { Content = Label(page), Height = _accentStroke ? _rowHeight : _rowHeight - 2, MinHeight = _accentStroke ? _rowHeight : _rowHeight - 2,
                    VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
                tab.Classes.Add("subtle"); tab.Classes.Add("dockTab");
                tab.Classes.Set("fluentDockTab", _accentStroke);
                row.Classes.Set("fluentDockRow", _accentStroke);
                AutomationProperties.SetName(tab, "选择" + page.Title);
                AutomationProperties.SetAutomationId(tab, "dock-tab-" + id);
                tab.Click += (_, _) => SelectRequested?.Invoke(id);
                tab.Dropped += point => PageDragged?.Invoke(id, point);
                tab.DragPositionChanged += point => DragPositionChanged?.Invoke(point);
                var menu = new ContextMenu();
                var pin = new MenuItem();
                pin.Click += (_, _) => PinRequested?.Invoke(id); menu.Items.Add(pin);
                var floatPage = new MenuItem { Header = "浮动此页", Tag = "dock-float" };
                floatPage.Click += (_, _) => FloatPageRequested?.Invoke(id); menu.Items.Add(floatPage);
                foreach (var target in Enum.GetValues<DockZone>())
                {
                    var destination = target;
                    var move = new MenuItem { Header = target switch { DockZone.Left => "移到左栏", DockZone.Right => "移到右栏", _ => "移到底栏" } };
                    move.Click += (_, _) => MoveRequested?.Invoke(id, destination);
                    menu.Items.Add(move);
                }
                foreach (var offset in new[] { -1, 1 })
                {
                    var delta = offset;
                    var reorder = new MenuItem { Header = offset < 0 ? "向前排列" : "向后排列" };
                    reorder.Click += (_, _) => ReorderRequested?.Invoke(id, PageIds(_state!).IndexOf(id) + delta);
                    menu.Items.Add(reorder);
                }
                var closeItem = new MenuItem { Header = "关闭页签", Tag = "dock-close" };
                closeItem.Click += (_, _) => CloseRequested?.Invoke(id);
                menu.Items.Add(closeItem); tab.ContextMenu = menu;
                var close = WorkbenchUi.IconButton("DockDismiss", "关闭页签：" + page.Title, showToolTip: false);
                close.Classes.Add("dockTabClose");
                // 完整的 12 单位实心轮廓按原尺寸呈现，高 DPI 下仍保持连续笔画。
                close.Width = close.MinWidth = close.Height = close.MinHeight = 18; close.Padding = new Thickness(3);
                if (close.Content is PathIcon closeIcon) closeIcon.Width = closeIcon.Height = 12;
                close.VerticalAlignment = VerticalAlignment.Center; close.Margin = new Thickness(0, 0, 3, 0);
                AutomationProperties.SetAutomationId(close, "dock-close-" + id);
                close.Click += (_, _) => CloseRequested?.Invoke(id);
                Grid.SetColumnSpan(tab, _accentStroke ? 2 : 1);
                close.ZIndex = 2;
                row.Children.Add(tab); Grid.SetColumn(close, 1); row.Children.Add(close);
                var stroke = new Border { BorderThickness = new Thickness(2, 2, 0, 0), CornerRadius = new CornerRadius(0),
                    IsHitTestVisible = false, Opacity = 0, ZIndex = 1, Margin = new Thickness(0, 1, 0, 1) };
                stroke.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("AppAccent"));
                Grid.SetColumnSpan(stroke, 2);
                row.Children.Add(stroke);
                _strokes.Add(id, stroke);
                entry = (row, tab); _buttons.Add(id, entry);
                _tabs.Children.Insert(index, row);
            }
            else if (_tabs.Children.IndexOf(entry.Row) != index)
            { _tabs.Children.Remove(entry.Row); _tabs.Children.Insert(index, entry.Row); }
            entry.Tab.Classes.Set("selected", id == selected);
            entry.Row.Classes.Set("selected", id == selected);
            var pinned = state.PinnedPages.Contains(id);
            ((MenuItem)entry.Tab.ContextMenu!.Items[0]!).Header = pinned ? "取消固定页签" : "固定页签";
            entry.Tab.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Tag as string == "dock-close").IsEnabled = !pinned;
            entry.Row.Children.OfType<Button>().Single(button => Grid.GetColumn(button) == 1).IsEnabled = !pinned;
            entry.Tab.Content = Label(page);
            AutomationProperties.SetName(entry.Tab, "选择" + page.Title + (pinned ? " · 已固定" : ""));
            if (!_pages.Children.Contains(page.Content)) _pages.Children.Add(page.Content);
            page.Content.IsVisible = id == selected;
        }
        if (_selectedId != selected)
        {
            var animate = _selectedId is not null;
            UpdateStroke(selected, animate);
            RevealSelectedTab(selected, animate);
        }
        _selectedId = selected;
    }

    private void RevealSelectedTab(string? selected, bool animate)
    {
        _tabScrollAnimation?.Dispose(); _tabScrollAnimation = null;
        var generation = ++_tabScrollGeneration;
        if (selected is null || !_buttons.TryGetValue(selected, out var entry)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _tabScrollGeneration || VisualRoot is null) return;
            var viewport = _tabScroll.Viewport.Width;
            if (viewport <= 0) return;
            var from = _tabScroll.Offset.X;
            var left = entry.Row.Bounds.X;
            var right = entry.Row.Bounds.Right;
            var target = left < from ? left : right > from + viewport ? right - viewport : from;
            target = Math.Clamp(target, 0, Math.Max(0, _tabScroll.Extent.Width - viewport));
            if (!animate || Motion.Reduced || Integration.MemoryProfile.DisableVisualEffects)
                _tabScroll.Offset = new Vector(target, 0);
            else _tabScrollAnimation = Motion.AnimateValue(from, target, Motion.StandardDuration,
                value => _tabScroll.Offset = new Vector(value, 0));
        }, DispatcherPriority.Loaded);
    }

    private sealed class DockTabButton : Button
    {
        protected override Type StyleKeyOverride => typeof(Button);
        private Point? _start;
        private Point _lastReported;
        private bool _dragging;
        private bool _suppressClick;
        public event Action<PixelPoint>? Dropped;
        public event Action<PixelPoint?>? DragPositionChanged;

        private PixelPoint ScreenPoint(PointerEventArgs e)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is null) return default;
            var point = e.GetPosition(top);
            // 无头自测没有真实屏幕句柄：退回窗口坐标，与主窗原点重合时结果一致。
            try { return top.PointToScreen(point); }
            catch (ArgumentException) { return new PixelPoint((int)point.X, (int)point.Y); }
        }
        public bool CancelDrag()
        {
            if (_start is null) return false;
            _start = null; _dragging = false; _suppressClick = true; Cursor = null;
            DragPositionChanged?.Invoke(null);
            return true;
        }
        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            CancelDrag();
            base.OnPointerCaptureLost(e);
        }
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _start = e.GetPosition(TopLevel.GetTopLevel(this)); _dragging = false; _suppressClick = false;
                // 显式捕获：跨窗口（浮动栏 → 主窗）拖动时仍能收到移动与释放，否则一出窗口就丢事件。
                e.Pointer.Capture(this);
            }
        }
        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_start is not { } start) return;
            var point = e.GetPosition(TopLevel.GetTopLevel(this));
            if (!_dragging)
            {
                // UI-1：统一 4px 拖拽死区（切比雪夫度量），位移 < 4px 绝不起拖，过滤单击微颤。
                if (!DragDeadZone.HasStarted(start, point)) return;
                _dragging = true; _suppressClick = true;
                Cursor = new Cursor(StandardCursorType.SizeAll);
                _lastReported = start;
            }
            // UI-1：浮动页签拖动卡顿的根因是每帧都上报位置、触发全量命中测试与重排。
            // 累计位移不到 4px 就不上报，拖拽中的无谓重布局被压掉，同时抑制微晃动。
            if (!DragDeadZone.ShouldReport(_lastReported, point)) return;
            _lastReported = point;
            DragPositionChanged?.Invoke(ScreenPoint(e));
        }
        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            var screen = ScreenPoint(e);
            var drop = _dragging;
            base.OnPointerReleased(e);
            _start = null; _dragging = false; _suppressClick = false; Cursor = null;
            DragPositionChanged?.Invoke(null);
            if (drop) Dropped?.Invoke(screen);
        }
        protected override void OnClick() { if (!_suppressClick) base.OnClick(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _start is not null)
            { CancelDrag(); e.Handled = true; return; }
            base.OnKeyDown(e);
        }
    }
}

internal static class WorkbenchUi
{
    public static Button IconButton(string icon, string title, bool showToolTip = true) => IconButton(icon, title, 30, showToolTip);
    public static Button IconButton(string icon, string title, double size, bool showToolTip = true)
    {
        object? geometry = null;
        var resourceKey = "Icon." + icon;
        Application.Current?.TryGetResource(resourceKey, null, out geometry);
        if (geometry is null) { resourceKey = "Icon.AppsList"; Application.Current?.TryGetResource(resourceKey, null, out geometry); }
        var path = new PathIcon { Data = geometry as Geometry, Width = 16, Height = 16 };
        path.Bind(PathIcon.DataProperty, new DynamicResourceExtension(resourceKey));
        var button = new Button { Width = size, Height = size, MinWidth = size, MinHeight = size, Padding = new Thickness(6),
            Content = path };
        button.Classes.Add("iconButton");
        // Accessible names remain available without hover popups.
        AutomationProperties.SetName(button, title);
        return button;
    }
}
