using Fufucode.Core.Automation;

// U1 standalone acceptance harness: every check uses a fresh temporary database.
// No model calls, no shared session host, no UI.

var failures = new List<string>();
void Check(bool condition, string name)
{
    if (condition) { Console.WriteLine($"PASS {name}"); return; }
    failures.Add(name);
    Console.WriteLine($"FAIL {name}");
}
void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); failures.Add(name); Console.WriteLine($"FAIL {name} (no throw)"); }
    catch (T) { Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failures.Add(name); Console.WriteLine($"FAIL {name} ({e.GetType().Name})"); }
}

string TempDb()
{
    var dir = Path.Combine(Path.GetTempPath(), "fufucode-u1-" + Guid.NewGuid().ToString("N"));
    return Path.Combine(dir, "automation.db");
}

// -- 1. thread / task / part hierarchy ----------------------------------------
{
    var store = new AutomationStore(TempDb());
    var t = store.CreateThread("th-1", "proj", "pi", "devin/swe-2", @"C:\wt", "sess-1", 1000);
    var task = store.CreateTask("task-A", "th-1", "build module", "base000", 1001);
    store.CreateTask("task-B", "th-1", "other work", "base000", 1002);
    store.CreatePart("p1", "task-A", "part one", 1003);
    store.CreatePart("p2", "task-A", "part two", 1004);
    Check(store.ListTasks("th-1").Count == 2, "thread holds multiple tasks");
    Check(store.ListParts("task-A").Count == 2, "task holds multiple parts");
    Check(t.Status == ThreadStatus.Active && task.Status == AutomationTaskStatus.Planned,
        "initial statuses");
    Check(store.ListEventsAfter(0).Count == 0, "no phantom events on creation");
}

// -- 2. partial rework does not disturb other parts/tasks ---------------------
{
    var store = new AutomationStore(TempDb());
    store.CreateThread("th-1", "proj", "pi", "m", @"C:\wt", null, 0);
    store.CreateTask("task-A", "th-1", "a", "base", 0);
    store.CreatePart("p1", "task-A", "one", 0);
    store.CreatePart("p2", "task-A", "two", 0);
    store.RecordDelivery("r1", "p1", "run-1", "head1", "{}", "e-d1", 0);
    store.RecordDelivery("r2", "p2", "run-1", "head1", "{}", "e-d2", 0);
    store.RecordDecision("d1", "p1", AutomationDecision.Accept, null, null, "M", "e-r1", 0, "run-1", "head1", 1);
    store.RecordDecision("d2", "p2", AutomationDecision.Rework, "fix it", null, "M", "e-r2", 0, "run-1", "head1", 1);

    Check(store.GetPart("p1").Status == DeliveryPartStatus.Accepted, "accepted part stays accepted");
    Check(store.GetPart("p2").Status == DeliveryPartStatus.ReworkRequested, "reworked part flagged");
    Check(store.GetTask("task-A").Status == AutomationTaskStatus.Rework,
        "task reflects partial rework");

    // rework -> new revision on p2; p1 history untouched
    store.RecordDelivery("r3", "p2", "run-2", "head2", "{}", "e-d3", 0);
    Check(store.ListRevisions("p2").Count == 2, "rework creates a new revision");
    Check(store.GetPart("p2").Status == DeliveryPartStatus.Delivered, "redelivery pending review");
    Check(store.GetPart("p1").Status == DeliveryPartStatus.Accepted,
        "redelivery of p2 does not touch p1");
    store.RecordDecision("d3", "p2", AutomationDecision.Accept, null, null, "M", "e-r3", 0, "run-2", "head2", 2);
    Check(store.GetTask("task-A").Status == AutomationTaskStatus.Accepted,
        "task accepted only after every part accepted");
}

// -- 3. recorded decisions are immutable --------------------------------------
{
    var store = new AutomationStore(TempDb());
    store.CreateThread("th-1", "proj", "pi", "m", @"C:\wt", null, 0);
    store.CreateTask("task-A", "th-1", "a", "base", 0);
    store.CreatePart("p1", "task-A", "one", 0);
    store.RecordDelivery("r1", "p1", "run-1", "h1", "{}", "e-d1", 0);
    store.RecordDecision("d1", "p1", AutomationDecision.Accept, null, null, "M", "e-r1", 0, "run-1", "h1", 1);
    Check(store.HasDecision("p1", 1), "decision persisted for revision 1");
    Throws<InvalidOperationException>(
        () => store.RecordDecision("d2", "p1", AutomationDecision.Rework, "x", null, "M", "e-r2", 0, "run-1", "h1", 1),
        "deciding an already-accepted part is rejected");
    Check(store.ListDecisions("p1").Count == 1, "original decision not overwritten");
}

