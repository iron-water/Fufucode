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
using Fufucode.App.Features.Plugins;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Fufucode.App;

public partial class MainWindow
{
    private WorkbenchLayoutState _dockLayout = new();
    private readonly WorkbenchPageRegistry _workbenchPages = new();
    private Features.Plugins.PluginWorkspacePageHost? _pluginPageHost;
    private Features.Plugins.IPluginWorkspacePage? _terminalPage;
    private BuiltInModuleViewModel _browserModule = null!;
    private BuiltInModuleViewModel _libraryModule = null!;
    private BuiltInModuleViewModel _localReviewModule = null!;
    private LibraryService? _libraryService;
    private BrowserPageState? _browserPageState;
    private DockZone? _browserDormantZone;
    private bool _modulePreferencesCanWrite = true;
    private readonly WorkspacePanelView WorkspacePanel = new();
    private readonly OverviewView _overview = new();
    private readonly Grid _parkedPages = new() { IsVisible = false };
    private IChatSession? _workspaceSession;
    private string? _layoutContext;
    private bool _applyingWorkspaceLayout;
    private bool _browserFullView;

    /// <summary>
    /// 浏览器全宽查看：右栏临时横跨整个工作区（左导航栏保留），不移动到别的宿主，
    /// 因此 WebView2 controller 不重建、DOM/历史保留。退出时走正常布局恢复。
    /// </summary>
    private void SetBrowserFullView(bool full)
    {
        if (_browserFullView == full) return;
        _browserFullView = full;
        ApplyWorkspaceLayout();
    }
    private bool _resizingDock;
    private bool _syncingDockState;
    private bool _leftDrawerOpen;
    private bool _rightDrawerOpen;
    private Control? _rightReturnFocus;
    private bool _drawerWasVisible;
    private Control? _drawerReturnFocus;
    internal bool IsRightDrawerForTesting { get; private set; }
    internal Features.Plugins.IPluginWorkspacePage? TerminalPageForTesting => _terminalPage;
    internal bool TerminalPluginAvailableForTesting => _terminalPage is not null;
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

