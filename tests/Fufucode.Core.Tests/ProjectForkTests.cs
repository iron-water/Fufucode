using System.Diagnostics;
using System.Text.Json;
using Fufucode.Core;
using Fufucode.Pi;

internal static class ProjectForkTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    public static async Task RunAsync(string root, string node, string cli)
    {
        var scratch = Path.Combine(root, "tmp", "project-fork", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(scratch, "source repo");
        Directory.CreateDirectory(source);
        await RunAsync("git", source, "init", "--initial-branch=main");
        await File.WriteAllTextAsync(Path.Combine(source, "tracked.txt"), "committed seed");
        await RunAsync("git", source, "add", "tracked.txt");
        await RunAsync("git", source, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "commit", "-m", "fixture seed");
        await File.WriteAllTextAsync(Path.Combine(source, "tracked.txt"), "uncommitted user content");
        var destination = Path.Combine(scratch, "分支 worktree");
        var worktree = await GitWorktreeService.CreateAsync(source, destination);
        Check(await File.ReadAllTextAsync(Path.Combine(destination, "tracked.txt")) == "committed seed" &&
            await File.ReadAllTextAsync(Path.Combine(source, "tracked.txt")) == "uncommitted user content",
            "worktree fork uses HEAD without changing or copying uncommitted source content");
        Check((await RunAsync("git", destination, "branch", "--show-current")).Trim() == worktree.Branch &&
            worktree.Branch.StartsWith("codex/chat-", StringComparison.Ordinal), "worktree has its own codex branch");
        try { await GitWorktreeService.CreateAsync(source, destination); throw new Exception("existing target accepted"); }
        catch (InvalidOperationException error) when (error.Message.Contains("already exists"))
        { Check(true, "existing worktree destination is never overwritten"); }

        var manager = new PersistentSessionManager(node, cli, new AppDataLayout(Path.Combine(scratch, "data")));
        var project = manager.Metadata.GetOrCreateProject(source);
        var parent = manager.Metadata.CreateSession(project, "Original chat");
        var seedText = await RunAsync(node, root, Path.Combine(root, "tests", "fixtures", "project-fork-seed.mjs"),
            source, manager.Layout.ProjectSessionsDirectory(project.Id), parent.Id);
        using var seed = JsonDocument.Parse(seedText);
        var file = seed.RootElement.GetProperty("file").GetString()!;
        manager.Metadata.UpdateSessionFile(parent.Id, file);
        manager.SetProjectTrustDecision(destination, ProjectTrustDecision.DoNotTrust);
        string childId;
        await using (var originalSession = await manager.ResumeAsync(parent.Id))
        {
            var original = await File.ReadAllBytesAsync(file);
            var fork = await manager.ForkIntoProjectAsync(originalSession, destination);
            await using var child = fork.Session;
            childId = child.Session.Id;
            Check(childId != parent.Id && child.Project.Path == destination && child.Session.ParentSessionId == parent.Id &&
                child.History.CurrentBranchMessages.Count == 2,
                "real Pi project fork retains conversation and records separate identity and lineage");
            using var header = JsonDocument.Parse(File.ReadLines(child.Session.FilePath!).First());
            Check(AppDataLayout.NormalizeProjectPath(header.RootElement.GetProperty("cwd").GetString()!).Equals(
                AppDataLayout.NormalizeProjectPath(destination), StringComparison.OrdinalIgnoreCase),
                "real Pi child session header has the new worktree working directory");
            var retained = await File.ReadAllBytesAsync(file);
            Check((await originalSession.Coordinator.QueryStateAsync()).Data!.Value.GetProperty("sessionId").GetString() == parent.Id &&
                original.SequenceEqual(retained),
                "original Pi process remains usable and source history is byte-for-byte unchanged");
            try { await manager.ResumeAsync(childId); throw new Exception("second child writer accepted"); }
            catch (InvalidOperationException error) when (error.Message.Contains("already owned"))
            { Check(true, "worktree child retains exclusive session ownership"); }
        }
        await using (var reopened = await manager.ResumeAsync(childId))
            Check(reopened.Project.Path == destination && reopened.History.CurrentBranchMessages.Count == 2 &&
                reopened.Coordinator.ApprovalAvailability == ApprovalAvailability.Verified,
                "real worktree child reopens independently with verified approval bridge");
        // This fixture owns the entire throwaway repository and the exact worktree just created.
        await RunAsync("git", source, "worktree", "remove", destination);
        Check(!Directory.Exists(destination) && Directory.Exists(source), "fixture cleanup uses git worktree remove only");
        await File.WriteAllTextAsync(Path.Combine(scratch, "acceptance.json"), JsonSerializer.Serialize(new
        { project = source, dataRoot = manager.Layout.Root, sessionId = parent.Id, forkSessionId = childId }));
    }

    private static async Task<string> RunAsync(string executable, string cwd, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = cwd, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var text = await output;
        var detail = await error;
        if (process.ExitCode != 0) throw new Exception(detail);
        return text;
    }
}
