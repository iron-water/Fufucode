using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Fufucode.App.Features.Plugins;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

internal static class LocalPluginReconciliationSelfTest
{
    internal static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool ok, string name)
        { if (ok) pass("Plugin reconciliation: " + name); else fail("Plugin reconciliation: " + name); }
        var root = Path.Combine(Path.GetTempPath(), "fufucode-plugin-reconcile-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "index.js"), "export default () => {}; // fixture never executed\n");
        File.WriteAllText(Path.Combine(source, "fufucode-plugin.json"), JsonSerializer.Serialize(new
        { id = "reconcile.plugin", displayName = "登记对账", version = "1.0", backend = "pi", entryRelativePath = "index.js", supportedHostVersions = new[] { "1.1.0" } }));
        var layout = new AppDataLayout(Path.Combine(root, "data"));
        var host = new TestHost(); var service = new LocalPluginService(layout, host);
        var version = await service.ImportAsync(new LocalPluginImportRequest(source));
        SessionLease? catalogLock = null;
        var activeRefused = false;
        service.CallbackRecordedForTesting = () =>
        {
            var pending = PluginCatalogStore.Load(service.Root).State!;
            var original = pending.Plugins.Single().InFlight.Single();
            var result = service.ReconcileReturnedRequestAsync("reconcile.plugin", original.Token, pending.Generation).GetAwaiter().GetResult();
            activeRefused = !result.Success;
            catalogLock = SessionLease.Acquire(service.Root, "plugins-local-catalog");
        };
        var commitFailed = false;
        try { await service.ApplyVersionAsync("reconcile.plugin", version.VersionId); }
        catch (InvalidOperationException) { commitFailed = true; }
        finally { catalogLock?.Dispose(); service.CallbackRecordedForTesting = null; }
        var state = PluginCatalogStore.Load(service.Root).State!;
        var plugin = state.Plugins.Single(); var request = plugin.InFlight.Single();
        var catalogPath = Path.Combine(service.Root, PluginCatalogStore.CatalogFileName);
        var journalPath = PluginCallbackJournal.PathFor(service.Root, request.Token);
        var catalogBytes = File.ReadAllBytes(catalogPath); var journalBytes = File.ReadAllBytes(journalPath);
        var completion = PluginCallbackJournal.Read(service.Root, plugin.Id, request).Completion!;
        Check(commitFailed && activeRefused && host.ApplyCalls == 1 && plugin.OperationToken == request.Token,
            "real OS catalog lock strands a returned registration; live original completion holder prevents reconciliation");
        Check(completion.RequestLeaseVersion == 1 && completion.OriginalContext == host.Original &&
            completion.OriginalContext.ProjectPath.Contains('\n') && completion.OriginalContext.SessionId.Contains('\n'),
            "structured opaque project/session bytes survive persistence without splitting context display text");

        async Task Refuse(string label)
        {
            var before = File.ReadAllBytes(catalogPath);
            var result = await service.ReconcileReturnedRequestAsync(plugin.Id, request.Token, state.Generation);
            Check(!result.Success && before.AsSpan().SequenceEqual(File.ReadAllBytes(catalogPath)) && host.ApplyCalls == 1,
                label + ": catalog, registration and callback count unchanged");
        }
        foreach (var altered in new[]
        {
            completion with { OriginalContext = null }, completion with { RequestLeaseVersion = 0 },
            completion with { OriginalContext = host.Original with { Generation = Guid.NewGuid() } },
            completion with { OriginalProcessExit = null }, completion with { Outcome = "cancelled" },
            completion with { Outcome = "threw" }, completion with { HostIdentity = HostIdentity.Unknown() },
            completion with { Token = "different-token" }, completion with { StartedAtUtc = request.StartedAtUtc.AddTicks(1) },
        })
        {
            File.WriteAllText(journalPath, JsonSerializer.Serialize(altered));
            var bytes = File.ReadAllBytes(journalPath);
            await Refuse("missing/legacy/unknown or mismatched original evidence " + altered.Outcome);
            Check(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(journalPath)), "refusal does not repair original evidence bytes");
        }
        File.WriteAllBytes(journalPath, journalBytes);
        host.Loaded = [Path.Combine(service.Root, plugin.StorageKey, version.DirectoryName)];
        await Refuse("still-loaded candidate directory"); host.Loaded = [];
        host.Scope = host.Original; await Refuse("original exited generation cannot be the current live connection");
        host.Scope = host.Current with { SessionId = "other-session" }; await Refuse("different session");
        host.Scope = host.Current with { ProjectPath = "other-project" }; await Refuse("different project");
        host.Scope = host.Current; host.IdentityValue = HostIdentity.Pi("other-version"); await Refuse("different host identity");
        host.IdentityValue = HostIdentity.Pi("1.1.0"); host.Busy = true; await Refuse("busy host cannot reserve maintenance"); host.Busy = false;
        host.LeaseCurrent = false; await Refuse("closed or changed host lease"); host.LeaseCurrent = true;
        var entry = Path.Combine(service.Root, plugin.StorageKey, version.DirectoryName, version.EntryRelativePath);
        var entryBytes = File.ReadAllBytes(entry); File.AppendAllText(entry, "tampered");
        await Refuse("altered candidate copy"); File.WriteAllBytes(entry, entryBytes);
        using (SessionLease.Acquire(service.Root, LocalPluginService.RequestLeaseKey(request.Token)))
            await Refuse("another actual holder owns the exact original request");
        using (SessionLease.Acquire(service.Root, "plugins-local-catalog"))
            await Refuse("catalog already owned by another writer");
        var cancelled = new CancellationToken(true); var cancellationSeen = false;
        try { await service.ReconcileReturnedRequestAsync(plugin.Id, request.Token, state.Generation, cancelled); }
        catch (OperationCanceledException) { cancellationSeen = true; }
        Check(cancellationSeen && catalogBytes.AsSpan().SequenceEqual(File.ReadAllBytes(catalogPath)), "pre-cancellation preserves all original state");
        host.OnAcquire = () => PluginCatalogStore.Save(service.Root, state with { Generation = state.Generation + 1 });
        var stale = await service.ReconcileReturnedRequestAsync(plugin.Id, request.Token, state.Generation);
        Check(!stale.Success && PluginCatalogStore.Load(service.Root).State!.Plugins.Single().InFlight.Single() == request && host.ActiveLeases == 0,
            "catalog CAS rejects a post-snapshot writer and releases the reserved host gate");
        host.OnAcquire = null; File.WriteAllBytes(catalogPath, catalogBytes);
        // A different request and new pending decision must survive exact-token reconciliation.
        var other = request with { Token = "opaque other request", StartedAtUtc = request.StartedAtUtc.AddSeconds(1) };
        var newReceipt = new ApplyReceipt { Token = other.Token, VersionId = version.VersionId, Outcome = "cancelled_unknown", AtUtc = DateTimeOffset.UtcNow };
        var withNewer = state with { Plugins = [plugin with { InFlight = [request, other], OperationToken = other.Token,
            PendingRollback = true, LastApplyReceipt = newReceipt, RecentReceipts = [newReceipt] }] };
        PluginCatalogStore.Save(service.Root, withNewer);
        using var vm = new LocalPluginViewModel(service);
        var section = new LocalPluginSectionView(); section.Attach(vm);
        await vm.RefreshCommand.ExecuteAsync(null);
        Check(!vm.ConfirmRegistrationEnd && !vm.ReconcileInFlightCommand.CanExecute(null), "new page requires explicit original inspection and confirmation");
        await vm.InspectInFlightCommand.ExecuteAsync(null);
        vm.ConfirmRegistrationEnd = true;
        var button = section.FindControl<Button>("ReconcileInFlightButton")!;
        Check(button.IsEnabled && vm.ReconcileInFlightCommand.CanExecute(null) &&
            section.FindControl<CheckBox>("ConfirmRegistrationEnd")!.IsChecked == true,
            "actual compiled confirmation and action bindings enable a selected inspected request");
        vm.SelectedInFlight = vm.InFlightRequests.Last();
        Check(!vm.ConfirmRegistrationEnd && !vm.ReconcileInFlightCommand.CanExecute(null), "changing original request resets confirmation and inspection");
        vm.SelectedInFlight = vm.InFlightRequests.First(); await vm.InspectInFlightCommand.ExecuteAsync(null);
        var beforeLate = File.ReadAllBytes(catalogPath);
        using (var closingVm = new LocalPluginViewModel(service))
        {
            await closingVm.RefreshCommand.ExecuteAsync(null); await closingVm.InspectInFlightCommand.ExecuteAsync(null);
            closingVm.ConfirmRegistrationEnd = true;
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.SnapshotBarrier = waiting.Task;
            var action = closingVm.ReconcileInFlightCommand.ExecuteAsync(null);
            closingVm.Dispose(); await action;
            host.SnapshotBarrier = null; waiting.TrySetResult();
            Check(beforeLate.AsSpan().SequenceEqual(File.ReadAllBytes(catalogPath)) && host.ActiveLeases == 0,
                "closing a page during its snapshot cancels reconciliation before metadata commit and releases the original holder");
        }
        var delayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SnapshotBarrier = delayed.Task; vm.ConfirmRegistrationEnd = true;
        var lateAction = vm.ReconcileInFlightCommand.ExecuteAsync(null);
        Check(vm.IsBusy && !vm.ReconcileInFlightCommand.CanExecute(null), "pending reconciliation disables duplicate UI action");
        vm.SelectedInFlight = vm.InFlightRequests.Last(); var newSelection = vm.SelectedInFlight;
        var newStatus = vm.StatusText;
        host.SnapshotBarrier = null; delayed.TrySetResult(); await lateAction;
        Check(vm.SelectedInFlight == newSelection && vm.StatusText == newStatus && vm.InFlightObservationText.Length == 0,
            "late original result cannot replace a different selected request or its UI explanation");
        Check(PluginCatalogStore.Load(service.Root).State!.Plugins.Single().InFlight.Single() == other,
            "late explicit action commits only its originally authorized token, never the new selection");
        // Restore the isolated fixture branch to exercise an ordinary visible completion as well.
        File.WriteAllBytes(catalogPath, beforeLate); await vm.RefreshCommand.ExecuteAsync(null);
        await vm.InspectInFlightCommand.ExecuteAsync(null);
        vm.ConfirmRegistrationEnd = true; await vm.ReconcileInFlightCommand.ExecuteAsync(null);
        var ended = PluginCatalogStore.Load(service.Root).State!.Plugins.Single();
        Check(ended.InFlight.Single() == other && ended.OperationToken == other.Token && ended.PendingApplyVersionId == plugin.PendingApplyVersionId &&
            ended.PendingRollback && ended.LastApplyReceipt == newReceipt && ended.RecentReceipts.Single() == newReceipt &&
            ended.SelectedVersionId == plugin.SelectedVersionId && ended.PreviousVersionId == plugin.PreviousVersionId,
            "explicit action removes only its token and preserves newer decision, unknown receipt and version pointers");
        Check(ended.Reconciliations.Single().Request == request && ended.Reconciliations.Single().OriginalReturn == completion &&
            journalBytes.AsSpan().SequenceEqual(File.ReadAllBytes(journalPath)) && entryBytes.AsSpan().SequenceEqual(File.ReadAllBytes(entry)) && host.ApplyCalls == 1,
            "separate durable reconciliation history preserves immutable return and candidate files without replay");
        Check(vm.HasReconciliationHistory && vm.ReconciliationHistoryText.Contains(request.Token) &&
            vm.ReconciliationHistoryText.Contains("未认证应用成功或外部任务终止") && !vm.ConfirmRegistrationEnd,
            "refreshed actual page keeps the ended original identity and bounded explanation");
        var after = File.ReadAllBytes(catalogPath);
        var duplicate = await service.ReconcileReturnedRequestAsync(plugin.Id, request.Token, state.Generation + 1);
        Check(!duplicate.Success && after.AsSpan().SequenceEqual(File.ReadAllBytes(catalogPath)), "duplicate action neither repeats nor replaces another request");
        Check(host.ActiveLeases == 0, "all successful and refused paths release host maintenance reservations");
        section.Detach();
        Check(!vm.ReconcileInFlightCommand.CanExecute(null), "detached/disposed page cannot reconcile a historical selection");
        File.WriteAllBytes(catalogPath, catalogBytes);
        var failureReturn = completion with { Outcome = "returned_failure" };
        File.WriteAllText(journalPath, JsonSerializer.Serialize(failureReturn));
        var failedRequestEnd = await service.ReconcileReturnedRequestAsync(plugin.Id, request.Token, state.Generation);
        var endedOwn = PluginCatalogStore.Load(service.Root).State!.Plugins.Single();
        Check(failedRequestEnd.Success && endedOwn.OperationToken is null && endedOwn.PendingApplyVersionId is null &&
            !endedOwn.PendingRollback && endedOwn.InFlight.Count == 0 && endedOwn.LastApplyReceipt is null &&
            endedOwn.Reconciliations.Single().OriginalReturn.Outcome == "returned_failure",
            "explicit failed-return ending clears only its own pending registration without inventing a successful receipt");
        var reopened = new LocalPluginService(layout);
        using var offline = new LocalPluginViewModel(reopened); await offline.RefreshCommand.ExecuteAsync(null);
        Check(offline.HasReconciliationHistory && offline.ReconciliationHistoryText.Contains("调用返回失败") &&
            !offline.HasInFlightRequests && !offline.ReconcileInFlightCommand.CanExecute(null),
            "reopened offline page preserves separate failed-return history and cannot repeat the ended request");
        Console.WriteLine("reconciliation evidence root: " + root);
    }

    private sealed class TestHost : ILocalPluginReconciliationHost
    {
        public PluginHostContext Original { get; } = new("fixture\nproject", "opaque\nsession", Guid.NewGuid());
        public PluginHostContext Current { get; } = new("fixture\nproject", "opaque\nsession", Guid.NewGuid());
        public PluginHostContext? Scope { get; set; }
        public HostIdentity IdentityValue { get; set; } = HostIdentity.Pi("1.1.0");
        public HostIdentity Identity => IdentityValue;
        public string[] Loaded { get; set; } = [];
        public bool Busy { get; set; }
        public bool LeaseCurrent { get; set; } = true;
        public Action? OnAcquire { get; set; }
        public Task? SnapshotBarrier { get; set; }
        public int ApplyCalls { get; private set; }
        public int ActiveLeases { get; private set; }
        public TestHost() => Scope = Original;
        public async Task<HostPluginSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
        {
            if (SnapshotBarrier is { } wait) await wait.WaitAsync(cancellationToken);
            return new HostPluginSnapshot(true, Identity, Loaded)
            { Generation = Scope?.Generation, StructuredContext = Scope, ContextId = "opaque display" };
        }
        public Task<HostApplyResult> ApplyAsync(string entry, string id, string version, CancellationToken cancellationToken = default)
        {
            ApplyCalls++; Scope = Current;
            return Task.FromResult(new HostApplyResult(true, "fixture returned")
            { OriginalProcessExit = new RpcProcessExit(Original.Generation, 123, 0, DateTimeOffset.UtcNow) });
        }
        public Task<IPluginReconciliationLease?> AcquireReconciliationAsync(HostPluginSnapshot expected,
            CancellationToken cancellationToken = default)
        {
            if (Busy) return Task.FromResult<IPluginReconciliationLease?>(null);
            ActiveLeases++; OnAcquire?.Invoke();
            return Task.FromResult<IPluginReconciliationLease?>(new Lease(this));
        }
        private sealed class Lease(TestHost host) : IPluginReconciliationLease
        {
            private bool _disposed;
            public bool IsCurrent => !_disposed && host.LeaseCurrent;
            public void Dispose() { if (_disposed) return; _disposed = true; host.ActiveLeases--; }
        }
    }
}
