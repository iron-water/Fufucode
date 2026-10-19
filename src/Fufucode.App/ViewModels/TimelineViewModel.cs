using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;
using Fufucode.App.Streaming;

namespace Fufucode.App.ViewModels;

/// <summary>
/// 时间线容器：订阅 <see cref="IChatSession"/> 事件，交给 <see cref="MessageRebuilder"/> 重建，
/// 再把批次增量同步到可绑定的 <see cref="Entries"/>；滚动状态（上翻不回拉 + 回到最新）在此维护。
/// </summary>
public sealed partial class TimelineViewModel : ViewModelBase, IDisposable
{
    private readonly IChatSession _session;
    private readonly MessageRebuilder _rebuilder = new();
    private readonly IRefreshScheduler _scheduler;
    private readonly IClipboardService _clipboard;
    private readonly Dictionary<string, TimelineEntryViewModel> _byId = new();
    // "停止窗口"：用户点击停止（Cancelling）到本轮 TurnSettled 之间，Core 目前会把中止
    // 报成 Unknown("Request aborted")；窗口内把 Unknown/UserCancelled 统一改写为 UserCancelled。
    // 实测真实序列（--realpi 段）：Cancelling → Running（中止收尾的瞬态）→
    // err(Unknown,"Request aborted") → Idle → TurnSettled——若以 Running 关窗会漏掉该错误，
    // 故以 TurnSettled（或连接终态）为关窗点；此后新一轮的错误不受影响。
    private bool _inStopWindow;
    // 当前回合容器：同一用户提交后的连续 assistant 消息都合并到这里。
    private AssistantMessageViewModel? _currentTurn;
    private DateTimeOffset _currentTurnStartedAt;
    // 本地乐观显示、等待后端确认的用户消息。
    private UserMessageViewModel? _pendingUser;
    private CancellationTokenSource? _submissionCts;
    private bool _disposed;

