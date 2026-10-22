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
    private readonly ScrollViewer _tabScroll = new()
    {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
    };
    private string? _selectedId;
    private readonly Grid _pages = new();
    private readonly TextBlock _empty = new() { Text = "暂无页面", Margin = new Thickness(14, 12), IsVisible = false };
    private readonly Border _dropTarget = new() { BorderThickness = new Thickness(2), IsVisible = false, IsHitTestVisible = false };
    private readonly Border _headerRule = new();
    private readonly Dictionary<string, (Grid Row, Button Tab)> _buttons = new();
    private WorkbenchLayoutState? _state;
    private readonly DockPanel _header = new() { Height = 40, LastChildFill = true };
    private readonly Grid _root = new() { RowDefinitions = new RowDefinitions("40,*") };
    private double _rowHeight = 40;
    private readonly MenuItem _floatRegionItem = new() { Header = "浮动整栏" };
    private readonly ContextMenu _regionMenu = new();
    private Button? _hideButton;
    private Button? _floatButton;
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
            { entry.Tab.Content = Label(page); ToolTip.SetTip(entry.Tab, Label(page)); }
    }
    public IReadOnlyCollection<WorkbenchPage> Catalog { get; set; } = Array.Empty<WorkbenchPage>();
    public event Action<string, DockZone>? OpenRequested;
    public event Action<string, DockZone>? MoveRequested;
    public event Action<string>? SelectRequested;
    public event Action<string>? CloseRequested;
    public event Action<string>? PinRequested;
    public event Action<DockZone>? HideRequested;
    public event Action<string, Point>? PageDragged;
    public event Action<Point?>? DragPositionChanged;
    public event Action<string, int>? ReorderRequested;
    public event Action<string>? FloatPageRequested;
    public event Action? FloatRegionRequested;

    public DockRegionView()
    {
        _empty.Classes.Add("dockEmpty");
        ToolTip.SetTip(_empty, "点击 + 打开功能页，也可以把页签拖到这里。");
        _dropTarget.Classes.Add("dockDropTarget");
        _header.Classes.Add("dockHeader");
        _headerRule.Classes.Add("dockHeaderRule");
        var add = WorkbenchUi.IconButton("Add", "打开功能页");
        var hide = WorkbenchUi.IconButton("ChevronDown", "隐藏此区域（页面与进程保留）");
        _hideButton = hide;
        // 整栏浮动必须有可见入口：页签占满栏头时右键只能落到页签菜单，
        // 只靠栏头空白右击会让“浮动整栏/变成独立窗口”事实上不可达。
        var floatRegion = WorkbenchUi.IconButton("WindowNew", "浮动整栏（变为独立窗口）");
        _floatButton = floatRegion;
        floatRegion.Click += (_, _) => FloatRegionRequested?.Invoke();
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
        add.Classes.Add("toolButton"); hide.Classes.Add("toolButton"); floatRegion.Classes.Add("toolButton");
        add.Margin = new Thickness(0, 0, 2, 0); hide.Margin = new Thickness(0, 0, 6, 0);
        floatRegion.Margin = new Thickness(0, 0, 2, 0);
        add.VerticalAlignment = hide.VerticalAlignment = floatRegion.VerticalAlignment = VerticalAlignment.Center;
        hide.Click += (_, _) => HideRequested?.Invoke(Zone);
        var header = _header;
        DockPanel.SetDock(hide, Dock.Right); header.Children.Add(hide);
        DockPanel.SetDock(floatRegion, Dock.Right); header.Children.Add(floatRegion);
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
        _tabScroll.Offset = new Vector(Math.Clamp(_tabScroll.Offset.X - delta * 72, 0, maximum), 0);
        return true;
    }
    internal void SetDropTarget(bool active) => _dropTarget.IsVisible = active;

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
        foreach (var entry in _buttons.Values) entry.Tab.Height = entry.Tab.MinHeight = settings.RowHeight - 2;
    }

    public void Render(WorkbenchLayoutState state)
    {
        _state = state;
        _floatRegionItem.Header = FloatingId is null ? "浮动整栏" : "停靠回原栏";
        // 浮动窗口自带“停靠回原栏”工具栏，页签栏不再重复显示浮动键。
        if (_floatButton is not null) _floatButton.IsVisible = FloatingId is null;
        var ids = PageIds(state).Where(id => Catalog.Any(page => page.Id == id)).ToList();
        _empty.IsVisible = ids.Count == 0;
        var selected = ids.Contains(SelectedPage(state)!) ? SelectedPage(state) : ids.FirstOrDefault();
        foreach (var id in _buttons.Keys.Except(ids).ToArray())
        { _tabs.Children.Remove(_buttons[id].Row); _buttons.Remove(id); }
        for (var index = 0; index < ids.Count; index++)
        {
            var id = ids[index];
            var page = Catalog.First(p => p.Id == id);
            if (!_buttons.TryGetValue(id, out var entry))
            {
                // DESK-1：关闭按钮瘦身后列宽同步收紧（28→22），页签更紧凑、滑动手感更顺。
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,22"), MinWidth = 76, MaxWidth = 180 };
                row.Classes.Add("dockTabRow");
                var tab = new DockTabButton { Content = Label(page), Height = _rowHeight - 2, MinHeight = _rowHeight - 2,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(12, 4, 4, 4), FontSize = 12 };
                tab.Classes.Add("subtle"); tab.Classes.Add("dockTab");
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
                var close = WorkbenchUi.IconButton("Dismiss", "关闭页签：" + page.Title);
                close.Classes.Add("dockTabClose");
                // DESK-1：关闭按钮从 24 粗大笨重缩到 16 轻量圆形，图标 10，
                // 命中区靠皮肤样式保证不缩到不可点；悬停只在皮肤层加高亮与危险色。
                close.Width = close.MinWidth = close.Height = close.MinHeight = 16; close.Padding = new Thickness(3);
                if (close.Content is PathIcon closeIcon) closeIcon.Width = closeIcon.Height = 10;
                close.VerticalAlignment = VerticalAlignment.Center; close.Margin = new Thickness(0, 0, 3, 0);
                AutomationProperties.SetAutomationId(close, "dock-close-" + id);
                close.Click += (_, _) => CloseRequested?.Invoke(id);
                row.Children.Add(tab); Grid.SetColumn(close, 1); row.Children.Add(close);
                var indicator = new Border { VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
                indicator.Classes.Add("dockIndicator"); Grid.SetColumnSpan(indicator, 2); row.Children.Add(indicator);
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
            ToolTip.SetTip(entry.Tab, pinned ? Label(page) + " · 已固定" : Label(page));
            entry.Row.Children.OfType<Border>().Single().IsVisible = id == selected;
            if (id == selected && selected != _selectedId)
                Dispatcher.UIThread.Post(() => entry.Row.BringIntoView(), DispatcherPriority.Loaded);
            if (!_pages.Children.Contains(page.Content)) _pages.Children.Add(page.Content);
            page.Content.IsVisible = id == selected;
        }
        _selectedId = selected;
    }

    private sealed class DockTabButton : Button
    {
        protected override Type StyleKeyOverride => typeof(Button);
        private Point? _start;
        private Point _lastReported;
        private bool _dragging;
        private bool _suppressClick;
        public event Action<Point>? Dropped;
        public event Action<Point?>? DragPositionChanged;
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
            { _start = e.GetPosition(TopLevel.GetTopLevel(this)); _dragging = false; _suppressClick = false; }
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
            DragPositionChanged?.Invoke(point);
        }
        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            var point = e.GetPosition(TopLevel.GetTopLevel(this));
            var drop = _dragging;
            base.OnPointerReleased(e);
            _start = null; _dragging = false; _suppressClick = false; Cursor = null;
            DragPositionChanged?.Invoke(null);
            if (drop) Dropped?.Invoke(point);
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
    public static Button IconButton(string icon, string title) => IconButton(icon, title, 30);
    public static Button IconButton(string icon, string title, double size)
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
        ToolTip.SetTip(button, title); AutomationProperties.SetName(button, title);
        return button;
    }
}
