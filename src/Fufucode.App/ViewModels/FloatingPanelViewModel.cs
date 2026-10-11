using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.ViewModels;

/// <summary>
/// 浮窗：承载四类会话事件提醒，并提供辅助对话入口。
/// Pinned 时嵌在主窗口内（悬浮面板，不占停靠位）；取消 pin 后可脱离成独立窗口。
/// 关闭浮窗只关掉这一个投递渠道，不影响通知列表本身。
/// </summary>
public sealed partial class FloatingPanelViewModel : ViewModelBase
{
    private readonly FloatingWindowPreferencesStore? _store;
    private FloatingWindowPreferences _applied;
    private string? _saveError;

    public FloatingPanelViewModel(NotificationCenterViewModel center, FloatingWindowPreferencesStore? store = null)
    {
        Center = center;
        _store = store;
        try { _applied = store?.Load() ?? new(); }
        catch (Exception error) { _saveError = error.GetBaseException().Message; _applied = new(); }
    }

    public NotificationCenterViewModel Center { get; }
    public string Title => "浮窗";
    public string? SaveError => _saveError;
    public bool HasSaveError => _saveError is not null;

    /// <summary>Pin：只在应用内部显示（主窗口内悬浮面板）。取消 pin：可脱离成独立窗口。</summary>
    public bool Pinned
    {
        get => _applied.Pinned;
        set
        {
            if (value == _applied.Pinned) return;
            Apply(_applied with { Pinned = value });
        }
    }

    /// <summary>用户是否开着浮窗。关闭后提醒回落到浮层。</summary>
    public bool IsOpen
    {
        get => _applied.IsOpen;
        set
        {
            if (value == _applied.IsOpen) return;
            Apply(_applied with { IsOpen = value });
        }
    }

    public bool DeliversToToast
    {
        get => _applied.DeliversToToast;
        set => Apply(_applied with { Delivery = SetFlag(FloatingDelivery.Toast, value) });
    }
    public bool DeliversToFloating
    {
        get => _applied.DeliversToFloating;
        set => Apply(_applied with { Delivery = SetFlag(FloatingDelivery.Floating, value) });
    }
    [ObservableProperty] private string _windowsDeliveryStatus = "系统通知尚未投递";
    /// <summary>Windows 系统通知，失败由宿主回退应用内。</summary>
    public bool DeliversToWindows
    {
        get => _applied.DeliversToWindows;
        set => Apply(_applied with { Delivery = SetFlag(FloatingDelivery.Windows, value) });
    }

    public double PanelWidth => _applied.Width;
    public double PanelHeight => _applied.Height;
    public bool HasPosition => _applied.HasPosition;
    public double Left => _applied.Left;
    public double Top => _applied.Top;

    /// <summary>位置与尺寸变化：独立窗口形态下由宿主回写。</summary>
    public void RememberBounds(double left, double top, double width, double height)
    {
        var next = _applied with { Left = left, Top = top, Width = width, Height = height };
        if (Math.Abs(next.Left - _applied.Left) < 1 && Math.Abs(next.Top - _applied.Top) < 1 &&
            Math.Abs(next.Width - _applied.Width) < 1 && Math.Abs(next.Height - _applied.Height) < 1) return;
        Persist(next, notify: false);
        _applied = next;
    }

    public string DeliverySummary => (_applied.DeliversToToast, _applied.DeliversToFloating, _applied.DeliversToWindows) switch
    {
        (false, false, false) => "当前没有投递渠道：提醒不会出现，通知列表仍可查看",
        (true, true, _) => "浮层与浮窗都会提醒",
        (true, false, _) => "只在浮层提醒",
        (false, true, _) => "只在浮窗提醒",
        _ => "渠道已按选择投递",
    };

    /// <summary>浮窗开关或形态变化后触发，宿主据此重新挂载。</summary>
    public event Action? HostChanged;

    public void ToggleOpen() => IsOpen = !IsOpen;

    [RelayCommand] private void TogglePin() => Pinned = !Pinned;
    [RelayCommand] private void ClosePanel() => IsOpen = false;

    private FloatingDelivery SetFlag(FloatingDelivery flag, bool on)
        => on ? _applied.Delivery | flag : _applied.Delivery & ~flag;

    private void Apply(FloatingWindowPreferences next)
    {
        Persist(next, notify: true);
        _applied = next;
    }

    private void Persist(FloatingWindowPreferences next, bool notify)
    {
        try
        {
            _store?.Save(next);
            _saveError = null;
        }
        catch (Exception error) { _saveError = error.GetBaseException().Message; }
        if (!notify) return;
        OnPropertyChanged(nameof(Pinned));
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(DeliversToToast));
        OnPropertyChanged(nameof(DeliversToFloating));
        OnPropertyChanged(nameof(DeliversToWindows));
        OnPropertyChanged(nameof(DeliverySummary));
        OnPropertyChanged(nameof(HasSaveError));
        OnPropertyChanged(nameof(SaveError));
        Dispatcher.UIThread.Post(() => HostChanged?.Invoke());
    }
}
