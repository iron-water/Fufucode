using System.Diagnostics;
using System.Text.Json;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;

/// <summary>
/// CONV-3 end-to-end checks for the idle auto-advancement loop. Every case uses a real git repository,
/// a real BackgroundHost with a real child writer process, and the real receiving/decision gates. The
/// only fixture is the child writer itself; the engine is a deterministic state machine, so this
/// proves the loop rather than a model's behaviour.
/// </summary>
internal static class AutoAdvancementChecks
{
    /// <summary>The fixture worker: commit a file, then emit the structured card the watchdog consumes.</summary>
    public static async Task Protocol(string[] args)
    {
        var prompt = await Console.In.ReadToEndAsync();
        var modelAt = Array.IndexOf(args, "--model");
        if (modelAt < 0 || args[modelAt + 1] != "devin/swe-2")
            throw new Exception("Explicit cheapest model required");
        var cardFile = args[1];
        var taskId = args[2];
        var partId = args[3];
        var task = Path.GetFileName(Environment.CurrentDirectory.TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        var session = "conv3-session-" + task;
        var file = taskId == "task-t2" ? "second.txt" : "first.txt";
        File.WriteAllText(file, taskId + "\n");
        await Git(Environment.CurrentDirectory, "add", file);
        await Git(Environment.CurrentDirectory, "commit", "-q", "-m", "CONV-3 deliver " + task);
        var head = (await Git(Environment.CurrentDirectory, "rev-parse", "HEAD")).Trim();
        var card = JsonSerializer.Serialize(new { taskId, status = "module_ready", head,
            writerStopped = true, parts = new[] { partId } });
        File.AppendAllText(cardFile, JsonSerializer.Serialize(new { task, session, head, prompt }) + "\n");
        // The watchdog reads the card from the checkout, not from stdout; the stdout line is the
        // harness's own protocol message and is intentionally kept separate.
        var cardDirectory = Path.Combine(Environment.CurrentDirectory, "tmp");
        Directory.CreateDirectory(cardDirectory);
        File.WriteAllText(Path.Combine(cardDirectory, "delivery.json"), card);
        Console.WriteLine(JsonSerializer.Serialize(new { type = "thread.started", thread_id = session }));
        Console.WriteLine(JsonSerializer.Serialize(new { type = "item.completed",
            item = new { type = "agent_message", text = card } }));
        Console.WriteLine("{\"type\":\"turn.completed\"}");
    }

    public static async Task Run(string root, Action<bool, string> check)
    {
        await AutomaticModeGate(root, check);
        await IdleReconciliation(root, check);
        await GoalDirectToCore(root, check);
    }

    private static async Task<string> Git(string dir, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var text = await output; var detail = await error;
        if (process.ExitCode != 0) throw new Exception("git " + args[0] + ": " + detail);
        return text.Trim();
    }

    private static async Task<string> InitRepo(string dir)
    {
        Directory.CreateDirectory(dir);
        await Git(dir, "init", "-q");
        await Git(dir, "config", "user.email", "conv3@example.invalid");
        await Git(dir, "config", "user.name", "conv3");
        await Git(dir, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(dir, "baseline.txt"), "BASE\n");
        await Git(dir, "add", "-A");
        await Git(dir, "commit", "-q", "-m", "baseline");
        return await Git(dir, "rev-parse", "HEAD");
    }

    private static long Revision(AutomationStore store) => store.ReadControlSnapshot().Revision;

    private static void DefineAndApprove(AutomationStore store, string id, string goal, ref long n,
        long now)
    {
        store.ApplyControlCommand(new("c" + ++n, Revision(store), "fixture", ControlAction.DefinePackage,
            new(id, id, goal, ["src/**"], ["Behaviour verified"], "codex", "devin/swe-2", "code", []),
            ExpectedPackageVersion: 0), now);
        store.ApplyControlCommand(new("c" + ++n, Revision(store), "fixture", ControlAction.ApprovePackage,
            PackageId: id, ExpectedPackageVersion: 1), now);
    }

    private static AutomationDelegation Delegation(long max = 8) =>
        new(2, true, true, max, "当前项目仓库与受管工作树", "devin/swe-2",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "fixture");

    private static HarnessRuntime Runtime(string counter, string taskId, string partId)
    {
        // A framework-dependent build launches through `dotnet`, so the assembly must be the first
        // argument; an apphost build runs the process directly.
        var executable = Environment.ProcessPath!;
        var prefix = new List<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            prefix.Add(typeof(AutoAdvancementChecks).Assembly.Location);
        prefix.AddRange(["--conv3-protocol", counter, taskId, partId, "--model", "devin/swe-2"]);
        return new("conv3-fixture", "codex", executable, prefix.ToArray(), "fixture-v1");
    }

    // ---------------------------------------------------------------- 1. the mode gate itself

    private static async Task AutomaticModeGate(string root, Action<bool, string> check)
    {
        var dir = Path.Combine(root, "mode"); Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "project.db");
        using var store = new AutomationStore(db);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var n = 0L;
        DefineAndApprove(store, "pkg", "Create the deliverable", ref n, now);
        check(store.ReadControlSnapshot().Mode == DevelopmentMode.Manual,
            "CONV-3: a new project still starts in manual mode");
        store.ApplyControlCommand(new("c" + ++n, Revision(store), "fixture", ControlAction.SetMode,
            Mode: DevelopmentMode.Automatic), now);
        check(store.ReadControlSnapshot().Mode == DevelopmentMode.Automatic,
            "CONV-3: Automatic is accepted and persisted instead of throwing");
        check(store.ReadControlSnapshot().Intents.Any(i => i.Status == "awaiting_binding"),
            "CONV-3: Automatic materializes a runnable intent for the approved plan");
        using (var reopened = new AutomationStore(db))
            check(reopened.ReadControlSnapshot().Mode == DevelopmentMode.Automatic,
                "CONV-3: the automatic mode survives reopening the database");
        store.ApplyControlCommand(new("c" + ++n, Revision(store), "fixture", ControlAction.SetMode,
            Mode: DevelopmentMode.Manual), now);
        check(store.ReadControlSnapshot().Mode == DevelopmentMode.Manual &&
            store.ReadControlSnapshot().Intents.All(i => i.Status == "suspended"),
            "CONV-3: returning to manual suspends the intent without deleting it");
        await Task.CompletedTask;
    }

