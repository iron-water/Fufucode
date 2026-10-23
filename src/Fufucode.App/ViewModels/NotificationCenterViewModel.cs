using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;
using Fufucode.App.Integration;
using Fufucode.Core;

namespace Fufucode.App.ViewModels;

public enum AppNotificationKind { Completed, Failed, Approval }

public sealed partial class NotificationItemViewModel : ViewModelBase
{
    public NotificationItemViewModel(string key, string sessionId, Guid generation, long run,
        AppNotificationKind kind, string title, string message, string? requestId, bool proactive)
    {
        Key = key; SessionId = sessionId; Generation = generation; Run = run; Kind = kind;
        Title = title; Message = message; RequestId = requestId; IsProactive = proactive;
        RequiresAction = kind == AppNotificationKind.Approval; ReceivedAt = DateTimeOffset.Now;
    }
    public string Key { get; }
    public string SessionId { get; }
    /// <summary>工具审批对应的工具名；空表示不是可直接应答的工具审批。</summary>
    public string ToolName { get; init; } = string.Empty;
    /// <summary>系统通知上的“允许/始终允许/拒绝”只对工具审批有意义。</summary>
    public bool IsToolApproval => IsApproval && !string.IsNullOrEmpty(ToolName) && !string.IsNullOrEmpty(RequestId);
    public Guid Generation { get; }
    public long Run { get; }
    public AppNotificationKind Kind { get; }
    public string Title { get; }
    public string Message { get; }
    public string? RequestId { get; }
    public bool IsProactive { get; }
    public DateTimeOffset ReceivedAt { get; }
    public string TimeText => ReceivedAt.ToString("HH:mm");
    public bool IsFailed => Kind == AppNotificationKind.Failed;
    public bool IsCompleted => Kind == AppNotificationKind.Completed;
    public bool IsApproval => Kind == AppNotificationKind.Approval;
    public string Glyph => Kind switch { AppNotificationKind.Completed => "✓", AppNotificationKind.Failed => "!", _ => "?" };
    [ObservableProperty] private bool _isRead;
    [ObservableProperty] private bool _requiresAction;
    public string RouteLabel => RequiresAction ? "返回会话处理" : "返回所属会话";
    partial void OnRequiresActionChanged(bool value) => OnPropertyChanged(nameof(RouteLabel));
}

public enum NotificationFilter
{
    All,
    Unread,
    Actionable,
}

/// <summary>
/// Reliable TurnSettled or explicit process fault only. RPC callbacks enqueue small records without
/// waiting for UI. Identity is session + connection generation + local submission/run number.
/// This bounded in-process center does not claim OS delivery or cross-restart exactly-once.
/// </summary>
public sealed partial class NotificationCenterViewModel : ViewModelBase, IDisposable
{
    private sealed class Attachment(string sessionId, Guid generation, IChatSession session)
    {
        public string SessionId { get; } = sessionId;
        public Guid Generation { get; } = generation;
        public IChatSession Session { get; } = session;
        public long Run;
        public bool Closed;
        public bool FaultNotified;
        public SessionOutcome? SettledOutcome;
        public EventHandler<SessionEvent>? ChatHandler;
        public Action<ExtensionUiRequest>? RequestHandler;
        public Action<ExtensionUiRequest>? ClosedHandler;
    }
    private sealed record Notice(Attachment Context, string Key, long Run, AppNotificationKind Kind,
        string Title, string Message, string? RequestId, bool WasFocused, bool IsClosedRequest = false,
        string ToolName = "");
    private sealed record Envelope(Notice? Notice = null, TaskCompletionSource? Barrier = null);
    private readonly object _gate = new();
    private readonly Channel<Envelope> _events = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(64)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _consumer;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private Attachment? _attachment;
    private long _nextRun;
    private bool _windowFocused = true;
    private bool _disposed;
    private int _dropped;

    public NotificationCenterViewModel(NotificationPreferencesViewModel preferences)
    {
        Preferences = preferences;
        Items.CollectionChanged += OnItemsChanged;
        _consumer = Task.Run(ConsumeAsync);
    }
    public NotificationPreferencesViewModel Preferences { get; }
    public ObservableCollection<NotificationItemViewModel> Items { get; } = new();

