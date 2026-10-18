using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Fufucode.App.ViewModels;

/// <summary>
/// 模型选择器里的一个关键字快捷项。每个关键字带有自己的关闭按钮，
/// 关闭后该关键字从本次选择器的快捷区隐藏（持久化到设置）。
/// </summary>
public sealed partial class ModelKeywordItem : ObservableObject
{
    private bool _isDismissed;

    public ModelKeywordItem(string keyword, bool isDismissed)
    {
        Keyword = keyword;
        _isDismissed = isDismissed;
    }

    public string Keyword { get; }

    /// <summary>是否已被用户关闭。</summary>
    public bool IsDismissed
    {
        get => _isDismissed;
        internal set
        {
            if (_isDismissed == value) return;
            _isDismissed = value;
            OnPropertyChanged();
        }
    }
}
