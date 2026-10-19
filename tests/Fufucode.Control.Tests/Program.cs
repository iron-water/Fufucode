using Fufucode.Core.Automation;
using Fufucode.Plugins.AutoDev.Engine.Harnesses;
using Microsoft.Data.Sqlite;

// 控制链路按 harness id 校验协议适配器；适配器实现现在随自动开发插件发布。
BatchHarnessAdapters.Register();

if (args.Length >= 2 && args[0] == "--c4-protocol")
{
    await VerticalChecks.Protocol(args); return;
}
if (args.Length >= 2 && args[0] == "--conv3-protocol")
{
    await AutoAdvancementChecks.Protocol(args); return;
}
if (args.SequenceEqual(["--c4-only"]) || args.Length == 2 && args[0] == "--c4-live")
{
    Fufucode.App.Abstractions.Automation.Harnesses.HarnessRuntime? runtime = null;
    if (args[0] == "--c4-live")
    {
        if (!Path.IsPathFullyQualified(args[1])) throw new ArgumentException("Explicit absolute runtime file required");
        runtime = System.Text.Json.JsonSerializer.Deserialize<Fufucode.App.Abstractions.Automation.Harnesses.HarnessRuntime>(File.ReadAllText(args[1]))
            ?? throw new ArgumentException("Runtime file must contain an explicit deployment object; null cannot fall back to a fixture");
    }
    var evidence = Path.Combine(Path.GetTempPath(), "fufucode-c4-" + Guid.NewGuid().ToString("N")); var checks = 0;
    await VerticalChecks.Run(evidence, (value, label) => { if (!value) throw new Exception("FAIL " + label); checks++; Console.WriteLine("PASS " + label); }, runtime);
    Console.WriteLine($"PASS {checks} C4 checks. Evidence: {evidence}"); return;
}

if (args.Length >= 2 && args[0] == "--protocol")
{
    File.AppendAllText(args[1], "started\n");
    _ = await Console.In.ReadToEndAsync();
    Console.WriteLine("{\"type\":\"thread.started\",\"thread_id\":\"fixture-session\"}");
    Console.WriteLine("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"fixture result\"}}");
    Console.WriteLine("{\"type\":\"turn.completed\"}");
    return;
}
if (args.Length >= 4 && args[0] == "--adoption-protocol")
{
    await AdoptionChecks.Protocol(args[1], args[2], args[3]);
    return;
}
if (args.Length >= 3 && args[0] == "--repair-protocol")
{
    var prompt = await Console.In.ReadToEndAsync(); var resume = args.Contains("resume");
    var modelAt = Array.IndexOf(args, "--model");
    if (modelAt < 0 || args[modelAt + 1] != "devin/swe-2" || resume && !args.Contains("fixture-managed-session"))
        throw new Exception("Fixture runtime did not preserve explicit model and original session");
    File.AppendAllText(args[1], System.Text.Json.JsonSerializer.Serialize(new { resume, model = args[modelAt + 1], prompt }) + "\n");
    if (resume && File.Exists(args[2] + ".hold"))
    {
        var until = DateTime.UtcNow.AddSeconds(45);
        while (!File.Exists(args[2])) { if (DateTime.UtcNow > until) throw new TimeoutException(); await Task.Delay(25); }
    }
    Console.WriteLine("{\"type\":\"thread.started\",\"thread_id\":\"fixture-managed-session\"}");
    Console.WriteLine("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"fixture evidence\"}}");
    Console.WriteLine("{\"type\":\"turn.completed\"}"); return;
}

