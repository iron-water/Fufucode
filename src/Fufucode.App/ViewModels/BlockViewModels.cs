using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.ViewModels;

/// <summary>消息内容块（文本/思考/工具调用）的展示模型，与 Abstractions 的 MessageBlockKind 一一对应。</summary>
public abstract class BlockViewModel : ViewModelBase
{
}

public sealed class TextBlockViewModel : BlockViewModel
{
    private readonly IClipboardService _clipboard;
    private string _text = string.Empty;

    public TextBlockViewModel(IClipboardService clipboard)
    {
        _clipboard = clipboard;
    }

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                RebuildSegments();
            }
        }
    }

    public ObservableCollection<TextSegmentViewModel> Segments { get; } = new();

    private void RebuildSegments()
    {
        Segments.Clear();
        foreach (var segment in TextSegmenter.Split(Text))
        {
            if (segment.IsCode)
            {
                Segments.Add(new CodeSegmentViewModel(_clipboard, segment.Text, segment.Language));
                continue;
            }

            // 段首尾的整行空白不渲染：代码围栏两侧的 \n\n 会各自空出一行，
            // 在视觉上把块间距放大到 ~50px；段内空行保留（属于正文排版）。
            var plain = segment.Text.Trim('\r', '\n');
            if (plain.Length > 0)
            {
                Segments.Add(new PlainSegmentViewModel(plain));
            }
        }
    }
}

public sealed partial class ThinkingBlockViewModel : BlockViewModel
{
    private string _text = string.Empty;
    private bool _isExpanded = true;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    /// <summary>思考块默认展开，流式期间可见；用户可折叠。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(IsCollapsed));
            }
        }
    }

    public bool IsCollapsed => !IsExpanded;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

public sealed partial class ToolCallBlockViewModel : BlockViewModel
{
    /// <summary>超过此行数的工具输出默认折叠。</summary>
    public const int CollapseOutputLineThreshold = 15;

    private const double CollapsedOutputHeight = 160;

    private string _toolName = string.Empty;
    private string _inputJson = string.Empty;
    private string? _statusText;
    private ToolCallStatus _status = ToolCallStatus.Pending;
    private string? _output;
    private bool _isErrorOutput;
    private bool _outputExpanded;
    private int _outputLineCount;

    public string ToolName
    {
        get => _toolName;
        set => SetProperty(ref _toolName, value);
    }

    /// <summary>工具入参。流式期间可能是尚未组装完成的 JSON 片段，只能当临时文本展示。</summary>
    public string InputJson
    {
        get => _inputJson;
        set => SetProperty(ref _inputJson, value);
    }

    public string? StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public ToolCallStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                BadgeTone = StatusColors.ForToolCall(value);
                IsRunning = value is ToolCallStatus.Pending or ToolCallStatus.Running;
                OnPropertyChanged(nameof(BadgeTone));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(BadgeIsAttention));
                OnPropertyChanged(nameof(BadgeIsSuccess));
                OnPropertyChanged(nameof(BadgeIsCritical));
                OnPropertyChanged(nameof(BadgeIsNeutral));
            }
        }
    }

    /// <summary>语义色调，XAML 徽章按类映射到主题令牌（不直接携带画笔）。</summary>
    public StatusTone BadgeTone { get; private set; } = StatusTone.Accent;

    public bool BadgeIsAttention => BadgeTone == StatusTone.Accent;
    public bool BadgeIsSuccess => BadgeTone == StatusTone.Success;
    public bool BadgeIsCritical => BadgeTone == StatusTone.Critical;
    public bool BadgeIsNeutral => BadgeTone == StatusTone.Neutral;

    public bool IsRunning { get; private set; }

    public string? Output
    {
        get => _output;
        set
        {
            if (SetProperty(ref _output, value))
            {
                RecomputeOutputCollapse();
            }
        }
    }

    public bool IsErrorOutput
    {
        get => _isErrorOutput;
        set => SetProperty(ref _isErrorOutput, value);
    }

    public bool HasOutput => !string.IsNullOrEmpty(Output);

    public bool CanCollapseOutput { get; private set; }

    public bool IsOutputExpanded
    {
        get => _outputExpanded;
        set
        {
            if (SetProperty(ref _outputExpanded, value))
            {
                RefreshOutputUi();
            }
        }
    }

    public double OutputMaxHeight { get; private set; } = double.PositiveInfinity;

    public string? OutputToggleLabel { get; private set; }

    /// <summary>应用一次终态快照（BlockFinished 或 MessageFinished 的工具块）。</summary>
    public void ApplySnapshot(ToolCallBlock block)
    {
        ToolName = block.ToolName;
        InputJson = block.InputJson;
        Status = block.Status;
        StatusText = block.StatusText;
        Output = block.Output;
        IsErrorOutput = block.IsErrorOutput;
    }

    private void RecomputeOutputCollapse()
    {
        _outputLineCount = TextSegmenter.CountLines(Output);
        var canCollapse = _outputLineCount > CollapseOutputLineThreshold;
        if (canCollapse != CanCollapseOutput)
        {
            CanCollapseOutput = canCollapse;
            OnPropertyChanged(nameof(CanCollapseOutput));
            if (!canCollapse && !IsOutputExpanded)
            {
                IsOutputExpanded = true;
                return;
            }
        }
        RefreshOutputUi();
    }

    private void RefreshOutputUi()
    {
        OutputMaxHeight = IsOutputExpanded ? double.PositiveInfinity : CollapsedOutputHeight;
        OutputToggleLabel = IsOutputExpanded ? "收起" : $"展开（{_outputLineCount} 行）";
        OnPropertyChanged(nameof(OutputMaxHeight));
        OnPropertyChanged(nameof(OutputToggleLabel));
    }

    [RelayCommand]
    private void ToggleOutput() => IsOutputExpanded = !IsOutputExpanded;
}
