using System.Diagnostics;
using System.Text.Json;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;
using Fufucode.Hosting.Tests;
using Fufucode.Plugins.AutoDev.Engine.Harnesses;
using static Fufucode.Hosting.Tests.Fixture;
using static Fufucode.Hosting.Tests.Verify;

BatchHarnessAdapters.Register();

// C1 execution reliability: owner and child-writer verification, controlled resolution of Unknown,
// and the delivery receiving gate, plus the C2 background host and its client protocol. Every case
// uses real child processes, real git repositories and real SQLite journals under a temp directory.
// No model is contacted; nothing touches user data.
if (args.Length >= 3 && args[0] == "--child") return await ChildRunner.Run(args);

// Optional real-model probe, only on an explicit request and only with the cheapest allowed model.
if (args.Length >= 2 && args[0] == "--probe")
{
    if (args.Length < 3 || args[2] != "devin/swe-2")
        throw new ArgumentException("The probe must name the free tier model explicitly: devin/swe-2.");
    return await Probe.Run(args[1], args[2]);
}

var root = Path.Combine(Path.GetTempPath(), "fufucode-hosting-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Console.WriteLine("Hosting evidence: " + root);
if (args.SequenceEqual(["--boundaries-only"]))
{
    await HostBoundaryChecks.Run(root);
    Console.WriteLine($"PASS {Passed} boundary checks. Evidence: {root}"); return 0;
}
Console.WriteLine("Evidence root: " + root);
var self = SystemProcessProbe.Shared;
var git = GitCliEvidence.Shared;

// --------------------------------------------- 1. owner identity is journaled, not guessed
{
    var scenario = New(root, "owner");
    var initial = GitInit(scenario.Worktree);
    using var store = Seed(scenario);
    var executor = new HarnessExecutor(store, scenario.Runs, "host-A@1", git);
    var local = new RunReconciler(store, executor.Runs, self, git, "host-A@1");
    var other = new RunReconciler(store, executor.Runs, self, git, "host-B@2");

    var run = await executor.ExecuteAsync(Runtime(scenario),
        new HarnessRequest("run-a", "thread", "task", "devin/swe-2", "commit"));
    Check(run.Status == HarnessRunStatus.Completed, "owner: a real child process completes the run");
    Check(run.SupervisorPid == self.CurrentPid && run.HostId == "host-A@1" &&
          run.SupervisorStartUtc is > 0 && run.WriterPid is > 0 && run.WriterStartUtc is > 0,
        "owner: supervisor and writer identity journaled as pid plus start time");
    Check(self.Probe(run.WriterPid!.Value).Liveness == ProcessLiveness.Dead,
        "owner: the finished writer process really is gone");
    Check(run.WorktreeHead == initial, "owner: launch-time HEAD taken from the real repository");
    Check((await git.HeadAsync(scenario.Worktree)) != initial,
        "owner: the child really committed inside the worktree");
    var audit = await local.AuditAsync("run-a");
    Check(audit.Verdict == OwnerVerdict.ConfirmedFinished && audit.WriterStopped,
        "owner: a terminal run with no live writer reconciles as finished");
    Check((await other.AuditAsync("run-a")).Verdict == OwnerVerdict.ConfirmedFinished,
        "owner: another host can verify a finished outcome read-only");
    Check(executor.Runs.ListActive().Count == 0, "owner: a terminal run releases the thread");
}

// --------------------------------------------- 2. two hosts compete; the live one owns
{
    var scenario = New(root, "compete");
    GitInit(scenario.Worktree);
    using var store = Seed(scenario);
    var executor = new HarnessExecutor(store, scenario.Runs, "host-A@1", git);
    using var supervisor = LaunchHost(scenario, "host-B@2", "run-host-b", "hang:12");
    var journaled = await AwaitIdentity(executor.Runs, "run-host-b");
    Check(journaled.SupervisorPid == supervisor.Id && journaled.HostId == "host-B@2",
        "compete: a real foreign host process is journaled as the supervisor");
    var local = new RunReconciler(store, executor.Runs, self, git, "host-A@1");
    var audit = await local.AuditAsync("run-host-b");
    Check(audit.Verdict == OwnerVerdict.ActiveElsewhere && !audit.WriterStopped,
        "compete: a live foreign writer blocks every takeover attempt");
    Check((await local.MarkOwnerLostAsync("run-host-b", Now())).Status == HarnessRunStatus.Running,
        "compete: an active run is never projected Unknown by another host");
    var blocked = await local.ResolveAsync(Solution("run-host-b", "thread", journaled.WorktreeHead!,
        HarnessRunStatus.Failed), Now());
    Check(!blocked.Applied && blocked.Code == "not-unknown",
        "compete: an open run is refused by the resolution entry, which only settles Unknown");

    using var witness = LaunchHost(scenario, "host-C@3", "run-host-c", "hang:2", "compete");
    Check(await Exit(witness, 60) == 3,
        "compete: a third host is refused before it can start a parallel writer");
    Check(StartCount(scenario.WriterCounter) == 1,
        "compete: only the owning host's writer started");
    Check(await Exit(supervisor, 60) == 0, "compete: only the owning host finishes its run");
    Check(executor.Runs.Get("run-host-b").Status == HarnessRunStatus.Completed,
        "compete: the journal of a finished run is written by its own host");
}

// --------------------------------------------- 3. a child writer surviving its supervisor
{
    var scenario = New(root, "survivor");
    GitInit(scenario.Worktree);
    var pidFile = Path.Combine(scenario.State, "survivor.pid");
    using var store = Seed(scenario);
    var runs = new HarnessRunStore(store);
    // The journaled writer exits, but its own child keeps holding the worktree.
    var (deadPid, deadStart) = ExitSpawner(scenario, pidFile, "120");
    var survivorPid = int.Parse(File.ReadAllText(pidFile));
    SeedRunning(scenario.Database, "run-orphan", "thread", scenario.Worktree);
    SetOwner(scenario.Database, "run-orphan", deadPid, deadStart, deadPid, deadStart, "Unknown");
    var head = new RunReconciler(store, runs, self, git, "host-A@1");
    var audit = await head.AuditAsync("run-orphan");
    Check(audit.Verdict == OwnerVerdict.ActiveElsewhere && audit.LiveChildren.Contains(survivorPid),
        "survivor: a real child writer of the dead owner is still active");
    var rejected = await head.ResolveAsync(Solution("run-orphan", "thread",
        (await git.HeadAsync(scenario.Worktree))!, HarnessRunStatus.Failed), Now());
    Check(!rejected.Applied && rejected.Code == "owner",
        "survivor: an active writer blocks resolution even though its parent exited");
    Check(GetStatus(scenario.Database, "run-orphan") == "Unknown",
        "survivor: a blocked resolution changes nothing in the journal");
    KillTree(survivorPid);
    await WaitFor(async () => (await head.AuditAsync("run-orphan")).Verdict ==
        OwnerVerdict.ConfirmedFinished, "survivor: after the child exits the owner is finished", 20000);
    Check((await head.ResolveAsync(Solution("run-orphan", "thread",
        (await git.HeadAsync(scenario.Worktree))!, HarnessRunStatus.Failed), Now())).Applied,
        "survivor: resolution is allowed once every writer is gone");
    Check(StartCount(Path.Combine(scenario.State, "survivor-child.log")) == 1,
        "survivor: the surviving writer really existed");
    Check(StartCount(scenario.WriterCounter) == 1, "survivor: settlement launched nothing new");
}

// --------------------------------------------- 4. pid reuse and uninspectable processes
{
    var scenario = New(root, "reuse");
    var head = GitInit(scenario.Worktree);
    using var store = Seed(scenario);
    var runs = new HarnessRunStore(store);
    SeedRunning(scenario.Database, "run-reuse", "thread", scenario.Worktree);
    // A real process exists now; a recorded start time minutes ago cannot be its identity, which
    // is exactly what operating-system pid reuse looks like from the inside.
    using var alive = Launch("--child", "hold", Path.Combine(scenario.State, "hold.log"), "20");
    var observed = self.Probe(alive.Id);
    var stale = DateTimeOffset.UtcNow.AddMinutes(-45);
    Check(observed.Liveness == ProcessLiveness.Alive && !observed.SameIdentityAs(stale),
        "reuse: a real live pid with a different recorded start time fails the identity test");
    // The reconciler consumes that observation through the probe seam with a synthetic pid that has
    // no children, so the verdict is deterministic instead of depending on the machine.
    var recycled = 999_999;
    SetOwner(scenario.Database, "run-reuse", recycled, stale.ToUnixTimeMilliseconds(), recycled,
        stale.ToUnixTimeMilliseconds(), "Unknown");
    var mismatched = new RunReconciler(store, runs,
        new FakeProbe(ProcessLiveness.Alive, DateTimeOffset.UtcNow), git, "host-A@1");
    Check((await mismatched.AuditAsync("run-reuse")).Verdict == OwnerVerdict.Unverifiable,
        "reuse: a live pid whose start time differs is unverifiable, never assumed");
    Check(!(await mismatched.MarkOwnerLostAsync("run-reuse", Now())).WriterStopped,
        "reuse: an unverifiable identity is never declared stopped");
    var blocked = await mismatched.ResolveAsync(Solution("run-reuse", "thread", head,
        HarnessRunStatus.Failed), Now());
    Check(!blocked.Applied && blocked.Code == "owner",
        "reuse: an unverifiable identity blocks resolution of an Unknown run");
    Check(GetStatus(scenario.Database, "run-reuse") == "Unknown",
        "reuse: a blocked resolution changes nothing in the journal");
    var blind = new RunReconciler(store, runs, new FakeProbe(ProcessLiveness.Indeterminate), git,
        "host-A@1");
    Check((await blind.AuditAsync("run-reuse")).Verdict == OwnerVerdict.Unverifiable,
        "blind: a process that cannot be inspected is unverifiable");
    Check(!(await blind.ResolveAsync(Solution("run-reuse", "thread", head,
        HarnessRunStatus.Failed), Now())).Applied, "blind: an uninspectable owner blocks resolution");
    SetOwner(scenario.Database, "run-reuse", null, null, null, null, "Unknown");
    var anonymous = new RunReconciler(store, runs, self, git, "host-A@1");
    Check((await anonymous.AuditAsync("run-reuse")).Verdict == OwnerVerdict.Unverifiable,
        "blind: a run with no journaled identity is unverifiable");
    Check(!(await anonymous.ResolveAsync(Solution("run-reuse", "thread", head,
        HarnessRunStatus.Failed), Now())).Applied, "blind: no identity means no resolution");
    alive.Kill(entireProcessTree: true);
}

// --------------------------------------------- 5. supervisor crash: Unknown, freeze, one settlement
{
    var scenario = New(root, "crash");
    GitInit(scenario.Worktree);
    using var store = Seed(scenario);
    var executor = new HarnessExecutor(store, scenario.Runs, "host-A@1", git);
    using (var supervisor = LaunchHost(scenario, "host-A@1", "run-crash", "hang:120"))
    {
        var journaled = await AwaitIdentity(executor.Runs, "run-crash");
        Check(self.Probe(journaled.WriterPid!.Value).Liveness == ProcessLiveness.Alive,
            "crash: a real writer process is in flight");
        KillTree(supervisor.Id);
    }
    var reconciler = new RunReconciler(store, executor.Runs, self, git, "host-A@1");
    await WaitFor(async () => (await reconciler.AuditAsync("run-crash")).Verdict ==
        OwnerVerdict.ConfirmedFinished, "crash: the killed owner reconciles as finished", 20000);
    Check((await reconciler.MarkOwnerLostAsync("run-crash", Now())).Status == HarnessRunStatus.Unknown,
        "crash: the lost owner projects Unknown");
    Check(store.GetThread("thread").Status == ThreadStatus.Frozen, "crash: the thread freezes");
    await Reject(() => executor.ExecuteAsync(Runtime(scenario),
        new HarnessRequest("run-next", "thread", "task", "devin/swe-2", "echo:second")),
        "crash: an unresolved Unknown blocks any new dispatch in that thread");
    Check(StartCount(scenario.WriterCounter) == 1,
        "crash: reconciliation and the refused dispatch launched nothing");
    var headNow = (await git.HeadAsync(scenario.Worktree))!;
    Check(!(await reconciler.ResolveAsync(Solution("run-crash", "ghost-thread", headNow,
        HarnessRunStatus.Failed), Now())).Applied, "crash: evidence must name the bound thread");
    var wrongHead = await reconciler.ResolveAsync(Solution("run-crash", "thread", new string('c', 40),
        HarnessRunStatus.Failed), Now());
    Check(!wrongHead.Applied && wrongHead.Code == "head-mismatch",
        "crash: evidence HEAD must equal the real repository HEAD");
    var shortSha = await reconciler.ResolveAsync(Solution("run-crash", "thread", "abc1234",
        HarnessRunStatus.Failed), Now());
    Check(!shortSha.Applied && shortSha.Code == "head-format", "crash: abbreviated SHAs are rejected");
    var notJson = await reconciler.ResolveAsync(new RunResolution("run-crash",
        HarnessRunStatus.Failed, "M", "not json"), Now());
    Check(!notJson.Applied && notJson.Code == "evidence", "crash: evidence must be one JSON object");
    var blankAuthor = await reconciler.ResolveAsync(new RunResolution("run-crash",
        HarnessRunStatus.Failed, " ",
        Solution("run-crash", "thread", headNow, HarnessRunStatus.Failed).EvidenceJson), Now());
    Check(!blankAuthor.Applied && blankAuthor.Code == "decided-by",
        "crash: a resolution must name its author");
    Check((await reconciler.ResolveAsync(Solution("run-crash", "thread", headNow,
        HarnessRunStatus.Failed), Now())).Applied,
        "crash: the controlled resolution settles the Unknown run");
    Check(StartCount(scenario.WriterCounter) == 1, "crash: resolution never reruns the model");
    var twice = await reconciler.ResolveAsync(Solution("run-crash", "thread", headNow,
        HarnessRunStatus.Completed), Now());
    Check(!twice.Applied && (twice.Code == "not-unknown" || twice.Code == "already-resolved"),
        "crash: a settled run cannot be re-decided");
    Check(executor.Runs.ListResolutions().Count == 1, "crash: the resolution journal is append-only");
    store.UnfreezeThread("thread", "unfreeze-1", Now());
    var next = await executor.ExecuteAsync(Runtime(scenario),
        new HarnessRequest("run-next", "thread", "task", "devin/swe-2", "echo:third"));
    Check(next.Status == HarnessRunStatus.Completed,
        "crash: the thread continues only after a settled outcome and an explicit unfreeze");
    Check(GetStatus(scenario.Database, "run-crash") == "Failed",
        "crash: the settled status is the journaled one");
}

// --------------------------------------------- 6. the delivery receiving gate
{
    var scenario = New(root, "gate");
    GitInit(scenario.Worktree);
    using var store = Seed(scenario);
    var executor = new HarnessExecutor(store, scenario.Runs, "host-A@1", git);
    var receiver = new DeliveryReceiver(store, executor.Runs, git);
    var run = await executor.ExecuteAsync(Runtime(scenario),
        new HarnessRequest("run-delivery", "thread", "task", "devin/swe-2", "commit"));
    var head = (await git.HeadAsync(scenario.Worktree))!;
    Check(run.Status == HarnessRunStatus.Completed, "gate: the transport run completed");
    Check(store.ListRevisions("part").Count == 0,
        "gate: a completed transport records no delivery revision by itself");
    var missingRun = await receiver.ReceiveAsync(Candidate("run-never", head, "rev-none", "gate-none"));
    Check(!missingRun.AcceptedForReview && missingRun.Code == "unknown-run",
        "gate: a run that was never dispatched is rejected");
    var shortHead = await receiver.ReceiveAsync(Candidate("run-delivery", "abc1234",
        "rev-short", "gate-short"));
    Check(!shortHead.AcceptedForReview && shortHead.Code == "head-format",
        "gate: a delivery must name a full commit SHA");
    var wrongHead = await receiver.ReceiveAsync(Candidate("run-delivery", new string('d', 40),
        "rev-w", "gate-w"));
    Check(!wrongHead.AcceptedForReview && wrongHead.Code == "head-mismatch",
        "gate: a HEAD that is not the repository HEAD is rejected");
    var wrongThread = await receiver.ReceiveAsync(Candidate("run-delivery", head, "rev-t", "gate-t")
        with { ThreadId = "ghost" });
    Check(!wrongThread.AcceptedForReview && wrongThread.Code == "thread-mismatch",
        "gate: run and thread identity must agree");
    var wrongTask = await receiver.ReceiveAsync(Candidate("run-delivery", head, "rev-k", "gate-k")
        with { TaskId = "other-task" });
    // Refused against the journaled dispatch identity, which is stronger than the card's own wiring:
    // no task name that the run was not dispatched for can pass, whatever the card claims.
    Check(!wrongTask.AcceptedForReview && wrongTask.Code == "run-task-mismatch",
        "gate: the card's task must be the task the run was dispatched for");
    var wrongPart = await receiver.ReceiveAsync(Candidate("run-delivery", head, "rev-p", "gate-p")
        with { PartId = "ghost-part" });
    Check(!wrongPart.AcceptedForReview && wrongPart.Code == "unknown-part",
        "gate: an unknown delivery part is rejected");
    var badCard = await receiver.ReceiveAsync(Candidate("run-delivery", head, "rev-c", "gate-c")
        with { CardStatus = "blocked" });
    Check(!badCard.AcceptedForReview && badCard.Code == "card-status",
        "gate: a non-deliverable card status is rejected");
    var accepted = await receiver.ReceiveAsync(Candidate("run-delivery", head, "rev-1", "gate-1"));
    Check(accepted.AcceptedForReview && accepted.Code == "delivered" && accepted.RevisionNumber == 1,
        "gate: bound identity, terminal journal and real HEAD make one reviewable delivery");
    Check(!store.HasDecision("part", 1) && !store.IsIntegrated("part", 1),
        "gate: a received delivery is neither accepted nor integrated");
    var replay = await receiver.ReceiveAsync(Candidate("run-delivery", head, "rev-1-retry",
        "gate-1-retry"));
    Check(replay.AcceptedForReview && replay.Code == "already-recorded" && replay.RevisionNumber == 1,
        "gate: a lost acknowledgment replays the same revision instead of duplicating");
    Check(store.ListRevisions("part").Count == 1, "gate: the replay added no second revision");
    var conflict = await receiver.ReceiveAsync(Candidate("run-delivery", head, "rev-1-conflict",
        "gate-1-conflict") with { CardJson = "{\"changed\":1}" });
    Check(!conflict.AcceptedForReview && conflict.Code == "conflicting-delivery",
        "gate: the same identity with different content is refused");

    // A stream without a terminal record and a native failure can never be presented as a delivery.
    var truncated = await executor.ExecuteAsync(Runtime(scenario),
        new HarnessRequest("run-trunc", "thread", "task", "devin/swe-2", "truncate"));
    Check(truncated.Status == HarnessRunStatus.Unknown,
        "gate: a stream without a terminal record is Unknown, not success");
    var truncatedDelivery = await receiver.ReceiveAsync(Candidate("run-trunc", head, "rev-trunc",
        "gate-trunc"));
    Check(!truncatedDelivery.AcceptedForReview &&
          truncatedDelivery.Code == "unknown-requires-resolution",
        "gate: a truncated stream routes to resolution, not to review");
    // An open Unknown holds the thread, so nothing else can be dispatched until it is settled.
    await Reject(() => executor.ExecuteAsync(Runtime(scenario),
        new HarnessRequest("run-after-trunc", "thread", "task", "devin/swe-2", "echo:x")),
        "gate: an unresolved truncated run blocks the thread");
    Check(executor.Runs.Get("run-trunc").SessionId is not null,
        "gate: the truncated stream keeps its session identity for reconciliation");
    var settleTrunc = await new RunReconciler(store, executor.Runs, self, git, "host-A@1")
        .ResolveAsync(Solution("run-trunc", "thread", head, HarnessRunStatus.Failed), Now());
    Check(settleTrunc.Applied, "gate: the truncated run is settled through the controlled entry");
    var failed = await executor.ExecuteAsync(Runtime(scenario),
        new HarnessRequest("run-failed", "thread", "task", "devin/swe-2", "fail"));
    Check(failed.Status == HarnessRunStatus.Failed, "gate: a native failure terminal records a failure");
    var failedDelivery = await receiver.ReceiveAsync(Candidate("run-failed", head, "rev-failed",
        "gate-failed"));
    Check(!failedDelivery.AcceptedForReview && failedDelivery.Code == "not-terminal",
        "gate: a failed run is never a delivery");

    // An in-flight writer cannot deliver; its killed owner goes through resolution only.
    using (var inflight = LaunchHost(scenario, "host-A@1", "run-open", "hang:120"))
    {
        var openRun = await AwaitIdentity(executor.Runs, "run-open");
        var whileRunning = await receiver.ReceiveAsync(Candidate("run-open", openRun.WorktreeHead!,
            "rev-o", "gate-o"));
        Check(!whileRunning.AcceptedForReview && whileRunning.Code == "not-terminal",
            "gate: an in-flight run is not a delivery");
        KillTree(inflight.Id);
    }
    var owner = new RunReconciler(store, executor.Runs, self, git, "host-A@1");
    await WaitFor(async () => (await owner.AuditAsync("run-open")).Verdict ==
        OwnerVerdict.ConfirmedFinished, "gate: the killed writer is gone", 20000);
    Check((await owner.MarkOwnerLostAsync("run-open", Now())).Status == HarnessRunStatus.Unknown,
        "gate: the killed run becomes Unknown");
    var killedHead = (await git.HeadAsync(scenario.Worktree))!;
    var unknownDelivery = await receiver.ReceiveAsync(Candidate("run-open", killedHead, "rev-u", "gate-u"));
    Check(!unknownDelivery.AcceptedForReview && unknownDelivery.Code == "unknown-requires-resolution",
        "gate: an unresolved Unknown routes to resolution, not to a new dispatch");
    Check((await owner.ResolveAsync(Solution("run-open", "thread", killedHead,
        HarnessRunStatus.Interrupted), Now())).Applied,
        "gate: the owner is settled as interrupted through the controlled entry");
    var stillNotDelivery = await receiver.ReceiveAsync(Candidate("run-open", killedHead, "rev-i", "gate-i"));
    Check(!stillNotDelivery.AcceptedForReview && stillNotDelivery.Code == "not-terminal",
        "gate: an interrupted run is never a delivery");
    store.UnfreezeThread("thread", "unfreeze-gate", Now());

    // A replaced worktree is refused instead of silently re-bound to a new directory.
    var elsewhere = New(root, "gate-alt");
    GitInit(elsewhere.Worktree);
    SetWorktree(scenario.Database, "run-delivery", elsewhere.Worktree);
    var replaced = await receiver.ReceiveAsync(Candidate("run-delivery", head, "rev-r", "gate-r")
        with { RevisionId = "rev-r2", EventId = "gate-r2" });
    Check(!replaced.AcceptedForReview && replaced.Code == "worktree-replaced",
        "gate: a replaced worktree is refused, not re-bound");
    SetWorktree(scenario.Database, "run-delivery", scenario.Worktree);

    // A frozen thread needs reconciliation before any delivery.
    store.FreezeThread("thread", "shared-M state unknown", "freeze-gate", Now());
    var frozen = await receiver.ReceiveAsync(Candidate("run-delivery", head, "rev-f", "gate-f")
        with { RevisionId = "rev-f2", EventId = "gate-f2" });
    Check(!frozen.AcceptedForReview && frozen.Code == "thread-frozen", "gate: a frozen thread rejects");
    store.UnfreezeThread("thread", "unfreeze-gate-2", Now());

    var confirm = await receiver.ConfirmAsync("part", "run-delivery", head);
    Check(confirm.AcceptedForReview && confirm.Code == "already-recorded" && confirm.RevisionNumber == 1,
        "gate: a lost receipt can be confirmed from the journal");
    var unknownConfirm = await receiver.ConfirmAsync("part", "run-never", head);
    Check(!unknownConfirm.AcceptedForReview && unknownConfirm.Code == "not-recorded",
        "gate: confirming an unrecorded delivery says so instead of inventing one");

    // Rework reopens the part; an accepted revision can never be quietly replaced.
    store.RecordDecision("review-1", "part", AutomationDecision.Rework, "add the missing check", null,
        "M", "decision-1", Now(), "run-delivery", head, 1);
    var reworkRun = await executor.ExecuteAsync(Runtime(scenario),
        new HarnessRequest("run-rework", "thread", "task", "devin/swe-2", "commit"));
    var reworkHead = (await git.HeadAsync(scenario.Worktree))!;
    Check(reworkRun.Status == HarnessRunStatus.Completed, "gate: the rework run completes");
    var stale = await receiver.ReceiveAsync(Candidate("run-rework", head, "rev-stale", "gate-stale"));
    Check(!stale.AcceptedForReview && stale.Code == "head-mismatch",
        "gate: a rework cannot deliver the old HEAD");
    var second = await receiver.ReceiveAsync(Candidate("run-rework", reworkHead, "rev-2", "gate-2"));
    Check(second.AcceptedForReview && second.RevisionNumber == 2,
        "gate: after rework a new revision is received");
    store.RecordDecision("review-2", "part", AutomationDecision.Accept, null, null, "M",
        "decision-2", Now(), "run-rework", reworkHead, 2);
    // Another completed run of the same thread now presents the same HEAD. That HEAD is rework's own
    // commit, so an earlier run cannot use it: provenance refuses the borrow first, and the rule that
    // used to catch it here (immutability) is verified separately with its own honest candidate.
    var borrowedHead = await receiver.ReceiveAsync(Candidate("run-delivery", reworkHead, "rev-3", "gate-3"));
    Check(!borrowedHead.AcceptedForReview && borrowedHead.Code == "head-not-from-run",
        "gate: a completed run cannot present a commit another run produced in the same worktree");
    Check(store.ListRevisions("part").Count == 2,
        "gate: the borrowed-HEAD attempt wrote no third revision");
    var afterAccept = await receiver.ReceiveAsync(new DeliveryCandidate("run-rework", "thread", "task",
        "part", reworkHead, "{\"summary\":\"the accepted work, offered again\"}", "rev-3b", "gate-3b",
        Now()) { CardStatus = "module_ready" });
    Check(!afterAccept.AcceptedForReview && afterAccept.Code == "already-decided",
        "gate: an accepted revision cannot be quietly replaced");
    Check(!store.IsIntegrated("part", 2), "gate: acceptance is recorded but not integration");
    Check(store.ListRevisions("part").Count == 2, "gate: the refused delivery wrote no third revision");
    Check(store.GetThread("thread").Status == ThreadStatus.Completed,
        "gate: accepting the last task completes the thread without any extra dispatch");
}

// --------------------------------------------- 7. a dirty worktree cannot claim a clean HEAD
{
    var scenario = New(root, "dirty");
    GitInit(scenario.Worktree);
    using var store = Seed(scenario);
    var executor = new HarnessExecutor(store, scenario.Runs, "host-A@1", git);
    var receiver = new DeliveryReceiver(store, executor.Runs, git);
    var run = await executor.ExecuteAsync(Runtime(scenario),
        new HarnessRequest("run-dirty", "thread", "task", "devin/swe-2", "echo:claims a commit it never made"));
    var head = (await git.HeadAsync(scenario.Worktree))!;
    File.WriteAllText(Path.Combine(scenario.Worktree, "tracked.txt"), "uncommitted edit\n");
    Check(run.Status == HarnessRunStatus.Completed, "dirty: the transport run completed");
    var dirty = await receiver.ReceiveAsync(Candidate("run-dirty", head, "rev-dirty", "gate-dirty"));
    Check(!dirty.AcceptedForReview && dirty.Code == "dirty-worktree",
        "dirty: uncommitted tracked edits block the delivery");
    Git(scenario.Worktree, ["checkout", "--", "tracked.txt"]);
    var clean = await receiver.ReceiveAsync(Candidate("run-dirty", head, "rev-clean", "gate-clean"));
    Check(clean.AcceptedForReview && clean.Code == "delivered",
        "dirty: the same identity is received once the worktree matches its HEAD");
}

// --------------------------------------------- 8. an unreadable worktree is refused
{
    var scenario = New(root, "missing");
    var head = GitInit(scenario.Worktree);
    using var store = Seed(scenario);
    var executor = new HarnessExecutor(store, scenario.Runs, "host-A@1", git);
    var receiver = new DeliveryReceiver(store, executor.Runs, git);
    var run = await executor.ExecuteAsync(Runtime(scenario),
        new HarnessRequest("run-missing", "thread", "task", "devin/swe-2", "echo:idle"));
    Check(run.Status == HarnessRunStatus.Completed, "missing: the run completes");
    var detached = Path.Combine(root, "deleted-worktree-" + Guid.NewGuid().ToString("N"));
    SetWorktree(scenario.Database, "run-missing", detached);
    var unreadable = await receiver.ReceiveAsync(Candidate("run-missing", head, "rev-x", "gate-x"));
    Check(!unreadable.AcceptedForReview && unreadable.Code is "worktree-replaced" or "worktree-unreadable",
        "missing: a vanished worktree is refused, not guessed");
}

// --------------------------------------------- 9. duplicate delivery survives a store reopen
{
    var scenario = New(root, "reopen");
    GitInit(scenario.Worktree);
    string head;
    using (var store = Seed(scenario))
    {
        var executor = new HarnessExecutor(store, scenario.Runs, "host-A@1", git);
        await executor.ExecuteAsync(Runtime(scenario),
            new HarnessRequest("run-twice", "thread", "task", "devin/swe-2", "commit"));
        head = (await git.HeadAsync(scenario.Worktree))!;
        var receiver = new DeliveryReceiver(store, executor.Runs, git);
        Check((await receiver.ReceiveAsync(Candidate("run-twice", head, "rev-a", "gate-a")))
            .AcceptedForReview, "reopen: the delivery is received");
    }
    using (var reopened = new AutomationStore(scenario.Database))
    {
        var receiver = new DeliveryReceiver(reopened, new HarnessRunStore(reopened), git);
        var again = await receiver.ReceiveAsync(Candidate("run-twice", head, "rev-a-again", "gate-a-again"));
        Check(again.AcceptedForReview && again.Code == "already-recorded" && again.RevisionNumber == 1,
            "reopen: a repeated delivery after a restart stays a single revision");
        Check(reopened.ListRevisions("part").Count == 1, "reopen: no duplicate revision was persisted");
    }
}

// --------------------------------------------- R1 (rework): run task identity is journaled and enforced
{
    var scenario = New(root, "identity");
    GitInit(scenario.Worktree);
    using var store = Seed(scenario);
    SeedSecondTask(store);
    var executor = new HarnessExecutor(store, scenario.Runs, "host-A@1", git);
    var receiver = new DeliveryReceiver(store, executor.Runs, git);

    // Task A really runs and really commits, so its journal entry is complete and terminal.
    var runA = await executor.ExecuteAsync(Runtime(scenario),
        new HarnessRequest("run-a", "thread", "task", "devin/swe-2", "commit"));
    var head = (await git.HeadAsync(scenario.Worktree))!;
    Check(runA.Status == HarnessRunStatus.Completed && runA.Output is not null &&
          runA.SessionId is not null, "identity: task A's run is genuinely complete");
    Check(runA.TaskId == "task",
        "identity: the dispatched task identity is journaled from the request, at claim time");

    // The faithful attack: task B exists in the same thread with its own part, and the card is a
    // well-formed delivery for it, citing A's run plus the real HEAD. Nothing about it is a typo.
    var crossTask = await receiver.ReceiveAsync(new DeliveryCandidate("run-a", "thread", "task-b",
        "part-b", head, "{\"summary\":\"borrowed from another task\"}", "rev-cross", "evt-cross",
        Now()) { CardStatus = "module_ready" });
    Check(!crossTask.AcceptedForReview && crossTask.Code == "run-task-mismatch",
        "identity: a completed run of task A cannot deliver task B's card");
    Check(store.ListRevisions("part-b").Count == 0 && store.ListRevisions("part").Count == 0,
        "identity: the refused cross-task card wrote no revision anywhere");
    // B's own task/part is still deliverable by a run that really belongs to it.
    var runB = await executor.ExecuteAsync(Runtime(scenario),
        new HarnessRequest("run-b", "thread", "task-b", "devin/swe-2", "commit"));
    var headB = (await git.HeadAsync(scenario.Worktree))!;
    Check(runB.Status == HarnessRunStatus.Completed && runB.TaskId == "task-b",
        "identity: task B's own run journals its own task identity");
    var honest = await receiver.ReceiveAsync(new DeliveryCandidate("run-b", "thread", "task-b",
        "part-b", headB, "{\"summary\":\"really task B\"}", "rev-honest", "evt-honest", Now())
    { CardStatus = "module_ready" });
    Check(honest.AcceptedForReview && honest.Code == "delivered" &&
          store.ListRevisions("part-b").Count == 1,
        "identity: the gate still receives a delivery whose run really belongs to the card's task");
    // The reverse direction (task A's part presenting the HEAD that task B's run produced in the same
    // shared worktree) is closed by the settle-time provenance journal and is asserted as its own
    // scenario below, against real runs that each left a different commit. See docs/records/B1-host.md.

    // A legacy row (written before task identity was journaled) fails closed with a diagnosis, and
    // the identity is never guessed from the request hash or the streamed output text.
    SeedRunning(scenario.Database, "run-legacy", "thread", scenario.Worktree);
    SetOwner(scenario.Database, "run-legacy", null, null, null, null, "Completed");
    var legacy = await receiver.ReceiveAsync(new DeliveryCandidate("run-legacy", "thread", "task",
        "part", head, "{\"summary\":\"legacy row\"}", "rev-legacy", "evt-legacy", Now())
    { CardStatus = "module_ready" });
    Check(!legacy.AcceptedForReview && legacy.Code == "run-task-unknown",
        "identity: a journaled run without task identity is refused, never assumed");
    Check(legacy.Detail.Contains("never journaled"),
        "identity: the refusal says what is missing so an operator can act on it");
    Check(executor.Runs.Get("run-legacy").TaskId is null,
        "identity: the receiving gate never writes an identity into the journal");

    // Resolution keeps the journaled identity, and a replay after a lost acknowledgment still does.
    var identityReconciler = new RunReconciler(store, executor.Runs, self, git, "host-A@1");
    var replay = await receiver.ReceiveAsync(new DeliveryCandidate("run-b", "thread", "task-b",
        "part-b", headB, "{\"summary\":\"really task B\"}", "rev-honest-retry", "evt-honest-retry",
        Now()) { CardStatus = "module_ready" });
    Check(replay.AcceptedForReview && replay.Code == "already-recorded" &&
          replay.RevisionNumber == 1 && store.ListRevisions("part-b").Count == 1,
        "identity: a lost acknowledgment replays the same revision with its task identity intact");
    var crossReplay = await receiver.ConfirmAsync("part-b", "run-a", headB);
    Check(!crossReplay.AcceptedForReview && crossReplay.Code == "not-recorded",
        "identity: confirming a borrowed identity finds nothing instead of inventing a revision");

    // Dispatch itself fails closed: a run without a task identity is never journaled, so the gap
    // cannot be created from the entry point either.
    await Reject(() => executor.ExecuteAsync(Runtime(scenario),
            new HarnessRequest("run-no-task", "thread", " ", "devin/swe-2", "echo:x")),
        "identity: a dispatch without a task identity is refused before anything is journaled");
    await Reject(() => Task.FromResult(executor.Runs.Get("run-no-task")),
        "identity: the refused dispatch left no run row behind");
    Check(StartCount(scenario.WriterCounter) == 2,
        "identity: the refused dispatch started no harness writer");
}

// --------------------------------------------- R2 (rework): Unknown resolution settles without rewriting identity
{
    var scenario = New(root, "identity-unknown");
    GitInit(scenario.Worktree);
    using var store = Seed(scenario);
    SeedSecondTask(store);
    var executor = new HarnessExecutor(store, scenario.Runs, "host-A@1", git);
    var receiver = new DeliveryReceiver(store, executor.Runs, git);
    var reconciler = new RunReconciler(store, executor.Runs, self, git, "host-A@1");
    using (var supervisor = LaunchHost(scenario, "host-A@1", "run-u", "hang:120"))
    {
        var journaled = await AwaitIdentity(executor.Runs, "run-u");
        Check(journaled.TaskId == "task", "unknown: an in-flight run already carries its task identity");
        var whileOpen = await receiver.ReceiveAsync(new DeliveryCandidate("run-u", "thread", "task-b",
            "part-b", journaled.WorktreeHead!, "{}", "rev-u", "evt-u", Now())
        { CardStatus = "module_ready" });
        Check(!whileOpen.AcceptedForReview, "unknown: an open run is not a delivery");
        KillTree(supervisor.Id);
    }
    await WaitFor(async () => (await reconciler.AuditAsync("run-u")).Verdict ==
        OwnerVerdict.ConfirmedFinished, "unknown: the killed owner reconciles as finished", 20000);
    Check((await reconciler.MarkOwnerLostAsync("run-u", Now())).Status == HarnessRunStatus.Unknown,
        "unknown: the lost owner projects Unknown");
    var head = (await git.HeadAsync(scenario.Worktree))!;
    var crossBefore = await receiver.ReceiveAsync(new DeliveryCandidate("run-u", "thread", "task-b",
        "part-b", head, "{}", "rev-u-b", "evt-u-b", Now()) { CardStatus = "module_ready" });
    // An open Unknown is refused before anything else is even compared: no card of any task can pass,
    // and the identity guards below still apply once the run has been settled.
    Check(!crossBefore.AcceptedForReview && crossBefore.Code == "unknown-requires-resolution",
        "unknown: a wrong-task card on an open Unknown is refused and routed to resolution");
    Check(store.ListRevisions("part-b").Count == 0,
        "unknown: the refused card wrote no revision for another task's part");
    var settled = await reconciler.ResolveAsync(new RunResolution("run-u",
        HarnessRunStatus.Failed, "M", JsonSerializer.Serialize(new
        {
            runId = "run-u",
            threadId = "thread",
            taskId = "task",
            headSha = head,
            writerStopped = true,
            terminalSignal = "failed"
        })), Now());
    Check(settled.Applied, "unknown: the correct identity settles through the controlled entry");
    Check(executor.Runs.Get("run-u").TaskId == "task",
        "unknown: resolution left the journaled task identity unchanged");
    store.UnfreezeThread("thread", "unfreeze-identity", Now());
    Check(executor.Runs.Get("run-u").TaskId == "task",
        "unknown: the unfrozen thread still reports the original dispatched task");

    // Evidence naming another task cannot retarget a settlement, and a legacy row with no journaled
    // task identity can never be certified as a delivery.
    using (var second = LaunchHost(scenario, "host-A@1", "run-u2", "hang:120"))
    {
        await AwaitIdentity(executor.Runs, "run-u2");
        KillTree(second.Id);
    }
    await WaitFor(async () => (await reconciler.AuditAsync("run-u2")).Verdict ==
        OwnerVerdict.ConfirmedFinished, "unknown: the second killed owner reconciles", 20000);
    await reconciler.MarkOwnerLostAsync("run-u2", Now());
    var headNow = (await git.HeadAsync(scenario.Worktree))!;
    var retargeted = await reconciler.ResolveAsync(new RunResolution("run-u2",
        HarnessRunStatus.Completed, "M", JsonSerializer.Serialize(new
        {
            runId = "run-u2",
            threadId = "thread",
            taskId = "task-b",
            headSha = headNow,
            writerStopped = true,
            terminalSignal = "completed"
        })), Now());
    Check(!retargeted.Applied && retargeted.Code == "run-task-mismatch",
        "unknown: evidence naming another task is refused instead of retargeting the run");
    Check(executor.Runs.Get("run-u2").TaskId == "task",
        "unknown: a refused settlement did not rewrite the identity");
    var certified = await reconciler.ResolveAsync(new RunResolution("run-u2",
        HarnessRunStatus.Completed, "M", JsonSerializer.Serialize(new
        {
            runId = "run-u2",
            threadId = "thread",
            headSha = headNow,
            writerStopped = true,
            terminalSignal = "completed"
        })), Now());
    Check(!certified.Applied && certified.Code == "journal",
        "unknown: an interrupted run without journaled output cannot be certified as a success");
    var halt = await reconciler.ResolveAsync(new RunResolution("run-u2",
        HarnessRunStatus.Interrupted, "M", JsonSerializer.Serialize(new
        {
            runId = "run-u2",
            threadId = "thread",
            taskId = "task",
            headSha = headNow,
            writerStopped = true,
            terminalSignal = "interrupted"
        })), Now());
    Check(halt.Applied && executor.Runs.Get("run-u2").TaskId == "task",
        "unknown: the honest settlement keeps the original task identity");

    // The real legacy shape: a row written by the previous journal, with complete process identity
    // but no task_id at all. It must never be certifiable as a delivery, and it must stay without
    // an invented identity when it is halted.
    var (deadPid, deadStart) = ExitIdentity("--child", "hold",
        Path.Combine(scenario.State, "legacy-hold.log"), "1");
    SeedRunning(scenario.Database, "run-legacy-u", "thread", scenario.Worktree);
    SetOwner(scenario.Database, "run-legacy-u", deadPid, deadStart, deadPid, deadStart, "Unknown");
    var legacySuccess = await reconciler.ResolveAsync(new RunResolution("run-legacy-u",
        HarnessRunStatus.Completed, "M", JsonSerializer.Serialize(new
        {
            runId = "run-legacy-u",
            threadId = "thread",
            headSha = headNow,
            writerStopped = true,
            terminalSignal = "completed"
        })), Now());
    Check(!legacySuccess.Applied && legacySuccess.Code == "run-task-unknown",
        "unknown: a legacy row with no journaled task identity is never certifiable as Completed");
    Check(legacySuccess.Detail.Contains("never journaled"),
        "unknown: the refusal names the missing identity and what to do instead");
    Check(executor.Runs.Get("run-legacy-u").TaskId is null,
        "unknown: the refused settlement invented no task identity");
    var legacyHalt = await reconciler.ResolveAsync(new RunResolution("run-legacy-u",
        HarnessRunStatus.Interrupted, "M", JsonSerializer.Serialize(new
        {
            runId = "run-legacy-u",
            threadId = "thread",
            headSha = headNow,
            writerStopped = true,
            terminalSignal = "interrupted"
        })), Now());
    Check(legacyHalt.Applied && executor.Runs.Get("run-legacy-u").TaskId is null,
        "unknown: the legacy row can still be halted honestly and stays without an invented identity");
}

// --------------------------------------------- H (rework 2, B1-HEAD-1): a run can only present its own settle HEAD
{
    var scenario = New(root, "provenance");
    GitInit(scenario.Worktree);
    using var store = Seed(scenario);
    SeedSecondTask(store);
    var executor = new HarnessExecutor(store, scenario.Runs, "host-A@1", git);
    var receiver = new DeliveryReceiver(store, executor.Runs, git);

    // Task A really runs and really commits.
    var runA = await executor.ExecuteAsync(Runtime(scenario),
        new HarnessRequest("run-head-a", "thread", "task", "devin/swe-2", "commit"));
    Check(runA.Status == HarnessRunStatus.Completed, "head: task A's run completes");
    // Task B then runs in the same thread and worktree and commits on top of A.
    var runB = await executor.ExecuteAsync(Runtime(scenario),
        new HarnessRequest("run-head-b", "thread", "task-b", "devin/swe-2", "commit"));
    Check(runB.Status == HarnessRunStatus.Completed, "head: task B's run completes");
    var headB = (await git.HeadAsync(scenario.Worktree))!;
    Check(headB != runB.WorktreeHead, "head: B really moved the repository forward");

    // The faithful attack: A's completed run presents the repository's current HEAD, which is B's
    // commit. Every guard that existed before this fix is satisfied (same thread, same task, same
    // worktree, terminal journal, HEAD equal to the live repository HEAD), so the gate used to accept it.
    string headA = executor.Runs.Get("run-head-a").SettleHead
        ?? throw new InvalidOperationException("run A journaled no settle HEAD");
    Check(headA != headB,
        "head: each run journaled the HEAD its own writer left, and the two differ");
    var borrowed = await receiver.ReceiveAsync(new DeliveryCandidate("run-head-a", "thread", "task",
        "part", headB, "{\"summary\":\"A's card, B's commit\"}", "rev-head-borrow", "evt-head-borrow",
        Now()) { CardStatus = "module_ready" });
    Check(!borrowed.AcceptedForReview && borrowed.Code == "head-not-from-run",
        "head: a run cannot deliver a HEAD that another run produced");
    Check(borrowed.Detail.Contains(headA),
        "head: the refusal names the HEAD this run actually left so an operator can see the mismatch");
    Check(store.ListRevisions("part").Count == 0,
        "head: the borrowed presentation wrote no revision");

    // A's own commit is still bound to A: presenting it after B moved the repository forward is the
    // pre-existing current-HEAD failure, unchanged by the new rule.
    var staleOwn = await receiver.ReceiveAsync(new DeliveryCandidate("run-head-a", "thread", "task",
        "part", headA, "{\"summary\":\"A's own commit, now behind HEAD\"}", "rev-head-stale",
        "evt-head-stale", Now()) { CardStatus = "module_ready" });
    Check(!staleOwn.AcceptedForReview && staleOwn.Code == "head-mismatch",
        "head: a run presenting its own fixed commit after the worktree moved still fails the current-HEAD check");

    // B's honest delivery for B's part works, and its lost receipt replays instead of duplicating.
    var honestB = await receiver.ReceiveAsync(new DeliveryCandidate("run-head-b", "thread", "task-b",
        "part-b", headB, "{\"summary\":\"B's own commit\"}", "rev-head-b", "evt-head-b", Now())
    { CardStatus = "module_ready" });
    Check(honestB.AcceptedForReview && honestB.Code == "delivered" && honestB.RevisionNumber == 1,
        "head: the run that really produced the HEAD is still deliverable");
    var replayB = await receiver.ReceiveAsync(new DeliveryCandidate("run-head-b", "thread", "task-b",
        "part-b", headB, "{\"summary\":\"B's own commit\"}", "rev-head-b-retry", "evt-head-b-retry",
        Now()) { CardStatus = "module_ready" });
    Check(replayB.AcceptedForReview && replayB.Code == "already-recorded" &&
          replayB.RevisionNumber == 1 && store.ListRevisions("part-b").Count == 1,
        "head: a lost receipt replays the same revision even with provenance enforced");
    var confirmB = await receiver.ConfirmAsync("part-b", "run-head-b", headB);
    Check(confirmB.AcceptedForReview && confirmB.Code == "already-recorded",
        "head: confirming the delivery from the journal still finds it");
    var confirmA = await receiver.ConfirmAsync("part", "run-head-a", headB);
    Check(!confirmA.AcceptedForReview && confirmA.Code == "not-recorded",
        "head: confirming a borrowed identity for another run finds nothing to replay");

    // A row whose provenance was never observed (the shape written before this column existed, or by
    // a host without an observer) fails closed, and can never be certified as a success either.
    SeedLegacy(scenario.Database, "run-legacy-head", "thread", "task", scenario.Worktree, "Completed",
        "fixture-session", "{\"summary\":\"legacy row\"}");
    var legacyDelivery = await receiver.ReceiveAsync(new DeliveryCandidate("run-legacy-head", "thread",
        "task", "part", headB, "{\"summary\":\"legacy row\"}", "rev-head-legacy", "evt-head-legacy",
        Now()) { CardStatus = "module_ready" });
    Check(!legacyDelivery.AcceptedForReview && legacyDelivery.Code == "settle-head-unobserved",
        "head: a run with no journaled provenance is never a delivery");
    Check(legacyDelivery.Detail.Contains("no journaled repository provenance"),
        "head: the refusal says what is missing and what to do instead");
    Check(store.ListRevisions("part").Count == 0,
        "head: the unobserved-provenance row wrote no revision");
    var legacyHeadReconciler = new RunReconciler(store, executor.Runs, self, git, "host-A@1");
    var (gonePid, goneStart) = ExitIdentity("--child", "hold",
        Path.Combine(scenario.State, "legacy-head-hold.log"), "1");
    SeedLegacy(scenario.Database, "run-legacy-settle", "thread", "task", scenario.Worktree, "Unknown",
        "fixture-session", "{\"summary\":\"interrupted writer\"}");
    SetOwner(scenario.Database, "run-legacy-settle", gonePid, goneStart, gonePid, goneStart, "Unknown");
    var certify = await legacyHeadReconciler.ResolveAsync(new RunResolution("run-legacy-settle",
        HarnessRunStatus.Completed, "M", JsonSerializer.Serialize(new
        {
            runId = "run-legacy-settle",
            threadId = "thread",
            taskId = "task",
            headSha = headB,
            writerStopped = true,
            terminalSignal = "completed"
        })), Now());
    Check(!certify.Applied && certify.Code == "settle-head-unobserved",
        "head: an Unknown run cannot be certified as a success with a HEAD read after the fact");
    Check(GetStatus(scenario.Database, "run-legacy-settle") == "Unknown",
        "head: the refused certification left the journal untouched");
    var haltHonestly = await legacyHeadReconciler.ResolveAsync(new RunResolution("run-legacy-settle",
        HarnessRunStatus.Interrupted, "M", JsonSerializer.Serialize(new
        {
            runId = "run-legacy-settle",
            threadId = "thread",
            taskId = "task",
            headSha = headB,
            writerStopped = true,
            terminalSignal = "interrupted"
        })), Now());
    Check(haltHonestly.Applied && executor.Runs.Get("run-legacy-settle").SettleHead is null,
        "head: the honest settlement still applies and attests no provenance for that run");
    Check(StartCount(scenario.WriterCounter) == 2,
        "head: the provenance scenario dispatched exactly the two real runs");
}

// --------------------------------------------- H2 (rework 2, B1-HEAD-1): transport without an observer
{
    // This is the shape tools/Fufucode.HarnessHost builds: an executor with no IGitEvidence. It may
    // still transport a run, but it can never certify one as a delivery, because nothing attested
    // what the repository held when that run's writer exited.
    var scenario = New(root, "no-observer");
    var initial = GitInit(scenario.Worktree);
    using var store = Seed(scenario);
    var executor = new HarnessExecutor(store, scenario.Runs);
    var receiver = new DeliveryReceiver(store, executor.Runs, git);
    var run = await executor.ExecuteAsync(Runtime(scenario),
        new HarnessRequest("run-blind", "thread", "task", "devin/swe-2", "commit"));
    Check(run.Status == HarnessRunStatus.Completed,
        "no-observer: the run still transports without a repository observer");
    Check(executor.Runs.Get("run-blind").WorktreeHead is null &&
          executor.Runs.Get("run-blind").SettleHead is null,
        "no-observer: nothing is attested about the repository when no observer is injected");
    Check((await git.HeadAsync(scenario.Worktree)) != initial,
        "no-observer: the writer really committed, so the work is there but unbound");
    var blindHead = (await git.HeadAsync(scenario.Worktree))!;
    var blind = await receiver.ReceiveAsync(new DeliveryCandidate("run-blind", "thread", "task",
        "part", blindHead, "{\"summary\":\"card from an observer-less host\"}", "rev-blind",
        "evt-blind", Now()) { CardStatus = "module_ready" });
    Check(!blind.AcceptedForReview && blind.Code == "settle-head-unobserved",
        "no-observer: an observer-less host can transport but never certify a delivery");
    Check(store.ListRevisions("part").Count == 0,
        "no-observer: the uncertifiable card wrote no revision");
}

// C2: the durable host queue, the single-host claim, and the one background host process with its
// client protocol. Real processes, real sockets, real journals; still no model contacted.
await HostScenarios.Run(root, self, git);
await HostBoundaryChecks.Run(root);

Console.WriteLine($"PASS {Passed} checks. Evidence: {root}");
return 0;

static DeliveryCandidate Candidate(string runId, string head, string revisionId, string eventId) =>
    new(runId, "thread", "task", "part", head, "{\"summary\":\"fixture delivery\"}", revisionId,
        eventId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) { CardStatus = "module_ready" };

static RunResolution Solution(string runId, string threadId, string head, HarnessRunStatus to) =>
    new(runId, to, "M", JsonSerializer.Serialize(new
    {
        runId,
        threadId,
        headSha = head,
        writerStopped = true,
        terminalSignal = to switch
        {
            HarnessRunStatus.Completed => "completed",
            HarnessRunStatus.Interrupted => "interrupted",
            _ => "failed"
        }
    }));