    // -------------------------------------------- 2. real host + real delivery + idle advancement

    private static async Task IdleReconciliation(string root, Action<bool, string> check)
    {
        var dir = Path.Combine(root, "idle"); Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "project.db");
        var main = Path.Combine(dir, "integration");
        var baseline = await InitRepo(main);
        var counter = Path.Combine(dir, "writers.jsonl");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var store = new AutomationStore(db);
        var journal = new HostJournal(store);
        var n = 0L;
        DefineAndApprove(store, "pkg-a", "Create first.txt", ref n, now);

        // A real registered execution identity and a real isolated checkout cloned at the baseline.
        var tree = Path.Combine(dir, "t1");
        await Git(dir, "clone", "-c", "core.autocrlf=false", "-q", main, tree);
        await Git(tree, "config", "user.email", "conv3@example.invalid");
        await Git(tree, "config", "user.name", "conv3");
        store.CreateThread("thread-t1", "project-a", "codex", "devin/swe-2", tree, null, now);
        store.CreateTask("task-t1", "thread-t1", "pkg-a", baseline, now);
        store.CreatePart("part-t1", "task-t1", "pkg-a delivery", now);

        store.ApplyControlCommand(new("c" + ++n, Revision(store), "fixture", ControlAction.SetMode,
            Mode: DevelopmentMode.Automatic), now);
        // The intent must be bound by the trusted consumer, which is the existing trusted path.
        var consumer = new ControlIntentConsumer(store);
        var intent = store.ReadControlSnapshot().Intents.Single(i => i.PackageId == "pkg-a");
        var queued = await consumer.QueueAsync(new("conv3-bind-t1", Revision(store), intent.Id, 1,
            "thread-t1", "task-t1", Runtime(counter, "task-t1", "part-t1"), null), now);

        var options = new HostOptions(db, Path.Combine(dir, "runs"), "conv3-host",
            Path.Combine(dir, "endpoint"));
        string head;
        await using (var host = new BackgroundHost(options))
        {
            check(await host.StartAsync(), "CONV-3: the sole background host starts");
            var until = DateTime.UtcNow.AddSeconds(60);
            while (journal.Get(queued.RunId)!.State is HostRequestState.Queued or HostRequestState.Started)
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("CONV-3 host wait expired");
                await Task.Delay(100);
            }
            var run = host.Executor.Runs.Get(queued.RunId);
            check(run.Status == HarnessRunStatus.Completed, "CONV-3: the fixture writer completed for real");
            head = run.SettleHead!;

            // Capture the terminal state through the real watchdog, exactly like production does.
            var watchdog = new HostDeliveryWatchdog(store, host.Executor.Runs,
                new DeliveryReceiver(store, host.Executor.Runs, GitCliEvidence.Shared));
            var report = await watchdog.ObserveAsync(queued.RunId, now + 1_000);
            check(report.Captures.Any(c => c.Outcome == "delivery.received"),
                "CONV-3: the real delivery was captured into the store");
            check(store.GetPart("part-t1").Status == DeliveryPartStatus.Delivered &&
                !store.HasDecision("part-t1", 1),
                "CONV-3: capture is not acceptance");