// -- 4. event dedup ------------------------------------------------------------
{
    var store = new AutomationStore(TempDb());
    store.CreateThread("th-1", "proj", "pi", "m", @"C:\wt", null, 0);
    var c1 = store.AppendEvent("ev-1", "delivery.received", "th-1", null, null, null, "{}", 0);
    var c2 = store.AppendEvent("ev-1", "delivery.received", "th-1", null, null, null, "{}", 0);
    Check(c1 == c2, "identical event replay returns same cursor");
    Check(store.ListEventsAfter(0).Count == 1, "replay does not duplicate events");
    Throws<InvalidOperationException>(
        () => store.AppendEvent("ev-1", "delivery.received", "th-1", null, null, null,
            "{\"different\":1}", 0),
        "conflicting event payload rejected");
}

// -- 5. command dedup -----------------------------------------------------------
{
    var store = new AutomationStore(TempDb());
    store.CreateThread("th-1", "proj", "pi", "m", @"C:\wt", null, 0);
    store.CreateTask("task-A", "th-1", "a", "base", 0);
    var applied = 0;
    var cmd = new AutomationCommand("cmd-1", "task.start", "th-1", "task-A", null, "{}", 0);
    var r1 = store.RunCommand(cmd, (c, tx) => applied++, 0);
    var r2 = store.RunCommand(cmd, (c, tx) => applied++, 0);
    Check(r1.Applied && !r2.Applied && applied == 1, "repeated command applies once");
    Check(r2.RejectReason == "duplicate", "duplicate command reported");
}

// -- 6. claim lifecycle + unknown delivery freeze --------------------------------
{
    var store = new AutomationStore(TempDb());
    store.CreateThread("th-1", "proj", "pi", "m", @"C:\wt", null, 0);
    store.CreateTask("task-A", "th-1", "a", "base", 0);
    store.CreatePart("p1", "task-A", "one", 0);
    store.CreateClaim("c1", "task-A", "p1", "run-1", 0);
    store.MarkClaimUnknown("c1");
    store.FreezeThread("th-1", "delivery outcome unknown", "e-frz", 0);
    Check(store.GetThread("th-1").Status == ThreadStatus.Frozen, "unknown delivery freezes thread");
    Check(store.ListUnresolvedClaims().Any(c => c.State == ClaimState.Unknown),
        "unknown claim kept for reconciliation");
    Throws<InvalidOperationException>(
        () => store.ResolveClaim("c1", "run-1"),
        "unknown claim cannot be resolved as delivered");
}

// -- 7. versioned DAG / dependency unlocking -------------------------------------
{
    var store = new AutomationStore(TempDb());
    store.CreateThread("th-1", "proj", "pi", "m", @"C:\wt", null, 0);
    store.CreateTask("task-A", "th-1", "a", "base", 0);
    store.CreateTask("task-B", "th-1", "b", "base", 0);
    store.CreatePart("p1", "task-A", "one", 0);
    store.AddDependency("task-B", "task-A", "p1", requiresIntegration: true);
    Check(!store.IsTaskUnlocked("task-B"), "locked until upstream accepted+integrated");
    store.RecordDelivery("r1", "p1", "run-1", "h1", "{}", "e-d1", 0);
    store.RecordDecision("d1", "p1", AutomationDecision.Accept, null, null, "M", "e-r1", 0, "run-1", "h1", 1);
    Check(!store.IsTaskUnlocked("task-B"), "accepted but not integrated stays locked");
    store.MarkIntegrated("p1", 1, "mainsha", "e-int", 0);
    Check(store.IsTaskUnlocked("task-B"), "unlocks after integration mark");
    Throws<InvalidOperationException>(
        () => store.AddDependency("task-A", "task-B", null, false),
        "cyclic dependency rejected");
}

// -- 8. undo fence: integration marks guard history -------------------------------
{
    var store = new AutomationStore(TempDb());
    store.CreateThread("th-1", "proj", "pi", "m", @"C:\wt", null, 0);
    store.CreateTask("task-A", "th-1", "a", "base", 0);
    store.CreatePart("p1", "task-A", "one", 0);
    store.RecordDelivery("r1", "p1", "run-1", "h1", "{}", "e-d1", 0);
    Throws<InvalidOperationException>(
        () => store.MarkIntegrated("p1", 1, "sha", "e-i", 0),
        "cannot integrate a part with no accept decision");
    store.RecordDecision("d1", "p1", AutomationDecision.Accept, null, null, "M", "e-r1", 0, "run-1", "h1", 1);
    store.MarkIntegrated("p1", 1, "sha", "e-i2", 0);
    Check(store.IsIntegrated("p1", 1), "integration mark persisted");
}

