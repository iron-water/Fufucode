using System.Collections.Concurrent;
using System.Text.Json;
using Fufucode.App.Abstractions.Graph;
using Fufucode.Core.Automation.Rebirth;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;
using Fufucode.Plugins.AutoDev;
using Fufucode.Plugins.AutoDev.Graph;
using Microsoft.Data.Sqlite;

// All executors in this program are TEST-ONLY fakes. No provider, model, CLI backend or network is invoked.
internal static class Program
{
    private static readonly string Sandbox = Path.Combine("C:/tmp/work/2026-11-02-graph-domain", "test-data", Guid.NewGuid().ToString("N"));
    private static int _passed;
    public static async Task<int> Main(string[] args)
    {
        Directory.CreateDirectory(Sandbox);
        try
        {
            await Test("persistence, shared references and immutable history", Persistence);
            await Test("real scan, imports, exclusions and manual conflict", Scanner);
            await Test("legacy path-hash and host identity share one authority", Identity);
            await Test("terminal receipts, evidence and human acceptance", Acceptance);
            await Test("stop waits for the executor acknowledgement", StopReceipt);
            await Test("disable stops scheduling and waits for cancellation", Disable);
            await Test("restart marks in-flight work unknown without retry", Restart);
            await Test("autonomy plans and advances real queued follow-ups", Autonomy);
            await Test("lead failure retains pending events until explicit human recovery", LeadFailureRecovery);
            await Test("model tools enforce provenance, idempotency and conflicts", Tools);
            await Test("tools stay bound to their run project", ProjectScope);
            await Test("changed budgets cancel and await real receipts", Budget);
            await Test("in-run evidence matures only after settlement", RunEvidence);
            await Test("actual session messages preserve roles and deduplicate", SessionMessages);
            await Test("exit zero and transport failures never fake ticket completion", UnknownReceipts);
            await Test("human recovery preserves veto scope and never launches", HumanRecovery);
            await Test("large graph prompts and paged reads stay explicitly bounded", BoundedContext);
            if (args.Contains("--sgline", StringComparer.Ordinal)) await Test("SGline read-only real-project scan", Sgline);
            Console.WriteLine($"PASS {_passed} groups; TEST-ONLY fake executors, zero model calls. Evidence sandbox: {Sandbox}");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); Console.Error.WriteLine("Retained test sandbox: " + Sandbox); return 1; }
    }
    private static async Task Test(string name, Func<Task> action)
    { await action(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("Assertion failed: " + message); }
    private static void Throws(Action action, string reason)
    { try { action(); } catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException) { return; } throw new Exception("Expected rejection: " + reason); }
    private static async Task Rejects(Func<Task> action, string reason)
    { try { await action(); } catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException) { return; } throw new Exception("Expected rejection: " + reason); }
    private static (string Data, string Project) Fixture(string name)
    {
        var root = Path.Combine(Sandbox, name); var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        return (Path.Combine(root, "data"), project);
    }
    private static GraphProjectService Service((string Data, string Project) f, IGraphRunExecutor? executor = null, string host = "host-project")
    { var s = new GraphProjectService(f.Data, executor ?? new NeverExecutor()); s.SetProject(host, f.Project); return s; }
    private static GraphTicket Ticket(GraphProjectService s, string name = "real fixture work", params string[] criteria)
    {
        var ticket = s.CreateTicket(name, "Check a real test fixture, never a production task", [], criteria.Length == 0 ? ["fixture verified"] : criteria);
        var organization = s.ReadOrganizationSnapshot();
        var member = new GraphResponsibility("fixture-executor:" + ticket.Id, GraphExecutionRole.Executor, [], ticket.Id);
        s.ApplyOrganization("fixture-delegate:" + ticket.Id, organization.Version, "Explicit test fixture execution delegation",
            [..organization.Responsibilities, member], [member.Id]);
        return ticket;
    }
    private static string PlanningTicket(GraphProjectService s, string data)
    {
        using var kernel = new RebirthKernel(Database(data));
        var snapshot = s.ReadSnapshot();
        var ticket = kernel.CreateTicket("fixture-planning-" + Guid.NewGuid().ToString("N"), snapshot.ProjectId,
            "TEST-ONLY project planning", "Exercise host-issued project lead tools", ["fixture planning verified"],
            "project-planning", "project-lead", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        s.ReadSnapshot(); // Refresh the Graph projection after this explicit kernel fixture mutation.
        return ticket.Id;
    }
    private static string Database(string data) => Directory.EnumerateFiles(data, "kernel.db", SearchOption.AllDirectories).Single();
    private static long Scalar(string db, string sql)
    { using var c = new SqliteConnection("Data Source=" + db); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(q.ExecuteScalar()); }
    private static void Sql(string db, string sql)
    { using var c = new SqliteConnection("Data Source=" + db); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }

    private static async Task Persistence()
    {
        var f = Fixture("persist"); string ticketId; long old; string sharedId;
        using (var s = Service(f))
        {
            Check(s.ReadSnapshot().Modules.Count == 0 && s.ReadSnapshot().Tickets.Count == 0, "no sample graph or tickets");
            s.SetPurpose("Maintain this test fixture with shared sources");
            var root = s.UpsertModule(null, null, "Original root", "real test hierarchy", []);
            var left = s.UpsertModule(null, root.Id, "Left", "", []);
            var right = s.UpsertModule(null, root.Id, "Right", "", []);
            var shared = s.UpsertModule(null, null, "Shared", "", []); sharedId = shared.Id;
            s.UpsertRelation(null, left.Id, shared.Id, "dependency", "observed test relation");
            s.UpsertRelation(null, right.Id, shared.Id, "dependency", "observed test relation");
            var ticket = s.CreateTicket("shared fixture", "Exercise reference identity", [left.Id, shared.Id], ["verified"]); ticketId = ticket.Id;
            s.AddEntry(ticket.Id, shared.Id, "message", "Actual fixture correspondence"); s.LinkSession(ticket.Id, "test-only-session");
            old = s.ReadSnapshot().Revision;
            s.UpsertModule(root.Id, null, "Renamed root", "changed", []);
            s.RemoveRelation(s.ReadSnapshot().Relations[0].Id);
            var historical = s.ReadSnapshot(old);
            Check(historical.IsHistorical && historical.Modules.Single(m => m.Id == root.Id).Name == "Original root", "historical name belongs to old version");
            Check(historical.Relations.Count == 2 && s.ReadSnapshot().Relations.Count == 1, "historical relation set immutable");
            Check(historical.Revisions.Any(r => r.Version > old), "history retains navigation metadata");
            Throws(() => s.RemoveModule(shared.Id), "shared ticket reference survives removal attempt");
            Throws(() => s.UpsertModule(root.Id, left.Id, "cycle", "", []), "hierarchy cycles rejected");
            await s.SaveOptionsAsync(s.ReadOptions() with { ScanDepth = 2, Concurrency = 2, Provider = "test-only-provider", Model = "test-only-model" });
        }
        using (var reopened = Service(f))
        {
            var snap = reopened.ReadSnapshot();
            Check(snap.Purpose == "Maintain this test fixture with shared sources", "purpose persisted");
            Check(snap.Tickets.Single().Id == ticketId && snap.Tickets.Single().SessionId == "test-only-session", "ticket authority and session persisted");
            Check(snap.Modules.Count(m => m.Id == sharedId) == 1, "one shared source identity");
            Check(reopened.ReadOptions().ScanDepth == 2 && reopened.ReadOptions().Concurrency == 2, "options persisted");
            Check(reopened.ReadSnapshot(old).Relations.Count == 2, "history persisted after restart");
        }
        Check(Scalar(Database(f.Data), "SELECT COUNT(*) FROM rb_tickets") == 1, "no graph copy of ticket state");
    }

    private static async Task Scanner()
    {
        var f = Fixture("scan");
        foreach (var dir in new[] { "A", "B", "Shared", "bin/fake", "obj/fake", "node_modules/fake", "build/fake", "out/fake", ".git/fake", ".claude/metadata", ".fufucode/runtime", ".playwright-mcp/screens", ".dart_tool/cache" }) Directory.CreateDirectory(Path.Combine(f.Project, dir));
        File.WriteAllText(Path.Combine(f.Project, "A", "A.csproj"), "<Project><ItemGroup><ProjectReference Include=\"../B/B.csproj\"/><Compile Include=\"../Shared/Common.cs\"/><Compile Include=\"$(Unknown)/Missing.cs\"/></ItemGroup><Import Project=\"../Shared/Shared.props\" /></Project>");
        File.WriteAllText(Path.Combine(f.Project, "B", "B.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(f.Project, "Shared", "Common.cs"), "// TEST fixture shared source");
        File.WriteAllText(Path.Combine(f.Project, "Shared", "Shared.props"), "<Project />");
        File.WriteAllText(Path.Combine(f.Project, "bin", "fake", "Fake.csproj"), "<Project />");
        Directory.CreateDirectory(Path.Combine(f.Project, "dart", "lib"));
        File.WriteAllText(Path.Combine(f.Project, "dart", "pubspec.yaml"), "name: real_fixture\n");
        File.WriteAllText(Path.Combine(f.Project, "dart", "lib", "main.dart"), "// import 'fake.dart';\nimport 'package:real_fixture/common.dart';\nexport 'common.dart';\nimport 'package:external/no.dart';\nconst fake = \"import 'fake.dart';\";\n");
        File.WriteAllText(Path.Combine(f.Project, "dart", "lib", "common.dart"), "// TEST fixture");
        using var s = Service(f); await s.ScanAsync(); var snap = s.ReadSnapshot();
        Check(snap.Modules.All(m => !m.SourcePaths.Any(p => p.Split('/').Any(part => part is "bin" or "obj" or "node_modules" or "build" or "out" or ".git" or ".claude" or ".fufucode" or ".playwright-mcp" or ".dart_tool"))), "generated and tool metadata directories excluded");
        Check(snap.Entries.Any(e => e.Kind == "source" && e.Reference == ".fufucode" && e.Text.Contains("excluded-directory")), "excluded runtime source path retained as observation");
        var legacyTool = s.UpsertModule("test-legacy-runtime-module", null, "Old scan runtime module", "TEST-ONLY previous scanner fixture", [".fufucode"]);
        Sql(Database(f.Data), "UPDATE graph_modules SET origin='scan' WHERE module_id='test-legacy-runtime-module'");
        await s.ScanAsync();
        Check(!s.ReadSnapshot().Modules.Any(m => m.Id == legacyTool.Id), "new exclusions prune prior scanner metadata modules");
        Check(snap.Relations.Count(r => r.Kind == "ProjectReference") == 1 && snap.Relations.Count(r => r.Kind == "Import") == 1 && snap.Relations.Count(r => r.Kind == "Compile") == 1, "literal actual references only");
        Check(snap.Entries.Any(e => e.Kind == "source" && e.Text.Contains("unresolved-reference")), "unresolved reference kept as provenance");
        Check(snap.Relations.Count(r => r.Kind == "dart:import") == 1 && snap.Relations.Count(r => r.Kind == "dart:export") == 1,
            "Dart resolves own package and relative literal directives; ignores comments, strings and external packages");
        var common = snap.Modules.Single(m => m.SourcePaths.Contains("Shared/Common.cs"));
        var a = snap.Modules.Single(m => m.SourcePaths.Contains("A/A.csproj"));
        var before = snap.Revision;
        s.UpsertModule(a.Id, a.ParentId, "User-owned name", "human detail", a.SourcePaths);
        await s.ScanAsync(); snap = s.ReadSnapshot();
        Check(snap.Modules.Single(m => m.Id == a.Id).Name == "User-owned name" && snap.Entries.Any(e => e.Kind == "conflict"), "scan preserves and records human conflict");
        Check(s.ReadSnapshot(before).Modules.Single(m => m.Id == a.Id).Name == "A", "old scan name stable");
        Check(snap.Modules.Count(m => m.Id == common.Id) == 1, "shared source has one identity");
        var priorRemoval = snap.Revision;
        File.Delete(Path.Combine(f.Project, "dart", "lib", "common.dart")); await s.ScanAsync();
        Check(!s.ReadSnapshot().Modules.Any(m => m.SourcePaths.Contains("dart/lib/common.dart"))
            && s.ReadSnapshot(priorRemoval).Modules.Any(m => m.SourcePaths.Contains("dart/lib/common.dart")), "source deletion updates current projection while preserving historical module");
        await s.SaveOptionsAsync(s.ReadOptions() with { ScanDepth = 0 }); await s.ScanAsync();
        Check(s.ReadSnapshot().Entries.Any(e => e.Kind == "scan" && e.Text.Contains("\"depth\":0")), "depth is used and recorded");
    }

    private static Task Identity()
    {
        var f = Fixture("identity"); var oldId = "project-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(f.Project)))[..12].ToLowerInvariant();
        var hash = oldId["project-".Length..]; var db = Path.Combine(f.Data, "auto-dev", "rebirth", hash, "kernel.db");
        using (var kernel = new RebirthKernel(db))
        {
            kernel.CreateProject("legacy-project", "Legacy real fixture", new RebirthBrief("Original human purpose", ["purpose"], [], [], ["verified"], [f.Project], 60, RebirthAutonomy.WithinMandate), "human", 1, oldId);
            kernel.CreateTicket("legacy-ticket", oldId, "Existing work", "Existing goal", ["verified"], "graph", "graph", 2, ticketId: "existing-ticket");
        }
        using (var s = Service(f, host: "stable-host-id"))
        { Check(s.ReadSnapshot().ProjectId == "stable-host-id" && s.ReadSnapshot().Tickets.Single().Id == "existing-ticket", "host maps to existing legacy authority"); }
        using (var s = Service(f, host: "alternate-host-alias")) Check(s.ReadSnapshot().Tickets.Single().Id == "existing-ticket", "aliases share path authority");
        Check(Scalar(db, "SELECT COUNT(*) FROM rb_projects") == 1 && Scalar(db, "SELECT COUNT(*) FROM rb_tickets") == 1, "no project/ticket duplication");
        return Task.CompletedTask;
    }

    private static async Task Acceptance()
    {
        var f = Fixture("accept"); var fake = new InstantExecutor(); using var s = Service(f, fake); s.SetPurpose("Verify actual local fixture work");
        var ticket = Ticket(s, "acceptance", "one", "two");
        await s.RunTicketAsync(ticket.Id);
        Check(s.ReadSnapshot().Tickets.Single().Status == "Review" && s.ReadSnapshot().Runs.Single().Status == "Completed", "executor completion only enters Review");
        Check(s.ReadSnapshot().Runs.Single().SessionId == "test-only-session-1", "executor session returned and persisted");
        Throws(() => s.AcceptTicket(ticket.Id, "no-evidence"), "cannot accept without evidence");
        var proof = Path.Combine(f.Project, "actual-test-proof.txt"); File.WriteAllText(proof, "TEST-ONLY local proof");
        s.AddEntry(ticket.Id, null, "evidence", "{\"criterion\":\"one\",\"passed\":true}", proof);
        Throws(() => s.AcceptTicket(ticket.Id, proof), "every criterion required");
        s.AddEntry(ticket.Id, null, "evidence", "{\"criterion\":\"two\",\"passed\":true}", proof);
        s.AcceptTicket(ticket.Id, proof);
        Check(s.ReadSnapshot().Tickets.Single().Status == "Done" && s.ReadSnapshot().Entries.Any(e => e.Kind == "Acceptance" && e.Actor == "human"), "only explicit human operation accepts");
        var stale = Ticket(s, "stale proof"); await s.RunTicketAsync(stale.Id);
        s.AddEntry(stale.Id, null, "evidence", "fixture verified", proof);
        File.AppendAllText(proof, " changed after evidence");
        Throws(() => s.AcceptTicket(stale.Id, proof), "modified evidence invalidates acceptance");
        Check(s.ReadSnapshot().Tickets.Single(t => t.Id == stale.Id).Status == "Review"
            && s.ReadSnapshot().Entries.Any(e => e.Kind == "conflict" && e.TicketId == stale.Id), "evidence conflict is persisted");
        s.AddEntry(stale.Id, null, "evidence", "fixture verified", proof); s.AcceptTicket(stale.Id, proof);
        var veto = Ticket(s, "veto"); s.VetoTicket(veto.Id, "Human rejects this route");
        await Rejects(() => s.RunTicketAsync(veto.Id), "veto blocks execution");
        Check(fake.Calls == 2, "veto did not call executor");
    }

    private static async Task StopReceipt()
    {
        var f = Fixture("stop"); var fake = new ControlledExecutor(); using var s = Service(f, fake); s.SetPurpose("Exercise actual cancellation acknowledgement");
        var ticket = Ticket(s); var running = s.RunTicketAsync(ticket.Id); await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await s.SaveOptionsAsync(s.ReadOptions() with { ScanDepth = 3 });
        Check(!fake.CancellationSeen.Task.IsCompleted, "changing scan depth does not stop a manually launched run");
        var stopping = s.StopTicketAsync(ticket.Id); await fake.CancellationSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!stopping.IsCompleted && s.ReadSnapshot().Runs.Single().Status == "StopRequested" && s.ReadSnapshot().Runs.Single().EndedUtc is null, "Cancel is a request, not a receipt");
        fake.Acknowledge.TrySetResult(new("interrupted", "TEST-ONLY executor acknowledged cancellation"));
        await stopping.WaitAsync(TimeSpan.FromSeconds(5)); await running;
        Check(s.ReadSnapshot().Runs.Single().Status == "Interrupted" && s.ReadSnapshot().Runs.Single().EndedUtc is not null, "terminal status waits for executor acknowledgement");
        await s.RunTicketAsync(ticket.Id);
        Check(fake.Calls == 2 && s.ReadSnapshot().Runs.All(r => r.Status == "Interrupted"), "only an explicit human retry can resume a known stopped run");
    }

    private static async Task Disable()
    {
        var f = Fixture("disable"); var fake = new ControlledExecutor(); using var s = Service(f, fake); s.SetPurpose("Test lifecycle disable");
        var first = Ticket(s); var second = Ticket(s, "queued second");
        await s.SetAutonomyAsync(true); await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disabling = s.SaveOptionsAsync(s.ReadOptions() with { Enabled = false });
        await fake.CancellationSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!disabling.IsCompleted && !s.ReadOptions().Enabled, "disable persists and waits for managed cancellation");
        fake.Acknowledge.TrySetResult(new("interrupted", "TEST-ONLY disable receipt")); await disabling.WaitAsync(TimeSpan.FromSeconds(5));
        Check(fake.Calls == 1, "disable did not schedule queued work");
        await Rejects(() => s.RunTicketAsync(second.Id), "disabled service rejects new launches");
        await Rejects(() => s.ScanAsync(), "disabled service rejects new scans");
        var handler = new GraphToolHandler(s); var result = await handler.ExecuteAsync(new("disabled-read", "graph_read", "{}")); Check(!result.Success, "disabled tools reject even reads");
    }

    private static async Task Restart()
    {
        var f = Fixture("restart"); string ticketId;
        using (var s = Service(f)) { s.SetPurpose("Recover unknown side effects conservatively"); ticketId = Ticket(s).Id; }
        using (var kernel = new RebirthKernel(Database(f.Data)))
        {
            kernel.AuthorizeTicket("authorize-recovery", ticketId, "human", 3);
            kernel.StartRun("orphaned-run", ticketId, "graph", "TEST-ONLY orphaned fixture", f.Project, 4, "orphan-run");
        }
        var fake = new InstantExecutor();
        using var reopened = Service(f, fake);
        Check(reopened.ReadSnapshot().Runs.Single().Status == "Unknown" && reopened.ReadSnapshot().Tickets.Single().Status == "NeedsReconciliation", "restart marks in-flight work unknown");
        await reopened.SetAutonomyAsync(true); await WaitUntil(() => reopened.ReadSnapshot().ProjectStatus != "Running");
        Check(fake.Calls == 0, "unknown side effects never retried");
        await Rejects(() => reopened.RunTicketAsync(ticketId), "manual run also requires reconciliation");
    }

    private static async Task Autonomy()
    {
        var f = Fixture("autonomy"); GraphProjectService? service = null; var calls = new ConcurrentQueue<string>();
        var fake = new CallbackExecutor(async request =>
        {
            calls.Enqueue(request.TicketId);
            if (calls.Count == 1)
            {
                var tools = new GraphToolHandler(service!, request.ProjectPath, request.Identity);
                Check(request.Identity?.Role == GraphExecutionRole.ProjectLead, "initial planning receives host ProjectLead identity");
                var follow = await tools.ExecuteAsync(new("test-followup", "graph_create_ticket", "{\"title\":\"Concrete fixture follow-up\",\"goal\":\"Verify the real fixture result\",\"moduleIds\":[],\"acceptance\":[\"fixture checked\"]}"));
                Check(follow.Success, "model can create follow-up under mandate");
                var followTicket = service!.ReadSnapshot().Tickets.Single(t => t.Title == "Concrete fixture follow-up");
                var delegation = await tools.ExecuteAsync(new("test-followup-delegation", "graph_delegate", JsonSerializer.Serialize(new
                {
                    responsibilityId = "followup-executor", ticketId = followTicket.Id, moduleIds = Array.Empty<string>(),
                    expectedVersion = service.ReadOrganizationSnapshot().Version, reason = "Explicit fixture follow-up delegation"
                })));
                Check(delegation.Success, "project lead explicitly delegates follow-up execution");
            }
            else if (request.Identity?.Role == GraphExecutionRole.Executor)
            {
                var tools = new GraphToolHandler(service!, request.ProjectPath, request.Identity);
                Check(!(await tools.ExecuteAsync(new("executor-create-denied", "graph_create_ticket", "{\"title\":\"forbidden\",\"goal\":\"forbidden\",\"moduleIds\":[],\"acceptance\":[\"x\"]}"))).Success,
                    "executor cannot create follow-up work");
                Check(!(await tools.ExecuteAsync(new("executor-module-denied", "graph_upsert_module", "{\"name\":\"forbidden\",\"summary\":\"forbidden\",\"sourcePaths\":[]}"))).Success,
                    "executor cannot change global architecture");
            }
            return new GraphRunResult("completed", "TEST-ONLY planned/ran fixture work");
        });
        using var s = Service(f, fake); service = s;
        await Rejects(() => s.SetAutonomyAsync(true), "empty purpose cannot start");
        s.SetPurpose("Inspect fixture and make checkable follow-up work"); await s.SetAutonomyAsync(true);
        await WaitUntil(() => s.ReadSnapshot().Tickets.Any(t => t.Title == "Concrete fixture follow-up" && t.Status == "Review"));
        await WaitUntil(() => s.ReadSnapshot().ProjectStatus != "Running");
        Check(s.ReadSnapshot().Runs.Count(r => r.TicketId == s.ReadSnapshot().Tickets.Single(t => t.Title == "Concrete fixture follow-up").Id) == 1,
            "delegated follow-up executes once, awaiting human acceptance");
        var arrival = Ticket(s, "queued arrival after idle");
        await WaitUntil(() => s.ReadSnapshot().Tickets.Single(t => t.Id == arrival.Id).Status == "Review");
        await WaitUntil(() => s.ReadSnapshot().ProjectStatus != "Running");
        var settled = s.ReadSnapshot().Runs.Count;
        var watermark = s.ReconcileLeadCycle(f.Project);
        var repeated = s.ReconcileLeadCycle(f.Project);
        Check(repeated.ObservedWatermark == watermark.ObservedWatermark && repeated.PlannedWatermark == watermark.PlannedWatermark,
            "replayed terminal facts do not advance lead event watermark");
        await WaitUntil(() => s.ReadSnapshot().ProjectStatus != "Running");
        var afterReplay = s.ReadSnapshot().Runs.Count;
        s.ReconcileLeadCycle(f.Project);
        Check(s.ReadSnapshot().Runs.Count == afterReplay && afterReplay >= settled,
            "lead completion does not self-excite another planning run");
        await s.SetAutonomyAsync(false);
        Check(s.ReadSnapshot().Runs.Count(r => r.TicketId == arrival.Id) == 1,
            "new delegated queue arrival resumes once within existing mandate");
        Check(s.ReadSnapshot().Entries.Any(e => e.Kind == "ticket-created" && e.Actor == "agent"), "follow-up provenance is agent");
    }

    private static async Task LeadFailureRecovery()
    {
        var f = Fixture("lead-failure"); var calls = 0;
        using var s = Service(f, new CallbackExecutor(_ => Task.FromResult(new GraphRunResult(
            Interlocked.Increment(ref calls) == 2 ? "unknown" : "completed", "TEST-ONLY lead receipt"))));
        s.SetPurpose("Retain human planning requests after uncertain lead outcomes");
        await s.SetAutonomyAsync(true);
        await WaitUntil(() => s.ReadSnapshot().Runs.Count == 1 && s.ReadSnapshot().ProjectStatus != "Running");
        s.SendOrganizationMessage("ordinary-notification", new GraphMessageDraft("project-lead", false, "message",
            "ordinary correspondence is not a wake signal", null, null, s.ReadSnapshot().Revision));
        s.ReconcileLeadCycle(f.Project);
        Check(calls == 1, "ordinary Notify does not wake a lead model");
        s.RecordSessionMessage(null, null, "lead-discussion", "human-request-1", "user", "HUMAN_REQUEST_MUST_SURVIVE_UNKNOWN");
        s.ReconcileLeadCycle(f.Project);
        await WaitUntil(() => s.ReadSnapshot().Runs.Any(r => r.Status == "Unknown") && s.ReadSnapshot().ProjectStatus != "Running");
        var attention = s.ReconcileLeadCycle(f.Project);
        Check(attention.State == "reconciliation-required", "unknown lead batch remains pending reconciliation, not idle");
        var before = calls;
        for (var i = 0; i < 3; i++) Check(s.ReconcileLeadCycle(f.Project).State == "reconciliation-required", "ordinary hooks do not retry uncertain planning");
        Check(calls == before && s.ReadSnapshot().Entries.Count(e => e.Kind == "lead-cycle-attention") == 1,
            "failure produces one durable attention entry without infinite retries");
        var recovered = s.ResumeLeadCycle(f.Project);
        Check(recovered.State == "planning-ready" && recovered.TicketId != attention.TicketId, "explicit human recovery creates a distinct reconciliation plan");
        Check(s.ReadLeadCyclePrompt(f.Project, recovered.TicketId!).EventsJson.Contains("HUMAN_REQUEST_MUST_SURVIVE_UNKNOWN"),
            "new reconciliation plan retains original human message facts");
        await WaitUntil(() => s.ReadSnapshot().ProjectStatus != "Running");
        Check(s.ReadSnapshot().Runs.Count(r => r.Status == "Unknown") == 1 && calls == before + 1,
            "unknown run is never replayed; only new lead reconciliation executes");
        Check(s.ReconcileLeadCycle(f.Project).State == "idle", "successful reconciliation does not self-excite");
        Check(s.ReadOrganizationSnapshot().Messages.All(m => m.Status != GraphMessageStatus.Applied),
            "planning transport never manufactures Applied acknowledgement");
        await s.SetAutonomyAsync(false);
    }

    private static async Task Tools()
    {
        var f = Fixture("tools"); GraphProjectService? service = null;
        var executor = new CallbackExecutor(async request =>
        {
        var s = service!;
        var handler = new GraphToolHandler(s, request.ProjectPath, request.Identity);
        Check(request.Identity?.Role == GraphExecutionRole.ProjectLead, "mutation fixture uses a real live project lead");
        var revision = s.ReadSnapshot().Revision;
        var args = JsonSerializer.Serialize(new { name = "Agent sourced module", summary = "real fixture", sourcePaths = Array.Empty<string>(), expectedRevision = revision });
        var call = new PureToolCall("idempotent-module", "graph_upsert_module", args);
        var first = await handler.ExecuteAsync(call); var replay = await handler.ExecuteAsync(call);
        Check(first.Success && replay.Success && first.Content == replay.Content, "idempotent exact replay returns same receipt");
        Check(s.ReadSnapshot().Modules.Count == 1 && s.ReadSnapshot().Entries.Any(e => e.Kind == "module" && e.Actor == "agent"), "agent actor cannot turn human");
        var changed = await handler.ExecuteAsync(call with { ArgumentsJson = args.Replace("real fixture", "different input") }); Check(!changed.Success, "command identity payload conflict rejected");
        var stale = await handler.ExecuteAsync(call with { Id = "new-stale-command" }); Check(!stale.Success && s.ReadSnapshot().Entries.Any(e => e.Kind == "conflict"), "stale revision rejected and conflict persisted");
        foreach (var tool in new[] { "graph_accept_ticket", "graph_set_purpose", "graph_gate", "graph_lift_veto" })
            Check(!(await handler.ExecuteAsync(new("forged-" + tool, tool, "{}"))).Success, "no privileged model tool: " + tool);
        Check(!(await handler.ExecuteAsync(new("forged-actor", "graph_add_entry", "{\"kind\":\"message\",\"text\":\"spoof\",\"actor\":\"human\"}"))).Success, "actor spoof rejected");
        var legacy = new AutoDevToolHandler(new PluginPackageContext("automation.auto-dev", f.Project, f.Project, t => t == typeof(IGraphProjectService) ? s : null, f.Data));
        Check(!(await legacy.ExecuteAsync(new("legacy-gate", "autodev_gate", "{}"))).Success, "legacy human spoof blocked");
        await s.SaveOptionsAsync(s.ReadOptions() with { BudgetMinutes = 1 });
        Sql(Database(f.Data), "UPDATE graph_state SET budget_started_utc=1");
        Check(!(await handler.ExecuteAsync(new("expired-budget", "graph_add_entry", "{\"kind\":\"message\",\"text\":\"out of budget\"}"))).Success, "expired budget rejects model writes");
        await Rejects(() => s.RunTicketAsync(Ticket(s, "new work").Id), "expired budget rejects executor launch");
        return new GraphRunResult("completed", "TEST-ONLY live tool fixture receipt");
        });
        using var owner = Service(f, executor); service = owner;
        var unbound = new GraphToolHandler(owner);
        var noMandate = await unbound.ExecuteAsync(new("no-mandate", "graph_create_ticket", "{\"title\":\"x\",\"goal\":\"x\",\"moduleIds\":[],\"acceptance\":[\"x\"]}"));
        Check(!noMandate.Success, "model cannot create a purpose");
        owner.SetPurpose("Test safe tool mutation");
        Check((await unbound.ExecuteAsync(new("unbound-read", "graph_read", "{}"))).Success, "no identity retains read access");
        Check(!(await unbound.ExecuteAsync(new("unbound-write", "graph_add_entry", "{\"kind\":\"message\",\"text\":\"untrusted\"}"))).Success,
            "no identity cannot mutate even under a valid purpose");
        await owner.RunTicketAsync(PlanningTicket(owner, f.Data));
    }

    private static async Task ProjectScope()
    {
        var f = Fixture("project-scope"); var other = Path.Combine(Sandbox, "other-project"); Directory.CreateDirectory(other);
        GraphProjectService? service = null;
        var executor = new CallbackExecutor(async request =>
        {
            var s = service!;
            var pinned = new GraphToolHandler(s, request.ProjectPath, request.Identity);
            s.SetProject("host-B", other); s.SetPurpose("Project B purpose");
            var result = await pinned.ExecuteAsync(new("project-A-entry", "graph_add_entry", JsonSerializer.Serialize(new
            { ticketId = request.TicketId, kind = "message", text = "Only project A should receive this" })));
            Check(result.Success && s.ReadSnapshot().ProjectId == "host-B" && !s.ReadSnapshot().Entries.Any(e => e.Text.Contains("Only project A")), "pinned tools preserve selected UI project and never mutate B");
            s.SetProject("host-project", f.Project);
            Check(s.ReadSnapshot().Entries.Any(e => e.Text.Contains("Only project A") && e.Actor == "agent"), "pinned command wrote to A");
            return new GraphRunResult("completed", "TEST-ONLY pinned tool receipt");
        });
        using var owner = Service(f, executor); service = owner;
        owner.SetPurpose("Project A purpose"); await owner.RunTicketAsync(Ticket(owner).Id);
    }

    private static async Task Budget()
    {
        var f = Fixture("budget"); var fake = new ControlledExecutor(); using var s = Service(f, fake); s.SetPurpose("Budget cancellation fixture");
        var running = s.RunTicketAsync(Ticket(s).Id); await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Sql(Database(f.Data), "UPDATE graph_state SET budget_started_utc=1");
        await s.SaveOptionsAsync(s.ReadOptions() with { BudgetMinutes = 1 });
        await fake.CancellationSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!running.IsCompleted && s.ReadSnapshot().Runs.Single().Status == "StopRequested", "expired revised budget requests cancellation without fake terminal");
        fake.Acknowledge.TrySetResult(new("interrupted", "TEST-ONLY budget receipt")); await running.WaitAsync(TimeSpan.FromSeconds(5));
        Check(s.ReadSnapshot().Runs.Single().Status == "Interrupted", "budget run waits for actual receipt");
    }

    private static async Task RunEvidence()
    {
        var f = Fixture("run-evidence"); var proof = Path.Combine(f.Project, "proof.txt"); File.WriteAllText(proof, "TEST-ONLY real evidence file");
        GraphProjectService? service = null;
        var fake = new CallbackExecutor(async request =>
        {
            var handler = new GraphToolHandler(service!, request.ProjectPath, request.Identity);
            var arguments = JsonSerializer.Serialize(new { ticketId = request.TicketId, runId = request.RunId, kind = "test", criterion = "fixture verified", reference = proof, passed = true });
            var result = await handler.ExecuteAsync(new("run-evidence-claim", "graph_evidence", arguments));
            Check(result.Success && result.Content.Contains("claim-recorded"), "in-run evidence is a claim until receipt");
            Check(!service!.ReadSnapshot().Entries.Any(e => e.Kind == "evidence"), "no passing authority evidence before run settles");
            return new GraphRunResult("completed", "TEST-ONLY receipt");
        });
        using var s = Service(f, fake); service = s; s.SetPurpose("Real local evidence fixture"); var ticket = Ticket(s);
        await s.RunTicketAsync(ticket.Id);
        Check(s.ReadSnapshot().Entries.Any(e => e.Kind == "evidence" && e.Actor == "agent"), "settlement binds evidence to actual run");
        Check(s.ReadSnapshot().Tickets.Single().Status == "Review", "model evidence does not self-accept");
        s.AcceptTicket(ticket.Id, proof); Check(s.ReadSnapshot().Tickets.Single().Status == "Done", "human can accept settled evidence");
    }

    private static async Task HumanRecovery()
    {
        var f = Fixture("human-recovery"); var calls = 0;
        using var s = Service(f, new CallbackExecutor(_ => Task.FromResult(++calls == 1
            ? new GraphRunResult("unknown", "TEST-ONLY uncertain receipt") : new GraphRunResult("completed", "TEST-ONLY explicit retry receipt"))));
        s.SetPurpose("Reconcile actual effects before a human explicitly retries"); var unknown = Ticket(s);
        await s.RunTicketAsync(unknown.Id); await Rejects(() => s.RunTicketAsync(unknown.Id), "unknown cannot run before reconciliation");
        Throws(() => s.ReconcileTicket(unknown.Id, " "), "resolution cannot be blank");
        s.ReconcileTicket(unknown.Id, "  TEST-ONLY human checked the uncertain fixture effects  ");
        Check(calls == 1 && s.ReadSnapshot().Tickets.Single().Status == "Authorized", "human reconciliation records knowledge without dispatch");
        Check(s.ReadSnapshot().Entries.Any(e => e.Kind == "reconciliation" && e.Actor == "human" && e.Text == "  TEST-ONLY human checked the uncertain fixture effects  "), "human resolution kept verbatim");
        await s.SetAutonomyAsync(true); await WaitUntil(() => s.ReadSnapshot().ProjectStatus != "Running");
        Check(calls == 1, "automatic queue cannot repeat reconciled unknown effects");
        await s.SetAutonomyAsync(false); await s.RunTicketAsync(unknown.Id);
        Check(calls == 2 && s.ReadSnapshot().Tickets.Single().Status == "Review", "only explicit human Run retries after reconciliation");
        Throws(() => s.ReconcileTicket(unknown.Id, "not unknown"), "only NeedsReconciliation may be reconciled");
        var vetoed = Ticket(s, "ticket veto"); var other = Ticket(s, "other veto");
        s.VetoTicket(vetoed.Id, "Original human veto"); s.VetoTicket(other.Id, "Other ticket remains vetoed");
        string projectVetoId;
        using (var kernel = new RebirthKernel(Database(f.Data)))
            projectVetoId = kernel.RecordVeto("test-only-project-veto", "host-project", null, "project:host-project", "project", "TEST-ONLY project veto", "human", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Decision.Id;
        Throws(() => s.LiftVeto(vetoed.Id, ""), "lift reason cannot be empty");
        s.LiftVeto(vetoed.Id, "  Human changed this ticket decision only  ");
        var decisions = s.ReadSnapshot().Entries.Where(e => e.Kind == "Veto").Select(e => JsonSerializer.Deserialize<RebirthDecision>(e.Text,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!).ToArray();
        Check(decisions.Single(d => d.TicketId == vetoed.Id).Status == "Lifted" && decisions.Single(d => d.TicketId == vetoed.Id).Text == "Original human veto", "original veto remains with Lifted state and original reason");
        Check(decisions.Single(d => d.Id == projectVetoId).Status == "Active" && decisions.Single(d => d.TicketId == other.Id).Status == "Active", "project and other-ticket vetoes remain active");
        Check(s.ReadSnapshot().Entries.Any(e => e.Kind == "veto-lifted" && e.Actor == "human" && e.Text.Contains("Human changed this ticket decision only")), "lift reason persisted as human correspondence");
        await Rejects(() => s.RunTicketAsync(vetoed.Id), "ticket lift cannot bypass project veto");
        using (var kernel = new RebirthKernel(Database(f.Data))) kernel.LiftVeto("fixture-lift-project", "host-project", projectVetoId, "human", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await s.RunTicketAsync(vetoed.Id); Check(calls == 3, "explicit Run succeeds after the relevant human constraints are lifted");
        var tools = new GraphToolHandler(s);
        foreach (var name in new[] { "graph_lift_veto", "graph_reconcile_ticket", "autodev_lift_veto", "autodev_reconcile" })
            Check(!(await tools.ExecuteAsync(new("denied-" + name, name, "{}"))).Success, "model cannot operate trusted recovery API: " + name);
        var activeFixture = Fixture("recovery-in-flight"); var controlled = new ControlledExecutor(); using var active = Service(activeFixture, controlled);
        active.SetPurpose("TEST-ONLY recovery while cancelling"); var runningTicket = Ticket(active); var run = active.RunTicketAsync(runningTicket.Id);
        await controlled.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); active.VetoTicket(runningTicket.Id, "Stop this live route");
        Throws(() => active.LiftVeto(runningTicket.Id, "too soon"), "cannot lift a veto while its executor is in flight");
        Throws(() => active.ReconcileTicket(runningTicket.Id, "too soon"), "cannot reconcile before the actual terminal receipt");
        controlled.Acknowledge.TrySetResult(new("interrupted", "TEST-ONLY terminal cancellation receipt")); await run;
        active.LiftVeto(runningTicket.Id, "Now the actual executor stopped");
    }

    private static async Task BoundedContext()
    {
        var f = Fixture("bounded-context"); var calls = 0; string? actualPrompt = null;
        using var s = Service(f, new CallbackExecutor(request =>
        { actualPrompt = request.Prompt; return Task.FromResult(new GraphRunResult(++calls == 1 ? "unknown" : "completed", "TEST-ONLY context fixture receipt")); }));
        s.SetPurpose("Keep a focused literal view of a large test graph");
        var root = s.UpsertModule(null, null, "Fixture root", "TEST-ONLY root", ["."]);
        var target = s.UpsertModule(null, root.Id, "Focused fixture target", "TARGET_LITERAL_" + new string('x', 20000), ["target/source.cs"]);
        var child = s.UpsertModule(null, target.Id, "Focused child", "TEST-ONLY child", ["target/child.cs"]);
        var neighbour = s.UpsertModule(null, root.Id, "Literal neighbour", "TEST-ONLY neighbour", ["shared.cs"]);
        s.UpsertRelation(null, target.Id, neighbour.Id, "dependency", "TEST-ONLY explicit relationship");
        var ticket = s.CreateTicket("Focused large fixture", "GOAL_LITERAL_" + new string('g', 15000), [target.Id], ["focused fixture proof"]);
        await s.RunTicketAsync(ticket.Id); s.ReconcileTicket(ticket.Id, "TEST-ONLY no effects occurred; explicit second Run will inspect focused context");
        // Synthetic records are bulk-loaded solely into this isolated TEST fixture, never a real project.
        using (var c = new SqliteConnection("Data Source=" + Database(f.Data)))
        {
            c.Open(); using var tx = c.BeginTransaction();
            for (var i = 0; i < 1600; i++)
            {
                var module = new GraphModule("bulk-fixture-" + i, root.Id, "TEST-ONLY unrelated module " + i, "UNRELATED_MODULE_LITERAL_" + i, ["bulk/" + i + ".cs"], 1);
                using var q = c.CreateCommand(); q.Transaction = tx;
                q.CommandText = "INSERT INTO graph_modules VALUES('host-project',$id,$parent,$json,'manual','test-fixture',1)";
                q.Parameters.AddWithValue("$id", module.Id); q.Parameters.AddWithValue("$parent", root.Id); q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(module)); q.ExecuteNonQuery();
            }
            for (var i = 0; i < 3300; i++)
            {
                using var q = c.CreateCommand(); q.Transaction = tx;
                q.CommandText = "INSERT INTO graph_sources VALUES('host-project',$entity,$path,'TEST-ONLY fixture source','test-only-hash',$detail,1,1)";
                q.Parameters.AddWithValue("$entity", "bulk-fixture-" + i % 1600); q.Parameters.AddWithValue("$path", "bulk-source/" + i + ".cs");
                q.Parameters.AddWithValue("$detail", "UNRELATED_GLOBAL_SOURCE_LITERAL_" + i); q.ExecuteNonQuery();
            }
            for (var i = 0; i < 10; i++)
            {
                using var q = c.CreateCommand(); q.Transaction = tx;
                q.CommandText = "INSERT INTO graph_sources VALUES('host-project',$entity,$path,'TEST-ONLY related source','related-hash',$detail,1,1)";
                q.Parameters.AddWithValue("$entity", target.Id); q.Parameters.AddWithValue("$path", "target/source-proof-" + i + ".cs");
                q.Parameters.AddWithValue("$detail", "RELATED_SOURCE_LITERAL_" + i); q.ExecuteNonQuery();
            }
            for (var i = 0; i < 80; i++)
            {
                using var q = c.CreateCommand(); q.Transaction = tx;
                q.CommandText = "INSERT INTO graph_entries VALUES($id,'host-project',$ticket,NULL,'message','test-fixture',$text,NULL,$now,1)";
                q.Parameters.AddWithValue("$id", "fixture-entry-" + i); q.Parameters.AddWithValue("$ticket", i < 30 ? ticket.Id : DBNull.Value);
                q.Parameters.AddWithValue("$text", (i < 30 ? "RELATED_CORRESPONDENCE_LITERAL_" : "UNRELATED_GLOBAL_MESSAGE_LITERAL_") + i + new string('m', 3000));
                q.Parameters.AddWithValue("$now", 100000 + i); q.ExecuteNonQuery();
            }
            tx.Commit();
        }
        s.AddEntry(ticket.Id, target.Id, "message", "TEST-ONLY large fixture snapshot ready");
        var full = s.ReadSnapshot(); var context = s.BuildContext(null, ticket.Id);
        using var parsed = JsonDocument.Parse(context); var data = parsed.RootElement;
        var ids = data.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToArray();
        Check(ids.Contains(target.Id) && ids.Contains(root.Id) && ids.Contains(child.Id) && ids.Contains(neighbour.Id), "focus includes bound module, parent, child and literal neighbour");
        Check(context.Length < 60000 && !context.Contains("UNRELATED_GLOBAL_SOURCE_LITERAL_") && !context.Contains("UNRELATED_GLOBAL_MESSAGE_LITERAL_"), "focus excludes unrelated global records and stays bounded");
        Check(data.GetProperty("omitted").GetProperty("sources").GetInt32() > 3200 && data.GetProperty("omitted").GetProperty("textCharacters").GetInt64() > 0
            && !data.GetProperty("scope").GetProperty("completeSnapshot").GetBoolean(), "omitted records and literal clipping are explicit");
        Check(data.GetProperty("runs").EnumerateArray().Any(r => r.GetProperty("status").GetString() == "Unknown"), "unknown receipt remains in focused facts");
        Check(data.GetProperty("entries").EnumerateArray().Any(e => e.GetProperty("kind").GetString() == "source" && e.GetProperty("text").GetString()!.Contains("related-hash")), "bounded sources retain actual literal provenance instead of generated summaries");
        Check(data.GetProperty("fullRead").GetProperty("arguments").GetProperty("full").GetBoolean(), "full reading entry point is explicit");
        using var planning = JsonDocument.Parse(s.BuildContext(null, null));
        Check(planning.RootElement.GetProperty("modules").GetArrayLength() <= 24 && planning.RootElement.GetProperty("scope").GetProperty("mode").GetString() == "planning-candidates", "unbound planning sees limited candidates, never every module");
        var tools = new GraphToolHandler(s);
        var index = await tools.ExecuteAsync(new("index", "graph_read", "{}")); Check(index.Success && index.Content.Length < 50000, "default graph_read is a bounded index");
        using var read = JsonDocument.Parse(index.Content);
        Check(read.RootElement.GetProperty("modules").GetArrayLength() == 24 && read.RootElement.GetProperty("entries").GetArrayLength() == 0
            && read.RootElement.GetProperty("pagination").GetProperty("modules").GetProperty("hasMore").GetBoolean(), "default index states module pagination and excludes correspondence by default");
        var page = await tools.ExecuteAsync(new("page", "graph_read", "{\"moduleOffset\":24,\"moduleLimit\":7,\"includeEntries\":true,\"entryOffset\":3,\"entryLimit\":5}"));
        Check(page.Success, "explicit paged read succeeds"); using var pageData = JsonDocument.Parse(page.Content);
        Check(pageData.RootElement.GetProperty("modules").GetArrayLength() == 7 && pageData.RootElement.GetProperty("entries").GetArrayLength() == 5, "module and entry page sizes honoured");
        var all = await tools.ExecuteAsync(new("explicit-full", "graph_read", "{\"full\":true}")); Check(all.Success, "explicit full read succeeds"); using var allData = JsonDocument.Parse(all.Content);
        Check(allData.RootElement.GetProperty("modules").GetArrayLength() == full.Modules.Count && allData.RootElement.GetProperty("entries").GetArrayLength() == full.Entries.Count(e => e.Kind != "organization"), "full:true preserves every architecture record without broadcasting private organization audit");
        Check(!(await tools.ExecuteAsync(new("bad-page", "graph_read", "{\"moduleOffset\":-1}"))).Success
            && !(await tools.ExecuteAsync(new("ambiguous-full", "graph_read", "{\"full\":true,\"includeEntries\":false}"))).Success, "invalid or contradictory pages rejected");
        await s.RunTicketAsync(ticket.Id);
        Check(actualPrompt is not null && actualPrompt.Length < 66000 && actualPrompt.Contains("focused-ticket")
            && !actualPrompt.Contains("UNRELATED_GLOBAL_SOURCE_LITERAL_") && !actualPrompt.Contains(new string('g', 5000)), "actual executor prompt uses bounded focused facts without re-injecting full goal or global source graph");
        Console.WriteLine($"TEST-ONLY large fixture: {full.Modules.Count} modules, {full.Entries.Count(e => e.Kind == "source")} sources; context={context.Length} chars, default index={index.Content.Length} chars, actual fake-executor prompt={actualPrompt!.Length} chars.");
    }

    private static async Task UnknownReceipts()
    {
        var f = Fixture("unknown-receipts");
        using (var s = Service(f, new CallbackExecutor(_ => Task.FromResult(new GraphRunResult("exit0", "TEST-ONLY process exit zero without settled task receipt")))))
        {
            s.SetPurpose("Do not equate process exit with completion"); var ticket = Ticket(s); await s.RunTicketAsync(ticket.Id);
            Check(s.ReadSnapshot().Runs.Single().Status == "Unknown" && s.ReadSnapshot().Tickets.Single().Status == "NeedsReconciliation", "exit zero without settled receipt remains unknown");
            Throws(() => s.AcceptTicket(ticket.Id, "nonexistent"), "unknown cannot be accepted");
        }
        var empty = Fixture("empty-delivery");
        using (var service = Service(empty, new CallbackExecutor(_ => Task.FromResult(new GraphRunResult("Completed", "No source changes") { DeliveryStatus = "EmptyCandidate" }))))
        {
            service.SetPurpose("Separate settlement from actual delivery");
            var work = Ticket(service); await service.RunTicketAsync(work.Id);
            Check(service.ReadSnapshot().Runs.Single().Status == "Completed" && service.ReadSnapshot().Tickets.Single().Status == "Review",
                "empty candidate does not falsify the actual execution terminal state");
            Check(service.ReadSnapshot().Entries.Any(e => e.Kind == "delivery") && service.ReadOrganizationSnapshot().Messages.Any(m => m.Content.Kind == "delivery-blocked" && !m.Content.HumanRecipient),
                "empty delivery is explicit and addressed to project responsibility, not automatically pushed to humans");
        }
        var failed = Fixture("transport-unknown");
        using var second = Service(failed, new CallbackExecutor(_ => throw new IOException("TEST-ONLY transport disconnected after potential side effects")));
        second.SetPurpose("Transport uncertainty is not deterministic failure"); await second.RunTicketAsync(Ticket(second).Id);
        Check(second.ReadSnapshot().Runs.Single().Status == "Unknown" && second.ReadSnapshot().Tickets.Single().Status == "NeedsReconciliation", "transport exception preserves uncertainty");
    }

    private static Task SessionMessages()
    {
        var f = Fixture("messages"); using var s = Service(f); s.SetPurpose("Actual message ingestion fixture");
        var ticket = Ticket(s); s.LinkSession(ticket.Id, "actual-fixture-session");
        var status = s.ReadSnapshot().Tickets.Single().Status;
        s.RecordSessionMessage(ticket.Id, null, "actual-fixture-session", "user-message", "user", "  original human text  ");
        s.RecordSessionMessage(ticket.Id, null, "actual-fixture-session", "assistant-message", "assistant", "TEST-ONLY assistant claim");
        var first = s.ReadSnapshot();
        s.RecordSessionMessage(ticket.Id, null, "actual-fixture-session", "user-message", "user", "  original human text  ");
        Check(s.ReadSnapshot().Revision == first.Revision && s.ReadSnapshot().Entries.Count == first.Entries.Count, "same session/message replay is a no-op");
        Check(first.Entries.Any(e => e.Kind == "input:Query" && e.Actor == "human" && e.Text == "  original human text  ")
            && first.Entries.Any(e => e.Kind == "claim" && e.Actor == "agent" && e.Reference == "session:actual-fixture-session/message:assistant-message"), "human input and assistant claim preserve source");
        Check(first.Tickets.Single().Status == status && first.Runs.Count == 0, "messages cannot change work or run states");
        Throws(() => s.RecordSessionMessage(ticket.Id, null, "actual-fixture-session", "assistant-message", "assistant", "different text"), "conflicting settled message cannot overwrite");
        Check(s.ReadSnapshot().Entries.Any(e => e.Kind == "conflict"), "message conflict persisted");
        Throws(() => s.RecordSessionMessage(ticket.Id, null, "unlinked-session", "x", "user", "spoof"), "ticket session linkage required");
        Throws(() => s.RecordSessionMessage(ticket.Id, null, "actual-fixture-session", "x", "system", "spoof"), "only actual user/assistant roles allowed");
        var other = Path.Combine(Sandbox, "message-other-project"); Directory.CreateDirectory(other); s.SetProject("other-message-project", other);
        s.RecordSessionMessage(ticket.Id, null, "actual-fixture-session", "late-message", "assistant", "late actual message for original ticket");
        Check(!s.ReadSnapshot().Entries.Any(e => e.Text.Contains("late actual")), "late linked session event cannot leak to newly selected project");
        return Task.CompletedTask;
    }

    private static async Task Sgline()
    {
        const string project = "C:/Projects/SGline";
        Check(Directory.Exists(project), "acceptance project exists");
        var data = Path.Combine(Sandbox, "sgline-data");
        using var s = new GraphProjectService(data, new NeverExecutor()); s.SetProject("acceptance-sgline", project);
        await s.ScanAsync(); var snap = s.ReadSnapshot();
        Check(snap.Modules.Count > 0 && snap.Entries.Any(e => e.Kind == "source"), "actual SGline sources discovered");
        Check(snap.Tickets.Count == 0 && snap.Runs.Count == 0, "scan creates no sample work or model calls");
        Check(snap.Relations.Any(r => r.Kind.StartsWith("dart:", StringComparison.Ordinal)), "SGline Dart literal import relations have real sources");
        Check(snap.Modules.All(m => !m.SourcePaths.Any(p => p.Split('/').Any(part => part is "bin" or "obj" or "node_modules" or "build" or "out" or ".git" or ".claude" or ".fufucode" or ".playwright-mcp" or ".dart_tool"))), "SGline generated output and tool metadata excluded");
        Console.WriteLine($"SGline actual scan: {snap.Modules.Count} modules, {snap.Relations.Count} literal relations, {snap.Entries.Count(e => e.Kind == "source")} source records, revision {snap.Revision}; project files read only.");
    }
    private static async Task WaitUntil(Func<bool> predicate)
    { var until = DateTime.UtcNow.AddSeconds(10); while (!predicate()) { if (DateTime.UtcNow > until) throw new TimeoutException("Local fake-executor condition did not settle."); await Task.Delay(15); } }

    private sealed class NeverExecutor : IGraphRunExecutor
    {
        public Task<GraphRunResult> ExecuteAsync(GraphRunRequest request, Action<string>? progress, CancellationToken cancellationToken) => throw new Exception("TEST-ONLY: executor must not be invoked in this test.");
    }
    private sealed class InstantExecutor : IGraphRunExecutor
    {
        public int Calls;
        public Task<GraphRunResult> ExecuteAsync(GraphRunRequest request, Action<string>? progress, CancellationToken cancellationToken)
        { var count = Interlocked.Increment(ref Calls); progress?.Invoke("TEST-ONLY local fake progress"); return Task.FromResult(new GraphRunResult("completed", "TEST-ONLY local fake receipt", SessionId: "test-only-session-" + count)); }
    }
    private sealed class ControlledExecutor : IGraphRunExecutor
    {
        public int Calls;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CancellationSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<GraphRunResult> Acknowledge = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<GraphRunResult> ExecuteAsync(GraphRunRequest request, Action<string>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls); using var registration = cancellationToken.Register(() => CancellationSeen.TrySetResult());
            Started.TrySetResult();
            // A failing assertion must not leave the test process stuck in service.Dispose().
            return await Acknowledge.Task.WaitAsync(TimeSpan.FromSeconds(20)); // TEST-ONLY delayed acknowledgement with a fixture timeout.
        }
    }
    private sealed class CallbackExecutor(Func<GraphRunRequest, Task<GraphRunResult>> callback) : IGraphRunExecutor
    {
        public Task<GraphRunResult> ExecuteAsync(GraphRunRequest request, Action<string>? progress, CancellationToken cancellationToken) => callback(request);
    }
}
