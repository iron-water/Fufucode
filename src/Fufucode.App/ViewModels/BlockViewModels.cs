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
    private bool _isFinal;
    private bool _isAbsorbed;

    /// <summary>是否是回合的最终总结。最终总结保持完整颜色与展开。</summary>
    public bool IsFinal
    {
        get => _isFinal;
        set => SetProperty(ref _isFinal, value);
    }

    /// <summary>回合结束后被最终总结弱化的中间过程文字；内容仍保留，可展开查看。</summary>
    public bool IsAbsorbed
    {
        get => _isAbsorbed;
        set => SetProperty(ref _isAbsorbed, value);
    }

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
    private bool _userExpanded = true;
    private bool _isLive = true;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    /// <summary>思考仍在生成：强制展开，且不允许折叠（与 Claude Desktop 一致）。</summary>
    public bool IsLive
    {
        get => _isLive;
        set
        {
            if (!SetProperty(ref _isLive, value)) return;
            OnPropertyChanged(nameof(IsExpanded));
            OnPropertyChanged(nameof(IsCollapsed));
            OnPropertyChanged(nameof(CanToggle));
        }
    }

    /// <summary>运行中强制展开；结束后回到用户选择（默认折叠）。</summary>
    public bool IsExpanded => _isLive || _userExpanded;

    /// <summary>生成过程中不允许折叠，避免把正在输出的推理藏起来。</summary>
    public bool CanToggle => !_isLive;

    public bool IsCollapsed => !IsExpanded;

    [RelayCommand]
    private void ToggleExpanded()
    {
        if (!CanToggle) return;
        _userExpanded = !_userExpanded;
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(IsCollapsed));
    }

    /// <summary>回合结束：停止 live，并按默认折叠（除非用户之前手动展开过）。</summary>
    public void Settle() => IsLive = false;
}

public sealed partial class ToolCallBlockViewModel : BlockViewModel
{
    /// <summary>超过此行数的工具输出默认折叠。</summary>
    public const int CollapseOutputLineThreshold = 15;

    private const double CollapsedOutputHeight = 160;

    private string _toolCallId = string.Empty;
    private string _toolName = string.Empty;
    private string _inputJson = string.Empty;
    private string? _statusText;
    private ToolCallStatus _status = ToolCallStatus.Pending;
    private string? _output;
    private bool _isErrorOutput;
    private bool _outputExpanded;
    private int _outputLineCount;

    /// <summary>工具调用 id：工具组用它做稳定身份与结果回填。</summary>
    public string ToolCallId
    {
        get => _toolCallId;
        set => SetProperty(ref _toolCallId, value);
    }

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
        ToolCallId = block.ToolCallId;
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