// -- 9. single-writer lease ---------------------------------------------------------
{
    var store = new AutomationStore(TempDb());
    store.CreateThread("th-1", "proj", "pi", "m", @"C:\wt", null, 0);
    store.CreateTask("task-A", "th-1", "a", "base", 0);
    var l1 = store.TryAcquireLease("l1", WriteLeaseKind.TaskWriter, "task-A", "host-1", 0, 60_000);
    Check(l1 is not null, "first writer acquires lease");
    var l2 = store.TryAcquireLease("l2", WriteLeaseKind.TaskWriter, "task-A", "host-2", 1_000, 60_000);
    Check(l2 is null, "second writer rejected while lease live");
    var l3 = store.TryAcquireLease("l3", WriteLeaseKind.TaskWriter, "task-A", "host-2", 61_001, 60_000);
    Check(l3 is not null, "expired lease can be taken over");
    Throws<InvalidOperationException>(
        () => store.ReleaseLease("l1", "host-1"),
        "superseded lease cannot be released");
    var i1 = store.TryAcquireLease("i1", WriteLeaseKind.Integrator, "proj", "m-1", 0, 60_000);
    var i2 = store.TryAcquireLease("i2", WriteLeaseKind.Integrator, "proj", "m-2", 1, 60_000);
    Check(i1 is not null && i2 is null, "only one managed integrator per project");
}

// -- 9b. fixed-thread single writer across tasks (U1-R2) -------------------------
{
    var store = new AutomationStore(TempDb());
    store.CreateThread("th-1", "proj", "pi", "m", @"C:\wt", "sess-1", 0);
    store.CreateTask("task-A", "th-1", "a", "base", 0);
    store.CreateTask("task-B", "th-1", "b", "base", 0);
    store.CreateThread("th-2", "proj", "pi", "m", @"C:\wt2", "sess-2", 0);
    store.CreateTask("task-C", "th-2", "c", "base", 0);
    var a = store.TryAcquireLease("la", WriteLeaseKind.TaskWriter, "task-A", "host-1", 0, 60_000);
    var b = store.TryAcquireLease("lb", WriteLeaseKind.TaskWriter, "task-B", "host-2", 1, 60_000);
    Check(a is not null && b is null,
        "same-thread tasks share one writer lease");
    var c = store.TryAcquireLease("lc", WriteLeaseKind.TaskWriter, "task-C", "host-2", 2, 60_000);
    Check(c is not null, "distinct thread keeps an independent writer");
}

// -- 12. delivery replay is idempotent (U1-R1) -----------------------------------
{
    var db = TempDb();
    {
        var s = new AutomationStore(db);
        s.CreateThread("th-1", "proj", "pi", "m", @"C:\wt", null, 0);
        s.CreateTask("task-A", "th-1", "a", "base", 0);
        s.CreatePart("p1", "task-A", "one", 0);
        var rev = s.RecordDelivery("r1", "p1", "run-1", "h1", "{}", "e-d1", 0);
        Check(rev.RevisionNumber == 1, "first delivery is revision 1");
    }
    var store = new AutomationStore(db);
    // Replay: same bound identity, a different revisionId must NOT mint a new revision.
    var replay = store.RecordDelivery("r-other", "p1", "run-1", "h1", "{}", "e-d1", 0);
    Check(replay.RevisionNumber == 1 && replay.RevisionId == "r1",
        "replayed delivery returns persisted revision");
    Check(store.ListRevisions("p1").Count == 1, "replay does not add a revision");
    Check(store.GetPart("p1").CurrentRevision == 1, "current revision unchanged by replay");
    Check(store.ListEventsAfter(0).Count == 1, "replay does not duplicate events");
    // Conflicting HEAD for the same run is rejected before mutation.
    Throws<InvalidOperationException>(
        () => store.RecordDelivery("r-x", "p1", "run-1", "h-other", "{}", "e-dx", 0),
        "conflicting HEAD for same run rejected");
    Check(store.ListRevisions("p1").Count == 1, "conflict left no partial mutation");
    // Same identity (part/run/head) with a changed delivery card is a conflict,
    // not a replay — rejected with no mutations.
    Throws<InvalidOperationException>(
        () => store.RecordDelivery("r-y", "p1", "run-1", "h1",
            "{\"different\":\"card\"}", "e-d1", 0),
        "changed card for same identity rejected");
    Check(store.ListRevisions("p1").Count == 1 && store.ListEventsAfter(0).Count == 1,
        "card conflict left no partial mutation");
    // State survives another reopen.
    var s2 = new AutomationStore(db);
    Check(s2.ListRevisions("p1").Count == 1 && s2.ListEventsAfter(0).Count == 1,
        "idempotent state stable across reopen");
}

