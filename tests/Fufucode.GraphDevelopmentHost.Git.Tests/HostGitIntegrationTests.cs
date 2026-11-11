using System.Diagnostics;
using System.Text.Json;
using Fufucode.App.Abstractions.Graph;
using Fufucode.App.Integration;
using Fufucode.Core;
using Fufucode.Core.Automation;
using Fufucode.Plugins.AutoDev.Graph;

var root = Path.Combine(Environment.GetEnvironmentVariable("FUFUCODE_GIT_TEST_ROOT") ?? Path.Combine(AppContext.BaseDirectory, "evidence"), "host-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? (OperatingSystem.IsWindows()
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe")
    : "/usr/bin/dotnet");
int assertions = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); assertions++; Console.WriteLine("PASS " + name); }
async Task Reject(Func<Task> work, string name)
{
    try { await work(); } catch (Exception e) when (e is ArgumentException or UnauthorizedAccessException or InvalidOperationException) { Check(true, name); return; }
    throw new Exception("Expected rejection: " + name);
}
async Task<string> Git(string repo, params string[] args)
{
    var start = new ProcessStartInfo("git") { WorkingDirectory = repo, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new Exception(await error); return (await output).Trim();
}
async Task<Fixture> NewFixture(string name)
{
    var directory = Path.Combine(root, name); var repo = Path.Combine(directory, "repo"); Directory.CreateDirectory(repo);
    await Git(repo, "init", "-b", "main"); await Git(repo, "config", "user.name", "Fixture"); await Git(repo, "config", "user.email", "fixture@localhost");
    await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "bin/\nobj/\n");
    await File.WriteAllTextAsync(Path.Combine(repo, "Fixture.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
    await File.WriteAllTextAsync(Path.Combine(repo, "Api.cs"), "public static class Api { public static int Value() => 1; }\n");
    await File.WriteAllTextAsync(Path.Combine(repo, "Other.cs"), "public static class Other { public static int Value() => 0; }\n");
    await File.WriteAllTextAsync(Path.Combine(repo, "Main.cs"), "class Entry { static void Main() { System.Console.WriteLine(Api.Value()); } }\n");
    await Git(repo, "add", "."); await Git(repo, "commit", "-m", "fixture baseline");
    var layout = new AppDataLayout(Path.Combine(directory, "data")); var executor = new Executor();
    var graph = new GraphProjectService(Path.Combine(directory, "graph"), executor); graph.SetProject(name, repo); graph.SetPurpose("Exercise real managed Git host delivery");
    return new(directory, repo, layout, executor, graph);
}
GraphDevelopmentHost Host(Fixture fixture, Func<GraphPublicationPermission, Task<bool>>? gate = null)
    => new(fixture.Layout, fixture.Graph, _ => Task.FromResult(true), _ => Task.FromResult(true), new ManagedValidationPolicy(["bin", "obj"]), gate);
async Task<(string Ticket, ManagedDevelopmentWorkspace Workspace)> Candidate(Fixture f, string name, string baseline, string file, string text)
{
    var created = await ManagedDevelopmentWorkspace.CreateFromBaseAsync(f.Repo, Path.Combine(f.Layout.Root, "auto-dev", "workspaces"), baseline);
    var workspace = created.Workspace ?? throw new Exception(created.Error);
    await File.WriteAllTextAsync(Path.Combine(workspace.Manifest.WorktreePath, file), text); await workspace.CaptureCandidateAsync();
    var ticket = f.Graph.CreateTicket(name, "Real Git Host fixture", [], ["verified"]);
    f.Executor.Manifests[ticket.Id] = workspace.ManifestPath; await f.Graph.RunTicketAsync(ticket.Id);
    for (var i = 0; i < 500 && !f.Graph.ReadSnapshot().Runs.Any(r => r.TicketId == ticket.Id && r.EndedUtc is not null); i++) await Task.Delay(10);
    Check(f.Graph.ReadSnapshot().Tickets.Single(t => t.Id == ticket.Id).Status == "Review", name + " executor settles in Review");
    return (ticket.Id, workspace);
}
async Task<(GraphCandidateValidation Candidate, GraphIntegratedValidation Validation, ManagedIntegrationTransaction Transaction)> Prepare(Fixture f, GraphDevelopmentHost host, (string Ticket, ManagedDevelopmentWorkspace Workspace) candidate, GraphValidationSpec spec)
{
    var verified = await host.ValidateCandidateAsync(candidate.Ticket, candidate.Workspace.ManifestPath, spec);
    Check(verified.Receipt.Status == ManagedValidationStatus.Passed, "real candidate command passes");
    var prepared = await host.IntegrateCandidateAsync(verified);
    var validated = await host.ValidateIntegratedAsync(verified, spec);
    return (verified, validated, prepared.Transaction!);
}
var build = new GraphValidationSpec(dotnet, ["build", "Fixture.csproj", "--nologo", "-v:q"], TimeSpan.FromMinutes(1));
var run = new GraphValidationSpec(dotnet, ["run", "--project", "Fixture.csproj", "--verbosity", "quiet"], TimeSpan.FromMinutes(1));
using var basic = await NewFixture("basic");
var baseline = await Git(basic.Repo, "rev-parse", "HEAD");
var first = await Candidate(basic, "first", baseline, "Other.cs", "public static class Other { public static int Value() => 2; }\n");
var host = Host(basic);
var ready = await Prepare(basic, host, first, build);
Check(ready.Validation.Receipt.Status == ManagedValidationStatus.Passed && host.VerifyIntegratedCurrent(ready.Validation), "actual prepared integration build passes");
Check(host.GetIntegrationJournalPath(ready.Validation) == ready.Transaction.JournalPath, "live validation resolves its actual journal for separate publication");
Check(await ManagedGitIntegration.GetIntegratedCommitAsync(basic.Repo) is null && await Git(basic.Repo, "rev-parse", "HEAD") == baseline, "prepared verification does not advance integrated or user ref");
await Reject(() => Task.Run(() => host.CompleteInternalWork(ready.Validation, ["wrong"], "active_goal", "fixture")), "invalid coverage cannot advance integrated ref");
Check(await ManagedGitIntegration.GetIntegratedCommitAsync(basic.Repo) is null, "invalid completion leaves ref absent");
await Reject(() => Task.Run(() => host.CompleteInternalWork(ready.Validation, ["verified"], "invalid", "fixture")), "invalid continuation rejected before commit");
var clone = JsonSerializer.Deserialize<GraphIntegratedValidation>(JsonSerializer.Serialize(ready.Validation))!;
Check(!host.VerifyIntegratedCurrent(clone), "serialized receipt never restores live authority");
// Recreate Host between prepare and commit; journal is evidence, never a completion capability.
var restarted = Host(basic);
Check(!restarted.VerifyIntegratedCurrent(ready.Validation), "new Host rejects old process live receipt");
var assessment = await restarted.RecoverIntegrationAsync(ready.Transaction.JournalPath);
Check(!assessment.DatabaseRecorded && !assessment.TicketDone && assessment.RequiresFreshValidation, "restart reports retained prepared transaction needing revalidation");
Check(!assessment.NoSideEffectsProven && !assessment.ManagedProcessStateKnown && assessment.IntegrationHeadMatches && assessment.IntegrationTreeClean,
    "clean journal recovery never fabricates proof of absent executor side effects");
var fresh = await restarted.ValidateCandidateAsync(first.Ticket, first.Workspace.ManifestPath, build);
var resumed = await restarted.ResumeIntegrationAsync(fresh, ready.Transaction.JournalPath);
var freshTarget = await restarted.ValidateIntegratedAsync(fresh, build);
// Simulate crash after Git succeeded and before DB writes. Reconciliation must not infer Done.
await ManagedGitIntegration.CommitAsync(resumed.Transaction!, resumed.Transaction!.PreparedCommit!, true);
var recoveryHost = Host(basic);
var gap = await recoveryHost.RecoverIntegrationAsync(ready.Transaction.JournalPath);
Check(gap.Transaction.Status == ManagedIntegrationStatus.Integrated && !gap.DatabaseRecorded && gap.RequiresFreshValidation, "Git success DB gap is explicitly pending revalidation");
Check(basic.Graph.ReadSnapshot().Tickets.Single(t => t.Id == first.Ticket).Status == "Review", "Git journal alone cannot mark Done");
var again = await recoveryHost.ValidateCandidateAsync(first.Ticket, first.Workspace.ManifestPath, build);
await recoveryHost.ResumeIntegrationAsync(again, ready.Transaction.JournalPath);
var final = await recoveryHost.ValidateIntegratedAsync(again, build);
recoveryHost.CompleteInternalWork(final, ["verified"], "active_goal", "fixture delivery");
recoveryHost.CompleteInternalWork(final, ["verified"], "active_goal", "fixture idempotent retry");
Check(basic.Graph.ReadSnapshot().Tickets.Single(t => t.Id == first.Ticket).Status == "Done", "fresh revalidation completes recovered integration");
Check(basic.Graph.ReadSnapshot().Entries.Count(e => e.TicketId == first.Ticket && e.Kind == "integration-version") == 1, "DB integration record idempotent");
var integratedHead = (await ManagedGitIntegration.GetIntegratedCommitAsync(basic.Repo))!;
var downstream = await ManagedDevelopmentWorkspace.CreateFromBaseAsync(basic.Repo, Path.Combine(basic.Layout.Root, "auto-dev", "workspaces"), integratedHead);
Check(File.ReadAllText(Path.Combine(downstream.Workspace!.Manifest.WorktreePath, "Other.cs")).Contains("=> 2"), "downstream reliable base inherits upstream code");
await Reject(() => recoveryHost.PublishIntegratedAsync(ready.Transaction.JournalPath, baseline), "integration approval is not publication approval");
var publisher = Host(basic, _ => Task.FromResult(true));
await File.WriteAllTextAsync(Path.Combine(basic.Repo, "private.txt"), "private");
await Reject(() => publisher.PublishIntegratedAsync(ready.Transaction.JournalPath, baseline), "untracked private user file blocks publication");
File.Delete(Path.Combine(basic.Repo, "private.txt"));
await File.AppendAllTextAsync(Path.Combine(basic.Repo, "Main.cs"), "// dirty\n"); await Git(basic.Repo, "add", "Main.cs");
await Reject(() => publisher.PublishIntegratedAsync(ready.Transaction.JournalPath, baseline), "dirty user index blocks publication");
await File.WriteAllTextAsync(Path.Combine(basic.Repo, "Main.cs"), "class Entry { static void Main() { System.Console.WriteLine(Api.Value()); } }\n"); await Git(basic.Repo, "add", "Main.cs");
var branchDriftPublisher = Host(basic, async permission =>
{
    Check(permission.TargetReference == "refs/heads/main", "publication permission identifies the exact target branch as well as HEAD");
    await Git(basic.Repo, "checkout", "-b", "unapproved-target"); return true;
});
await Reject(() => branchDriftPublisher.PublishIntegratedAsync(ready.Transaction.JournalPath, baseline), "same-HEAD branch switch during approval cannot redirect publication");
Check(await Git(basic.Repo, "rev-parse", "HEAD") == baseline, "unapproved branch retained unchanged");
await Git(basic.Repo, "checkout", "main");
Check(await publisher.ReadTargetHeadAsync() == baseline, "Host reads actual publication target SHA");
var published = await publisher.PublishIntegratedAsync(ready.Transaction.JournalPath);
Check(published.Status == ManagedIntegrationStatus.Published && await Git(basic.Repo, "rev-parse", "HEAD") == integratedHead && await Git(basic.Repo, "status", "--porcelain") == "", "authorized native ff publishes synchronized clean main");
Check((await Host(basic).RecoverIntegrationAsync(ready.Transaction.JournalPath)).TicketDone, "restart reconciles durable Done and publication without restoring live receipt");
await Reject(() => Host(basic).PublishIntegratedAsync(ready.Transaction.JournalPath, baseline), "historical publication receipt cannot authorize retry");
var quickGit = new GraphValidationSpec(OperatingSystem.IsWindows() ? "C:/Program Files/Git/cmd/git.exe" : "/usr/bin/git", ["--version"], TimeSpan.FromSeconds(10));
var stalePrepared = await ManagedGitIntegration.PrepareAsync(first.Workspace, Path.Combine(basic.Layout.Root, "auto-dev", "integration"));
var next = await Candidate(basic, "downstream delivery", integratedHead, "Extra.cs", "public static class Extra { }\n");
var nextHost = Host(basic); var nextReady = await Prepare(basic, nextHost, next, quickGit);
await nextHost.CompleteInternalWorkAsync(nextReady.Validation, ["verified"], "active_goal", "fixture downstream");
await recoveryHost.CompleteInternalWorkAsync(final, ["verified"], "active_goal", "idempotent ancestor recovery");
Check(await ManagedGitIntegration.GetIntegratedCommitAsync(basic.Repo) == nextReady.Transaction.PreparedCommit, "recovered old delivery never rewinds a later integrated descendant");
await Reject(() => ManagedGitIntegration.CommitAsync(stalePrepared, stalePrepared.PreparedCommit!, true), "genuinely stale uncommitted preparation rejects integrated-ref drift");
Check((await nextHost.RecoverIntegrationsAsync()).Any(r => r.Disposition == "UnboundGitJournalRequiresDecision"), "unbound legacy Git journal is surfaced rather than silently skipped");
basic.Graph.Dispose(); // Simulate an actual process owner restart, not a concurrent second writer.
using (var reopenedGraph = new GraphProjectService(Path.Combine(basic.Root, "graph"), basic.Executor))
{
    reopenedGraph.SetProject("basic", basic.Repo);
    var reopenedHost = Host(basic with { Graph = reopenedGraph });
    var durable = await reopenedHost.RecoverIntegrationAsync(ready.Transaction.JournalPath);
    Check(durable.DatabaseRecorded && durable.TicketDone && durable.Transaction.Status == ManagedIntegrationStatus.Published,
        "fresh Graph service reloads durable DB and reconciles published journal");
    Check(!reopenedHost.VerifyIntegratedCurrent(final), "fresh Graph service cannot restore old live verification authority");
    var missingDirectory = Path.Combine(basic.Layout.Root, "auto-dev", "integration", "integration-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(missingDirectory);
    var intent = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(ready.Transaction.JournalPath)!, "host-state.json")))!;
    intent["JournalPath"] = Path.Combine(missingDirectory, "journal.json"); intent["PreparedCommit"] = null; intent["Status"] = "Preparing"; intent["RepairTicketId"] = null;
    await File.WriteAllTextAsync(Path.Combine(missingDirectory, "host-state.json"), intent.ToJsonString());
    var missingJournal = await reopenedHost.RecoverIntegrationAsync(Path.Combine(missingDirectory, "journal.json"));
    Check(missingJournal.Transaction.Status == ManagedIntegrationStatus.Interrupted && !missingJournal.NoSideEffectsProven && missingJournal.RequiresFreshValidation,
        "pre-Git durable intent without journal is retained and never replayed as safe");
}

