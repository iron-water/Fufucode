using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Fufucode.Core;
using Fufucode.Pi;

/// <summary>
/// A13 contract package: thinking-level availability, extension UI audit trail, read-only session
/// project metadata, read-only diff hunks, and fork lineage with lock handoff.
/// </summary>
internal static class A13ContractTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    /// <summary>The log writer shares read only, so a reader must tolerate concurrent writes.</summary>
    private static string ReadLog(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 20) { Thread.Sleep(50); }
        }
    }

    private static string Scratch(string root, string tag) =>
        Path.Combine(root, "tmp", "a13-" + tag + "-" + Guid.NewGuid().ToString("N"));

    private static async Task WaitFor(Func<bool> condition, string description, int seconds = 20)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException(description);
            await Task.Delay(20);
        }
    }

    public static async Task RunAsync(string root, string fake, string dotnet)
    {
        await ThinkingLevelsAsync(root, fake, dotnet);
        await UiAuditTrailAsync(root, fake, dotnet);
        SessionProjectMetadata(root);
        await DiffAsync(root);
        await ForkAsync(root, fake, dotnet);
    }

    // ---------------------------------------------------------------- ① thinking levels

    private static async Task ThinkingLevelsAsync(string root, string fake, string dotnet)
    {
        PiRpcOptions Options() => new(dotnet, [fake], root);
        await using (var session = PiSessions.Coordinator(Options()))
        {
            await session.StartAsync();
            var availability = await session.QueryThinkingLevelsAsync();
            Check(availability.Levels.SequenceEqual(["off", "low", "medium", "high"]) &&
                availability.Supports("high") && !availability.Supports("xhigh"),
                "A13 thinking levels: Pi reports the level list and membership is explicit");
            await session.SetThinkingLevelAsync("medium");
            var state = (await session.QueryStateAsync()).Data!.Value;
            Check(ThinkingLevelAvailability.ReadCurrentLevel(state) == "medium",
                "A13 thinking levels: a supported level is applied as requested");
        }
        // A model without reasoning silently clamps; the fixture clamps to off like Pi does.
        await using (var clamping = PiSessions.Coordinator(Options()))
        {
            await clamping.StartAsync();
            await clamping.SetThinkingLevelAsync("xhigh");
            var state = (await clamping.QueryStateAsync()).Data!.Value;
            Check(ThinkingLevelAvailability.ReadCurrentLevel(state) == "off" &&
                !(await clamping.QueryThinkingLevelsAsync()).Supports("xhigh"),
                "A13 thinking levels: an unsupported level is clamped, not reported as applied");
        }
        var layout = new AppDataLayout(Scratch(root, "thinking"));
        var project = Path.Combine(layout.Root, "project");
        Directory.CreateDirectory(project);
        var manager = new PersistentSessionManager(dotnet, fake, layout, requireApprovalBridge: false);
        manager.Settings.Update(s => s with { SelectedModel = "fixture/fixture-model", ThinkingLevel = "xhigh" });
        try
        {
            await using var refused = await manager.CreateAsync(project, backend: BackendId.Pi);
            throw new Exception("A13 a clamped thinking level was accepted as applied");
        }
        catch (RpcException error)
        {
            Check(error.Message.Contains("does not offer", StringComparison.Ordinal) &&
                error.Message.Contains("xhigh", StringComparison.Ordinal) &&
                error.Message.Contains("off, low, medium, high", StringComparison.Ordinal),
                "A13 thinking levels: session creation names the unsupported level and what Pi offers");
        }
        manager.Settings.Update(s => s with { ThinkingLevel = "medium" });
        await using (var accepted = await manager.CreateAsync(project, backend: BackendId.Pi))
        {
            Check(accepted.Session.Id.Length > 0, "A13 thinking levels: a supported level still opens a session");
        }
        // Parse contract: a malformed capability response is refused instead of silently empty.
        try
        {
            ThinkingLevelAvailability.Parse(new RpcResponse("1", "get_available_thinking_levels", true,
                JsonSerializer.SerializeToElement(new { levels = "off" }), null));
            throw new Exception("A13 a malformed level list was accepted");
        }
        catch (RpcException) { Console.WriteLine("PASS A13 thinking levels: a malformed level list is refused"); }
        try
        {
            ThinkingLevelAvailability.Parse(new RpcResponse("1", "get_available_thinking_levels", false, null, "no"));
            throw new Exception("A13 a failed capability query was accepted");
        }
        catch (RpcException) { Console.WriteLine("PASS A13 thinking levels: a failed capability query is refused"); }
    }

    // ------------------------------------------------------- ② extension UI audit trail

    private static async Task UiAuditTrailAsync(string root, string fake, string dotnet)
    {
        var layout = new AppDataLayout(Scratch(root, "uitrail"));
        string path;
        await using (var log = new DiagnosticLog(layout))
        {
            path = log.FilePath;
            await using var session = PiSessions.Coordinator(new PiRpcOptions(dotnet, [fake], root, DiagnosticSink: log));
            await session.StartAsync();
            await session.SendPromptAsync("approval");
            await WaitFor(() => session.PendingExtensionUiRequests.Count == 1, "A13 no approval dialog arrived");
            var request = session.PendingExtensionUiRequests.Single();
            await session.RespondToExtensionUiAsync(request, PiExtensionUiResponse.Confirm(true));
            await Task.Delay(150);
        }
        var text = ReadLog(path);
        Check(text.Contains("\"eventType\":\"extension_ui_request\"", StringComparison.Ordinal) &&
            text.Contains("\"eventType\":\"extension_ui_response\"", StringComparison.Ordinal),
            "A13 UI trail: a dialog leaves both a request and a response record");
        Check(text.Contains("\"command\":\"confirm\"", StringComparison.Ordinal) &&
            text.Contains("\"requestId\":\"approval-1\"", StringComparison.Ordinal),
            "A13 UI trail: the record carries the dialog method and id");
        Check(text.Contains("\"outcome\":\"confirmed\"", StringComparison.Ordinal),
            "A13 UI trail: an accepted confirmation is recorded as confirmed");
        Check(text.Contains("\"outcome\":\"notified\"", StringComparison.Ordinal) &&
            text.Contains("\"command\":\"notify\"", StringComparison.Ordinal),
            "A13 UI trail: notifications are recorded without pretending they were answered");
        Check(!text.Contains("Exact action", StringComparison.Ordinal) &&
            !text.Contains("Approval pending", StringComparison.Ordinal),
            "A13 UI trail: dialog payload text never reaches the log");

        // Timeouts, close and unknown methods are distinct outcomes for the same audit.
        var timeoutLayout = new AppDataLayout(Scratch(root, "uitimeout"));
        string timeoutPath;
        await using (var log = new DiagnosticLog(timeoutLayout))
        {
            timeoutPath = log.FilePath;
            await using var session = PiSessions.Coordinator(new PiRpcOptions(dotnet, [fake], root,
                DiagnosticSink: log));
            await session.StartAsync();
            await session.SendPromptAsync("approval");
            await WaitFor(() => session.PendingExtensionUiRequests.Count == 1, "A13 no approval dialog arrived");
            // The fixture dialog expires after 350 ms; nothing answers it here.
            await Task.Delay(1500);
        }
        Check(ReadLog(timeoutPath).Contains("\"outcome\":\"expired\"", StringComparison.Ordinal) &&
            ReadLog(timeoutPath).Contains("\"requestId\":\"approval-1\"", StringComparison.Ordinal),
            "A13 UI trail: an unanswered dialog expires with its own outcome");

        var closedLayout = new AppDataLayout(Scratch(root, "uiclosed"));
        string closedPath;
        await using (var log = new DiagnosticLog(closedLayout))
        {
            closedPath = log.FilePath;
            await using var session = PiSessions.Coordinator(new PiRpcOptions(dotnet, [fake], root,
                DiagnosticSink: log));
            await session.StartAsync();
            await SendUnknownMethodAsync(session);
            await session.SendPromptAsync("approval");
            await WaitFor(() => session.PendingExtensionUiRequests.Count == 1, "A13 no approval dialog arrived");
            await session.CloseAsync();
            await Task.Delay(150);
        }
        var closed = ReadLog(closedPath);
        Check(closed.Contains("\"outcome\":\"closed\"", StringComparison.Ordinal),
            "A13 UI trail: closing the session records that pending dialogs were abandoned");
        Check(closed.Contains("\"outcome\":\"ignored\"", StringComparison.Ordinal),
            "A13 UI trail: an unknown extension UI method is recorded as ignored");

        // The export path keeps the new field and stays sanitized.
        await using var exported = new MemoryStream();
        var count = await DiagnosticExport.WriteJsonLinesAsync(closedLayout, exported);
        var exportText = Encoding.UTF8.GetString(exported.ToArray());
        Check(count > 0 && exportText.Contains("\"outcome\"", StringComparison.Ordinal) &&
            !exportText.Contains("Approval pending", StringComparison.Ordinal),
            "A13 UI trail: diagnostic export carries outcomes and no payload text");
    }

    private static async Task SendUnknownMethodAsync(ChatSessionCoordinator session)
    {
        try { await session.SendPromptAsync("ui_unknown"); }
        catch (RpcException) { }
        await Task.Delay(150);
    }

    // -------------------------------------------------- ③ sessions project metadata

    private static void SessionProjectMetadata(string root)
    {
        var layout = new AppDataLayout(Scratch(root, "project"));
        Directory.CreateDirectory(layout.Root);
        var project = Path.Combine(layout.Root, "work");
        Directory.CreateDirectory(project);
        var metadata = new DesktopMetadataStore(layout);
        Check(metadata.TryGetProject(project) is null,
            "A13 sessions project: listing a project does not register it as a side effect");
        var created = metadata.GetOrCreateProject(project);
        Check(metadata.TryGetProject(project)?.Id == created.Id,
            "A13 sessions project: a registered project is found read-only afterwards");
        var canonical = AppDataLayout.NormalizeProjectPath(project + Path.DirectorySeparatorChar);
        Check(canonical == created.Path, "A13 sessions project: the stored path is the normalized canonical form");
        var session = metadata.CreateSession(created, "named");
        Check(metadata.ListSessions(created.Id).Single().Id == session.Id,
            "A13 sessions project: sessions are listed per project id");
    }

    // ------------------------------------------------------------- ④ read-only diff

    private static async Task DiffAsync(string root)
    {
        var work = Scratch(root, "diff");
        Directory.CreateDirectory(work);
        if (await RunGit(work, ["init", "-q"]) != 0) { Console.WriteLine("SKIP A13 diff: git unavailable"); return; }
        await RunGit(work, ["config", "user.email", "fixture@example.test"]);
        await RunGit(work, ["config", "user.name", "fixture"]);
        var file = Path.Combine(work, "a.txt");
        await File.WriteAllTextAsync(file, "one\ntwo\nthree\nfour\nfive\n");
        await RunGit(work, ["add", "a.txt"]);
        await RunGit(work, ["commit", "-q", "-m", "base"]);
        await File.WriteAllTextAsync(file, "one\nTWO\nthree\nfour\nfive\nsix\n");

        var diff = await DiffQuery.FileAsync(work, "a.txt");
        Check(diff.Error is null && diff.Hunks.Count == 1 && diff.IsBinary == false,
            "A13 diff: a modified file yields one parsed hunk");
        var hunk = diff.Hunks[0];
        Check(hunk.OldStart == 1 && hunk.NewStart == 1 && hunk.Lines.Any(l => l.Kind == '+' && l.Text == "TWO") &&
            hunk.Lines.Any(l => l.Kind == '-' && l.Text == "two") &&
            hunk.Lines.Any(l => l.Kind == ' ' && l.Text == "one"),
            "A13 diff: hunk header and added/removed/context lines are classified");
        var narrow = await DiffQuery.FileAsync(work, "a.txt", contextLines: 0);
        Check(narrow.Hunks.Count == 2 && narrow.Hunks.Sum(h => h.Lines.Count) < hunk.Lines.Count,
            "A13 diff: a smaller context produces fewer context lines, never different content");
        var bounded = await DiffQuery.FileAsync(work, "a.txt", maximumLines: 2);
        Check(bounded.Truncated && bounded.Hunks.Sum(h => h.Lines.Count) == 2,
            "A13 diff: the line budget truncates and says so");
        var escaped = await DiffQuery.FileAsync(work, ".." + Path.DirectorySeparatorChar + "outside.txt");
        Check(escaped.Error == "path-escapes-project" && escaped.Hunks.Count == 0,
            "A13 diff: a path outside the project is refused");
        try { await DiffQuery.FileAsync(work, "a.txt", contextLines: 500); throw new Exception("A13 huge context accepted"); }
        catch (ArgumentOutOfRangeException) { Console.WriteLine("PASS A13 diff: an unbounded context request is refused"); }

        var report = await ChangeRangeQuery.WorkspaceAsync(work);
        var changed = report.Files.Single(f => f.Path == "a.txt");
        Check(report.Available && changed.AddedLines == 2 && changed.RemovedLines == 1,
            "A13 diff: per-file added and removed line counts come from git numstat");

        var plain = Path.Combine(Path.GetTempPath(), "fufucode-a13-nogit-" + Guid.NewGuid().ToString("N"));
        await RunGit(work, ["add", "a.txt"]);
        await File.WriteAllTextAsync(file, "final\n");
        var staged = await DiffQuery.FileAsync(work, "a.txt");
        Check(staged.Hunks.SelectMany(h => h.Lines).Any(l => l.Kind == '-' && l.Text == "one") &&
              staged.Hunks.SelectMany(h => h.Lines).Any(l => l.Kind == '+' && l.Text == "final"),
            "A13 diff: staged and unstaged edits compare HEAD to the actual current file");
        await File.WriteAllTextAsync(Path.Combine(work, "untracked.txt"), "new content\n");
        var untracked = await DiffQuery.FileAsync(work, "untracked.txt");
        Check(untracked.Error is null && untracked.Hunks.SelectMany(h => h.Lines).Any(l => l.Kind == '+' && l.Text == "new content"),
            "A13 diff: an untracked file exposes real additions");
        await File.WriteAllBytesAsync(Path.Combine(work, "untracked.bin"), [0, 1, 2, 3]);
        var binary = await DiffQuery.FileAsync(work, "untracked.bin");
        Check(binary.Error is null && binary.IsBinary == true && binary.Hunks.Count == 0,
            "A13 diff: an untracked binary is not mislabeled as an empty text diff");
        Directory.CreateDirectory(plain);
        var none = await DiffQuery.FileAsync(plain, "a.txt");
        Check(none.Error == "not-a-git-worktree", "A13 diff: a non-git directory reports why it has no diff");
    }

    private static async Task<int> RunGit(string directory, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start git.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output; await error;
        return process.ExitCode;
    }

    // -------------------------------------------------- ⑤ fork metadata and lock handoff

    private static async Task ForkAsync(string root, string fake, string dotnet)
    {
        var layout = new AppDataLayout(Scratch(root, "fork"));
        var project = Path.Combine(layout.Root, "work");
        Directory.CreateDirectory(project);
        var manager = new PersistentSessionManager(dotnet, fake, layout, requireApprovalBridge: false);
        var parent = await manager.CreateAsync(project, backend: BackendId.Pi);
        var parentId = parent.Session.Id;
        var result = await manager.ForkAsync(parent, "entry-1");
        var child = result.Session;
        var forkState = (await child.Coordinator.QueryStateAsync()).Data!.Value;
        Check(forkState.GetProperty("sessionId").GetString() == child.Session.Id,
            "A13 fork: desktop identity equals the actual Pi child identity");
        Check(child.Session.Id != parentId && child.Session.ParentSessionId == parentId &&
            child.Session.ForkEntryId == "entry-1" && child.Session.IsFork,
            "A13 fork: the child row records its parent and the entry it branched from");
        Check(result.ParentSessionId == parentId && result.ForkEntryId == "entry-1",
            "A13 fork: the result reports the lineage it created");
        Check(manager.Metadata.GetSession(parentId) is { } kept && kept.Id == parentId,
            "A13 fork: the parent session row is kept so both branches stay reachable");
        Check(manager.Metadata.ListSessions(child.Project.Id).Count == 2,
            "A13 fork: both sessions belong to the same project");
        Check(child.Session.FilePath is { Length: > 0 } && File.Exists(child.Session.FilePath) &&
            child.Coordinator.Snapshot.Connection == ConnectionState.Connected,
            "A13 fork: the child inherits the running Pi connection and registers its branch file");
        // The lock moved: the parent can no longer hold it, and the child directory claims it.
        var directory = layout.ProjectSessionsDirectory(child.Project.Id);
        try
        {
            using var claimed = SessionLease.Acquire(directory, child.Session.Id);
            throw new Exception("A13 fork: the child session lock was not held after the fork");
        }
        catch (InvalidOperationException) { Console.WriteLine("PASS A13 fork: the child session lock is held after handoff"); }
        using (var parentLock = SessionLease.Acquire(directory, parentId))
        {
            Check(parentLock is not null, "A13 fork: the parent lock is released and can be taken again");
        }
        await parent.DisposeAsync();
        Check(child.Coordinator.Snapshot.Connection == ConnectionState.Connected,
            "A13 fork: disposing the parent session does not kill the shared Pi process");
        var forked = manager.Metadata.GetSession(child.Session.Id);
        Check(forked.ParentSessionId == parentId,
            "A13 fork: lineage survives a re-read of the metadata store");
        try { await manager.ForkAsync(child, "bad-entry"); throw new Exception("A13 an invalid fork entry was accepted"); }
        catch (RpcException error)
        {
            Check(error.Message.Contains("fork failed", StringComparison.Ordinal),
                "A13 fork: Pi's refusal of an unknown entry surfaces as a fork failure");
        }
        await child.DisposeAsync();
        await using var reopened = await manager.ResumeAsync(child.Session.Id);
        Check(reopened.Session.Id == child.Session.Id && reopened.Session.ParentSessionId == parentId,
            "A13 fork: the same child can be reopened with its lineage intact");
    }
}
