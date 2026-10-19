using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.Core.Automation;

namespace Fufucode.App.Features.Automation;

public sealed partial class AutomationControlViewModel
{
    public ObservableCollection<ControlRunStopState> CancelledRuns { get; } = new();
    [ObservableProperty] private ControlRunStopState? _selectedCancelledRun;
    [ObservableProperty] private string _reconciliationText = "先选择取消记录，再核查原运行和工作树。";
    [ObservableProperty] private string _resolutionReason = "";
    [ObservableProperty] private string _resolutionEvidence = "";
    private ControlStopReconciliation? _stopAudit;
    public bool HasCancelledRuns => CancelledRuns.Count > 0;

    private void SelectCancelledRuns(string? threadId, string? taskId)
    {
        CancelledRuns.Clear(); SelectedCancelledRun = null; ClearStopAudit();
        if (threadId is not null || taskId is not null)
            foreach (var run in Snapshot?.RunStops ?? [])
                if (threadId is not null ? run.ThreadId == threadId : run.TaskId == taskId) CancelledRuns.Add(run);
        OnPropertyChanged(nameof(HasCancelledRuns));
    }
    private void ClearStopAudit()
    {
        _stopAudit = null; ResolutionReason = ""; ResolutionEvidence = "";
        ReconciliationText = "先选择取消记录，再核查原运行和工作树。";
        InspectCancelledRunCommand.NotifyCanExecuteChanged(); ResolveCancelledRunCommand.NotifyCanExecuteChanged();
        NotifyPersistenceCommands();
    }
    partial void OnSelectedCancelledRunChanged(ControlRunStopState? value) => ClearStopAudit();
    partial void OnResolutionReasonChanged(string value) => ResolveCancelledRunCommand.NotifyCanExecuteChanged();
    partial void OnResolutionEvidenceChanged(string value) => ResolveCancelledRunCommand.NotifyCanExecuteChanged();
    private bool CanInspectCancelledRun() => CanRefresh() && SelectedCancelledRun is not null;
    [RelayCommand(CanExecute = nameof(CanInspectCancelledRun))]
    public async Task InspectCancelledRunAsync()
    {
        if (!CanInspectCancelledRun()) return;
        var selected = SelectedCancelledRun!; var epoch = _epoch; var store = _store!;
        ClearStopAudit(); IsBusy = true;
        try
        {
            var audit = await Task.Run(() => store.ReadControlStopReconciliationAsync(selected.RunId));
            if (!_disposed && epoch == _epoch && SelectedCancelledRun == selected)
            {
                _stopAudit = audit;
                ReconciliationText = $"运行 {audit.RunId}\n原取消请求 {audit.StopCommandId}\n工作树 {audit.Worktree}\n当前 HEAD {audit.ObservedHead ?? "无法读取"}\n" +
                    (audit.TrackedClean switch { true => "已跟踪文件无修改", false => "已跟踪文件仍有修改；保留原文件，请核对", _ => "工作树状态无法读取" }) +
                    "\n" + (audit.CanInterrupt ? "原写入者已退出。填写实际核查依据和理由，可明确记录为中断。" : "当前证据不允许记录中断，请保留并继续核查。") +
                    "\n" + audit.Detail + "\n记录中断不接受输出，不自动重跑或解冻线程。";
            }
        }
        catch (Exception error) { if (!_disposed && epoch == _epoch) ReconciliationText = "核查未完成：" + error.Message; }
        finally { if (!_disposed) IsBusy = false; }
        await DrainPendingConnectionAsync();
    }
    private bool CanResolveCancelledRun() => CanRefresh() && _stopAudit is { CanInterrupt: true } audit &&
        SelectedCancelledRun?.RunId == audit.RunId && !string.IsNullOrWhiteSpace(ResolutionReason) && !string.IsNullOrWhiteSpace(ResolutionEvidence);
    [RelayCommand(CanExecute = nameof(CanResolveCancelledRun))]
    public async Task ResolveCancelledRunAsync()
    {
        if (!CanResolveCancelledRun()) return;
        var store = _store!; var audit = _stopAudit!;
        var command = new ControlStopResolutionCommand(Guid.NewGuid().ToString("N"), audit.Revision, "desktop-user",
            audit.RunId, audit.ThreadId, audit.TaskId, audit.StopCommandId, audit.EvidenceToken, audit.ObservedHead!, ResolutionReason, ResolutionEvidence);
        if (await Submit(Prepare(command, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())))
        { ClearStopAudit(); StatusText = "已记录为中断；原文件和历史保留，未接受输出或重新执行。"; }
    }
}
