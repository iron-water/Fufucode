using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.Core.Automation;

namespace Fufucode.App.Features.Automation;

public sealed record AutomationDecisionAction(string Label, string Option, IAsyncRelayCommand<string> Command);
public sealed record AutomationWinnerChoice(ControlPackageOutcome Outcome, string Label);

public sealed partial class AutomationControlViewModel
{
    public ObservableCollection<AutomationDecisionAction> DecisionActions { get; } = new();
    public ObservableCollection<AutomationWinnerChoice> WinnerChoices { get; } = new();
    [ObservableProperty] private AutomationWinnerChoice? _selectedWinner;
    [ObservableProperty] private string _responseText = "";
    [ObservableProperty] private string _decisionEffectText = "";
    public bool IsQuestion => SelectedDecision?.Item.Kind == "question";
    public bool IsChoice => SelectedDecision?.Item.Kind == "choice";
    public bool NeedsReviewEvidence => SelectedDecision?.Item.Kind == "review";
    public bool NeedsReason => SelectedDecision?.Item.Kind is "review" or "repair" or "reconsider" or "authorization" or "knowledge" or "restructure";
    private static readonly JsonSerializerOptions ContextOptions = new() { PropertyNameCaseInsensitive = true };
    private static T Context<T>(JsonElement json) => json.Deserialize<T>(ContextOptions) ?? throw new InvalidOperationException("待办上下文缺失。");

    private void ConfigureDecisionForm(AutomationInboxRow? row)
    {
        ResponseText = ""; SelectedWinner = null; WinnerChoices.Clear(); DecisionActions.Clear(); DecisionEffectText = "";
        OnPropertyChanged(nameof(IsQuestion)); OnPropertyChanged(nameof(IsChoice));
        OnPropertyChanged(nameof(NeedsReviewEvidence)); OnPropertyChanged(nameof(NeedsReason));
        if (row is null) return;
        using var json = JsonDocument.Parse(row.Item.ContextJson); var root = json.RootElement;
        var package = Field(root, "package");
        switch (row.Item.Kind)
        {
            case "knowledge":
                var proposal = Context<ControlKnowledgeProposal>(root);
                DetailText = $"{row.Title}\n知识 {proposal.Key} · 基于版本 {proposal.PreviousVersion}\n{proposal.Text}\n来源 {proposal.Source}\n提出者 {proposal.ProposedBy}";
                DecisionEffectText = "发布保留原文与来源；采用到任务规范需另行决定。拒绝必须填写理由。";
                break;
            case "question":
                var question = Context<ControlQuestionState>(root);
                DetailText = $"{row.Title}\n从 {question.FromPackage} · 版本 {question.FromVersion}\n向 {question.ToPackage} · 版本 {question.ToVersion}\n{question.Text}\n提问者 {question.AskedBy}";
                DecisionEffectText = "记录你的回答，保留问答双方的规范版本；不自动转成知识或修改任务。";
                break;
            case "adoption":
                var adoption = Context<ControlKnowledgeAdoption>(Field(root, "adoption"));
                var fact = Context<ControlKnowledgeFact>(Field(root, "fact"));
                DetailText = $"{row.Title}\n任务规范 {adoption.PackageId} · 版本 {adoption.PackageVersion}\n目标 {Value(Field(package, "Spec"), "Goal")}\n知识 {fact.Key} · 版本 {fact.Version}\n{fact.Text}\n来源 {fact.Source}\n发布者 {fact.PublishedBy}";
                DecisionEffectText = "采用会生成并批准新的任务规范版本。不会启动执行、接受交付或记录集成。";
                break;
            case "choice":
                var group = Context<ControlGroupState>(root);
                DetailText = $"{row.Title}\n共同基准 {group.Spec.Baseline}\n比较依据\n{string.Join("\n", group.Spec.Criteria ?? [])}";
                foreach (var member in group.Members)
                {
                    var contract = Snapshot!.State.Control.Packages.Single(p => p.Spec.Id == member.PackageId);
                    DetailText += $"\n候选 {member.PackageId} · 版本 {member.Version} · {contract.Spec.Title}\n任务 {member.TaskId} · " + (member.Accepted ? "已接受" : "尚未接受") + (member.Integrated ? " · 已集成" : " · 未集成");
                    if (member.Approved && member.Accepted && member.TaskId is not null && !member.Integrated)
                        WinnerChoices.Add(new(member, $"{contract.Spec.Title} · {member.PackageId} · v{member.Version}"));
                }
                DecisionEffectText = "明确选择一个已接受的候选；Core再次核查共同基准和当前证据。选择不代表集成。";
                break;
            case "restructure":
                var restructure = Context<ControlRestructureState>(root);
                DetailText = $"{row.Title}\n重组理由 {restructure.Spec.Reason}\n提出者 {restructure.ProposedBy}";
                foreach (var source in restructure.SourceContracts)
                    DetailText += "\n原规范\n" + DescribeContract(source.Spec, source.Version);
                foreach (var replacement in restructure.Spec.Replacements)
                    DetailText += "\n后继规范（尚未批准）\n" + DescribeContract(replacement, null);
                foreach (var mapping in restructure.Spec.GoalMappings)
                    DetailText += $"\n目标映射 {mapping.SourceId} → {string.Join("、", mapping.SuccessorIds)}\n{mapping.Reason}";
                foreach (var relation in restructure.Spec.Groups)
                    DetailText += $"\n后继关系 {relation.Id} · {relation.Relation} · {string.Join("、", relation.Members)}\n基准 {relation.Baseline}\n依据 {string.Join("、", relation.Criteria ?? [])}";
                foreach (var mapping in restructure.Spec.GroupMappings)
                    DetailText += $"\n关系映射 {mapping.SourceGroupId} → {string.Join("、", mapping.SuccessorGroupIds)}\n{mapping.Reason}";
                DecisionEffectText = "批准会退役原规范并创建后继规范。原历史保留，接受、集成与执行身份不自动继承。拒绝必须填写理由。";
                break;
        }
        if (row.Item.BlockedReason is not null) DetailText += "\n" + row.Availability;
        foreach (var option in row.Item.Options)
            if (ActionLabel(row.Item.Kind, option) is { } label) DecisionActions.Add(new(label, option, DecideCommand));
    }

