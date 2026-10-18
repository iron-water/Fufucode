using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Fufucode.Core.Automation;
using Fufucode.App.Abstractions.Automation.Harnesses;
using Fufucode.Core.Automation.Harnesses;
using Microsoft.Data.Sqlite;

namespace Fufucode.App.SelfTest;

internal static partial class AutomationControlSelfTest
{
    private static async Task CheckHeldContractMutationsAsync(AutomationStore live, string root, Action<bool, string> check)
    {
        // Each real SQLite backup isolates a destructive attempt while the original extra writer is actually held.
        foreach (var operation in new[] { "define", "adopt", "restructure" })
        {
            var path = Path.Combine(root, "held-mutation-" + operation + ".db");
            await BackupControlFixtureAsync(live.DatabasePath, path);
            using var store = AutomationStore.AttachControlClient(path);
            check(new HarnessRunStore(live).Get("held-a").Status == HarnessRunStatus.Running &&
                new HarnessRunStore(store).Get("held-a").Status == HarnessRunStatus.Running,
                "backup preserves the genuinely running held writer: " + operation);
            long Rev() => store.ReadControlSnapshot().Revision;
            long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var package = store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == "a");
            var refused = false;
            Action action;
            if (operation == "define") action = () => store.ApplyControlCommand(new("held-define", Rev(), "fixture", ControlAction.DefinePackage,
                package.Spec with { Goal = "Changed while another writer still runs" }, ExpectedPackageVersion: package.Version), Now());
            else if (operation == "adopt")
            {
                store.ApplyControlKnowledge(new("held-proposal", Rev(), "fixture", ControlKnowledgeAction.Propose, Key: "held-policy", Text: "New policy", Source: "fixture", ExpectedKnowledgeVersion: 0), Now());
                store.ApplyControlKnowledge(new("held-publish", Rev(), "fixture", ControlKnowledgeAction.Publish, ProposalId: "held-proposal", ExpectedKnowledgeVersion: 0), Now());
                action = () => store.ApplyControlKnowledge(new("held-adopt", Rev(), "fixture", ControlKnowledgeAction.Adopt, Key: "held-policy", ExpectedKnowledgeVersion: 1)
                    { PackageId = "a", ExpectedPackageVersion = package.Version }, Now());
            }
            else
            {
                var group = store.ReadControlGroups().Single(g => g.Spec.Id == "competition").Spec;
                var nextGroup = group with { Id = "held-group", Members = ["held-next", "b"] };
                var spec = new ControlRestructureSpec(new Dictionary<string, long> { ["a"] = package.Version }, [package.Spec with { Id = "held-next" }],
                    [new("a", ["held-next"], "Preserve original goal")], [nextGroup], [new(group.Id, [nextGroup.Id], "Preserve competition")], "Explicit fixture successor");
                store.ApplyControlRestructure(new("held-restructure", Rev(), "fixture", ControlRestructureAction.Propose, spec), Now());
                action = () => store.ApplyControlRestructure(new("held-approve", Rev(), "fixture", ControlRestructureAction.Approve, ProposalId: "held-restructure"), Now());
            }
            try { action(); } catch (InvalidOperationException) { refused = true; }
            check(refused && store.ReadControlSnapshot().Packages.Single(p => p.Spec.Id == "a").Version == package.Version,
                "actual unbound held writer blocks contract mutation transactionally: " + operation);
        }
    }

    private static async Task BackupControlFixtureAsync(string sourcePath, string targetPath)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (true)
        {
            try
            {
                await Task.Run(() =>
                {
                    using var source = new SqliteConnection(new SqliteConnectionStringBuilder
                        { DataSource = sourcePath, Pooling = false, DefaultTimeout = 1 }.ToString());
                    using var target = new SqliteConnection(new SqliteConnectionStringBuilder
                        { DataSource = targetPath, Pooling = false, DefaultTimeout = 1 }.ToString());
                    source.Open(); target.Open(); source.BackupDatabase(target);
                });
                return;
            }
            catch (SqliteException ex) when ((ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6) && DateTime.UtcNow < deadline)
            {
                // Let actual UI observation transactions finish; never clear or fabricate writer state.
                await Task.Delay(40);
            }
        }
    }
}
