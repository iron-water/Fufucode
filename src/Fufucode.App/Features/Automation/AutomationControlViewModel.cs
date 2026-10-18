using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.ViewModels;
using Fufucode.Core.Automation;

namespace Fufucode.App.Features.Automation;

public sealed record AutomationInboxRow(ControlDecisionItem Item)
{
    public string Title => KindLabel(Item.Kind) + " · " + Item.ObjectId;
    public string Availability => Item.BlockedReason is { } blocked ? "操作限制 · " + (blocked switch
        { "writer_not_settled" or "writer_or_contract_not_ready" => "执行者或规范尚未就绪", "manual_mode" => "返工执行需先选择半自动", "contract_not_approved" => "当前规范未批准", "thread_frozen" => "线程需对账",
          "obsolete_fact_version" => "知识版本已更新，只能拒绝旧提案", "obsolete_contract" => "规范或依据已变更", "no_eligible_accepted_candidate" => "尚无符合基准的已接受候选", "already_integrated_group" => "组内已有成果集成", _ => "需要先完成前置核查" })
        : Item.Actionable ? "等待决定" : "暂不可操作";
    public static string KindLabel(string kind) => kind switch
    { "plan" => "计划批准", "review" => "成果审核", "repair" => "返工批准", "reconsider" => "重新审核", "integration" => "集成批准", "authorization" => "集成授权",
      "knowledge" => "知识提案", "question" => "定向问答", "adoption" => "知识采用", "choice" => "竞争选择", "restructure" => "任务重组", _ => "主控待办" };
}

public sealed partial class AutomationControlViewModel : ViewModelBase, IDisposable
{
    private AutomationStore? _store;
    private long _epoch;
    private bool _disposed;
    private NativeControlAttempt? _lastAttempt;
    private string? _attemptDatabase;
    private (string Path, string? Context)? _pendingConnection;
    private readonly System.Threading.SemaphoreSlim _refreshGate = new(1, 1);
    private bool _refreshInProgress;
    public event Action? GraphChanged;
    public event Action<string, string?>? DatabaseConnected;
    public string? ProjectContext { get; private set; }
    public ControlClientSnapshot? Snapshot { get; private set; }
    public ObservableCollection<AutomationInboxRow> Inbox { get; } = new();
    [ObservableProperty] private string _databasePath = "";
    [ObservableProperty] private string _statusText = "连接已有工作台，查看真实线程、交付与待办。";
    [ObservableProperty] private string _summaryText = "尚未连接";
    [ObservableProperty] private string _modeText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _runningOnly;
    [ObservableProperty] private bool _expandTasks = true;
    [ObservableProperty] private AutomationInboxRow? _selectedDecision;
    [ObservableProperty] private string _detailText = "选择线程、任务或成果查看详情。";
    [ObservableProperty] private string _reasonText = "";
    [ObservableProperty] private string _evidenceText = "";
    [ObservableProperty] private ControlRunningExecution? _selectedExecution;
    public bool HasSelectedExecution => SelectedExecution is not null;
    public string ExecutionText => SelectedExecution is not { } run ? "" : $"运行 {run.RunId}\n线程 {run.ThreadId} · 任务 {run.TaskId}\n宿主 {run.HostId}\n" +
        (run.StopStatus == "requested" ? "取消请求已记录，等待原宿主处理。" : "取消会中断本次执行；其输出与工作树仍需对账，不能直接接受或自动重跑。");