    public TimelineViewModel(IChatSession session, IRefreshScheduler scheduler, IClipboardService clipboard)
    {
        _session = session;
        _scheduler = scheduler;
        _clipboard = clipboard;
        _session.Event += OnSessionEvent;
        Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public ObservableCollection<TimelineEntryViewModel> Entries { get; } = new();

    /// <summary>时间线为空（用于对话页空态插画）。</summary>
    public bool IsEmpty => Entries.Count == 0;

    /// <summary>用户当前是否停留在时间线底部（由 View 在滚动时回写）。</summary>
    [ObservableProperty]
    private bool _isAtBottom = true;

    /// <summary>上翻期间累积的新条目数。</summary>
    [ObservableProperty]
    private int _pendingSinceScrollUp;

    /// <summary>离底超过阈值才显示"回到最新"，避免轻微滚动就弹按钮。</summary>
    [ObservableProperty] private bool _backToLatestVisible;

    public bool ShowBackToLatest => BackToLatestVisible;

    public void SetBackToLatestVisible(bool visible) => BackToLatestVisible = visible;

    public string BackToLatestLabel => PendingSinceScrollUp > 0
        ? $"回到最新（+{PendingSinceScrollUp}）"
        : "回到最新";

    /// <summary>请求视图滚动到底部（视图订阅后调用 ScrollViewer.ScrollToEnd）。</summary>
    public event Action? ScrollToEndRequested;

    /// <summary>显式的"回到最新"操作：界面可对它做平滑滚动。</summary>
    public event Action? ScrollToEndSmoothRequested;

    /// <summary>新建会话：清空时间线与重建器。</summary>
    public void Reset()
    {
        // 旧回合必须先解除工具组订阅：渲染后的 ToolGroupViewModel 被 DisplayBlocks 持有，
        // 不解除会连同其子工具与渲染控件一起驻留。
        _currentTurn?.DetachForRemoval();
        foreach (var entry in Entries)
            (entry as AssistantMessageViewModel)?.DetachForRemoval();
        Entries.Clear();
        _byId.Clear();
        _rebuilder.Reset();
        _currentTurn = null;
        _pendingUser = null;
        _submissionCts?.Cancel();
        _submissionCts = null;
        PendingSinceScrollUp = 0;
        IsAtBottom = true;
    }

    /// <summary>
    /// 启动时回放持久化历史：每条消息按"开始+终态"事件灌入同一重建管线，单次刷新。
    /// 只放终态消息，不产生流式增量（计划 §7.2：先重建状态，再接收新工作）。
    /// 先清空旧内容：同一时间线重复回放（重连/重启）不得叠加整份记录。
    /// </summary>
    public void LoadHistory(IReadOnlyList<ChatMessage> messages)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => LoadHistory(messages));
            return;
        }

        Reset();
        foreach (var message in messages)
        {
            _rebuilder.Apply(new SessionEvent.MessageStarted(message.Id, message.Role, message.Model, message.Blocks.Count));
            _rebuilder.Apply(new SessionEvent.MessageFinished(message.Id, message, message.CumulativeUsage));
        }

        ApplyBatch();
        RefreshTurnSummaries();
        ScrollToEndRequested?.Invoke();
    }

    /// <summary>
    /// 追加一条界面级提示（如恢复诊断、"审批功能接入中"）。它不是会话错误，
    /// 不走 SessionError 语义，也不进入重建器（一次性条目，无后续更新）。
    /// </summary>
    public void AddNotification(string text)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddNotification(text));
            return;
        }

        var vm = new ErrorEntryViewModel
        {
            KindText = "提示",
            Message = text,
            Severity = EntrySeverity.Info,
            Timestamp = DateTimeOffset.Now,
        };
        Entries.Add(vm);

        if (IsAtBottom)
        {
            ScrollToEndRequested?.Invoke();
        }
        else
        {
            PendingSinceScrollUp++;
        }
    }

    /// <summary>由视图在滚动位置变化时调用，决定流式期间是否自动跟随。</summary>
    public void SetAutoScrollAtBottom(bool atBottom)
    {
        IsAtBottom = atBottom;
        if (atBottom)
        {
            PendingSinceScrollUp = 0;
            BackToLatestVisible = false;
        }
    }

    [RelayCommand]
    private void ScrollToLatest()
    {
        IsAtBottom = true;
        PendingSinceScrollUp = 0;
        ScrollToEndRequested?.Invoke();
    }

    /// <summary>用户主动"回到最新"：走平滑滚动；流式自动跟随仍用瞬时滚动，避免互相打断。</summary>
    [RelayCommand]
    private void ScrollToLatestSmooth()
    {
        IsAtBottom = true;
        PendingSinceScrollUp = 0;
        BackToLatestVisible = false;
        ScrollToEndSmoothRequested?.Invoke();
    }

    partial void OnBackToLatestVisibleChanged(bool value) => OnPropertyChanged(nameof(ShowBackToLatest));

    partial void OnPendingSinceScrollUpChanged(int value)
    {
        OnPropertyChanged(nameof(ShowBackToLatest));
        OnPropertyChanged(nameof(BackToLatestLabel));
    }

    private void OnSessionEvent(object? sender, SessionEvent e)
    {
        if (_disposed) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnSessionEvent(sender, e));
            return;
        }

        switch (e)
        {
            case SessionEvent.ActivityChanged { Activity: SessionActivity.Cancelling }:
                _inStopWindow = true;
                break;
            case SessionEvent.TurnSettled:
            case SessionEvent.ConnectionChanged { Connection: SessionConnection.Faulted
                or SessionConnection.Stopped }:
                _inStopWindow = false;
                break;
        }

        // 停止窗口内的"中止"错误归一为用户取消（Core 后续改为直接发 UserCancelled 时此处即成空操作）。
        if (_inStopWindow && e is SessionEvent.SessionError error
            && error.Kind is SessionErrorKind.Unknown or SessionErrorKind.UserCancelled)
        {
            e = error with { Kind = SessionErrorKind.UserCancelled };
        }

        _rebuilder.Apply(e);
        _scheduler.Schedule(ApplyBatch);

        // 终态事件立即刷新（计划 §6.6：结束事件立即刷新）
        if (e is SessionEvent.MessageFinished or SessionEvent.SessionError or SessionEvent.TurnSettled)
        {
            ApplyBatch();
            if (e is SessionEvent.TurnSettled settled)
            {
                ConfirmPendingUser();
                FinalizeCurrentTurn(settled.Outcome);
                RefreshTurnSummaries();
            }
        }
    }

    private void ApplyBatch()
    {
        if (_disposed) return;
        var batch = _rebuilder.Flush();
        var hasChanges = batch.AddedEntries.Count > 0 || batch.UpdatedEntryIds.Count > 0;

        foreach (var entry in batch.AddedEntries)
        {
            // 用户消息是硬边界；若已有本地乐观气泡，则复用它而不是再插一条。
            if (entry.Kind == TimelineEntryKind.UserMessage)
            {
                if (TryReconcilePendingUser(entry)) continue;
                // MessageStarted 先到时还没有正文：先认领乐观气泡，等 MessageFinished 再比对文本，
                // 否则会多插一条重复的用户消息，并把回合占位提前终结成空回合。
                if (AdoptPendingUserUntilText(entry)) continue;
                FinalizeCurrentTurn(SessionOutcome.Unknown);
            }

            var vm = entry.Kind == TimelineEntryKind.AssistantMessage && entry.Message is { } message
                ? MergeIntoTurn(entry, message)
                : CreateViewModel(entry);
            if (vm is null)
            {
                continue;
            }

            vm.Timestamp = entry.Timestamp;
            _byId[entry.Id] = vm;
            if (entry.Kind != TimelineEntryKind.AssistantMessage) Entries.Add(vm);
        }

        foreach (var id in batch.UpdatedEntryIds)
        {
            if (!_byId.TryGetValue(id, out var vm))
            {
                continue;
            }

            var snapshot = _rebuilder.GetEntry(id);
            if (snapshot is null)
            {
                continue;
            }

            switch (vm, snapshot.Message, snapshot.Error)
            {
                case (AssistantMessageViewModel assistant, ChatMessage message, _):
                    assistant.MergeMessage(message);
                    break;
                case (UserMessageViewModel user, ChatMessage message, _):
                    user.UpdateFrom(message);
                    if (ReferenceEquals(user, _pendingUser)) ConfirmPendingUser();
                    break;
                case (ErrorEntryViewModel error, _, ErrorEntry errorEntry):
                    error.UpdateFrom(errorEntry);
                    break;
            }
        }

        if (!hasChanges)
        {
            return;
        }

        if (IsAtBottom)
        {
            ScrollToEndRequested?.Invoke();
        }
        else
        {
            PendingSinceScrollUp += batch.AddedEntries.Count;
        }
    }

    /// <summary>
    /// 发送开始：立即显示用户气泡与回合占位，不等待后端回执。
    /// 这样所有后端的进入动画从同一时刻开始，差异只体现在"等待提示"出现的时间。
    /// </summary>
    public void BeginSubmission(string text)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => BeginSubmission(text));
            return;
        }

        FinalizeCurrentTurn(SessionOutcome.Unknown);
        _submissionCts?.Cancel();
        _submissionCts = new CancellationTokenSource();
        var cancellation = _submissionCts.Token;

        var profile = (_session as IPresentationProfiled)?.Presentation ?? BackendPresentationProfile.Default;

        var pending = new UserMessageViewModel
        {
            Id = "pending-" + Guid.NewGuid().ToString("N"),
            Timestamp = DateTimeOffset.Now,
            Text = text,
            IsPending = true,
        };
        _pendingUser = pending;
        Entries.Add(pending);

        var turn = new AssistantMessageViewModel(_clipboard)
        {
            Id = "turn-" + Guid.NewGuid().ToString("N"),
            Timestamp = DateTimeOffset.Now,
            Presentation = profile,
            IsAwaitingFirstEvent = true,
            AwaitingText = profile.HasSlowStartup ? $"正在启动 {profile.Name}…" : "正在等待响应…",
        };
        _currentTurn = turn;
        _currentTurnStartedAt = turn.Timestamp;
        Entries.Add(turn);

        if (IsAtBottom) ScrollToEndRequested?.Invoke();

        _ = ShowAckHintAfterAsync(pending, profile, cancellation);
        _ = ShowAwaitingAfterAsync(turn, profile, cancellation);
    }

    /// <summary>后端拒绝发送：保留气泡并转失败态，移除尚未开始的回合占位。</summary>
    public void FailSubmission()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(FailSubmission);
            return;
        }

        _submissionCts?.Cancel();
        if (_pendingUser is { } pending)
        {
            pending.IsPending = false;
            pending.IsFailed = true;
            pending.ShowAckHint = false;
            _pendingUser = null;
        }

        if (_currentTurn is { IsAwaitingFirstEvent: true } turn)
        {
            turn.DetachForRemoval();
            Entries.Remove(turn);
            _currentTurn = null;
        }
    }

    private async Task ShowAckHintAfterAsync(UserMessageViewModel pending,
        BackendPresentationProfile profile, CancellationToken cancellationToken)
    {
        if (profile.AckHintDelayMs <= 0) return;
        try
        {
            await Task.Delay(profile.AckHintDelayMs, cancellationToken);
        }
        catch (OperationCanceledException) { return; }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested) return;
            if (ReferenceEquals(_pendingUser, pending) && pending.IsPending) pending.ShowAckHint = true;
        });
    }

    private async Task ShowAwaitingAfterAsync(AssistantMessageViewModel turn,
        BackendPresentationProfile profile, CancellationToken cancellationToken)
    {
        var delay = profile.HasSlowStartup ? 0 : profile.AwaitFirstEventGraceMs;
        if (delay > 0)
        {
            try { await Task.Delay(delay, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested) return;
            if (ReferenceEquals(_currentTurn, turn) && turn.IsAwaitingFirstEvent)
                turn.ShowAwaitingIndicator = true;
        });
    }

    /// <summary>
    /// 把一条 assistant 消息并入当前回合；回合不存在或已结束时新建。
    /// 这是"一个回合一个头像"的根：同一用户提交产生的多条消息不再各建条目。
    /// </summary>
    /// <summary>把后端回传的用户消息与本地乐观气泡合并，避免重复显示。</summary>
    /// <summary>
    /// 用户消息条目尚无正文时，先把后端条目 id 映射到乐观气泡，
    /// 这样它后续的增量会落在同一个气泡上，不会重复插入。
    /// </summary>
    private bool AdoptPendingUserUntilText(TimelineEntry entry)
    {
        if (_pendingUser is not { } pending || entry.Message is not { } message) return false;
        if (message.Blocks.OfType<TextBlock>().Any()) return false;
        _byId[entry.Id] = pending;
        return true;
    }

    private bool TryReconcilePendingUser(TimelineEntry entry)
    {
        if (_pendingUser is not { } pending || entry.Message is not { } message) return false;
        var text = message.Blocks.OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty;
        if (!string.Equals(pending.Text, text, StringComparison.Ordinal)) return false;

        pending.UpdateFrom(message);
        pending.IsPending = false;
        pending.IsFailed = false;
        pending.ShowAckHint = false;
        _byId[entry.Id] = pending;
        _pendingUser = null;
        _submissionCts?.Cancel();
        return true;
    }

    private AssistantMessageViewModel MergeIntoTurn(TimelineEntry entry, ChatMessage message)
    {
        if (_currentTurn is null || _currentTurn.IsComplete)
        {
            _currentTurn = new AssistantMessageViewModel(_clipboard) { Id = entry.Id };
            _currentTurnStartedAt = entry.Timestamp;
            _currentTurn.Timestamp = entry.Timestamp;
            Entries.Add(_currentTurn);
        }

        // 后端已开始产出：用户消息视为确认，等待提示停止。
        ConfirmPendingUser();
        _submissionCts?.Cancel();
        _currentTurn.MergeMessage(message);
        return _currentTurn;
    }

    private void ConfirmPendingUser()
    {
        if (_pendingUser is not { } pending) return;
        pending.IsPending = false;
        pending.IsFailed = false;
        pending.ShowAckHint = false;
        _pendingUser = null;
    }

    /// <summary>回合结束：定阶段、算耗时、收起未固定的工具组与思考块，并解除旧回合的工具组订阅。</summary>
    internal void FinalizeCurrentTurn(SessionOutcome outcome)
    {
        if (_currentTurn is not { IsComplete: false } turn) return;
        var elapsed = DateTimeOffset.Now - (_currentTurnStartedAt == default ? DateTimeOffset.Now : _currentTurnStartedAt);
        turn.FinalizeTurn(outcome, elapsed);
        _currentTurn = null;
        // 结束后本回合不再流式更新；解除工具组订阅（块与投影保留，回合照常显示）。
        turn.DetachForRemoval();
    }

    /// <summary>
    /// 时间线本身即将被丢弃（窗口、侧边聊天或会话重建）：解除会话事件订阅，
    /// 释放当前回合与全部条目的工具组订阅，避免旧会话继续驱动已废弃的 VM。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Event -= OnSessionEvent;
        _currentTurn = null;
        foreach (var entry in Entries)
        {
            (entry as AssistantMessageViewModel)?.DetachForRemoval();
        }
        Entries.Clear();
        _byId.Clear();
    }

    private TimelineEntryViewModel? CreateViewModel(TimelineEntry entry)
    {
        switch (entry.Kind, entry.Message, entry.Error)
        {
            case (TimelineEntryKind.UserMessage, ChatMessage message, _):
                var user = new UserMessageViewModel { Id = entry.Id };
                user.UpdateFrom(message);
                return user;
            case (TimelineEntryKind.AssistantMessage, ChatMessage message, _):
                var assistant = new AssistantMessageViewModel(_clipboard) { Id = entry.Id };
                assistant.UpdateFrom(message);
                return assistant;
            case (TimelineEntryKind.Error, _, ErrorEntry error):
                var errorVm = new ErrorEntryViewModel();
                errorVm.UpdateFrom(error);
                return errorVm;
            default:
                return null;
        }
    }
}