    /// <summary>按当前过滤条件显示的行；Items 仍是完整记录。</summary>
    public ObservableCollection<NotificationItemViewModel> FilteredItems { get; } = new();
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private NotificationItemViewModel? _latestPrompt;
    public bool HasItems => Items.Count > 0;
    public bool HasVisibleItems => FilteredItems.Count > 0;
    public int UnreadCount => Items.Count(item => !item.IsRead);
    public int PendingActionCount => Items.Count(item => item.RequiresAction);
    /// <summary>概览卡展示的保留记录总数（包含已读，不等于当前过滤结果）。</summary>
    public int TotalCount => Items.Count;
    public string SummaryText => PendingActionCount > 0 ? $"{PendingActionCount} 项待处理" : UnreadCount > 0 ? $"{UnreadCount} 条新通知" : "通知";
    public bool HasPrompt => LatestPrompt is not null;

    private NotificationFilter _filter = NotificationFilter.All;
    public NotificationFilter Filter
    {
        get => _filter;
        private set
        {
            if (_filter == value) return;
            _filter = value;
            OnPropertyChanged(nameof(Filter));
            OnPropertyChanged(nameof(IsAllFilter));
            OnPropertyChanged(nameof(IsUnreadFilter));
            OnPropertyChanged(nameof(IsActionableFilter));
            OnPropertyChanged(nameof(FilteredEmptyText));
            RebuildFilteredItems();
        }
    }
    public bool IsAllFilter => _filter == NotificationFilter.All;
    public bool IsUnreadFilter => _filter == NotificationFilter.Unread;
    public bool IsActionableFilter => _filter == NotificationFilter.Actionable;
    public string FilteredEmptyText => _filter switch
    {
        NotificationFilter.Unread => "没有未读通知",
        NotificationFilter.Actionable => "没有失败或待处理通知",
        _ => "暂无通知",
    };

    [RelayCommand]
    private void SetFilter(string? value)
    {
        Filter = value switch
        {
            "unread" => NotificationFilter.Unread,
            "actionable" => NotificationFilter.Actionable,
            _ => NotificationFilter.All,
        };
    }
    public bool HasDroppedNotices => Volatile.Read(ref _dropped) != 0;
    public string DroppedNoticeText => HasDroppedNotices ? "部分提醒未显示，请以会话状态为准。" : string.Empty;
    public event Action<string>? SessionRequested;
    public event Action? CloseRequested;
    partial void OnLatestPromptChanged(NotificationItemViewModel? value) => OnPropertyChanged(nameof(HasPrompt));

