using System.Diagnostics;
using System.Text;
using Fufucode.Core;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}

var temp = Path.Combine(Path.GetTempPath(), "fufu-gitsnapshot-" + Guid.NewGuid().ToString("N"));
await RangeChecks.Run(Check);
Directory.CreateDirectory(temp);
try
{
    await Git(temp, "init");
    await Git(temp, "config", "user.email", "test@example.invalid");
    await Git(temp, "config", "user.name", "snapshot-test");
    await Git(temp, "config", "core.autocrlf", "false");

    await File.WriteAllTextAsync(Path.Combine(temp, "a.txt"), "one\ntwo\nthree\n");
    var chinesePath = Path.Combine(temp, "中文 目录", "带 空格.txt");
    Directory.CreateDirectory(Path.GetDirectoryName(chinesePath)!);
    await File.WriteAllTextAsync(chinesePath, "旧内容\n保留行\n");
    await Git(temp, "add", "-A");
    await Git(temp, "commit", "-m", "init");

    var statusBefore = (await Git(temp, "status", "--porcelain")).Trim();

    // Modify tracked file and add an untracked file before capture.
    await File.WriteAllTextAsync(Path.Combine(temp, "a.txt"), "one\nTWO\nthree\n");
    await File.WriteAllTextAsync(Path.Combine(temp, "untracked.txt"), "brand new\n");
    await File.WriteAllTextAsync(chinesePath, "旧内容\n新改动\n");

    var snapshot = await GitDiffSnapshot.CaptureAsync(temp,
        ["a.txt", "untracked.txt", "中文 目录/带 空格.txt", "..\\outside.txt"]);

    Check(snapshot.Status == GitDiffSnapshotStatus.Captured, "status captured");
    var head = (await Git(temp, "rev-parse", "--verify", "HEAD")).Trim();
    Check(snapshot.HeadCommit == head, "head matches real rev-parse");
    var root = Path.GetFullPath((await Git(temp, "rev-parse", "--show-toplevel")).Trim());
    Check(snapshot.RepositoryRoot == root, "repository root matches real toplevel");
    Check(snapshot.Entries.Count == 4, "all requested paths have entries");

    var a = snapshot.Entries.Single(e => e.RelativePath == "a.txt");
    Check(a.ExistedAtCapture && a.TrackedAtCapture && !a.Stale, "a.txt tracked and fresh at capture");
    Check(a.Diff.Error is null && a.Diff.Hunks.Count > 0, "a.txt diff captured");
    Check(Encoding.UTF8.GetString(a.Bytes!) == "one\nTWO\nthree\n", "a.txt bytes captured");

    var untracked = snapshot.Entries.Single(e => e.RelativePath == "untracked.txt");
    Check(untracked.ExistedAtCapture && !untracked.TrackedAtCapture, "untracked file captured");
    Check(untracked.Diff.Error is null, "untracked diff has no error");

    var chinese = snapshot.Entries.Single(e => e.RelativePath == "中文 目录/带 空格.txt");
    Check(chinese.Diff.Error is null && chinese.Bytes is not null, "chinese/space path captured");

    var escaped = snapshot.Entries.Single(e => e.RelativePath == "../outside.txt");
    Check(escaped.Error == "path-escapes-project" && !escaped.ExistedAtCapture,
        "out-of-project path refused");

    // The snapshot contains one error entry (path-escapes-project): freshness must be false.
    Check(!(await snapshot.IsFreshAsync()), "snapshot with an error entry is not fresh");
    var cleanSnap = await GitDiffSnapshot.CaptureAsync(temp,
        ["a.txt", "untracked.txt", "中文 目录/带 空格.txt"]);
    Check(await cleanSnap.IsFreshAsync(), "clean snapshot fresh immediately after capture");

    // Change the source after capture: old snapshot bytes must not change and freshness must fail.
    await File.WriteAllTextAsync(Path.Combine(temp, "a.txt"), "one\nCHANGED-AFTER\nthree\n");
    Check(Encoding.UTF8.GetString(a.Bytes!) == "one\nTWO\nthree\n", "captured bytes immutable after source change");
    Check(!(await snapshot.IsFreshAsync()), "snapshot reports stale after source change");

    // Read-only: index untouched (nothing staged), source file keeps the after-capture content,
    // and the post-change working state is exactly what we wrote (capture added nothing).
    Check((await Git(temp, "diff", "--cached", "--name-only")).Trim().Length == 0,
        "nothing staged by snapshot reads");
    Check(File.ReadAllText(Path.Combine(temp, "a.txt")) == "one\nCHANGED-AFTER\nthree\n",
        "source file content kept (read-only, no restore)");
    // Only the two tracked files we deliberately changed appear in `git diff HEAD`; the snapshot
    // reads must not add or hide entries.
    var raw = await Git(temp, "-c", "core.quotepath=false", "diff", "--name-only", "HEAD");
    var trackedNames = raw.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Check(trackedNames.Length == 2 && trackedNames.Contains("a.txt") &&
        trackedNames.Contains("中文 目录/带 空格.txt"),
        "git sees only the files we changed, quoting disabled");

    // Reparse point is refused rather than followed outside the repo.
    if (OperatingSystem.IsWindows())
    {
        var outside = Path.Combine(temp, "..", Path.GetFileName(temp) + "-outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "x\n");
        var link = Path.Combine(temp, "linkdir");
        var linkResult = await Run("cmd", temp, "/c", "mklink", "/J", link, outside);
        if (linkResult.Code == 0)
        {
            var snap2 = await GitDiffSnapshot.CaptureAsync(temp, ["linkdir/secret.txt"]);
            var entry = snap2.Entries.Single();
            Check(entry.Error == "reparse-point-refused" || entry.Error == "path-escapes-project",
                "reparse point refused");
        }
    }

    // Cancellation is honored, not swallowed into a fake success.
    var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    var threw = false;
    try { await GitDiffSnapshot.CaptureAsync(temp, ["a.txt"], cancelled.Token); }
    catch (OperationCanceledException) { threw = true; }
    Check(threw, "cancellation propagates");

    // Empty path list yields an explicit status instead of a fake capture.
    var empty = await GitDiffSnapshot.CaptureAsync(temp, []);
    Check(empty.Status == GitDiffSnapshotStatus.NoPathsCaptured, "empty request is explicit");

    // Regression 1: project is a subdirectory. Root and subdir both contain a.txt; only root's is
    // tracked. The snapshot must describe the file under projectPath, not the root same-named file.
    var sub = Path.Combine(temp, "sub");
    Directory.CreateDirectory(sub);
    await File.WriteAllTextAsync(Path.Combine(sub, "a.txt"), "subcontent\n");
    var subSnap = await GitDiffSnapshot.CaptureAsync(sub, ["a.txt"]);
    var subEntry = subSnap.Entries.Single();
    Check(subEntry.ExistedAtCapture && !subEntry.TrackedAtCapture,
        "subdir project resolves state against subdir file, not root same-name tracked file");
    Check(Encoding.UTF8.GetString(subEntry.Bytes!) == "subcontent\n", "subdir bytes are the subdir file");

    // Regression 2: a tracked file deleted after capture is still tracked (index identity is
    // independent of existence).
    await File.WriteAllTextAsync(Path.Combine(temp, "deleted.txt"), "to be deleted\n");
    await Git(temp, "add", "deleted.txt");
    await Git(temp, "commit", "-m", "add deleted");
    var delSnap = await GitDiffSnapshot.CaptureAsync(temp, ["deleted.txt"]);
    Check(delSnap.Entries.Single().TrackedAtCapture, "tracked before delete");
    File.Delete(Path.Combine(temp, "deleted.txt"));
    var delSnap2 = await GitDiffSnapshot.CaptureAsync(temp, ["deleted.txt"]);
    var delEntry = delSnap2.Entries.Single();
    Check(!delEntry.ExistedAtCapture && delEntry.TrackedAtCapture,
        "deleted tracked file keeps TrackedAtCapture=true");
    Check(!(await delSnap.IsFreshAsync()), "snapshot taken before delete reports not fresh after delete");
    Check(await delSnap2.IsFreshAsync(),
        "deleted-file snapshot stays fresh while file remains deleted");

    // Regression 3: entries with errors (path-escapes-project, stale) must not report fresh.
    var errSnap = await GitDiffSnapshot.CaptureAsync(temp, ["../outside.txt"]);
    Check(errSnap.Entries.Single().Error == "path-escapes-project", "escape error recorded");
    Check(!(await errSnap.IsFreshAsync()), "error entry is never fresh");
    Check(!(await snapshot.IsFreshAsync()), "snapshot with post-capture change stays not fresh");

    // Non-git directory is reported, not faked.
    var plain = Path.Combine(temp, "..", Path.GetFileName(temp) + "-plain");
    Directory.CreateDirectory(plain);
    var nogit = await GitDiffSnapshot.CaptureAsync(plain, ["a.txt"]);
    Check(nogit.Status == GitDiffSnapshotStatus.NotGitWorktree, "non-git directory refused");

    Console.WriteLine($"ALL {passed} CHECKS PASSED");
}
finally
{
    try { Directory.Delete(temp, recursive: true); } catch { }
    var outside = temp + "-outside";
    var plain = temp + "-plain";
    try { if (Directory.Exists(outside)) Directory.Delete(outside, true); } catch { }
    try { if (Directory.Exists(plain)) Directory.Delete(plain, true); } catch { }
}

static async Task<string> Git(string directory, params string[] args)
{
    var result = await Run("git", directory, args);
    if (result.Code != 0) throw new Exception($"git {string.Join(' ', args)} failed: {result.Error}");
    return result.Output;
}

static async Task<(int Code, string Output, string Error)> Run(string program, string directory, params string[] args)
{
    var start = new ProcessStartInfo(program)
    {
        WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
    };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    using var process = Process.Start(start) ?? throw new Exception("start failed");
    var output = await process.StandardOutput.ReadToEndAsync();
    var error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (process.ExitCode, output, error);
}
