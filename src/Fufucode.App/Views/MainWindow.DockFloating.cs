using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Fufucode.App.Infrastructure;
using Fufucode.App.Views;

namespace Fufucode.App;

public partial class MainWindow
{
    private sealed record FloatingDockHost(Window Window, DockRegionView Dock);
    private readonly Dictionary<string, FloatingDockHost> _floatingDocks = new(StringComparer.Ordinal);
    private bool _renderingDockPages;
    private DispatcherTimer? _floatingDimensionDebounce;

    private void DebounceSaveWorkspaceDimensions()
    {
        if (_floatingDimensionDebounce is null)
        {
            _floatingDimensionDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _floatingDimensionDebounce.Tick += (_, _) =>
            {
                _floatingDimensionDebounce.Stop();
                SaveWorkspaceDimensions();
            };
        }
        _floatingDimensionDebounce.Stop();
        _floatingDimensionDebounce.Start();
    }

    private IEnumerable<DockRegionView> AllDockHosts() => Docks().Concat(_floatingDocks.Values.Select(host => host.Dock));
    internal Window? FloatingDockWindowForTesting(string id) => _floatingDocks.GetValueOrDefault(id)?.Window;

    internal string? FloatDockPage(string id, double? preferredWidth = null, double? preferredHeight = null)
    {
        if (!_workbenchPages.ContainsKey(id)) return null;
        var floating = _dockLayout.FloatPage(id);
        if (floating is null) return null;
        // 性能页等信息密集型页面需要在首次显示前就有足够画布；已有更大尺寸时保留用户设置。
        if (preferredWidth is { } width) floating.Width = Math.Max(floating.Width, width);
        if (preferredHeight is { } height) floating.Height = Math.Max(floating.Height, height);
        RenderDockPages(); SaveWorkspaceDimensions();
        return floating.Id;
    }

    internal string? FloatDockRegion(DockZone zone)
    {
        var floating = _dockLayout.FloatRegion(zone);
        if (floating is null) return null;
        RenderDockPages(); SaveWorkspaceDimensions();
        return floating.Id;
    }

    internal void DockFloatingRegion(string id, DockZone? destination = null)
    {
        _dockLayout.DockFloating(id, destination);
        RenderDockPages(); SaveWorkspaceDimensions();
    }

    private void HideFloatingRegion(string id)
    {
        if (_dockLayout.Floating.FirstOrDefault(item => item.Id == id) is not { } floating) return;
        floating.Hidden = true;
        if (_floatingDocks.TryGetValue(id, out var host)) host.Window.Hide();
        SynchronizeAuxiliaryPageVisibility();
        SaveWorkspaceDimensions();
    }

