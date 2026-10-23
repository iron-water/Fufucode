using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.Core;
using Fufucode.App.Features.Plugins;
using System.Threading;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;

namespace Fufucode.App.SelfTest;

internal static class LocalPluginRealSelfTest
{
    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool ok, string name) { if (ok) pass("Real plugins: " + name); else fail("Real plugins: " + name); }
        var root = Path.Combine(Path.GetTempPath(), "fufucode-plugin-shell-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var layout = new AppDataLayout(Path.Combine(root, "data")); layout.EnsureCreated();
        File.WriteAllText(Path.Combine(layout.AgentDirectory, "models.json"), """
        {"providers":{"proof":{"baseUrl":"http://127.0.0.1:9/v1","api":"openai-completions","apiKey":"offline","models":[{"id":"devin/swe-2","name":"offline","reasoning":false,"input":["text"],"contextWindow":10000,"maxTokens":1000,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}]}}}
        """);
        new AppSettingsStore(layout).Update(settings => settings with { SelectedModel = "proof/devin/swe-2" });
        var previous = Program.SelfTestDataRoot; Program.SelfTestDataRoot = layout.Root;
        var options = new AppOptions(AppSessionMode.Real, project, null, null, null, layout.Root, BackendId.Pi);
        var window = new MainWindow(options) { Width = 1200, Height = 800 };
        var opens = 0;
        RealSessionHost? actualHost = null;
        window.OpenHostForTesting = async token =>
        {
            if (++opens == 1) throw new InvalidOperationException("offline-before-real-bootstrap");
            return actualHost = await RealSessionHost.OpenAsync(options, token);
        };
        window.Show();
        try
        {
            await Until(() => window.FindControl<Button>("StartupRetryButton")!.IsVisible);
            var plugins = window.LocalPluginViewModelForTesting;
            var section = window.ExtensionsPageForTesting.LocalPluginsForTesting;
            Check(window.DataContext is null && window.CurrentSessionId is null && !plugins.HasHost,
                "startup failure keeps the shell without a fake chat host");
            var offlineSource = Path.Combine(root, "offline-version"); Directory.CreateDirectory(offlineSource);
            File.WriteAllText(Path.Combine(offlineSource, "index.js"), "export default function(pi) {}\n");
            File.WriteAllText(Path.Combine(offlineSource, "fufucode-plugin.json"), JsonSerializer.Serialize(new
            { id = "shell.plugin", displayName = "Shell", version = "1.0", backend = "pi", entryRelativePath = "index.js", supportedHostVersions = new[] { "1.1.0" } }));
            plugins.SourceDirectory = offlineSource;
            await plugins.PreviewCommand.ExecuteAsync(null);
            await plugins.ImportCommand.ExecuteAsync(null);
            Check(plugins.Versions.Count == 1 && !plugins.CanApply, "offline import precedes actual Pi startup");
            window.FindControl<Button>("StartupRetryButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            // Host identity becomes known before AttachRealSession's catalog refresh finishes.
            // Assert the recovered page after the actual startup overlay and refresh have settled.
            await Until(() => window.CurrentSessionId is not null && plugins.HasHost && !plugins.IsBusy &&
                !window.FindControl<Border>("StartupOverlay")!.IsVisible);
            var id = window.CurrentSessionId;
            var model = (MainViewModel)window.DataContext!;
            await model.NavigateCommand.ExecuteAsync(AppPage.Extensions);
            window.UpdateLayout();
            await Until(() => ReferenceEquals(TopLevel.GetTopLevel(section), window));
            Console.WriteLine($"Recovered shell: opens={opens}; sameVM={ReferenceEquals(section.ViewModel, plugins)}; disposed={plugins.IsDisposed}; versions={plugins.Versions.Count}; sameTopLevel={ReferenceEquals(TopLevel.GetTopLevel(section), window)}; page={model.CurrentPage}; parent={section.Parent?.GetType().Name}");
            Check(opens == 2 && ReferenceEquals(section.ViewModel, plugins) && !plugins.IsDisposed && plugins.Versions.Count == 1 &&
                  ReferenceEquals(TopLevel.GetTopLevel(section), window),
                "real recovery restores the original section, catalog and view model without duplicate bootstrap");
            Check(plugins.ApplyButtonText == "安全重启并应用", "real shared page displays the safe restart action label");
            async Task Import(string version, string body)
            {
                var source = Path.Combine(root, "v" + version); Directory.CreateDirectory(source);
                File.WriteAllText(Path.Combine(source, "index.js"), body);
                File.WriteAllText(Path.Combine(source, "fufucode-plugin.json"), JsonSerializer.Serialize(new
                { id = "shell.plugin", displayName = "Shell", version, backend = "pi", entryRelativePath = "index.js", supportedHostVersions = new[] { "1.1.0" } }));
                plugins.SourceDirectory = source;
                await plugins.PreviewCommand.ExecuteAsync(null);
                await plugins.ImportCommand.ExecuteAsync(null);
                plugins.SelectedVersion = plugins.Versions.Single(v => v.VersionText == version);
            }
            ((MainViewModel)window.DataContext!).Input.DraftText = "real shell draft 中文";
            Check(plugins.CanApply, "actual compiled page enables compatible idle Pi safe apply");
            await plugins.ApplyCommand.ExecuteAsync(null);
            Check(((MainViewModel)window.DataContext!).CurrentPage == AppPage.Extensions &&
                ReferenceEquals(window.FindControl<ContentControl>("PageHost")!.Content, window.ExtensionsPageForTesting),
                "real apply keeps the actual current extension page mounted so its result remains reachable");
            Check(plugins.SelectedPlugin!.Item.IsActive && plugins.SelectedPlugin.SelectedVersionText == "1.0" &&
                  window.CurrentSessionId == id && ((MainViewModel)window.DataContext!).Input.DraftText == "real shell draft 中文",
                "shared command wires real apply, original session and persisted draft");
            var catalogPath = Path.Combine(window.LocalPluginsForTesting.Root, PluginCatalogStore.CatalogFileName);
            var originalCatalog = File.ReadAllBytes(catalogPath);
            var originalReceipt = plugins.SelectedReceipt!.Receipt;
            var returnRecord = JsonSerializer.Deserialize<PluginCallbackCompletion>(File.ReadAllText(
                PluginCallbackJournal.PathFor(window.LocalPluginsForTesting.Root, originalReceipt.Token)))!;
            Check(returnRecord.Outcome == "returned_success" && returnRecord.Token == originalReceipt.Token &&
                  returnRecord.VersionId == originalReceipt.VersionId && returnRecord.HostIdentity == HostIdentity.Pi("1.1.0") &&
                  id is not null && returnRecord.HostContext?.Contains(id, StringComparison.Ordinal) == true,
                "real Pi safe apply durably records its own returned success with original request and session context");
            var journalJson = JsonSerializer.SerializeToElement(returnRecord);
            Check(journalJson.TryGetProperty("OriginalContext", out var originalScope) && originalScope.ValueKind == JsonValueKind.Object,
                "returned real request has structured original project/session/generation for bounded reconciliation");
            Check(typeof(LocalPluginViewModel).GetProperty("ReconcileInFlightCommand") is not null,
                "shared plugin page offers an explicit bounded returned-registration reconciliation command");
            Check(journalJson.TryGetProperty("OriginalProcessExit", out var closedProcess) && closedProcess.ValueKind == JsonValueKind.Object,
                "real safe restart durably associates the original request with its actually exited RPC process");
            Check(returnRecord.OriginalGeneration is { } originalGeneration && returnRecord.OriginalProcessExit is { } originalExit &&
                originalExit.Generation == originalGeneration && originalExit.ProcessId > 0 &&
                returnRecord.HostContext?.EndsWith(originalGeneration.ToString(), StringComparison.Ordinal) == true,
                "real original exit matches the request's pre-restart generation and session context");
            var originalJournalPath = PluginCallbackJournal.PathFor(window.LocalPluginsForTesting.Root, originalReceipt.Token);
            var originalJournalBytes = File.ReadAllBytes(originalJournalPath);
            await plugins.InspectReceiptCommand.ExecuteAsync(null);
            Check(plugins.ReceiptObservationText.Contains("此刻宿主实际加载原版本") &&
                  plugins.SelectedReceipt.Receipt == originalReceipt,
                "shared receipt command observes real Pi proof loading without rewriting the original receipt");
            Check(originalCatalog.SequenceEqual(File.ReadAllBytes(catalogPath)) && window.CurrentSessionId == id,
                "real receipt inspection preserves exact catalog bytes and the original session without replay");
            await Import("2.0", "export default function(pi) {}\n");
            await plugins.ApplyCommand.ExecuteAsync(null);
            var secondReturn = JsonSerializer.Deserialize<PluginCallbackCompletion>(File.ReadAllText(
                PluginCallbackJournal.PathFor(window.LocalPluginsForTesting.Root, plugins.SelectedReceipt!.Receipt.Token)))!;
            Check(secondReturn.OriginalGeneration != returnRecord.OriginalGeneration &&
                secondReturn.OriginalProcessExit?.Generation == secondReturn.OriginalGeneration &&
                originalJournalBytes.SequenceEqual(File.ReadAllBytes(originalJournalPath)),
                "a second real restart binds its own old generation without rewriting the first request's exit");
            Check(plugins.SelectedPlugin!.Item.IsActive && plugins.SelectedPlugin.SelectedVersionText == "2.0",
                "same shared view model safely updates the actual loaded version");
            Check(((MainViewModel)window.DataContext!).CurrentPage == AppPage.Extensions,
                "real update preserves the visible extension page rather than silently returning to Chat");
            await Import("3.0", "export default function(pi) { throw new Error('shell-bad-candidate'); }\n");
            await plugins.ApplyCommand.ExecuteAsync(null);
            Check(plugins.SelectedPlugin!.Item.IsActive && plugins.SelectedPlugin.SelectedVersionText == "2.0",
                "failed shared apply confirms the preserved actual previous version");
            var failedReturn = JsonSerializer.Deserialize<PluginCallbackCompletion>(File.ReadAllText(
                PluginCallbackJournal.PathFor(window.LocalPluginsForTesting.Root, plugins.SelectedReceipt!.Receipt.Token)))!;
            Check(failedReturn.Outcome == "returned_failure" && failedReturn.OriginalGeneration is not null &&
                failedReturn.OriginalProcessExit?.Generation == failedReturn.OriginalGeneration &&
                failedReturn.OriginalGeneration != secondReturn.OriginalGeneration && plugins.SelectedPlugin.Item.IsActive,
                "failed real candidate recovery records only its original exited connection while the restored connection stays live");
            Check(((MainViewModel)window.DataContext!).Session.Connection == Fufucode.App.Abstractions.SessionConnection.Connected,
                "after rollback the shared chat view model points to the restored live connection");
            Check(((MainViewModel)window.DataContext!).CurrentPage == AppPage.Extensions,
                "failed candidate recovery keeps the original visible extension page and failure result");
            await plugins.RollbackCommand.ExecuteAsync(null);
            Check(plugins.SelectedPlugin!.Item.IsActive && plugins.SelectedPlugin.SelectedVersionText == "1.0",
                "shared rollback command actually restores the earlier fixed version");
            Check(((MainViewModel)window.DataContext!).CurrentPage == AppPage.Extensions,
                "real explicit rollback keeps the extension page open for its result and next action");
            // Strand a real successfully returned Pi restart using the actual catalog OS lease.
            await Import("4.0", "export default function(pi) {}\n");
            var service = window.LocalPluginsForTesting;
            SessionLease? catalogLock = null;
            service.CallbackRecordedForTesting = () => catalogLock = SessionLease.Acquire(service.Root, "plugins-local-catalog");
            try { await plugins.ApplyCommand.ExecuteAsync(null); }
            finally { catalogLock?.Dispose(); service.CallbackRecordedForTesting = null; }
            await plugins.RefreshCommand.ExecuteAsync(null);
            var stranded = plugins.SelectedInFlight!.Request;
            var strandedReturn = PluginCallbackJournal.Read(service.Root, "shell.plugin", stranded).Completion!;
            var strandedJournal = PluginCallbackJournal.PathFor(service.Root, stranded.Token);
            var strandedBytes = File.ReadAllBytes(strandedJournal);
            var originalRegistered = PluginCatalogStore.Load(service.Root).State!.Plugins.Single();
            var fixedVersion = originalRegistered.Versions.Single(v => v.VersionId == stranded.VersionId);
            var strandedEntry = Path.Combine(service.Root, originalRegistered.StorageKey, fixedVersion.DirectoryName, fixedVersion.EntryRelativePath);
            var entryBytes = File.ReadAllBytes(strandedEntry);
            Check(strandedReturn.Outcome == "returned_success" && strandedReturn.OriginalContext?.SessionId == id &&
                strandedReturn.OriginalProcessExit?.Generation == strandedReturn.OriginalGeneration &&
                originalRegistered.OperationToken == stranded.Token && originalRegistered.LastApplyReceipt?.Token != stranded.Token,
                "actual Pi returned and exited its original connection while catalog completion failed and exact registration survived");
            await plugins.InspectInFlightCommand.ExecuteAsync(null);
            plugins.ConfirmRegistrationEnd = true;
            var beforeRefusal = File.ReadAllBytes(catalogPath);
            await plugins.ReconcileInFlightCommand.ExecuteAsync(null);
            Check(beforeRefusal.SequenceEqual(File.ReadAllBytes(catalogPath)) && plugins.InFlightObservationText.Contains("仍加载原候选"),
                "actual loaded candidate refuses explicit registration ending without changing the catalog");
            // User explicitly restores a trusted fixed version through the normal real safe-restart action.
            plugins.SelectedVersion = plugins.Versions.Single(v => v.VersionText == "1.0");
            await plugins.ApplyCommand.ExecuteAsync(null);
            var beforeEnding = PluginCatalogStore.Load(service.Root).State!.Plugins.Single();
            var currentCoordinator = actualHost!.Current.Coordinator;
            var liveGeneration = currentCoordinator.Snapshot.Generation;
            // Assert the product-kept page; do not reopen it on behalf of the user.
            Check(((MainViewModel)window.DataContext!).CurrentPage == AppPage.Extensions &&
                ReferenceEquals(window.FindControl<ContentControl>("PageHost")!.Content, window.ExtensionsPageForTesting),
                "explicit restored fixed version leaves the actual plugin recovery page mounted without test navigation");
            await Until(() => ReferenceEquals(TopLevel.GetTopLevel(section), window));
            await plugins.InspectInFlightCommand.ExecuteAsync(null);
            plugins.ConfirmRegistrationEnd = true;
            Check(section.FindControl<Button>("ReconcileInFlightButton")!.IsEnabled,
                "shared real page enables the explicit confirmed action for the original unloaded request");
            foreach (var size in new[] { (1100, 800), (800, 640) })
            {
                window.Width = size.Item1; window.Height = size.Item2;
                await Task.Delay(200); window.UpdateLayout();
                var button = section.FindControl<Button>("ReconcileInFlightButton")!;
                button.BringIntoView(); await Task.Delay(250); window.UpdateLayout();
                var viewport = window.FindControl<Control>("PageHost")!;
                var top = button.TranslatePoint(default, window);
                var bottom = viewport.TranslatePoint(new Point(0, viewport.Bounds.Height), window);
                Check(button.IsEnabled && top is { } t && bottom is { } b && t.Y >= 48 && t.Y + button.Bounds.Height <= b.Y + 1,
                    $"explicit original-request reconciliation action is reachable at {size.Item1}x{size.Item2}");
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(); await Task.Delay(100);
                using var frame = window.CaptureRenderedFrame();
                frame?.Save(Path.Combine(root, $"reconciliation-{size.Item1}x{size.Item2}.png"), PngBitmapEncoderOptions.Default);
            }
            await plugins.ReconcileInFlightCommand.ExecuteAsync(null);
            var reconciled = PluginCatalogStore.Load(service.Root).State!.Plugins.Single();
            Check(!reconciled.InFlight.Any(r => r.Token == stranded.Token) && reconciled.Reconciliations.Single().Request == stranded &&
                reconciled.SelectedVersionId == beforeEnding.SelectedVersionId && reconciled.PreviousVersionId == beforeEnding.PreviousVersionId &&
                reconciled.LastApplyReceipt == beforeEnding.LastApplyReceipt && reconciled.RecentReceipts.SequenceEqual(beforeEnding.RecentReceipts),
                "real explicit ending records only original metadata and preserves newer actual selection and application receipts");
            Check(ReferenceEquals(actualHost.Current.Coordinator, currentCoordinator) && currentCoordinator.Snapshot.Generation == liveGeneration &&
                !currentCoordinator.IsInIdleMaintenance && window.CurrentSessionId == id &&
                ((MainViewModel)window.DataContext!).Input.DraftText == "real shell draft 中文",
                "metadata reconciliation releases actual idle maintenance without restarting the connection or changing session/draft");
            Check(entryBytes.SequenceEqual(File.ReadAllBytes(strandedEntry)) && strandedBytes.SequenceEqual(File.ReadAllBytes(strandedJournal)) &&
                plugins.HasReconciliationHistory && plugins.ReconciliationHistoryText.Contains(stranded.Token),
                "real page retains original files, immutable return evidence and ended-registration history");
            var adapter = new PiLocalPluginHost(layout, () => actualHost,
                () => RealSessionBootstrap.ResolveRuntime(options, required: true).Cli, () => false,
                (_, _, _) => throw new InvalidOperationException("verification must never invoke restart"));
            var idleSnapshot = await adapter.SnapshotAsync();
            using (var lease = await adapter.AcquireReconciliationAsync(idleSnapshot))
            {
                Check(lease is { IsCurrent: true } && currentCoordinator.IsInIdleMaintenance && !adapter.CanRestart,
                    "actual reconciliation lease holds the Pi adapter gate and live Core idle maintenance together");
                var duplicateLease = await adapter.AcquireReconciliationAsync(idleSnapshot);
                Check(duplicateLease is null, "actual reserved adapter rejects a concurrent second reconciliation");
                duplicateLease?.Dispose();
                var blockedApply = await adapter.ApplyWithRestartAsync(strandedEntry, "shell.plugin", "4.0", idleSnapshot);
                Check(!blockedApply.Success && ReferenceEquals(actualHost.Current.Coordinator, currentCoordinator),
                    "actual reserved adapter rejects a competing safe apply before invoking any restart");
                var promptBlocked = false;
                try { await currentCoordinator.SendPromptAsync("maintenance-test-must-not-be-sent"); }
                catch (InvalidOperationException) { promptBlocked = true; }
                Check(promptBlocked && currentCoordinator.Snapshot.Activity == ActivityState.Idle,
                    "actual Core maintenance rejects prompt submission locally without sending a model request");
            }
            var wrongScope = idleSnapshot with { StructuredContext = idleSnapshot.StructuredContext! with { SessionId = "other-session" } };
            var staleLease = await adapter.AcquireReconciliationAsync(wrongScope);
            Check(staleLease is null && !currentCoordinator.IsInIdleMaintenance && adapter.CanRestart,
                "stale scope refusal releases the actual apply gate and leaves the live connection idle");
            staleLease?.Dispose();
            await ((MainViewModel)window.DataContext!).NavigateCommand.ExecuteAsync(AppPage.Chat);
            Check(window.CurrentSessionId == id && ((MainViewModel)window.DataContext!).Input.DraftText == "real shell draft 中文",
                "return from plugin management keeps original session and draft");
        }
        finally { window.Close(); await Task.Delay(200); Program.SelfTestDataRoot = previous; Console.WriteLine("real shell evidence root: " + root); }
        static async Task Until(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(35);
            while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("Real plugin shell bootstrap not ready"); await Task.Delay(30); }
        }
    }
}
