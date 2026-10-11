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
    internal bool NotificationCenterVisibleForTesting => GlobalOverlay.IsVisible && _overlayKind == "notifications";

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
        _usage.CloseRequested += () => CloseGlobalOverlay("usage");
        _notifications.CloseRequested += () => CloseGlobalOverlay("notifications");
        _notifications.SessionRequested += async id =>
        {
            if (_host is not null && _host.Current.Session.Id != id) await SwitchSessionAsync(id);
            if (_host is null || _host.Current.Session.Id == id) await NavigateWorkspaceAsync(AppPage.Chat);
        };
        _notifications.PropertyChanged += OnNotificationPropertyChanged;
        InitializeFloatingPanel();
        _actions.ExecutionFailed += message => _vm?.Timeline.AddNotification("动作失败：" + message);

        string? SessionUnavailable() => _vm is null ? "会话尚未就绪" : null;
        string? WorkAreaUnavailable() => _vm is null ? "会话尚未就绪" : _vm.IsSettingsPage ? "返回工作区后可使用" : null;
        Register("commands", "命令面板", "全局", "Ctrl+K / Ctrl+Shift+P", "搜索应用动作，与输入框斜杠命令独立", () => { OpenGlobalOverlay("commands"); _palette.Open(); return Task.CompletedTask; });
        Register("settings", "设置", "全局", "", "切换设置与工作区", () => NavigateWorkspaceAsync(_vm?.IsSettingsPage == true ? AppPage.Chat : AppPage.Settings), SessionUnavailable);
        Register("chat", "返回对话", "导航", "", "返回当前会话", () => NavigateWorkspaceAsync(AppPage.Chat), SessionUnavailable);
        Register("extensions", "扩展", "导航", "", "管理已安装的 Pi 扩展", () => NavigateWorkspaceAsync(AppPage.Extensions), SessionUnavailable);
        Register("files", "文件", "工作区", "Ctrl+P", "浏览项目文件，完整路径可复制", () => OpenWorkspaceTabAsync(WorkspacePanelKind.Files), WorkAreaUnavailable);
        Register("changes", "变更", "工作区", "", "查看工作树或会话直接写入范围", () => OpenWorkspaceTabAsync(WorkspacePanelKind.Changes), WorkAreaUnavailable);
        Register("terminal", "交互终端", "工作区", "Ctrl+J", "独立于 Pi RPC 的项目终端", () => { OpenDockPage("terminal", DockZone.Bottom); return Task.CompletedTask; }, WorkAreaUnavailable);
        Register("usage", "用量详情", "会话", "", "当前会话、本轮、全部记录口径", () => { OpenGlobalOverlay("usage"); _usage.Open(); return Task.CompletedTask; }, SessionUnavailable);
        Register("notifications", "通知中心", "全局", "", "完成、失败与待审批归属", () => { OpenGlobalOverlay("notifications"); _notifications.Open(); return Task.CompletedTask; });
        Register("floating.toggle", "显示/隐藏浮窗", "全局", "", "浮窗承载会话事件提醒与辅助对话", () => { ToggleFloatingPanel(); return Task.CompletedTask; });
        Register("floating.pin", "固定/取消固定浮窗", "全局", "", "固定时只在应用内显示；取消固定可独立成窗", () => { SetFloatingPinned(!_floating.Pinned); return Task.CompletedTask; });
        Register("preview", "文件预览", "工作区", "", "预览所选文件内容", () => { OpenDockPage("preview", DockZone.Right); return Task.CompletedTask; }, WorkAreaUnavailable);
        Register("browser", "浏览器", "工作区", "", "内置浏览器", () => { OpenDockPage("browser", DockZone.Right); return Task.CompletedTask; },
            () => !_workbenchPages.ContainsKey("browser") ? "浏览器模块已停用，可在设置中启用" : WorkAreaUnavailable());
        Register("library", "资料库", "工作区", "", "导入、检索与引用本地资料", () => { OpenDockPage("library", DockZone.Right); return Task.CompletedTask; },
            () => !_workbenchPages.ContainsKey("library") ? "资料库模块已停用，可在设置中启用" : WorkAreaUnavailable());
        CaptionCommands.Command = _actions.GetCommand("commands");
        CaptionUsage.Command = _actions.GetCommand("usage");
        CaptionNotifications.Command = _actions.GetCommand("notifications");
        Activated += (_, _) => _notifications.SetWindowFocused(true);
        Deactivated += (_, _) => _notifications.SetWindowFocused(false);
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
        vm.Input.SubmissionStarting += _notifications.BeginSubmission;
        vm.Input.SubmissionRejected += _notifications.CancelSubmission;
        vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(MainViewModel.CurrentPage)) _actions.RefreshCapabilities(); };
        _actions.RefreshCapabilities();
        AttachCustomization(vm);
    }

    private async Task OpenWorkspaceTabAsync(WorkspacePanelKind kind)
    {
        if (_vm is not { } vm) return;
        var tab = vm.WorkspacePanel.Tabs.FirstOrDefault(item => item.Kind == kind);
        if (tab is not null) { tab.IsOpen = true; vm.WorkspacePanel.SelectedTab = tab; }
        OpenDockPage(kind switch { WorkspacePanelKind.Files => "files", WorkspacePanelKind.Changes => "changes", _ => "preview" }, DockZone.Right);
        await vm.WorkspacePanel.RefreshAsync();
    }

    /// <summary>浮窗内的"打开通知中心"入口：复用全局浮层，不另开一套。</summary>
    internal void OpenNotificationCenterOverlay()
    {
        OpenGlobalOverlay("notifications");
        _notifications.Open();
    }

    private void OpenGlobalOverlay(string kind)
    {
        if (_overlayKind is null) _overlayReturnFocus = FocusManager?.GetFocusedElement() as Control;
        _palette.IsOpen = false; _usage.IsOpen = false; _notifications.IsOpen = false;
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
        _palette.IsOpen = false; _usage.IsOpen = false; _notifications.IsOpen = false;
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
        if (_floating is not null) RefreshNotificationChannel();
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