    /// <summary>
    /// 浏览器引用/标注进入当前主聊天草稿：与资料库引用同一套“不覆盖已有草稿”语义。
    /// 标注截图先真实落盘，再由聊天附件读取；发送失败如实提示，不伪造成功。
    /// </summary>
    internal async Task<string> AcceptBrowserReferenceAsync(BrowserChatReference reference)
    {
        if (_vm is not { IsChatPage: true } target)
            return "网页引用已复制；请先打开目标主聊天会话，再生成引用。";
        var imageMarkdown = string.Empty;
        if (reference.ImagePath is { Length: > 0 } path)
        {
            try { target.Input.AddAttachment(await AttachedFile.LoadAsync(path)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                target.Timeline.AddNotification("标注截图附加失败：" + error.Message);
            }
            // 聊天时间线渲染 Markdown：用绝对路径引用，用户消息气泡里直接能看到标注截图。
            imageMarkdown = "\n\n![网页标注](" + path.Replace("\\", "/") + ")";
        }
        var applied = target.Input.ApplyExternalText(reference.Text + imageMarkdown);
        var message = applied
            ? "网页内容已插入当前主聊天草稿，尚未发送。"
            : "当前主聊天已有草稿；网页内容已保存为待恢复文本，未覆盖草稿。";
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
        // 浏览器页由 workspace.browser 插件贡献；未安装时这里不预留页面。
        _browserModule = new("builtin.browser", "浏览器", SetBrowserModuleEnabled);
        _libraryModule = new("builtin.library", "资料库", SetLibraryModuleEnabled);
        _localReviewModule = new("builtin.local-review", "本地审查", SetLocalReviewModuleEnabled);
        // 资料库、本地审查、浏览器、终端、自动开发都由插件贡献；宿主不保留页面壳。
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
        // 浏览器页由 workspace.browser 插件提供；模块开关只驱动插件启停，
        // 页面注册/释放统一走 PluginWorkspacePageHost，避免宿主再维护第二套页面生命周期。
        if (!enabled && _pluginPageHost?.TryGet("browser", out var oldPage) == true &&
            oldPage.View is BrowserView oldView)
        {
            _browserPageState = oldView.CaptureState();
            _browserDormantZone = _dockLayout.Find("browser");
        }
        try { _pluginPageHost?.Manager.SetPackageEnabled("workspace.browser", enabled); }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        { _vm?.Timeline.AddNotification("浏览器插件状态切换失败：" + error.Message); }
        if (enabled && _pluginPageHost?.TryGet("browser", out var newPage) == true &&
            newPage.View is BrowserView newView)
        {
            if (_browserPageState is { } state) newView.RestoreState(state);
            OpenDockPage("browser", _browserDormantZone ?? DockZone.Right);
        }
        RenderDockPages();
        _actions.RefreshCapabilities();
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
        if (_pluginPageHost?.TryGet("browser", out var browserPage) == true &&
            browserPage.View is BrowserView browserView)
            _browserPageState = browserView.CaptureState();
        _host.Manager.Metadata.SetPreference(ModulePreferenceKey(),
            new WorkbenchModulePreferences(BrowserEnabled: _pluginPageHost?.TryGet("browser", out _) == true,
                BrowserState: _browserPageState,
                LibraryEnabled: _workbenchPages.ContainsKey("library"),
                LocalReviewEnabled: _workbenchPages.ContainsKey("local-review"),
                AutomationEnabled: false, AutomationDatabase: null).Serialize());
    }

    private void ApplyLibraryModuleState(bool enabled)
    {
        try { _pluginPageHost?.Manager.SetPackageEnabled("workspace.library", enabled); }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        { _vm?.Timeline.AddNotification("资料库插件状态切换失败：" + error.Message); }
        RenderDockPages();
        _actions.RefreshCapabilities();
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

    /// <summary>输入区上下文条：当前会话所属项目（新会话且未发送第一条消息时可改）与固定后端。</summary>
    private void WireSessionContext(MainViewModel vm)
    {
        _chatPage.SessionContextRequested = () =>
        {
            if (_host is null) return null;
            var backend = BackendCapabilities.For(_host.Current.Session.Backend);
            var project = _host.Manager.Metadata.GetProject(_host.Current.Session.ProjectId);
            var organization = new SessionOrganization(_host.Manager.Metadata);
            var name = organization.Project(project).Name;
            // 只有还没有用户消息的新会话才能改项目；发送第一条后不再显示项目切换。
            var canChange = !HasUserMessage(_host.Current.Session);
            return new Views.ChatPage.SessionContextInfo(
                project.Id, string.IsNullOrWhiteSpace(name) ? Path.GetFileName(project.Path) : name,
                backend.Name, BackendBadgeColor(backend.Id), canChange);
        };
        _chatPage.ChangeProjectRequested = async () =>
        {
            if (_host is null) return;
            var projects = OverviewProjects()
                .Select(project => (project.Id, new SessionOrganization(_host.Manager.Metadata).Project(project).Name))
                .ToArray();
            await _chatPage.ShowProjectPickerAsync(projects, _host.Current.Session.ProjectId, async id =>
            {
                await MoveCurrentSessionToProjectAsync(id);
                _chatPage.RefreshSessionContext();
            });
        };
        _chatPage.RefreshSessionContext();
        // 发出第一条消息后项目即固定：上下文条收起项目切换入口。
        vm.Input.SubmissionStarted += _ => Dispatcher.UIThread.Post(() => _chatPage.RefreshSessionContext());
    }

    /// <summary>把当前会话移动到目标项目（或无项目独立工作区）。
    /// 只有还没有用户消息的新会话允许移动；移动后会话在目标项目里继续，凭据与历史不变。</summary>
    private async Task MoveCurrentSessionToProjectAsync(string? projectId)
    {
        if (_host is null) return;
        if (_vm?.Input.IsTurnActive == true)
            throw new InvalidOperationException("当前回合进行中，请先停止或等待完成再切换项目。");
        var metadata = _host.Manager.Metadata;
        var session = _host.Current.Session;
        var target = projectId is null
            ? metadata.GetOrCreateProject(ScratchWorkspace.Ensure(_options.CreateAppDataLayout()))
            : metadata.GetProject(projectId);
        if (target.Id == session.ProjectId) return;

        // 新会话的会话文件随会话移动到目标项目目录，旧目录不留孤儿文件。
        var sourceFile = session.FilePath;
        var projectDirectory = _host.Manager.Layout.ProjectSessionsDirectory(target.Id);
        var backendDirectory = Path.Combine(projectDirectory, BackendFolder(session.Backend));
        Directory.CreateDirectory(backendDirectory);
        string? moved = null;
        if (sourceFile is { Length: > 0 } && File.Exists(sourceFile))
        {
            moved = Path.Combine(backendDirectory, Path.GetFileName(sourceFile));
            File.Move(sourceFile, moved, overwrite: false);
        }

        try
        {
            metadata.MoveSession(session.Id, target.Id, moved);
        }
        catch
        {
            if (moved is not null && File.Exists(moved) && sourceFile is not null)
                File.Move(moved, sourceFile, overwrite: true);
            throw;
        }

        // 展示分组同步：移动到目标项目后不再挂在原来的分区。
        new SessionOrganization(metadata).PlaceSession(session.Id, target.Id, null);
        // 先把窗口切到目标项目，再按同一会话 id 重开，避免在旧项目目录里继续执行。
        var path = target.Path;
        if (!RealSessionHost.PathEquals(_host.ProjectPath, path)) await SwitchProjectAsync(path);
        // 切换项目会恢复该项目最近的会话；这里再切回刚才移动的会话。
        if (_host.Current.Session.Id != session.Id)
            await _host.SwitchToAsync(session.Id, _bootstrapCts.Token);
        RefreshSessionList();
    }

    private static string BackendFolder(Fufucode.Core.BackendId backend) => backend switch
    {
        Fufucode.Core.BackendId.Omp => "omp",
        Fufucode.Core.BackendId.Claude => "claude",
        Fufucode.Core.BackendId.Codex => "codex",
        Fufucode.Core.BackendId.Dsh => "dsh",
        Fufucode.Core.BackendId.Pi => string.Empty,
        _ => "fumo",
    };

    private static bool HasUserMessage(Fufucode.Core.SessionRecord session)
    {
        if (session.FilePath is not { Length: > 0 } path || !File.Exists(path)) return false;
        try
        {
            // FUMO/协议后端：JSON 里的 messages 数组；Pi：JSONL 每行一条。
            var text = File.ReadAllText(path);
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                return text.Contains("\"Role\": 1", StringComparison.Ordinal) ||
                       text.Contains("\"Role\":1", StringComparison.Ordinal);
            return text.Contains("\"role\":\"user\"", StringComparison.Ordinal);
        }
        catch (Exception) { return true; }
    }

    private static string BackendBadgeColor(Fufucode.Core.BackendId backend) => backend switch
    {
        Fufucode.Core.BackendId.Pi => "#6CCB5F",
        Fufucode.Core.BackendId.Omp => "#FF9E4C",
        Fufucode.Core.BackendId.Codex => "#3399FF",
        Fufucode.Core.BackendId.Dsh => "#2BD4C4",
        Fufucode.Core.BackendId.Claude => "#E8794A",
        _ => "#8B5CF6",
    };

    private void AttachWorkspaceContext(MainViewModel vm)
    {
        WireSessionContext(vm);
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
        var key = Path.GetFullPath(path);
        Fufucode.Core.Diagnostics.StartupProbe.Mark("workspace.project.begin", key);
        _pluginPageHost?.SetProject(key, _options.Mode == AppSessionMode.Mock);
        SyncTerminalPageReference();
        Fufucode.Core.Diagnostics.StartupProbe.Mark("workspace.project.pages", "plugin pages project switched");
        if (_layoutContext != key)
        {
            if (_layoutContext is not null)
            {
                try { SaveModulePreferences(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                { vm.Timeline.AddNotification("前一项目模块状态保存失败：" + error.Message); }
            }
            _layoutContext = key;
            try
            {
                var modules = WorkbenchModulePreferences.Restore(_host?.Manager.Metadata.GetPreference(ModulePreferenceKey()), out var diagnostic);
                _modulePreferencesCanWrite = diagnostic is null;
                _browserPageState = modules.BrowserState;
                Features.Plugins.IPluginWorkspacePage? browserPage = null;
                var browserActive = _pluginPageHost?.TryGet("browser", out browserPage) == true;
                _browserModule.ApplyState(browserActive, browserActive ? "已安装" : "未安装");
                if (browserActive && browserPage?.View is BrowserView browserView && modules.BrowserState is { } browserState)
                    browserView.RestoreState(browserState);
                var libraryActive = _pluginPageHost?.TryGet("library", out _) == true;
                _libraryModule.ApplyState(libraryActive, libraryActive ? "已安装" : "未安装");
                var reviewActive = _pluginPageHost?.TryGet("local-review", out _) == true;
                _localReviewModule.ApplyState(reviewActive, reviewActive ? "已安装" : "未安装");
                if (diagnostic is not null) vm.Timeline.AddNotification(diagnostic);
                var text = _host?.Manager.Metadata.GetPreference(LayoutPreferenceKey());
                _dockLayout = WorkbenchLayoutState.Restore(text,_workbenchPages.KnownPageIds);
            }
            catch (Exception error) when (error is IOException or Microsoft.Data.Sqlite.SqliteException)
            { _modulePreferencesCanWrite = false; vm.Timeline.AddNotification("布局或模块设置读取失败：" + error.Message); _dockLayout = new(); }
        }
        Fufucode.Core.Diagnostics.StartupProbe.Mark("workspace.modules", "module preferences restored");
        _syncingDockState = true;
        try { vm.SetRightPanelOpen(!_dockLayout.Hidden[DockZone.Right]); }
        finally { _syncingDockState = false; }
        RenderDockPages();
        Fufucode.Core.Diagnostics.StartupProbe.Mark("workspace.dock-render", "dock pages rendered");
        UpdateLocalReviewProject(path);
        // 所有可选页面安装状态统一由插件市场管理；设置不再维护第二套模块开关。
        vm.Settings.AttachBuiltInModules([]);
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
        // 设置 → 已归档：项目/会话归档管理。宿主更换或数据根变化时重建控件。
        var metadata = MetadataOrMock();
        if (_archivePage is null ||
            !string.Equals(_archiveStorePath, metadata.DatabasePath, StringComparison.OrdinalIgnoreCase))
        {
            _archivePage = new Features.Archive.ArchivePage(new Features.Archive.ArchiveViewModel(
                metadata,
                record => ActivateOverviewSessionAsync(record),
                path => SwitchProjectAsync(path),
                RefreshSessionList));
            _archiveStorePath = metadata.DatabasePath;
        }
        vm.Settings.ArchiveContent = _archivePage;
        _archivePage.Refresh();
        _ = vm.WorkspacePanel.RefreshAsync();
        Fufucode.Core.Diagnostics.StartupProbe.Mark("workspace.attach.end", "workspace context attached");
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

    private void OnPluginPageStateChanged(object? sender, EventArgs e)
    {
        _actions.RefreshCapabilities();
        if (_syncingDockState || _terminalPage is null) return;
        if (_terminalPage.IsVisible) OpenDockPage("terminal", DockZone.Bottom);
        else if (_dockLayout.Find("terminal") is not null) CloseDockPage("terminal");
    }

    internal void OpenDockPage(string id, DockZone target)
    {
        if (!_workbenchPages.ContainsKey(id)) return;
        _dockLayout.Open(id,target);
        if (Bounds.Width < 1100 && _dockLayout.Find(id) is { } drawer && drawer != DockZone.Bottom)
        { _leftDrawerOpen = drawer == DockZone.Left; _rightDrawerOpen = drawer == DockZone.Right; }
        RenderDockPages(); SaveWorkspaceDimensions();
        if (id == "terminal") _terminalPage?.Show();
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
        // 一个页面只能有一个视觉宿主：已在其它停靠区或浮窗中的页面不能再作为默认页塞进来，
        // 否则 Avalonia 会报“控件已有视觉父级”。
        var occupied = _dockLayout.Pages.Where(pair => pair.Key != zone)
            .SelectMany(pair => pair.Value)
            .Concat(_dockLayout.Floating.SelectMany(floating => floating.Pages))
            .ToHashSet(StringComparer.Ordinal);
        var preferred = new WorkbenchLayoutState().Pages[zone]
            .Where(id => _workbenchPages.ContainsKey(id)).ToList();
        var defaults = preferred.Where(id => !occupied.Contains(id)).ToList();
        if (defaults.Count > 0)
        {
            _dockLayout.Pages[zone] = defaults;
            _dockLayout.Selected[zone] = defaults[0];
            return true;
        }
        // 所有默认页都已被别的区域持有：把第一个默认页移过来，保持“一页一宿主”。
        if (preferred.Count == 0) return false;
        _dockLayout.Move(preferred[0], zone);
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
                    _terminalPage?.IsVisible == true) _terminalPage?.Show();
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

            if (_browserFullView && right && _dockLayout.Find("browser") == DockZone.Right)
            {
                // 浏览器全宽：盖住聊天与终端，只保留左侧导航栏；controller 仍挂在原右栏宿主里。
                Grid.SetColumn(RightPanelBorder, 0);
                Grid.SetColumnSpan(RightPanelBorder, 3);
                Grid.SetRowSpan(RightPanelBorder, 3);
                RightPanelBorder.ZIndex = 12;
                RightPanelBorder.IsVisible = true;
                RightPanelBorder.Width = double.NaN;
                RightPanelBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
                RightPanelBorder.Margin = default;
                RightPanelSplitter.IsVisible = false;
                RightDrawerScrim.IsVisible = false;
            }
        }
        finally { _applyingWorkspaceLayout = false; }
    }

    private Thickness DrawerBand() => _vm?.IsChatPage == true ? new(0, _chatPage.HeaderBandHeight, 0, _chatPage.BottomReserveHeight) : default;

    private void CaptureWorkspaceDimensions()
    {
        if (SidebarBorder.IsVisible && Bounds.Width >= 1100) _dockLayout.LeftWidth = ShellGrid.ColumnDefinitions[0].ActualWidth;
        if (RightPanelBorder.IsVisible && !IsRightDrawerForTesting && !_browserFullView) _dockLayout.RightWidth = WorkspaceShell.ColumnDefinitions[2].ActualWidth;
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