var gitExecutable = OperatingSystem.IsWindows() ? "C:/Program Files/Git/cmd/git.exe" : "/usr/bin/git";
var quick = new GraphValidationSpec(gitExecutable, ["--version"], TimeSpan.FromSeconds(10));
using var control = await NewFixture("control");
var controlBase = await Git(control.Repo, "rev-parse", "HEAD");
var controlCandidate = await Candidate(control, "controlled delivery", controlBase, "Other.cs", "public static class Other { public static int Value() => 3; }\n");
bool allowed = true;
var controlHost = new GraphDevelopmentHost(control.Layout, control.Graph, _ => Task.FromResult(allowed), _ => Task.FromResult(true));
var controlled = await Prepare(control, controlHost, controlCandidate, quick);
await Git(controlled.Transaction.WorktreePath, "-c", "user.name=test", "-c", "user.email=test@localhost", "commit", "--allow-empty", "-m", "integration version drift");
Check(!controlHost.VerifyIntegratedCurrent(controlled.Validation), "identical source but changed actual integration HEAD revokes receipt");
await Reject(() => Task.Run(() => controlHost.CompleteInternalWork(controlled.Validation, ["verified"], "active_goal", "must not complete")), "changed Git version cannot reuse integration validation");
await Git(controlled.Transaction.WorktreePath, "checkout", "--detach", controlled.Transaction.PreparedCommit!);
var beforeFailure = await controlHost.ValidateIntegratedAsync(controlled.Candidate, quick);
allowed = false;
var deniedRepeat = await controlHost.ValidateIntegratedAsync(controlled.Candidate, quick);
Check(deniedRepeat.Receipt.Status == ManagedValidationStatus.Denied && !controlHost.VerifyIntegratedCurrent(beforeFailure), "replacement failed or denied validation revokes earlier passing capability");
allowed = true;
var beforeVeto = await controlHost.ValidateIntegratedAsync(controlled.Candidate, quick);
control.Graph.VetoTicket(controlCandidate.Ticket, "fixture current human veto");
await Reject(() => Task.Run(() => controlHost.CompleteInternalWork(beforeVeto, ["verified"], "active_goal", "must not complete")), "current Veto hard-blocks stale passing integration receipt");
Check(await ManagedGitIntegration.GetIntegratedCommitAsync(control.Repo) is null, "Veto before completion leaves integrated ref untouched");