    private static string DescribeContract(WorkPackageSpec spec, long? version) =>
        $"{spec.Id} · {spec.Title}" + (version is null ? "" : $" · 版本 {version}") +
        $"\n目标 {spec.Goal}\n范围 {string.Join("、", spec.Scope)}\n验收 {string.Join("、", spec.Acceptance)}\n{spec.Harness} · {spec.Model} · {spec.Artifact}\n依赖 {string.Join("、", spec.DependsOn)}\n知识引用 {string.Join("、", spec.KnowledgeRefs ?? [])}";

    private static string? ActionLabel(string kind, string option) => (kind, option) switch
    {
        ("plan", "approve") => "批准计划", ("review", "accept") => "接受", ("review", "rework") => "打回",
        ("review", "blocked") => "阻塞", ("review", "defer") => "暂缓", ("reconsider", "reopen") => "重新审核",
        ("repair", "request_repair") => "批准返工", ("integration", "authorize") => "授权集成", ("authorization", "revoke") => "撤销授权",
        ("knowledge", "publish") => "发布知识", ("knowledge", "reject") => "拒绝提案", ("question", "answer") => "记录回答",
        ("adoption", "adopt") => "采用到新规范", ("choice", "select") => "选择此候选",
        ("restructure", "approve") => "批准重组", ("restructure", "reject") => "拒绝重组", _ => null
    };

    private object BuildAdvancedDecision(AutomationStore store, ControlDecisionItem item, JsonElement root,
        string id, long revision, string actor, string option, string reason, long now)
    {
        // Capture every typed field before starting the worker. Later selection/project changes cannot alter it.
        switch (item.Kind)
        {
            case "knowledge":
                var proposal = Context<ControlKnowledgeProposal>(root);
                var publication = new ControlKnowledgeCommand(id, revision, actor,
                    option == "publish" ? ControlKnowledgeAction.Publish : ControlKnowledgeAction.RejectProposal,
                    ExpectedKnowledgeVersion: proposal.PreviousVersion, ProposalId: proposal.Id, Reason: reason);
                return publication;
            case "question":
                var question = Context<ControlQuestionState>(root);
                var answer = new ControlKnowledgeCommand(id, revision, actor, ControlKnowledgeAction.Answer,
                    Text: ResponseText, ExpectedFromVersion: question.FromVersion, ExpectedToVersion: question.ToVersion, QuestionId: question.Id);
                return answer;
            case "adoption":
                var adoption = Context<ControlKnowledgeAdoption>(Field(root, "adoption"));
                var adoptionCommand = new ControlKnowledgeCommand(id, revision, actor, ControlKnowledgeAction.Adopt,
                    Key: adoption.Key, ExpectedKnowledgeVersion: adoption.KnowledgeVersion)
                    { PackageId = adoption.PackageId, ExpectedPackageVersion = adoption.PackageVersion };
                return adoptionCommand;
            case "choice":
                var winner = SelectedWinner!.Outcome;
                var selection = new ControlRelationCommand(id, revision, actor, ControlRelationAction.SelectWinner,
                    GroupId: item.ObjectId, Winner: winner.PackageId, ExpectedEvidenceToken: winner.EvidenceToken);
                return selection;
            case "restructure":
                var decision = new ControlRestructureCommand(id, revision, actor,
                    option == "approve" ? ControlRestructureAction.Approve : ControlRestructureAction.Reject, ProposalId: item.ObjectId, Reason: reason);
                return decision;
            default: throw new InvalidOperationException("此待办由主控处理。");
        }
    }
    partial void OnResponseTextChanged(string value) => NotifyCommands();
    partial void OnSelectedWinnerChanged(AutomationWinnerChoice? value) => NotifyCommands();
}
