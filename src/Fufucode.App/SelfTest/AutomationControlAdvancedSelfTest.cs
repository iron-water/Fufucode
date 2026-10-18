using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Fufucode.App.Features.Automation;
using Fufucode.Core.Automation;

namespace Fufucode.App.SelfTest;

internal static partial class AutomationControlSelfTest
{
    private static async Task RunAdvancedAsync(AutomationStore store, AutomationControlViewModel model,
        AutomationControlPageView view, Window window, Action<bool, string> check)
    {
        var serial = 0; string Id() => "advanced-" + ++serial;
        long Rev() => store.ReadControlSnapshot().Revision;
        long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        WorkPackageSpec Spec(string id) => new(id, "same", "Keep notification policy", ["fixture.txt"], ["Policy evidence retained"], "codex", "devin/swe-2", "evidence", []) { KnowledgeRefs = ["notifications"] };
        void Define(string id, long prior = 0) => store.ApplyControlCommand(new(Id(), Rev(), "fixture", ControlAction.DefinePackage, Spec(id), ExpectedPackageVersion: prior), Now());
        void Approve(string id) => store.ApplyControlCommand(new(Id(), Rev(), "fixture", ControlAction.ApprovePackage, PackageId: id, ExpectedPackageVersion: 1), Now());
        string Propose(string text, long previous = 0)
        {
            var id = Id(); store.ApplyControlKnowledge(new(id, Rev(), "fixture", ControlKnowledgeAction.Propose,
                Key: "notifications", Text: text, Source: "fixture policy document", ExpectedKnowledgeVersion: previous), Now()); return id;
        }
        async Task Select(string kind, string? id = null)
        {
            await Until(() => !model.IsBusy);
            await model.RefreshAsync(); model.SelectedDecision = model.Inbox.Single(r => r.Item.Kind == kind && (id is null || r.Item.ObjectId == id)); await Paint(window);
        }
        var published = Propose("Three notification destinations; status bar defaults off.");
        var obsolete = Propose("Obsolete competing notification policy");
        Define("knowledge-target"); Approve("knowledge-target"); Define("question-peer");
        await Select("knowledge", published);
        check(model.DetailText.Contains("Three notification") && model.DetailText.Contains("fixture policy document") && model.DetailText.Contains("版本 0"),
            "knowledge form shows exact proposed text, evidence source, and prior version");
        check(model.DecisionActions.Select(a => a.Option).SequenceEqual(["publish", "reject"]) && !model.DecideCommand.CanExecute("reject"),
            "knowledge form exposes only publish/reject and requires rejection reason");
        await PressDecision(window, view, "发布知识");
        var current = store.ReadControlSnapshot();
        check(current.Knowledge.Facts.Single().Version == 1 && current.Packages.Single(p => p.Spec.Id == "knowledge-target").Version == 1 &&
            current.Packages.Single(p => p.Spec.Id == "knowledge-target").AdoptedKnowledge.Count == 0,
            "native publication preserves immutable fact and does not automatically adopt it");
        var publicationRevision = Rev(); await model.RetryReceiptAsync(); check(Rev() == publicationRevision, "publication returns its original receipt without a second fact");
        await Select("knowledge", obsolete);
        check(model.DecisionActions.Single().Option == "reject" && !model.DecideCommand.CanExecute("publish") && model.DetailText.Contains("只能拒绝"),
            "obsolete fact proposal shows the restriction and cannot publish over its competitor");
        model.ReasonText = "A current version was already published"; await PressDecision(window, view, "拒绝提案");
        check(store.ReadControlSnapshot().Knowledge.Proposals.Single(p => p.Id == obsolete).Reason == "A current version was already published", "native obsolete proposal rejection retains human reason");
        await Select("adoption", "knowledge-target");
        check(model.DetailText.Contains("Keep notification policy") && model.DetailText.Contains("知识 notifications · 版本 1"), "adoption shows target contract and exact fact version");
        await PressDecision(window, view, "采用到新规范");
        current = store.ReadControlSnapshot(); var target = current.Packages.Single(p => p.Spec.Id == "knowledge-target");
        check(target.Version == 2 && target.Approved && target.AdoptedKnowledge["notifications"] == 1 && current.Knowledge.Adoptions.Single(a => a.PackageId == "knowledge-target").Status == "adopted",
            "native adoption versions and approves only the named contract");
        check(model.Snapshot!.Graph!.Threads.Count == 2 && store.GetPart("a-one").Status == DeliveryPartStatus.Accepted && !store.IsIntegrated("a-one", 1),
            "knowledge decisions neither create executions nor alter accepted deliveries");
        var questionId = Id();
        store.ApplyControlKnowledge(new(questionId, Rev(), "fixture", ControlKnowledgeAction.Ask, Text: "Which policy applies?",
            FromPackage: "question-peer", ToPackage: "knowledge-target", ExpectedFromVersion: 1, ExpectedToVersion: 2), Now());
        await Select("question", questionId);
        var response = view.FindControl<TextBox>("ResponseInput")!;
        check(response.IsVisible && model.DetailText.Contains("question-peer · 版本 1") && model.DetailText.Contains("knowledge-target · 版本 2") && !model.DecideCommand.CanExecute("answer"),
            "question form names both contract versions and refuses an empty answer");
        response.Text = "Keep all three routes. 中文😀";
        check(model.ResponseText == response.Text && model.DecideCommand.CanExecute("answer"), "compiled answer input binds Unicode text to the explicit answer command");
        await model.RefreshAsync(); check(model.ResponseText == "Keep all three routes. 中文😀", "unchanged refresh preserves an in-progress answer");
        window.Width = 340; await Paint(window); response.BringIntoView(); await Paint(window);
        var responseOrigin = response.TranslatePoint(default, window)!.Value;
        check(response.Bounds.Width > 0 && response.Bounds.Width <= 316 && response.IsVisible && responseOrigin.Y >= 0 && responseOrigin.Y + response.Bounds.Height <= window.ClientSize.Height,
            "answer form is actually visible inside a narrow floating sidebar viewport");
        using (var image = window.CaptureRenderedFrame()) image?.Save(Path.Combine(Program.SelfTestDataRoot!, "automation", "native-question.png"), PngBitmapEncoderOptions.Default);
        await PressDecision(window, view, "记录回答");
        window.Width = 980; model.SelectTab("overview"); await Paint(window);
        using (var image = window.CaptureRenderedFrame()) image?.Save(Path.Combine(Program.SelfTestDataRoot!, "automation", "v4-overview.png"), PngBitmapEncoderOptions.Default);
        model.SelectTab("activity"); await Paint(window);
        check(store.ReadControlSnapshot().Knowledge.Questions.Single(q => q.Id == questionId) is { Answer: "Keep all three routes. 中文😀", AnsweredBy: "desktop-user" },
            "focused native button records the exact human answer rather than publishing knowledge");
        var answerRevision = Rev(); await model.RetryReceiptAsync(); check(Rev() == answerRevision, "answer replay retains original answer without duplicate event");
        var staleQuestion = Id();
        store.ApplyControlKnowledge(new(staleQuestion, Rev(), "fixture", ControlKnowledgeAction.Ask, Text: "Old contract question",
            FromPackage: "question-peer", ToPackage: "knowledge-target", ExpectedFromVersion: 1, ExpectedToVersion: 2), Now());
        await Select("question", staleQuestion); response.Text = "Answer against stale contract";
        await PressDecision(window, view, "记录回答", () => Define("question-peer", 1));
        check(store.ReadControlSnapshot().Knowledge.Questions.Single(q => q.Id == staleQuestion).Answer is null && model.StatusText.Contains("操作未完成"),
            "an external version change refuses the captured native answer without silently rebasing it");
        await Select("question", staleQuestion);
        check(!model.SelectedDecision!.Item.Actionable && model.DecisionActions.Count == 0 && !model.DecideCommand.CanExecute("answer"), "refreshed obsolete question cannot be answered as a new contract");

        Define("reshape-old");
        ControlRestructureSpec Restructure(string successor) => new(new Dictionary<string, long> { ["reshape-old"] = 1 }, [Spec(successor)],
            [new("reshape-old", [successor], "Keep notification policy and evidence")], [], [], "Separate bounded successor");
        var restructure = store.ApplyControlRestructure(new(Id(), Rev(), "fixture", ControlRestructureAction.Propose, Restructure("reshape-new")), Now());
        await Select("restructure", restructure.ProposalId);
        check(model.DetailText.Contains("原规范") && model.DetailText.Contains("后继规范") && model.DetailText.Contains("reshape-old → reshape-new") && model.DetailText.Contains("Policy evidence retained"),
            "restructure form shows original goals, replacement scope/acceptance, and explicit provenance mapping");
        await PressDecision(window, view, "批准重组");
        current = store.ReadControlSnapshot();
        check(current.Packages.Single(p => p.Spec.Id == "reshape-old").Superseded && !current.Packages.Single(p => p.Spec.Id == "reshape-new").Approved &&
            store.GetPart("a-one").Status == DeliveryPartStatus.Accepted && model.Snapshot!.Graph!.Threads.Count == 2,
            "native restructure retires only its original contract without inventing successor execution or inherited acceptance");
        var restructureRevision = Rev(); await model.RetryReceiptAsync(); check(Rev() == restructureRevision, "restructure replay does not recreate successor contracts");
        Define("reshape-stale");
        var staleSpec = new ControlRestructureSpec(new Dictionary<string, long> { ["reshape-stale"] = 1 }, [Spec("stale-successor")],
            [new("reshape-stale", ["stale-successor"], "Keep explicit goal")], [], [], "Obsolete route");
        var staleRestructure = store.ApplyControlRestructure(new(Id(), Rev(), "fixture", ControlRestructureAction.Propose, staleSpec), Now());
        await Select("restructure", staleRestructure.ProposalId);
        await PressDecision(window, view, "批准重组", () => Define("reshape-stale", 1));
        check(store.ReadControlSnapshot().Restructures.Single(r => r.Id == staleRestructure.ProposalId).Status == "pending" && !store.ReadControlSnapshot().Packages.Any(p => p.Spec.Id == "stale-successor"),
            "stale native restructure approval writes no successor");
        await Select("restructure", staleRestructure.ProposalId);
        check(model.DecisionActions.Single().Option == "reject" && !model.DecideCommand.CanExecute("approve"), "obsolete restructure retains only Core's rejection route");
        model.ReasonText = "The source contract changed"; await PressDecision(window, view, "拒绝重组");
        check(store.ReadControlSnapshot().Restructures.Single(r => r.Id == staleRestructure.ProposalId).DecisionReason == "The source contract changed", "native restructure rejection preserves its reason and original proposal");

        var secondFact = Propose("A new policy version", 1); await Select("knowledge", secondFact); await PressDecision(window, view, "发布知识");
        await Select("adoption", "knowledge-target");
        await PressDecision(window, view, "采用到新规范", () =>
        {
            var thirdFact = Propose("A newer policy version", 2);
            store.ApplyControlKnowledge(new(Id(), Rev(), "fixture", ControlKnowledgeAction.Publish, ExpectedKnowledgeVersion: 2, ProposalId: thirdFact), Now());
        });
        check(store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == "knowledge-target").Version == 2 && model.StatusText.Contains("操作未完成"),
            "stale native adoption does not substitute a newer fact into the user's old decision");
        await Select("adoption", "knowledge-target"); await PressDecision(window, view, "采用到新规范");
        check(store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == "knowledge-target").AdoptedKnowledge["notifications"] == 3,
            "only a fresh explicit adoption binds the latest fact version");
        model.ReturnToGraph(); await Paint(window);
        check(!response.IsVisible && model.DecisionActions.Count == 0 && model.WinnerChoices.Count == 0 && model.ResponseText == "",
            "return clears advanced form state without disconnecting the authority");
    }
}