var root = Path.Combine(Path.GetTempPath(), "fufucode-control-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Console.WriteLine("Control evidence: " + root);
var path = Path.Combine(root, "project.db");
var count = 0;
void Check(bool value, string label)
{ if (!value) throw new Exception("FAIL " + label); count++; Console.WriteLine("PASS " + label); }
void Refused<T>(Action action, string label) where T : Exception
{
    try { action(); } catch (T) { Check(true, label); return; }
    throw new Exception("FAIL " + label + " was accepted");
}
WorkPackageSpec Spec(string id, string[]? deps = null) => new(id, id, "Complete a concrete local change",
    ["src/owned/**"], ["Behavior verified in temporary data"], "codex", "devin/swe-2", "code", deps ?? []);
using var store = new AutomationStore(path);
ControlCommand Define(string cmd, string id, long rev, long version = 0, string[]? deps = null) =>
    new(cmd, rev, "human-local", ControlAction.DefinePackage, Spec(id, deps), ExpectedPackageVersion: version);
ControlCommand Approve(string cmd, string id, long rev, long version) =>
    new(cmd, rev, "human-local", ControlAction.ApprovePackage, PackageId: id, ExpectedPackageVersion: version);
ControlCommand Mode(string cmd, long rev, DevelopmentMode mode) =>
    new(cmd, rev, "human-local", ControlAction.SetMode, Mode: mode);
var initial = store.ReadControlSnapshot();
Check(initial.Mode == DevelopmentMode.Manual && initial.Revision == 0, "new project defaults to manual");
var defineA = Define("define-a", "A", 0);
var receiptA = store.ApplyControlCommand(defineA, 1);
store.ApplyControlCommand(Approve("approve-a", "A", 1, 1), 2);
Check(store.ReadControlSnapshot().Intents.Count == 0, "manual approval creates no execution intent");
Check(store.ApplyControlCommand(defineA, 100) == receiptA, "old command replay returns its original receipt before CAS");
Check(store.ReadControlSnapshot().Revision == 2, "replay writes no new state or event");
Refused<InvalidOperationException>(() => store.ApplyControlCommand(defineA with { DecidedBy = "other" }, 3), "same command id cannot change actor");
Refused<InvalidOperationException>(() => store.ApplyControlCommand(Define("stale", "B", 0), 3), "stale client is refused");
Refused<ArgumentOutOfRangeException>(() => store.ApplyControlCommand(Mode("bogus", 2, (DevelopmentMode)99), 3), "an unknown mode value is refused");
Check(store.ReadControlSnapshot().Revision == 2, "failed commands leave revision unchanged");
// CONV-3: Automatic is a first-class delegated mode. Switching to it persists and materializes the
// same execution intents SemiAutomatic does, so an approved plan is actually runnable.
store.ApplyControlCommand(Mode("auto", 2, DevelopmentMode.Automatic), 3);
Check(store.ReadControlSnapshot().Mode == DevelopmentMode.Automatic, "automatic mode is accepted and persisted");
Check(store.ReadControlSnapshot().Intents.Single().Status == "awaiting_binding",
    "automatic mode creates the planning intent for the approved version");
store.ApplyControlCommand(Mode("semi", 3, DevelopmentMode.SemiAutomatic), 4);
var pending = store.ReadControlSnapshot().Intents.Single();
Check(pending.PackageId == "A" && pending.PackageVersion == 1 && pending.Status == "awaiting_binding",
    "explicit semi mode creates one planning intent for the approved version");
using (var c = new SqliteConnection("Data Source=" + path))
{
    c.Open(); using var q = c.CreateCommand();
    q.CommandText = "SELECT COUNT(*) FROM tasks";
    Check(Convert.ToInt64(q.ExecuteScalar()) == 0, "planning invents no registered task or execution");
}
store.ApplyControlCommand(Approve("approve-again", "A", 4, 1), 5);
Check(store.ReadControlSnapshot().Intents.Count == 1, "repeat approval never duplicates an intent");
store.ApplyControlCommand(Define("define-b", "B", 5, deps: ["A"]), 6);
Refused<ArgumentException>(() => store.ApplyControlCommand(Define("cycle", "A", 6, 1, ["B"]), 7), "dependency cycle rejected without mutation");
Refused<ArgumentException>(() => store.ApplyControlCommand(Define("missing", "C", 6, deps: ["absent"]), 7), "missing dependency is refused");
store.ApplyControlCommand(Define("revise-a", "A", 6, 1), 8);
Check(store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == "A") is { Version: 2, Approved: false },
    "editing a package requires a new approval");
Check(store.ReadControlSnapshot().Intents.Single().Status == "superseded", "old version intent cannot remain active");
Check(store.GetWorkPackageVersion("A", 1).Spec.Title == "A", "old specification remains readable");
Refused<InvalidOperationException>(() => store.ApplyControlCommand(Approve("old-approval", "A", 7, 1), 9), "old version approval refused");
store.ApplyControlCommand(Approve("approve-a2", "A", 7, 2), 10);
store.ApplyControlCommand(Mode("manual", 8, DevelopmentMode.Manual), 11);
Check(store.ReadControlSnapshot().Intents.Count(i => i.Status == "suspended") == 1, "manual mode suspends only pending intent");
store.ApplyControlCommand(Mode("semi-again", 9, DevelopmentMode.SemiAutomatic), 12);
Check(store.ReadControlSnapshot().Intents.Count(i => i.Status == "awaiting_binding") == 1,
    "reenter semi mode preserves intent identity and does not reactivate obsolete versions");
using var other = new AutomationStore(path);
Refused<InvalidOperationException>(() => other.ApplyControlCommand(Define("second-client", "D", 9), 13),
    "second store cannot decide against an old revision");
