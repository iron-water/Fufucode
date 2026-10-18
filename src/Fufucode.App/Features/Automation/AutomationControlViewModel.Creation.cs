using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;

namespace Fufucode.App.Features.Automation;

public sealed partial class AutomationPackageDraft : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _goal = "";
    [ObservableProperty] private string _scope = "";
    [ObservableProperty] private string _acceptance = "";
    [ObservableProperty] private string _harness = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _artifact = "evidence";
    [ObservableProperty] private string _dependencies = "";
    [ObservableProperty] private string _knowledge = "";
    internal static string[] Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    internal WorkPackageSpec Spec() => new(Id.Trim(), Title.Trim(), Goal.Trim(), Lines(Scope), Lines(Acceptance), Harness.Trim(), Model.Trim(), Artifact, Lines(Dependencies))
        { KnowledgeRefs = Lines(Knowledge) };
    internal bool Complete => new[] { Id, Title, Goal, Scope, Acceptance, Harness, Model }.All(s => !string.IsNullOrWhiteSpace(s)) && Artifact is "code" or "evidence";
    internal static AutomationPackageDraft From(WorkPackageSpec spec) => new()
    { Id = spec.Id, Title = spec.Title, Goal = spec.Goal, Scope = string.Join("\n", spec.Scope), Acceptance = string.Join("\n", spec.Acceptance),
      Harness = spec.Harness, Model = spec.Model, Artifact = spec.Artifact, Dependencies = string.Join("\n", spec.DependsOn), Knowledge = string.Join("\n", spec.KnowledgeRefs ?? []) };
}
public sealed partial class AutomationPackageOption : ObservableObject
{
    public AutomationPackageOption(WorkPackageVersion package, Action changed) { Package = package; _changed = changed; }
    private readonly Action _changed;
    public WorkPackageVersion Package { get; }
    public string Label => $"{Package.Spec.Title} · {Package.Spec.Id} · v{Package.Version}" + (Package.Approved ? " · 已批准" : " · 未批准");
    [ObservableProperty] private bool _isMember;
    partial void OnIsMemberChanged(bool value) => _changed();
}
public sealed record AutomationRelationOption(ControlRelation Relation, string Label);

