using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Fufucode.App.ViewModels;

/// <summary>
/// 端点表单里一个可勾选的模型。勾选结果会保存为端点的模型清单，
/// 之后会话的模型下拉直接来自这份清单，不需要每次重新拉取。
/// </summary>
public sealed partial class EndpointModelOption : ObservableObject
{
    private bool _isSelected;

    public EndpointModelOption(string id, string displayName, bool isSelected)
    {
        Id = id;
        DisplayName = displayName;
        _isSelected = isSelected;
    }

    /// <summary>勾选状态；批量设置时用 SetSelectedSilently 避免逐项回调。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            SelectionChanged?.Invoke();
        }
    }

    public string Id { get; }
    public string DisplayName { get; }

    /// <summary>勾选状态变化时通知父 VM 更新计数。</summary>
    public Action? SelectionChanged { get; set; }

    /// <summary>批量勾选时不逐项触发回调，由调用方统一通知。</summary>
    internal void SetSelectedSilently(bool value)
    {
        if (_isSelected == value) return;
        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));
    }
}
