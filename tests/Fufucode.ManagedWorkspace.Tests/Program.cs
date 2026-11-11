using System.Diagnostics;
using System.Text.Json;
using Fufucode.Core.Automation;

// Every Git mutation is confined to a new fixture under the explicitly supplied sandbox.
if (args.Length != 1) throw new ArgumentException("Pass an absolute fixture sandbox directory.");
var root = Path.Combine(Path.GetFullPath(args[0]), "managed-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
async Task Check(string name, Func<Task> test)
{
    await test();
    Console.WriteLine("PASS " + name);
    passed++;
}
void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
async Task Reject(Func<Task> action)
{
    try { await action(); }
    catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException or ArgumentException) { return; }
    throw new Exception("Expected refusal.");
}
async Task<string> Git(string cwd, params string[] arguments)
{
    var info = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false,
        RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new Exception(await error);
    return await output;
}
async Task<(string Repo, string Sandbox)> Fixture()
{
    var path = Path.Combine(root, Guid.NewGuid().ToString("N"));
    var repo = Path.Combine(path, "repository with spaces");
    Directory.CreateDirectory(repo);
    await Git(repo, "init");
    await Git(repo, "config", "user.email", "fixture@example.invalid");
    await Git(repo, "config", "user.name", "Managed Workspace Fixture");
    await Git(repo, "config", "core.autocrlf", "false");
    await Git(repo, "config", "core.longpaths", "true");
    await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "original\n");
    await File.WriteAllTextAsync(Path.Combine(repo, "delete.txt"), "delete\n");
    await File.WriteAllBytesAsync(Path.Combine(repo, "binary.bin"), [0, 1, 2, 3, 0, 255]);
    await Git(repo, "add", "--", ".");
    await Git(repo, "commit", "-m", "Fixture baseline");
    return (repo, Path.Combine(path, "application sandbox"));
}
async Task<ManagedDevelopmentWorkspace> Create(string repo, string sandbox, string? branch = null)
{
    var result = await ManagedDevelopmentWorkspace.CreateAsync(repo, sandbox, branch);
    Assert(result.Status == ManagedWorkspaceStatus.Ready, result.Error ?? result.Status.ToString());
    return result.Workspace!;
}
await Check("non-Git needs authorization and does not initialize", async () =>
{
    var path = Path.Combine(root, "not git");
    Directory.CreateDirectory(path);
    var result = await ManagedDevelopmentWorkspace.CreateAsync(path, Path.Combine(root, "sandbox"));
    Assert(result.Status == ManagedWorkspaceStatus.NeedsRepositoryAuthorization, "Wrong non-Git status.");
    Assert(!Directory.Exists(Path.Combine(path, ".git")), "Unexpected git init.");
    Assert((await ManagedDevelopmentWorkspace.CreateAsync(Path.Combine(root, "missing"), Path.Combine(root, "sandbox"))).Status == ManagedWorkspaceStatus.NeedsRepositoryAuthorization, "Missing path did not require authorization.");
});
await Check("dirty tracked, staged and untracked baselines refused", async () =>
{
    var (repo, sandbox) = await Fixture();
    await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "user\n");
    Assert((await ManagedDevelopmentWorkspace.CreateAsync(repo, sandbox)).Status == ManagedWorkspaceStatus.BaselineDirty, "Tracked dirty accepted.");
    await Git(repo, "add", "--", "tracked.txt");
    Assert((await ManagedDevelopmentWorkspace.CreateAsync(repo, sandbox)).Status == ManagedWorkspaceStatus.BaselineDirty, "Staged dirty accepted.");
    await Git(repo, "commit", "-m", "Fixture change");
    await File.WriteAllTextAsync(Path.Combine(repo, "user.txt"), "user");
    Assert((await ManagedDevelopmentWorkspace.CreateAsync(repo, sandbox)).Status == ManagedWorkspaceStatus.BaselineDirty, "Untracked dirty accepted.");
    Assert(!Directory.Exists(sandbox), "Dirty check created sandbox.");
});
await Check("binary, deletion and untracked capture; private indexes; direct integration stays disabled", async () =>
{
    var (repo, sandbox) = await Fixture();
    var workspace = await Create(repo, sandbox, "candidate/test");
    await Git(repo, "config", "diff.noprefix", "true");
    var tree = workspace.Manifest.WorktreePath;
    await File.WriteAllTextAsync(Path.Combine(tree, "tracked.txt"), "candidate\n");
    File.Delete(Path.Combine(tree, "delete.txt"));
    await File.WriteAllBytesAsync(Path.Combine(tree, "binary.bin"), [0, 255, 42, 0, 100]);
    await File.WriteAllBytesAsync(Path.Combine(tree, "new binary.bin"), [0, 3, 4, 255]);
    await File.WriteAllTextAsync(Path.Combine(tree, "new text.txt"), "new\n");
    var stagedBefore = await Git(tree, "diff", "--cached");
    var manifest = await workspace.CaptureCandidateAsync();
    Assert(manifest.ChangedFiles.Length == 5, "Missing candidate files.");
    Assert(manifest.PatchSize == new FileInfo(manifest.PatchPath).Length, "Wrong patch size.");
    Assert((await File.ReadAllTextAsync(manifest.PatchPath)).Contains("GIT binary patch"), "Binary patch missing.");
    Assert(await Git(tree, "diff", "--cached") == stagedBefore, "Candidate index polluted.");
    Assert(string.IsNullOrEmpty(await Git(repo, "status", "--porcelain")), "User main tree polluted.");
    await Reject(() => workspace.IntegrateAsync(false, manifest.PatchHash));
    await Reject(() => workspace.IntegrateAsync(true, "not approved hash"));
    // Integration now requires ManagedGitIntegration's prepare/validate/commit/publish
    // transaction, covered by Core.Automation.Tests; this legacy entry must never apply.
    await Reject(() => workspace.IntegrateAsync(true, manifest.PatchHash));
    Assert(await File.ReadAllTextAsync(Path.Combine(repo, "tracked.txt")) == "original\n", "Legacy entry changed user text.");
    Assert((await File.ReadAllBytesAsync(Path.Combine(repo, "binary.bin"))).SequenceEqual(new byte[] { 0, 1, 2, 3, 0, 255 }), "Legacy entry changed user binary.");
    Assert(File.Exists(Path.Combine(repo, "delete.txt")) && !File.Exists(Path.Combine(repo, "new binary.bin")), "Legacy entry applied candidate paths.");
    Assert(string.IsNullOrEmpty(await Git(repo, "status", "--porcelain")), "Legacy entry changed the user index or tree.");
    Assert((await Git(repo, "rev-parse", "HEAD")).Trim() == manifest.BaseHead, "Legacy entry changed HEAD.");
    await Reject(() => workspace.RemoveAsync(true));
    Assert(Directory.Exists(tree), "Dirty tree lost.");
    var disk = JsonSerializer.Deserialize<ManagedWorkspaceManifest>(await File.ReadAllTextAsync(workspace.ManifestPath))!;
    Assert(disk.Status == ManagedWorkspaceStatus.Failed && File.Exists(disk.PatchPath), "Failure/candidate not preserved.");
});
await Check("cancellation retains candidate and worktree", async () =>
{
    var (repo, sandbox) = await Fixture();
    var workspace = await Create(repo, sandbox);
    await File.WriteAllTextAsync(Path.Combine(workspace.Manifest.WorktreePath, "tracked.txt"), "candidate\n");
    var manifest = await workspace.CaptureCandidateAsync();
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try { await workspace.CaptureCandidateAsync(cancellation.Token); throw new Exception("Cancellation ignored."); }
    catch (OperationCanceledException) { }
    Assert(File.Exists(manifest.PatchPath) && Directory.Exists(manifest.WorktreePath), "Cancellation lost candidate.");
});
await Check("safe cleanup, detached HEAD, nested-root and sandbox refusal", async () =>
{
    var (repo, sandbox) = await Fixture();
    var workspace = await Create(repo, sandbox);
    Assert((await Git(workspace.Manifest.WorktreePath, "rev-parse", "--abbrev-ref", "HEAD")).Trim() == "HEAD", "Not detached.");
    await Reject(() => workspace.RemoveAsync(false));
    await File.WriteAllTextAsync(Path.Combine(workspace.Manifest.WorktreePath, "committed.txt"), "undelivered\n");
    await Git(workspace.Manifest.WorktreePath, "add", "--", ".");
    await Git(workspace.Manifest.WorktreePath, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "Candidate commit");
    var candidate = await workspace.CaptureCandidateAsync();
    Assert(candidate.ChangedFiles.Contains("committed.txt"), "Committed candidate omitted.");
    await Reject(() => workspace.RemoveAsync(true));
    var clean = await Create(repo, sandbox);
    await clean.RemoveAsync(true);
    Assert(!Directory.Exists(clean.Manifest.WorktreePath) && File.Exists(clean.ManifestPath), "Safe cleanup failed.");
    var nested = Path.Combine(repo, "nested");
    Directory.CreateDirectory(nested);
    Assert((await ManagedDevelopmentWorkspace.CreateAsync(nested, sandbox)).Status == ManagedWorkspaceStatus.NeedsRepositoryAuthorization, "Subdirectory treated as root.");
    await Reject(() => ManagedDevelopmentWorkspace.CreateAsync(repo, Path.Combine(repo, "sandbox")));
});
await Check("restart load preserves candidate identity without direct publication", async () =>
{
    var (repo, sandbox) = await Fixture();
    var original = await Create(repo, sandbox, "restart/candidate");
    var ready = await ManagedDevelopmentWorkspace.LoadAsync(original.ManifestPath);
    Assert(ready.Manifest.Status == ManagedWorkspaceStatus.Ready, "Ready load failed.");
    await File.WriteAllTextAsync(Path.Combine(ready.Manifest.WorktreePath, "tracked.txt"), "restart candidate\n");
    var candidate = await ready.CaptureCandidateAsync();
    var loaded = await ManagedDevelopmentWorkspace.LoadAsync(ready.ManifestPath);
    Assert(loaded.Manifest.PatchHash == candidate.PatchHash, "Candidate identity changed.");
    await Reject(() => loaded.IntegrateAsync(false, candidate.PatchHash));
    await Reject(() => loaded.IntegrateAsync(true, candidate.PatchHash));
    Assert(await File.ReadAllTextAsync(Path.Combine(repo, "tracked.txt")) == "original\n", "Restart published candidate to user tree.");
    Assert(await File.ReadAllTextAsync(Path.Combine(loaded.Manifest.WorktreePath, "tracked.txt")) == "restart candidate\n", "Restart lost candidate content.");
    Assert((await ManagedDevelopmentWorkspace.LoadAsync(loaded.ManifestPath)).Manifest.Status == ManagedWorkspaceStatus.CandidateReady, "Legacy refusal changed candidate state.");
});
await Check("load rejects tampered paths, base, repository, metadata and patch without rewriting", async () =>
{
    var (repo, sandbox) = await Fixture();
    var other = await Fixture();
    var workspace = await Create(repo, sandbox);
    await File.WriteAllTextAsync(Path.Combine(workspace.Manifest.WorktreePath, "new.txt"), "candidate\n");
    var candidate = await workspace.CaptureCandidateAsync();
    var original = await File.ReadAllTextAsync(workspace.ManifestPath);
    async Task Tamper(ManagedWorkspaceManifest tampered)
    {
        var text = JsonSerializer.Serialize(tampered);
        await File.WriteAllTextAsync(workspace.ManifestPath, text);
        await Reject(() => ManagedDevelopmentWorkspace.LoadAsync(workspace.ManifestPath));
        Assert(await File.ReadAllTextAsync(workspace.ManifestPath) == text, "Load rewrote manifest.");
        Assert(Directory.Exists(candidate.WorktreePath), "Load removed worktree.");
        await File.WriteAllTextAsync(workspace.ManifestPath, original);
    }
    await Tamper(candidate with { RepositoryPath = other.Repo });
    await Tamper(candidate with { WorktreePath = other.Repo });
    await Tamper(candidate with { PatchPath = Path.Combine(root, "outside.patch") });
    await Tamper(candidate with { BaseHead = new string('0', 40) });
    await Tamper(candidate with { ChangedFiles = ["../escape"] });
    await Tamper(candidate with { PatchSize = candidate.PatchSize + 1 });
    await Tamper(candidate with { Branch = "different" });
    await Tamper(candidate with { Status = ManagedWorkspaceStatus.Removed });
    await File.AppendAllTextAsync(candidate.PatchPath, "tamper");
    await Reject(() => ManagedDevelopmentWorkspace.LoadAsync(workspace.ManifestPath));
    Assert(File.Exists(candidate.PatchPath), "Tampered artifact deleted.");
});
Console.WriteLine($"Passed {passed} managed workspace scenarios. Fixtures retained: {root}");
