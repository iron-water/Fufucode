using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;

namespace Fufucode.App;

public partial class MainWindow
{
    private readonly AppActionRegistry _actions = new();
    private CommandPaletteViewModel _palette = null!;
    private readonly UsageDetailsViewModel _usage = new();
    private NotificationPreferencesViewModel _notificationPreferences = null!;
    private NotificationCenterViewModel _notifications = null!;
    private readonly Views.CommandPaletteView _paletteView = new();
    private readonly Views.UsageDetailsView _usageView = new();
    private readonly Views.NotificationCenterView _notificationsView = new();
    private Control? _overlayReturnFocus;
    private string? _overlayKind;
    internal AppActionRegistry ActionsForTesting => _actions;
    internal Task ExecuteWorkbenchActionAsync(string id) => _actions.ExecuteAsync(id);
    internal CommandPaletteViewModel PaletteForTesting => _palette;
    internal UsageDetailsViewModel UsageForTesting => _usage;
    internal NotificationCenterViewModel NotificationsForTesting => _notifications;
    internal bool NotificationCenterVisibleForTesting => _notificationsView.IsEffectivelyVisible;

    private void InitializeGlobalWorkspace()
    {
        var dataRoot = Program.SelfTestMode
            ? Program.SelfTestDataRoot ?? Path.Combine(Path.GetTempPath(), "fufucode-selftest-appdata")
            : _options.CreateAppDataLayout().Root;
        _notificationPreferences = new(new NotificationPreferencesStore(Path.Combine(dataRoot, "notification-preferences.json")));
        _notifications = new(_notificationPreferences);
        _palette = new(_actions);
        _paletteView.DataContext = _palette;
        _usageView.DataContext = _usage;
        _notificationsView.DataContext = _notifications;
        _palette.CloseRequested += () => CloseGlobalOverlay("commands");
        _usage.CloseRequested += () => CloseDockPage("usage");
        _notifications.CloseRequested += () => CloseDockPage("notifications");
        _workbenchPages.RegisterCore("notifications", "通知", "Alert", _notificationsView);
        _workbenchPages.RegisterCore("usage", "用量", "Info", _usageView);
        _notificationsView.Focusable = _usageView.Focusable = true;
        _notifications.SessionRequested += async id =>
        {
            if (_host is not null && _host.Current.Session.Id != id) await SwitchSessionAsync(id);
            if (_host is null || _host.Current.Session.Id == id) await NavigateWorkspaceAsync(AppPage.Chat);
        };
        _notifications.PropertyChanged += OnNotificationPropertyChanged;
        InitializeNotificationDelivery();
        RenderDockPages();
        _actions.ExecutionFailed += message => _vm?.Timeline.AddNotification("动作失败：" + message);

        string? SessionUnavailable() => _vm is null ? "会话尚未就绪" : null;
        string? WorkAreaUnavailable() => _vm is null ? "会话尚未就绪" : _vm.IsSettingsPage ? "返回工作区后可使用" : null;
        RegisterBrowserActions();
        Register("commands", "命令面板", "全局", "Ctrl+K / Ctrl+Shift+P", "搜索应用动作，与输入框斜杠命令独立", () => { OpenGlobalOverlay("commands"); _palette.Open(); return Task.CompletedTask; });
        Register("settings", "设置", "全局", "", "切换设置与工作区", () => NavigateWorkspaceAsync(_vm?.IsSettingsPage == true ? AppPage.Chat : AppPage.Settings), SessionUnavailable);
        Register("chat", "返回对话", "导航", "", "返回当前会话", () => NavigateWorkspaceAsync(AppPage.Chat), SessionUnavailable);
        Register("extensions", "扩展", "导航", "", "管理已安装的扩展", () => NavigateWorkspaceAsync(AppPage.Extensions), SessionUnavailable);
        Register("files", "文件", "工作区", "Ctrl+P", "浏览项目文件，完整路径可复制", () => OpenWorkspaceTabAsync(WorkspacePanelKind.Files), WorkAreaUnavailable);
        Register("changes", "变更", "工作区", "", "查看工作树或会话直接写入范围", () => OpenWorkspaceTabAsync(WorkspacePanelKind.Changes), WorkAreaUnavailable);
        Register("terminal", "交互终端", "工作区", "Ctrl+J", "独立于 Pi RPC 的项目终端", () => { OpenDockPage("terminal", DockZone.Bottom); return Task.CompletedTask; }, WorkAreaUnavailable);
        string? TerminalMissing() => _terminalDock?.SelectedTab is null ? "请先打开终端" : null;
        Register("terminal.copy", "复制终端选区", "终端", "", "复制固定输出快照中的选择，退出后仍可用", TerminalDock.CopySelectionAsync,
            () => _terminalDock?.SelectedTab?.OutputSelection.HasText == true ? null : "请先选择终端输出");
        Register("terminal.paste", "粘贴到终端草稿", "终端", "", "保留草稿并追加剪贴板；需显式运行，绝不自动发送", TerminalDock.PasteToDraftAsync, TerminalMissing);
        Register("terminal.find", "查找终端输出", "终端", "", "在当前终端有界输出中查找", () => { TerminalDock.FindOutput(); return Task.CompletedTask; }, TerminalMissing);
        Register("terminal.bottom", "终端返回底部", "终端", "", "恢复跟随当前终端输出", () => { TerminalDock.ReturnToBottom(); return Task.CompletedTask; }, TerminalMissing);
        Register("terminal.copy-or-interrupt", "终端复制或中断", "终端", "", "输出有选区时复制，否则中断；命令框文字选择由编辑器复制", TerminalDock.CopyOrInterruptAsync, TerminalMissing);
        Register("terminal.interrupt", "中断终端命令", "终端", "", "中断当前终端的shell命令", () => { _terminalDock?.SelectedTab?.Interrupt(); return Task.CompletedTask; },
            () => _terminalDock?.SelectedTab?.IsRunning == true ? null : "没有运行中的终端");
        Register("terminal.new", "新建终端", "终端", "", "在当前项目新增独立终端", () => { _terminalDock?.CreateTerminalCommand.Execute(null); return Task.CompletedTask; },
            () => _terminalDock is null ? "项目尚未就绪" : null);
        Register("usage", "用量详情", "会话", "", "当前会话、本轮、全部记录口径", () => { OpenDockPage("usage", DockZone.Right); _usage.Open(); _usageView.Focus(); return Task.CompletedTask; }, SessionUnavailable);
        Register("notifications", "通知中心", "全局", "", "完成、失败与待审批归属", () => { OpenNotificationCenterOverlay(); return Task.CompletedTask; });
        Register("browser", "浏览器", "工作区", "", "内置浏览器", () => { OpenDockPage("browser", DockZone.Right); return Task.CompletedTask; },
            () => !_workbenchPages.ContainsKey("browser") ? "浏览器模块已停用，可在设置中启用" : WorkAreaUnavailable());
        Register("library", "资料库", "工作区", "", "导入、检索与引用本地资料", () => { OpenDockPage("library", DockZone.Right); return Task.CompletedTask; },
            () => !_workbenchPages.ContainsKey("library") ? "资料库模块已停用，可在设置中启用" : WorkAreaUnavailable());
        CaptionCommands.Command = _actions.GetCommand("commands");
        CaptionUsage.Command = _actions.GetCommand("usage");
        CaptionNotifications.Command = _actions.GetCommand("notifications");
        Activated += (_, _) => _notifications.SetWindowFocused(true);
        Deactivated += (_, _) => Dispatcher.UIThread.Post(RefreshNotificationFocus);
        SizeChanged += (_, _) => ResizeGlobalOverlay();
        ResizeGlobalOverlay();
    }