// Real candidate builds pass independently, but their mechanical merge breaks compilation.
using var compile = await NewFixture("compile-failure");
var cb = await Git(compile.Repo, "rev-parse", "HEAD");
var ca = await Candidate(compile, "api change", cb, "Api.cs", "public static class Api { public static int Value() => 1; public static int NewValue() => 1; }\n");
// A adds an API, B references a baseline method whose return contract changes in A: both candidates must compile separately.
await File.WriteAllTextAsync(Path.Combine(ca.Workspace.Manifest.WorktreePath, "Api.cs"), "public static class Api { public static string Value() => \"1\"; }\n"); await ca.Workspace.CaptureCandidateAsync();
var cbCandidate = await Candidate(compile, "old signature consumer", cb, "Other.cs", "public static class Other { public static int Value() => Api.Value(); }\n");
var compileHost = Host(compile);
var cr1 = await Prepare(compile, compileHost, ca, build); Check(cr1.Validation.Receipt.Status == ManagedValidationStatus.Passed, "API-change integration builds independently");
compileHost.CompleteInternalWork(cr1.Validation, ["verified"], "active_goal", "fixture api");
var cr2 = await Prepare(compile, compileHost, cbCandidate, build);
Check(cr2.Validation.Receipt.Status == ManagedValidationStatus.Failed && cr2.Validation.Receipt.ExitCode != 0, "real compiler rejects merged incompatible API");
await Reject(() => Task.Run(() => compileHost.CompleteInternalWork(cr2.Validation, ["verified"], "active_goal", "must fail")), "failed integration compiler receipt cannot complete");
Check(await ManagedGitIntegration.GetIntegratedCommitAsync(compile.Repo) == cr1.Transaction.PreparedCommit && compile.Graph.ReadSnapshot().Tickets.Single(t => t.Id == cbCandidate.Ticket).Status == "Review", "compiler failure leaves integrated version and DB Done unchanged");
Check(await Git(compile.Repo, "rev-parse", "HEAD") == cb && await Git(compile.Repo, "status", "--porcelain") == "", "compiler failure never dirties user main");
var repair = await compileHost.CreateIntegrationRepairAsync(cr2.Transaction.JournalPath, cr2.Validation.Receipt.StandardError?.Path ?? "real compiler output");
Check(repair.WorktreePath == cr2.Transaction.WorktreePath && repair.OriginalTicketId == cbCandidate.Ticket,
    "ordinary executor repair ticket binds actual failed integration tree");
