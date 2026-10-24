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
    private bool _isPending;
    private bool _isFailed;
    private bool _showAckHint;
    private bool _isPinned;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    /// <summary>在左侧轮次刻度里固定这一轮，便于快速跳回。</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set => SetProperty(ref _isPinned, value);
    }

    /// <summary>本地乐观显示、尚未收到后端确认的用户消息。</summary>
    public bool IsPending
    {
        get => _isPending;
        set
        {
            if (!SetProperty(ref _isPending, value)) return;
            OnPropertyChanged(nameof(ShowAckHint));
        }
    }

    /// <summary>发送被后端拒绝；气泡保留并显示失败态，草稿由输入区回填。</summary>
    public bool IsFailed
    {
        get => _isFailed;
        set
        {
            if (!SetProperty(ref _isFailed, value)) return;
            OnPropertyChanged(nameof(ShowAckHint));
        }
    }

    /// <summary>确认延迟超过后端阈值时显示"发送中…"。</summary>
    public bool ShowAckHint
    {
        get => _showAckHint;
        set => SetProperty(ref _showAckHint, value);
    }

    public bool IsConfirmed => !IsPending && !IsFailed;

    public void UpdateFrom(ChatMessage message)
    {
        Text = message.Blocks.OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty;
    }
}

public enum TurnPhase
{
    Streaming,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// 一个 Agent 回合的展示模型。
///
/// 与旧实现的关键区别：它不再对应"一条 API 消息"，而是对应"一次用户提交到 TurnSettled"。
/// 同一回合里连续的多条 assistant 消息、思考块、工具调用、中间文字、最终回答都合并到这里，
/// 因此整个回合只渲染一个头像。
///
/// 块的身份使用 (MessageId, contentIndex) 复合键：不同消息的 contentIndex 会重复，
/// 只用索引会让后一条消息覆盖前一条的块。工具调用额外用 ToolCallId 做组内定位。
/// </summary>
public sealed class AssistantMessageViewModel : TimelineEntryViewModel
{
    private readonly IClipboardService _clipboard;
    private readonly Dictionary<(string MessageId, int Index), BlockViewModel> _blocks = new();
    private readonly List<(string MessageId, int Index)> _blockOrder = new();
    private readonly List<ToolGroupViewModel> _groups = new();
    private readonly List<string> _messageOrder = new();
    private string? _model;
    private TurnPhase _phase = TurnPhase.Streaming;
    private bool _isComplete;
    private bool _isAwaitingFirstEvent = true;
    private bool _showAwaitingIndicator;
    private string _awaitingText = "正在等待响应…";

    public AssistantMessageViewModel(IClipboardService clipboard)
    {
        _clipboard = clipboard;
    }

    public string? Model
    {
        get => _model;
        set
        {
            if (!SetProperty(ref _model, value)) return;
            OnPropertyChanged(nameof(HasModel));
            OnPropertyChanged(nameof(ModelText));
        }
    }

    public bool HasModel => !string.IsNullOrWhiteSpace(Model);
    /// <summary>模型短名：去掉 provider/ 前缀，与竞品一致只显示模型本身。</summary>
    public string ModelText => Model is { Length: > 0 } model
        ? model[(model.LastIndexOf('/') + 1)..]
        : string.Empty;

    private UsageInfo? _usage;
    /// <summary>本回合的 token 用量；未知时为 null（不显示占位）。</summary>
    public UsageInfo? Usage
    {
        get => _usage;
        set
        {
            if (!SetProperty(ref _usage, value)) return;
            OnPropertyChanged(nameof(HasUsage));
            OnPropertyChanged(nameof(UsageText));
        }
    }

    public bool HasUsage => _usage is { } usage && (usage.InputTokens is not null || usage.OutputTokens is not null);

