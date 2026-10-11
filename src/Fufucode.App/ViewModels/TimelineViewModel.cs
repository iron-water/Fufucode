using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
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
public sealed partial class TimelineViewModel : ViewModelBase
{
    private readonly IChatSession _session;
    private readonly MessageRebuilder _rebuilder = new();
    private readonly IRefreshScheduler _scheduler;
    private readonly IClipboardService _clipboard;
    private readonly Dictionary<string, TimelineEntryViewModel> _byId = new();
    // "停止窗口"：用户点击停止（Cancelling）到本轮 TurnSettled 之间，Core 目前会把中止
    // 报成 Unknown("Request aborted")；窗口内把 Unknown/UserCancelled 统一改写为 UserCancelled。
    // 实测真实序列（--realpi Atria 段）：Cancelling → Running（中止收尾的瞬态）→
    // err(Unknown,"Request aborted") → Idle → TurnSettled——若以 Running 关窗会漏掉该错误，
    // 故以 TurnSettled（或连接终态）为关窗点；此后新一轮的错误不受影响。
    private bool _inStopWindow;

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

    public bool ShowBackToLatest => !IsAtBottom;

    public string BackToLatestLabel => PendingSinceScrollUp > 0
        ? $"回到最新（+{PendingSinceScrollUp}）"
        : "回到最新";

    /// <summary>请求视图滚动到底部（视图订阅后调用 ScrollViewer.ScrollToEnd）。</summary>
    public event Action? ScrollToEndRequested;

    /// <summary>新建会话：清空时间线与重建器。</summary>
    public void Reset()
    {
        Entries.Clear();
        _byId.Clear();
        _rebuilder.Reset();
        PendingSinceScrollUp = 0;
        IsAtBottom = true;
    }

    /// <summary>
    /// 启动时回放持久化历史：每条消息按"开始+终态"事件灌入同一重建管线，单次刷新。
    /// 只放终态消息，不产生流式增量（计划 §7.2：先重建状态，再接收新工作）。
    /// </summary>
    public void LoadHistory(IReadOnlyList<ChatMessage> messages)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => LoadHistory(messages));
            return;
        }

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
        }
    }

    [RelayCommand]
    private void ScrollToLatest()
    {
        IsAtBottom = true;
        PendingSinceScrollUp = 0;
        ScrollToEndRequested?.Invoke();
    }

    partial void OnIsAtBottomChanged(bool value) => OnPropertyChanged(nameof(ShowBackToLatest));

    partial void OnPendingSinceScrollUpChanged(int value)
    {
        OnPropertyChanged(nameof(ShowBackToLatest));
        OnPropertyChanged(nameof(BackToLatestLabel));
    }

    private void OnSessionEvent(object? sender, SessionEvent e)
    {
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
            if (e is SessionEvent.TurnSettled) RefreshTurnSummaries();
        }
    }

    private void ApplyBatch()
    {
        var batch = _rebuilder.Flush();
        var hasChanges = batch.AddedEntries.Count > 0 || batch.UpdatedEntryIds.Count > 0;

        foreach (var entry in batch.AddedEntries)
        {
            var vm = CreateViewModel(entry);
            if (vm is null)
            {
                continue;
            }

            vm.Timestamp = entry.Timestamp;
            _byId[entry.Id] = vm;
            Entries.Add(vm);
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
                    assistant.UpdateFrom(message);
                    break;
                case (UserMessageViewModel user, ChatMessage message, _):
                    user.UpdateFrom(message);
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