Check((await compileHost.CreateIntegrationRepairAsync(cr2.Transaction.JournalPath, "retry")).RepairTicketId == repair.RepairTicketId,
    "repair ticket creation is idempotent");
Check(compile.Graph.ReadSnapshot().Tickets.Single(t => t.Id == repair.RepairTicketId).Acceptance.SequenceEqual(["verified"]), "repair preserves original acceptance criteria");
Check((await compileHost.GetIntegrationRepairBindingAsync(repair.RepairTicketId))!.WorktreePath == cr2.Transaction.WorktreePath,
    "executor resolver physically rechecks repair tree binding");
var fakeRepair = compile.Graph.CreateTicket("forged repair", "fixture forgery", [], ["verified"]);
compile.Graph.AddEntry(fakeRepair.Id, null, "integration-repair", JsonSerializer.Serialize(repair with { RepairTicketId = fakeRepair.Id }), repair.JournalPath);
await Reject(() => compileHost.GetIntegrationRepairBindingAsync(fakeRepair.Id), "model-writable Graph entry alone cannot forge repair execution binding");
await File.WriteAllTextAsync(Path.Combine(repair.WorktreePath, "Other.cs"), "public static class Other { public static int Value() => 0; }\n");
await Git(repair.WorktreePath, "add", "Other.cs"); await Git(repair.WorktreePath, "-c", "user.name=repair", "-c", "user.email=repair@localhost", "commit", "-m", "repair incompatible caller");
await ManagedGitIntegration.RefreshPreparedAsync(await ManagedGitIntegration.LoadAsync(repair.JournalPath));
var repairedCandidate = await compileHost.ValidateCandidateAsync(cbCandidate.Ticket, cbCandidate.Workspace.ManifestPath, build);
await compileHost.ResumeIntegrationAsync(repairedCandidate, repair.JournalPath);
var repairedValidation = await compileHost.ValidateIntegratedAsync(repairedCandidate, build);
Check(repairedValidation.Receipt.Status == ManagedValidationStatus.Passed, "actual repaired integration recompiles with fresh validation");
await compileHost.CompleteInternalWorkAsync(repairedValidation, ["verified"], "active_goal", "fixture compiler repair");
Check(compile.Graph.ReadSnapshot().Tickets.Single(t => t.Id == cbCandidate.Ticket).Status == "Done", "repaired original delivery completes only after new live merged-version validation");
await Reject(() => compileHost.GetIntegrationRepairBindingAsync(repair.RepairTicketId), "completed integration cannot be reopened by stale repair ticket");
await File.WriteAllTextAsync(Path.Combine(compile.Repo, "external.txt"), "external"); await Git(compile.Repo, "add", "."); await Git(compile.Repo, "commit", "-m", "external main advance");
await Reject(() => Host(compile, _ => Task.FromResult(true)).PublishIntegratedAsync(cr1.Transaction.JournalPath, cb), "publication main HEAD drift rejected safely");
Check(File.Exists(Path.Combine(compile.Repo, "external.txt")), "external main commit preserved");

