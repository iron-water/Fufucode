using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Fufucode.App.Features.Automation;
using Fufucode.Core.Automation;

namespace Fufucode.App.SelfTest;

internal static partial class AutomationControlSelfTest
{
    private static async Task RunCreationAsync(AutomationStore store, AutomationControlViewModel model,
        AutomationControlPageView view, Window window, Action<bool, string> check)
    {
        long Rev() => store.ReadControlSnapshot().Revision;
        long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        async Task Begin(string kind) { await Until(() => !model.IsBusy); await model.RefreshAsync(); model.BeginCreation(kind); await Paint(window); }
        void Fill(string id)
        {
            var draft = model.PackageDraft; draft.Id = id; draft.Title = "same"; draft.Goal = "Keep bounded native policy";
            draft.Scope = "fixture.txt\nowned/**"; draft.Acceptance = "Retain actual evidence";
        }
        var form = view.FindControl<AutomationControlCreationView>("CreationForm")!;
        async Task Save(Action? change = null)
        {
            var button = form.FindControl<Button>("CreationSave")!; await Paint(window); button.BringIntoView(); await Paint(window);
            await Until(() => !model.IsBusy);
            window.Activate();
            if (!button.IsEnabled || !button.Focus()) throw new InvalidOperationException("Creation submit is unavailable");
            var previous = model.CreateCommand.ExecutionTask;
            change?.Invoke(); window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            try { await Until(() => model.CreateCommand.ExecutionTask != previous); }
            catch (TimeoutException error) { throw new InvalidOperationException($"Actual creation key did not start command: {model.CreationKind}; busy={model.IsBusy}; enabled={button.IsEnabled}; focused={button.IsFocused}; active={window.IsActive}; status={model.StatusText}", error); }
            await model.CreateCommand.ExecutionTask!; await Paint(window);
        }
        await Begin("package");
        check(model.CreationVisible && !model.CreateCommand.CanExecute(null), "empty native contract cannot be submitted");
        Fill("native-A"); form.FindControl<TextBox>("PackageGoalInput")!.Text = "Keep bounded native policy 中文😀";
        await Until(() => !model.IsBusy);
        check(model.PackageDraft.Goal.EndsWith("中文😀") && model.CreateCommand.CanExecute(null), "compiled native goal input binds exact Unicode text and complete contract enables submit");
        var creationButton = form.FindControl<Button>("CreationSave")!;
        await Paint(window); creationButton.BringIntoView(); await Paint(window);
        await Until(() => !model.IsBusy); window.Activate();
        check(creationButton.Focus() && creationButton.IsFocused, "creation button receives actual keyboard focus before snapshot refresh");
        await model.RefreshAsync(); await Paint(window);
        check(creationButton.IsFocused, "read-only unchanged snapshot refresh preserves creation button keyboard focus");
        await Save();
        var package = store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == "native-A");
        check(package.Version == 1 && !package.Approved && package.Spec.Scope.SequenceEqual(["fixture.txt", "owned/**"]) && package.Spec.Model == "devin/swe-2" && !model.CreationVisible,
            "focused native contract creation keeps scope, explicit model and unapproved plan without a run");
        var receiptRevision = Rev(); await model.RetryReceiptAsync(); check(Rev() == receiptRevision, "native contract creation original receipt does not create a second version");
        await Begin("package"); Fill("native-A"); check(!model.CreateCommand.CanExecute(null), "typing an existing ID does not silently overwrite its contract");
        form.FindControl<ComboBox>("CreationFromInput")!.SelectedItem = model.CreationPackages.Single(p => p.Package.Spec.Id == "native-A");
        check(model.IsPackageEditing && form.FindControl<TextBox>("PackageIdInput")!.IsReadOnly && model.CreationVersionText.Contains("native-A v1"), "explicit contract revision locks original ID and displays captured version");
        model.PackageDraft.Goal = "Explicit native policy revision"; await Save();
        check(store.GetWorkPackageVersion("native-A", 1).Spec.Goal.EndsWith("中文😀") && store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == "native-A").Version == 2,
            "native revision preserves exact historical contract instead of editing it in place");
        var opaqueSpec = new WorkPackageSpec(" native-opaque ", "same", "Opaque historical identity", ["fixture.txt"], ["Preserve identity"], "codex", "devin/swe-2", "evidence", []);
        store.ApplyControlCommand(new("native-opaque-seed", Rev(), "fixture", ControlAction.DefinePackage, opaqueSpec, ExpectedPackageVersion: 0), Now());
        await Begin("package"); model.CreationFrom = model.CreationPackages.Single(p => p.Package.Spec.Id == opaqueSpec.Id);
        model.PackageDraft.Goal = "Explicit revision of the exact original identity"; await Save();
        check(store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == opaqueSpec.Id).Version == 2 && !store.ReadControlSnapshot().Packages.Any(p => p.Spec.Id == opaqueSpec.Id.Trim()),
            "editing a legal opaque contract ID preserves it exactly rather than trimming and retargeting: " + model.StatusText);
        await Begin("package"); Fill("native-B"); await Save();
        await Begin("package"); Fill("native-stale");
        var oldFormVersion = model.CreationVersionText;
        await Save(() => store.ApplyControlCommand(new("create-stale-state", Rev(), "other-human", ControlAction.SetMode, Mode: DevelopmentMode.Manual), Now()));
        check(!store.ReadControlSnapshot().Packages.Any(p => p.Spec.Id == "native-stale") && model.CreationVisible && model.PackageDraft.Id == "native-stale" && model.CreationVersionText == oldFormVersion && model.StatusText.Contains("操作未完成"),
            "stale creation refuses original revision and preserves user inputs for explicit rechecking");
        await Begin("knowledge"); model.CreationKey = "native-policy"; model.CreationText = "Three routes 中文😀";
        check(!model.CreateCommand.CanExecute(null), "native knowledge proposal requires evidence source");
        form.FindControl<TextBox>("KnowledgeSourceInput")!.Text = "human policy reference"; await Save();
        var fact = store.ReadControlSnapshot().Knowledge.Proposals.Single(p => p.Key == "native-policy");
        check(fact is { Text: "Three routes 中文😀", Source: "human policy reference", PreviousVersion: 0, Status: "pending" } && !store.ReadControlSnapshot().Knowledge.Facts.Any(f => f.Key == "native-policy"),
            "native knowledge creation records a source-bound pending proposal rather than publication");
        await Begin("question"); model.CreationText = "Who owns this native policy?";
        var first = model.CreationPackages.Single(p => p.Package.Spec.Id == "native-A"); var second = model.CreationPackages.Single(p => p.Package.Spec.Id == "native-B");
        form.FindControl<ComboBox>("CreationFromInput")!.SelectedItem = first; form.FindControl<ComboBox>("CreationToInput")!.SelectedItem = first;
        check(!model.CreateCommand.CanExecute(null), "native directed question refuses the same source and target despite duplicate titles");
        form.FindControl<ComboBox>("CreationToInput")!.SelectedItem = second; await Save();
        var question = store.ReadControlSnapshot().Knowledge.Questions.Single(q => q.Text == "Who owns this native policy?");
        check(question is { FromPackage: "native-A", FromVersion: 2, ToPackage: "native-B", ToVersion: 1, Answer: null }, "native question binds the distinct original package IDs and versions");

        foreach (var kind in Enum.GetValues<ControlRelation>())
        {
            await Begin("relation"); model.CreationKey = "native-relation-" + kind;
            model.CreationRelation = model.RelationOptions.Single(o => o.Relation == kind);
            form.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Tag, "native-A")).IsChecked = true;
            form.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Tag, "native-B")).IsChecked = true;
            if (kind == ControlRelation.Competitive)
            {
                check(!model.CreateCommand.CanExecute(null), "native competition requires committed baseline and shared criteria");
                model.RelationBaseline = model.Snapshot!.Graph!.Threads.SelectMany(t => t.Tasks).SelectMany(t => t.Parts).Single(p => p.PartId == "b-one").HeadSha!;
                model.RelationCriteria = "Actual accepted policy evidence";
            }
            if (kind == ControlRelation.Pipeline)
            {
                model.RelationOrder = "native-B\nnative-B";
                check(!model.CreateCommand.CanExecute(null), "native pipeline refuses duplicate or incomplete order"); model.RelationOrder = "native-B\nnative-A";
            }
            await Save(); var group = store.ReadControlGroups().Single(g => g.Spec.Id == "native-relation-" + kind);
            check(group.Spec.Relation == kind && group.Winner is null && group.Spec.Members.Length == 2 &&
                (kind != ControlRelation.Pipeline || group.Spec.Members.SequenceEqual(["native-B", "native-A"])), "native relationship stores explicit membership and semantics: " + kind);
        }
        check(store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == "native-A").Spec.DependsOn.Contains("native-B"), "native explicit pipeline introduces the intended contract dependency without execution");
        await Begin("package"); model.CreationFrom = model.CreationPackages.Single(p => p.Package.Spec.Id == "native-B");
        var originalB = model.CreationFrom.Package.Version; model.PackageDraft.Dependencies = "native-A"; await Save();
        check(store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == "native-B").Version == originalB && model.CreationVisible && model.PackageDraft.Dependencies == "native-A" && model.StatusText.Contains("操作未完成"),
            "native contract cycle is rejected transactionally while keeping the invalid input for correction: " + model.StatusText +
                $"; visible={model.CreationVisible}; deps={model.PackageDraft.Dependencies}");

        await Begin("restructure"); model.CreationFrom = model.CreationPackages.Single(p => p.Package.Spec.Id == "native-A");
        model.PackageDraft.Id = "native-successor"; model.CreationReason = "Preserve the original policy in one bounded successor";
        check(model.CreationPreview.Contains("native-A → native-successor") && model.CreationPreview.Contains("native-successor.relation.native-relation-Competitive") && model.CreationPreview.Contains("native-B") && model.CreationPreview.Contains("Actual accepted policy evidence"),
            "native successor preview explicitly preserves source goal and all affected relationship members");
        await Save(); var restructure = store.ReadControlSnapshot().Restructures.Single(r => r.Spec.Replacements.Any(p => p.Id == "native-successor"));
        check(restructure.Status == "pending" && restructure.Spec.GroupMappings.Length == 4 && !store.ReadControlSnapshot().Packages.Any(p => p.Spec.Id == "native-successor") &&
            !store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == "native-A").Superseded,
            "native single successor creates a formal mapped proposal without approval, retirement or fabricated successor execution");
        check(model.Snapshot!.Graph!.Threads.Count == 2 && store.GetPart("a-one").Status == DeliveryPartStatus.Accepted && !store.IsIntegrated("a-one", 1),
            "all native creation operations preserve real threads, accepted sibling evidence and integration boundary");
        await Begin("knowledge"); model.CreationText = "unfinished private draft"; model.ReturnToGraph();
        check(!model.CreationVisible && model.CreationText == "" && model.CreationPackages.Count == 0 && model.Connected, "return clears creation input and captured choices while keeping the same authority");
        await Begin("package"); Fill("do-not-cross-project"); model.SetProjectContext("creation-project-switch");
        check(!model.CreationVisible && !model.CreateCommand.CanExecute(null) && model.PackageDraft.Id == "" && !model.Connected, "project switch clears old native creation scope and forbids retargeting a draft");
        await model.ConnectAsync(store.DatabasePath);
    }
}
