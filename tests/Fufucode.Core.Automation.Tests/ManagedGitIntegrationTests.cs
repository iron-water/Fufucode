using System.Diagnostics;
using System.Text.Json;
using Fufucode.Core.Automation;

// Real Git integration regression executable, deliberately independent of App/Kernel fixtures.
var root = Path.Combine(Environment.GetEnvironmentVariable("FUFUCODE_GIT_TEST_ROOT") ?? Path.Combine(AppContext.BaseDirectory, "git-test-evidence"), "managed-git-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var repo = Path.Combine(root, "repo");
Directory.CreateDirectory(repo);
var sandbox = Path.Combine(root, "sandbox");
async Task<string> Git(string tree, params string[] arguments)
{
    var start = new ProcessStartInfo("git") { WorkingDirectory = tree, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in arguments) start.ArgumentList.Add(a);
    using var p = Process.Start(start)!;
    var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
    await p.WaitForExitAsync();
    if (p.ExitCode != 0) throw new Exception(await error);
    return (await output).Trim();
}
int assertions = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); assertions++; Console.WriteLine("PASS " + name); }
async Task Reject(Func<Task> action, string name) { try { await action(); } catch (InvalidOperationException) { Check(true, name); return; } catch (UnauthorizedAccessException) { Check(true, name); return; } throw new Exception("Expected rejection: " + name); }
await Git(repo, "init", "-b", "main");
await Git(repo, "config", "user.name", "Git test"); await Git(repo, "config", "user.email", "test@localhost");
await File.WriteAllTextAsync(Path.Combine(repo, "base.txt"), "base\n");
await Git(repo, "add", "."); await Git(repo, "commit", "-m", "base");
var baseline = await Git(repo, "rev-parse", "HEAD");
async Task<ManagedDevelopmentWorkspace> Workspace(string commit)
{
    var creation = await ManagedDevelopmentWorkspace.CreateFromBaseAsync(repo, sandbox, commit);
    return creation.Workspace ?? throw new Exception(creation.Error);
}
var a = await Workspace(baseline); var b = await Workspace(baseline);
await File.WriteAllTextAsync(Path.Combine(a.Manifest.WorktreePath, "a.txt"), "a\n");
await File.WriteAllBytesAsync(Path.Combine(a.Manifest.WorktreePath, "binary.dat"), [0, 255, 10, 0]);
await File.WriteAllTextAsync(Path.Combine(b.Manifest.WorktreePath, "b.txt"), "b\n");
await a.CaptureCandidateAsync(); await b.CaptureCandidateAsync();
Check(a.Manifest.CandidateCommit is not null && a.Manifest.CandidateRef is not null, "candidate is durable commit/ref");
Check(await Git(repo, "status", "--porcelain") == "", "capture leaves user tree clean");
using (var stopped = new CancellationTokenSource())
{
    stopped.Cancel();
    try { await ManagedGitIntegration.PrepareAsync(a, sandbox, stopped.Token); throw new Exception("Cancellation expected"); }
    catch (OperationCanceledException) { Check(File.Exists(a.Manifest.PatchPath) && Directory.Exists(a.Manifest.WorktreePath), "stopped prepare preserves candidate and execution tree"); }
}
var ta = await ManagedGitIntegration.PrepareAsync(a, sandbox);
Check(ta.Status == ManagedIntegrationStatus.Prepared, "prepare independent tree");
await File.WriteAllTextAsync(ta.JournalPath, JsonSerializer.Serialize(ta with { Status = ManagedIntegrationStatus.Preparing, PreparedCommit = null }));
ta = await ManagedGitIntegration.RecoverAsync(ta.JournalPath);
Check(ta.Status == ManagedIntegrationStatus.Prepared && ta.PreparedCommit is not null, "interrupted prepare reconciles merged version without validation authority");
Check(await ManagedGitIntegration.GetIntegratedCommitAsync(repo) is null, "prepare does not advance integrated ref");
await Reject(() => ManagedGitIntegration.CommitAsync(ta, ta.PreparedCommit!, false), "validation authority required");
await Reject(() => ManagedGitIntegration.CommitAsync(ta, baseline, true), "validation version bound");
ta = await ManagedGitIntegration.CommitAsync(ta, ta.PreparedCommit!, true);
await File.WriteAllTextAsync(ta.JournalPath, JsonSerializer.Serialize(ta with { Status = ManagedIntegrationStatus.Committing }));
Check((await ManagedGitIntegration.RecoverAsync(ta.JournalPath)).Status == ManagedIntegrationStatus.Integrated, "commit interruption reconciliation");
Check((await ManagedGitIntegration.CommitAsync(ta, ta.PreparedCommit!, true)).Status == ManagedIntegrationStatus.Integrated, "commit retry idempotent");
Check(await Git(repo, "rev-parse", "HEAD") == baseline, "commit does not publish");
var tb = await ManagedGitIntegration.PrepareAsync(b, sandbox);
await File.WriteAllTextAsync(Path.Combine(tb.WorktreePath, "unverified.txt"), "mutation");
await Reject(() => ManagedGitIntegration.CommitAsync(tb, tb.PreparedCommit!, true), "integration untracked changes invalidate validation");
File.Delete(Path.Combine(tb.WorktreePath, "unverified.txt"));
var checkedOutIntegrated = Path.Combine(root, "checked-out-integrated");
await Git(repo, "worktree", "add", checkedOutIntegrated, "autodev/integrated");
await Reject(() => ManagedGitIntegration.CommitAsync(tb, tb.PreparedCommit!, true), "checked-out managed ref cannot desynchronize live worktree");
await Git(repo, "worktree", "remove", checkedOutIntegrated);
Check(File.Exists(Path.Combine(tb.WorktreePath, "a.txt")) && File.Exists(Path.Combine(tb.WorktreePath, "b.txt")), "stale-base candidates three-way merge");
Check((await File.ReadAllBytesAsync(Path.Combine(tb.WorktreePath, "binary.dat"))).SequenceEqual(new byte[] {0,255,10,0}), "binary preserved");
tb = await ManagedGitIntegration.CommitAsync(tb, tb.PreparedCommit!, true);
var child = await Workspace(tb.PreparedCommit!);
Check(File.Exists(Path.Combine(child.Manifest.WorktreePath, "a.txt")) && File.Exists(Path.Combine(child.Manifest.WorktreePath, "b.txt")), "downstream inherits integrated code");
await Reject(() => ManagedGitIntegration.PublishAsync(tb, false, baseline), "separate publication authorization");
await File.WriteAllTextAsync(Path.Combine(repo, "private.txt"), "private");
await Reject(() => ManagedGitIntegration.PublishAsync(tb, true, baseline), "untracked blocks publication");
var dirtyChild = await Workspace(tb.PreparedCommit!);
Check(File.Exists(Path.Combine(dirtyChild.Manifest.WorktreePath, "b.txt")), "explicit base independent of dirty main");
File.Delete(Path.Combine(repo, "private.txt"));
await File.WriteAllTextAsync(Path.Combine(repo, "base.txt"), "dirty\n");
await Git(repo, "add", "base.txt");
await Reject(() => ManagedGitIntegration.PublishAsync(tb, true, baseline), "dirty index blocks publication");
await File.WriteAllTextAsync(Path.Combine(repo, "base.txt"), "base\n"); await Git(repo, "add", "base.txt");
tb = await ManagedGitIntegration.PublishAsync(tb, true, baseline);
Check(await Git(repo, "status", "--porcelain") == "" && File.Exists(Path.Combine(repo, "b.txt")), "native ff synchronizes index and worktree");
Check((await ManagedGitIntegration.PublishAsync(tb, true, baseline)).Status == ManagedIntegrationStatus.Published, "publication retry idempotent");
var interrupted = tb with { Status = ManagedIntegrationStatus.Publishing };
await File.WriteAllTextAsync(tb.JournalPath, JsonSerializer.Serialize(interrupted));
Check((await ManagedGitIntegration.RecoverAsync(tb.JournalPath)).Status == ManagedIntegrationStatus.Published, "publication interruption reconciliation");
var c = await Workspace(tb.PreparedCommit!); var d = await Workspace(tb.PreparedCommit!);
await File.WriteAllTextAsync(Path.Combine(c.Manifest.WorktreePath, "base.txt"), "c\n");
await File.WriteAllTextAsync(Path.Combine(d.Manifest.WorktreePath, "base.txt"), "d\n");
await c.CaptureCandidateAsync(); await d.CaptureCandidateAsync();
var tc = await ManagedGitIntegration.PrepareAsync(c, sandbox);
tc = await ManagedGitIntegration.CommitAsync(tc, tc.PreparedCommit!, true);
var td = await ManagedGitIntegration.PrepareAsync(d, sandbox);
Check(td.Status == ManagedIntegrationStatus.Conflict && Directory.Exists(td.WorktreePath), "conflict tree retained");
Check((await ManagedGitIntegration.LoadAsync(td.JournalPath)).Status == ManagedIntegrationStatus.Conflict, "conflict journal recoverable");
await File.WriteAllTextAsync(Path.Combine(td.WorktreePath, "base.txt"), "resolved\n");
await Git(td.WorktreePath, "add", "."); await Git(td.WorktreePath, "-c", "user.name=test", "-c", "user.email=test@localhost", "commit", "-m", "resolve");
td = await ManagedGitIntegration.RefreshPreparedAsync(td);
Check(td.Status == ManagedIntegrationStatus.Prepared, "resolved merge requires fresh version validation");
await Git(td.WorktreePath, "-c", "user.name=test", "-c", "user.email=test@localhost", "commit", "--allow-empty", "-m", "drift");
await Reject(() => ManagedGitIntegration.CommitAsync(td, td.PreparedCommit!, true), "integration HEAD drift blocks stale validation");
await Reject(() => ManagedGitIntegration.CommitAsync(ta, ta.PreparedCommit!, true), "integrated ref drift rejects stale transaction");
await File.WriteAllTextAsync(Path.Combine(repo, "external.txt"), "external\n"); await Git(repo, "add", "."); await Git(repo, "commit", "-m", "external advance");
await Reject(() => ManagedGitIntegration.PublishAsync(tc, true, tb.PreparedCommit!), "user HEAD drift preserved");
Check(File.Exists(Path.Combine(repo, "external.txt")), "user drift code retained");
Console.WriteLine($"PASS {assertions} real-Git assertions; retained evidence {root}");
