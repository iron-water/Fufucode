using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Abstractions;
using Fufucode.App.Mock;

namespace Fufucode.App.ViewModels;

/// <summary>侧栏的会话条目（对标 Codex Desktop 线程列表元信息）。真实模式下 Id 来自桌面元数据；IsCurrent 标记当前会话。</summary>
public sealed class SessionItemViewModel : ObservableObject
{
    public SessionItemViewModel(
        string title,
        string subtitle,
        string? id = null,
        bool isCurrent = false,
        bool isInterrupted = false,
        string? modelName = null,
        string? tokenSummary = null,
        DateTimeOffset? lastOpened = null)
    {
        Title = title;
        Subtitle = subtitle;
        Id = id;
        IsCurrent = isCurrent;
        IsInterrupted = isInterrupted;
        ModelName = modelName;
        TokenSummary = tokenSummary;
        LastOpened = lastOpened;
    }

    public string Title { get; }

    public string Subtitle { get; }

    public string? Id { get; }

    public bool IsCurrent { get; }

    /// <summary>是否在上次运行中被中断（计划 §7.1，对应 WasActive=true）。</summary>
    public bool IsInterrupted { get; }

    /// <summary>模型名称摘要（如已知）。</summary>
    public string? ModelName { get; }

    /// <summary>Token 或用量摘要。</summary>
    public string? TokenSummary { get; }

    public DateTimeOffset? LastOpened { get; }

    public bool HasModel => !string.IsNullOrEmpty(ModelName);

    public bool HasTokens => !string.IsNullOrEmpty(TokenSummary);

    private bool _isRunning;
    private bool _isFailed;
    private bool _isUnread;
    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }
    public bool IsFailed { get => _isFailed; private set => SetProperty(ref _isFailed, value); }
    public bool IsUnread { get => _isUnread; private set => SetProperty(ref _isUnread, value); }
    public bool HasId => !string.IsNullOrEmpty(Id);
    public string StatusDescription => IsRunning ? "正在运行" : IsFailed ? "上次任务失败" :
        IsUnread ? "有未读结果" : IsInterrupted ? "上次运行中断" : "会话";

    internal void UpdateLiveState(SessionActivity activity, SessionOutcome? outcome, SessionConnection connection)
    {
        IsRunning = activity is SessionActivity.Running or SessionActivity.Compacting or
            SessionActivity.Retrying or SessionActivity.Cancelling;
        IsFailed = !IsRunning && (outcome == SessionOutcome.Failed || connection == SessionConnection.Faulted);
        OnPropertyChanged(nameof(StatusDescription));
    }

    internal void MarkUnread(bool unread)
    {
        IsUnread = unread;
        OnPropertyChanged(nameof(StatusDescription));
    }

    /// <summary>会话管理页对某一条会话的操作。由所属 SidebarViewModel 注入。</summary>
    public ICommand? RenameCommand { get; internal set; }
    public ICommand? TogglePinCommand { get; internal set; }
    public ICommand? ArchiveCommand { get; internal set; }
    private bool _isPinned;
    public bool IsPinned { get => _isPinned; internal set => SetProperty(ref _isPinned, value); }

    /// <summary>相对时间或格式化日期（对标 Codex 线程列表时间展示）。</summary>
    public string TimeText
    {
        get
        {
            if (LastOpened is null) return string.Empty;
            var dt = LastOpened.Value;
            var elapsed = DateTimeOffset.UtcNow - dt;
            if (elapsed.TotalMinutes < 1) return "刚刚";
            if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes} 分钟前";
            if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours} 小时前";
            if (elapsed.TotalDays < 2) return "昨天";
            return dt.LocalDateTime.ToString("MM-dd");
        }
    }
}