    private void SynchronizeFloatingHosts()
    {
        foreach (var id in _floatingDocks.Keys.Where(id => !_dockLayout.Floating.Any(item => item.Id == id)).ToArray())
        {
            var host = _floatingDocks[id];
            foreach (var page in _workbenchPages.Values) host.Dock.DetachPage(page.Content);
            _floatingDocks.Remove(id);
            ReleaseWindowSkin(host.Window);
            host.Window.Content = null; host.Window.Close();
        }
        foreach (var floating in _dockLayout.Floating)
        {
            if (_floatingDocks.ContainsKey(floating.Id)) continue;
            var id = floating.Id;
            var dock = new DockRegionView { FloatingId = id, Zone = floating.HomeZone };
            var window = new Window
            {
                Title = "Fufucode · 辅助栏", Width = floating.Width, Height = floating.Height,
                // 单行标题栏需同时容纳标题、停靠工具与三枚窗口键；420 是它们不挤压的最小宽度。
                MinWidth = 420, MinHeight = 200, ShowInTaskbar = false,
                WindowDecorations = WindowDecorations.BorderOnly,
                WindowStartupLocation = WindowStartupLocation.Manual,
                RequestedThemeVariant = RequestedThemeVariant, Topmost = floating.Topmost
            };
            window.Bind(BackgroundProperty, new DynamicResourceExtension("AppWindowBackground"));
            // 单行标题栏：标题（拖动区）+ 两个紧凑图标键 + 窗口按钮。
            // 文字按钮与自绘复选框夹在 32px 标题栏里会互相挤压、裁切；
            // “停靠回原栏”保留一键直达，其余停靠目标与置顶收进 ⋯ 菜单。
            var title = new TextBlock { Text = window.Title, Margin = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
            title.Bind(TextBlock.TextProperty, window.GetObservable(TitleProperty));
            var titleDrag = new Border { Child = title, Background = Avalonia.Media.Brushes.Transparent };
            titleDrag.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;
                if (e.ClickCount == 2) window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                else window.BeginMoveDrag(e);
            };
            var dockBack = Views.WorkbenchUi.IconButton("ArrowEnterLeft", "停靠回原栏");
            dockBack.Classes.Add("toolButton");
            dockBack.Width = dockBack.Height = 28;
            dockBack.Click += (_, _) => DockFloatingRegion(id);
            var optionsMenu = new MenuFlyout();
            foreach (var zone in Enum.GetValues<DockZone>())
            {
                var target = zone;
                var item = new MenuItem { Header = zone switch { DockZone.Left => "停靠到左栏", DockZone.Right => "停靠到右栏", _ => "停靠到底栏" } };
                item.Click += (_, _) => DockFloatingRegion(id, target);
                optionsMenu.Items.Add(item);
            }
            optionsMenu.Items.Add(new Separator());
            var topmostItem = new MenuItem
            {
                Header = "置顶窗口", ToggleType = MenuItemToggleType.CheckBox, IsChecked = floating.Topmost
            };
            topmostItem.Click += (_, _) =>
            {
                if (FindState() is not { } state) return;
                state.Topmost = window.Topmost = topmostItem.IsChecked;
                SaveWorkspaceDimensions();
            };
            optionsMenu.Items.Add(topmostItem);
            var options = Views.WorkbenchUi.IconButton("More", "更多停靠选项与置顶");
            options.Classes.Add("toolButton");
            options.Width = options.Height = 28;
            options.Flyout = optionsMenu;
            var dockActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4,
                Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            dockActions.Children.Add(dockBack); dockActions.Children.Add(options);
            var captionActions = new StackPanel { Orientation = Orientation.Horizontal };
            var minimize = new CaptionAssetButton { Kind = "minimize" };
            var maximize = new CaptionAssetButton { Kind = "maximize" };
            var close = new CaptionAssetButton { Kind = "close" };
            minimize.Click += (_, _) => window.WindowState = WindowState.Minimized;
            maximize.Click += (_, _) => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            window.PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) maximize.Kind = window.WindowState == WindowState.Maximized ? "restore" : "maximize"; };
            close.Click += (_, _) => HideFloatingRegion(id);
            Avalonia.Automation.AutomationProperties.SetName(minimize, "最小化辅助栏");
            Avalonia.Automation.AutomationProperties.SetName(maximize, "最大化或还原辅助栏");
            Avalonia.Automation.AutomationProperties.SetName(close, "隐藏辅助栏");
            captionActions.Children.Add(minimize); captionActions.Children.Add(maximize); captionActions.Children.Add(close);
            var captionGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Height = 32 };
            captionGrid.Children.Add(titleDrag);
            Grid.SetColumn(dockActions, 1); captionGrid.Children.Add(dockActions);
            Grid.SetColumn(captionActions, 2); captionGrid.Children.Add(captionActions);
            var captionBar = new Border { Child = captionGrid, Height = 32 }; captionBar.Classes.Add("chromeBar");
            var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            root.Children.Add(captionBar);
            Grid.SetRow(dock, 1); root.Children.Add(dock); window.Content = root;
            dock.OpenRequested += (pageId, _) =>
            {
                if (!_workbenchPages.ContainsKey(pageId)) return;
                _dockLayout.OpenInFloating(pageId, id); RenderDockPages(); SaveWorkspaceDimensions();
            };
            dock.SelectRequested += pageId => OpenDockPage(pageId, dock.Zone);
            dock.MoveRequested += MoveDockPage;
            dock.CloseRequested += CloseDockPage;
            dock.HideRequested += _ => HideFloatingRegion(id);
            dock.FloatPageRequested += pageId => FloatDockPage(pageId);
            dock.FloatRegionRequested += () => DockFloatingRegion(id);
            dock.PinRequested += pageId =>
            {
                if (!_dockLayout.PinnedPages.Remove(pageId)) _dockLayout.PinnedPages.Add(pageId);
                RenderDockPages(); SaveWorkspaceDimensions();
            };
            dock.ReorderRequested += (pageId, index) => { _dockLayout.Reorder(pageId, index); RenderDockPages(); SaveWorkspaceDimensions(); };
            // Local controls keep their own input semantics. Configured actions use this window's focus.
            window.AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Handled || _shortcutsPage?.IsRecording == true) return;
                var focused = window.FocusManager?.GetFocusedElement() as Control;
                if (IsShortcutCompositionActive(focused)) return;
                if (RouteConfiguredShortcut(e, focused)) e.Handled = true;
            }, RoutingStrategies.Tunnel);
            window.Closing += (_, e) =>
            {
                // Avalonia checks owned windows before raising the owner's Closing event.
                // Hiding/cancelling that check would prevent the main shutdown handler from running.
                if (_closed || !_floatingDocks.ContainsKey(id) || e.CloseReason != WindowCloseReason.WindowClosing) return;
                e.Cancel = true; HideFloatingRegion(id);
            };
            window.SizeChanged += (_, _) => Capture();
            window.PositionChanged += (_, _) => Capture();
            window.Activated += (_, _) => _notifications?.SetWindowFocused(true);
            window.Deactivated += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshNotificationFocus);
            _floatingDocks.Add(id, new(window, dock));
            void Capture()
            {
                if (_renderingDockPages || FindState() is not { } state) return;
                state.Width = window.Width; state.Height = window.Height;
                state.X = window.Position.X; state.Y = window.Position.Y;
                DebounceSaveWorkspaceDimensions();
            }
            FloatingDockState? FindState() => _dockLayout.Floating.FirstOrDefault(item => item.Id == id);
        }
    }

    private void RenderFloatingWindows()
    {
        // Restoring the layout happens in the main-window constructor, before its native owner is shown.
        if (_closed || !IsVisible) return;
        foreach (var floating in _dockLayout.Floating)
        {
            var host = _floatingDocks[floating.Id];
            host.Window.RequestedThemeVariant = RequestedThemeVariant;
            host.Window.Topmost = floating.Topmost;
            var appearance = _customization?.Current ?? new();
            host.Window.FontSize = appearance.FontSize;
            foreach (var resource in Resources) host.Window.Resources[resource.Key] = resource.Value;
            ApplyWindowSkin(host.Window, appearance);
            host.Dock.ApplyAppearance(appearance);
            host.Window.Title = "Fufucode · " + string.Join(" / ", floating.Pages
                .Where(_workbenchPages.ContainsKey).Select(id => _workbenchPages[id].Title));
            if (floating.Hidden) { host.Window.Hide(); continue; }
            if (!host.Window.IsVisible)
            {
                host.Window.Position = VisibleFloatingPosition(floating, host.Window);
                host.Window.Show(this);
            }
        }
    }

    private PixelPoint VisibleFloatingPosition(FloatingDockState state, Window window)
    {
        var candidate = state.X is { } x && state.Y is { } y ? new PixelPoint(x, y) : new PixelPoint(Position.X + 48, Position.Y + 48);
        var screens = window.Screens.All;
        var screen = screens.FirstOrDefault(item => item.WorkingArea.Contains(candidate)) ?? window.Screens.ScreenFromWindow(this) ?? window.Screens.Primary;
        if (screen is null) return candidate;
        var area = screen.WorkingArea;
        var width = (int)Math.Ceiling(state.Width * screen.Scaling);
        var height = (int)Math.Ceiling(state.Height * screen.Scaling);
        return new PixelPoint(Math.Clamp(candidate.X, area.X, Math.Max(area.X, area.Right - width)),
            Math.Clamp(candidate.Y, area.Y, Math.Max(area.Y, area.Bottom - height)));
    }

    private void DisposeFloatingDocks()
    {
        if (_floatingDimensionDebounce?.IsEnabled == true)
        {
            _floatingDimensionDebounce.Stop();
            SaveWorkspaceDimensions();
        }
        foreach (var host in _floatingDocks.Values.ToArray())
        {
            foreach (var page in _workbenchPages.Values) host.Dock.DetachPage(page.Content);
            ReleaseWindowSkin(host.Window);
            host.Window.Content = null; host.Window.Close();
        }
        _floatingDocks.Clear();
    }
}
