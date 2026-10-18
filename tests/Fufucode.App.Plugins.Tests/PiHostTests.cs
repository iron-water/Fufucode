using System.Text.Json;
using Fufucode.App.Features.Plugins;
using Fufucode.App.Integration;
using Fufucode.Core;

namespace Fufucode.App.Plugins.Tests;

internal static class PiHostTests
{
    public static async Task<int> RunAsync(string node, string cli)
    {
        var count = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); ++count; Console.WriteLine("PASS " + name); }
        var root = Path.Combine(Path.GetTempPath(), "fufucode-pi-host-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var layout = new AppDataLayout(Path.Combine(root, "data")); layout.EnsureCreated();
        File.WriteAllText(Path.Combine(layout.AgentDirectory, "models.json"), JsonSerializer.Serialize(new
        {
            providers = new Dictionary<string, object> { ["proof"] = new
            {
                baseUrl = "http://127.0.0.1:9/v1", apiKey = "offline", api = "openai-completions",
                models = new[] { new { id = "devin/swe-2", name = "offline", reasoning = false, input = new[] { "text" },
                    contextWindow = 10000, maxTokens = 1000, cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 } } },
            } },
        }));
        new AppSettingsStore(layout).Update(settings => settings with { SelectedModel = "proof/devin/swe-2" });
        var options = new AppOptions(AppSessionMode.Real, project, null, node, cli, layout.Root, BackendId.Pi);
        RealSessionHost? current = await RealSessionHost.OpenAsync(options, CancellationToken.None, layout);
        var original = current;
        try
        {
            var adapter = new PiLocalPluginHost(layout, () => current, () => cli, () => false,
                (expected, paths, ct) => expected.ApplyExtensionsAndRestartAsync(paths, ct));
            var service = new LocalPluginService(layout, adapter);
            var sessionId = current.Current.Session.Id;
            current.SaveDraft("keep original draft 中文");
            var first = PluginFixture.Create(root, "v1", "host.plugin", "Host plugin", "1.0",
                entryBody: "export default async function(pi) { await Promise.resolve(); }\n");
            var second = PluginFixture.Create(root, "v2", "host.plugin", "Host plugin", "2.0",
                entryBody: "export default function(pi) {}\n");
            var bad = PluginFixture.Create(root, "vbad", "host.plugin", "Host plugin", "3.0",
                entryBody: "export default function(pi) { throw new Error('host-candidate-failed'); }\n");
            var a = await service.ImportAsync(new LocalPluginImportRequest(first));
            var b = await service.ImportAsync(new LocalPluginImportRequest(second));
            var c = await service.ImportAsync(new LocalPluginImportRequest(bad));
            var empty = await adapter.SnapshotAsync();
            var modelState = await current.Current.Coordinator.QueryStateAsync();
            Check(modelState.Success && modelState.Data!.Value.GetProperty("model").GetProperty("id").GetString() == "devin/swe-2",
                "real host uses the explicitly selected cheapest probe model without sending a prompt");
            Check(empty.Success && empty.LoadedExtensionPaths.Count == 0 && adapter.Identity.Version == "1.1.0",
                "real empty Pi snapshot and actual precise runtime identity are available");
            using (var vm = new LocalPluginViewModel(service))
            {
                await vm.RefreshCommand.ExecuteAsync(null);
                Check(vm.HasHost && vm.CanApply && vm.ApplyButtonText == "安全重启并应用",
                    "actual desktop view model exposes only the explicit safe-restart label for a live Pi host");
            }
            var applied = await service.ApplyVersionWithRestartAsync("host.plugin", a.VersionId);
            var listed = (await service.ListAsync()).Items.Single();
            Check(applied.Success && listed.IsActive && listed.SelectedVersionId == a.VersionId,
                "first explicit safe restart confirms the original fixed entry in the real process");
            Check(current.Current.Session.Id == sessionId && current.LoadDraft() == "keep original draft 中文",
                "safe apply retains original session identity and persisted draft");
            var before = current.Current.Coordinator.Snapshot.Generation;
            var ordinary = await service.ApplyVersionAsync("host.plugin", b.VersionId);
            Check(ordinary.IsPending && current.Current.Coordinator.Snapshot.Generation == before,
                "ordinary apply on an active version only records pending and never restarts");
            var update = await service.ApplyVersionWithRestartAsync("host.plugin", b.VersionId);
            listed = (await service.ListAsync()).Items.Single();
            Check(update.Success && listed.IsActive && listed.SelectedVersionId == b.VersionId && listed.PreviousVersionId == a.VersionId,
                "explicit safe update switches actual loaded entry and preserves previous version");
            var failed = await service.ApplyVersionWithRestartAsync("host.plugin", c.VersionId);
            listed = (await service.ListAsync()).Items.Single();
            Check(!failed.Success && listed.IsActive && listed.SelectedVersionId == b.VersionId && current.HasLiveSession,
                "a bad candidate factory fails and real prior version/session is restored");
            var rollback = await service.RollbackWithRestartAsync("host.plugin");
            listed = (await service.ListAsync()).Items.Single();
            Check(rollback.Success && listed.IsActive && listed.SelectedVersionId == a.VersionId,
                "explicit rollback restores the real old fixed entry through the same restart chain");
            var blockedRemoval = await service.RemoveVersionAsync("host.plugin", a.VersionId);
            Check(blockedRemoval.IsPending && (await service.ListAsync()).Items.Single().Versions.Count == 3,
                "removing the loaded version preserves its actual fixed package");
            var snapshot = await adapter.SnapshotAsync();
            var originalProof = PiPluginLoadProof.Open(layout, current.ActiveExtensionPaths.Single());
            Check(originalProof.PluginId == "host.plugin" && originalProof.Version == "1.0",
                "current persisted loader metadata reopens its exact plugin/version identity");
            var enabledBeforeBlocked = current.EnabledExtensionPaths.ToArray();
            using (var maintenance = current.Current.Coordinator.BeginIdleMaintenance())
            {
                var blocked = await service.ApplyVersionWithRestartAsync("host.plugin", b.VersionId);
                Check(!blocked.Success && current.EnabledExtensionPaths.SequenceEqual(enabledBeforeBlocked),
                    "a held maintenance reservation rejects safe apply before changing enabled paths");
            }
            current = null;
            var switched = await adapter.ApplyWithRestartAsync(listed.Versions.First(v => v.VersionId == b.VersionId).EntryRelativePath,
                "host.plugin", "2.0", snapshot);
            current = original;
            Check(!switched.Success && current.HasLiveSession, "stale project/host identity cannot restart a different session");

            using (var maintenance = current.Current.Coordinator.BeginIdleMaintenance())
            {
                Check(!current.Current.Coordinator.CanSendPrompt, "real idle maintenance blocks all new prompt submissions");
                var refused = false;
                try { await current.Current.Coordinator.SendPromptAsync("must not reach model", PromptDelivery.FollowUp); }
                catch (InvalidOperationException) { refused = true; }
                Check(refused, "follow-up is rejected before RPC while maintenance owns the idle connection");
                var nested = false;
                try { current.Current.Coordinator.BeginIdleMaintenance(); } catch (InvalidOperationException) { nested = true; }
                Check(nested, "second maintenance reservation is rejected without a competing writer");
            }
            Check(current.Current.Coordinator.CanSendPrompt, "ending maintenance restores ordinary prompt availability");
            Check(current.Current.Session.Id == sessionId && current.LoadDraft() == "keep original draft 中文",
                "failed candidate and rollback still preserve the original session and draft");
            var damaged = current.ActiveExtensionPaths.Single() + ".json";
            var savedMetadata = File.ReadAllBytes(damaged);
            File.WriteAllText(damaged, "{broken");
            var unknown = await adapter.SnapshotAsync();
            Check(!unknown.Success && unknown.LoadedExtensionPaths.Count == 0,
                "corrupt persisted loader identity is never a successful empty loaded snapshot");
            File.WriteAllBytes(damaged, savedMetadata);
        }
        finally
        {
            await original.DisposeAsync();
            Console.WriteLine("real host evidence root: " + root);
        }
        Console.WriteLine($"HOST ALL PASS ({count})");
        return 0;
    }
}