            // Now the host is idle: the engine must advance with no active worker.
            var engine = new AutoAdvancementEngine(store);
            check((await engine.AdvanceAsync("project-a", now + 2_000)).Outcome ==
                AutoAdvancementOutcome.NeedsDelegation,
                "CONV-3: Automatic without a recorded delegation refuses to guess its scope");
            engine.RecordDelegation("project-a", Delegation());

            var advance = await engine.AdvanceAsync("project-a", now + 3_000);
            check(advance.Outcome == AutoAdvancementOutcome.Advanced,
                "CONV-3: an idle automatic project advances instead of stalling (" + advance.Detail + ")");
            check(advance.ReviewsAccepted == 1 &&
                store.GetPart("part-t1").Status == DeliveryPartStatus.Accepted,
                "CONV-3: the settled delivery is auto-accepted inside the recorded scope");
            check(advance.IntegrationsAuthorized == 1 &&
                store.ReadControlIntegrationCandidate("pkg-a") is { Authorized: true, Integrated: false },
                "CONV-3: accepted evidence is authorized for integration, not merged by the engine");
            check(store.ListRevisions("part-t1").Single().HeadSha == head &&
                store.ListDecisions("part-t1").Single().Decision == AutomationDecision.Accept,
                "CONV-3: the accepted revision keeps the run's own settle HEAD");

            // Idempotence: a second pass adds no duplicate decision and reports a real terminal state.
            var decisions = store.ListDecisions("part-t1").Count;
            var second = await engine.AdvanceAsync("project-a", now + 4_000);
            check(store.ListDecisions("part-t1").Count == decisions,
                "CONV-3: re-running the pass adds no duplicate decision");
            check(second.Outcome is AutoAdvancementOutcome.Stalled or AutoAdvancementOutcome.ProjectCompleted,
                "CONV-3: the second pass reports a real state, not another advance");

            // Integration is a human/M step, not an engine side effect; do it, then confirm the
            // engine reports the project as completed rather than spinning.
            var candidate = store.ReadControlIntegrationCandidate("pkg-a")!;
            store.MarkIntegrated("part-t1", 1, head, "fixture", now + 5_000);
            var done = await engine.AdvanceAsync("project-a", now + 6_000);
            check(done.Outcome == AutoAdvancementOutcome.ProjectCompleted,
                "CONV-3: an accepted and integrated target reaches the completed terminal state");
            await host.StopAsync();
        }
        check(File.Exists(Path.Combine(tree, "first.txt")),
            "CONV-3: the fixture writer produced a real file in its own checkout");
        using (var reopened = new AutomationStore(db))
            check(reopened.GetPart("part-t1").Status == DeliveryPartStatus.Accepted &&
                reopened.IsIntegrated("part-t1", 1),
                "CONV-3: the decisions survive reopening the authoritative store");
    }

    // ---------------------------------------------------------------- 3. goal direct to core

    private static async Task GoalDirectToCore(string root, Action<bool, string> check)
    {
        var dir = Path.Combine(root, "goal"); Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "project.db");
        using var store = new AutomationStore(db);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        check(store.ListProjectGoals().Count == 0, "CONV-3: a fresh project has no goal version yet");
        var first = store.ApplyProjectGoal(new ProjectGoalCommand("g1", 0, "human", "project-g",
            "Ship the workbench", ["Goal coverage with matching candidate SHA"],
            DevelopmentMode.Automatic), now);
        check(first.Version == 1 && store.ReadControlSnapshot().Mode == DevelopmentMode.Automatic,
            "CONV-3: submitting a goal generates goal version 1 and activates automatic");
        check(store.ApplyProjectGoal(new ProjectGoalCommand("g1", 0, "human", "project-g",
            "Ship the workbench", ["Goal coverage with matching candidate SHA"],
            DevelopmentMode.Automatic), now) == first,
            "CONV-3: a replayed goal command returns the original receipt");
        var second = store.ApplyProjectGoal(new ProjectGoalCommand("g2", 1, "human", "project-g",
            "Ship the workbench and the docs", ["Docs included"], DevelopmentMode.Automatic), now);
        check(second.Version == 2 && store.CurrentProjectGoal("project-g") is { Version: 2 },
            "CONV-3: a new goal creates a new version");
        check(store.ListProjectGoals("project-g").Single(g => g.Version == 1).Status == "superseded",
            "CONV-3: the previous goal version is closed, never deleted");
        try
        {
            store.ApplyProjectGoal(new ProjectGoalCommand("g3", 1, "human", "project-g",
                "Stale write", ["x"], DevelopmentMode.Automatic), now);
            check(false, "CONV-3: a stale expected-goal-version must be refused");
        }
        catch (InvalidOperationException)
        {
            check(true, "CONV-3: a stale expected-goal-version is refused");
        }
        await Task.CompletedTask;
    }
}
