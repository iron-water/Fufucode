using System.Diagnostics;
using Fufucode.App.Abstractions.Graph;
using Fufucode.App.Integration;
using Fufucode.Core;
using Fufucode.Core.Automation;
using Fufucode.Plugins.AutoDev.Graph;

var root = Path.Combine(Environment.GetEnvironmentVariable("FUFUCODE_TEST_ROOT") ?? Path.GetTempPath(), "fufucode-legacy-host-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var repo = Path.Combine(root, "repo"); Directory.CreateDirectory(repo);
async Task Git(params string[] args)
{
    var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync();
    await o; if (p.ExitCode != 0) throw new Exception(await e);
}
void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS " + text); }
async Task Reject(Func<Task> action) { try { await action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or UnauthorizedAccessException) { return; } throw new Exception("Expected rejection"); }
await Git("init"); await Git("config", "user.email", "fixture@example.invalid"); await Git("config", "user.name", "Fixture");
await File.WriteAllTextAsync(Path.Combine(repo, "source.txt"), "base\n"); await Git("add", "."); await Git("commit", "-m", "fixture baseline");
var layout = new AppDataLayout(Path.Combine(root, "data"));
var creation = await ManagedDevelopmentWorkspace.CreateAsync(repo, Path.Combine(layout.Root, "auto-dev", "workspaces"));
var workspace = creation.Workspace!;
await File.WriteAllTextAsync(Path.Combine(workspace.Manifest.WorktreePath, "source.txt"), "candidate\n");
await workspace.CaptureCandidateAsync();
var original = await File.ReadAllBytesAsync(workspace.Manifest.PatchPath);
using var graph = new GraphProjectService(Path.Combine(root, "graph"), new Executor(workspace.ManifestPath));
graph.SetProject("fixture", repo); graph.SetPurpose("Test application host safely");
var ticket = graph.CreateTicket("fixture work", "verify host boundary", [], ["candidate verified"]);
await graph.RunTicketAsync(ticket.Id);
while (!graph.ReadSnapshot().Runs.Any(r => r.TicketId == ticket.Id && r.EndedUtc != null)) await Task.Delay(20);
var gitExe = OperatingSystem.IsWindows() ? "C:/Program Files/Git/cmd/git.exe" : "/usr/bin/git";
var spec = new GraphValidationSpec(gitExe, ["--version"], TimeSpan.FromSeconds(10));
var deniedHost = new GraphDevelopmentHost(layout, graph, null);
var denied = await deniedHost.ValidateCandidateAsync(ticket.Id, workspace.ManifestPath, spec);
Check(denied.Receipt.Status == ManagedValidationStatus.Denied, "missing gate denies");
int approvals = 0;
bool failGate = false;
var host = new GraphDevelopmentHost(layout, graph, _ => { approvals++; if (failGate) throw new IOException("explicit fixture approval failure"); return Task.FromResult(true); }, _ => Task.FromResult(true));
var result = await host.ValidateCandidateAsync(ticket.Id, workspace.ManifestPath, spec);
Check(result.Receipt.Status == ManagedValidationStatus.Passed && host.VerifyCurrent(result), "live approval and receipt verify");
Check(!host.VerifyCurrent(result with { }), "copied result not trusted");
var originalArgument = result.Contract.Arguments[0];
result.Contract.Arguments[0] = "status";
Check(!host.VerifyCurrent(result), "contract mutation revokes authority");
result.Contract.Arguments[0] = originalArgument;
var noIntegration = new GraphDevelopmentHost(layout, graph, _ => Task.FromResult(true));
var gated = await noIntegration.ValidateCandidateAsync(ticket.Id, workspace.ManifestPath, spec);
await Reject(() => noIntegration.IntegrateCandidateAsync(gated));
Check(true, "missing independent integration gate rejects");
var retained = await File.ReadAllBytesAsync(workspace.Manifest.PatchPath);
Check(original.SequenceEqual(retained), "validation preserves candidate patch");
await Reject(() => host.ValidateCandidateAsync(ticket.Id, Path.Combine(root, "manifest.json"), spec));
Check(approvals == 1, "outside manifest rejected before approval");
await File.AppendAllTextAsync(Path.Combine(workspace.Manifest.WorktreePath, "source.txt"), "tamper\n");
Check(!host.VerifyCurrent(result), "workspace mutation revokes live authority");
await Reject(() => host.ValidateCandidateAsync(ticket.Id, workspace.ManifestPath, spec));
await File.WriteAllTextAsync(Path.Combine(workspace.Manifest.WorktreePath, "source.txt"), "candidate\n");
var integration = await host.IntegrateCandidateAsync(result);
Check(integration.RequiresPostIntegrationValidation && File.ReadAllText(Path.Combine(repo, "source.txt")).Replace("\r\n", "\n") == "base\n", "integration leaves user tree unchanged and requires isolated target validation");
var integrationTree = integration.Transaction!.WorktreePath;
var appliedSource = await File.ReadAllBytesAsync(Path.Combine(integrationTree, "source.txt"));
await File.AppendAllTextAsync(Path.Combine(integrationTree, "source.txt"), "unexpected\n");
await Reject(() => host.ValidateIntegratedAsync(result, spec));
await File.WriteAllBytesAsync(Path.Combine(integrationTree, "source.txt"), appliedSource);
var target = await host.ValidateIntegratedAsync(result, spec);
Check(host.VerifyIntegratedCurrent(target), "integrated target live receipt verifies");
failGate = true;
var failedTarget = await host.ValidateIntegratedAsync(result, spec);
Check(failedTarget.Receipt.Status == ManagedValidationStatus.Failed && !host.VerifyIntegratedCurrent(failedTarget), "failed target validation cannot authorize completion");
failGate = false;
Check(!host.VerifyIntegratedCurrent(target), "replacement failed validation invalidates earlier passing capability");
target = await host.ValidateIntegratedAsync(result, spec);
Check(host.VerifyIntegratedCurrent(target), "fresh successful revalidation restores current capability");
Check(!host.VerifyIntegratedCurrent(target with { }), "copied target result has no authority");
await Reject(() => Task.Run(() => host.CompleteInternalWork(target, ["not an acceptance criterion"], "active_goal", "Fixture validates host plumbing only")));
host.CompleteInternalWork(target, ["candidate verified"], "active_goal", "Fixture-only accepted coverage; not a real product quality claim");
Check(graph.ReadSnapshot().Tickets.Single(t => t.Id == ticket.Id).Status == "Done", "explicit host coverage and live target receipt complete internal work");
graph.SendOrganizationMessage("late-interface-change", new GraphMessageDraft("project-lead", false, "interface",
    "Fixture interface changed after completion", ticket.Id, null, graph.ReadSnapshot().Revision));
Check(graph.ReadSnapshot().Tickets.Single(t => t.Id == ticket.Id).Status == "Done" &&
    !graph.ReadOrganizationSnapshot().Barriers.Single(b => b.TicketId == ticket.Id).RequiresReview,
    "ordinary interface notification cannot revoke verified completion");
await File.AppendAllTextAsync(Path.Combine(integrationTree, "source.txt"), "target mutation\n");
Check(!host.VerifyIntegratedCurrent(target), "target source mutation revokes receipt");
await File.WriteAllTextAsync(Path.Combine(integrationTree, "source.txt"), "candidate\n");
await File.WriteAllTextAsync(Path.Combine(integrationTree, "test.txt"), "new test\n");
Check(!host.VerifyIntegratedCurrent(target), "target test mutation revokes receipt");
await Reject(() => host.ValidateIntegratedAsync(result, spec with { Arguments = ["status"] }));
await Git("-C", integrationTree, "add", "."); await Git("-C", integrationTree, "commit", "-m", "changed target head");
Check(!host.VerifyIntegratedCurrent(target), "target HEAD change revokes receipt");
await Reject(() => host.ValidateIntegratedAsync(result, spec));
Console.WriteLine("PASS host fixture retained at " + root);

sealed class Executor(string manifest) : IGraphRunExecutor
{
    public Task<GraphRunResult> ExecuteAsync(GraphRunRequest request, Action<string>? progress, CancellationToken cancellationToken)
        => Task.FromResult(new GraphRunResult("completed", "fixture candidate") { CandidateManifestPath = manifest });
}
