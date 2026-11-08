using Fufucode.App.Abstractions.Graph;
using Fufucode.Plugins.AutoDev.Graph;
using Microsoft.Data.Sqlite;
using System.Text.Json;

var root = Path.Combine("C:/tmp/work/2026-11-08-autodev-core/communication-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "project"));
var data = Path.Combine(root, "data"); var project = Path.Combine(root, "project");
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { try { action(); } catch (Exception e) when (e is InvalidOperationException or ArgumentException) { Console.WriteLine("PASS reject " + name); return; } throw new Exception("Expected rejection: " + name); }
string ticket, other;
using (var s = new GraphProjectService(data, new NeverExecutor()))
{
    s.SetProject("organization-test", project);
    var initial = s.ReadOrganizationSnapshot();
    Check(initial.Responsibilities.Single().Id == "project-lead", "stable default lead");
    ticket = s.CreateTicket("one", "goal", [], ["criterion"]).Id;
    other = s.CreateTicket("two", "goal", [], ["criterion"]).Id;
    var members = new[] { initial.Responsibilities.Single(), new GraphResponsibility("worker", GraphExecutionRole.Executor, [], ticket) };
    var updated = s.ApplyOrganization("org", 0, "delegation", members, ["worker"]);
    Check(updated.Version == 1, "organization version");
    Check(s.ApplyOrganization("org", 0, "delegation", members, ["worker"]).Version == 1, "idempotent replay");
    Reject(() => s.ApplyOrganization("org", 0, "different", members, ["worker"]), "command conflict");
    Reject(() => s.ApplyOrganization("stale", 0, "stale", members, []), "CAS");
    s.SetWorkDependency("edge", ticket, other, false, "work order");
    Reject(() => s.SetWorkDependency("cycle", other, ticket, false, "cycle"), "dependency cycle");
    Reject(() => s.SetWorkDependency("missing", ticket, "absent", false, "missing"), "unknown dependency");
    var message = new GraphMessageDraft("human", true, "interface", "sender claim, not host evidence", ticket, null, s.ReadSnapshot().Revision);
    s.SendOrganizationMessage("send", message);
    var received = s.ReadOrganizationInbox("human", true).Single();
    Check(received.Content.Communication == GraphCommunicationKind.Notify, "legacy API defaults to sourced Notify");
    Reject(() => s.AdvanceOrganizationMessage("ack-notify", received.Id, GraphMessageStatus.Delivered, "read"), "Notify has no acknowledgement state machine");
    s.CloseOrganizationBarrier("close-unread", ticket);
    foreach (var kind in new[] { "interface", "invalidated", "ownership", "veto", "stop", "permission" })
        s.SendOrganizationMessage("late-" + kind, message with { Kind = kind });
    Check(!s.ReadOrganizationSnapshot().Barriers.Single().RequiresReview, "topic names never invalidate completion/evidence");
    Check(s.ReadOrganizationSnapshot().Controls.Length == 0, "control-sounding claims create no Kernel authority");
    s.SendOrganizationMessage("request", message with { Communication = GraphCommunicationKind.Request, Kind = "question" });
    var request = s.ReadOrganizationInbox("human", true).Last();
    Check(request.RequestStatus == GraphRequestStatus.Open, "Request begins Open");
    s.CloseOrganizationBarrier("close-open-request", ticket);
    s.ResolveOrganizationRequest("resolve", request.Id, GraphRequestStatus.Resolved, "answer");
    Check(s.ReadOrganizationInbox("human", true).Last().RequestStatus == GraphRequestStatus.Resolved, "Request resolves without delivery/read handshake");
    Reject(() => s.ResolveOrganizationRequest("reopen", request.Id, GraphRequestStatus.Open, "reopen"), "terminal request cannot reopen");
    s.SendOrganizationMessage("cancel-request", message with { Communication = GraphCommunicationKind.Request });
    s.ResolveOrganizationRequest("cancel", s.ReadOrganizationInbox("human", true).Last().Id, GraphRequestStatus.Cancelled, "no longer needed");
    s.SendOrganizationMessage("decline-request", message with { Communication = GraphCommunicationKind.Request });
    s.ResolveOrganizationRequest("decline", s.ReadOrganizationInbox("human", true).Last().Id, GraphRequestStatus.Declined, "cannot answer");
    Reject(() => s.SendOrganizationMessage("forged", message with { Communication = GraphCommunicationKind.Control }), "Control cannot enter through message API");
    var semantic = message with { RecipientId = "", HumanRecipient = false, Address = new(other, Role: GraphExecutionRole.Executor), Communication = GraphCommunicationKind.Request };
    s.SendOrganizationMessage("unassigned", semantic);
    Reject(() => s.ResolveOrganizationRequest("nonrecipient-resolution", s.ReadOrganizationSnapshot().Messages.Last().Id,
        GraphRequestStatus.Resolved, "not mine"), "sender cannot resolve another responsibility's Request");
    Check(s.ResolveOrganizationRecipients(semantic.Address!).Length == 0, "no active recipient stays unassigned, not Lead fallback");
    s.DelegateResponsibility("assign-later", s.ReadOrganizationSnapshot().Version, "later assignment", new("worker-two", GraphExecutionRole.Executor, [], other));
    Check(s.ResolveOrganizationRecipients(semantic.Address!).SequenceEqual(["worker-two"]), "semantic ticket-role follows persistent responsibility");
    Check(s.ResolveOrganizationRecipients(new(Role: GraphExecutionRole.Executor)).Length == 2, "role addressing deterministically resolves multiple recipients");
    var module = s.UpsertModule(null, null, "Subsystem", "fixture", []);
    s.DelegateResponsibility("module-owner", s.ReadOrganizationSnapshot().Version, "module role", new("scope", GraphExecutionRole.ScopeLead, [module.Id]));
    Check(s.ResolveOrganizationRecipients(new(ModuleId: module.Id, Role: GraphExecutionRole.ScopeLead)).SequenceEqual(["scope"]), "module-role semantic addressing");
    var childModule = s.UpsertModule(null, module.Id, "Nested subsystem", "recursive scope fixture", []);
    Check(s.ResolveOrganizationRecipients(new(ModuleId: childModule.Id, Role: GraphExecutionRole.ScopeLead)).SequenceEqual(["scope"]),
        "module semantic addressing respects recursive delegated subtree ownership");
    Reject(() => s.SendOrganizationMessage("unknown-address", semantic with { Address = new("missing") }), "cross-project or unknown ticket rejected");
    Reject(() => s.SendOrganizationMessage("mixed-address", semantic with { RecipientId = "project-lead" }), "mixed physical/semantic address rejected");
    Reject(() => s.ReadOrganizationInbox("worker", false), "human cannot read internal inbox");
    var controlState = s.IssueOrganizationControl("real-stop", ticket, GraphControlKind.Stop, "human stop");
    var control = controlState.Controls.Single();
    Check(control.DecisionId.StartsWith("veto-"), "Control carries actual Kernel decision");
    Check(s.ReadSnapshot().Tickets.Single(t => t.Id == ticket).Status.ToString() == "ReplanRequired", "unread Control immediately changes Kernel ticket state");
    Reject(() => s.CloseOrganizationBarrier("blocked-real", ticket), "unread Kernel Control blocks target completion");
    s.CloseOrganizationBarrier("unrelated", other);
    Check(s.IssueOrganizationControl("real-stop", ticket, GraphControlKind.Stop, "human stop").Controls.Length == 1, "Control replay does not duplicate decision");
    s.RevokeOrganizationControl("revoke-stop", control.Id, "human revocation");
    s.CloseOrganizationBarrier("after-revoke", ticket);
    Check(s.ReadOrganizationSnapshot().Controls.Single().Status == GraphControlStatus.Revoked, "explicit revocation lifts Kernel veto");
}
using (var reopened = new GraphProjectService(data, new NeverExecutor()))
{
    reopened.SetProject("organization-test", project);
    var state = reopened.ReadOrganizationSnapshot();
    Check(state.Messages.Length == 11 && state.Controls.Single().Status == GraphControlStatus.Revoked, "durable typed communication restart");
    Check(reopened.ResolveOrganizationResponsibility(ticket, GraphExecutionRole.Executor)?.Id == "worker", "delegation lookup");
    var memberA = new GraphResponsibility("patch-A", GraphExecutionRole.Verifier, [], ticket);
    var memberB = new GraphResponsibility("patch-B", GraphExecutionRole.ScopeLead, [], ticket);
    var patchA = reopened.DelegateResponsibility("patch-A", state.Version, "delegate A", memberA);
    var patchB = reopened.DelegateResponsibility("patch-B", patchA.Version, "delegate B", memberB);
    Check(reopened.DelegateResponsibility("patch-A", state.Version, "delegate A", memberA).Version == patchA.Version, "patch replay original version");
    Check(reopened.ReadOrganizationSnapshot().Version == patchB.Version, "patch replay preserves later organization");
}
// Rewrite the additive envelope to an actual old persisted shape, including its old command hash.
var database = Directory.GetFiles(Path.Combine(data, "auto-dev", "rebirth"), "kernel.db", SearchOption.AllDirectories).Single();
using (var connection = new SqliteConnection("Data Source=" + database))
{
    connection.Open();
    using var select = connection.CreateCommand();
    select.CommandText = "SELECT state_json FROM graph_organization";
    var legacy = System.Text.Json.Nodes.JsonNode.Parse((string)select.ExecuteScalar()!)!;
    legacy.AsObject().Remove("controls");
    foreach (var item in legacy["messages"]!.AsArray())
    {
        item!.AsObject().Remove("requestStatus");
        item["content"]!.AsObject().Remove("communication");
        item["content"]!.AsObject().Remove("address");
    }
    foreach (var barrier in legacy["barriers"]!.AsArray()) barrier!["requiresReview"] = true;
    using var update = connection.CreateCommand();
    update.CommandText = "UPDATE graph_organization SET state_json=$json";
    update.Parameters.AddWithValue("$json", legacy.ToJsonString()); update.ExecuteNonQuery();
    // Select the original seven-field draft and reproduce its pre-upgrade hash exactly.
    var content = legacy["messages"]![0]!["content"]!.DeepClone();
    var payload = new System.Text.Json.Nodes.JsonObject { ["payload"] = content, ["identity"] = null }.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    using var receipt = connection.CreateCommand();
    receipt.CommandText = "UPDATE graph_organization_commands SET payload_hash=$hash,result_json=$json WHERE command_id='send'";
    receipt.Parameters.AddWithValue("$hash", hash); receipt.Parameters.AddWithValue("$json", legacy.ToJsonString()); receipt.ExecuteNonQuery();
}
using (var legacy = new GraphProjectService(data, new NeverExecutor()))
{
    legacy.SetProject("organization-test", project);
    var state = legacy.ReadOrganizationSnapshot();
    Check(state.Messages.All(m => m.Content.Communication == GraphCommunicationKind.Notify), "old JSON without channel fields remains readable Notify");
    Check(state.Barriers.All(b => !b.RequiresReview), "old message-derived review flags cannot survive as authority");
    var original = state.Messages.First().Content;
    Check(legacy.SendOrganizationMessage("send", original).Messages.Length == 11, "pre-upgrade command hash replay compatible");
    legacy.CloseOrganizationBarrier("legacy-close", ticket);
}
await ToolBoundaryTests.Run(root);
Console.WriteLine("PASS organization communication; zero model calls; sandbox " + root);
sealed class NeverExecutor : IGraphRunExecutor
{
    public Task<GraphRunResult> ExecuteAsync(GraphRunRequest request, Action<string>? progress, CancellationToken cancellationToken) => throw new Exception("No execution expected");
}
