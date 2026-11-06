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
using Avalonia.VisualTree;
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
    /// <summary>布局间隙保持 8px；透明命中区覆盖两侧，避免扩大永久空隙。</summary>
    internal const double DockSplitterSize = 8;
    internal const double DockSplitterHitSize = 24;

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

    /// <summary>下一次布局是否由用户主动开关触发；只有这种布局才做宽度动画。</summary>
    private bool _animateNextLayout;
    private DockRegionMotion _leftDockMotion = null!;
    private DockRegionMotion _rightDockMotion = null!;
    private DockRegionMotion _bottomDockMotion = null!;
    private bool _browserFullView;

    /// <summary>
    /// 浏览器全宽查看：右栏临时横跨整个工作区（左导航栏保留），不移动到别的宿主，
    /// 因此 WebView2 controller 不重建、DOM/历史保留。退出时走正常布局恢复。
    /// </summary>
    private void SetBrowserFullView(bool full)
    {
        if (_browserFullView == full) return;
        _browserFullView = full;
        ApplyWorkspaceLayout(snap: true);
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
        _leftDockMotion = new(SidebarBorder, LeftPanelSplitter, (extent, gap) =>
        {
            ShellGrid.ColumnDefinitions[0].Width = new GridLength(extent);
            ShellGrid.ColumnDefinitions[1].Width = new GridLength(gap);
        });
        _rightDockMotion = new(RightPanelBorder, RightPanelSplitter, (extent, gap) =>
        {
            WorkspaceShell.ColumnDefinitions[2].Width = new GridLength(extent);
            WorkspaceShell.ColumnDefinitions[1].Width = new GridLength(gap);
        });
        _bottomDockMotion = new(TerminalBorder, TerminalSplitter, (extent, gap) =>
        {
            WorkspaceShell.RowDefinitions[2].Height = new GridLength(extent);
            WorkspaceShell.RowDefinitions[1].Height = new GridLength(gap);
        });
        Closed += (_, _) =>
        {
            _leftDockMotion.Dispose();
            _rightDockMotion.Dispose();
            _bottomDockMotion.Dispose();
        };
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
        // 资料库、本地审查、浏览器、终端由插件贡献；自动开发使用内建 Graph。
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
            dock.DragPositionChanged += UpdateDockDropTargetFromScreen;
            dock.PageDragged += DropDockPageAtScreen;
        }
        LeftDock.Zone = DockZone.Left; RightDock.Zone = DockZone.Right; BottomDock.Zone = DockZone.Bottom;
        Opened += (_, _) => RenderDockPages();
        SizeChanged += (_, _) => ApplyWorkspaceLayout(snap: true);
        _chatPage.FindControl<Border>("ComposerCard")!.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 0.5 && !_resizingDock)
                Dispatcher.UIThread.Post(() => ApplyWorkspaceLayout());
        };
        foreach (var splitter in new[] { LeftPanelSplitter, RightPanelSplitter, TerminalSplitter })
        {
            splitter.DragStarted += (_, _) => BeginDockResize();
            splitter.DragCompleted += (_, _) => { CaptureWorkspaceDimensions(); _resizingDock = false; ApplyWorkspaceLayout(); };
            splitter.PointerCaptureLost += (_, _) =>
            {
                if (!_resizingDock) return;
                CaptureWorkspaceDimensions(); _resizingDock = false; ApplyWorkspaceLayout();
            };
            splitter.AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) BeginDockResize();
            }, RoutingStrategies.Tunnel);
            splitter.KeyUp += (_, _) => { CaptureWorkspaceDimensions(); _resizingDock = false; ApplyWorkspaceLayout(); };
        }
        AddHandler(KeyDownEvent, OnWorkspaceKeyDown, RoutingStrategies.Tunnel);
        RenderDockPages();

        void Add(string id, string title, string icon, Control control) => _workbenchPages.RegisterCore(id, title, icon, control);
    }

    /// <summary>
    /// 从会话里的变更卡跳到变更面板：切到"本次会话"口径、刷新，并尽量选中被点的文件。
    /// 路径可能是工具入参里的绝对路径，也可能是项目相对路径，这里统一归一化后再匹配。
    /// </summary>
    internal async Task ShowTurnChangesAsync(string? path)
    {
        OpenDockPage("changes", DockZone.Right);
        if (_vm is not { } vm) return;
        var panel = vm.WorkspacePanel;
        if (panel.SelectedScope != WorkspaceChangeScope.SessionWrites)
            panel.SelectedScope = WorkspaceChangeScope.SessionWrites;
        await panel.RefreshChangesAsync();
        if (string.IsNullOrWhiteSpace(path)) return;

        var slash = path.Replace('\\', '/');
        var relative = slash;
        var project = Path.GetFullPath(panel.ProjectPath).Replace('\\', '/').TrimEnd('/');
        if (slash.StartsWith(project + "/", StringComparison.OrdinalIgnoreCase))
            relative = slash[(project.Length + 1)..];
        panel.SelectedChange = panel.Changes.FirstOrDefault(file =>
            string.Equals(file.Path, relative, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(file.FullPath.Replace('\\', '/'), slash, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>收起状态的停靠区没有可见边界，拖拽时用工作区边缘这段宽度承接。</summary>
    private const double DockEdgeDropWidth = 36;

    private DockZone? DockTargetAt(Point point)
    {
        foreach (var (zone, border) in new[] { (DockZone.Left, SidebarBorder), (DockZone.Right, RightPanelBorder), (DockZone.Bottom, TerminalBorder) })
            if (border.IsEffectivelyVisible && border.TranslatePoint(default, this) is { } location && new Rect(location, border.Bounds.Size).Contains(point))
                return zone;
        // 所有区域都已展开时没有额外热点，避免把普通拖放误判成停靠。
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return null;
        if (DockClosed(DockZone.Left) && point.X is >= 0 and <= DockEdgeDropWidth) return DockZone.Left;
        if (DockClosed(DockZone.Right) && point.X <= Bounds.Width && point.X >= Bounds.Width - DockEdgeDropWidth) return DockZone.Right;
        if (DockClosed(DockZone.Bottom) && point.Y <= Bounds.Height && point.Y >= Bounds.Height - DockEdgeDropWidth) return DockZone.Bottom;
        return null;
    }

    /// <summary>窄窗左右栏是抽屉，抽屉收起等同停靠区关闭。</summary>
    private bool DockClosed(DockZone zone) => zone != DockZone.Bottom && Bounds.Width < 1100
        ? zone == DockZone.Left ? !_leftDrawerOpen : !_rightDrawerOpen
        : _dockLayout.Hidden[zone];

    /// <summary>拖到收起的停靠区边缘时直接展开，让用户能把页面放进去，而不必先手动开启侧栏。</summary>
    private void OpenDockZoneForDrop(DockZone zone)
    {
        if (!DockClosed(zone)) return;
        _animateNextLayout = true;
        if (zone != DockZone.Bottom && Bounds.Width < 1100)
        { _leftDrawerOpen = zone == DockZone.Left; _rightDrawerOpen = zone == DockZone.Right; }
        _dockLayout.Hidden[zone] = false;
        RenderDockPages();
    }

    internal void UpdateDockDropTarget(Point? point)
    {
        var target = point is { } position ? DockTargetAt(position) : null;
        // 拖动过程中只高亮目标，不重排布局：中途展开区域会重建控件并丢失指针捕获，导致这次拖放失效。
        foreach (var dock in Docks()) dock.SetDropTarget(target == dock.Zone);
    }

    /// <summary>屏幕坐标 → 主窗客户区坐标。</summary>
    private Point ClientFromScreen(PixelPoint screen) => this.PointToClient(screen);

    /// <summary>屏幕坐标直接与各停靠区的屏幕矩形比较，避免跨窗口换算误差把“拖到左栏”误判成空白区。</summary>
    private DockZone? DockTargetAtScreen(PixelPoint screen)
    {
        try { return DockTargetAtScreenCore(screen); }
        catch (ArgumentException) { return DockTargetAt(ClientFromScreen(screen)); }
    }

    private DockZone? DockTargetAtScreenCore(PixelPoint screen)
    {
        var point = new Point(screen.X, screen.Y);
        var scaling = RenderScaling > 0 ? RenderScaling : 1;
        foreach (var (zone, border) in new[] { (DockZone.Left, SidebarBorder), (DockZone.Right, RightPanelBorder), (DockZone.Bottom, TerminalBorder) })
        {
            if (!border.IsEffectivelyVisible) continue;
            var origin = border.PointToScreen(new Point(0, 0));
            var size = new Size(border.Bounds.Width * scaling, border.Bounds.Height * scaling);
            if (new Rect(origin.X, origin.Y, size.Width, size.Height).Contains(point)) return zone;
        }
        var windowOrigin = Avalonia.VisualExtensions.PointToScreen(this, new Point(0, 0));
        var windowWidth = Bounds.Width * scaling;
        var windowHeight = Bounds.Height * scaling;
        var edge = DockEdgeDropWidth * scaling;
        if (DockClosed(DockZone.Left) && screen.X >= windowOrigin.X && screen.X <= windowOrigin.X + edge) return DockZone.Left;
        if (DockClosed(DockZone.Right) && screen.X >= windowOrigin.X + windowWidth - edge && screen.X <= windowOrigin.X + windowWidth) return DockZone.Right;
        if (DockClosed(DockZone.Bottom) && screen.Y >= windowOrigin.Y + windowHeight - edge && screen.Y <= windowOrigin.Y + windowHeight) return DockZone.Bottom;
        return null;
    }

    /// <summary>页签拖动上报的是屏幕坐标，跨窗口拖放也落在同一套命中逻辑里。</summary>
    internal void UpdateDockDropTargetFromScreen(PixelPoint? screen)
    {
        var target = screen is { } point ? DockTargetAtScreen(point) : null;
        foreach (var dock in Docks()) dock.SetDropTarget(target == dock.Zone);
    }

    internal void DropDockPage(string id, Point point)
    {
        UpdateDockDropTarget(null);
        if (DockTargetAt(point) is { } zone) MoveDockPageAnimated(id, zone);
        // 拖到非停靠区：直接变成独立窗口，不再需要栏头按钮。
        else FloatPageFromDrag(id);
    }

    internal void DropDockPageAtScreen(string id, PixelPoint screen)
    {
        UpdateDockDropTarget(null);
        if (DockTargetAtScreen(screen) is { } zone) MoveDockPageAnimated(id, zone);
        else FloatPageFromDrag(id);
    }

    private void FloatPageFromDrag(string id)
    {
        if (!_workbenchPages.ContainsKey(id) || _dockLayout.FindFloating(id) is not null) return;
        _animateNextLayout = true;
        FloatDockPage(id);
    }

    /// <summary>拖入停靠区：区域展开 + 页面淡入；来自单页浮动窗时先让窗口淡出再关闭。</summary>
    private void MoveDockPageAnimated(string id, DockZone zone)
    {
        void Commit()
        {
            _animateNextLayout = true;
            MoveDockPage(id, zone);
            Docks().FirstOrDefault(dock => dock.Zone == zone)?.AnimatePageArrival();
        }
        if (_dockLayout.FindFloating(id) is { Pages.Count: 1 } floating &&
            _floatingDocks.TryGetValue(floating.Id, out var host) && host.Window.IsVisible && !Motion.Reduced)
        {
            var window = host.Window;
            var disposed = false;
            _ = Motion.AnimateValue(1, 0, Motion.Fast, value => window.Opacity = value, () =>
            {
                if (disposed) return;
                disposed = true;
                window.Opacity = 1;
                Commit();
            });
            return;
        }
        Commit();
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
            var project = _host.Manager.Metadata.GetProject(_host.Current.Session.ProjectId);
            var organization = new SessionOrganization(_host.Manager.Metadata);
            var name = organization.Project(project).Name;
            // 只有还没有用户消息的新会话才能改项目；发送第一条后不再显示项目切换。
            var canChange = !HasUserMessage(_host.Current.Session);
            return new Views.ChatPage.SessionContextInfo(
                project.Id, string.IsNullOrWhiteSpace(name) ? Path.GetFileName(project.Path) : name,
                canChange);
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
        NotifySessionChanged();
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

    private bool HasUserMessage(Fufucode.Core.SessionRecord session) =>
        SessionPresence.HasUserMessage(MetadataOrMock(), session);

    private void AttachWorkspaceContext(MainViewModel vm)
    {
        WireSessionContext(vm);
        var path = _host?.ProjectPath ?? _options.ProjectPath ?? Environment.CurrentDirectory;
        vm.ProjectRootPath = Path.GetFullPath(path);
        try { EnsureGraphProject(path); }
        catch (IOException error) when (!vm.IsGraphPage)
        {
            // Another Graph owner cannot prevent an ordinary Chat window from opening.
            _graphWorkspace = null;
            _graphMainHost.Child = null;
            foreach (var slot in _graphPanelSlots.Values) slot.Child = null;
            vm.Timeline.AddNotification("项目地图暂不可用：" + error.Message);
        }
        vm.Timeline.ResolveReferencedFiles(vm.ProjectRootPath);
        if (_host is null && _options.Mode == AppSessionMode.Mock) vm.WorkspacePanel.AttachMock(path);
        else if (_host is null)
            vm.WorkspacePanel.AttachContext(path, null,
                ct => ChangeRangeQuery.WorkspaceAsync(path, ct),
                ct => ChangeRangeQuery.SessionWritesAsync(path, [], [], ct));
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
            // 设置里改了默认后端：会话管理“新建会话”条上的当前后端同步刷新。
            if (args.PropertyName == nameof(ViewModels.SettingsPageViewModel.BackendStatusText))
                RefreshNewSessionBackends();
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
                NotifySessionChanged));
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
        if (_syncingDockState || _renderingDockPages || _terminalPage is null) return;
        if (_terminalPage.IsVisible)
        {
            // 用户点“收起”后，插件在下架视图时仍可能回报 IsVisible=true；
            // 此时不能再把底栏自动打开，否则表现为“收起无效”。
            if (_dockLayout.Hidden[DockZone.Bottom] && _dockLayout.Find("terminal") == DockZone.Bottom) return;
            OpenDockPage("terminal", DockZone.Bottom);
        }
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
        // 设置页占满工作区，三区被强制隐藏；在这里只翻 Hidden 不会产生任何可见变化，
        // 用户看到的就是“点了没反应”。先把工作区切回来，再执行真正的开关。
        if (_vm?.IsSettingsPage == true)
        {
            _ = ToggleDockFromSettingsAsync(zone);
            return;
        }
        _animateNextLayout = true;
        if (zone != DockZone.Bottom && Bounds.Width < 1100)
        {
            var opening = zone == DockZone.Left ? !_leftDrawerOpen : !_rightDrawerOpen;
            _leftDrawerOpen = opening && zone == DockZone.Left;
            _rightDrawerOpen = opening && zone == DockZone.Right;
            _dockLayout.Hidden[zone] = !opening;
            RefreshDockVisibility(); SaveWorkspaceDimensions();
            return;
        }
        // Hidden 表达开关意图；关闭动画结束前面板仍可见，不能把它当作仍需关闭。
        // 空右栏直接显示添加页面入口，不恢复用户已经关闭的页签。
        // 插件直接挂载的可见空面板则仍可正常收起。
        var border = zone switch
        {
            DockZone.Left => SidebarBorder,
            DockZone.Right => RightPanelBorder,
            _ => TerminalBorder,
        };
        var isOpen = !_dockLayout.Hidden[zone] &&
            (_dockLayout.Pages[zone].Count > 0 || border.IsVisible);
        if (isOpen) _dockLayout.Hidden[zone] = true;
        else _dockLayout.Hidden[zone] = false;
        RefreshDockVisibility(); SaveWorkspaceDimensions();
    }

    /// <summary>在设置页点停靠区开关：先回到对话工作区，并直接打开该区域的页签。
    /// 设置页会把三区强制隐藏，此时按可见性翻转只会关掉逻辑上的开态，用户看到的仍是“没反应”。</summary>
    private async Task ToggleDockFromSettingsAsync(DockZone zone)
    {
        if (_vm?.NavigateCommand is { } navigate) await navigate.ExecuteAsync(AppPage.Chat);
        if (_vm?.IsSettingsPage == true) return;
        _animateNextLayout = true;
        _dockLayout.Hidden[zone] = false;
        if (zone != DockZone.Bottom && Bounds.Width < 1100)
        {
            _leftDrawerOpen = zone == DockZone.Left;
            _rightDrawerOpen = zone == DockZone.Right;
        }
        RenderDockPages();
        SaveWorkspaceDimensions();
    }

    /// <summary>纯开关不重建页签或搬移页面；三区共用同一显隐刷新路径。</summary>
    private int _dockContentFreezeGeneration;

    private void FreezeCentralLayoutDuringDockMotion()
    {
        if (Views.Motion.Reduced || MemoryProfile.DisableVisualEffects || PageHost.Bounds.Width <= 0) return;
        var generation = ++_dockContentFreezeGeneration;
        PageHost.Width = PageHost.Bounds.Width;
        PageHost.Height = PageHost.Bounds.Height;
        PageHost.HorizontalAlignment = HorizontalAlignment.Left;
        PageHost.VerticalAlignment = VerticalAlignment.Top;
        DispatcherTimer.RunOnce(Release, Views.Motion.StandardDuration + TimeSpan.FromMilliseconds(32));
        void Release()
        {
            if (generation != _dockContentFreezeGeneration) return;
            if (_leftDockMotion.IsAnimating || _rightDockMotion.IsAnimating || _bottomDockMotion.IsAnimating)
            {
                DispatcherTimer.RunOnce(Release, TimeSpan.FromMilliseconds(16));
                return;
            }
            PageHost.ClearValue(WidthProperty);
            PageHost.ClearValue(HeightProperty);
            PageHost.ClearValue(HorizontalAlignmentProperty);
            PageHost.ClearValue(VerticalAlignmentProperty);
        }
    }

    private void RefreshDockVisibility()
    {
        FreezeCentralLayoutDuringDockMotion();
        _syncingDockState = true;
        try { _vm?.SetRightPanelOpen(!_dockLayout.Hidden[DockZone.Right]); }
        finally { _syncingDockState = false; }
        SynchronizeAuxiliaryPageVisibility();
        RefreshNotificationChannel();
        ApplyWorkspaceLayout();
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

    private void BeginDockResize()
    {
        if (_resizingDock) return;
        ApplyWorkspaceLayout(snap: true);
        _resizingDock = true;
    }

    private void ApplyWorkspaceLayout(bool snap = false)
    {
        if (_applyingWorkspaceLayout || _resizingDock || _closed) return;
        // 重复刷新保留当前动画；窗口缩放、页面切换与拖动前才取消并落到终态。
        var shouldAnimate = _animateNextLayout;
        _animateNextLayout = false;
        _applyingWorkspaceLayout = true;
        try
        {
            // 动画只能从"当前实际宽度"出发，否则会先跳到目标再回跳。
            var currentLeftWidth = ShellGrid.ColumnDefinitions[0].ActualWidth;
            var currentRightWidth = WorkspaceShell.ColumnDefinitions[2].ActualWidth;
            var currentBottomHeight = WorkspaceShell.RowDefinitions[2].ActualHeight;
            var width = Bounds.Width > 0 ? Bounds.Width : Width;
            var settings = _vm?.IsSettingsPage == true;
            var narrow = width < 1100;

            // 保持标准 Grid 空间划分：各区域各占一格，互不遮挡，绝不重叠穿模。
            var left = !settings && (narrow ? _leftDrawerOpen : !_dockLayout.Hidden[DockZone.Left]);
            var leftWidth = Math.Clamp(_dockLayout.LeftWidth, 200, Math.Max(200, width - 750));
            var targetLeftWidth = left && !narrow ? leftWidth : 0;
            if (narrow) _leftDockMotion.SetDrawer(left, shouldAnimate, snap || settings);
            else _leftDockMotion.SetDocked(targetLeftWidth, currentLeftWidth,
                ShellGrid.ColumnDefinitions[1].ActualWidth, left, shouldAnimate, snap || settings);
            Grid.SetColumn(SidebarBorder, 0);
            Grid.SetColumnSpan(SidebarBorder, narrow ? 3 : 1);
            SidebarBorder.ZIndex = narrow ? 12 : 0;
            SidebarBorder.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            SidebarBorder.Width = narrow ? _dockLayout.LeftWidth : double.NaN;
            if (narrow && SidebarBorder.IsVisible) SidebarBorder.Margin = DrawerBand();
            else SidebarBorder.ClearValue(Layoutable.MarginProperty);

            Grid.SetColumn(LeftPanelSplitter, 1);
            Grid.SetColumnSpan(LeftPanelSplitter, 1);
            LeftPanelSplitter.Margin = new Thickness(-8, 0);
            LeftPanelSplitter.Width = DockSplitterHitSize;
            LeftPanelSplitter.HorizontalAlignment = HorizontalAlignment.Center;
            LeftPanelSplitter.ZIndex = 5;

            Grid.SetColumn(WorkspaceShell, 2);
            Grid.SetColumnSpan(WorkspaceShell, 1);

            var available = width - (left && !narrow ? leftWidth + DockSplitterSize : 0);
            var right = !settings && (narrow ? _rightDrawerOpen : !_dockLayout.Hidden[DockZone.Right]);
            IsRightDrawerForTesting = narrow;
            var rightWidth = Math.Clamp(_dockLayout.RightWidth, 32, Math.Max(32, available - 32));

            WorkspaceShell.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            var targetRightWidth = right && !IsRightDrawerForTesting ? rightWidth : 0;
            if (narrow) _rightDockMotion.SetDrawer(right, shouldAnimate, snap || settings);
            else _rightDockMotion.SetDocked(targetRightWidth, currentRightWidth,
                WorkspaceShell.ColumnDefinitions[1].ActualWidth, right, shouldAnimate, snap || settings);
            RightDrawerScrim.IsVisible = right && IsRightDrawerForTesting;

            Grid.SetColumn(RightPanelBorder, IsRightDrawerForTesting ? 0 : 2);
            Grid.SetColumnSpan(RightPanelBorder, IsRightDrawerForTesting ? 3 : 1);
            Grid.SetRow(RightPanelBorder, 0);
            Grid.SetRowSpan(RightPanelBorder, IsRightDrawerForTesting ? 1 : 3);
            RightPanelBorder.ZIndex = IsRightDrawerForTesting ? 12 : 0;
            RightPanelBorder.HorizontalAlignment = IsRightDrawerForTesting ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            RightPanelBorder.Width = IsRightDrawerForTesting ? rightWidth : double.NaN;
            if (IsRightDrawerForTesting && RightPanelBorder.IsVisible) RightPanelBorder.Margin = DrawerBand();
            else RightPanelBorder.ClearValue(Layoutable.MarginProperty);

            RightDrawerScrim.Margin = RightPanelBorder.Margin;
            Grid.SetColumn(RightPanelSplitter, 1);
            Grid.SetColumnSpan(RightPanelSplitter, 1);
            RightPanelSplitter.Margin = new Thickness(-8, 0);
            RightPanelSplitter.Width = DockSplitterHitSize;
            RightPanelSplitter.HorizontalAlignment = HorizontalAlignment.Center;
            RightPanelSplitter.ZIndex = 5;

            var bottom = !settings && !_dockLayout.Hidden[DockZone.Bottom];
            var height = WorkspaceShell.Bounds.Height > 0 ? WorkspaceShell.Bounds.Height : Height - 74;
            var bottomHeight = Math.Clamp(_dockLayout.BottomHeight, 160, Math.Max(160, height - 360));
            WorkspaceShell.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            var targetBottomHeight = bottom ? bottomHeight : 0;
            _bottomDockMotion.SetDocked(targetBottomHeight, currentBottomHeight,
                WorkspaceShell.RowDefinitions[1].ActualHeight, bottom, shouldAnimate, snap || settings);
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
            TerminalSplitter.Margin = new Thickness(0, -8);
            TerminalSplitter.Height = DockSplitterHitSize;
            TerminalSplitter.VerticalAlignment = VerticalAlignment.Center;
            TerminalSplitter.ZIndex = 5;

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
        if (!_leftDockMotion.IsAnimating && !_dockLayout.Hidden[DockZone.Left] && SidebarBorder.IsVisible && Bounds.Width >= 1100)
            _dockLayout.LeftWidth = ShellGrid.ColumnDefinitions[0].ActualWidth;
        if (!_rightDockMotion.IsAnimating && !_dockLayout.Hidden[DockZone.Right] && RightPanelBorder.IsVisible && !IsRightDrawerForTesting && !_browserFullView)
            _dockLayout.RightWidth = WorkspaceShell.ColumnDefinitions[2].ActualWidth;
        if (!_bottomDockMotion.IsAnimating && !_dockLayout.Hidden[DockZone.Bottom] && TerminalBorder.IsVisible)
            _dockLayout.BottomHeight = WorkspaceShell.RowDefinitions[2].ActualHeight;
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
        if (Views.TableLightboxOverlay.IsOpen(this)) return;
        if (e.Key == Key.Escape && Docks().Select(dock => dock.CancelDrag()).ToArray().Any(canceled => canceled))
        { UpdateDockDropTarget(null); e.Handled = true; return; }
        if (HandleGlobalShortcut(e)) { e.Handled = true; return; }
        if (e.Key == Key.Escape && _leftDrawerOpen) { ToggleDock(DockZone.Left); e.Handled = true; }
        else if (e.Key == Key.Escape && IsRightDrawerForTesting && _rightDrawerOpen) { ToggleDock(DockZone.Right); e.Handled = true; }
    }

    private void ToggleSidebar_Click(object? sender, RoutedEventArgs e) => ToggleDock(DockZone.Left);
    private void ToggleTerminal_Click(object? sender, RoutedEventArgs e) => ToggleDock(DockZone.Bottom);
    private void ToggleRightDock_Click(object? sender, RoutedEventArgs e) => ToggleDock(DockZone.Right);
    private void CloseRightDrawer_Click(object? sender, RoutedEventArgs e) => ToggleDock(DockZone.Right);
    private void ToggleTheme_Click(object? sender, RoutedEventArgs e)
    {
        // 明暗主题和设置页“主题”必须是同一份外观主题：统一写 ui.json，再由 ApplyCustomization
        // 落到窗口与应用级。此前这里写 AppSettings.Theme，会被 ApplyCustomization 用外观主题
        // 立即覆盖（窗口被钉在旧变体、应用级被重置成跟随系统），表现为“切了又弹回去”。
        if (_customization is null) return;
        var next = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? "light" : "dark";
        _customization.Change(settings => settings with { Theme = next });
    }
    internal void ResetDockLayout() { _leftDrawerOpen = _rightDrawerOpen = false; _dockLayout = new() { NotificationMigrationApplied = true }; RenderDockPages(); SaveWorkspaceDimensions(); }
    private void ResetLayout_Click(object? sender, RoutedEventArgs e) => ResetDockLayout();
    private void RestoreFocus(Control? control) => Dispatcher.UIThread.Post(() => { if (!_closed && control?.IsEffectivelyVisible == true) control.Focus(); });
}
