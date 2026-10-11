using System;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.ViewModels;

/// <summary>One applied preference object shared by settings and notifications.</summary>
public sealed class NotificationPreferencesViewModel : ViewModelBase
{
    private readonly NotificationPreferencesStore? _store;
    private NotificationPreferences _applied = new();
    private string? _saveError;
    private string _saveStatusText = "当前已应用 · 未写入磁盘";

    public NotificationPreferencesViewModel(NotificationPreferencesStore? store = null)
    {
        _store = store;
        if (store is null) return;
        try { _applied = store.Load(); _saveStatusText = "当前已应用"; }
        catch (Exception error) { _saveError = error.GetBaseException().Message; _saveStatusText = "读取失败 · 当前使用默认值"; }
    }

    public int SelectedCompletionPolicyIndex
    {
        get => (int)_applied.CompletionPolicy;
        set
        {
            if (value is < 0 or > 2 || value == SelectedCompletionPolicyIndex) return;
            Apply(_applied with { CompletionPolicy = (CompletionNotificationPolicy)value });
        }
    }
    public CompletionNotificationPolicy CompletionPolicy => _applied.CompletionPolicy;
    public bool NotifyApprovals
    {
        get => _applied.NotifyApprovals;
        set { if (value != _applied.NotifyApprovals) Apply(_applied with { NotifyApprovals = value }); }
    }
    public string SaveStatusText => _saveStatusText;
    public string? SaveError => _saveError;
    public bool HasSaveError => _saveError is not null;
    public event Action? Applied;

    private void Apply(NotificationPreferences next)
    {
        try
        {
            _store?.Save(next);
            _applied = next;
            _saveError = null;
            _saveStatusText = _store is null ? "当前已应用 · 未写入磁盘" : "已保存 · 当前已应用";
            Applied?.Invoke();
        }
        catch (Exception error) { _saveError = error.GetBaseException().Message; _saveStatusText = "保存失败 · 已回退到原值"; }
        // A two-way control must return to the actual applied value when the write fails.
        OnPropertyChanged(nameof(SelectedCompletionPolicyIndex));
        OnPropertyChanged(nameof(CompletionPolicy));
        OnPropertyChanged(nameof(NotifyApprovals));
        OnPropertyChanged(nameof(SaveStatusText));
        OnPropertyChanged(nameof(SaveError));
        OnPropertyChanged(nameof(HasSaveError));
    }
}