Check(other.ReadControlSnapshot().Revision == 10, "reopened U1 store reads the same authoritative state");
Check(other.ListEventsAfter(0).Count == 10, "control events share the U1 cursor and exclude failed/replayed commands");

// Abort late, after package mutation, event and receipt would have been written.
// The full decision must roll back, not merely reject before it starts.
using (var c = new SqliteConnection("Data Source=" + path))
{
    c.Open(); using var q = c.CreateCommand();
    q.CommandText = "CREATE TRIGGER control_abort BEFORE INSERT ON control_receipts BEGIN SELECT RAISE(ABORT,'injected failure'); END;";
    q.ExecuteNonQuery();
}
Refused<SqliteException>(() => store.ApplyControlCommand(Define("rollback", "D", 10), 14), "late persistence failure aborts the command");
Check(store.ReadControlSnapshot().Revision == 10 && store.ReadControlSnapshot().Packages.Count == 2,
    "rollback restores revision and package state");
Check(store.ListEventsAfter(0).Count == 10, "rollback leaves no orphan U1 event");
using (var c = new SqliteConnection("Data Source=" + path))
{
    c.Open(); using var q = c.CreateCommand(); q.CommandText = "DROP TRIGGER control_abort"; q.ExecuteNonQuery();
}
store.ApplyControlCommand(Define("rollback", "D", 10), 15);
Check(store.ReadControlSnapshot().Revision == 11, "rolled-back receipt does not poison a legitimate retry");

// A parent's changed contract invalidates direct and transitive approvals even if the
// dependent's own text did not change. All former decisions remain historical evidence.
var chainPath = Path.Combine(root, "chain.db");
using var chain = new AutomationStore(chainPath);
long chainRevision = 0;
foreach (var (id, dependencies) in new[] { ("upstream", Array.Empty<string>()), ("middle", new[] { "upstream" }), ("leaf", new[] { "middle" }) })
{
    chain.ApplyControlCommand(Define("define-" + id, id, chainRevision++, deps: dependencies), 20);
    chain.ApplyControlCommand(Approve("approve-" + id, id, chainRevision++, 1), 21);
}
chain.ApplyControlCommand(Mode("chain-semi", chainRevision++, DevelopmentMode.SemiAutomatic), 22);
Check(chain.ReadControlSnapshot().Intents.Count == 3, "chain has one planning intent per approved package");
chain.ApplyControlCommand(Define("change-upstream", "upstream", chainRevision++, 1), 23);
Check(chain.ReadControlSnapshot().Packages.All(p => !p.Approved), "upstream revision invalidates transitive dependent approvals");
Check(chain.ReadControlSnapshot().Intents.All(i => i.Status == "superseded"), "transitive obsolete intents cannot be consumed");
chain.ApplyControlCommand(Mode("chain-manual", chainRevision++, DevelopmentMode.Manual), 24);
chain.ApplyControlCommand(Mode("chain-semi2", chainRevision++, DevelopmentMode.SemiAutomatic), 25);
Check(chain.ReadControlSnapshot().Intents.All(i => i.Status == "superseded"), "mode switch cannot revive invalidated approvals");

// Simultaneous clients compete on one revision. SQLite admits only one decision.
var racePath = Path.Combine(root, "race.db");
using var raceA = new AutomationStore(racePath);
using var raceB = new AutomationStore(racePath);
using var barrier = new Barrier(2);
Task<bool> Decide(AutomationStore s, string id) => Task.Run(() =>
{
    barrier.SignalAndWait();
    try { s.ApplyControlCommand(Define("race-" + id, id, 0), 30); return true; }
    catch (InvalidOperationException) { return false; }
});
var race = await Task.WhenAll(Decide(raceA, "A"), Decide(raceB, "B"));
Check(race.Count(v => v) == 1, "concurrent clients apply exactly one decision at the same revision");
Check(raceA.ReadControlSnapshot().Revision == 1 && raceA.ListEventsAfter(0).Count == 1,
    "concurrent refusal leaves no extra receipt or event");
ReapprovalChecks.Run(root, Check);
await BindingChecks.Run(root, Check);
await ArchiveChecks.Run(root, Check);
await RelationChecks.Run(root, Check);
await KnowledgeChecks.Run(root, Check);
await AdoptionChecks.Run(root, Check);
await RestructureChecks.Run(root, Check);
await IntegrationChecks.Run(root, Check);
await RepairChecks.Run(root, Check);
await VerticalChecks.Run(Path.Combine(root, "vertical"), Check);
await VerticalChecks.Run(Path.Combine(root, "vertical-timeout"), Check, timeoutFixture: true);
await AutoAdvancementChecks.Run(Path.Combine(root, "conv3"), Check);
Console.WriteLine($"PASS {count} checks. Evidence: {root}");
