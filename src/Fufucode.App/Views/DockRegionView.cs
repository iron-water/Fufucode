using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.Views;

internal sealed class DockRegionView : UserControl
{
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly Grid _pages = new();
    private readonly TextBlock _empty = new() { Text = "此区域没有打开的页面。点击右上角“+”打开功能页，或把其他区域的页签拖到这里。", Margin = new Thickness(14, 12), IsVisible = false };
    private readonly Border _dropTarget = new() { BorderThickness = new Thickness(2), IsVisible = false, IsHitTestVisible = false };
    private readonly Border _headerRule = new();
    private readonly Dictionary<string, (Grid Row, Button Tab)> _buttons = new();
    private WorkbenchLayoutState? _state;
    private readonly DockPanel _header = new() { Height = 40, LastChildFill = true };
    private readonly Grid _root = new() { RowDefinitions = new RowDefinitions("40,*") };
    private double _rowHeight = 40;
    public DockZone Zone { get; set; }
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

    public DockRegionView()
    {
        _empty.Classes.Add("dockEmpty");
        _dropTarget.Classes.Add("dockDropTarget");
        _header.Classes.Add("dockHeader");
        _headerRule.Classes.Add("dockHeaderRule");
        var add = WorkbenchUi.IconButton("Add", "打开功能页");
        var hide = WorkbenchUi.IconButton("ChevronDown", "隐藏此区域（页面与进程保留）");
        add.Classes.Add("toolButton"); hide.Classes.Add("toolButton");
        add.Margin = new Thickness(0, 0, 2, 0); hide.Margin = new Thickness(0, 0, 6, 0);
        add.VerticalAlignment = hide.VerticalAlignment = VerticalAlignment.Center;
        hide.Click += (_, _) => HideRequested?.Invoke(Zone);
        var header = _header;
        DockPanel.SetDock(hide, Dock.Right); header.Children.Add(hide);
        DockPanel.SetDock(add, Dock.Right); header.Children.Add(add);
        header.Children.Add(new ScrollViewer { Content = _tabs,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
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
                var item = new MenuItem { Header = page.Title };
                if (Application.Current?.TryGetResource("Icon." + page.Icon, ActualThemeVariant, out var icon) == true && icon is Geometry geometry)
                    item.Icon = new PathIcon { Data = geometry, Width = 16, Height = 16 };
                item.Click += (_, _) => OpenRequested?.Invoke(page.Id, Zone);
                menu.Items.Add(item);
            }
            menu.ShowAt(add);
        };
    }

    internal bool IsDropTarget => _dropTarget.IsVisible;
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
            if (!Catalog.Any(page => ReferenceEquals(page.Content, control) && state.Pages[Zone].Contains(page.Id)))
                _pages.Children.Remove(control);
    }

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
        var ids = state.Pages[Zone].Where(id => Catalog.Any(page => page.Id == id)).ToList();
        _empty.IsVisible = ids.Count == 0;
        var selected = ids.Contains(state.Selected[Zone]!) ? state.Selected[Zone] : ids.FirstOrDefault();
        foreach (var id in _buttons.Keys.Except(ids).ToArray())
        { _tabs.Children.Remove(_buttons[id].Row); _buttons.Remove(id); }
        for (var index = 0; index < ids.Count; index++)
        {
            var id = ids[index];
            var page = Catalog.First(p => p.Id == id);
            if (!_buttons.TryGetValue(id, out var entry))
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,26"), MinWidth = 82, MaxWidth = 180 };
                row.Classes.Add("dockTabRow");
                var tab = new DockTabButton { Content = page.Title, Height = _rowHeight - 2, MinHeight = _rowHeight - 2,
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
                    reorder.Click += (_, _) => ReorderRequested?.Invoke(id, _state!.Pages[Zone].IndexOf(id) + delta);
                    menu.Items.Add(reorder);
                }
                var closeItem = new MenuItem { Header = "关闭页签", Tag = "dock-close" };
                closeItem.Click += (_, _) => CloseRequested?.Invoke(id);
                menu.Items.Add(closeItem); tab.ContextMenu = menu;
                var close = WorkbenchUi.IconButton("Dismiss", "关闭页签：" + page.Title);
                close.Classes.Add("dockTabClose");
                close.Width = close.MinWidth = close.Height = close.MinHeight = 22; close.Padding = new Thickness(5);
                close.VerticalAlignment = VerticalAlignment.Center; close.Margin = new Thickness(0, 0, 4, 0);
                close.Click += (_, _) => CloseRequested?.Invoke(id);
                row.Children.Add(tab); Grid.SetColumn(close, 1); row.Children.Add(close);
                var indicator = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
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
            ToolTip.SetTip(entry.Tab, pinned ? page.Title + " · 已固定" : page.Title);
            entry.Row.Children.OfType<Border>().Single().IsVisible = id == selected;
            if (id == selected) Dispatcher.UIThread.Post(() => entry.Tab.BringIntoView(), DispatcherPriority.Loaded);
            if (!_pages.Children.Contains(page.Content)) _pages.Children.Add(page.Content);
            page.Content.IsVisible = id == selected;
        }
    }

    private sealed class DockTabButton : Button
    {
        private Point? _start;
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
            if (Math.Abs(point.X - start.X) + Math.Abs(point.Y - start.Y) > 8)
            { _dragging = true; _suppressClick = true; Cursor = new Cursor(StandardCursorType.SizeAll); }
            if (_dragging) DragPositionChanged?.Invoke(point);
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
        Application.Current?.TryGetResource("Icon." + icon, null, out geometry);
        if (geometry is null) Application.Current?.TryGetResource("Icon.AppsList", null, out geometry);
        var path = new PathIcon { Data = geometry as Geometry, Width = 16, Height = 16 };
        var button = new Button { Width = size, Height = size, MinWidth = size, MinHeight = size, Padding = new Thickness(6),
            Content = path };
        button.Classes.Add("iconButton");
        ToolTip.SetTip(button, title); AutomationProperties.SetName(button, title);
        return button;
    }
}