// -- 13. stale review cannot decide a replacement (U1-R3) -------------------------
{
    var store = new AutomationStore(TempDb());
    store.CreateThread("th-1", "proj", "pi", "m", @"C:\wt", null, 0);
    store.CreateTask("task-A", "th-1", "a", "base", 0);
    store.CreatePart("p1", "task-A", "one", 0);
    store.RecordDelivery("r1", "p1", "run-1", "h1", "{}", "e-d1", 0);
    store.RecordDecision("d1", "p1", AutomationDecision.Rework, "redo", null, "M", "e-r1", 0, "run-1", "h1", 1);
    store.RecordDelivery("r2", "p1", "run-2", "h2", "{}", "e-d2", 0);
    // An old review against revision 1 / run-1 / h1 must not accept revision 2.
    Throws<InvalidOperationException>(
        () => store.RecordDecision("d2", "p1", AutomationDecision.Accept, null, null, "M",
            "e-r2", 0, "run-2", "h2", 1),
        "stale revision number rejected");
    Throws<InvalidOperationException>(
        () => store.RecordDecision("d3", "p1", AutomationDecision.Accept, null, null, "M",
            "e-r3", 0, "run-1", "h2", 2),
        "stale runId rejected");
    Throws<InvalidOperationException>(
        () => store.RecordDecision("d4", "p1", AutomationDecision.Accept, null, null, "M",
            "e-r4", 0, "run-2", "h1", 2),
        "stale HEAD rejected");
    Check(store.GetPart("p1").Status == DeliveryPartStatus.Delivered,
        "rejected stale reviews did not change part state");
    // An unbound review (the original probe: no expected identity at all) can no
    // longer even compile-skip validation: null/empty identity is rejected up
    // front, and there is no overload that omits identity.
    Throws<ArgumentException>(
        () => store.RecordDecision("d6", "p1", AutomationDecision.Accept, null, null, "M",
            "e-r6", 0, null!, "h2", 2),
        "absent runId rejected");
    Throws<ArgumentException>(
        () => store.RecordDecision("d7", "p1", AutomationDecision.Accept, null, null, "M",
            "e-r7", 0, "run-2", "", 2),
        "absent HEAD rejected");
    var ok = store.RecordDecision("d5", "p1", AutomationDecision.Accept, null, null, "M",
        "e-r5", 0, "run-2", "h2", 2);
    Check(ok.RevisionNumber == 2 && store.GetPart("p1").Status == DeliveryPartStatus.Accepted,
        "matching identity accepts the live revision");
}

// -- 10. crash recovery --------------------------------------------------------------
{
    var db = TempDb();
    {
        var store = new AutomationStore(db);
        store.CreateThread("th-1", "proj", "pi", "m", @"C:\wt", null, 0);
        store.CreateTask("task-A", "th-1", "a", "base", 0);
        store.CreatePart("p1", "task-A", "one", 0);
        store.CreateClaim("c1", "task-A", "p1", "run-1", 0);
    } // simulate host death: store dropped with a live claim
    var recovered = new AutomationStore(db);
    var report = recovered.RecoverAfterCrash(0);
    Check(report.UnknownClaims.Contains("c1"), "crash recovery marks live claim unknown");
    Check(report.FrozenThreads.Contains("th-1"), "crash recovery freezes owning thread");
    Check(recovered.GetThread("th-1").Status == ThreadStatus.Frozen,
        "frozen state survives reopen");
    // reopen again: identity and state rebuilt from disk
    var reopened = new AutomationStore(db);
    Check(reopened.ListUnresolvedClaims().Single().ClaimId == "c1",
        "identity rebuilt after reopen");
}

// -- 11. reopened store keeps immutable history ---------------------------------------
{
    var db = TempDb();
    {
        var s = new AutomationStore(db);
        s.CreateThread("th-1", "proj", "pi", "m", @"C:\wt", null, 0);
        s.CreateTask("task-A", "th-1", "a", "base", 0);
        s.CreatePart("p1", "task-A", "one", 0);
        s.RecordDelivery("r1", "p1", "run-1", "h1", "{}", "e-d1", 0);
        s.RecordDecision("d1", "p1", AutomationDecision.Accept, null, null, "M", "e-r1", 0, "run-1", "h1", 1);
    }
    var s2 = new AutomationStore(db);
    Check(s2.GetPart("p1").Status == DeliveryPartStatus.Accepted,
        "accepted state survives reopen");
    Throws<InvalidOperationException>(
        () => s2.RecordDecision("d9", "p1", AutomationDecision.Rework, "x", null, "M", "e-r9", 0, "run-1", "h1", 1),
        "reopened store still refuses to rewrite history");
}

Console.WriteLine(failures.Count == 0 ? "ALL PASS" : $"{failures.Count} FAILURES");
return failures.Count;