    public bool Connected => _store is not null;
    public bool HasDetails => SelectedDecision is not null || DetailText != "选择线程、任务或成果查看详情。";
    public void SetProjectContext(string? context)
    {
        if (ProjectContext == context) return;
        // 每个项目保留自己的主控输入草稿；切回时恢复未发送文本。
        if (ProjectContext is not null) _goalDraftByProject[ProjectContext] = GoalDraft;
        ProjectContext = context; Disconnect();
        GoalDraft = context is not null && _goalDraftByProject.TryGetValue(context, out var draft) ? draft : "";
    }
    private readonly System.Collections.Generic.Dictionary<string, string> _goalDraftByProject = new(StringComparer.OrdinalIgnoreCase);
    public void Disconnect()
    {
        ClearPersistence();
        CloseCreation();
        ++_epoch; _pendingConnection = null; _store?.Dispose(); _store = null; Snapshot = null; DatabasePath = "";
        Inbox.Clear(); SelectedDecision = null; _lastAttempt = null; _attemptDatabase = null;
        SummaryText = "尚未连接"; ModeText = ""; ReturnToGraph(); NotifyCommands(); GraphChanged?.Invoke();
        RebuildProjection();
    }
    public async Task ConnectAsync(string path)
    {
        if (_disposed) return;
        if (IsBusy) { _pendingConnection = (path, ProjectContext); return; }
        if (_refreshInProgress)
        {
            _pendingConnection = (path, ProjectContext);
            // A connection change is a user operation, so it may disable actions. Await
            // the old read before draining the latest requested project, rather than
            // returning a completed connection task while that request is still queued.
            IsBusy = true;
            try { await _refreshGate.WaitAsync(); _refreshGate.Release(); }
            finally { if (!_disposed) IsBusy = false; }
            await DrainPendingConnectionAsync();
            return;
        }
        var epoch = ++_epoch; var context = ProjectContext; IsBusy = true;
        AutomationStore? candidate = null;
        try
        {
            var result = await Task.Run(() =>
            {
                candidate = AutomationStore.AttachControlClient(path);
                return candidate.ReadControlClientSnapshot(includeGraph: true);
            });
            if (_disposed || epoch != _epoch) { candidate?.Dispose(); return; }
            _store?.Dispose(); _store = candidate; DatabasePath = Path.GetFullPath(path);
            _lastAttempt = null; _attemptDatabase = null; SelectedDecision = null;
            _overrideDatabase = DatabasePath;
            CloseCreation();
            SelectCancelledRuns(null, null);
            LoadDelegationSettings(DatabasePath);
            Apply(result); StatusText = "已连接 · 没有创建或启动执行者。";
            ApplyEntryPolicy(Program.SelfTestDataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fufucode"));
            BindPersistence(result);
            DatabaseConnected?.Invoke(DatabasePath, context);
        }
        catch (Exception error) { candidate?.Dispose(); if (!_disposed && epoch == _epoch) StatusText = "连接失败：" + error.Message; }
        finally { if (!_disposed) { IsBusy = false; await DrainPendingConnectionAsync(); } }
    }
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        if (!CanRefresh()) return;
        await _refreshGate.WaitAsync();
        try
        {
            if (!CanRefresh()) return;
            var store = _store!; var epoch = _epoch;
            _refreshInProgress = true;
            try
            {
                var snapshot = await Task.Run(() => store.ReadControlClientSnapshot(includeGraph: true));
                // Observing the authority must not disable the focused input controls. A
                // mutation can run while this read completes; never publish its older view.
                if (!_disposed && epoch == _epoch && !IsBusy &&
                    (Snapshot is null || (snapshot.Inbox.Revision >= Snapshot.Inbox.Revision &&
                        snapshot.State.EventCursor >= Snapshot.State.EventCursor))) Apply(snapshot);
            }
            catch (Exception error) { if (!_disposed && epoch == _epoch) StatusText = "刷新失败：" + error.Message; }
        }
        finally { _refreshInProgress = false; _refreshGate.Release(); }
        await DrainPendingConnectionAsync();
    }
    private async Task DrainPendingConnectionAsync()
    {
        if (_disposed || IsBusy || _refreshInProgress || _pendingConnection is not { } pending) return;
        _pendingConnection = null;
        if (pending.Context == ProjectContext) await ConnectAsync(pending.Path);
    }
    private bool CanRefresh() => !_disposed && Connected && !IsBusy;
    private void Apply(ControlClientSnapshot snapshot)
    {
        var selected = SelectedDecision;
        var redraw = JsonSerializer.Serialize(Snapshot?.Graph) != JsonSerializer.Serialize(snapshot.Graph);
        Snapshot = snapshot;
        if (SelectedExecution is { } selection)
        {
            var current = snapshot.RunningExecutions?.SingleOrDefault(run => run.RunId == selection.RunId);
            // Do not substitute a new owner/writer token into a decision already being edited.
            if (current is null || current.EvidenceToken != selection.EvidenceToken) SelectedExecution = null;
            else if (current != selection) SelectedExecution = current;
        }
        var desired = new System.Collections.Generic.List<AutomationInboxRow>();
        foreach (var item in snapshot.Inbox.Items)
        {
            var existing = Inbox.FirstOrDefault(row => row.Item.Id == item.Id);
            var same = existing is not null && existing.Item.EvidenceToken == item.EvidenceToken && existing.Item.ContextJson == item.ContextJson &&
                existing.Item.Actionable == item.Actionable && existing.Item.BlockedReason == item.BlockedReason && existing.Item.Options.SequenceEqual(item.Options);
            desired.Add(same ? existing! : new(item));
        }
        if (selected is not null && !desired.Any(row => ReferenceEquals(row, selected)))
        {
            PreserveInvalidatedDecisionInput();
            SelectedDecision = null;
        }
        // Clearing a bound ListBox also clears its two-way selection and the user's draft.
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < Inbox.Count && ReferenceEquals(Inbox[index], desired[index])) continue;
            var previous = Inbox.IndexOf(desired[index]);
            if (previous >= 0) Inbox.Move(previous, index); else Inbox.Insert(index, desired[index]);
        }
        while (Inbox.Count > desired.Count) Inbox.RemoveAt(Inbox.Count - 1);
        if (selected is not null && SelectedDecision is null) { DetailText = "所选内容已更新，请重新选择并核对。" +
            (InvalidatedDrafts.Count > 0 ? "旧输入保留在下方，可明确保存草稿；不能提交旧决定。" : ""); OnPropertyChanged(nameof(HasDetails)); }
        var graph = snapshot.Graph!;
        SummaryText = $"{graph.Threads.Count} 个真实线程 · {graph.Threads.Sum(t => t.Tasks.Count)} 个任务 · {Inbox.Count} 项待办";
        ModeText = snapshot.Inbox.Mode switch { DevelopmentMode.Manual => "全人工", DevelopmentMode.SemiAutomatic => "半自动", _ => "自动开发" };
        // v4 投影是纯只读映射：先按最新快照重建四个页面对象，再让画布按需重绘。
        RebuildProjection();
        NotifyCommands(); if (redraw) GraphChanged?.Invoke();
    }
    public void SelectThread(string id)
    {
        var thread = Snapshot?.Graph?.Threads.SingleOrDefault(t => t.ThreadId == id); if (thread is null) return;
        SelectedDecision = null;
        SelectCancelledRuns(id, null);
        SelectedExecution = Snapshot?.RunningExecutions?.SingleOrDefault(run => run.ThreadId == id);
        DetailText = $"线程 {thread.ThreadId}\n项目 {thread.ProjectId}\n{thread.Harness} · {thread.Model}\n{thread.Tasks.Count} 个任务 · " + (thread.Running ? "正在执行" : "当前没有执行进程");
        DetailText += DescribeStops(thread.ThreadId, null);
        OnPropertyChanged(nameof(HasDetails));
    }
    public void SelectTask(string id)
    {
        var task = Snapshot?.Graph?.Threads.SelectMany(t => t.Tasks).SingleOrDefault(t => t.TaskId == id); if (task is null) return;
        SelectedDecision = null; DetailText = $"{task.Title}\n任务 {task.TaskId}\n{task.Parts.Count} 个成果部分"; OnPropertyChanged(nameof(HasDetails));
        SelectedExecution = Snapshot?.RunningExecutions?.SingleOrDefault(run => run.TaskId == id);
        SelectCancelledRuns(null, id);
        DetailText += DescribeStops(null, task.TaskId);
    }
    public void SelectPart(string id)
    {
        SelectCancelledRuns(null, null);
        var part = Snapshot?.Graph?.Threads.SelectMany(t => t.Tasks).SelectMany(t => t.Parts).SingleOrDefault(p => p.PartId == id); if (part is null) return;
        SelectedDecision = Inbox.FirstOrDefault(row => row.Item.ObjectId == id && row.Item.Kind is "review" or "repair" or "reconsider");
        if (SelectedDecision is null)
            DetailText = $"{part.Title}\n成果 {part.PartId} · 版本 {part.Revision}\n{part.Status}" + (part.Integrated ? " · 已集成" : " · 尚未集成") + $"\n运行 {part.RunId}\n提交 {part.HeadSha}";
        OnPropertyChanged(nameof(HasDetails));
    }
    [RelayCommand]
    public void ReturnToGraph() { _localDraftId = null; CloseCreation(); SelectedDecision = null; SelectedExecution = null; SelectCancelledRuns(null, null); DetailText = "选择线程、任务或成果查看详情。"; OnPropertyChanged(nameof(HasDetails)); }
    partial void OnSelectedDecisionChanged(AutomationInboxRow? value)
    {
        _localDraftId = null;
        SelectCancelledRuns(null, null);
        SelectedExecution = null;
        ReasonText = ""; EvidenceText = "";
        if (value is not null)
        {
            using var json = JsonDocument.Parse(value.Item.ContextJson);
            var root = json.RootElement; var package = Field(root, "package"); var spec = Field(package, "Spec"); var part = Field(root, "part");
            DetailText = value.Title + "\n" + Value(spec, "Goal") + "\n" + string.Join("\n", new[] { "Scope", "Acceptance" }
                .Select(name => Field(spec, name) is { ValueKind: JsonValueKind.Array } a ? string.Join(" · ", a.EnumerateArray().Select(x => x.GetString())) : ""));
            if (part.ValueKind == JsonValueKind.Object)
                DetailText += $"\n成果 {Value(part, "PartId")} · 版本 {Value(part, "Revision")}\n运行 {Value(part, "RunId")}\n提交 {Value(part, "HeadSha")}";
            if (Field(root, "parts") is { ValueKind: JsonValueKind.Array } parts)
                foreach (var entry in parts.EnumerateArray())
                    DetailText += $"\n成果 {Value(entry, "PartId")} · 版本 {Value(entry, "Revision")}\n运行 {Value(entry, "RunId")}\n提交 {Value(entry, "HeadSha")}\n核查依据 {Value(entry, "ReviewEvidenceJson")}";
            // Core has already redacted cards for active/Unknown writers; the UI also gates display.
            if (value.Item.Actionable) DetailText += "\n" + Value(part, "CardJson");
            if (!value.Item.Actionable) DetailText += "\n" + value.Availability;
        }
        ConfigureDecisionForm(value);
        OnPropertyChanged(nameof(HasDetails)); NotifyCommands();
    }
    private bool CanDecide(string? option)
    {
        if (!CanRefresh() || SelectedDecision is not { Item.Actionable: true } row || option is null || !row.Item.Options.Contains(option)) return false;
        return row.Item.Kind switch
        {
            "plan" => option == "approve", "integration" => option == "authorize",
            "review" => option is ("accept" or "rework" or "blocked" or "defer") && !string.IsNullOrWhiteSpace(EvidenceText) && (option == "accept" || !string.IsNullOrWhiteSpace(ReasonText)),
            "repair" or "reconsider" or "authorization" => !string.IsNullOrWhiteSpace(ReasonText),
            "knowledge" => option == "publish" || option == "reject" && !string.IsNullOrWhiteSpace(ReasonText),
            "question" => option == "answer" && !string.IsNullOrWhiteSpace(ResponseText),
            "adoption" => option == "adopt",
            "restructure" => option == "approve" || option == "reject" && !string.IsNullOrWhiteSpace(ReasonText),
            "choice" => option == "select" && SelectedWinner is not null && WinnerChoices.Contains(SelectedWinner), _ => false
        };
    }
    [RelayCommand(CanExecute = nameof(CanDecide))]
    public async Task DecideAsync(string option)
    {
        if (!CanDecide(option)) return;
        var store = _store!; var item = SelectedDecision!.Item; var revision = Snapshot!.Inbox.Revision;
        var id = Guid.NewGuid().ToString("N"); var reason = ReasonText; var evidence = JsonSerializer.Serialize(new { validation = EvidenceText });
        using var json = JsonDocument.Parse(item.ContextJson); var root = json.RootElement; var part = Field(root, "part");
        var taskId = Value(root, "TaskId"); var runId = Value(part, "RunId"); var head = Value(part, "HeadSha"); var decisionId = Value(part, "ReviewId");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); const string actor = "desktop-user";
        object command = item.Kind switch
        {
            "plan" => new ControlCommand(id, revision, actor, ControlAction.ApprovePackage, PackageId: item.ObjectId, ExpectedPackageVersion: item.Version),
            "review" => new ControlReviewCommand(id, revision, actor, taskId, item.ObjectId, item.Version!.Value, runId, head, item.EvidenceToken,
                option switch { "accept" => AutomationDecision.Accept, "rework" => AutomationDecision.Rework, "blocked" => AutomationDecision.Blocked, "defer" => AutomationDecision.Defer, _ => throw new ArgumentException("Unknown review choice") }, reason, evidence),
            "repair" => new ControlRepairCommand(id, revision, actor, taskId, item.ObjectId, item.Version!.Value, runId, head, decisionId, item.EvidenceToken, reason),
            "reconsider" => new ControlReconsiderCommand(id, revision, actor, taskId, item.ObjectId, item.Version!.Value, runId, head, item.EvidenceToken, decisionId, reason),
            "integration" => new ControlIntegrationCommand(id, revision, actor, ControlIntegrationAction.Authorize, item.ObjectId, item.Version!.Value, item.EvidenceToken),
            "authorization" => new ControlIntegrationCommand(id, revision, actor, ControlIntegrationAction.Revoke,
                Value(root, "PackageId"), item.Version!.Value, item.EvidenceToken, item.ObjectId, reason),
            _ => BuildAdvancedDecision(store, item, root, id, revision, actor, option, reason, now)
        };
        await Submit(Prepare(command, now), completesSelectedDecision: true);
    }
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task SetModeAsync(string mode)
    {
        if (!CanRefresh() || mode is not ("manual" or "semi" or "auto")) return;
        var store = _store!; var command = new ControlCommand(Guid.NewGuid().ToString("N"), Snapshot!.Inbox.Revision, "desktop-user", ControlAction.SetMode,
            Mode: mode switch
            {
                "manual" => DevelopmentMode.Manual,
                "auto" => DevelopmentMode.Automatic,
                _ => DevelopmentMode.SemiAutomatic,
            });
        await Submit(Prepare(command, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }
    private async Task<bool> Submit(NativeControlAttempt attempt, bool completesSelectedDecision = false)
    {
        var completedDecision = completesSelectedDecision ? SelectedDecision : null;
        var epoch = _epoch; var succeeded = false; var coreRecorded = false; IsBusy = true; _lastAttempt = attempt; _attemptDatabase = DatabasePath;
        try
        {
            var scope = _persistenceScope ?? throw new InvalidOperationException("本地请求存储未就绪，请核对工作台身份和保存错误。");
            var generation = _persistenceGeneration;
            var request = attempt.Request;
            var recorded = await Task.Run(() => _requestJournal.Record(scope, request, generation));
            var next = recorded.ValueOrThrow().Generation;
            if (!_disposed && epoch == _epoch) _persistenceGeneration = next;
            var receipt = await Task.Run(attempt.Execute);
            coreRecorded = true;
            var observed = await Task.Run(() => _requestJournal.ObserveReceipt(scope, request.CommandId,
                JsonSerializer.Serialize(receipt, receipt.GetType()), next));
            if (!_disposed && epoch == _epoch) _persistenceGeneration = observed.ValueOrThrow().Generation;
            else observed.ValueOrThrow();
            succeeded = true;
            if (!_disposed && epoch == _epoch) StatusText = "决定已记录；接受与集成、返工批准与执行分别处理。";
        }
        catch (Exception error) { if (!_disposed && epoch == _epoch) StatusText = coreRecorded
            ? "工作台已记录决定，但本地回执未保存：" + error.Message + "；请核对原回执，不要重复提交。"
            : "操作未完成：" + error.Message + "；请核对状态或重取原回执。"; }
        finally { if (!_disposed) IsBusy = false; }
        if (!_disposed && epoch == _epoch)
        {
            // Only the input belonging to this successful command is complete. A user may select
            // another pending item while the original operation awaits its local publication.
            _completedDecisionInput = succeeded ? completedDecision : null;
            try { await RefreshAsync(); }
            finally { _completedDecisionInput = null; }
        }
        if (!_disposed && epoch == _epoch) LoadLocalRecords(reloadGeneration: false);
        await DrainPendingConnectionAsync();
        return succeeded && !_disposed && epoch == _epoch;
    }
    private bool CanRetry() => CanRefresh() && _lastAttempt is not null && _attemptDatabase == DatabasePath;
    [RelayCommand(CanExecute = nameof(CanRetry))]
    public async Task RetryReceiptAsync()
    {
        if (!CanRetry()) return;
        await ObserveOriginalReceiptAsync(_lastAttempt!.Request);
    }
    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnReasonTextChanged(string value) => NotifyCommands();
    partial void OnSelectedExecutionChanged(ControlRunningExecution? value)
    { ReasonText = ""; OnPropertyChanged(nameof(HasSelectedExecution)); OnPropertyChanged(nameof(ExecutionText)); NotifyCommands(); }
    private string DescribeStops(string? threadId, string? taskId) => string.Join("", (Snapshot?.RunStops ?? [])
        .Where(stop => threadId is not null ? stop.ThreadId == threadId : stop.TaskId == taskId)
        .Select(stop => $"\n取消请求 {stop.CommandId} · 运行 {stop.RunId}\n" + (stop.Status switch
        { "requested" => Snapshot!.RunningExecutions?.Any(run => run.RunId == stop.RunId) == true ? "等待原宿主处理" : "原执行不再提供可取消身份；取消结果需对账",
            "finished_before_cancel" => "执行先行结束，未按取消认定", "reconciled_interrupted" => "人类已记录为中断；未接受输出或重新执行", _ => "取消尝试已结束；仍需对账，不是交付成功" }) + $"\n理由 {stop.Reason}"));
    private bool CanCancelExecution() => CanRefresh() && SelectedExecution is { StopCommandId: null } && !string.IsNullOrWhiteSpace(ReasonText);
    [RelayCommand(CanExecute = nameof(CanCancelExecution))]
    public async Task CancelExecutionAsync()
    {
        if (!CanCancelExecution()) return;
        var store = _store!; var execution = SelectedExecution!;
        var command = new ControlStopRunCommand(Guid.NewGuid().ToString("N"), Snapshot!.Inbox.Revision, "desktop-user",
            execution.RunId, execution.ThreadId, execution.TaskId, execution.EvidenceToken, ReasonText);
        if (await Submit(Prepare(command, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())))
            StatusText = "取消请求已持久化；等待原宿主处理，运行结果仍需对账。";
    }
    partial void OnEvidenceTextChanged(string value) => NotifyCommands();
    partial void OnRunningOnlyChanged(bool value) => GraphChanged?.Invoke();
    partial void OnExpandTasksChanged(bool value) => GraphChanged?.Invoke();
    private void NotifyCommands() { RefreshCommand.NotifyCanExecuteChanged(); DecideCommand.NotifyCanExecuteChanged(); SetModeCommand.NotifyCanExecuteChanged(); RetryReceiptCommand.NotifyCanExecuteChanged(); BeginCreationCommand.NotifyCanExecuteChanged(); CreateCommand.NotifyCanExecuteChanged(); CancelExecutionCommand.NotifyCanExecuteChanged(); InspectCancelledRunCommand.NotifyCanExecuteChanged(); ResolveCancelledRunCommand.NotifyCanExecuteChanged(); NotifyPersistenceCommands(); }
    private static JsonElement Field(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        ? value.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value : default;
    private static string Value(JsonElement value, string name) { var field = Field(value, name); return field.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : field.ToString(); }
    public void Dispose() { if (_disposed) return; _disposed = true; Disconnect(); }
}