public sealed partial class AutomationControlViewModel
{
    private ControlShadowExport? _creationState;
    private long _creationEpoch;
    private string? _creationDatabase;
    public ObservableCollection<AutomationPackageOption> CreationPackages { get; } = new();
    public AutomationRelationOption[] RelationOptions { get; } =
        [new(ControlRelation.Independent, "独立并行"), new(ControlRelation.Competitive, "竞争候选"), new(ControlRelation.Complementary, "互补协作"), new(ControlRelation.Pipeline, "流水线")];
    public string[] HarnessOptions { get; } = ["pi", "omp", "codex", "dsh"];
    public string[] ArtifactOptions { get; } = ["evidence", "code"];
    public AutomationPackageDraft PackageDraft { get; private set; } = new();
    [ObservableProperty] private bool _creationVisible;
    [ObservableProperty] private string _creationKind = "";
    [ObservableProperty] private string _creationTitle = "";
    [ObservableProperty] private string _creationKey = "";
    [ObservableProperty] private string _creationText = "";
    [ObservableProperty] private string _creationSource = "";
    [ObservableProperty] private string _creationReason = "";
    [ObservableProperty] private string _relationBaseline = "";
    [ObservableProperty] private string _relationCriteria = "";
    [ObservableProperty] private string _relationOrder = "";
    [ObservableProperty] private AutomationPackageOption? _creationFrom;
    [ObservableProperty] private AutomationPackageOption? _creationTo;
    [ObservableProperty] private AutomationRelationOption? _creationRelation;
    public bool IsPackageCreation => CreationVisible && CreationKind is "package" or "restructure";
    public bool IsKnowledgeCreation => CreationVisible && CreationKind == "knowledge";
    public bool IsQuestionCreation => CreationVisible && CreationKind == "question";
    public bool IsRelationCreation => CreationVisible && CreationKind == "relation";
    public bool IsRestructureCreation => CreationVisible && CreationKind == "restructure";
    public bool IsPackageEditing => CreationKind == "package" && CreationFrom is not null;
    public bool ShowSourcePackage => CreationVisible && CreationKind is "package" or "question" or "restructure";
    public bool IsCompetitionCreation => IsRelationCreation && CreationRelation?.Relation == ControlRelation.Competitive;
    public bool IsPipelineCreation => IsRelationCreation && CreationRelation?.Relation == ControlRelation.Pipeline;
    public string CreationVersionText => _creationState is null ? "" : $"打开时工作台版本 {_creationState.Control.Revision}" +
        (CreationFrom is null ? "" : $" · 原规范 {CreationFrom.Package.Spec.Id} v{CreationFrom.Package.Version}") +
        (CreationTo is null ? "" : $" → {CreationTo.Package.Spec.Id} v{CreationTo.Package.Version}");
    public string CreationPreview
    {
        get
        {
            if (!IsRestructureCreation || CreationFrom is null) return "";
            return $"{CreationFrom.Package.Spec.Id} → {PackageDraft.Id}\n原目标 {CreationFrom.Package.Spec.Goal}\n后继目标 {PackageDraft.Goal}\n目标保留理由 {CreationReason}\n" +
                string.Join("\n", SuccessorGroups().Select(g => $"关系 {g.Id} · {g.Relation} · {string.Join(" → ", g.Members)}\n基准 {g.Baseline}\n依据 {string.Join("、", g.Criteria ?? [])}"));
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public void BeginCreation(string kind)
    {
        if (!CanRefresh() || kind is not ("package" or "knowledge" or "question" or "relation" or "restructure")) return;
        SelectCancelledRuns(null, null);
        _localDraftId = null;
        CloseCreation(); _creationState = Snapshot!.State; _creationEpoch = _epoch; _creationDatabase = DatabasePath;
        CreationKind = kind; CreationTitle = kind switch { "package" => "任务规范", "knowledge" => "知识提案", "question" => "定向问题", "relation" => "并行关系", _ => "单项替代方案" };
        CreationKey = ""; CreationText = ""; CreationSource = ""; CreationReason = ""; RelationBaseline = ""; RelationCriteria = ""; RelationOrder = "";
        SetPackageDraft(new());
        foreach (var package in _creationState.Control.Packages.Where(p => !p.Superseded)) CreationPackages.Add(new(package, CreationInputsChanged));
        CreationRelation = RelationOptions[0]; CreationVisible = true; CreationInputsChanged();
    }
    [RelayCommand]
    public void CloseCreation()
    {
        CreationVisible = false; CreationFrom = null; CreationTo = null; CreationPackages.Clear(); _creationState = null; _creationDatabase = null;
        CreationKey = ""; CreationText = ""; CreationSource = ""; CreationReason = "";
        RelationBaseline = ""; RelationCriteria = ""; RelationOrder = ""; SetPackageDraft(new());
        CreationInputsChanged();
    }
    private void SetPackageDraft(AutomationPackageDraft draft)
    {
        PackageDraft = draft; draft.PropertyChanged += (_, _) => CreationInputsChanged(); OnPropertyChanged(nameof(PackageDraft));
    }
    public void CreationInputsChanged()
    {
        foreach (var property in new[] { nameof(IsPackageCreation), nameof(IsKnowledgeCreation), nameof(IsQuestionCreation), nameof(IsRelationCreation),
            nameof(IsRestructureCreation), nameof(IsPackageEditing), nameof(ShowSourcePackage), nameof(IsCompetitionCreation), nameof(IsPipelineCreation), nameof(CreationVersionText), nameof(CreationPreview) }) OnPropertyChanged(property);
        CreateCommand.NotifyCanExecuteChanged();
        NotifyPersistenceCommands();
    }
    private bool CanCreate()
    {
        if (!CanRefresh() || !CreationVisible || _creationState is null || _creationEpoch != _epoch || _creationDatabase != DatabasePath) return false;
        bool Has(string text) => !string.IsNullOrWhiteSpace(text);
        bool Selected(AutomationPackageOption? p) => p is not null && CreationPackages.Contains(p);
        return CreationKind switch
        {
            "package" => PackageDraft.Complete && (CreationFrom is not null ? Selected(CreationFrom) && PackageDraft.Id == CreationFrom.Package.Spec.Id : !_creationState.Control.Packages.Any(p => p.Spec.Id == PackageDraft.Id.Trim())),
            "knowledge" => Has(CreationKey) && Has(CreationText) && Has(CreationSource),
            "question" => Has(CreationText) && Selected(CreationFrom) && Selected(CreationTo) && CreationFrom!.Package.Spec.Id != CreationTo!.Package.Spec.Id,
            "restructure" => Selected(CreationFrom) && PackageDraft.Complete && Has(CreationReason) && !_creationState.Control.Packages.Any(p => p.Spec.Id == PackageDraft.Id.Trim()),
            "relation" => Has(CreationKey) && CreationRelation is not null && RelationOptions.Contains(CreationRelation) && CreationPackages.Count(p => p.IsMember) >= 2 &&
                CreationPackages.Where(p => p.IsMember).All(p => !p.Package.Approved) &&
                (CreationRelation.Relation != ControlRelation.Competitive || GitCliEvidence.IsSha(RelationBaseline.Trim()) && Has(RelationCriteria)) &&
                (CreationRelation.Relation != ControlRelation.Pipeline || PipelineOrderValid()), _ => false
        };
    }
    private bool PipelineOrderValid()
    {
        var order = AutomationPackageDraft.Lines(RelationOrder); var selected = CreationPackages.Where(p => p.IsMember).Select(p => p.Package.Spec.Id).ToArray();
        return order.Length == selected.Length && order.Distinct(StringComparer.Ordinal).Count() == order.Length && selected.All(order.Contains);
    }
    private ControlGroupSpec[] SuccessorGroups()
    {
        if (_creationState is null || CreationFrom is null) return [];
        var old = CreationFrom.Package.Spec.Id;
        return _creationState.Groups.Where(g => !g.Superseded && g.Spec.Members.Contains(old)).Select(g => g.Spec with
            { Id = PackageDraft.Id.Trim() + ".relation." + g.Spec.Id, Members = g.Spec.Members.Select(id => id == old ? PackageDraft.Id.Trim() : id).ToArray() }).ToArray();
    }
    [RelayCommand(CanExecute = nameof(CanCreate))]
    public async System.Threading.Tasks.Task CreateAsync()
    {
        if (!CanCreate()) return;
        var store = _store!; var revision = _creationState!.Control.Revision; var id = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); const string actor = "desktop-user";
        object nativeCommand;
        switch (CreationKind)
        {
            case "package":
                var packageSpec = PackageDraft.Spec();
                if (CreationFrom is { } original) packageSpec = packageSpec with { Id = original.Package.Spec.Id };
                var define = new ControlCommand(id, revision, actor, ControlAction.DefinePackage, packageSpec, ExpectedPackageVersion: CreationFrom?.Package.Version ?? 0);
                nativeCommand = define; break;
            case "knowledge":
                var key = CreationKey.Trim(); var factVersion = _creationState.Control.Knowledge.Facts.Where(f => f.Key == key).Select(f => f.Version).DefaultIfEmpty(0).Max();
                var proposal = new ControlKnowledgeCommand(id, revision, actor, ControlKnowledgeAction.Propose, Key: key, Text: CreationText, Source: CreationSource, ExpectedKnowledgeVersion: factVersion);
                nativeCommand = proposal; break;
            case "question":
                var from = CreationFrom!.Package; var to = CreationTo!.Package;
                var question = new ControlKnowledgeCommand(id, revision, actor, ControlKnowledgeAction.Ask, Text: CreationText, FromPackage: from.Spec.Id,
                    ToPackage: to.Spec.Id, ExpectedFromVersion: from.Version, ExpectedToVersion: to.Version);
                nativeCommand = question; break;
            case "relation":
                var relation = CreationRelation!.Relation;
                var members = relation == ControlRelation.Pipeline ? AutomationPackageDraft.Lines(RelationOrder) : CreationPackages.Where(p => p.IsMember).Select(p => p.Package.Spec.Id).ToArray();
                var group = new ControlGroupSpec(CreationKey.Trim(), relation, members, relation == ControlRelation.Competitive ? RelationBaseline.Trim() : null,
                    relation == ControlRelation.Competitive ? AutomationPackageDraft.Lines(RelationCriteria) : null);
                var command = new ControlRelationCommand(id, revision, actor, ControlRelationAction.DefineGroup, group);
                nativeCommand = command; break;
            case "restructure":
                var source = CreationFrom!.Package; var replacements = new[] { PackageDraft.Spec() }; var groups = SuccessorGroups();
                var mappings = _creationState.Groups.Where(g => !g.Superseded && g.Spec.Members.Contains(source.Spec.Id))
                    .Select(g => new ControlGroupMapping(g.Spec.Id, [PackageDraft.Id.Trim() + ".relation." + g.Spec.Id], CreationReason)).ToArray();
                var spec = new ControlRestructureSpec(new Dictionary<string, long> { [source.Spec.Id] = source.Version }, replacements,
                    [new(source.Spec.Id, [PackageDraft.Id.Trim()], CreationReason)], groups, mappings, CreationReason);
                var restructure = new ControlRestructureCommand(id, revision, actor, ControlRestructureAction.Propose, spec);
                nativeCommand = restructure; break;
            default: return;
        }
        if (await Submit(Prepare(nativeCommand, now))) CloseCreation();
    }
    partial void OnCreationFromChanged(AutomationPackageOption? value)
    {
        if (value is not null && CreationKind is "package" or "restructure")
        {
            var draft = AutomationPackageDraft.From(value.Package.Spec); if (CreationKind == "restructure") draft.Id = ""; SetPackageDraft(draft);
        }
        CreationInputsChanged();
    }
    partial void OnCreationToChanged(AutomationPackageOption? value) => CreationInputsChanged();
    partial void OnCreationRelationChanged(AutomationRelationOption? value) => CreationInputsChanged();
    partial void OnCreationKeyChanged(string value) => CreationInputsChanged();
    partial void OnCreationTextChanged(string value) => CreationInputsChanged();
    partial void OnCreationSourceChanged(string value) => CreationInputsChanged();
    partial void OnCreationReasonChanged(string value) => CreationInputsChanged();
    partial void OnRelationBaselineChanged(string value) => CreationInputsChanged();
    partial void OnRelationCriteriaChanged(string value) => CreationInputsChanged();
    partial void OnRelationOrderChanged(string value) => CreationInputsChanged();
}
