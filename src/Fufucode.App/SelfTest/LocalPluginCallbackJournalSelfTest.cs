using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Fufucode.App.ViewModels;
using Fufucode.App.Features.Plugins;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

internal static class LocalPluginCallbackJournalSelfTest
{
    internal static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool condition, string name)
        { if (condition) pass("Plugin callback journal: " + name); else fail("Plugin callback journal: " + name); }
        var root = Path.Combine(Path.GetTempPath(), "fufucode-plugin-callback-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "index.js"), "export default () => {}; // fixture never executed\n");
        File.WriteAllText(Path.Combine(source, "fufucode-plugin.json"), JsonSerializer.Serialize(new
        { id = "journal.plugin", displayName = "返回记账测试", version = "1.0", backend = "pi", entryRelativePath = "index.js", supportedHostVersions = new[] { "1.1.0" } }));
        var layout = new AppDataLayout(Path.Combine(root, "data"));
        var host = new LockingHost();
        var service = new LocalPluginService(layout, host);
        var version = await service.ImportAsync(new LocalPluginImportRequest(source));
        host.Root = service.Root;
        var failedCommit = false;
        try { await service.ApplyVersionAsync("journal.plugin", version.VersionId); }
        catch (InvalidOperationException) { failedCommit = true; }
        finally { host.Lock?.Dispose(); }
        var pending = PluginCatalogStore.Load(service.Root).State!.Plugins.Single();
        var request = pending.InFlight.Single();
        Check(failedCommit && host.ApplyCalls == 1 && pending.LastApplyReceipt is null && pending.OperationToken == request.Token,
            "real catalog lock rejects completion even after a successful host callback; original protected request remains");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Token)));
        var journalPath = Path.Combine(service.Root, ".callback-results", key + ".json");
        Check(File.Exists(journalPath),
            "host return is durably recorded under the exact request before a failing catalog completion");
        var completion = PluginCallbackJournal.Read(service.Root, pending.Id, request).Completion!;
        Check(completion is { Outcome: "returned_success" } && completion.Token == request.Token &&
            completion.VersionId == request.VersionId && completion.StartedAtUtc == request.StartedAtUtc &&
            completion.HostIdentity == host.Identity && completion.HostContext == "fixture-project\nfixture-session\n3",
            "durable return binds the exact original request, version, registration and host context");
        var catalogPath = Path.Combine(service.Root, PluginCatalogStore.CatalogFileName);
        var catalogBytes = File.ReadAllBytes(catalogPath);
        var journalBytes = File.ReadAllBytes(journalPath);
        var reopened = new LocalPluginService(layout);
        var observation = await reopened.InspectInFlightAsync(pending.Id, request.Token);
        Check(observation.Kind == PluginReceiptObservationKind.Unknown && observation.CallbackCompletion == completion &&
            observation.CallbackDiagnostic.Contains("尚未由此证明"),
            "reopening offline preserves received return evidence while keeping loading and safe release unknown");
        using (var vm = new LocalPluginViewModel(reopened))
        {
            var section = new LocalPluginSectionView(); section.Attach(vm);
            await vm.RefreshCommand.ExecuteAsync(null);
            await vm.InspectInFlightCommand.ExecuteAsync(null);
            var text = section.FindControl<TextBox>("InFlightObservation")!;
            Check(text.IsReadOnly && text.Text == vm.InFlightObservationText &&
                text.Text!.Contains("调用返回成功（未认证目录提交）") && text.Text.Contains(request.Token) &&
                text.Text.Contains("引擎终止") && vm.SelectedInFlight?.Request == request,
                "real compiled binding distinguishes received host success from catalog commit and engine termination");
            section.Detach();
        }
        Check(catalogBytes.AsSpan().SequenceEqual(File.ReadAllBytes(catalogPath)) &&
            journalBytes.AsSpan().SequenceEqual(File.ReadAllBytes(journalPath)) && host.ApplyCalls == 1,
            "offline reads and UI leave original catalog/return bytes intact and never replay the callback");
        var previousRoot = Program.SelfTestDataRoot;
        Program.SelfTestDataRoot = layout.Root;
        var window = new MainWindow { Width = 1100, Height = 800 }; window.Show();
        try
        {
            await ((MainViewModel)window.DataContext!).NavigateCommand.ExecuteAsync(AppPage.Extensions);
            var vm = window.LocalPluginViewModelForTesting;
            await vm.RefreshCommand.ExecuteAsync(null);
            await vm.InspectInFlightCommand.ExecuteAsync(null);
            var text = window.ExtensionsPageForTesting.LocalPluginsForTesting.FindControl<TextBox>("InFlightObservation")!;
            var viewport = window.FindControl<Control>("PageHost")!;
            foreach (var size in new[] { (1100, 800), (800, 640) })
            {
                window.Width = size.Item1; window.Height = size.Item2;
                await Task.Delay(200); window.UpdateLayout(); text.BringIntoView();
                await Task.Delay(250); window.UpdateLayout();
                var top = text.TranslatePoint(default, window);
                var bottom = viewport.TranslatePoint(new Avalonia.Point(0, viewport.Bounds.Height), window);
                Check(text.Text!.Contains("调用返回成功（未认证目录提交）") && top is { } t && bottom is { } b &&
                    t.Y >= 48 && t.Y + text.Bounds.Height <= b.Y + 1,
                    $"complete callback-return evidence and its boundaries are visible at {size.Item1}x{size.Item2}");
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(); await Task.Delay(100);
                using var frame = window.CaptureRenderedFrame();
                frame?.Save(Path.Combine(root, $"callback-{size.Item1}x{size.Item2}.png"), PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); await Task.Delay(100); Program.SelfTestDataRoot = previousRoot; }
        foreach (var altered in new[]
        {
            completion with { SchemaVersion = 99 }, completion with { PluginId = "other.plugin" },
            completion with { Token = request.Token + "x" }, completion with { VersionId = "other-version" },
            completion with { StartedAtUtc = request.StartedAtUtc.AddTicks(1) }, completion with { Outcome = "confirmed" },
            completion with { ReturnedAtUtc = default }, completion with { HostIdentity = null! },
        })
        {
            File.WriteAllText(journalPath, JsonSerializer.Serialize(altered));
            var alteredBytes = File.ReadAllBytes(journalPath);
            Check(PluginCallbackJournal.Read(service.Root, pending.Id, request).Completion is null &&
                alteredBytes.AsSpan().SequenceEqual(File.ReadAllBytes(journalPath)),
                "future, malformed or mismatched return record is rejected intact: " +
                (altered.SchemaVersion == 99 ? "future schema" : altered.Outcome));
        }
        File.WriteAllText(journalPath, "{broken return");
        var bad = File.ReadAllBytes(journalPath);
        Check(PluginCallbackJournal.Read(service.Root, pending.Id, request).Completion is null &&
            bad.AsSpan().SequenceEqual(File.ReadAllBytes(journalPath)), "damaged return file is never repaired or adopted silently");
        File.WriteAllBytes(journalPath, journalBytes);
        var overwriteRefused = false;
        try { PluginCallbackJournal.Save(service.Root, completion with { Outcome = "returned_failure" }); }
        catch (IOException) { overwriteRefused = true; }
        Check(overwriteRefused && journalBytes.AsSpan().SequenceEqual(File.ReadAllBytes(journalPath)),
            "a later writer cannot replace immutable original return evidence");
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(journalPath)!, "*.tmp-*").Any(),
            "failed duplicate publication leaves no partial record that reads could mistake for a return");
        File.Move(journalPath, journalPath + ".saved");
        Check(PluginCallbackJournal.Read(service.Root, pending.Id, request).Completion is null &&
            PluginCallbackJournal.Read(service.Root, pending.Id, request).Diagnostic.Contains("不能据此断言"),
            "missing journal remains unknown rather than certifying a running or terminated callback");
        File.Move(journalPath + ".saved", journalPath);
        var legacy = JsonNode.Parse(File.ReadAllText(journalPath))!;
        legacy.AsObject().Remove("OriginalGeneration");
        legacy.AsObject().Remove("OriginalProcessExit");
        File.WriteAllText(journalPath, legacy.ToJsonString());
        var legacyBytes = File.ReadAllBytes(journalPath);
        var legacyRead = PluginCallbackJournal.Read(service.Root, pending.Id, request).Completion;
        Check(legacyRead is not null && legacyRead.OriginalGeneration is null && legacyRead.OriginalProcessExit is null,
            "legacy return records without exit fields remain readable without invented termination evidence");
        Check(legacyBytes.AsSpan().SequenceEqual(File.ReadAllBytes(journalPath)),
            "reading legacy return records does not migrate or rewrite their original bytes");
        File.WriteAllBytes(journalPath, journalBytes);
        foreach (var mode in new[] { "failure", "cancelled", "threw", "journal-blocked" })
        {
            var caseHost = new LockingHost { Mode = mode };
            var caseService = new LocalPluginService(new AppDataLayout(Path.Combine(root, mode)), caseHost);
            var caseVersion = await caseService.ImportAsync(new LocalPluginImportRequest(source));
            caseHost.Root = caseService.Root;
            var rejected = false;
            try { await caseService.ApplyVersionAsync(pending.Id, caseVersion.VersionId); }
            catch (Exception error) when (error is InvalidOperationException or IOException) { rejected = true; }
            finally { caseHost.Lock?.Dispose(); }
            var state = PluginCatalogStore.Load(caseService.Root).State!.Plugins.Single();
            var own = state.InFlight.Single();
            var recorded = PluginCallbackJournal.Read(caseService.Root, state.Id, own).Completion;
            var expected = mode switch { "failure" => "returned_failure", "cancelled" => "cancelled", "threw" => "threw", _ => null };
            Check(rejected && state.SelectedVersionId is null && state.LastApplyReceipt is null &&
                state.OperationToken == own.Token && caseHost.ApplyCalls == 1 && recorded?.Outcome == expected,
                "failed completion or journal publication preserves protection and records only the actual callback fact: " + mode);
        }
        var generation = Guid.NewGuid();
        foreach (var mode in new[] { "matched-exit", "wrong-exit", "missing-generation" })
        {
            var caseHost = new LockingHost
            {
                Generation = mode == "missing-generation" ? null : generation,
                OriginalExit = new RpcProcessExit(mode == "wrong-exit" ? Guid.NewGuid() : generation, 123,
                    0, DateTimeOffset.UtcNow),
            };
            var caseService = new LocalPluginService(new AppDataLayout(Path.Combine(root, mode)), caseHost);
            var caseVersion = await caseService.ImportAsync(new LocalPluginImportRequest(source));
            caseHost.Root = caseService.Root;
            try { await caseService.ApplyVersionAsync(pending.Id, caseVersion.VersionId); }
            catch (InvalidOperationException) { }
            finally { caseHost.Lock?.Dispose(); }
            var state = PluginCatalogStore.Load(caseService.Root).State!.Plugins.Single();
            var own = state.InFlight.Single();
            var record = PluginCallbackJournal.Read(caseService.Root, state.Id, own).Completion!;
            Check(record is not null && record.OriginalGeneration == caseHost.Generation &&
                (mode == "matched-exit" ? record.OriginalProcessExit == caseHost.OriginalExit : record.OriginalProcessExit is null) &&
                state.OperationToken == own.Token && state.LastApplyReceipt is null,
                "injected exit evidence is adopted only for the exact original generation and never releases protection: " + mode);
            if (mode == "matched-exit" && record is not null)
            {
                var path = PluginCallbackJournal.PathFor(caseService.Root, own.Token);
                var savedBytes = File.ReadAllBytes(path);
                foreach (var altered in new[]
                {
                    record with { OriginalGeneration = Guid.NewGuid() },
                    record with { OriginalGeneration = null },
                    record with { OriginalProcessExit = record.OriginalProcessExit! with { ProcessId = 0 } },
                    record with { OriginalProcessExit = record.OriginalProcessExit! with { ObservedAtUtc = default } },
                })
                {
                    File.WriteAllText(path, JsonSerializer.Serialize(altered));
                    var alteredBytes = File.ReadAllBytes(path);
                    Check(PluginCallbackJournal.Read(caseService.Root, state.Id, own).Completion is null &&
                        alteredBytes.AsSpan().SequenceEqual(File.ReadAllBytes(path)),
                        "damaged or wrong-generation original exit evidence is refused intact");
                }
                File.WriteAllBytes(path, savedBytes);
                var oldRoot = Program.SelfTestDataRoot;
                Program.SelfTestDataRoot = Path.Combine(root, mode);
                var evidenceWindow = new MainWindow { Width = 1100, Height = 800 }; evidenceWindow.Show();
                try
                {
                    await ((MainViewModel)evidenceWindow.DataContext!).NavigateCommand.ExecuteAsync(AppPage.Extensions);
                    var vm = evidenceWindow.LocalPluginViewModelForTesting;
                    await vm.RefreshCommand.ExecuteAsync(null);
                    await vm.InspectInFlightCommand.ExecuteAsync(null);
                    var text = evidenceWindow.ExtensionsPageForTesting.LocalPluginsForTesting.FindControl<TextBox>("InFlightObservation")!;
                    var viewport = evidenceWindow.FindControl<Control>("PageHost")!;
                    Check(text.IsReadOnly && text.Text!.Contains("原RPC进程已退出：PID 123") && text.Text.Contains("原请求保护保持"),
                        "compiled UI displays injected original exit evidence separately from protection release");
                    foreach (var size in new[] { (1100, 800), (800, 640) })
                    {
                        evidenceWindow.Width = size.Item1; evidenceWindow.Height = size.Item2;
                        await Task.Delay(200); evidenceWindow.UpdateLayout(); text.BringIntoView();
                        await Task.Delay(250); evidenceWindow.UpdateLayout();
                        var top = text.TranslatePoint(default, evidenceWindow);
                        var bottom = viewport.TranslatePoint(new Avalonia.Point(0, viewport.Bounds.Height), evidenceWindow);
                        Check(top is { } t && bottom is { } b && t.Y >= 48 && t.Y + text.Bounds.Height <= b.Y + 1,
                            $"original exit and protection boundary remain fully visible at {size.Item1}x{size.Item2}");
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); await Task.Delay(100);
                        using var frame = evidenceWindow.CaptureRenderedFrame();
                        frame?.Save(Path.Combine(root, $"original-exit-{size.Item1}x{size.Item2}.png"), PngBitmapEncoderOptions.Default);
                    }
                }
                finally { evidenceWindow.Close(); await Task.Delay(100); Program.SelfTestDataRoot = oldRoot; }
            }
        }
        Console.WriteLine("callback journal evidence root: " + root);
    }

    private sealed class LockingHost : ILocalPluginHost
    {
        public HostIdentity Identity => HostIdentity.Pi("1.1.0");
        public string Root { get; set; } = string.Empty;
        public string Mode { get; init; } = "success";
        public Guid? Generation { get; init; }
        public RpcProcessExit? OriginalExit { get; init; }
        public FileStream? Lock { get; private set; }
        public int ApplyCalls { get; private set; }
        public Task<HostPluginSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new HostPluginSnapshot(true, Identity, []) { ContextId = "fixture-project\nfixture-session\n3", Generation = Generation });
        public Task<HostApplyResult> ApplyAsync(string entry, string id, string version, CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            if (Mode == "journal-blocked")
            {
                var request = PluginCatalogStore.Load(Root).State!.Plugins.Single().InFlight.Single();
                Directory.CreateDirectory(PluginCallbackJournal.PathFor(Root, request.Token));
                return Task.FromResult(new HostApplyResult(true));
            }
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("plugins-local-catalog".ToUpperInvariant())));
            Lock = new FileStream(Path.Combine(Root, ".locks", key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (Mode == "cancelled") throw new OperationCanceledException("fixture callback cancelled");
            if (Mode == "threw") throw new IOException("fixture callback threw");
            return Task.FromResult(new HostApplyResult(Mode != "failure", "fixture actually returned before catalog commit") { OriginalProcessExit = OriginalExit });
        }
    }
}
