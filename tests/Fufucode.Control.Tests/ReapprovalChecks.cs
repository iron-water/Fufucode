using Fufucode.Core.Automation;

internal static class ReapprovalChecks
{
    internal static void Run(string root, Action<bool, string> check)
    {
        using var store = new AutomationStore(Path.Combine(root, "reapproval.db"));
        var sequence = 0;
        string Id() => "reapproval-" + ++sequence;
        long Revision() => store.ReadControlSnapshot().Revision;
        WorkPackageSpec Spec(string id, string[] deps) => new(id, id, "Versioned contract fixture",
            ["owned/**"], ["Verify contract version"], "codex", "devin/swe-2", "code", deps);
        foreach (var (id, deps) in new[] { ("Parent", Array.Empty<string>()), ("Child", new[] { "Parent" }), ("Leaf", new[] { "Child" }) })
        {
            store.ApplyControlCommand(new(Id(), Revision(), "human-fixture", ControlAction.DefinePackage,
                Package: Spec(id, deps), ExpectedPackageVersion: 0), 1);
            store.ApplyControlCommand(new(Id(), Revision(), "human-fixture", ControlAction.ApprovePackage,
                PackageId: id, ExpectedPackageVersion: 1), 1);
        }
        store.ApplyControlCommand(new(Id(), Revision(), "human-fixture", ControlAction.SetMode,
            Mode: DevelopmentMode.SemiAutomatic), 2);
        store.ApplyControlCommand(new(Id(), Revision(), "human-fixture", ControlAction.DefinePackage,
            Package: Spec("Parent", []) with { Goal = "Changed upstream contract" }, ExpectedPackageVersion: 1), 3);
        // Behavior, not an internal table assertion: approval of the refreshed plans must be usable again.
        foreach (var package in store.ReadControlSnapshot().Packages)
            store.ApplyControlCommand(new(Id(), Revision(), "human-fixture", ControlAction.ApprovePackage,
                PackageId: package.Spec.Id, ExpectedPackageVersion: package.Version), 4);
        var current = store.ReadControlSnapshot();
        check(current.Packages.All(p => p.Approved) && current.Intents.Count(i => i.Status == "awaiting_binding") == 3,
            "reapproval after an upstream change yields usable new intents for all dependents");
        check(current.Packages.All(p => p.Version == 2) && current.Intents.Count(i => i.Status == "superseded") == 3,
            "changed transitive contracts get fresh identities while superseded intents stay historical");
        check(store.GetWorkPackageVersion("Child", 1).Spec.DependsOn.SequenceEqual(["Parent"]),
            "dependent contract renewal preserves the old specification instead of recycling its execution identity");
    }
}
