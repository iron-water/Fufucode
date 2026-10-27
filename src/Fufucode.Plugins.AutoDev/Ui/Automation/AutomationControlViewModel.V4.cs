using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.Core.Automation;

namespace Fufucode.App.Features.Automation;

/// <summary>工作台二级页签。总览为默认入口。</summary>
public enum AutomationWorkbenchTab { Overview, Threads, Deliveries, Activity }

/// <summary>执行线程页的视角。列表为默认。</summary>
public enum AutomationThreadViewMode { List, DependencyGraph }

public sealed record AutomationV4ThreadRow(
    string ThreadId, string Title, string Backend, string Model, string Effort, string Stage,
    string Worktree, string BaseSha, string HeadSha, string LastProgress, string LastEvent,
    string Reason, string NextAction, string Duration, bool Running, bool Blocked, bool Frozen)
{
    public string Facts => $"{Backend} · {Model} · 强度 {Effort}";
    public string Tree => $"工作树 {Worktree}";
    public string Shas => $"起点 {BaseSha} · 当前 {HeadSha}";
    public string State => Running ? "正在执行 · 运行 " + Duration : Frozen ? "需对账" : Blocked ? "阻塞" : Stage;
    public string LastEventText => $"最后事件 {LastEvent} · 最后有效进展 {LastProgress}";
}

public sealed record AutomationV4ThreadEdge(string SourceThreadId, string TargetThreadId, string Label, bool RequiresIntegration);

public sealed record AutomationV4DeliveryPartRow(
    string PartId, string TaskId, string Title, long Revision, string RunId, string HeadSha,
    string DeliveryState, string ReviewConclusion, string Integration, string Verification,
    string Wiring, bool Settled, bool Accepted, bool Integrated, bool EvidencePresent, bool IsCurrentRevision)
{
    public string Bindings => $"交付版本 r{Revision} · 运行 {RunId} · 提交 {HeadSha}" +
        (IsCurrentRevision ? "" : " · 旧版本（不可用旧证据批准新版本）");
    public string Flow => $"{DeliveryState} → {ReviewConclusion} → {Integration}";
}

public sealed record AutomationV4MessageRow(
    string MessageId, string Correlation, string Kind, string Body, string DeliveryState, string Timestamp)
{
    public string Header => string.IsNullOrWhiteSpace(Correlation) ? Kind : Kind + " · " + Correlation;
}

public sealed record AutomationV4AuditRow(
    int Cursor, string Category, string Title, string Detail, string Scope, string Actor, string Timestamp)
{
    public string Timeline => $"{Timestamp} · {Category}";
}

/// <summary>委托与预算抽屉的本地设置。默认值来自 v4 规范，未配置项显式标注为未知。</summary>
public sealed partial class AutomationDelegationSettings : ObservableObject
{
    [ObservableProperty] private int _concurrency = AutomationV4Projection.DefaultConcurrency;
    [ObservableProperty] private bool _autoReview = true;
    [ObservableProperty] private bool _autoIntegrate = true;
    [ObservableProperty] private string _implementationModel = "devin/swe-2";
    [ObservableProperty] private string _verificationModel = "devin/swe-2";
    [ObservableProperty] private bool _allowNetwork;
    [ObservableProperty] private bool _allowPaidActions;
    [ObservableProperty] private bool _periodicLocalSweep = true;
    public string ScopeText => "允许范围：当前项目仓库与受管工作树；不使用付费模型不属于默认授权。";
    public string BudgetText => "预算：未配置显式上限。未配置时按次数与 token 上限兜底，不编造价格或余额；达限停止新的付费动作，在途执行按委托处理。";
    public string PermissionText => "权限：普通工具审批与开发委托分开记录；越权不会因为“自动开发”模式被放行。";
}

