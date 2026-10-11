using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;

namespace Fufucode.App;

/// <summary>
/// 浮窗宿主：同一份浮窗内容有两种挂载形态。
/// Pinned = 主窗口内的悬浮面板（不占停靠位，可拖动）；取消 pin = 独立窗口。
/// 关闭浮窗只是停用一个投递渠道，通知列表与通知中心不受影响。
/// </summary>
public partial class MainWindow
{
    private FloatingPanelViewModel _floating = null!;
    private Views.FloatingPanelView? _floatingView;
    private Window? _floatingWindow;
    private Border? _floatingHost;
    private bool _floatingManualShow;
    private readonly System.Collections.Generic.HashSet<string> _windowsNoticeKeys = new();
    private string? _windowsFallbackKey;

    internal FloatingPanelViewModel FloatingForTesting => _floating;
    internal bool FloatingDetachedForTesting => _floatingWindow is not null;
    internal bool FloatingPinnedVisibleForTesting => _floatingHost?.IsVisible == true;
    internal Views.FloatingPanelView? FloatingViewForTesting => _floatingView;
    internal Control SideChatHostForTesting => _sideChatHost;
    internal Window? FloatingWindowForTesting => _floatingWindow;

    private void InitializeFloatingPanel()
    {
        var dataRoot = Program.SelfTestMode
            ? Program.SelfTestDataRoot ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fufucode-selftest-appdata")
            : _options.CreateAppDataLayout().Root;
        _floating = new FloatingPanelViewModel(_notifications, new FloatingWindowPreferencesStore(
            System.IO.Path.Combine(dataRoot, "floating-window.json")));
        _floating.HostChanged += () =>
        {
            if (!_floating.IsOpen) _floatingManualShow = false;
            ApplyFloatingHost(); RefreshNotificationChannel();
        };
        _notifications.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(NotificationCenterViewModel.HasItems) or nameof(NotificationCenterViewModel.Items))
                ApplyFloatingHost();
        };
        _settingsPage.AttachFloating(_floating);
        ApplyFloatingHost();
    }

    /// <summary>把浮窗重新挂到当前形态对应的宿主上。</summary>
    private void ApplyFloatingHost()
    {
        if (_floating is null) return;
        // 空面板不常驻：只在有提醒、或用户手动唤出时出现，避免压住工作区。
        if (!_floating.IsOpen || !ShouldShowFloating())
        {
            DetachFloatingView();
            _floatingHost?.IsVisible = false;
            if (_floatingWindow is not null) CloseFloatingWindow();
            return;
        }
        if (_floating.Pinned) AttachFloatingInline();
        else AttachFloatingWindow();
    }

    private bool ShouldShowFloating() => _floatingManualShow || _notifications.HasItems;

    /// <summary>手动"显示浮窗"时即使暂无提醒也显示（面板会给出空状态说明）。</summary>
    private void SetFloatingVisible(bool visible)
    {
        _floatingManualShow = visible;
        if (visible && !_floating.IsOpen) _floating.IsOpen = true;
        else ApplyFloatingHost();
    }

    private Control EnsureFloatingView()
    {
        if (_floatingView is not null) return _floatingView;
        _floatingView = new Views.FloatingPanelView { DataContext = _floating };
        _floatingView.SideChatRequested += MountSideChatIntoFloating;
        _floatingView.DragRequested += MoveFloatingPanel;
        _floatingView.DragCompleted += CaptureFloatingWindowBounds;
        return _floatingView;
    }

    private void AttachFloatingInline()
    {
        CloseFloatingWindow();
        var view = EnsureFloatingView();
        if (_floatingHost is null)
        {
            _floatingHost = new Border
            {
                ZIndex = 22, Margin = new Thickness(16),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = view,
            };
            Grid.SetRow(_floatingHost, 1);
            ChromeRoot.Children.Add(_floatingHost);
        }
        // 独立窗口归还控件后再挂回内嵌宿主（同一实例只能挂一处）。
        if (_floatingWindow is not null) { _floatingWindow.Content = null; }
        if (!ReferenceEquals(_floatingHost!.Child, view)) _floatingHost.Child = view;
        _floatingHost.Width = _floating.PanelWidth;
        _floatingHost.Height = _floating.PanelHeight;
        _floatingHost.IsVisible = true;
    }

    private void AttachFloatingWindow()
    {
        var view = EnsureFloatingView();
        // 控件只能有一个父级：先把它从内嵌宿主摘下，再交给独立窗口。
        if (_floatingHost is not null) { _floatingHost.Child = null; _floatingHost.IsVisible = false; }
        if (_floatingWindow is null)
        {
            _floatingWindow = new Window
            {
                Title = "浮窗", Width = _floating.PanelWidth, Height = _floating.PanelHeight,
                MinWidth = 280, MinHeight = 240,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = true,
                Content = view,
            };
            PositionFloatingWindow();
            _floatingWindow.Closed += (_, _) =>
            {
                // 用户直接关掉窗口等价于关闭浮窗（提醒回落到浮层）。
                if (_floatingWindow is null) return;
                _floatingWindow = null;
                if (_floating.IsOpen) _floating.IsOpen = false;
                _floatingHost?.IsVisible = false;
            };
            _floatingWindow.Show(this);
        }
        else if (!ReferenceEquals(_floatingWindow.Content, view)) _floatingWindow.Content = view;
    }

    private void PositionFloatingWindow()
    {
        if (_floatingWindow is not { } window) return;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        if (_floating.HasPosition) { window.Position = new PixelPoint((int)_floating.Left, (int)_floating.Top); return; }
        window.Position = new PixelPoint(Position.X + (int)Bounds.Width - (int)_floating.PanelWidth - 40, Position.Y + 120);
    }

    /// <summary>内嵌形态拖动：只改面板在窗口内的位置。</summary>
    private void MoveFloatingPanel(Point delta)
    {
        if (_floatingHost is null || !_floating.Pinned) return;
        var margin = _floatingHost.Margin;
        _floatingHost.Margin = new Thickness(
            Math.Max(0, margin.Left + delta.X), Math.Max(0, margin.Top + delta.Y), 0, 0);
        _floatingHost.HorizontalAlignment = HorizontalAlignment.Left;
        _floatingHost.VerticalAlignment = VerticalAlignment.Top;
    }

    private void CaptureFloatingWindowBounds(Point _)
    {
        if (_floatingWindow is not { } window) return;
        _floating.RememberBounds(window.Position.X, window.Position.Y, window.Width, window.Height);
    }

    private void CloseFloatingWindow()
    {
        if (_floatingWindow is not { } window) return;
        _floatingWindow = null;
        window.Content = null;
        try { window.Close(); } catch (InvalidOperationException) { }
    }

    private void DetachFloatingView()
    {
        CloseFloatingWindow();
        if (_floatingHost is not null) _floatingHost.Child = null;
    }

    /// <summary>
    /// 辅助对话在浮窗与停靠页之间迁移同一个控件实例：不新建会话，也不出现两份聊天。
    /// </summary>
    private void MountSideChatIntoFloating()
    {
        if (_floatingView is null) return;
        var slot = _floatingView.SideChatHost;
        if (ReferenceEquals(slot.Content, _sideChatHost)) return;
        if (_sideChatDockSlot is not null) _sideChatDockSlot.Child = null;
        slot.Content = _sideChatHost;
    }

    /// <summary>浮窗收起或切回提醒分区后，把辅助对话送回停靠页。</summary>
    private void ReturnSideChatToDock()
    {
        if (_floatingView?.SideChatHost.Content is null) return;
        _floatingView.SideChatHost.Content = null;
        if (_sideChatDockSlot is not null) _sideChatDockSlot.Child = _sideChatHost;
    }

    /// <summary>浮窗状态行取用的状态文案（与状态栏同一口径）。</summary>
    internal string FloatingStatusText(string id) => StatusText(id);

    /// <summary>浮窗快捷动作按钮绑定的命令。</summary>
    internal System.Windows.Input.ICommand FloatingActionCommand(string id) => _actions.GetCommand(id);

    private void ToggleFloatingPanel()
    {
        var show = !(ShouldShowFloating() && _floating.IsOpen);
        SetFloatingVisible(show);
    }

    /// <summary>渠道偏好变化后立即重算浮层可见性，不必等下一次通知。</summary>
    private void RefreshNotificationChannel()
    {
        NotificationPrompt.IsVisible = _notifications.HasPrompt && (_floating.DeliversToToast ||
            _windowsFallbackKey == _notifications.LatestPrompt?.Key);
        if (_floating.DeliversToWindows && _notifications.LatestPrompt is { } notice &&
            _windowsNoticeKeys.Add(notice.Key)) _ = DeliverWindowsNoticeAsync(notice);
    }

    private async Task DeliverWindowsNoticeAsync(NotificationItemViewModel notice)
    {
        var result = Program.SelfTestMode ? new WindowsToastResult(false, "自测不投递系统通知，已回退应用内提醒") :
            await WindowsToastService.SendAsync(notice.Key, notice.Title, notice.Message,
                _host is null ? null : Integration.SessionDeepLink.Create(notice.SessionId, _host.ProjectPath));
        _floating.WindowsDeliveryStatus = result.Status;
        if (!result.Delivered) _windowsFallbackKey = notice.Key;
        NotificationPrompt.IsVisible = _notifications.HasPrompt && (_floating.DeliversToToast ||
            _windowsFallbackKey == _notifications.LatestPrompt?.Key);
    }

    private void SetFloatingPinned(bool pinned)
    {
        _floating.Pinned = pinned;
        if (pinned) ReturnSideChatToDock();
    }
}
