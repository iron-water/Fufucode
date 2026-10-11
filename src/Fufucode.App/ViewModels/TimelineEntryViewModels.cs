using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;
using Fufucode.App.Streaming;

namespace Fufucode.App.ViewModels;

/// <summary>时间线条目的展示模型基类。</summary>
public abstract class TimelineEntryViewModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;

    private DateTimeOffset _timestamp;

    public DateTimeOffset Timestamp
    {
        get => _timestamp;
        set
        {
            if (SetProperty(ref _timestamp, value))
            {
                OnPropertyChanged(nameof(TimeText));
                OnPropertyChanged(nameof(HasTime));
            }
        }
    }

    /// <summary>未知时间不显示占位值：mock 或历史缺失时返回空串。</summary>
    public string TimeText => Timestamp == default ? string.Empty : Timestamp.ToLocalTime().ToString("HH:mm");
    public bool HasTime => TimeText.Length > 0;
}

public sealed class UserMessageViewModel : TimelineEntryViewModel
{
    private string _text = string.Empty;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public void UpdateFrom(ChatMessage message)
    {
        Text = message.Blocks.OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty;
    }
}

public sealed class AssistantMessageViewModel : TimelineEntryViewModel
{
    private readonly IClipboardService _clipboard;
    private readonly Dictionary<int, BlockViewModel> _blocks = new();
    private string? _model;

    public AssistantMessageViewModel(IClipboardService clipboard)
    {
        _clipboard = clipboard;
    }

    public string? Model
    {
        get => _model;
        set => SetProperty(ref _model, value);
    }

    /// <summary>按 contentIndex 稳定维护块 VM：流式刷新时保留各块的展开/折叠状态。</summary>
    public ObservableCollection<BlockViewModel> Blocks { get; } = new();
    private string _changeSummary = "", _artifactSummary = "";
    public string ChangeSummary { get => _changeSummary; set { if (SetProperty(ref _changeSummary, value)) OnPropertyChanged(nameof(HasChangeSummary)); } }
    public string ArtifactSummary { get => _artifactSummary; set { if (SetProperty(ref _artifactSummary, value)) OnPropertyChanged(nameof(HasArtifactSummary)); } }
    public bool HasChangeSummary => ChangeSummary.Length > 0;
    public bool HasArtifactSummary => ArtifactSummary.Length > 0;

    /// <summary>摘要卡折叠状态。展开显示明细，折叠后只留一个简洁入口，仍参与正常布局。</summary>
    private bool _summaryExpanded = true;
    public bool SummaryExpanded
    {
        get => _summaryExpanded;
        set
        {
            if (!SetProperty(ref _summaryExpanded, value)) return;
            OnPropertyChanged(nameof(SummaryToggleLabel));
        }
    }
    public string SummaryToggleLabel => SummaryExpanded ? "折叠摘要" : "摘要";
    public void ToggleSummary() => SummaryExpanded = !SummaryExpanded;

    public void UpdateFrom(ChatMessage message)
    {
        Model = message.Model;

        var alive = new HashSet<int>();
        for (var index = 0; index < message.Blocks.Count; index++)
        {
            var snapshot = message.Blocks[index];
            alive.Add(index);
            if (!_blocks.TryGetValue(index, out var vm))
            {
                vm = CreateBlock(snapshot);
                _blocks[index] = vm;
                InsertBlockInOrder(index, vm);
            }
            ApplySnapshot(vm, snapshot);
        }

        foreach (var index in _blocks.Keys.Where(k => !alive.Contains(k)).ToArray())
        {
            Blocks.Remove(_blocks[index]);
            _blocks.Remove(index);
        }
    }

    private BlockViewModel CreateBlock(MessageBlock snapshot)
    {
        return snapshot.Kind switch
        {
            MessageBlockKind.Thinking => new ThinkingBlockViewModel(),
            MessageBlockKind.ToolCall => new ToolCallBlockViewModel(),
            _ => new TextBlockViewModel(_clipboard),
        };
    }

    private static void ApplySnapshot(BlockViewModel vm, MessageBlock snapshot)
    {
        switch (vm, snapshot)
        {
            case (TextBlockViewModel text, TextBlock textBlock):
                text.Text = textBlock.Text;
                break;
            case (ThinkingBlockViewModel thinking, ThinkingBlock thinkingBlock):
                thinking.Text = thinkingBlock.Text;
                break;
            case (ToolCallBlockViewModel tool, ToolCallBlock toolBlock):
                tool.ApplySnapshot(toolBlock);
                break;
        }
    }

    private void InsertBlockInOrder(int index, BlockViewModel vm)
    {
        var insertAt = 0;
        foreach (var existingIndex in _blocks.Keys.Where(k => k != index).OrderBy(k => k))
        {
            if (existingIndex > index)
            {
                break;
            }
            insertAt++;
        }
        Blocks.Insert(insertAt, vm);
    }
}

/// <summary>条目严重度：决定错误卡片配色（Error 红底；Neutral/Info 普通表面）。</summary>
public enum EntrySeverity
{
    Error,
    Neutral,
    Info,
}

public sealed class ErrorEntryViewModel : TimelineEntryViewModel
{
    private string _kindText = string.Empty;
    private string _message = string.Empty;
    private EntrySeverity _severity = EntrySeverity.Error;

    public string KindText
    {
        get => _kindText;
        set => SetProperty(ref _kindText, value);
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public EntrySeverity Severity
    {
        get => _severity;
        set
        {
            if (SetProperty(ref _severity, value))
            {
                OnPropertyChanged(nameof(IsError));
                OnPropertyChanged(nameof(IsNeutral));
                OnPropertyChanged(nameof(IsInfo));
            }
        }
    }

    public bool IsError => Severity == EntrySeverity.Error;
    public bool IsNeutral => Severity == EntrySeverity.Neutral;
    public bool IsInfo => Severity == EntrySeverity.Info;

    public void UpdateFrom(ErrorEntry error)
    {
        if (error.Kind == SessionErrorKind.UserCancelled)
        {
            // 用户主动停止：不是错误，按中性条目展示。
            KindText = "已中断";
            Message = "本轮已按你的请求停止。";
            Severity = EntrySeverity.Neutral;
            return;
        }

        KindText = error.Kind switch
        {
            SessionErrorKind.CommandRejected => "命令被拒绝",
            SessionErrorKind.ModelFailed => "模型失败",
            SessionErrorKind.ProcessExited => "进程退出",
            SessionErrorKind.ProtocolError => "协议错误",
            _ => "未知错误",
        };
        Message = error.Message;
        Severity = EntrySeverity.Error;
    }
}