    private void Register(string id, string title, string group, string shortcut, string description,
        Func<Task> action, Func<string?>? disabled = null) => _actions.Register(new(id, title, group, shortcut, description, action, disabled));

    private void AttachGlobalContext(MainViewModel vm)
    {
        vm.Settings.AttachNotificationPreferences(_notificationPreferences);
        if (_host is { } host)
        {
            _usage.AttachRealContext(host.Manager.Layout, host.ProjectPath, host.Current.Session.Id, host.Current.Coordinator);
            _notifications.Attach(host.Current.Session.Id, host.Current.Coordinator.Snapshot.Generation ?? Guid.Empty, vm.Session);
        }
        else
        {
            _usage.AttachMock();
            _notifications.Attach("mock-window", Guid.NewGuid(), vm.Session);
        }
        // Input.SubmissionStarting/SubmissionRejected 在 AttachSession 中随 VM 创建接一次，
        // 这里不再重复订阅（重复会让通知中心为同一次提交建多个运行）。
        // 页面切换处理器持有字段引用，换 VM 时在 AttachSession 中解除，避免旧 VM 继续驱动全局动作。
        _globalVmPropertyChanged = (_, args) => { if (args.PropertyName == nameof(MainViewModel.CurrentPage)) _actions.RefreshCapabilities(); };
        vm.PropertyChanged += _globalVmPropertyChanged;
        _actions.RefreshCapabilities();
        AttachCustomization(vm);
    }