// Real runtime assertion passes in each candidate but fails after merge.
using var tests = await NewFixture("test-failure");
await File.WriteAllTextAsync(Path.Combine(tests.Repo, "Api.cs"), "public static class Api { public static int Value() => 0; }\n");
await File.WriteAllTextAsync(Path.Combine(tests.Repo, "Main.cs"), "class Entry { static void Main() { if (Api.Value() + Other.Value() > 1) throw new System.Exception(\"fixture assertion\"); } }\n");
await Git(tests.Repo, "add", "."); await Git(tests.Repo, "commit", "-m", "runtime assertion baseline");
var tb = await Git(tests.Repo, "rev-parse", "HEAD");
var one = await Candidate(tests, "left", tb, "Api.cs", "public static class Api { public static int Value() => 1; }\n");
var two = await Candidate(tests, "right", tb, "Other.cs", "public static class Other { public static int Value() => 1; }\n");
var testHost = Host(tests); var tr1 = await Prepare(tests, testHost, one, run); testHost.CompleteInternalWork(tr1.Validation, ["verified"], "active_goal", "fixture one");
var tr2 = await Prepare(tests, testHost, two, run);
Check(tr2.Validation.Receipt.Status == ManagedValidationStatus.Failed && tr2.Validation.Receipt.ExitCode != 0, "real runtime integration assertion fails after successful compilation");
await Reject(() => Task.Run(() => testHost.CompleteInternalWork(tr2.Validation, ["verified"], "active_goal", "must fail")), "failed integration tests cannot complete");
Check(await ManagedGitIntegration.GetIntegratedCommitAsync(tests.Repo) == tr1.Transaction.PreparedCommit && tests.Graph.ReadSnapshot().Tickets.Single(t => t.Id == two.Ticket).Status == "Review", "integration test failure cannot advance ref or Done");
using (var automatic = await NewFixture("automatic-delivery"))
{
    var automaticBase = await Git(automatic.Repo, "rev-parse", "HEAD");
    automatic.Executor.OnRun = async request =>
    {
        var tools = new GraphToolHandler(automatic.Graph, request.ProjectPath, request.Identity);
        var proposed = await tools.ExecuteAsync(new Fufucode.Core.Backend.PureToolCall("automatic-proposal", "graph_propose_validation",
            JsonSerializer.Serialize(new { executable = dotnet, arguments = new[] { "build", "Fixture.csproj", "--nologo", "-v:q" },
                timeoutSeconds = 60, coveredCriteria = new[] { "verified" }, reason = "Real host compiler verification of the fixture" })));
        Check(proposed.Success, "live executor can propose verification without declaring success");
    };
    var automaticHost = Host(automatic);
    using var delivery = new GraphDeliveryCoordinator(automatic.Graph, automaticHost);
    var candidate = await Candidate(automatic, "automatic", automaticBase, "Other.cs", "public static class Other { public static int Value() => 3; }\n");
    for (var i = 0; i < 1800 && automatic.Graph.ReadSnapshot().Tickets.Single(t => t.Id == candidate.Ticket).Status != "Done"; i++) await Task.Delay(50);
    Check(automatic.Graph.ReadSnapshot().Tickets.Single(t => t.Id == candidate.Ticket).Status == "Done", "natural terminal checkpoint automatically verifies integrates and completes internal work");
    Check(await ManagedGitIntegration.GetIntegratedCommitAsync(automatic.Repo) is not null && await Git(automatic.Repo, "rev-parse", "HEAD") == automaticBase,
        "automatic internal delivery preserves user main without publication authorization");
}
using (var vetoDuringValidation = await NewFixture("validation-veto"))
{
    var baseCommit = await Git(vetoDuringValidation.Repo, "rev-parse", "HEAD");
    var candidate = await Candidate(vetoDuringValidation, "veto during validation", baseCommit, "Main.cs",
        "class Entry { static void Main() { System.Threading.Thread.Sleep(20000); } }\n");
    var validationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var controlledHost = new GraphDevelopmentHost(vetoDuringValidation.Layout, vetoDuringValidation.Graph,
        _ => { validationEntered.TrySetResult(); return Task.FromResult(true); }, _ => Task.FromResult(true),
        new ManagedValidationPolicy(["bin", "obj"]));
    var validation = controlledHost.ValidateCandidateAsync(candidate.Ticket, candidate.Workspace.ManifestPath, run);
    await validationEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
    await Task.Delay(250);
    vetoDuringValidation.Graph.VetoTicket(candidate.Ticket, "Human revoked this running verification");
    GraphCandidateValidation? cancelled = null;
    var cancellationAcknowledged = false;
    try { cancelled = await validation; cancellationAcknowledged = cancelled.Receipt.Status is ManagedValidationStatus.Cancelled or ManagedValidationStatus.Denied; }
    catch (OperationCanceledException) { cancellationAcknowledged = true; }
    Check(cancellationAcknowledged,
        "unread human Veto cancels in-flight host validation, not only its eventual completion");
    Check((cancelled is null || !controlledHost.VerifyCurrent(cancelled)) && await ManagedGitIntegration.GetIntegratedCommitAsync(vetoDuringValidation.Repo) is null,
        "revoked host process cannot authorize integration");
}
Console.WriteLine($"PASS {assertions} Host/Git assertions; evidence {root}");

sealed class Executor : IGraphRunExecutor
{
    public Dictionary<string, string> Manifests { get; } = [];
    public Func<GraphRunRequest, Task>? OnRun { get; set; }
    public async Task<GraphRunResult> ExecuteAsync(GraphRunRequest request, Action<string>? progress, CancellationToken cancellationToken)
    {
        if (OnRun is not null) await OnRun(request);
        return new GraphRunResult("completed", "fixture candidate") { CandidateManifestPath = Manifests[request.TicketId] };
    }
}
sealed record Fixture(string Root, string Repo, AppDataLayout Layout, Executor Executor, GraphProjectService Graph) : IDisposable
{
    public void Dispose() => Graph.Dispose();
}