public sealed partial class AutomationControlViewModel
{
    private const string DelegationSettingsFile = "automation-v4-delegation.json";
    private AutomationWorkbenchTab _activeTab = AutomationWorkbenchTab.Overview;
    private AutomationThreadViewMode _threadView = AutomationThreadViewMode.List;
    private AutomationV4ThreadRow? _selectedThreadRow;
    private AutomationV4DeliveryPartRow? _selectedDeliveryRow;
    private bool _drawerOpen;
    private bool _showAllThreads;
    private string? _overrideDatabase;
    private string? _goalDraft;
    private long _snapshotUtc;
    private bool _backendReady;
    private string _backendReadiness = "尚未连接工作台，执行后端就绪状态未知。";
    private int _lastObservedInbox = -1;
    private AutomationWorkspaceMarker? _workspaceMarker;
    private string _entryPolicyText = "未连接工作台，项目入口策略未知。";
    private string _entryPolicyNext = "";
    private string _entryPolicyMode = "";
    private bool _entryReady;

    /// <summary>
    /// 入口策略：新项目默认自动开发，已有项目保留原模式/暂停/在途身份。
    /// 只在首次进入时标记；标记写入失败会被显示且不改变任何核心状态。
    /// </summary>
    public string EntryPolicyText => _entryPolicyText;
    public string EntryPolicyNext => _entryPolicyNext;
    public string EntryPolicyMode => _entryPolicyMode;
    public bool EntryPolicyReady => _entryReady;

    /// <summary>应用入口策略。连接成功后调用一次，不产生任何核心命令。</summary>
    public void ApplyEntryPolicy(string dataRoot)
    {
        if (Snapshot is not { } snapshot) { _entryPolicyText = "未连接工作台，项目入口策略未知。"; NotifyV4(); return; }
        _workspaceMarker ??= new AutomationWorkspaceMarker(dataRoot);
        var key = ProjectContext ?? DatabasePath;
        if (string.IsNullOrWhiteSpace(key)) { _entryPolicyText = "项目身份未知：不改变任何模式，也不自动默认。"; NotifyV4(); return; }
        var firstVisit = _workspaceMarker.IsFirstVisit(key);
        var hasGoal = snapshot.State.Control.Packages.Count > 0 || snapshot.Inbox.Items.Count > 0;
        var hasBackend = (snapshot.Graph?.Threads.Count ?? 0) > 0 ||
            (snapshot.RunningExecutions?.Count ?? 0) > 0 || snapshot.Inbox.Mode != DevelopmentMode.Manual;
        var policy = AutomationEntryPolicy.ForReady(hasGoal, hasBackend, firstVisit);
        _entryPolicyMode = policy.ModeText;
        _entryPolicyText = policy.Reason;
        _entryPolicyNext = policy.NextAction;
        _entryReady = policy.IsReady;
        if (policy.IsFirstVisit)
        {
            if (_workspaceMarker.TryMark(key, out var error)) _entryPolicyText += " 已记录该工作台的首次进入。";
            else _entryPolicyText += " 首次进入标记未保存（原文件保留）：" + error + "；改按已有项目处理，不静默改变模式。";
        }
        NotifyV4();
    }

    public ObservableCollection<AutomationV4ThreadRow> ThreadRows { get; } = new();
    public ObservableCollection<AutomationV4ThreadRow> MiniThreadRows { get; } = new();
    public ObservableCollection<AutomationV4ThreadEdge> ThreadEdges { get; } = new();
    public ObservableCollection<AutomationV4DeliveryPartRow> DeliveryRows { get; } = new();
    public ObservableCollection<AutomationV4MessageRow> MessageRows { get; } = new();
    public ObservableCollection<AutomationV4AuditRow> AuditRows { get; } = new();
    public AutomationDelegationSettings Delegation { get; } = new();

    /// <summary>主控对话输入草稿按项目保留：切回项目时恢复未发送文本。</summary>
    public string GoalDraft
    {
        get => _goalDraft ?? "";
        set
        {
            if (_goalDraft == value) return;
            _goalDraft = value;
            OnPropertyChanged();
        }
    }

    public AutomationWorkbenchTab ActiveTab
    {
        get => _activeTab;
        private set
        {
            if (_activeTab == value) return;
            _activeTab = value;
            OnPropertyChanged();
            NotifyV4();
        }
    }

