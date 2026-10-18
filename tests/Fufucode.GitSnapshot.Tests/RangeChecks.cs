using System.Diagnostics;
using System.Text;
using Fufucode.Core;

internal static class RangeChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-range-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        async Task<string> Git(params string[] args)
        {
            var info = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var p = Process.Start(info)!; var read = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync(); if (p.ExitCode != 0) throw new Exception(await error); return await read;
        }
        await Git("init", "-q"); await Git("config", "user.name", "Fixture"); await Git("config", "user.email", "fixture@example.invalid");
        await Git("config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(root, "a.txt"), "base\n");
        File.WriteAllText(Path.Combine(root, "special.txt"), "-- old\n");
        File.WriteAllText(Path.Combine(root, "deleted.txt"), "to remove\n");
        await Git("add", "-A"); await Git("commit", "-q", "-m", "base");
        var basis = (await Git("rev-parse", "HEAD")).Trim();
        var wrongHeadWorktree = await GitDiffSnapshot.CaptureRangeAsync(root, ["a.txt"], GitDiffSnapshotSource.Worktree,
            expectedHeadCommit: new string('0', 40));
        check(wrongHeadWorktree.Status != GitDiffSnapshotStatus.Captured, "range: worktree entry also honors the expected HEAD");
        File.WriteAllText(Path.Combine(root, "a.txt"), "staged\n");
        File.WriteAllText(Path.Combine(root, "special.txt"), "++ new\n");
        File.WriteAllText(Path.Combine(root, "中文 [1].txt"), "索引文字\n");
        File.Delete(Path.Combine(root, "deleted.txt"));
        File.WriteAllBytes(Path.Combine(root, "binary.bin"), [0, 1, 255, 10]);
        await Git("add", "-A");
        File.WriteAllText(Path.Combine(root, "a.txt"), "unstaged after index\n");
        var beforeStatus = await Git("status", "--porcelain");
        var staged = await GitDiffSnapshot.CaptureRangeAsync(root, ["a.txt", "中文 [1].txt", "deleted.txt", "binary.bin", "special.txt"],
            GitDiffSnapshotSource.StagedIndex, expectedHeadCommit: basis);
        check(staged.Status == GitDiffSnapshotStatus.Captured && staged.Entries.All(e => e.Error is null && !e.Stale), "range: staged snapshot captures actual index entries");
        var entry = staged.Entries.Single(e => e.RelativePath == "a.txt");
        check(Encoding.UTF8.GetString(entry.Bytes!) == "staged\n", "range: staged bytes never use later worktree edits");
        check(entry.Diff.Hunks.SelectMany(h => h.Lines).Any(l => l.Kind == '+' && l.Text == "staged") &&
            !entry.Diff.Hunks.SelectMany(h => h.Lines).Any(l => l.Text.Contains("unstaged")), "range: staged patch matches exactly the frozen index bytes");
        check(staged.Entries.Single(e => e.RelativePath == "special.txt").Diff.Hunks.SelectMany(h => h.Lines)
            .Any(l => l.Kind == '-' && l.Text == "-- old") && staged.Entries.Single(e => e.RelativePath == "special.txt")
            .Diff.Hunks.SelectMany(h => h.Lines).Any(l => l.Kind == '+' && l.Text == "++ new"),
            "range: hunk text resembling file headers is preserved");
        check(Encoding.UTF8.GetString(staged.Entries.Single(e => e.RelativePath == "中文 [1].txt").Bytes!) == "索引文字\n", "range: Unicode and literal pathspec punctuation are preserved");
        check(staged.Entries.Single(e => e.RelativePath == "binary.bin").Bytes!.SequenceEqual(new byte[] { 0, 1, 255, 10 }) &&
            staged.Entries.Single(e => e.RelativePath == "binary.bin").Diff.IsBinary == true, "range: binary index bytes are copied without text decoding");
        check(!staged.Entries.Single(e => e.RelativePath == "deleted.txt").ExistedAtCapture &&
            staged.Entries.Single(e => e.RelativePath == "deleted.txt").Diff.Hunks.Count > 0, "range: staged deletion keeps its actual removal patch");
        check(await staged.IsFreshAsync() && !await staged.MatchesWorkingTreeAsync(), "range: source freshness and current-worktree line location are distinct");
        check(await Git("status", "--porcelain") == beforeStatus && (await Git("rev-parse", "HEAD")).Trim() == basis, "range: snapshot does not stage, write or move the source HEAD");
        var sourceJson = System.Text.Json.JsonSerializer.Serialize(staged);
        var restored = System.Text.Json.JsonSerializer.Deserialize<FileDiffSnapshot>(sourceJson)!;
        check(restored.Source == GitDiffSnapshotSource.StagedIndex && await restored.IsFreshAsync(), "range: persisted source identity remains verifiable after reopening");
        await Git("update-index", "--chmod=+x", "a.txt");
        var mode = await GitDiffSnapshot.CaptureRangeAsync(root, ["a.txt"], GitDiffSnapshotSource.StagedIndex);
        check(mode.Entries.Single().Diff.OldMode == "100644" && mode.Entries.Single().Diff.NewMode == "100755",
            "range: index executable mode changes remain in the snapshot metadata");
        check(!await staged.IsFreshAsync(), "range: index mode change invalidates the source fingerprint");
        await Git("add", "a.txt");
        check(!await staged.IsFreshAsync(), "range: restaging invalidates the captured source fingerprint");
        await Git("commit", "-q", "-m", "target"); var target = (await Git("rev-parse", "HEAD")).Trim();
        File.WriteAllText(Path.Combine(root, "a.txt"), "changed worktree after target\n");
        var historical = await GitDiffSnapshot.CaptureRangeAsync(root, ["a.txt", "中文 [1].txt", "deleted.txt"],
            GitDiffSnapshotSource.TargetTree, basis, target, target);
        check(Encoding.UTF8.GetString(historical.Entries.Single(e => e.RelativePath == "a.txt").Bytes!) == "unstaged after index\n",
            "range: explicit target bytes come from the committed tree");
        check(historical.Entries.Single(e => e.RelativePath == "a.txt").Diff.Hunks.SelectMany(h => h.Lines).Any(l => l.Kind == '-' && l.Text == "base"),
            "range: target patch uses the specified base instead of current HEAD");
        check(await historical.IsFreshAsync() && !await historical.MatchesWorkingTreeAsync(), "range: later uncommitted edits do not replace immutable historical content");
        var rootCommit = await GitDiffSnapshot.CaptureRangeAsync(root, ["a.txt"], GitDiffSnapshotSource.TargetTree,
            targetCommit: basis, expectedHeadCommit: target);
        check(rootCommit.Entries.Single().Diff.Hunks.SelectMany(h => h.Lines).Any(l => l.Kind == '+' && l.Text == "base"), "range: a root commit diffs against empty content without writing an empty tree");
        var refused = await GitDiffSnapshot.CaptureRangeAsync(root, ["a.txt"], GitDiffSnapshotSource.TargetTree, basis, target, basis);
        check(refused.Status == GitDiffSnapshotStatus.NoPathsCaptured && refused.Error == "head-changed-since-scope-resolution", "range: changed HEAD between scope resolution and capture is refused");
        var invalidPath = await GitDiffSnapshot.CaptureRangeAsync(root, ["../outside.txt"], GitDiffSnapshotSource.TargetTree, basis, target);
        check(invalidPath.Entries.Single().Error is not null, "range: historical paths cannot escape the project");
        var blob = (await Git("rev-parse", target + ":a.txt")).Trim();
        var nonCommit = await GitDiffSnapshot.CaptureRangeAsync(root, ["a.txt"], GitDiffSnapshotSource.TargetTree, basis, blob);
        check(nonCommit.Error == "invalid-snapshot-commit", "range: a blob ID cannot masquerade as a target commit");
        File.WriteAllBytes(Path.Combine(root, "large.bin"), new byte[GitDiffSnapshot.MaximumFileBytes + 1]); await Git("add", "large.bin");
        var large = await GitDiffSnapshot.CaptureRangeAsync(root, ["large.bin"], GitDiffSnapshotSource.StagedIndex);
        check(large.Entries.Single().Bytes is null && large.Entries.Single().Error == "file-too-large", "range: oversized Git blobs are refused before loading content");
        File.WriteAllText(Path.Combine(root, "fresh.txt"), "fresh\n"); await Git("add", "fresh.txt"); await Git("commit", "-q", "-m", "move head");
        check(!await historical.IsFreshAsync(), "range: observed HEAD change marks historical location context stale");
        var small = await GitDiffSnapshot.CaptureAsync(root, ["a.txt"]);
        File.WriteAllBytes(Path.Combine(root, "a.txt"), new byte[GitDiffSnapshot.MaximumFileBytes + 1]);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var grownFresh = await small.IsFreshAsync();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        check(!grownFresh && !await small.MatchesWorkingTreeAsync(),
            "range: a captured file that grows beyond the byte cap is refused by freshness and location checks");
        check(allocated < GitDiffSnapshot.MaximumFileBytes,
            "range: freshness rejects the oversized file without allocating its contents");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = false;
        try { await GitDiffSnapshot.CaptureRangeAsync(root, ["a.txt"], GitDiffSnapshotSource.StagedIndex, cancellationToken: cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "range: cancellation propagates without a partial success");
        Console.WriteLine("Range evidence: " + root);
    }
}
