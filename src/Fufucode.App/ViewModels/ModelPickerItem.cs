using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Fufucode.App.ViewModels;

/// <summary>
/// 模型选择器里的一行。Magpie 作为一个聚合提供商出现，
/// 这里的 Value 是发给端点的完整模型 id（如 antigravity/gemini-3.8-flash）。
/// </summary>
public sealed partial class ModelPickerItem : ObservableObject
{
    private bool _isPinned;

    public ModelPickerItem(string value, string label, bool isPinned)
    {
        Value = value;
        Label = label;
        _isPinned = isPinned;
    }

    /// <summary>发给端点的模型 id。</summary>
    public string Value { get; }

    /// <summary>面向用户的显示名。</summary>
    public string Label { get; }

    /// <summary>置顶状态；置顶项排在最前。</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value) return;
            _isPinned = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PinGlyph));
        }
    }

    /// <summary>置顶图标：实心星表示已置顶。</summary>
    public string PinGlyph => _isPinned ? "★" : "☆";

    public string PinToolTip => _isPinned ? "取消置顶" : "置顶该模型";
}