    public AutomationThreadViewMode ThreadView
    {
        get => _threadView;
        private set
        {
            if (_threadView == value) return;
            _threadView = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsThreadListVisible));
            OnPropertyChanged(nameof(IsThreadGraphVisible));
            OnPropertyChanged(nameof(ThreadViewText));
        }
    }

    public bool IsOverviewTab => ActiveTab == AutomationWorkbenchTab.Overview;
    public bool IsThreadsTab => ActiveTab == AutomationWorkbenchTab.Threads;
    public bool IsDeliveriesTab => ActiveTab == AutomationWorkbenchTab.Deliveries;
    public bool IsActivityTab => ActiveTab == AutomationWorkbenchTab.Activity;
    public bool IsThreadListVisible => IsThreadsTab && ThreadView == AutomationThreadViewMode.List;
    public bool IsThreadGraphVisible => IsThreadsTab && ThreadView == AutomationThreadViewMode.DependencyGraph;
    public string ThreadViewText => ThreadView == AutomationThreadViewMode.List ? "列表视角" : "依赖图视角";

    public AutomationV4ThreadRow? SelectedThreadRow
    {
        get => _selectedThreadRow;
        private set { if (_selectedThreadRow == value) return; _selectedThreadRow = value; OnPropertyChanged(); }
    }

    public AutomationV4DeliveryPartRow? SelectedDeliveryRow
    {
        get => _selectedDeliveryRow;
        private set { if (_selectedDeliveryRow == value) return; _selectedDeliveryRow = value; OnPropertyChanged(); }
    }

    public bool DrawerOpen
    {
        get => _drawerOpen;
        private set { if (_drawerOpen == value) return; _drawerOpen = value; OnPropertyChanged(); }
    }

    public bool ShowAllThreads
    {
        get => _showAllThreads;
        set { if (_showAllThreads == value) return; _showAllThreads = value; OnPropertyChanged(); RebuildProjection(); }
    }

    /// <summary>后台连接状态。宿主快照为唯一真源，观察失败时保留最后快照并标注时间。</summary>
    public string HostConnectionText => Connected
        ? "后台已连接 · 快照 " + AutomationV4Projection.FormatClock(_snapshotUtc)
        : "后台未连接 · 只读观察不可用";

    public string OfflineNotice => Connected && StatusText.StartsWith("刷新失败", StringComparison.Ordinal)
        ? $"后台连接已断开；当前为截至 {AutomationV4Projection.FormatClock(_snapshotUtc)} 的最后快照，执行状态未知，写操作保持禁用。"
        : "";
    public bool ShowOfflineNotice => OfflineNotice.Length > 0;
    public bool HostConnected => Connected;

    public string CurrentNextAction { get; private set; } = "尚无已连接的宿主快照；连接工作台后显示真实下一步。";
    public string CurrentProgressNote { get; private set; } = "";
    public string DecisionCountText { get; private set; } = "0";
    public string WorkingCountText { get; private set; } = "0";
    public string RecentDeliveryText { get; private set; } = "尚无成果记录。";
    public string LastProgressText { get; private set; } = AutomationV4Projection.Unknown;
    public string AcceptanceText { get; private set; } = "项目终验绑定目标版本与候选 SHA；未提供时保持未验收。";
    public string AcceptanceGapText { get; private set; } = "尚无已连接快照，验收缺口未知。";
    public string BackendReadinessText => _backendReadiness;
    public bool BackendReady => _backendReady;

    /// <summary>项目运行状态：目标/环境/暂停/工作中分别可解释，不显示“运行中”占位。</summary>
    public string ProjectStateText { get; private set; } = "未连接";
    public string ModeTextV4 => Snapshot is null ? "自动开发（未连接）"
        : Snapshot.Inbox.Mode == DevelopmentMode.Manual ? "人工接管"
        : Snapshot.Inbox.Mode == DevelopmentMode.SemiAutomatic ? "半自动（仅建议）" : "自动开发";

    public string DelegationSummary => $"并发 {Delegation.Concurrency}（默认 {AutomationV4Projection.DefaultConcurrency}） · " +
        (Delegation.AutoReview ? "自动审核" : "人工审核") + " · " + (Delegation.AutoIntegrate ? "自动集成" : "人工集成") +
        " · 实现 " + Delegation.ImplementationModel + " · 验证 " + Delegation.VerificationModel;

    public bool CanSelectTab => true;

    /// <summary>进入某个二级页签。只在总览和线程页重建视图，不产生任何核心命令。</summary>
    [RelayCommand]
    public void SelectTab(string tab)
    {
        ActiveTab = tab switch
        {
            "threads" => AutomationWorkbenchTab.Threads,
            "deliveries" => AutomationWorkbenchTab.Deliveries,
            "activity" => AutomationWorkbenchTab.Activity,
            _ => AutomationWorkbenchTab.Overview,
        };
    }

    /// <summary>列表 / 依赖图双视角切换。图保留线程节点与内部任务/部分端口。</summary>
    [RelayCommand]
    public void SetThreadView(string view) =>
        ThreadView = view == "graph" ? AutomationThreadViewMode.DependencyGraph : AutomationThreadViewMode.List;

    [RelayCommand]
    public void ToggleThreadView() =>
        ThreadView = ThreadView == AutomationThreadViewMode.List ? AutomationThreadViewMode.DependencyGraph : AutomationThreadViewMode.List;

    [RelayCommand]
    public void OpenDelegationDrawer() => DrawerOpen = true;

    [RelayCommand]
    public void CloseDelegationDrawer() => DrawerOpen = false;

    private long _goalVersion;

    /// <summary>当前目标版本；未提交过时为 0，界面据此显示“等待目标”。</summary>
    public long GoalVersion => _goalVersion;
    public string GoalVersionText => _goalVersion == 0 ? "等待目标" : "目标版本 v" + _goalVersion;

    /// <summary>
    /// 主控对话提交目标：真正写入核心，生成目标版本，并在自动模式下按当前委托激活自动推进。
    /// 不是只保存草稿：成功会递增目标版本；失败保留输入并如实显示原因，不假装提交。
    /// </summary>
    [RelayCommand]
    public async System.Threading.Tasks.Task SubmitGoalRequestAsync()
    {
        if (string.IsNullOrWhiteSpace(GoalDraft)) return;
        if (!CanRefresh()) { StatusText = "未连接工作台，目标未提交；输入保留。"; return; }
        var store = _store!;
        var statement = GoalDraft.Trim();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var goalId = ProjectContext ?? DatabasePath;
        if (string.IsNullOrWhiteSpace(goalId)) { StatusText = "项目身份未知，目标未提交；输入保留。"; return; }
        var command = new ProjectGoalCommand(Guid.NewGuid().ToString("N"), _goalVersion, "desktop-user",
            goalId, statement, ["按目标覆盖度与候选 SHA 一致证据验收"], ActivateMode: DevelopmentMode.Automatic);
        try
        {
            var receipt = await System.Threading.Tasks.Task.Run(() => store.ApplyProjectGoal(command, now));
            _goalVersion = receipt.Version;
            // 记录委托范围：自动推进必须有明确并发、预算与审核/集成授权，未登记不被当成无限权限。
            var engine = new AutoAdvancementEngine(store);
            engine.RecordDelegation(goalId, new AutomationDelegation(Delegation.Concurrency,
                Delegation.AutoReview, Delegation.AutoIntegrate, 64, "当前项目仓库与受管工作树",
                Delegation.ImplementationModel, now, "desktop-user"));
            StatusText = $"已提交目标并激活自动推进：目标版本 v{receipt.Version} · {receipt.Mode}。";
            GoalDraft = "";
            OnPropertyChanged(nameof(GoalVersion));
            OnPropertyChanged(nameof(GoalVersionText));
            await RefreshAsync();
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            StatusText = "目标未提交，输入保留：" + error.Message;
        }
    }

    public void SaveDelegationSettings()
    {
        var database = _overrideDatabase;
        if (string.IsNullOrWhiteSpace(database))
        {
            OnPropertyChanged(nameof(DelegationSummary));
            return;
        }
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(database))!, DelegationSettingsFile);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                Delegation.Concurrency, Delegation.AutoReview, Delegation.AutoIntegrate,
                Delegation.ImplementationModel, Delegation.VerificationModel,
                Delegation.AllowNetwork, Delegation.AllowPaidActions, Delegation.PeriodicLocalSweep,
            }));
            OnPropertyChanged(nameof(DelegationSummary));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            StatusText = "委托设置未保存，原文件保留：" + error.Message;
        }
    }

    public void LoadDelegationSettings(string? database)
    {
        if (string.IsNullOrWhiteSpace(database)) return;
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(database))!, DelegationSettingsFile);
            if (!File.Exists(path)) { OnPropertyChanged(nameof(DelegationSummary)); return; }
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            if (root.TryGetProperty("Concurrency", out var concurrency) && concurrency.TryGetInt32(out var value))
                Delegation.Concurrency = Math.Clamp(value, 1, 8);
            if (root.TryGetProperty("AutoReview", out var review)) Delegation.AutoReview = review.GetBoolean();
            if (root.TryGetProperty("AutoIntegrate", out var integrate)) Delegation.AutoIntegrate = integrate.GetBoolean();
            if (root.TryGetProperty("ImplementationModel", out var implementation) && implementation.GetString() is { Length: > 0 } model)
                Delegation.ImplementationModel = model;
            if (root.TryGetProperty("VerificationModel", out var verification) && verification.GetString() is { Length: > 0 } verifyModel)
                Delegation.VerificationModel = verifyModel;
            if (root.TryGetProperty("AllowNetwork", out var network)) Delegation.AllowNetwork = network.GetBoolean();
            if (root.TryGetProperty("AllowPaidActions", out var paid)) Delegation.AllowPaidActions = paid.GetBoolean();
            if (root.TryGetProperty("PeriodicLocalSweep", out var sweep)) Delegation.PeriodicLocalSweep = sweep.GetBoolean();
            OnPropertyChanged(nameof(DelegationSummary));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            StatusText = "委托设置读取失败，使用默认值：" + error.Message;
        }
    }

    /// <summary>主操作：暂停新任务 / 继续。半自动仍在核心拒绝时给出真实原因，不伪造成功。</summary>
    [RelayCommand(CanExecute = nameof(CanSetAutomaticMode))]
    public async System.Threading.Tasks.Task ResumeAutomaticAsync()
    {
        if (!CanSetAutomaticMode()) return;
        var store = _store!;
        var command = new ControlCommand(Guid.NewGuid().ToString("N"), Snapshot!.Inbox.Revision, "desktop-user",
            ControlAction.SetMode, Mode: DevelopmentMode.Automatic);
        await Submit(Prepare(command, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    private bool CanSetAutomaticMode() => CanRefresh() && Snapshot is { Inbox.Mode: not DevelopmentMode.Automatic };

    /// <summary>回到自动开发模式下的监督视图：不改核心状态，只把页面复位到总览。</summary>
    [RelayCommand]
    public void ReturnToOverview()
    {
        ActiveTab = AutomationWorkbenchTab.Overview;
        SelectedThreadRow = null;
        SelectedDeliveryRow = null;
        DrawerOpen = false;
        NotifyV4();
    }

    private void NotifyV4()
    {
        OnPropertyChanged(nameof(IsOverviewTab)); OnPropertyChanged(nameof(IsThreadsTab));
        OnPropertyChanged(nameof(IsDeliveriesTab)); OnPropertyChanged(nameof(IsActivityTab));
        OnPropertyChanged(nameof(IsThreadListVisible)); OnPropertyChanged(nameof(IsThreadGraphVisible));
        OnPropertyChanged(nameof(HostConnectionText)); OnPropertyChanged(nameof(OfflineNotice));
        OnPropertyChanged(nameof(ShowOfflineNotice)); OnPropertyChanged(nameof(DelegationSummary));
        OnPropertyChanged(nameof(CurrentNextAction)); OnPropertyChanged(nameof(CurrentProgressNote));
        OnPropertyChanged(nameof(DecisionCountText)); OnPropertyChanged(nameof(WorkingCountText));
        OnPropertyChanged(nameof(RecentDeliveryText)); OnPropertyChanged(nameof(LastProgressText));
        OnPropertyChanged(nameof(ProjectStateText)); OnPropertyChanged(nameof(ModeTextV4));
        OnPropertyChanged(nameof(AcceptanceText)); OnPropertyChanged(nameof(AcceptanceGapText));
        OnPropertyChanged(nameof(BackendReadinessText)); OnPropertyChanged(nameof(EntryPolicyText));
        OnPropertyChanged(nameof(EntryPolicyNext)); OnPropertyChanged(nameof(EntryPolicyMode)); OnPropertyChanged(nameof(EntryPolicyReady));
        OnPropertyChanged(nameof(GoalVersion)); OnPropertyChanged(nameof(GoalVersionText));
    }

    /// <summary>
    /// 纯投影重建：把当前宿主快照映射为四个真实页面对象。只读快照，不推送任何命令；
    /// 观测缺失时保留最后快照并把未知项显式标注，不用空串伪装成已知值。
    /// </summary>
    public void RebuildProjection()
    {
        _snapshotUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ThreadRows.Clear(); MiniThreadRows.Clear(); ThreadEdges.Clear();
        DeliveryRows.Clear(); MessageRows.Clear(); AuditRows.Clear();
        if (Snapshot?.Graph is not { } graph)
        {
            CurrentNextAction = "尚无已连接的宿主快照；连接工作台后显示真实下一步。";
            CurrentProgressNote = "";
            DecisionCountText = "0"; WorkingCountText = "0";
            RecentDeliveryText = "尚无成果记录。"; LastProgressText = AutomationV4Projection.Unknown;
            ProjectStateText = "未连接"; AcceptanceGapText = "尚无已连接快照，验收缺口未知。";
            AcceptanceText = "项目终验绑定目标版本与候选 SHA；未提供时保持未验收。";
            _lastObservedInbox = -1;
            _backendReady = false; _backendReadiness = "尚未连接工作台，执行后端就绪状态未知。";
            NotifyV4();
            return;
        }
        var now = _snapshotUtc;
        var running = Snapshot.RunningExecutions ?? [];
        var stops = Snapshot.RunStops ?? [];
        foreach (var thread in graph.Threads)
        {
            var execution = running.FirstOrDefault(run => run.ThreadId == thread.ThreadId);
            var stop = stops.FirstOrDefault(s => s.ThreadId == thread.ThreadId);
            var blocked = thread.Status == ThreadStatus.Frozen || thread.Tasks.Any(t => t.Status == AutomationTaskStatus.Blocked);
            var taskParts = thread.Tasks.SelectMany(t => t.Parts).ToArray();
            var head = thread.Tasks.Select(t => t.Parts.Select(p => p.HeadSha ?? "").LastOrDefault(s => s.Length > 0) ?? "")
                .LastOrDefault(s => s.Length > 0) ?? "";
            var accepted = taskParts.Count(p => p.Status == DeliveryPartStatus.Accepted);
            var pending = taskParts.Count(p => p.Status == DeliveryPartStatus.Delivered);
            var stage = thread.Running ? "工作中" : blocked ? "需对账或阻塞"
                : taskParts.Length == 0 ? "尚无成果" : pending > 0 ? "等待审核"
                : accepted == taskParts.Length ? "全部已接受" : "已交付";
            var next = thread.Running ? "执行中；交付到达后进入审核与整合"
                : blocked ? "先核查冻结或阻塞原因，再决定返工或继续"
                : pending > 0 ? "审核待处理交付后继续"
                : accepted == taskParts.Length && taskParts.Length > 0 ? "等待依赖解锁或下一步派工"
                : "等待主控规划下一步";
            var row = new AutomationV4ThreadRow(thread.ThreadId, thread.ProjectId + " · " + thread.Harness,
                thread.Harness, thread.Model, "未提供", stage, AutomationV4Projection.Unknown,
                AutomationV4Projection.FormatSha(thread.Tasks.SelectMany(t => t.Parts)
                    .Select(p => p.RunId ?? "").FirstOrDefault(s => s.Length > 0)),
                AutomationV4Projection.FormatSha(head.Length > 0 ? head : null),
                pending > 0 ? "有待审交付" : accepted > 0 ? "存在已接受成果" : AutomationV4Projection.Unknown,
                stop is null ? AutomationV4Projection.Unknown : "已请求取消 · " + stop.Status,
                thread.Status == ThreadStatus.Frozen ? "线程需对账" : blocked ? "存在阻塞任务" : "",
                next, execution is null ? AutomationV4Projection.Unknown : AutomationV4Projection.FormatDuration(now, _runStartUtc(execution)),
                thread.Running, blocked, thread.Status == ThreadStatus.Frozen);
            ThreadRows.Add(row);
        }
        foreach (var row in ThreadRows.Where(r => r.Running || r.Blocked || r.Frozen))
            MiniThreadRows.Add(row);
        if (MiniThreadRows.Count == 0)
            foreach (var row in ThreadRows.Take(2)) MiniThreadRows.Add(row);
        foreach (var edge in graph.Edges)
        {
            var source = graph.Threads.FirstOrDefault(t => t.Tasks.Any(task => task.TaskId == edge.SourceTaskId));
            var target = graph.Threads.FirstOrDefault(t => t.Tasks.Any(task => task.TaskId == edge.TargetTaskId));
            ThreadEdges.Add(new AutomationV4ThreadEdge(source?.ThreadId ?? edge.SourceTaskId,
                target?.ThreadId ?? edge.TargetTaskId, edge.SourceTaskId + " → " + edge.TargetTaskId, edge.RequiresIntegration));
        }
        foreach (var thread in graph.Threads)
            foreach (var task in thread.Tasks)
                foreach (var part in task.Parts)
                {
                    var revisions = _store?.ListRevisions(part.PartId) ?? [];
                    var current = revisions.OrderBy(r => r.RevisionNumber).LastOrDefault();
                    var decisions = _store?.ListDecisions(part.PartId) ?? [];
                    var revisionDecision = decisions.Where(d => d.RevisionNumber == part.Revision).LastOrDefault();
                    var isCurrent = current is not null && current.RevisionNumber == part.Revision;
                    var accepted = part.Status == DeliveryPartStatus.Accepted;
                    DeliveryRows.Add(new AutomationV4DeliveryPartRow(part.PartId, task.TaskId, part.Title, part.Revision,
                        part.RunId is { Length: > 0 } run ? run : AutomationV4Projection.Unknown,
                        AutomationV4Projection.FormatSha(part.HeadSha),
                        isCurrent ? "已提交" : "旧版本 · 已过期",
                        revisionDecision is null ? "尚未审核" : DescribeDecision(revisionDecision),
                        part.Integrated ? "已整合" : "未整合",
                        AutomationV4Projection.VerificationLabel(part.Integrated, current is not null),
                        part.Integrated ? "无待接线需求（已整合）" : "共享入口接线由公共写入者处理",
                        revisionDecision is not null, accepted, part.Integrated, current is not null, isCurrent));
                }
        var items = Snapshot.Inbox.Items;
        DecisionCountText = items.Count(i => i.Actionable).ToString();
        WorkingCountText = graph.Threads.Count(t => t.Running).ToString();
        foreach (var item in items)
            MessageRows.Add(new AutomationV4MessageRow(item.Id, item.ObjectId, AutomationInboxRow.KindLabel(item.Kind),
                item.Actionable ? "已保存 → 已投递 → 待处理" : "已保存 → 待投递（" + item.Actionable + "）",
                item.Actionable ? "需要你决定" : "暂不可操作", AutomationV4Projection.FormatClock(_snapshotUtc)));
        if (items.Count == 0)
            MessageRows.Add(new AutomationV4MessageRow("main-control", "当前目标", "主控对话",
                "当前没有待决事项；自动模式在委托范围内继续推进。", "只读", AutomationV4Projection.FormatClock(_snapshotUtc)));
        RecentDeliveryText = DeliveryRows.Count == 0 ? "尚无成果记录。" :
            $"已整合 {DeliveryRows.Count(d => d.Integrated)} · 已接受待整合 {DeliveryRows.Count(d => d.Accepted && !d.Integrated)} · " +
            $"待审核 {DeliveryRows.Count(d => !d.Settled)} · 验证待补 {DeliveryRows.Count(d => !d.EvidencePresent)}";
        LastProgressText = DeliveryRows.Count == 0 ? "尚无已接受成果"
            : "最近成果：" + DeliveryRows.Last().Title + " · " + DeliveryRows.Last().ReviewConclusion;
        AcceptanceGapText = DeliveryRows.Count == 0 ? "尚无成果，无法进行终验。"
            : string.Join("\n", DeliveryRows.Where(d => !d.Accepted || !d.Integrated)
                .Select(d => $"{d.Title}：{(d.Accepted ? "已接受但未整合" : "未审核")} · {d.Verification}"));
        AcceptanceText = DeliveryRows.Count > 0 && DeliveryRows.All(d => d.Accepted && d.Integrated)
            ? "已记录成果全部接受并整合；项目终验仍需目标版本与候选 SHA 的一致证据。"
            : "当前还有未完成目标，不能宣布项目完成；发布/推送是另一个获授权动作。";
        CurrentNextAction = items.Any(i => i.Actionable)
            ? $"处理 {DecisionCountText} 项需要你决定的事项，然后按当前委托继续"
            : graph.Threads.Any(t => t.Running) ? "执行线程工作中；交付到达后进入审核与整合"
            : "没有活跃运行：按目标缺口规划下一步或等待你补充要求";
        CurrentProgressNote = $"可见线程 {graph.Threads.Count} · 工作中 {WorkingCountText} · 待审成果 {DeliveryRows.Count(d => !d.Settled)}";
        ProjectStateText = Snapshot.Inbox.Mode == DevelopmentMode.Automatic
            ? graph.Threads.Any(t => t.Running) ? "开发中"
                : items.Any(i => i.Actionable) ? "需要你决定" : "等待下一步"
            : Snapshot.Inbox.Mode == DevelopmentMode.SemiAutomatic ? "半自动" : "人工接管";
        // 后端就绪只按真实观测判定：有已注册执行身份或运行中的执行才算就绪，不按模式或配置推断。
        _backendReady = graph.Threads.Count > 0 || running.Count > 0;
        _backendReadiness = _backendReady
            ? $"执行后端已就绪 · {graph.Threads.Count} 个已注册执行身份 · 运行中 {running.Count}"
            : "执行后端未就绪：没有已注册的执行身份。缺少插件或模型时不会回显假成功，也不自动切换到更贵的模型。";
        _lastObservedInbox = items.Count;
        NotifyV4();
    }

    private static string DescribeDecision(PartDecision decision) => decision.Decision switch
    {
        AutomationDecision.Accept => "已接受 · " + decision.DecidedBy,
        AutomationDecision.Rework => "已请求返工",
        AutomationDecision.Blocked => "已阻塞",
        AutomationDecision.Defer => "已暂缓",
        _ => "已记录决定",
    };

    private long? _runStartUtc(ControlRunningExecution execution) =>
        execution.SupervisorStartUtc > 0 ? execution.SupervisorStartUtc : null;

    /// <summary>线程行选择：打开既有的线程详情，不产生命令。</summary>
    [RelayCommand]
    public void SelectThreadRow(AutomationV4ThreadRow? row)
    {
        if (row is null) return;
        SelectedThreadRow = row; SelectedDeliveryRow = null;
        SelectThread(row.ThreadId);
    }

    /// <summary>成果行选择：打开既有成果详情并同步可能的审核待办。</summary>
    [RelayCommand]
    public void SelectDeliveryRow(AutomationV4DeliveryPartRow? row)
    {
        if (row is null) return;
        SelectedDeliveryRow = row; SelectedThreadRow = null;
        SelectPart(row.PartId);
    }

    /// <summary>主控对话里的待决事项：切到总览的决定区，不自动做决定。</summary>
    [RelayCommand]
    public void FocusDecision(AutomationV4MessageRow? row)
    {
        if (row is null) return;
        SelectedDecision = Inbox.FirstOrDefault(item => item.Item.Id == row.MessageId);
    }

    /// <summary>视图重建后同步选择面板，供宿主页面在画布重绘时调用。</summary>
    public void RefreshSelectionPanels()
    {
        if (SelectedThreadRow is { } thread && !Snapshot?.Graph?.Threads.Any(t => t.ThreadId == thread.ThreadId) == true)
            SelectedThreadRow = null;
        if (SelectedDeliveryRow is { } part && DeliveryRows.All(d => d.PartId != part.PartId))
            SelectedDeliveryRow = null;
    }
}