    public void SetWindowFocused(bool focused)
    {
        bool becameFocused;
        lock (_gate) { becameFocused = focused && !_windowFocused; _windowFocused = focused; }
        if (becameFocused) LatestPrompt = null;
    }
    public void Attach(string sessionId, Guid generation, IChatSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId); ArgumentNullException.ThrowIfNull(session);
        Detach();
        var context = new Attachment(sessionId, generation, session);
        lock (_gate)
        {
            if (_disposed) return;
            if (session.Activity != SessionActivity.Idle) context.Run = ++_nextRun;
            _attachment = context;
        }
        context.ChatHandler = (_, ev) => OnSessionEvent(context, ev);
        session.Event += context.ChatHandler;
        if (session is IExtensionUiSession extensionUi)
        {
            context.RequestHandler = request => ObserveApproval(context, request);
            context.ClosedHandler = request => ObserveApprovalClosed(context, request);
            extensionUi.ExtensionUiRequestReceived += context.RequestHandler;
            extensionUi.ExtensionUiRequestClosed += context.ClosedHandler;
            foreach (var request in extensionUi.PendingExtensionUiRequests) ObserveApproval(context, request);
        }
    }
    public void Detach()
    {
        Attachment? context;
        lock (_gate) { context = _attachment; _attachment = null; }
        if (context is null) return;
        context.Session.Event -= context.ChatHandler;
        if (context.Session is IExtensionUiSession extensionUi)
        {
            extensionUi.ExtensionUiRequestReceived -= context.RequestHandler;
            extensionUi.ExtensionUiRequestClosed -= context.ClosedHandler;
        }
        void ClearExpiredActions()
        {
            foreach (var item in Items.Where(item => item.SessionId == context.SessionId && item.Generation == context.Generation)) item.RequiresAction = false;
            if (LatestPrompt is { IsApproval: true } latest && latest.SessionId == context.SessionId && latest.Generation == context.Generation)
                LatestPrompt = null;
            NotifyCounts();
        }
        if (Dispatcher.UIThread.CheckAccess()) ClearExpiredActions(); else Dispatcher.UIThread.Post(ClearExpiredActions);
    }
    /// <summary>Call before SendAsync; follow-ups belong to the continuous run until settled.</summary>
    public long BeginSubmission()
    {
        lock (_gate)
        {
            if (_attachment is not { } context) return 0;
            if (context.Session.Activity == SessionActivity.Idle || context.Run == 0 || context.Closed)
            { context.Run = ++_nextRun; context.Closed = false; context.SettledOutcome = null; }
            return context.Run;
        }
    }
    /// <summary>Rejected idle submission is not completion; an active timed-out run remains tracked.</summary>
    public void CancelSubmission(long run)
    {
        lock (_gate)
            if (_attachment is { } context && context.Run == run && context.Session.Activity == SessionActivity.Idle) context.Closed = true;
    }
    private void OnSessionEvent(Attachment context, SessionEvent ev)
    {
        Notice? notice = null;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_attachment, context)) return;
            switch (ev)
            {
                case SessionEvent.TurnSettled settled when context.Run > 0 && !context.Closed:
                    context.Closed = true;
                    context.SettledOutcome = settled.Outcome;
                    if (settled.Outcome == SessionOutcome.Completed)
                        notice = MakeNotice(context, AppNotificationKind.Completed, "任务已完成", "本轮工作已结束，可以返回会话查看结果。");
                    else if (settled.Outcome == SessionOutcome.Failed)
                        notice = MakeNotice(context, AppNotificationKind.Failed, "任务失败", "本轮未成功，请返回会话查看错误。");
                    break; // Unknown / Interrupted never become success notifications.
                case SessionEvent.ConnectionChanged { Connection: SessionConnection.Faulted }:
                case SessionEvent.SessionError { Kind: SessionErrorKind.ProcessExited or SessionErrorKind.ProtocolError }:
                    if (!context.FaultNotified)
                    {
                        context.FaultNotified = true;
                        // A failed run and its subsequent process fault share one failure notice.
                        if (context.Closed && context.SettledOutcome == SessionOutcome.Failed) break;
                        var afterSettled = context.Closed;
                        context.Closed = true;
                        notice = MakeNotice(context, AppNotificationKind.Failed, "会话连接中断", "连接发生故障；任务结果尚未确认，请返回会话处理。");
                        if (afterSettled)
                            notice = notice with { Key = $"{context.SessionId}/{context.Generation:N}/connection-fault" };
                    }
                    break;
            }
            if (notice is not null && !MarkSeen(notice.Key)) notice = null;
        }
        if (notice is not null) Enqueue(notice);
    }
    private Notice MakeNotice(Attachment context, AppNotificationKind kind, string title, string message) =>
        new(context, $"{context.SessionId}/{context.Generation:N}/{context.Run}", context.Run, kind, title, message, null, _windowFocused);
    private void ObserveApproval(Attachment context, ExtensionUiRequest request)
    {
        Notice notice;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_attachment, context) || request.Generation != context.Generation) return;
            var key = $"{context.SessionId}/{request.Generation:N}/request/{request.Id}";
            if (!MarkSeen(key)) return;
            // 工具审批单独成条：系统通知要据此给出“允许/始终允许/拒绝”三个按钮。
            var tool = request.Method == "confirm" ? ApprovalPolicy.ToolOf(request) ?? string.Empty : string.Empty;
            notice = tool.Length > 0
                ? new(context, key, context.Run, AppNotificationKind.Approval, "有一项工具待批准",
                    $"允许执行 {tool}？选择“始终允许”后，本项目的同类工具不再询问。", request.Id, _windowFocused, ToolName: tool)
                : new(context, key, context.Run, AppNotificationKind.Approval, "有一项权限或问题待处理",
                    "请返回所属会话查看并作出决定。", request.Id, _windowFocused);
        }
        Enqueue(notice);
    }
    public void ObserveApproval(ExtensionUiRequest request)
    {
        Attachment? context; lock (_gate) context = _attachment;
        if (context is not null) ObserveApproval(context, request);
    }
    private void ObserveApprovalClosed(Attachment context, ExtensionUiRequest request)
    {
        lock (_gate)
            if (_disposed || !ReferenceEquals(_attachment, context) || request.Generation != context.Generation) return;
        Enqueue(new(context, string.Empty, context.Run, AppNotificationKind.Approval, string.Empty, string.Empty, request.Id, true, true));
    }
    private bool MarkSeen(string key)
    {
        if (!_seen.Add(key)) return false;
        _seenOrder.Enqueue(key);
        while (_seenOrder.Count > 256) _seen.Remove(_seenOrder.Dequeue());
        return true;
    }
    private void Enqueue(Notice notice)
    {
        if (_events.Writer.TryWrite(new(notice))) return;
        Interlocked.Increment(ref _dropped);
        Dispatcher.UIThread.Post(() => { OnPropertyChanged(nameof(HasDroppedNotices)); OnPropertyChanged(nameof(DroppedNoticeText)); });
    }
    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var envelope in _events.Reader.ReadAllAsync(_shutdown.Token))
            {
                if (envelope.Barrier is { } barrier) { barrier.TrySetResult(); continue; }
                if (envelope.Notice is { } notice) await Dispatcher.UIThread.InvokeAsync(() => ApplyNotice(notice));
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }
    private void ApplyNotice(Notice notice)
    {
        lock (_gate) if (_disposed || !ReferenceEquals(_attachment, notice.Context)) return;
        if (notice.IsClosedRequest)
        {
            foreach (var item in Items.Where(item => item.SessionId == notice.Context.SessionId && item.Generation == notice.Context.Generation && item.RequestId == notice.RequestId)) item.RequiresAction = false;
            if (LatestPrompt?.RequestId == notice.RequestId) LatestPrompt = null;
            NotifyCounts(); return;
        }
        var proactive = notice.Kind == AppNotificationKind.Approval ? Preferences.NotifyApprovals && !notice.WasFocused
            : Preferences.CompletionPolicy == CompletionNotificationPolicy.Always || Preferences.CompletionPolicy == CompletionNotificationPolicy.Unfocused && !notice.WasFocused;
        var row = new NotificationItemViewModel(notice.Key, notice.Context.SessionId, notice.Context.Generation, notice.Run,
            notice.Kind, notice.Title, notice.Message, notice.RequestId, proactive) { ToolName = notice.ToolName };
        Items.Insert(0, row); while (Items.Count > 128) Items.RemoveAt(Items.Count - 1);
        if (proactive) LatestPrompt = row;
        NotifyCounts();
    }
    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (NotificationItemViewModel item in e.OldItems) item.PropertyChanged -= OnItemPropertyChanged;
        if (e.NewItems is not null)
            foreach (NotificationItemViewModel item in e.NewItems) item.PropertyChanged += OnItemPropertyChanged;
        NotifyCounts();
        RebuildFilteredItems();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(NotificationItemViewModel.IsRead) or nameof(NotificationItemViewModel.RequiresAction))) return;
        NotifyCounts();
        RebuildFilteredItems();
    }

    private void RebuildFilteredItems()
    {
        FilteredItems.Clear();
        foreach (var item in Items)
        {
            var visible = _filter switch
            {
                NotificationFilter.Unread => !item.IsRead,
                NotificationFilter.Actionable => item.RequiresAction || item.IsFailed,
                _ => true,
            };
            if (visible) FilteredItems.Add(item);
        }
        OnPropertyChanged(nameof(HasVisibleItems));
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(HasItems)); OnPropertyChanged(nameof(UnreadCount));
        OnPropertyChanged(nameof(PendingActionCount)); OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(SummaryText));
    }
    public void Open() => IsOpen = true;
    [RelayCommand] private void Toggle() => IsOpen = !IsOpen;
    [RelayCommand] private void Close() { IsOpen = false; CloseRequested?.Invoke(); }
    [RelayCommand] private void DismissPrompt() => LatestPrompt = null;
    [RelayCommand]
    private void Activate(NotificationItemViewModel? item)
    {
        if (item is null) return;
        item.IsRead = true; LatestPrompt = null; Close(); NotifyCounts(); SessionRequested?.Invoke(item.SessionId);
    }
    [RelayCommand]
    private void ClearCompleted()
    {
        for (var index = Items.Count - 1; index >= 0; index--) if (!Items[index].RequiresAction) Items.RemoveAt(index);
        LatestPrompt = null; NotifyCounts();
    }
    internal async Task FlushAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _events.Writer.WriteAsync(new(Barrier: completion), _shutdown.Token); await completion.Task;
    }
    public void Dispose()
    {
        if (_disposed) return;
        Detach(); lock (_gate) _disposed = true;
        _events.Writer.TryComplete(); _shutdown.Cancel();
        _ = _consumer.ContinueWith(_ => _shutdown.Dispose(), TaskScheduler.Default);
    }
}
