using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.ViewModels;

/// <summary>文本段（普通文本或代码块）的展示模型。</summary>
public abstract class TextSegmentViewModel : ViewModelBase
{
}

public sealed partial class PlainSegmentViewModel : TextSegmentViewModel
{
    public PlainSegmentViewModel(string text)
    {
        Text = text;
    }

    private string _text = string.Empty;

    /// <summary>可观察：流式增量直接改文本，不重建集合，避免整段闪烁。</summary>
    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }
}

public sealed partial class CodeSegmentViewModel : TextSegmentViewModel
{
    /// <summary>超过此行数的代码块默认折叠（计划 §9：长输出折叠）。</summary>
    public const int CollapseLineThreshold = 20;

    private const double CollapsedMaxHeight = 180;

    private readonly IClipboardService _clipboard;

    public CodeSegmentViewModel(IClipboardService clipboard, string code, string language)
    {
        _clipboard = clipboard;
        Code = code;
        Language = language;
        LineCount = TextSegmenter.CountLines(code);
        CanCollapse = LineCount > CollapseLineThreshold;
        _isExpanded = !CanCollapse;
        RefreshSizeUi();
    }

    private string _code = string.Empty;
    private string _language = string.Empty;

    /// <summary>可观察：流式代码块增量更新，不重建集合。</summary>
    public string Code
    {
        get => _code;
        set => SetProperty(ref _code, value);
    }

    public string Language
    {
        get => _language;
        set => SetProperty(ref _language, value);
    }

    public int LineCount { get; set; }

    public bool CanCollapse { get; set; }

    public bool ShowLanguage => !string.IsNullOrEmpty(Language);

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value) => RefreshSizeUi();

    public double MaxHeight { get; private set; } = double.PositiveInfinity;

    public string ExpandLabel { get; private set; } = "展开";

    private void RefreshSizeUi()
    {
        MaxHeight = IsExpanded ? double.PositiveInfinity : CollapsedMaxHeight;
        ExpandLabel = IsExpanded ? "收起" : $"展开（{LineCount} 行）";
        OnPropertyChanged(nameof(MaxHeight));
        OnPropertyChanged(nameof(ExpandLabel));
    }

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private async Task CopyAsync() => await _clipboard.SetTextAsync(Code);
}
