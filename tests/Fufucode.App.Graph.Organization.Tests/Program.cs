using Fufucode.App.Abstractions.Graph;
using Fufucode.Plugins.AutoDev.Graph;

var root = Path.Combine("C:/tmp/work/2026-11-05-graph-report/domain", "organization-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "project"));
var data = Path.Combine(root, "data"); var project = Path.Combine(root, "project");
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { try { action(); } catch (Exception e) when (e is InvalidOperationException or ArgumentException) { Console.WriteLine("PASS reject " + name); return; } throw new Exception("Expected rejection: " + name); }
string ticket;
using (var s = new GraphProjectService(data, new NeverExecutor()))
{
    s.SetProject("organization-test", project);
    var initial = s.ReadOrganizationSnapshot();
    Check(initial.Responsibilities.Single().Id == "project-lead", "stable default lead");
    ticket = s.CreateTicket("one", "goal", [], ["criterion"]).Id;
    var other = s.CreateTicket("two", "goal", [], ["criterion"]).Id;
    var members = new[] { initial.Responsibilities.Single(), new GraphResponsibility("worker", GraphExecutionRole.Executor, [], ticket) };
    var updated = s.ApplyOrganization("org", 0, "delegation", members, ["worker"]);
    Check(updated.Version == 1, "organization version");
    Check(s.ApplyOrganization("org", 0, "delegation", members, ["worker"]).Version == 1, "idempotent replay");
    Reject(() => s.ApplyOrganization("org", 0, "different", members, ["worker"]), "command conflict");
    Reject(() => s.ApplyOrganization("stale", 0, "stale", members, []), "CAS");
    s.SetWorkDependency("edge", ticket, other, false, "work order");
    Reject(() => s.SetWorkDependency("cycle", other, ticket, false, "cycle"), "dependency cycle");
    Reject(() => s.SetWorkDependency("missing", ticket, "absent", false, "missing"), "unknown dependency");
    var message = new GraphMessageDraft("human", true, "veto", "review this", ticket, null, s.ReadSnapshot().Revision);
    s.SendOrganizationMessage("send", message);
    var received = s.ReadOrganizationInbox("human", true).Single();
    Check(received.Status == GraphMessageStatus.Received, "persisted not delivered");
    Reject(() => s.CloseOrganizationBarrier("blocked", ticket), "pending mandatory message");
    Reject(() => s.AdvanceOrganizationMessage("skip", received.Id, GraphMessageStatus.Applied, "skip"), "cannot skip delivery");
    s.AdvanceOrganizationMessage("delivered", received.Id, GraphMessageStatus.Delivered, "host delivered");
    s.AdvanceOrganizationMessage("read", received.Id, GraphMessageStatus.Read, "viewed");
    s.AdvanceOrganizationMessage("applied", received.Id, GraphMessageStatus.Applied, "handled");
    s.CloseOrganizationBarrier("close", ticket);
    s.SendOrganizationMessage("late", message with { Kind = "invalidated" });
    Check(s.ReadOrganizationSnapshot().Barriers.Single().RequiresReview, "late critical event invalidates barrier");
    Reject(() => s.ReadOrganizationInbox("worker", false), "human cannot read internal inbox");
    var late = s.ReadOrganizationInbox("human", true).Last();
    s.AdvanceOrganizationMessage("late-delivered", late.Id, GraphMessageStatus.Delivered, "delivered");
    s.AdvanceOrganizationMessage("late-read", late.Id, GraphMessageStatus.Read, "read");
    Reject(() => s.AdvanceOrganizationMessage("empty-applied", late.Id, GraphMessageStatus.Applied, ""), "applied requires processing explanation");
    s.SendOrganizationMessage("control-reject", message with { Kind = "stop" });
    var control = s.ReadOrganizationInbox("human", true).Last();
    s.AdvanceOrganizationMessage("control-delivered", control.Id, GraphMessageStatus.Delivered, "delivered");
    s.AdvanceOrganizationMessage("control-read", control.Id, GraphMessageStatus.Read, "read");
    s.AdvanceOrganizationMessage("control-rejected", control.Id, GraphMessageStatus.Rejected, "disputed");
    s.AdvanceOrganizationMessage("late-applied", late.Id, GraphMessageStatus.Applied, "reviewed invalidation");
    Reject(() => s.CloseOrganizationBarrier("rejected-control-close", ticket), "rejected stop cannot clear barrier");
}
using (var reopened = new GraphProjectService(data, new NeverExecutor()))
{
    reopened.SetProject("organization-test", project);
    var state = reopened.ReadOrganizationSnapshot();
    Check(state.Dependencies.Length == 1 && state.Messages.Length == 3 && state.Barriers.Single().RequiresReview, "durable restart snapshot");
    Check(reopened.ResolveOrganizationResponsibility(ticket, GraphExecutionRole.Executor)?.Id == "worker", "delegation lookup");
    var memberA = new GraphResponsibility("patch-A", GraphExecutionRole.Verifier, [], ticket);
    var memberB = new GraphResponsibility("patch-B", GraphExecutionRole.ScopeLead, [], ticket);
    var patchA = reopened.DelegateResponsibility("patch-A", state.Version, "delegate A", memberA);
    var patchB = reopened.DelegateResponsibility("patch-B", patchA.Version, "delegate B", memberB);
    var replayA = reopened.DelegateResponsibility("patch-A", state.Version, "delegate A", memberA);
    Check(replayA.Version == patchA.Version, "patch replay after unrelated delegation returns original version");
    Check(reopened.ReadOrganizationSnapshot().Version == patchB.Version && reopened.ReadOrganizationSnapshot().Responsibilities.Any(r => r.Id == memberB.Id), "patch replay preserves later organization");
    Reject(() => reopened.DelegateResponsibility("patch-A", state.Version, "different content", memberA), "patch command content conflict");
}
await ToolBoundaryTests.Run(root);
Console.WriteLine("PASS organization vertical slice; zero model calls; sandbox " + root);
sealed class NeverExecutor : IGraphRunExecutor
{
    public Task<GraphRunResult> ExecuteAsync(GraphRunRequest request, Action<string>? progress, CancellationToken cancellationToken) => throw new Exception("No execution expected");
}