    /// <summary>用量文本："输入 1.2K · 输出 3.4K"；有费用时追加。</summary>
    public string UsageText
    {
        get
        {
            if (_usage is not { } usage) return string.Empty;
            var parts = new List<string>();
            if (usage.InputTokens is { } input) parts.Add("输入 " + FormatTokens(input));
            if (usage.OutputTokens is { } output) parts.Add("输出 " + FormatTokens(output));
            if (usage.TotalCost is { } cost) parts.Add("$" + cost.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
            return string.Join(" · ", parts);
        }
    }

    private static string FormatTokens(int value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value / 1_000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "K",
        _ => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private long? _durationMs;
    /// <summary>本回合耗时（毫秒）；未知时为 null。</summary>
    public long? DurationMs
    {
        get => _durationMs;
        set
        {
            if (!SetProperty(ref _durationMs, value)) return;
            OnPropertyChanged(nameof(HasDuration));
            OnPropertyChanged(nameof(DurationText));
        }
    }

    public bool HasDuration => _durationMs is > 0;
    public string DurationText => _durationMs is not { } ms ? string.Empty
        : ms < 1000 ? $"{ms} ms"
        : ms < 60_000 ? $"{ms / 1000d:0.#} s"
        : $"{(int)(ms / 60_000)} min {(int)(ms % 60_000 / 1000)} s";

    /// <summary>回合阶段。流式期间不显示底部元信息卡。</summary>
    public TurnPhase Phase
    {
        get => _phase;
        private set
        {
            if (!SetProperty(ref _phase, value)) return;
            OnPropertyChanged(nameof(IsStreaming));
            OnPropertyChanged(nameof(ShowMetaCard));
            OnPropertyChanged(nameof(HeaderStatusText));
        }
    }

    public bool IsStreaming => Phase == TurnPhase.Streaming;

    /// <summary>回合已开始但还没有收到任何内容块。</summary>
    public bool IsAwaitingFirstEvent
    {
        get => _isAwaitingFirstEvent;
        internal set
        {
            if (!SetProperty(ref _isAwaitingFirstEvent, value)) return;
            if (value) return;
            ShowAwaitingIndicator = false;
        }
    }

    /// <summary>超过后端宽限阈值后显示等待指示器（三点动画）。</summary>
    public bool ShowAwaitingIndicator
    {
        get => _showAwaitingIndicator;
        set => SetProperty(ref _showAwaitingIndicator, value);
    }

    /// <summary>等待文案：慢启动后端显示具体名称，其余显示通用文案。</summary>
    public string AwaitingText
    {
        get => _awaitingText;
        set => SetProperty(ref _awaitingText, value);
    }

    /// <summary>后端声明呈现时序；UI 只读取，不判断后端类型。</summary>
    public BackendPresentationProfile Presentation { get; set; } = BackendPresentationProfile.Default;
    public bool IsComplete => _isComplete;

    /// <summary>输出结束后才出现底部卡片（用量/模型/时间/操作）。</summary>
    public bool ShowMetaCard => !IsStreaming;

    public string HeaderStatusText => Phase switch
    {
        TurnPhase.Completed => "已完成",
        TurnPhase.Failed => "已失败",
        TurnPhase.Cancelled => "已中断",
        _ when _groups.Any(group => group.IsActive) => "正在使用工具",
        _ => "正在撰写",
    };

    /// <summary>源数据：按 (MessageId, contentIndex) 稳定维护，流式刷新保留折叠状态。</summary>
    public ObservableCollection<BlockViewModel> Blocks { get; } = new();

    /// <summary>
    /// 渲染投影：相邻工具调用合并为 <see cref="ToolGroupViewModel"/>，其余块原样保留。
    /// 折叠只改投影的展示状态，不动源数据。
    /// </summary>
    public ObservableCollection<object> DisplayBlocks { get; } = new();

    /// <summary>本回合的文件改动总结卡（Codex 风格），由 TimelineViewModel 在回合结束时填充。</summary>
    public TurnChangeSummaryViewModel ChangeCard { get; } = new();

    /// <summary>正文里出现的链接与文件路径，回合结束时提取一次。</summary>
    public System.Collections.ObjectModel.ObservableCollection<MessageLinkViewModel> Links { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<MessageFileViewModel> Files { get; } = new();
    public bool HasLinks => Links.Count > 0;
    public bool HasFiles => Files.Count > 0;
    private bool _relatedFilesExpanded;
    public bool RelatedFilesExpanded
    {
        get => _relatedFilesExpanded;
        set
        {
            if (!SetProperty(ref _relatedFilesExpanded, value)) return;
            OnPropertyChanged(nameof(RelatedFilesToggleText));
        }
    }
    public string RelatedFilesToggleText => RelatedFilesExpanded ? "收起相关文件" : $"相关文件 · {Files.Count}";
    public void ToggleRelatedFiles() => RelatedFilesExpanded = !RelatedFilesExpanded;

    private string _changeSummary = "", _artifactSummary = "";
    public string ChangeSummary { get => _changeSummary; set { if (SetProperty(ref _changeSummary, value)) OnPropertyChanged(nameof(HasChangeSummary)); } }
    public string ArtifactSummary
    {
        get => _artifactSummary;
        set
        {
            if (!SetProperty(ref _artifactSummary, value)) return;
            OnPropertyChanged(nameof(HasArtifactSummary));
            OnPropertyChanged(nameof(ShowArtifactBlock));
        }
    }
    public bool HasChangeSummary => ChangeSummary.Length > 0;
    public bool HasArtifactSummary => ArtifactSummary.Length > 0;

    /// <summary>变更卡已经逐文件列出产物时不再重复；没有产物时整块不出现。</summary>
    public bool ShowArtifactBlock => HasArtifactSummary && !ChangeCard.HasChanges;

    /// <summary>变更卡内容变化后，产物块的可见性可能跟着变。</summary>
    internal void RaiseSummariesChanged() => OnPropertyChanged(nameof(ShowArtifactBlock));

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

    /// <summary>
    /// 合并一条 assistant 消息到本回合。按 (MessageId, index) upsert 块，
    /// 不重建已有块，因此流式刷新与展开状态都不会丢。
    /// </summary>
    public void MergeMessage(ChatMessage message)
    {
        Model = message.Model;
        if (message.CumulativeUsage is { } usage) Usage = usage;

        if (!_messageOrder.Contains(message.Id)) _messageOrder.Add(message.Id);
        if (message.Blocks.Count > 0) IsAwaitingFirstEvent = false;

        var alive = new HashSet<int>();
        for (var index = 0; index < message.Blocks.Count; index++)
        {
            var snapshot = message.Blocks[index];
            alive.Add(index);
            var key = (message.Id, index);
            if (!_blocks.TryGetValue(key, out var vm))
            {
                vm = CreateBlock(snapshot);
                _blocks[key] = vm;
                InsertBlockInOrder(key, vm);
            }
            ApplySnapshot(vm, snapshot);
        }

        foreach (var key in _blocks.Keys.Where(k => k.MessageId == message.Id && !alive.Contains(k.Index)).ToArray())
        {
            var vm = _blocks[key];
            Blocks.Remove(vm);
            _blocks.Remove(key);
            _blockOrder.Remove(key);
        }

        RebuildDisplayBlocks();
        DetectFinalAnswer(message);
    }

    private bool _finalAnswerStarted;

    /// <summary>
    /// 最终回答开始的乐观判定：工具都已结束时，出现一条"只有正文、没有工具"的助手消息，
    /// 就把它当作最终总结，立即折叠上方过程并在其上继续流式。
    ///
    /// 若之后又出现工具调用，说明这段文字只是中间总结；已折叠的组保持折叠不回溯，
    /// 避免视口跳动（与 Claude Code / DSH 的行为一致）。
    /// </summary>
    private void DetectFinalAnswer(ChatMessage message)
    {
        if (_finalAnswerStarted || _groups.Count == 0) return;
        if (_groups.Any(group => group.IsActive)) return;

        var hasText = message.Blocks.Any(block => block is TextBlock text && !string.IsNullOrWhiteSpace(text.Text));
        var hasTool = message.Blocks.Any(block => block is ToolCallBlock);
        if (!hasText || hasTool) return;

        _finalAnswerStarted = true;
        foreach (var group in _groups) group.CollapseOnTurnSettled();
        foreach (var thinking in Blocks.OfType<ThinkingBlockViewModel>()) thinking.IsLive = false;

        RefreshReferences();

        var lastText = Blocks.OfType<TextBlockViewModel>().LastOrDefault();
        foreach (var text in Blocks.OfType<TextBlockViewModel>())
        {
            text.IsFinal = ReferenceEquals(text, lastText);
            text.IsAbsorbed = !text.IsFinal;
        }

        OnPropertyChanged(nameof(HeaderStatusText));
    }

    /// <summary>兼容旧调用：一条消息即一次合并。</summary>
    public void UpdateFrom(ChatMessage message) => MergeMessage(message);

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

    /// <summary>块的全序：先按消息到达顺序，再按消息内 contentIndex。</summary>
    private long OrderOf((string MessageId, int Index) key)
    {
        var messageIndex = _messageOrder.IndexOf(key.MessageId);
        return (long)Math.Max(0, messageIndex) * 100_000 + key.Index;
    }

    private void InsertBlockInOrder((string MessageId, int Index) key, BlockViewModel vm)
    {
        var order = OrderOf(key);
        var insertAt = 0;
        for (; insertAt < _blockOrder.Count; insertAt++)
        {
            if (OrderOf(_blockOrder[insertAt]) > order) break;
        }
        _blockOrder.Insert(insertAt, key);
        Blocks.Insert(insertAt, vm);
    }

    /// <summary>把相邻工具调用折叠成组；文本/思考/错误会断开分组。</summary>
    /// <summary>
    /// 重建渲染投影。
    ///
    /// 关键是复用已有的 <see cref="ToolGroupViewModel"/> 实例：状态（展开、用户固定）
    /// 挂在组上，重建时换新实例会让用户刚展开的组在下一个工具结果到达时突然折叠。
    /// </summary>
    private void RebuildDisplayBlocks()
    {
        var next = new List<object>();
        ToolGroupViewModel? openGroup = null;
        var groupIndex = 0;

        foreach (var block in Blocks)
        {
            if (block is ToolCallBlockViewModel tool)
            {
                if (openGroup is null)
                {
                    openGroup = FindOrCreateGroup(tool, groupIndex++);
                    next.Add(openGroup);
                }
                if (!openGroup.Contains(tool)) openGroup.Add(tool);
                continue;
            }

            openGroup = null;
            next.Add(block);
        }

        // 消息快照可能替换或移除工具块；先按当前 Blocks 清理组内陈旧条目。
        var liveTools = Blocks.OfType<ToolCallBlockViewModel>().ToHashSet();
        foreach (var group in _groups)
        {
            foreach (var stale in group.Tools.Where(tool => !liveTools.Contains(tool)).ToArray())
                group.Remove(stale);
        }

        // 不再出现在投影里的组解除订阅并丢弃。
        var liveGroups = next.OfType<ToolGroupViewModel>().ToHashSet();
        foreach (var group in _groups.Where(group => !liveGroups.Contains(group)).ToArray())
        {
            group.Detach();
            _groups.Remove(group);
        }

        if (DisplayBlocks.SequenceEqual(next)) return;
        DisplayBlocks.Clear();
        foreach (var item in next) DisplayBlocks.Add(item);
        OnPropertyChanged(nameof(HeaderStatusText));
    }

    /// <summary>按稳定 GroupId 复用已有组；找不到才新建。</summary>
    private ToolGroupViewModel FindOrCreateGroup(ToolCallBlockViewModel tool, int ordinal)
    {
        var groupId = string.IsNullOrWhiteSpace(tool.ToolCallId)
            ? $"{Id}:group:{ordinal}"
            : tool.ToolCallId;

        var existing = _groups.FirstOrDefault(group => group.GroupId == groupId);
        if (existing is not null) return existing;

        var group = new ToolGroupViewModel(groupId);
        _groups.Add(group);
        return group;
    }

    /// <summary>
    /// 回合结束或从时间线移除前调用：解除全部工具组对子工具的订阅。
    /// 这是旧回合最主要的驻留来源（组通过 PropertyChanged 持有子工具，进而钉住已渲染控件）；
    /// 保留块与投影本身，回合仍可正常显示，移除时随整个条目一起释放。
    /// </summary>
    internal void DetachForRemoval()
    {
        foreach (var group in _groups) group.Detach();
        _groups.Clear();
    }

    /// <summary>
    /// 回合结束：定阶段、记耗时、收起未手动展开的工具组与思考块，
    /// 并把最后一个文本块标记为最终总结（上方过程内容弱化但不删除）。
    /// </summary>
    /// <summary>
    /// 从普通文本段提取链接与文件路径。
    /// 只扫 PlainSegment：代码块里的路径属于代码内容，不应变成可点的引用。
    /// </summary>
    private void RefreshReferences()
    {
        Links.Clear();
        Files.Clear();
        foreach (var text in Blocks.OfType<TextBlockViewModel>())
        {
            foreach (var segment in text.Segments.OfType<PlainSegmentViewModel>())
                MessageReferenceExtractor.Extract(segment.Text, Links, Files);
        }
        if (this is AssistantMessageViewModel assistant && assistant.ProjectRootForReferences is { } root)
            assistant.ResolveReferencedFiles(root);
        OnPropertyChanged(nameof(HasLinks));
        OnPropertyChanged(nameof(HasFiles));
    }

    public string? ProjectRootForReferences { get; private set; }

    public void ResolveReferencedFiles(string? projectRoot)
    {
        ProjectRootForReferences = projectRoot;
        foreach (var file in Files) file.Resolve(projectRoot);
        OnPropertyChanged(nameof(Files));
    }

    public void FinalizeTurn(SessionOutcome outcome, TimeSpan elapsed)
    {
        if (_isComplete) return;
        _isComplete = true;

        Phase = outcome switch
        {
            SessionOutcome.Completed => TurnPhase.Completed,
            SessionOutcome.Truncated => TurnPhase.Failed,
            SessionOutcome.Failed => TurnPhase.Failed,
            SessionOutcome.Interrupted => TurnPhase.Cancelled,
            _ => TurnPhase.Completed,
        };

        if (elapsed > TimeSpan.Zero) DurationMs = (long)elapsed.TotalMilliseconds;

        foreach (var group in _groups) group.CollapseOnTurnSettled();
        foreach (var thinking in Blocks.OfType<ThinkingBlockViewModel>()) thinking.IsLive = false;

        RefreshReferences();

        var lastText = Blocks.OfType<TextBlockViewModel>().LastOrDefault();
        foreach (var text in Blocks.OfType<TextBlockViewModel>())
        {
            text.IsFinal = ReferenceEquals(text, lastText);
            text.IsAbsorbed = !text.IsFinal;
        }
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