    private async Task OpenWorkspaceTabAsync(WorkspacePanelKind kind)
    {
        if (_vm is not { } vm) return;
        var tab = vm.WorkspacePanel.Tabs.FirstOrDefault(item => item.Kind == kind);
        if (tab is not null) { tab.IsOpen = true; vm.WorkspacePanel.SelectedTab = tab; }
        OpenDockPage(kind switch { WorkspacePanelKind.Files => "files", WorkspacePanelKind.Changes => "changes", _ => "files" }, DockZone.Right);
        await vm.WorkspacePanel.RefreshAsync();
    }

    /// <summary>打开同一通知功能页；已有浮动位置和通知历史保留。</summary>
    internal void OpenNotificationCenterOverlay()
    {
        OpenDockPage("notifications", DockZone.Right);
        _notifications.Open();
        _notificationsView.Focus();
    }

    private void OpenGlobalOverlay(string kind)
    {
        if (_overlayKind is null) _overlayReturnFocus = FocusManager?.GetFocusedElement() as Control;
        _palette.IsOpen = false;
        _overlayKind = kind;
        GlobalOverlayHost.Content = kind switch { "commands" => _paletteView, "usage" => _usageView, _ => (object)_notificationsView };
        GlobalOverlay.IsVisible = true;
        SetBrowserOcclusion(true);
        ResizeGlobalOverlay();
        if (kind != "commands") Dispatcher.UIThread.Post(() => GlobalOverlayHost.Focus());
    }

    private void ResizeGlobalOverlay()
    {
        GlobalOverlayHost.Width = Math.Max(320, Math.Min(680, (Bounds.Width > 0 ? Bounds.Width : Width) - 48));
        GlobalOverlayHost.MaxHeight = Math.Max(160, (Bounds.Height > 0 ? Bounds.Height : Height) - 80);
    }

    private void CloseGlobalOverlay(string? expected = null)
    {
        if (expected is not null && expected != _overlayKind) return;
        _palette.IsOpen = false;
        _overlayKind = null; GlobalOverlay.IsVisible = false; GlobalOverlayHost.Content = null;
        SetBrowserOcclusion(false);
        RestoreFocus(_overlayReturnFocus);
        _overlayReturnFocus = null;
    }

    private void GlobalScrim_Click(object? sender, RoutedEventArgs e) => CloseGlobalOverlay();
    private void OnNotificationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        ToolTip.SetTip(CaptionNotifications, _notifications.SummaryText);
        NotificationPrompt.DataContext = _notifications.LatestPrompt;
        // 浮层只在用户选择该渠道时出现；关掉浮层渠道后浮窗仍按原策略提醒。
        if (_notificationDelivery is not null) RefreshNotificationChannel();
        else NotificationPrompt.IsVisible = _notifications.HasPrompt;
    }
    private void NotificationPromptOpen_Click(object? sender, RoutedEventArgs e) => _notifications.ActivateCommand.Execute(_notifications.LatestPrompt);
    private void NotificationPromptDismiss_Click(object? sender, RoutedEventArgs e) => _notifications.DismissPromptCommand.Execute(null);

    private bool HandleGlobalShortcut(KeyEventArgs e)
    {
        if (e.Handled || _shortcutsPage?.IsRecording == true || IsShortcutCompositionActive()) return false;
        if (e.Key == Key.Escape && _overlayKind is not null)
        {
            if (_overlayKind == "commands") return false; // Palette owns IME-aware Escape.
            CloseGlobalOverlay(); return true;
        }
        if (e.Key == Key.Escape && _vm?.IsChatPage == false)
        { _ = NavigateWorkspaceAsync(_vm.IsSettingsPage ? _settingsReturnPage : AppPage.Chat); return true; }
        var focused = FocusManager?.GetFocusedElement() as Control;
        return RouteConfiguredShortcut(e, focused);
    }
}
