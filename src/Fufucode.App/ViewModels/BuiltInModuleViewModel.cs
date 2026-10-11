using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Fufucode.App.ViewModels;

public sealed partial class BuiltInModuleViewModel : ViewModelBase
{
    private readonly Func<bool, string> _apply;
    private bool _synchronizing;
    public string Id { get; }
    public string Title { get; }
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private string _status = "已启用";

    public BuiltInModuleViewModel(string id, string title, Func<bool, string> apply)
    { Id = id; Title = title; _apply = apply; }

    partial void OnIsEnabledChanged(bool value)
    {
        if (_synchronizing) return;
        try { Status = _apply(value); }
        catch (Exception error)
        {
            ApplyState(!value, "应用失败：" + error.Message);
        }
    }

    internal void ApplyState(bool enabled, string status)
    {
        _synchronizing = true;
        try { IsEnabled = enabled; Status = status; }
        finally { _synchronizing = false; }
    }
}
