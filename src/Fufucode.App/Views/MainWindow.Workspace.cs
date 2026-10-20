using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;
using Fufucode.App.Infrastructure.Browser;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using Fufucode.Core;
using Fufucode.App.Features.Library;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Fufucode.App;

public partial class MainWindow
{
    private WorkbenchLayoutState _dockLayout = new();
    private readonly Dictionary<string, TerminalDockViewModel> _projectTerminals = new(StringComparer.OrdinalIgnoreCase);
    private readonly WorkbenchPageRegistry _workbenchPages = new();
    private BuiltInModuleViewModel _browserModule = null!;
    private BuiltInModuleViewModel _libraryModule = null!;
    private BuiltInModuleViewModel _localReviewModule = null!;
    private BuiltInModuleViewModel _automationModule = null!;
    private string? _automationDatabase;
    private LibraryService? _libraryService;
    private BrowserPageState? _browserPageState;
    private bool _modulePreferencesCanWrite = true;
    private readonly WorkspacePanelView WorkspacePanel = new();
    private readonly TerminalDockView TerminalDock = new();
    private readonly OverviewView _overview = new();
    private readonly Grid _parkedPages = new() { IsVisible = false };
    private TerminalDockViewModel? _terminalDock;
    private IChatSession? _workspaceSession;
    private string? _layoutContext;
    private bool _applyingWorkspaceLayout;
    private bool _resizingDock;
    private bool _syncingDockState;
    private bool _leftDrawerOpen;
    private bool _rightDrawerOpen;
    private Control? _rightReturnFocus;
    private bool _drawerWasVisible;
    private Control? _drawerReturnFocus;
    internal bool IsRightDrawerForTesting { get; private set; }
    internal TerminalDockViewModel TerminalForTesting => _terminalDock!;
    internal double RightPreferenceForTesting => _dockLayout.RightWidth;
    internal WorkbenchLayoutState DockLayoutForTesting => _dockLayout;
    internal Control PageForTesting(string id) => _workbenchPages[id].Content;
    internal BuiltInModuleViewModel BrowserModuleForTesting => _browserModule;
    internal BuiltInModuleViewModel LibraryModuleForTesting => _libraryModule;
    internal BuiltInModuleViewModel LocalReviewModuleForTesting => _localReviewModule;
    internal bool PageAvailableForTesting(string id) => _workbenchPages.ContainsKey(id);

    // Resolve synchronously at click time, never after an asynchronous session switch.
    internal string AcceptLibraryCitation(string text)
    {
        if (_vm is not { IsChatPage: true } target || string.IsNullOrWhiteSpace(text))
            return "引用已复制；请先打开目标主聊天会话，再生成引用。";
        var applied = target.Input.ApplyExternalText(text);
        var message = applied
            ? "资料引用已插入当前主聊天草稿，尚未发送。"
            : "当前主聊天已有草稿；资料引用已保存为待恢复文本，未覆盖草稿。";
        target.Timeline.AddNotification(message);
        _chatPage.Input.Focus();
        return message;
    }

