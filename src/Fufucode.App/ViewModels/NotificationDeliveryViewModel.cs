using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.ViewModels;

public sealed partial class NotificationDeliveryViewModel : ViewModelBase
{
    private readonly NotificationDeliveryPreferencesStore _store;
    private NotificationDeliveryPreferences _applied = new();
    public NotificationDeliveryViewModel(NotificationDeliveryPreferencesStore store)
    {
        _store = store;
        try { _applied = store.Load(); }
        catch (Exception error) { SaveError = error.GetBaseException().Message; }
    }
    public NotificationWindowPosition? LegacyPosition => _applied.LegacyPosition;
    public string? MigrationNote => _store.MigrationNote;
    [ObservableProperty] private string? _saveError;
    [ObservableProperty] private string _windowsDeliveryStatus = "系统通知尚未投递";
    public bool DeliversToToast { get => _applied.Toast; set => Apply(_applied with { Toast = value }); }
    public bool DeliversToPage { get => _applied.Page; set => Apply(_applied with { Page = value }); }
    public bool DeliversToWindows { get => _applied.Windows; set => Apply(_applied with { Windows = value }); }
    public string DeliverySummary => "已开启：" + (DeliversToToast ? "应用内提示 " : "") +
        (DeliversToPage ? "通知页提示 " : "") + (DeliversToWindows ? "Windows通知" : "") +
        (!DeliversToToast && !DeliversToPage && !DeliversToWindows ? "无；通知记录仍可查看" : "");
    public event Action? Changed;
    private void Apply(NotificationDeliveryPreferences value)
    {
        if (value == _applied) return;
        _applied = value;
        try { _store.Save(value); SaveError = null; }
        catch (Exception error) { SaveError = error.GetBaseException().Message; }
        OnPropertyChanged(nameof(DeliversToToast)); OnPropertyChanged(nameof(DeliversToPage));
        OnPropertyChanged(nameof(DeliversToWindows)); OnPropertyChanged(nameof(DeliverySummary));
        Changed?.Invoke();
    }
}
