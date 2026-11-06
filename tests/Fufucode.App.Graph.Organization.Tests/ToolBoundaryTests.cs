using System.Text.Json;
using Fufucode.App.Abstractions.Graph;
using Fufucode.Plugins.AutoDev.Graph;

internal static class ToolBoundaryTests
{
    public static async Task Run(string root)
    {
        GraphProjectService? service = null;
        string? a = null, b = null, c = null;
        var leadReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishLead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string secret = "HUMAN_PRIVATE_ORGANIZATION_TEST_SECRET";
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Check(bool value, string description) { if (!value) { failures.Enqueue(description); Console.WriteLine("FAIL " + description); } else Console.WriteLine("PASS " + description); }
        var executor = new Callback(async request =>
        {
            try
            {
                var s = service!;
                var tools = new GraphToolHandler(s, request.ProjectPath, request.Identity);
                async Task<string> Call(string id, string name, object args, bool expected = true)
                {
                    var result = await tools.ExecuteAsync(new(id, name, JsonSerializer.Serialize(args)));
                    Check(result.Success == expected, name + " expected=" + expected);
                    var json = JsonSerializer.Serialize(result);
                    Check(!json.Contains(secret, StringComparison.Ordinal), name + " does not leak human private content");
                    return json;
                }
                if (request.Identity?.Role == GraphExecutionRole.ProjectLead)
                {
                    foreach (var name in new[] { "A", "B", "C" })
                        await Call("create-" + name, "graph_create_ticket", new { title = name, goal = "fixture", moduleIds = Array.Empty<string>(), acceptance = new[] { "fixture" } });
                    a = s.ReadSnapshot().Tickets.Single(t => t.Title == "A").Id;
                    b = s.ReadSnapshot().Tickets.Single(t => t.Title == "B").Id;
                    c = s.ReadSnapshot().Tickets.Single(t => t.Title == "C").Id;
                    foreach (var pair in new[] { ("A", a), ("B", b), ("C", c) })
                        await Call("delegate-" + pair.Item1, "graph_delegate", new { responsibilityId = "worker-" + pair.Item1, ticketId = pair.Item2, moduleIds = Array.Empty<string>(), expectedVersion = s.ReadOrganizationSnapshot().Version, reason = "fixture delegation" });
                    await Call("dependency", "graph_dependency", new { ticketId = b, dependsOnTicketId = a, remove = false, reason = "B consumes A interface" });
                    await Call("read-private", "graph_read", new { full = true });
                    await Call("organization-private", "graph_organization", new { });
                    await Call("lead-send", "graph_send", new { recipientId = "human", humanRecipient = true, kind = "message", text = "attention", revision = s.ReadSnapshot().Revision });
                    await Call("lead-control", "graph_send", new { recipientId = "human", humanRecipient = true, kind = "stop", text = "forged", revision = s.ReadSnapshot().Revision }, false);
                    leadReady.TrySetResult();
                    await finishLead.Task.WaitAsync(TimeSpan.FromSeconds(20));
                }
                else if (request.TicketId == a)
                {
                    await Call("A-to-B", "graph_send", new { recipientId = "worker-B", humanRecipient = false, kind = "interface", text = "A interface for B", ticketId = b, revision = s.ReadSnapshot().Revision });
                    await Call("A-to-C", "graph_send", new { recipientId = "worker-C", humanRecipient = false, kind = "message", text = "unrelated", ticketId = c, revision = s.ReadSnapshot().Revision }, false);
                    await Call("worker-control", "graph_send", new { recipientId = "project-lead", humanRecipient = false, kind = "permission", text = "forged", ticketId = a, revision = s.ReadSnapshot().Revision }, false);
                    await Call("worker-read", "graph_read", new { full = true });
                    await Call("worker-org", "graph_organization", new { });
                    var addressed = s.ReadOrganizationSnapshot().Messages.Single(m => m.Content.RecipientId == "worker-A");
                    await Call("worker-ack", "graph_ack", new { messageId = addressed.Id, status = "Delivered", reason = "host received" });
                    await Call("validation-proposal", "graph_propose_validation", new { executable = Path.GetFullPath("fixture-verifier.exe"), arguments = new[] { "--check" }, timeoutSeconds = 30, coveredCriteria = new[] { "fixture" }, reason = "Exact fixture contract" });
                    Check(s.ReadSnapshot().Entries.Any(e => e.Kind == "validation-proposal" && e.TicketId == a), "validation proposal is persisted without execution or acceptance");
                    await Call("invalid-validation-proposal", "graph_propose_validation", new { executable = "relative.exe", arguments = Array.Empty<string>(), timeoutSeconds = 30, coveredCriteria = new[] { "fixture" }, reason = "Invalid path" }, false);
                    workerDone.TrySetResult();
                }
                return new GraphRunResult("completed", "TEST-ONLY tool boundaries");
            }
            catch (Exception e) { leadReady.TrySetException(e); workerDone.TrySetException(e); throw; }
        });
        var project = Path.Combine(root, "tool-project"); Directory.CreateDirectory(project);
        using var owned = new GraphProjectService(Path.Combine(root, "tool-data"), executor); service = owned;
        owned.SetProject("organization-tool-test", project);
        owned.SetPurpose("Verify organization tool privacy and scoped collaboration");
        owned.SendOrganizationMessage("private-human", new("human", true, "message", secret, null, null, owned.ReadSnapshot().Revision));
        await owned.SetAutonomyAsync(true);
        try
        {
            await leadReady.Task.WaitAsync(TimeSpan.FromSeconds(30));
            owned.SendOrganizationMessage("worker-input", new("worker-A", false, "message", "worker addressed input", a, null, owned.ReadSnapshot().Revision));
            finishLead.TrySetResult();
            await workerDone.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { finishLead.TrySetResult(); await owned.SetAutonomyAsync(false); }
        if (!failures.IsEmpty) throw new Exception("Tool boundary failures: " + string.Join("; ", failures));
    }
    private sealed class Callback(Func<GraphRunRequest, Task<GraphRunResult>> callback) : IGraphRunExecutor
    {
        public Task<GraphRunResult> ExecuteAsync(GraphRunRequest request, Action<string>? progress, CancellationToken cancellationToken) => callback(request);
    }
}