    private void InitializeWorkspaceShell()
    {
        ShellGrid.Children.Add(_parkedPages);
        var (files, changes) = WorkspacePanel.TakeDockPages();
        Add("overview", "会话管理", "AppsList", _overview);
        _overview.DataContextChanged += (_, _) => RenderOverview();
        Add("files", "文件", "Folder", files);
        Add("changes", "变更", "Branch", changes);
        _workbenchPages.RegisterOptional("browser", "浏览器", "Globe", "builtin.browser",
            () => CreateBrowserPage(), control => ((BrowserView)control).Dispose());
        _browserModule = new("builtin.browser", "浏览器", SetBrowserModuleEnabled);
        _workbenchPages.RegisterOptional("library", "资料库", "Folder", "builtin.library", () =>
        {
            _libraryService ??= new LibraryService(Path.Combine(
                Program.SelfTestDataRoot ?? _options.CreateAppDataLayout().Root, "library"));
            var view = new LibraryPageView();
            var library = new LibraryViewModel(_libraryService);
            library.CitationCopyRequested += text => library.StatusText = AcceptLibraryCitation(text);
            view.Attach(library);
            return view;
        }, control => ((LibraryPageView)control).Detach());
        _libraryModule = new("builtin.library", "资料库", SetLibraryModuleEnabled);
        _workbenchPages.RegisterOptional("local-review", "本地审查", "ShieldCheckmark", "builtin.local-review",
            CreateLocalReviewPage, control => ((Features.LocalReview.LocalReviewPageView)control).Detach());
        _localReviewModule = new("builtin.local-review", "本地审查", SetLocalReviewModuleEnabled);
        _workbenchPages.RegisterOptional("automation", "自动开发", "Automation", "builtin.automation", () =>
        {
            var page = new Features.Automation.AutomationControlPageView();
            var model = new Features.Automation.AutomationControlViewModel(Program.SelfTestDataRoot ?? _options.CreateAppDataLayout().Root); model.SetProjectContext(_layoutContext);
            model.DatabaseConnected += (database, context) =>
            {
                if (context != _layoutContext) return;
                _automationDatabase = database;
                try { SaveModulePreferences(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { model.StatusText = "连接已完成，但保存入口失败：" + error.Message; }
            };
            page.Attach(model); return page;
        }, control => ((Features.Automation.AutomationControlPageView)control).Detach());
        _automationModule = new("builtin.automation", "自动开发", SetAutomationModuleEnabled);
        Add("terminal", "终端", "Terminal", TerminalDock);
        Add("sidechat", "侧边聊天", "Chat", CreateSideChatHost());
        WorkspacePanel.PreviewRequested += () => OpenDockPage("files", DockZone.Right);
        foreach (var dock in Docks())
        {
            dock.Catalog = _workbenchPages.Values.ToArray();
            dock.OpenRequested += MoveDockPage;
            dock.MoveRequested += MoveDockPage;
            dock.SelectRequested += id => OpenDockPage(id, dock.Zone);
            dock.CloseRequested += CloseDockPage;
            dock.FloatPageRequested += id => FloatDockPage(id);
            dock.FloatRegionRequested += () => FloatDockRegion(dock.Zone);
            dock.PinRequested += id =>
            {
                if (!_dockLayout.PinnedPages.Remove(id)) _dockLayout.PinnedPages.Add(id);
                RenderDockPages(); SaveWorkspaceDimensions();
            };
            dock.ReorderRequested += (id, index) => { _dockLayout.Reorder(id, index); RenderDockPages(); SaveWorkspaceDimensions(); };
            dock.HideRequested += ToggleDock;
            dock.DragPositionChanged += UpdateDockDropTarget;
            dock.PageDragged += DropDockPage;
        }
        LeftDock.Zone = DockZone.Left; RightDock.Zone = DockZone.Right; BottomDock.Zone = DockZone.Bottom;
        Opened += (_, _) => RenderDockPages();
        SizeChanged += (_, _) => ApplyWorkspaceLayout();
        _chatPage.FindControl<Border>("ComposerCard")!.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 0.5 && !_resizingDock)
                Dispatcher.UIThread.Post(ApplyWorkspaceLayout);
        };
        foreach (var splitter in new[] { LeftPanelSplitter, RightPanelSplitter, TerminalSplitter })
        {
            splitter.DragStarted += (_, _) => _resizingDock = true;
            splitter.DragCompleted += (_, _) => { CaptureWorkspaceDimensions(); _resizingDock = false; ApplyWorkspaceLayout(); };
            splitter.AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) _resizingDock = true;
            }, RoutingStrategies.Tunnel);
            splitter.KeyUp += (_, _) => { CaptureWorkspaceDimensions(); _resizingDock = false; ApplyWorkspaceLayout(); };
        }
        AddHandler(KeyDownEvent, OnWorkspaceKeyDown, RoutingStrategies.Tunnel);
        RenderDockPages();

        void Add(string id, string title, string icon, Control control) => _workbenchPages.RegisterCore(id, title, icon, control);
    }

    private string ModulePreferenceKey() => "workbench:modules:v1:" + _layoutContext;

    private void ApplyBrowserModuleState(bool enabled)
    {
        if (!enabled && _workbenchPages.ContainsKey("browser"))
            _browserPageState = ((BrowserView)_workbenchPages["browser"].Content).CaptureState();
        var activating = enabled && !_workbenchPages.ContainsKey("browser");
        var wasFocused = _workbenchPages.ContainsKey("browser") &&
            _workbenchPages["browser"].Content.IsEffectivelyVisible;
        _workbenchPages.SetEnabled("browser", enabled, content =>
        {
            foreach (var dock in AllDockHosts()) dock.DetachPage(content);
            _parkedPages.Children.Remove(content);
        });
        if (activating) ((BrowserView)_workbenchPages["browser"].Content).RestoreState(_browserPageState);
        RenderDockPages();
        _actions.RefreshCapabilities();
        if (!enabled && wasFocused && _vm?.IsChatPage == true)
            RestoreFocus(_chatPage.FindControl<TextBox>("InputBox"));
    }

    private string SetBrowserModuleEnabled(bool enabled)
    {
        ApplyBrowserModuleState(enabled);
        _modulePreferencesCanWrite = true;
        var status = enabled ? "已启用" : "已停用";
        if (_host is null) return status + " · 当前演示窗口";
        try
        {
            SaveModulePreferences();
            return status + " · 已保存到此项目";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _vm?.Timeline.AddNotification("模块状态保存失败：" + error.Message);
            return status + " · 保存失败，重开后可能恢复原状态";
        }
    }

    private void SaveModulePreferences()
    {
        if (_host is null || !_modulePreferencesCanWrite) return;
        if (_workbenchPages.ContainsKey("browser")) _browserPageState = ((BrowserView)_workbenchPages["browser"].Content).CaptureState();
        _host.Manager.Metadata.SetPreference(ModulePreferenceKey(),
            new WorkbenchModulePreferences(BrowserEnabled: _workbenchPages.ContainsKey("browser"), BrowserState: _browserPageState,
                LibraryEnabled: _workbenchPages.ContainsKey("library"),
                LocalReviewEnabled: _workbenchPages.ContainsKey("local-review"),
                AutomationEnabled: _workbenchPages.ContainsKey("automation"), AutomationDatabase: _automationDatabase).Serialize());
    }

    private void ApplyLibraryModuleState(bool enabled)
    {
        _workbenchPages.SetEnabled("library", enabled, content =>
        {
            foreach (var dock in AllDockHosts()) dock.DetachPage(content);
            _parkedPages.Children.Remove(content);
        });
        RenderDockPages();
        _actions.RefreshCapabilities();
    }

    private string SetAutomationModuleEnabled(bool enabled)
    {
        _workbenchPages.SetEnabled("automation", enabled, content =>
        { foreach (var dock in AllDockHosts()) dock.DetachPage(content); _parkedPages.Children.Remove(content); });
        RenderDockPages(); _actions.RefreshCapabilities(); _modulePreferencesCanWrite = true;
        if (enabled) RestoreAutomationConnection();
        try { SaveModulePreferences(); return enabled ? "已启用" : "已停用"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { return "状态保存失败：" + error.Message; }
    }
    private void RestoreAutomationConnection()
    {
        if (!_workbenchPages.ContainsKey("automation")) return;
        var model = (Features.Automation.AutomationControlViewModel)_workbenchPages["automation"].Content.DataContext!;
        model.SetProjectContext(_layoutContext);
        model.Disconnect();
        if (!string.IsNullOrWhiteSpace(_automationDatabase)) _ = model.ConnectAsync(_automationDatabase);
    }

    private string SetLibraryModuleEnabled(bool enabled)
    {
        ApplyLibraryModuleState(enabled);
        _modulePreferencesCanWrite = true;
        if (_host is null) return (enabled ? "已启用" : "已停用") + " · 当前演示窗口";
        try { SaveModulePreferences(); return (enabled ? "已启用" : "已停用") + " · 已保存到此项目"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _vm?.Timeline.AddNotification("模块状态保存失败：" + error.Message);
            return "状态已变更 · 保存失败，重开后可能恢复原状态";
        }
    }

    private DockRegionView[] Docks() => [LeftDock,RightDock,BottomDock];

    private DockZone? DockTargetAt(Point point)
    {
        foreach (var (zone, border) in new[] { (DockZone.Left, SidebarBorder), (DockZone.Right, RightPanelBorder), (DockZone.Bottom, TerminalBorder) })
            if (border.IsEffectivelyVisible && border.TranslatePoint(default, this) is { } location && new Rect(location, border.Bounds.Size).Contains(point))
                return zone;
        return null;
    }

    internal void UpdateDockDropTarget(Point? point)
    {
        var target = point is { } position ? DockTargetAt(position) : null;
        foreach (var dock in Docks()) dock.SetDropTarget(target == dock.Zone);
    }

    internal void DropDockPage(string id, Point point)
    {
        UpdateDockDropTarget(null);
        if (DockTargetAt(point) is { } zone) MoveDockPage(id, zone);
    }

    private void SetBrowserOcclusion(bool value)
    {
        foreach (var page in _workbenchPages.Values)
            foreach (var browser in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(page.Content).OfType<BrowserViewHost>())
                if (ReferenceEquals(TopLevel.GetTopLevel(browser), this)) browser.SetOccluded(value);
    }

    private void AttachWorkspaceContext(MainViewModel vm)
    {
        var path = _host?.ProjectPath ?? Environment.CurrentDirectory;
        if (_host is null) vm.WorkspacePanel.AttachMock(path);
        else
        {
            var coordinator = _host.Current.Coordinator;
            vm.WorkspacePanel.AttachContext(path, _host.Current.Session.Id,
                ct => ChangeRangeQuery.WorkspaceAsync(path,ct),
                ct => ChangeRangeQuery.SessionWritesAsync(path,coordinator.Snapshot.Messages,coordinator.Snapshot.Tools,ct));
        }
        WorkspacePanel.SetDockContext(vm.WorkspacePanel);
        _overview.DataContext = vm;
        if (_workspaceSession is not null)
        {
            _workspaceSession.Event -= OnWorkspaceSessionEvent;
        }
        _workspaceSession = vm.Session;
        _workspaceSession.Event += OnWorkspaceSessionEvent;
        if (_terminalDock is not null) _terminalDock.PropertyChanged -= OnTerminalPropertyChanged;
        var key = Path.GetFullPath(path);
        if (!_projectTerminals.TryGetValue(key, out _terminalDock))
        {
            _terminalDock = new TerminalDockViewModel(useMock: _options.Mode == AppSessionMode.Mock, workspacePath:key);
            _projectTerminals.Add(key,_terminalDock);
        }
        _terminalDock.PropertyChanged += OnTerminalPropertyChanged;
        TerminalDock.DataContext = _terminalDock;
        if (_layoutContext != key)
        {
            if (_layoutContext is not null)
            {
                try { SaveModulePreferences(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                { vm.Timeline.AddNotification("前一项目模块状态保存失败：" + error.Message); }
                ApplyBrowserModuleState(false);
            }
            _layoutContext = key;
            try
            {
                var modules = WorkbenchModulePreferences.Restore(_host?.Manager.Metadata.GetPreference(ModulePreferenceKey()), out var diagnostic);
                _modulePreferencesCanWrite = diagnostic is null;
                var wasEnabled = _workbenchPages.ContainsKey("browser");
                if (!modules.BrowserEnabled) ApplyBrowserModuleState(false);
                _browserPageState = modules.BrowserState;
                if (modules.BrowserEnabled)
                {
                    ApplyBrowserModuleState(true);
                    if (wasEnabled) ((BrowserView)_workbenchPages["browser"].Content).RestoreState(_browserPageState);
                }
                _browserModule.ApplyState(modules.BrowserEnabled, diagnostic ?? (modules.BrowserEnabled ? "已启用" : "已停用"));
                ApplyLibraryModuleState(modules.LibraryEnabled);
                _libraryModule.ApplyState(modules.LibraryEnabled, diagnostic ?? (modules.LibraryEnabled ? "已启用" : "已停用"));
                ApplyLocalReviewModuleState(modules.LocalReviewEnabled);
                _localReviewModule.ApplyState(modules.LocalReviewEnabled, diagnostic ?? (modules.LocalReviewEnabled ? "已启用" : "已停用"));
                _automationDatabase = modules.AutomationDatabase;
                _workbenchPages.SetEnabled("automation", modules.AutomationEnabled, content =>
                { foreach (var dock in AllDockHosts()) dock.DetachPage(content); _parkedPages.Children.Remove(content); });
                _automationModule.ApplyState(modules.AutomationEnabled, diagnostic ?? (modules.AutomationEnabled ? "已启用" : "已停用"));
                RestoreAutomationConnection();
                if (diagnostic is not null) vm.Timeline.AddNotification(diagnostic);
                var text = _host?.Manager.Metadata.GetPreference(LayoutPreferenceKey());
                _dockLayout = WorkbenchLayoutState.Restore(text,_workbenchPages.KnownPageIds);
            }
            catch (Exception error) when (error is IOException or Microsoft.Data.Sqlite.SqliteException)
            { _modulePreferencesCanWrite = false; vm.Timeline.AddNotification("布局或模块设置读取失败：" + error.Message); _dockLayout = new(); }
        }
        _syncingDockState = true;
        try { vm.SetRightPanelOpen(!_dockLayout.Hidden[DockZone.Right]); }
        finally { _syncingDockState = false; }
        RenderDockPages();
        UpdateLocalReviewProject(path);
        vm.Settings.AttachBuiltInModules([_browserModule, _libraryModule, _localReviewModule, _automationModule]);
        vm.Settings.ShortcutsContent = _shortcutsPage;
        // 快捷键页内部分组下钻 → 同步到设置页子页路由，让面包屑/标题/淡入一起走。
        _shortcutsPage.GroupOpened += group => vm.Settings.OpenShortcutsGroup(group);
        _shortcutsPage.GroupClosed += () => vm.Settings.CloseShortcutsGroup();
        // 面包屑返回（点分类名或 GoBack）→ 控件跟随回到一级目录。
        vm.Settings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ViewModels.SettingsPageViewModel.CurrentPageId) &&
                _shortcutsPage.ActiveGroup != vm.Settings.ShortcutsGroupId)
                _shortcutsPage.SyncGroup(vm.Settings.ShortcutsGroupId);
        };
        vm.Settings.StorageContent = _storagePage;
        _ = vm.WorkspacePanel.RefreshAsync();
    }

    private string LayoutPreferenceKey() => "workbench:layout:v1:" + _layoutContext;

    private void OnWorkspaceSessionEvent(object? sender, SessionEvent e)
    {
        if (e is not (SessionEvent.TurnSettled or SessionEvent.ActivityChanged or SessionEvent.ConnectionChanged or SessionEvent.SessionError)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || !ReferenceEquals(sender, _workspaceSession)) return;
            RenderOverview();
            NotifyLocalPluginHostChanged();
            if (e is SessionEvent.TurnSettled) _ = _vm?.WorkspacePanel.RefreshAsync();
        }, DispatcherPriority.Background);
    }

    private void OnTerminalPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerminalDockViewModel.SelectedTab) or nameof(TerminalDockViewModel.HasOutputSelection))
            _actions.RefreshCapabilities();
        if (e.PropertyName != nameof(TerminalDockViewModel.IsVisible) || _syncingDockState) return;
        if (_terminalDock?.IsVisible == true) OpenDockPage("terminal", DockZone.Bottom);
        else if (_dockLayout.Find("terminal") is { } zone)
        {
            CloseDockPage("terminal");
        }
    }

    internal void OpenDockPage(string id, DockZone target)
    {
        if (!_workbenchPages.ContainsKey(id)) return;
        _dockLayout.Open(id,target);
        if (Bounds.Width < 1100 && _dockLayout.Find(id) is { } drawer && drawer != DockZone.Bottom)
        { _leftDrawerOpen = drawer == DockZone.Left; _rightDrawerOpen = drawer == DockZone.Right; }
        RenderDockPages(); SaveWorkspaceDimensions();
        if (id == "terminal") _terminalDock?.Show();
    }

    internal void MoveDockPage(string id, DockZone target)
    {
        if (!_workbenchPages.ContainsKey(id)) return;
        _dockLayout.Move(id,target);
        if (Bounds.Width < 1100 && target != DockZone.Bottom)
        { _leftDrawerOpen = target == DockZone.Left; _rightDrawerOpen = target == DockZone.Right; }
        RenderDockPages(); SaveWorkspaceDimensions();
    }

    private void CloseDockPage(string id)
    {
        CloseDockPageCore(id);
    }
    internal void CloseDockPageForTesting(string id) => CloseDockPageCore(id);
    private void CloseDockPageCore(string id)
    {
        // Closing a workspace page only removes its view, never kills the project-owned terminal.
        var zone = _dockLayout.Find(id);
        _dockLayout.Close(id);
        if (zone is not null && _dockLayout.Pages.TryGetValue(zone.Value, out var pages) && pages.Count == 0) _dockLayout.Hidden[zone.Value] = true;
        RenderDockPages(); SaveWorkspaceDimensions();
    }

    internal void ToggleDockForTesting(DockZone zone) => ToggleDock(zone);
    private void ToggleDock(DockZone zone)
    {
        if (zone == DockZone.Left && Bounds.Width < 1100)
        {
            _leftDrawerOpen = !_leftDrawerOpen;
            _rightDrawerOpen = false;
            if (_leftDrawerOpen && EnsureDockHasPages(zone)) { RenderDockPages(); SaveWorkspaceDimensions(); }
            else ApplyWorkspaceLayout();
            return;
        }
        if (zone == DockZone.Right && Bounds.Width < 1100)
        {
            _rightDrawerOpen = !_rightDrawerOpen;
            _leftDrawerOpen = false;
            if (_rightDrawerOpen && EnsureDockHasPages(zone)) { RenderDockPages(); SaveWorkspaceDimensions(); }
            else ApplyWorkspaceLayout();
            return;
        }
        // 有效开态 = 未隐藏且有页签：空区不收起时 Pages 空+Hidden=false 也是"关"，
        // 按有效开态切换，一次点击即可从死态恢复（回填默认页后再显示）。
        var isOpen = !_dockLayout.Hidden[zone] && _dockLayout.Pages[zone].Count > 0;
        if (isOpen) _dockLayout.Hidden[zone] = true;
        else { EnsureDockHasPages(zone); _dockLayout.Hidden[zone] = false; }
        RenderDockPages(); SaveWorkspaceDimensions();
    }

    /// <summary>区域的页签被逐一关空后，开关只能翻转 Hidden、面板仍不可见（空区不收起规则），
    /// 形成无法恢复的死状态。打开空区时回填该区默认页（视图只是被移回暂存区，重挂即可）。
    /// 返回是否发生了回填。</summary>
    private bool EnsureDockHasPages(DockZone zone)
    {
        if (_dockLayout.Pages[zone].Count > 0) return false;
        var defaults = new WorkbenchLayoutState().Pages[zone].Where(id => _workbenchPages.ContainsKey(id)).ToList();
        if (defaults.Count == 0) return false;
        _dockLayout.Pages[zone] = defaults;
        _dockLayout.Selected[zone] = defaults[0];
        return true;
    }

    private void RenderDockPages()
    {
        if (_renderingDockPages) return;
        _renderingDockPages = true;
        try
        {
            SeedNotificationMigration();
            SynchronizeFloatingHosts();
            foreach (var dock in AllDockHosts()) dock.Catalog = _workbenchPages.Values.ToArray();
            foreach (var dock in AllDockHosts()) dock.ReleasePages(_dockLayout);
            foreach (var page in _workbenchPages.Values)
            {
                if (_dockLayout.Find(page.Id) is null && _dockLayout.FindFloating(page.Id) is null)
                {
                    if (!_parkedPages.Children.Contains(page.Content)) _parkedPages.Children.Add(page.Content);
                }
                else _parkedPages.Children.Remove(page.Content);
            }
            foreach (var dock in AllDockHosts()) dock.Render(_dockLayout);
            RefreshNotificationChannel();
            RenderFloatingWindows();
            SynchronizeAuxiliaryPageVisibility();
            _syncingDockState = true;
            try
            {
                _vm?.SetRightPanelOpen(!_dockLayout.Hidden[DockZone.Right]);
                if (_dockLayout.Find("terminal") is { } zone && !_dockLayout.Hidden[zone] &&
                    TerminalDock.IsVisible) _terminalDock?.Show();
            }
            finally { _syncingDockState = false; }
            ApplyWorkspaceLayout();
        }
        finally { _renderingDockPages = false; }
    }

    private void ApplyWorkspaceLayout()
    {
        if (_applyingWorkspaceLayout || _resizingDock) return;
        _applyingWorkspaceLayout = true;
        try
        {
            var width = Bounds.Width > 0 ? Bounds.Width : Width;
            var settings = _vm?.IsSettingsPage == true;
            var narrow = width < 1100;
            var isGlass = _windowSkins.TryGetValue(this, out var skinLease)
                && skinLease.Skin == Features.Skins.SkinId.LiquidGlass;

            // 保持标准 Grid 空间划分：各区域各占一格，互不遮挡，绝不重叠穿模。
            var left = !settings && (narrow ? _leftDrawerOpen : !_dockLayout.Hidden[DockZone.Left]);
            var leftWidth = Math.Clamp(_dockLayout.LeftWidth, 200, Math.Max(200, width - 750));
            ShellGrid.ColumnDefinitions[0].Width = new GridLength(left && !narrow ? leftWidth : 0);
            ShellGrid.ColumnDefinitions[1].Width = new GridLength(left && !narrow ? 5 : 0);
            SidebarBorder.IsVisible = left;
            LeftPanelSplitter.IsVisible = left && !narrow;
            Grid.SetColumn(SidebarBorder, 0);
            Grid.SetColumnSpan(SidebarBorder, narrow ? 3 : 1);
            SidebarBorder.ZIndex = narrow ? 12 : 0;
            SidebarBorder.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            SidebarBorder.Width = narrow ? _dockLayout.LeftWidth : double.NaN;
            if (narrow) SidebarBorder.Margin = DrawerBand();
            else SidebarBorder.ClearValue(Layoutable.MarginProperty);

            Grid.SetColumn(LeftPanelSplitter, 1);
            Grid.SetColumnSpan(LeftPanelSplitter, 1);
            LeftPanelSplitter.Margin = default;
            LeftPanelSplitter.Width = double.NaN;
            LeftPanelSplitter.ZIndex = 0;

            Grid.SetColumn(WorkspaceShell, 2);
            Grid.SetColumnSpan(WorkspaceShell, 1);

            var available = width - (left && !narrow ? leftWidth + 5 : 0);
            var rightHasPages = _dockLayout.Pages.TryGetValue(DockZone.Right, out var rPages) && rPages.Count > 0;
            var right = !settings && (narrow ? _rightDrawerOpen : (!_dockLayout.Hidden[DockZone.Right] && (rightHasPages || IsRightDrawerForTesting)));
            IsRightDrawerForTesting = narrow;
            var rightWidth = Math.Clamp(_dockLayout.RightWidth, 32, Math.Max(32, available - 32));

            WorkspaceShell.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            WorkspaceShell.ColumnDefinitions[1].Width = new GridLength(right && !IsRightDrawerForTesting ? 5 : 0);
            WorkspaceShell.ColumnDefinitions[2].Width = new GridLength(right && !IsRightDrawerForTesting ? rightWidth : 0);
            RightPanelBorder.IsVisible = right;
            RightPanelSplitter.IsVisible = right && !IsRightDrawerForTesting;
            RightDrawerScrim.IsVisible = right && IsRightDrawerForTesting;

            Grid.SetColumn(RightPanelBorder, IsRightDrawerForTesting ? 0 : 2);
            Grid.SetColumnSpan(RightPanelBorder, IsRightDrawerForTesting ? 3 : 1);
            Grid.SetRow(RightPanelBorder, 0);
            Grid.SetRowSpan(RightPanelBorder, IsRightDrawerForTesting ? 1 : 3);
            RightPanelBorder.ZIndex = IsRightDrawerForTesting ? 12 : 0;
            RightPanelBorder.HorizontalAlignment = IsRightDrawerForTesting ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            RightPanelBorder.Width = IsRightDrawerForTesting ? rightWidth : double.NaN;
            if (right && IsRightDrawerForTesting) RightPanelBorder.Margin = DrawerBand();
            else RightPanelBorder.ClearValue(Layoutable.MarginProperty);

            RightDrawerScrim.Margin = RightPanelBorder.Margin;
            Grid.SetColumn(RightPanelSplitter, 1);
            Grid.SetColumnSpan(RightPanelSplitter, 1);
            RightPanelSplitter.Margin = default;
            RightPanelSplitter.Width = double.NaN;
            RightPanelSplitter.ZIndex = 0;

            var bottom = !settings && !_dockLayout.Hidden[DockZone.Bottom];
            var height = WorkspaceShell.Bounds.Height > 0 ? WorkspaceShell.Bounds.Height : Height - 74;
            var bottomHeight = Math.Clamp(_dockLayout.BottomHeight, 160, Math.Max(160, height - 360));
            WorkspaceShell.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            WorkspaceShell.RowDefinitions[1].Height = new GridLength(bottom ? 5 : 0);
            WorkspaceShell.RowDefinitions[2].Height = new GridLength(bottom ? bottomHeight : 0);

            TerminalBorder.IsVisible = bottom;
            TerminalSplitter.IsVisible = bottom;
            TerminalBorder.Height = double.NaN;
            TerminalBorder.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetColumn(TerminalBorder, 0);
            Grid.SetRow(TerminalBorder, 2);
            Grid.SetRowSpan(TerminalBorder, 1);
            TerminalBorder.ZIndex = 0;
            TerminalBorder.ClearValue(Layoutable.MarginProperty);

            Grid.SetColumn(TerminalSplitter, 0);
            Grid.SetRow(TerminalSplitter, 1);
            Grid.SetRowSpan(TerminalSplitter, 1);
            TerminalSplitter.Margin = default;
            TerminalSplitter.Height = double.NaN;
            TerminalSplitter.ZIndex = 0;

            var drawerVisible = narrow && (left || right);
            if (drawerVisible && !_drawerWasVisible)
                _drawerReturnFocus = FocusManager?.GetFocusedElement() as Control;
            else if (!drawerVisible && _drawerWasVisible)
            {
                RestoreFocus(_drawerReturnFocus);
                _drawerReturnFocus = null;
            }
            _drawerWasVisible = drawerVisible;
        }
        finally { _applyingWorkspaceLayout = false; }
    }

    private Thickness DrawerBand() => _vm?.IsChatPage == true ? new(0, _chatPage.HeaderBandHeight, 0, _chatPage.BottomReserveHeight) : default;

    private void CaptureWorkspaceDimensions()
    {
        if (SidebarBorder.IsVisible && Bounds.Width >= 1100) _dockLayout.LeftWidth = ShellGrid.ColumnDefinitions[0].ActualWidth;
        if (RightPanelBorder.IsVisible && !IsRightDrawerForTesting) _dockLayout.RightWidth = WorkspaceShell.ColumnDefinitions[2].ActualWidth;
        if (TerminalBorder.IsVisible) _dockLayout.BottomHeight = WorkspaceShell.RowDefinitions[2].ActualHeight;
        SaveWorkspaceDimensions();
    }

    private void SaveWorkspaceDimensions()
    {
        _customization?.SynchronizeLayout(_dockLayout);
        if (_host is null) { LayoutStateText.Text = "演示布局 · 当前窗口"; return; }
        try { _host.Manager.Metadata.SetPreference(LayoutPreferenceKey(),_dockLayout.Serialize()); LayoutStateText.Text = "布局已保存"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { LayoutStateText.Text = "布局保存失败"; _vm?.Timeline.AddNotification("布局保存失败：" + error.Message); }
    }

    private void OnWorkspaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Docks().Select(dock => dock.CancelDrag()).ToArray().Any(canceled => canceled))
        { UpdateDockDropTarget(null); e.Handled = true; return; }
        if (HandleGlobalShortcut(e)) { e.Handled = true; return; }
        if (e.Key == Key.Escape && _leftDrawerOpen) { _leftDrawerOpen = false; ApplyWorkspaceLayout(); e.Handled = true; }
        else if (e.Key == Key.Escape && IsRightDrawerForTesting && RightPanelBorder.IsVisible) { ToggleDock(DockZone.Right); e.Handled = true; }
    }

    private void ToggleSidebar_Click(object? sender, RoutedEventArgs e) => ToggleDock(DockZone.Left);
    private void ToggleTerminal_Click(object? sender, RoutedEventArgs e) => ToggleDock(DockZone.Bottom);
    private void ToggleRightDock_Click(object? sender, RoutedEventArgs e) => ToggleDock(DockZone.Right);
    private void CloseRightDrawer_Click(object? sender, RoutedEventArgs e) => ToggleDock(DockZone.Right);
    private void ToggleTheme_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is { } vm) vm.Settings.ThemeIndex = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? 1 : 2;
    }
    internal void ResetDockLayout() { _leftDrawerOpen = _rightDrawerOpen = false; _dockLayout = new() { NotificationMigrationApplied = true }; RenderDockPages(); SaveWorkspaceDimensions(); }
    private void ResetLayout_Click(object? sender, RoutedEventArgs e) => ResetDockLayout();
    private void RestoreFocus(Control? control) => Dispatcher.UIThread.Post(() => { if (!_closed && control?.IsEffectivelyVisible == true) control.Focus(); });
}
