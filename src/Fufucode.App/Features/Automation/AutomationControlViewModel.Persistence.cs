using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Features.Automation.Persistence;
using Fufucode.Core.Automation;

namespace Fufucode.App.Features.Automation;

public sealed partial class AutomationControlViewModel
{
    private readonly AutomationDraftStore _draftStore;
    private readonly AutomationRequestJournal _requestJournal;
    private AutomationPersistenceScope? _persistenceScope;
    private long _persistenceGeneration;
    private string? _localDraftId;
    public ObservableCollection<AutomationDraftEntry> SavedDrafts { get; } = new();
    public ObservableCollection<AutomationRequestRecord> PendingRequests { get; } = new();
    [ObservableProperty] private bool _persistenceReady;
    [ObservableProperty] private string _persistenceText = "输入需明确保存草稿；结果未知的操作只核对原回执，不自动重发。";
    [ObservableProperty] private AutomationDraftEntry? _selectedSavedDraft;
    [ObservableProperty] private AutomationRequestRecord? _selectedPendingRequest;
    [ObservableProperty] private string _savedDraftPreview = "";
    public AutomationControlViewModel(string? persistenceDataRoot = null, IAutomationPersistenceFileIo? persistenceIo = null)
    {
        var root = persistenceDataRoot ?? Program.SelfTestDataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fufucode");
        _draftStore = new(root, fileIo: persistenceIo); _requestJournal = new(root, fileIo: persistenceIo);
    }
    private sealed record NativeControlAttempt(AutomationRequestRecord Request, Func<object> Execute);
    private NativeControlAttempt Prepare(object command, long now)
    {
        var store = _store!;
        var kind = command switch
        {
            ControlCommand => AutomationRequestKind.Apply, ControlReviewCommand => AutomationRequestKind.Review,
            ControlReconsiderCommand => AutomationRequestKind.Reconsider, ControlRepairCommand => AutomationRequestKind.Repair,
            ControlIntegrationCommand => AutomationRequestKind.Integration, ControlKnowledgeCommand => AutomationRequestKind.Knowledge,
            ControlRelationCommand => AutomationRequestKind.Relation, ControlRestructureCommand => AutomationRequestKind.Restructure,
            ControlStopRunCommand => AutomationRequestKind.Stop, ControlStopResolutionCommand => AutomationRequestKind.StopResolution,
            _ => throw new InvalidOperationException("Unsupported native command type.")
        };
        var json = JsonSerializer.Serialize(command, command.GetType()); using var parsed = JsonDocument.Parse(json);
        var request = new AutomationRequestRecord { CommandId = parsed.RootElement.GetProperty("CommandId").GetString()!, Kind = kind,
            PayloadJson = json, PayloadSha256 = AutomationPersistenceValidation.Sha256HexUtf8(json), CreatedAtMs = now, State = AutomationRequestState.PendingUnknown };
        object Execute() => command switch
        {
            ControlCommand c => store.ApplyControlCommand(c, now), ControlReviewCommand c => store.ApplyControlReview(c, now),
            ControlReconsiderCommand c => store.ApplyControlReconsider(c, now), ControlRepairCommand c => store.ApplyControlRepair(c, now),
            ControlIntegrationCommand c => store.ApplyControlIntegration(c, now), ControlKnowledgeCommand c => store.ApplyControlKnowledge(c, now),
            ControlRelationCommand c => store.ApplyControlRelation(c, now), ControlRestructureCommand c => store.ApplyControlRestructure(c, now),
            ControlStopRunCommand c => store.ApplyControlStopRun(c, now), ControlStopResolutionCommand c => store.ApplyControlStopResolutionAsync(c, now).GetAwaiter().GetResult(),
            _ => throw new InvalidOperationException("Unsupported native command type.")
        };
        return new(request, Execute);
    }
    private void ClearPersistence()
    {
        _persistenceScope = null; _persistenceGeneration = 0; _localDraftId = null; PersistenceReady = false;
        SavedDrafts.Clear(); PendingRequests.Clear(); SelectedSavedDraft = null; SelectedPendingRequest = null; SavedDraftPreview = "";
        InvalidatedDrafts.Clear(); SelectedInvalidatedDraft = null;
        _completedDecisionInput = null;
        OnPropertyChanged(nameof(HasInvalidatedInput));
    }
    private void BindPersistence(ControlClientSnapshot snapshot)
    {
        ClearPersistence();
        if (snapshot.AuthorityIdentity is null) { PersistenceText = "原宿主尚未提供稳定工作台身份；仅观察，请更新原宿主后重新连接。"; return; }
        try
        {
            _persistenceScope = AutomationPersistenceScope.TryCreate(ProjectContext ?? "default-workspace", DatabasePath, snapshot.AuthorityIdentity).ValueOrThrow();
            LoadLocalRecords(reloadGeneration: true);
        }
        catch (Exception error) { PersistenceReady = false; PersistenceText = "本地范围不可用，仅观察，原文件保留：" + error.Message; }
    }
    private void LoadLocalRecords(bool reloadGeneration)
    {
        if (_persistenceScope is not { } scope) return;
        try
        {
            var drafts = _draftStore.Load(scope).ValueOrThrow(); var pending = _requestJournal.ListPendingUnknown(scope).ValueOrThrow();
            if (reloadGeneration) _persistenceGeneration = _draftStore.ReadGeneration(scope).ValueOrThrow();
            SavedDrafts.Clear(); foreach (var draft in drafts) SavedDrafts.Add(draft);
            PendingRequests.Clear(); foreach (var request in pending) PendingRequests.Add(new AutomationRequestRecord
            { CommandId = request.CommandId, Kind = request.Kind, PayloadJson = request.PayloadJson, PayloadSha256 = request.PayloadSha256,
                CreatedAtMs = request.CreatedAtMs, State = request.State });
            PersistenceReady = true; PersistenceText = $"{SavedDrafts.Count} 份保留草稿 · {PendingRequests.Count} 项结果未知。保存输入不会提交决定，核对回执不会重新执行。";
        }
        catch (Exception error) { PersistenceReady = false; PersistenceText = "本地记录不可读取，原文件保留：" + error.Message; }
    }
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public void ReloadLocalRecords() => LoadLocalRecords(reloadGeneration: true);
    partial void OnPersistenceReadyChanged(bool value) => NotifyPersistenceCommands();
    private void NotifyPersistenceCommands()
    { SaveDraftCommand.NotifyCanExecuteChanged(); RestoreDraftCommand.NotifyCanExecuteChanged(); ObservePendingRequestCommand.NotifyCanExecuteChanged(); ReloadLocalRecordsCommand.NotifyCanExecuteChanged();
      SaveInvalidatedDraftCommand.NotifyCanExecuteChanged(); DiscardInvalidatedDraftCommand.NotifyCanExecuteChanged(); }
    partial void OnSelectedSavedDraftChanged(AutomationDraftEntry? value)
    {
        SavedDraftPreview = value is null ? "" : $"草稿 {value.TodoKind} · {value.TodoId}\n理由 {value.Reason}\n依据 {value.Evidence}\n回答 {value.Response}";
        RestoreDraftCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedPendingRequestChanged(AutomationRequestRecord? value) => ObservePendingRequestCommand.NotifyCanExecuteChanged();
    private bool CanObservePendingRequest() => CanRefresh() && PersistenceReady && SelectedPendingRequest is not null;
    [RelayCommand(CanExecute = nameof(CanObservePendingRequest))]
    public async Task ObservePendingRequestAsync() { if (CanObservePendingRequest()) await ObserveOriginalReceiptAsync(SelectedPendingRequest!); }
    private async Task ObserveOriginalReceiptAsync(AutomationRequestRecord request)
    {
        if (!CanRefresh() || _persistenceScope is not { } scope) return;
        var epoch = _epoch; var store = _store!; var generation = _persistenceGeneration; IsBusy = true;
        try
        {
            var receipt = await Task.Run(() => store.ReadControlReceipt(request.Kind.ToString(), request.PayloadJson));
            if (receipt is null) { if (!_disposed && epoch == _epoch) StatusText = "尚未找到原回执，结果仍未知；没有重新发送或执行。"; }
            else
            {
                var write = await Task.Run(() => _requestJournal.ObserveReceipt(scope, request.CommandId, receipt, generation));
                var next = write.ValueOrThrow().Generation;
                if (!_disposed && epoch == _epoch) { _persistenceGeneration = next; StatusText = "已核对并保存原回执；没有重新执行。"; }
            }
        }
        catch (Exception error) { if (!_disposed && epoch == _epoch) StatusText = "原回执核对未完成：" + error.Message; }
        finally { if (!_disposed) IsBusy = false; }
        if (!_disposed && epoch == _epoch) { await RefreshAsync(); LoadLocalRecords(reloadGeneration: false); }
        await DrainPendingConnectionAsync();
    }
    private sealed record CreationInput(string Kind, string PackageJson, string? From, string? To,
        Dictionary<string, long> Versions, string[] Members, ControlRelation? Relation, string Key, string Text,
        string Source, string Reason, string Baseline, string Criteria, string Order);
    private AutomationDraftEntry CaptureDraft()
    {
        string kind, id, context, token = ""; long? version;
        if (CreationVisible && _creationState is not null)
        {
            kind = "creation"; id = CreationKind; version = _creationState.Control.Revision;
            var touched = CreationPackages.Where(p => p.IsMember || ReferenceEquals(p, CreationFrom) || ReferenceEquals(p, CreationTo)).ToArray();
            context = JsonSerializer.Serialize(new CreationInput(CreationKind, JsonSerializer.Serialize(PackageDraft), CreationFrom?.Package.Spec.Id,
                CreationTo?.Package.Spec.Id, touched.ToDictionary(p => p.Package.Spec.Id, p => p.Package.Version),
                CreationPackages.Where(p => p.IsMember).Select(p => p.Package.Spec.Id).ToArray(), CreationRelation?.Relation,
                CreationKey, CreationText, CreationSource, CreationReason, RelationBaseline, RelationCriteria, RelationOrder));
        }
        else if (SelectedDecision is { } row)
        { kind = row.Item.Kind; id = row.Item.Id; context = row.Item.ContextJson; token = row.Item.EvidenceToken; version = row.Item.Version; }
        else if (_stopAudit is { } audit)
        { kind = "stop-resolution"; id = audit.RunId; context = JsonSerializer.Serialize(audit); token = audit.EvidenceToken; version = audit.Revision; }
        else if (SelectedExecution is { } execution)
        { kind = "stop"; id = execution.RunId; context = JsonSerializer.Serialize(execution); token = execution.EvidenceToken; version = Snapshot!.Inbox.Revision; }
        else if (SelectedInvalidatedDraft is { } invalidated && InvalidatedDrafts.Contains(invalidated)) return invalidated;
        else throw new InvalidOperationException("请选择决定、执行或创建表单后保存输入。");
        return new() { DraftId = _localDraftId ?? Guid.NewGuid().ToString("N"), TodoId = id, TodoKind = kind, TodoVersion = version,
            ContextJson = context, ContextSha256 = AutomationPersistenceValidation.Sha256HexUtf8(context), EvidenceToken = token,
            EvidenceTokenSha256 = AutomationPersistenceValidation.Sha256HexUtf8(token),
            Reason = kind == "stop-resolution" ? ResolutionReason : ReasonText, Evidence = kind == "stop-resolution" ? ResolutionEvidence : EvidenceText,
            Response = ResponseText, SelectedCandidateIds = SelectedWinner is null ? [] : [SelectedWinner.Outcome.PackageId] };
    }
    private bool CanSaveDraft() => CanRefresh() && PersistenceReady && (CreationVisible || SelectedDecision is not null || SelectedExecution is not null || _stopAudit is not null || SelectedInvalidatedDraft is not null);
    [RelayCommand(CanExecute = nameof(CanSaveDraft))]
    public async Task SaveDraftAsync()
    {
        if (!CanSaveDraft()) return;
        await SaveCapturedDraftAsync(CaptureDraft());
    }
    private async Task SaveCapturedDraftAsync(AutomationDraftEntry draft)
    {
        if (_persistenceScope is not { } scope) return;
        var epoch = _epoch; var generation = _persistenceGeneration; IsBusy = true;
        try
        {
            var saved = await Task.Run(() => _draftStore.Save(scope, draft, generation));
            var next = saved.ValueOrThrow().Generation;
            if (!_disposed && epoch == _epoch) { if (!InvalidatedDrafts.Contains(draft)) _localDraftId = draft.DraftId;
                _persistenceGeneration = next; StatusText = "草稿已保存；没有提交决定或启动执行。"; }
        }
        catch (Exception error) { if (!_disposed && epoch == _epoch) StatusText = "草稿未保存，原输入保留：" + error.Message; }
        finally { if (!_disposed) IsBusy = false; }
        if (!_disposed && epoch == _epoch) LoadLocalRecords(reloadGeneration: false);
        await DrainPendingConnectionAsync();
    }
    private bool CanRestoreDraft() => CanRefresh() && PersistenceReady && SelectedSavedDraft is not null;
    [RelayCommand(CanExecute = nameof(CanRestoreDraft))]
    public async Task RestoreDraftAsync()
    {
        if (!CanRestoreDraft() || _persistenceScope is not { } scope) return;
        var draft = SelectedSavedDraft!; var epoch = _epoch;
        try
        {
            var stored = _draftStore.Find(scope, draft.DraftId).ValueOrThrow();
            if (stored is null || stored.ContentFingerprint != draft.ContentFingerprint || stored.UpdatedAtMs != draft.UpdatedAtMs)
                throw new InvalidOperationException("草稿已在他处更新，请重读本地记录。");
            if (draft.TodoKind == "creation")
            {
                if (draft.TodoVersion != Snapshot!.Inbox.Revision) throw new InvalidOperationException("创建草稿属于旧工作台版本，原稿仍保留，不能自动换版本。");
                var input = JsonSerializer.Deserialize<CreationInput>(draft.ContextJson)!;
                foreach (var pair in input.Versions)
                    if (!Snapshot.State.Control.Packages.Any(p => p.Spec.Id == pair.Key && p.Version == pair.Value)) throw new InvalidOperationException("原规范版本已变化。");
                BeginCreation(input.Kind); SetPackageDraft(JsonSerializer.Deserialize<AutomationPackageDraft>(input.PackageJson)!);
                CreationFrom = CreationPackages.SingleOrDefault(p => p.Package.Spec.Id == input.From);
                CreationTo = CreationPackages.SingleOrDefault(p => p.Package.Spec.Id == input.To);
                // Source selection may populate the package fields; restore the exact edited fields afterwards.
                SetPackageDraft(JsonSerializer.Deserialize<AutomationPackageDraft>(input.PackageJson)!);
                foreach (var option in CreationPackages) option.IsMember = input.Members.Contains(option.Package.Spec.Id);
                CreationRelation = RelationOptions.SingleOrDefault(option => option.Relation == input.Relation);
                CreationKey = input.Key; CreationText = input.Text; CreationSource = input.Source; CreationReason = input.Reason;
                RelationBaseline = input.Baseline; RelationCriteria = input.Criteria; RelationOrder = input.Order;
            }
            else if (draft.TodoKind == "stop" || draft.TodoKind == "stop-resolution")
            {
                if (draft.TodoKind == "stop")
                {
                    var execution = JsonSerializer.Deserialize<ControlRunningExecution>(draft.ContextJson)!; SelectTask(execution.TaskId);
                    if (SelectedExecution is not { } current || current.EvidenceToken != draft.EvidenceToken ||
                        Snapshot!.Inbox.Revision != draft.TodoVersion || JsonSerializer.Serialize(current) != draft.ContextJson)
                        throw new InvalidOperationException("执行身份或版本已变化，原取消草稿保留。");
                }
                else
                {
                    var stop = JsonSerializer.Deserialize<ControlStopReconciliation>(draft.ContextJson)!;
                    SelectTask(stop!.TaskId); SelectedCancelledRun = CancelledRuns.SingleOrDefault(run => run.RunId == stop.RunId);
                    await InspectCancelledRunAsync();
                    if (_disposed || epoch != _epoch) return;
                    if (_stopAudit is not { } current || current.Revision != draft.TodoVersion || current.EvidenceToken != draft.EvidenceToken)
                        throw new InvalidOperationException("原对账证据已变化，请重新核对，原草稿仍保留。");
                    ResolutionReason = draft.Reason; ResolutionEvidence = draft.Evidence;
                }
            }
            else
            {
                var row = Inbox.SingleOrDefault(item => item.Item.Id == draft.TodoId && item.Item.Kind == draft.TodoKind);
                if (row is null || row.Item.Version != draft.TodoVersion || row.Item.EvidenceToken != draft.EvidenceToken || row.Item.ContextJson != draft.ContextJson)
                    throw new InvalidOperationException("待办身份、版本或证据已变化，旧草稿保留且未套用。");
                SelectedDecision = row;
                if (draft.SelectedCandidateIds.Count > 0) SelectedWinner = WinnerChoices.SingleOrDefault(w => w.Outcome.PackageId == draft.SelectedCandidateIds.Single());
            }
            if (draft.TodoKind != "stop-resolution") { ReasonText = draft.Reason; EvidenceText = draft.Evidence; ResponseText = draft.Response; }
            _localDraftId = draft.DraftId; StatusText = "已恢复原输入，请核对后明确提交；没有执行决定。";
        }
        catch (Exception error) { if (!_disposed && epoch == _epoch) StatusText = "草稿未恢复：" + error.Message; }
    }
}
