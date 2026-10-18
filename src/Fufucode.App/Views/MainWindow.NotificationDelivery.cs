using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;

namespace Fufucode.App;

public partial class MainWindow
{
    private NotificationDeliveryViewModel _notificationDelivery = null!;
    private readonly HashSet<string> _windowsNoticeKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> _windowsNoticeOrder = new();
    private readonly HashSet<string> _windowsFallbackKeys = new(StringComparer.Ordinal);
    private bool _notificationDeliveryRefreshing;
    internal NotificationDeliveryViewModel NotificationDeliveryForTesting => _notificationDelivery;
    internal Control SideChatHostForTesting => _sideChatHost;
    internal Func<NotificationItemViewModel, Task<WindowsToastResult>>? WindowsNoticeDeliveryForTesting { get; set; }

    private void InitializeNotificationDelivery()
    {
        var root = Program.SelfTestMode ? Program.SelfTestDataRoot ?? Path.Combine(Path.GetTempPath(), "fufucode-selftest-appdata") : _options.CreateAppDataLayout().Root;
        _notificationDelivery = new(new NotificationDeliveryPreferencesStore(root));
        _notificationDelivery.Changed += RefreshNotificationChannel;
        _settingsPage.AttachNotificationDelivery(_notificationDelivery);
        _notifications.Items.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null) foreach (NotificationItemViewModel item in e.OldItems) item.PropertyChanged -= OnNotificationReadChanged;
            if (e.NewItems is not null) foreach (NotificationItemViewModel item in e.NewItems) item.PropertyChanged += OnNotificationReadChanged;
            RefreshNotificationChannel();
        };
        foreach (var dock in AllDockHosts()) dock.BadgeText = NotificationBadge;
        RefreshNotificationChannel();
    }

    private void OnNotificationReadChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(NotificationItemViewModel.IsRead)) RefreshNotificationChannel(); }
    private string? NotificationBadge(string id) => id == "notifications" && _notificationDelivery?.DeliversToPage == true &&
        _notifications.UnreadCount > 0 ? _notifications.UnreadCount.ToString() : null;

    private void SeedNotificationMigration()
    {
        if (_notificationDelivery is null || _layoutContext is null || _dockLayout.NotificationMigrationApplied) return;
        _dockLayout.NotificationMigrationApplied = true;
        if (_notificationDelivery.LegacyPosition is not { } position || _dockLayout.Find("notifications") is not null ||
            _dockLayout.FindFloating("notifications") is not null) return;
        var previousSelection = _dockLayout.Selected[DockZone.Right];
        var previousHidden = _dockLayout.Hidden[DockZone.Right];
        _dockLayout.Open("notifications", DockZone.Right);
        var state = _dockLayout.FloatPage("notifications")!;
        _dockLayout.Selected[DockZone.Right] = previousSelection;
        _dockLayout.Hidden[DockZone.Right] = previousHidden;
        state.Width = position.Width; state.Height = position.Height;
        state.X = position.X; state.Y = position.Y; state.Hidden = true;
    }

    private void RefreshNotificationChannel()
    {
        if (_notificationDelivery is null || _notifications is null || _closed || _notificationDeliveryRefreshing) return;
        _notificationDeliveryRefreshing = true;
        try
        {
            NotificationPrompt.IsVisible = _notifications.HasPrompt && (_notificationDelivery.DeliversToToast ||
                (_notificationDelivery.DeliversToWindows && _notifications.LatestPrompt is { } latest && _windowsFallbackKeys.Contains(latest.Key)));
            ToolTip.SetTip(CaptionNotifications, _notifications.SummaryText);
            _notificationsView.ShowChannelPrompt(_notificationDelivery.DeliversToPage
                ? _notifications.Items.FirstOrDefault(item => item.IsProactive && !item.IsRead && (!item.IsApproval || item.RequiresAction)) : null);
            foreach (var dock in AllDockHosts()) { dock.BadgeText = NotificationBadge; dock.RefreshBadges(); }
            if (_notificationDelivery.DeliversToWindows && _notifications.LatestPrompt is { } notice &&
                _windowsNoticeKeys.Add(notice.Key))
            {
                _windowsNoticeOrder.Enqueue(notice.Key);
                while (_windowsNoticeOrder.Count > 256)
                { var expired = _windowsNoticeOrder.Dequeue(); _windowsNoticeKeys.Remove(expired); _windowsFallbackKeys.Remove(expired); }
                _ = DeliverWindowsNoticeAsync(notice);
            }
        }
        finally { _notificationDeliveryRefreshing = false; }
    }

    private async Task DeliverWindowsNoticeAsync(NotificationItemViewModel notice)
    {
        WindowsToastResult result;
        try
        {
            result = WindowsNoticeDeliveryForTesting is { } test ? await test(notice) : Program.SelfTestMode
                ? new WindowsToastResult(false, "自测不投递系统通知，已回退应用内提醒")
                : await WindowsToastService.SendAsync(notice.Key, notice.Title, notice.Message,
                    _host is null ? null : Integration.SessionDeepLink.Create(notice.SessionId, _host.ProjectPath));
        }
        catch (Exception error) { result = new(false, "系统通知未送达，已回退应用内提示：" + error.GetBaseException().Message); }
        if (_closed || !_windowsNoticeKeys.Contains(notice.Key)) return;
        if (notice.Key == _notifications.LatestPrompt?.Key) _notificationDelivery.WindowsDeliveryStatus = result.Status;
        if (!result.Delivered) _windowsFallbackKeys.Add(notice.Key);
        RefreshNotificationChannel();
    }

    private void SynchronizeAuxiliaryPageVisibility()
    {
        if (_notifications is null || _usage is null) return;
        _notificationsView.SetCloseAllowed(!_dockLayout.PinnedPages.Contains("notifications"));
        _usageView.SetCloseAllowed(!_dockLayout.PinnedPages.Contains("usage"));
        _notifications.IsOpen = _notificationsView.IsEffectivelyVisible;
        if (_usageView.IsEffectivelyVisible && !_usage.IsOpen) _usage.Open();
        else if (!_usageView.IsEffectivelyVisible) _usage.IsOpen = false;
    }

    private void RefreshNotificationFocus()
    { if (!_closed) _notifications?.SetWindowFocused(IsActive || _floatingDocks.Values.Any(host => host.Window.IsActive)); }
}
