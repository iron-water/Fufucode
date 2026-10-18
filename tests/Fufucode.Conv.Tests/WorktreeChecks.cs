using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;

namespace Fufucode.Conv.Tests;

/// <summary>
/// CONV-1 worktree lifecycle: intent is persisted before anything is created, creation is a real
/// `git worktree add`, the real HEAD and branch are verified, an unknown result is never retried,
/// and releasing ownership never deletes the user's tree.
/// </summary>
internal static class WorktreeChecks
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        var scenario = ConvFixture.New(root, "worktree");
        GitInit(scenario.Worktree, out var baseline);
        using var store = new AutomationStore(scenario.Database);
        var manager = new WorktreeLifecycleManager(store, GitCliEvidence.Shared, scenario.WorktreeRoot);
        var branch = "conv/wt-1";
        var path = Path.Combine(scenario.WorktreeRoot, "wt-1");

        ConvWorktreeIntent Intent(string id, string branchName = "conv/wt-1", string? target = null,
            string baseSha = "") => new(id, "project", scenario.Worktree, baseSha.Length == 0 ? baseline : baseSha,
                branchName, target ?? path, ConvFixture.Now());

        void Refused(Action action, string label)
        {
            try { action(); check(false, label + " (accepted)"); }
            catch (Exception) { check(true, label); }
        }

        var declared = manager.DeclareIntent(Intent("wt-1"), ConvFixture.Now());
        check(declared.State == ConvWorktreeState.Intent && declared.Path == Path.GetFullPath(path),
            "worktree: the intent is persisted before anything is created");
        check(manager.GetWorktree("wt-1").BaseSha == baseline && !Directory.Exists(path),
            "worktree: declaring an intent creates nothing on disk");
        check(store.ListEventsAfter(0).Any(e => e.Type == "worktree.intent"),
            "worktree: the intent is recorded as a durable event");
        check(manager.DeclareIntent(Intent("wt-1"), ConvFixture.Now()).State == ConvWorktreeState.Intent,
            "worktree: re-declaring the same intent is idempotent");
        Refused(() => manager.DeclareIntent(Intent("wt-1", branchName: "conv/other"), ConvFixture.Now()),
            "worktree: re-declaring the same id with different content is refused");
        Refused(() => manager.DeclareIntent(Intent("wt-2", target: Path.Combine(scenario.WorktreeRoot, "..", "escape")), ConvFixture.Now()),
            "worktree: a path outside the managed root is refused");
        Refused(() => manager.DeclareIntent(Intent("wt-3", target: path), ConvFixture.Now()),
            "worktree: a second identity cannot claim an existing path");
        Refused(() => manager.DeclareIntent(Intent("wt-4", target: Path.Combine(scenario.WorktreeRoot, "wt-4"), baseSha: "abc1234"), ConvFixture.Now()),
            "worktree: an abbreviated base SHA is refused");
        Refused(() => manager.DeclareIntent(Intent("wt-5", target: Path.Combine(scenario.WorktreeRoot, "wt-5"), baseSha: new string('a', 40)), ConvFixture.Now()),
            "worktree: a base SHA that is not in the repository is refused");
        check(manager.ListWorktrees().Count == 1 && manager.ListWorktrees("project").Count == 1,
            "worktree: only the one accepted intent is listed");

        var created = await manager.CreateAsync("wt-1", ConvFixture.Now());
        check(created.State == ConvWorktreeState.Created,
            "worktree: creation reports a created state");
        check(created.VerifiedHead == baseline, "worktree: the real HEAD is the declared base SHA");
        check(Directory.Exists(path), "worktree: the checkout really exists on disk");
        check(ConvFixture.Git(path, ["rev-parse", "--abbrev-ref", "HEAD"]) == branch,
            "worktree: the real branch is the declared branch");
        check(File.ReadAllText(Path.Combine(path, "tracked.txt")) == "initial\n",
            "worktree: the new checkout carries the baseline content");
        var listed = ConvFixture.Git(scenario.Worktree, ["worktree", "list", "--porcelain"]);
        check(listed.Contains("wt-1"), "worktree: git itself lists the managed checkout");
        var listCount = listed.Split('\n').Count(line => line.StartsWith("worktree ", StringComparison.Ordinal));
        var eventsAfterCreate = store.ListEventsAfter(0).Count(e => e.Type == "worktree.created");

        var again = await manager.CreateAsync("wt-1", ConvFixture.Now());
        check(again.WorktreeId == "wt-1" && again.VerifiedHead == baseline,
            "worktree: creating an already created tree returns the record instead of a second checkout");
        var listedAgain = ConvFixture.Git(scenario.Worktree, ["worktree", "list", "--porcelain"]);
        check(listedAgain.Split('\n').Count(line => line.StartsWith("worktree ", StringComparison.Ordinal)) == listCount,
            "worktree: the repeat created no additional worktree");
        check(store.ListEventsAfter(0).Count(e => e.Type == "worktree.created") == eventsAfterCreate,
            "worktree: the repeat wrote no additional created event");

        var reconcile = await manager.ReconcileAsync("wt-1");
        check(reconcile.ObservedHead == baseline && reconcile.TrackedClean == true &&
              reconcile.Verdict is "created" or "verified",
            "worktree: reconciliation reads the real HEAD and clean state");
        ConvFixture.GitCommit(path, "advance inside the managed checkout");
        var moved = await manager.ReconcileAsync("wt-1");
        check(moved.Verdict == "head_mismatch" && moved.ObservedHead != baseline,
            "worktree: a moved checkout is reported as a mismatch instead of a success");

        manager.ReleaseOwnership("wt-1", ConvFixture.Now(), "module fixture release");
        var released = manager.GetWorktree("wt-1");
        check(released.State == ConvWorktreeState.Released && released.ReleasedUtc is > 0,
            "worktree: releasing records the release durably");
        check(store.ListEventsAfter(0).Any(e => e.Type == "worktree.released"),
            "worktree: the release is recorded as a durable event");
        check(Directory.Exists(path) && File.Exists(Path.Combine(path, "tracked.txt")),
            "worktree: releasing ownership deletes nothing");
        var afterRelease = await manager.CreateAsync("wt-1", ConvFixture.Now());
        check(afterRelease.State == ConvWorktreeState.Released && Directory.Exists(path),
            "worktree: a released tree is never re-created or removed");
        var finalList = ConvFixture.Git(scenario.Worktree, ["worktree", "list", "--porcelain"]);
        check(finalList.Split('\n').Count(line => line.StartsWith("worktree ", StringComparison.Ordinal)) == listCount,
            "worktree: the whole scenario left exactly one managed checkout");
    }

    private static void GitInit(string worktree, out string baseline)
    {
        baseline = ConvFixture.GitInit(worktree);
    }
}
