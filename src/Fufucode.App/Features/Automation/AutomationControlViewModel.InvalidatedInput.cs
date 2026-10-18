using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Features.Automation.Persistence;

namespace Fufucode.App.Features.Automation;

public sealed partial class AutomationControlViewModel
{
    private AutomationInboxRow? _completedDecisionInput;
    public ObservableCollection<AutomationDraftEntry> InvalidatedDrafts { get; } = new();
    public bool HasInvalidatedInput => InvalidatedDrafts.Count > 0;
    [ObservableProperty] private AutomationDraftEntry? _selectedInvalidatedDraft;
    public string InvalidatedInputPreview => SelectedInvalidatedDraft is not { } draft ? "" :
        $"原待办 {draft.TodoKind} · {draft.TodoId}\n原版本 {draft.TodoVersion}\n理由 {draft.Reason}\n依据 {draft.Evidence}\n回答 {draft.Response}\n候选 {string.Join("、", draft.SelectedCandidateIds)}";

    private void PreserveInvalidatedDecisionInput()
    {
        if (SelectedDecision is null || ReferenceEquals(SelectedDecision, _completedDecisionInput) || CreationVisible ||
            (ReasonText.Length == 0 && EvidenceText.Length == 0 && ResponseText.Length == 0 && SelectedWinner is null)) return;
        var draft = CaptureDraft();
        var existing = InvalidatedDrafts.FirstOrDefault(d => d.DraftId == draft.DraftId);
        if (existing is not null) InvalidatedDrafts[InvalidatedDrafts.IndexOf(existing)] = draft;
        else InvalidatedDrafts.Add(draft);
        OnPropertyChanged(nameof(HasInvalidatedInput));
        SelectedInvalidatedDraft = draft;
    }

    partial void OnSelectedInvalidatedDraftChanged(AutomationDraftEntry? value)
    {
        OnPropertyChanged(nameof(InvalidatedInputPreview));
        SaveInvalidatedDraftCommand.NotifyCanExecuteChanged();
        DiscardInvalidatedDraftCommand.NotifyCanExecuteChanged();
        SaveDraftCommand.NotifyCanExecuteChanged();
    }

    private bool CanSaveInvalidatedDraft() => CanRefresh() && PersistenceReady &&
        SelectedInvalidatedDraft is { } draft && InvalidatedDrafts.Contains(draft);

    [RelayCommand(CanExecute = nameof(CanSaveInvalidatedDraft))]
    public async Task SaveInvalidatedDraftAsync()
    {
        if (CanSaveInvalidatedDraft()) await SaveCapturedDraftAsync(SelectedInvalidatedDraft!);
    }

    private bool CanDiscardInvalidatedDraft() => !IsBusy && SelectedInvalidatedDraft is { } draft && InvalidatedDrafts.Contains(draft);
    [RelayCommand(CanExecute = nameof(CanDiscardInvalidatedDraft))]
    public void DiscardInvalidatedDraft()
    {
        if (!CanDiscardInvalidatedDraft()) return;
        InvalidatedDrafts.Remove(SelectedInvalidatedDraft!);
        OnPropertyChanged(nameof(HasInvalidatedInput));
        SelectedInvalidatedDraft = InvalidatedDrafts.LastOrDefault();
        // Discarding the temporary input buffer must not delete a saved draft or alter Core.
    }
}