/// <summary>
/// 项目/会话侧栏（M4）。mock 模式：新建=清空时间线、长会话演示；
/// 真实模式：项目内会话列表（桌面元数据）、选中切换、新建、当前会话重命名、项目路径显示。
/// 切换/新建/重命名经回调交给组合根（涉及 Pi 进程更换，不在本 VM 内完成）。
/// </summary>
public sealed partial class SidebarViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// 真实模式的组合根回调。ProjectText/CurrentName/CurrentSessionId 为装配时快照；
    /// SwitchProjectRequested/OpenProjectRequested/RecentProjects 承载 M3.1 运行中项目切换。
    /// </summary>
    public sealed record Callbacks(
        Func<string, Task> SwitchRequested,
        Func<Task> NewSessionRequested,
        Func<string, Task> RenameRequested,
        string ProjectText,
        string CurrentName,
        string CurrentSessionId,
        Func<string, Task>? SwitchProjectRequested = null,
        Func<Task>? OpenProjectRequested = null,
        IReadOnlyList<string>? RecentProjects = null,
        IReadOnlyList<NewSessionBackendOption>? NewSessionBackends = null,
        Func<string, Task>? NewSessionWithBackendRequested = null);

    /// <summary>新建会话时可选的后端；不可用的条目在菜单中禁用。</summary>
    public sealed record NewSessionBackendOption(string Key, string Label, bool Available);

    /// <summary>新建会话可选后端（默认后端始终在首位）。mock 模式为空。</summary>
    public IReadOnlyList<NewSessionBackendOption> NewSessionBackends { get; }

    private readonly IChatSession _session;
    private readonly TimelineViewModel _timeline;
    private readonly Callbacks? _callbacks;
    private SessionItemViewModel? _selectedSession;
    private string _currentName;
    private string _renameDraft = string.Empty;
    private bool _isRenaming;
    private bool _suppressSelection;
    private bool _operationPending;
    private bool _conversationVisible = true;
    private bool _disposed;
    private SessionItemViewModel? _liveSessionItem;

    public SidebarViewModel(IChatSession session, TimelineViewModel timeline,
        IReadOnlyList<SessionItemViewModel>? sessions = null, Callbacks? callbacks = null)
    {
        _session = session;
        _timeline = timeline;
        _callbacks = callbacks;

        Sessions = new ObservableCollection<SessionItemViewModel>(sessions ?? MockDemoSessions());
        NewSessionBackends = callbacks?.NewSessionBackends ?? [];
        ProjectText = callbacks?.ProjectText ?? string.Empty;
        _currentName = callbacks?.CurrentName ?? string.Empty;

        // 最近项目 Flyout：仅列磁盘上仍存在且非当前的项目（最多 5 个，最近在前）。
        RecentProjectPaths = (callbacks?.RecentProjects ?? (IReadOnlyList<string>)[])
            .Where(System.IO.Directory.Exists)
            .Where(path => callbacks is null ||
                           !Integration.RealSessionHost.PathEquals(path, callbacks.ProjectText))
            .Take(5)
            .ToArray();

        var current = callbacks is null
            ? Sessions.FirstOrDefault()
            : Sessions.FirstOrDefault(item => item.Id == callbacks.CurrentSessionId) ?? Sessions.FirstOrDefault();
        _suppressSelection = true;
        SelectedSession = current;
        _suppressSelection = false;
        _liveSessionItem = SelectedSession;
        RefreshLiveState();
        _session.Event += OnSessionEvent;
    }

    public ObservableCollection<SessionItemViewModel> Sessions { get; }

    /// <summary>mock 演示的三条会话：用于提出界面意见时对照不同状态。</summary>
    private static IReadOnlyList<SessionItemViewModel> MockDemoSessions() =>
    [
        new SessionItemViewModel("重构前端布局", "mock · 进行中", "demo-1", isCurrent: true, modelName: "演示模型", tokenSummary: "1.2k tokens", lastOpened: DateTimeOffset.UtcNow.AddMinutes(-3)),
        new SessionItemViewModel("修复终端转义输出", "mock · 上次任务失败", "demo-2", isInterrupted: true, modelName: "演示模型", tokenSummary: "860 tokens", lastOpened: DateTimeOffset.UtcNow.AddHours(-5)),
        new SessionItemViewModel("整理设置页分类", "mock · 有未读结果", "demo-3", modelName: "演示模型", tokenSummary: "430 tokens", lastOpened: DateTimeOffset.UtcNow.AddDays(-1)),
    ];

    private ISessionManagement? _management;
    /// <summary>组合根注入的会话管理能力（重命名、置顶、归档）。未注入时页面只显示列表。</summary>
    public void AttachSessionManagement(ISessionManagement management)
    { _management = management; RefreshSessionCommands(); }
    private void RefreshSessionCommands()
    {
        foreach (var item in Sessions)
        {
            item.RenameCommand = _management?.Rename;
            item.TogglePinCommand = _management?.TogglePin;
            item.ArchiveCommand = _management?.Archive;
            item.IsPinned = _management?.IsPinned(item.Id) == true;
        }
    }

    /// <summary>mock 专属操作（长会话演示）只在 mock 会话下可见。</summary>
    public bool IsMock => _session is MockChatSession;

    public bool IsReal => !IsMock;

    public void SetConversationVisible(bool visible)
    {
        _conversationVisible = visible;
        if (visible) _liveSessionItem?.MarkUnread(false);
    }

    private void RefreshLiveState() => _liveSessionItem?.UpdateLiveState(
        _session.Activity, _session.LastOutcome, _session.Connection);

    private void OnSessionEvent(object? sender, SessionEvent e)
    {
        if (_disposed) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnSessionEvent(sender, e));
            return;
        }
        if (e is SessionEvent.ActivityChanged or SessionEvent.ConnectionChanged or
            SessionEvent.TurnSettled or SessionEvent.SessionError)
        {
            RefreshLiveState();
            if (e is SessionEvent.TurnSettled && !_conversationVisible)
                _liveSessionItem?.MarkUnread(true);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _session.Event -= OnSessionEvent;
    }

    /// <summary>当前项目路径（真实模式显示，完整路径放 Tooltip）。</summary>
    public string ProjectText { get; }

    /// <summary>项目路径末段（页头项目名）。</summary>
    public string ProjectName
    {
        get
        {
            var trimmed = ProjectText.TrimEnd('\\', '/');
            var index = trimmed.LastIndexOfAny(new[] { '\\', '/' });
            return index >= 0 ? trimmed[(index + 1)..] : trimmed;
        }
    }

    public bool HasProject => !string.IsNullOrWhiteSpace(ProjectText);

    /// <summary>最近项目（存在、非当前、最多 5）；项目行 Flyout 的列表源。</summary>
    public IReadOnlyList<string> RecentProjectPaths { get; }

    public bool HasRecentProjects => RecentProjectPaths.Count > 0;

    /// <summary>项目行可点开 Flyout 的前提：组合根提供了切换回调（真实模式）。</summary>
    public bool CanSwitchProject => _callbacks?.SwitchProjectRequested is not null;

    /// <summary>切换到指定的最近项目（真实模式；守卫与失败提示在组合根）。</summary>
    [RelayCommand]
    private async Task SwitchProjectAsync(string? path)
    {
        if (path is null || _callbacks?.SwitchProjectRequested is null)
        {
            return;
        }

        await RunSwitchAsync(() => _callbacks.SwitchProjectRequested(path));
    }

    /// <summary>“打开其他文件夹…”（组合根弹文件夹选择器后走同一切换路径）。</summary>
    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        if (_callbacks?.OpenProjectRequested is null)
        {
            return;
        }

        await RunSwitchAsync(() => _callbacks.OpenProjectRequested());
    }

    /// <summary>页头内联重命名：编辑中的草稿名。</summary>
    public string RenameDraft
    {
        get => _renameDraft;
        set => SetProperty(ref _renameDraft, value);
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        private set => SetProperty(ref _isRenaming, value);
    }

    /// <summary>进入内联重命名（页头编辑图标）。</summary>
    [RelayCommand]
    private void BeginRename()
    {
        RenameDraft = CurrentName;
        IsRenaming = true;
    }

    /// <summary>取消内联重命名（Esc）。</summary>
    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    /// <summary>当前会话名称（编辑框绑定；保存经 RenameRequested）。</summary>
    public string CurrentName
    {
        get => _currentName;
        set
        {
            if (SetProperty(ref _currentName, value))
            {
                SaveNameCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public SessionItemViewModel? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (!SetProperty(ref _selectedSession, value))
            {
                return;
            }

            if (!_suppressSelection && !_operationPending && _callbacks is not null &&
                value?.Id is { } id && id != _callbacks.CurrentSessionId)
            {
                _ = RunSwitchAsync(() => _callbacks.SwitchRequested(id));
            }
        }
    }

    /// <summary>新建会话（mock：清空当前时间线，不落盘；真实：经组合根新建并切换）。</summary>
    [RelayCommand]
    private void NewSession()
    {
        if (_callbacks is null)
        {
            _timeline.Reset();
            return;
        }

        _ = RunSwitchAsync(() => _callbacks.NewSessionRequested());
    }

    /// <summary>按指定后端新建会话（会话管理页“新建会话”右侧菜单）。</summary>
    [RelayCommand]
    private async Task NewSessionWithBackendAsync(string? backendKey)
    {
        if (_callbacks?.NewSessionWithBackendRequested is null || string.IsNullOrWhiteSpace(backendKey))
        {
            return;
        }

        await RunSwitchAsync(() => _callbacks.NewSessionWithBackendRequested(backendKey));
    }

    /// <summary>保存当前会话名称（真实模式；经 Pi set_session_name 落盘）。</summary>
    [RelayCommand(CanExecute = nameof(CanSaveName))]
    private async Task SaveNameAsync()
    {
        if (_callbacks is null)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(RenameDraft) ? CurrentName : RenameDraft;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        IsRenaming = false;
        await _callbacks.RenameRequested(name.Trim());
    }

    private bool CanSaveName() => IsReal && !_operationPending && !string.IsNullOrWhiteSpace(CurrentName);

    private async Task RunSwitchAsync(Func<Task> operation)
    {
        if (_operationPending)
        {
            return;
        }

        _operationPending = true;
        SaveNameCommand.NotifyCanExecuteChanged();
        try
        {
            await operation();
        }
        finally
        {
            _operationPending = false;
            SaveNameCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>会话集合或当前名称变化后刷新（组合根在切换/重命名完成后调用）。</summary>
    public void UpdateSessions(IReadOnlyList<SessionItemViewModel> items, string currentId, string currentName)
    {
        _suppressSelection = true;
        Sessions.Clear();
        foreach (var item in items)
        {
            item.RenameCommand = _management?.Rename;
            item.TogglePinCommand = _management?.TogglePin;
            item.ArchiveCommand = _management?.Archive;
            item.IsPinned = _management?.IsPinned(item.Id) == true;
            Sessions.Add(item);
        }

        SelectedSession = Sessions.FirstOrDefault(item => item.Id == currentId) ?? Sessions.FirstOrDefault();
        _liveSessionItem = SelectedSession;
        RefreshLiveState();
        CurrentName = currentName;
        _suppressSelection = false;
    }

    /// <summary>生成长会话演示（用于 ≥500 条的滚动与输入卡顿自测，仅 mock）。</summary>
    [RelayCommand]
    private async Task GenerateLongConversationAsync()
    {
        if (_session is MockChatSession mock)
        {
            await mock.GenerateLongConversationAsync(500);
        }
    }
}

/// <summary>会话管理页对单条会话可执行的动作。由主窗口按当前会话实现。</summary>
public interface ISessionManagement
{
    ICommand? Rename { get; }
    ICommand? TogglePin { get; }
    ICommand? Archive { get; }
    bool IsPinned(string? sessionId);
}
