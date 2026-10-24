using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Fufucode.App.Features.Plugins;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

internal static class LocalPluginInFlightSelfTest
{
    internal static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool condition, string name)
        { if (condition) pass("Plugin in-flight: " + name); else fail("Plugin in-flight: " + name); }
        var previous = Program.SelfTestDataRoot;
        var root = Path.Combine(Path.GetTempPath(), "fufucode-plugin-inflight-" + Guid.NewGuid().ToString("N"));
        Program.SelfTestDataRoot = Path.Combine(root, "data");
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "index.js"), "export default () => {}; // fixture never executed\n");
        File.WriteAllText(Path.Combine(source, "fufucode-plugin.json"), JsonSerializer.Serialize(new
        { id = "inflight.plugin", displayName = "未结束请求测试", version = "1.0", backend = "pi", entryRelativePath = "index.js", supportedHostVersions = new[] { "1.1.0" } }));
        var host = new WaitingHost();
        var service = new LocalPluginService(new AppDataLayout(Program.SelfTestDataRoot), host);
        var version = await service.ImportAsync(new LocalPluginImportRequest(source));
        var applying = service.ApplyVersionAsync("inflight.plugin", version.VersionId);
        await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pending = PluginCatalogStore.Load(service.Root).State!.Plugins.Single();
        var original = pending.InFlight.Single();
        var catalogPath = Path.Combine(service.Root, PluginCatalogStore.CatalogFileName);
        var bytes = File.ReadAllBytes(catalogPath);
        var entry = Path.Combine(service.Root, pending.StorageKey, version.DirectoryName, version.EntryRelativePath);
        Check(pending.LastApplyReceipt is null && pending.RecentReceipts.Count == 0,
            "a real pending callback has its original token and no invented completed receipt");
        Check((await service.ListAsync()).Items.Single().InFlightRequests.Single() == original,
            "catalog projection preserves original token, version and registration time");
        var absent = await service.InspectInFlightAsync(pending.Id, original.Token);
        Check(absent.Kind == PluginReceiptObservationKind.NotLoaded && absent.Request == original &&
            absent.Explanation.Contains("不能证明原回调") && absent.CatalogGeneration == PluginCatalogStore.Load(service.Root).State!.Generation,
            "currently not loaded is only an observation and never proof that the pending callback ended");
        host.LoadedPaths = [entry];
        var loaded = await service.InspectInFlightAsync(pending.Id, original.Token);
        Check(loaded.Kind == PluginReceiptObservationKind.Loaded && loaded.Request == original && loaded.Explanation.Contains("不能认证"),
            "loaded observation does not invent a successful receipt or clear pending protection");
        host.LoadedPaths = [Path.Combine(Path.GetDirectoryName(entry)!, "other.js")];
        Check((await service.InspectInFlightAsync(pending.Id, original.Token)).Kind == PluginReceiptObservationKind.NotLoaded,
            "a different file in the same version is not the original entry");
        host.LoadedPaths = [entry];
        var saved = File.ReadAllBytes(entry); File.AppendAllText(entry, "// modified");
        Check((await service.InspectInFlightAsync(pending.Id, original.Token)).Kind == PluginReceiptObservationKind.Unknown,
            "tampered fixed copy remains unknown and protected");
        File.WriteAllBytes(entry, saved);
        host.CurrentIdentity = HostIdentity.Unknown();
        Check((await service.InspectInFlightAsync(pending.Id, original.Token)).Kind == PluginReceiptObservationKind.Unknown,
            "unknown host identity cannot confirm an unfinished request");
        host.CurrentIdentity = HostIdentity.Pi("9.9.9");
        Check((await service.InspectInFlightAsync(pending.Id, original.Token)).Kind == PluginReceiptObservationKind.Unknown,
            "incompatible current host cannot confirm an unfinished request");
        host.CurrentIdentity = HostIdentity.Pi("1.1.0");
        foreach (var token in new[] { "", "missing-token", " " + original.Token })
        {
            var rejected = false;
            try { await service.InspectInFlightAsync(pending.Id, token); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "missing or altered token never falls back to the newest request: " + (token.Length == 0 ? "empty" : "nonmatching"));
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var refused = false;
            try { await service.InspectInFlightAsync(pending.Id, original.Token, cancelled.Token); }
            catch (OperationCanceledException) { refused = true; }
            Check(refused, "cancelled inspection stops without invoking mutation or replay");
        }
        Check(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(catalogPath)) && host.ApplyCalls == 1,
            "all observations preserve exact catalog bytes, original pending token and callback count");
        host.OnSnapshot = () => throw new IOException("fixture snapshot unavailable");
        Check((await service.InspectInFlightAsync(pending.Id, original.Token)).Kind == PluginReceiptObservationKind.Unknown,
            "host query failure remains unknown rather than certifying not-loaded or callback termination");
        host.OnSnapshot = null;
        File.WriteAllText(catalogPath, "{malformed fixture");
        var corruptBytes = File.ReadAllBytes(catalogPath);
        var corruptRejected = false;
        try { await service.InspectInFlightAsync(pending.Id, original.Token); }
        catch (LocalPluginCatalogCorruptException) { corruptRejected = true; }
        finally
        {
            Check(corruptRejected && corruptBytes.AsSpan().SequenceEqual(File.ReadAllBytes(catalogPath)),
                "damaged catalog is rejected intact and never silently rebuilt by inspection");
            File.WriteAllBytes(catalogPath, bytes);
        }
        var window = new MainWindow { Width = 1100, Height = 800 }; window.Show();
        Task<LocalPluginActionResult>? secondApply = null;
        try
        {
            var vm = window.LocalPluginViewModelForTesting;
            await vm.RefreshCommand.ExecuteAsync(null);
            using var surface = new LocalPluginTestSurface(vm);
            surface.Window.UpdateLayout();
            var section = surface.Section;
            var requests = section.FindControl<ListBox>("InFlightRequestList");
            Check(requests?.ItemCount == 1,
                "compiled test host exposes a request without a receipt for exact-token read-only inspection");
            Check(vm.SelectedInFlight?.Request == original && !vm.HasReceipts && vm.HasInFlightRequests,
                "shared page selects the original unfinished request independently of completed receipts");
            var details = section.FindControl<TextBox>("InFlightOriginalDetails")!;
            Check(details.IsReadOnly && details.Text!.Contains(original.Token) && details.Text.Contains(original.StartedAtUtc.ToString("O")),
                "compiled original details are copyable and retain exact identity and time");
            await vm.InspectInFlightCommand.ExecuteAsync(null);
            var observation = section.FindControl<TextBox>("InFlightObservation")!;
            Check(observation.IsReadOnly && observation.Text == vm.InFlightObservationText &&
                observation.Text.Contains(original.Token) && observation.Text.Contains("尚无结束回执") && !vm.IsBusy,
                "compiled shared command displays offline unknown for the exact original request without creating a receipt");
            var viewport = surface.Viewport;
            foreach (var size in new[] { (1100, 800), (800, 640) })
            {
                surface.Window.Width = size.Item1; surface.Window.Height = size.Item2;
                await Task.Delay(200); surface.Window.UpdateLayout(); observation.BringIntoView();
                await Task.Delay(250); surface.Window.UpdateLayout();
                var top = observation.TranslatePoint(default, surface.Window);
                var bottom = viewport.TranslatePoint(new Avalonia.Point(0, viewport.Bounds.Height), surface.Window);
                Check(top is { } t && bottom is { } b && t.Y >= 0 && t.Y + observation.Bounds.Height <= b.Y + 1,
                    $"complete unfinished-request observation remains accessible at {size.Item1}x{size.Item2} after scrolling settles");
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(); await Task.Delay(100);
                using var frame = surface.Window.CaptureRenderedFrame();
                frame?.Save(Path.Combine(root, $"inflight-{size.Item1}x{size.Item2}.png"), PngBitmapEncoderOptions.Default);
            }
            Check(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(catalogPath)) && host.ApplyCalls == 1,
                "opening and inspecting the compiled host never releases protection or invokes a second apply");

            host.LoadedPaths = [];
            secondApply = service.ApplyVersionAsync(pending.Id, original.VersionId);
            Check(host.ApplyCalls == 2, "second fixture callback actually entered for the same version");
            var two = PluginCatalogStore.Load(service.Root).State!.Plugins.Single().InFlight;
            var second = two.Single(request => request.Token != original.Token);
            Check(two.Count == 2 && second.VersionId == original.VersionId &&
                (await service.InspectInFlightAsync(pending.Id, original.Token)).Request == original &&
                (await service.InspectInFlightAsync(pending.Id, second.Token)).Request == second,
                "same-version callbacks are inspected separately by exact original tokens");
            using var detached = new LocalPluginViewModel(service);
            await detached.RefreshCommand.ExecuteAsync(null);
            Check(detached.InFlightRequests.Count == 2 && detached.InFlightRequests.All(row => row.Caption.Contains(row.Request.Token)),
                "same-version requests remain distinguishable by their original tokens in the list");
            detached.SelectedInFlight = detached.InFlightRequests.Single(row => row.Request.Token == original.Token);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<HostPluginSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.OnSnapshot = () => { entered.TrySetResult(); return release.Task; };
            var viewing = detached.InspectInFlightCommand.ExecuteAsync(null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(detached.IsBusy && !detached.InspectInFlightCommand.CanExecute(null),
                "busy inspection disables another read command through the same binding source");
            detached.SelectedInFlight = detached.InFlightRequests.Single(row => row.Request.Token == second.Token);
            release.SetResult(new HostPluginSnapshot(true, host.Identity, []));
            await viewing;
            Check(detached.InFlightObservationText.Length == 0 && detached.SelectedInFlight.Request == second && !detached.IsBusy,
                "late observation for another token of the same version cannot attach to the new selection");
            using var closing = new LocalPluginViewModel(service);
            host.OnSnapshot = null;
            await closing.RefreshCommand.ExecuteAsync(null);
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            host.OnSnapshot = () => { entered.TrySetResult(); return release.Task; };
            var closingRead = closing.InspectInFlightCommand.ExecuteAsync(null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            closing.Dispose();
            var closingText = closing.InFlightObservationText;
            release.SetResult(new HostPluginSnapshot(true, host.Identity, []));
            await closingRead;
            Check(closing.IsDisposed && !closing.IsBusy && closing.InFlightObservationText == closingText,
                "closing the page cancels and refuses late observations without releasing the pending callback");
            Check(!closing.InspectInFlightCommand.CanExecute(null),
                "disposed page cannot start another unfinished-request inspection");
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var inspecting = service.InspectInFlightAsync(pending.Id, original.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            host.Release.TrySetResult(new HostApplyResult(false, "fixture callbacks ended during inspection"));
            await Task.WhenAll(applying, secondApply);
            var completedBytes = File.ReadAllBytes(catalogPath);
            release.SetResult(new HostPluginSnapshot(true, host.Identity, []));
            var stale = await inspecting;
            Check(stale.Kind == PluginReceiptObservationKind.Stale && stale.Request == original &&
                completedBytes.AsSpan().SequenceEqual(File.ReadAllBytes(catalogPath)),
                "callback completion during inspection returns stale original evidence without rewriting its new receipt");
            host.OnSnapshot = null;
            var endedRejected = false;
            try { await service.InspectInFlightAsync(pending.Id, original.Token); }
            catch (InvalidOperationException) { endedRejected = true; }
            Check(endedRejected, "completed request cannot silently inspect another unfinished request or an unrelated receipt");
            await vm.RefreshCommand.ExecuteAsync(null);
            Check(!vm.HasInFlightRequests && vm.SelectedInFlight is null && vm.InFlightObservationText.Length == 0 && vm.HasReceipts,
                "refresh removes finished registrations and exposes their real receipts without retaining a stale observation");
        }
        finally
        {
            host.Release.TrySetResult(new HostApplyResult(false, "fixture callback completed"));
            await applying;
            if (secondApply is not null) await secondApply;
            window.Close(); await Task.Delay(100);
            Program.SelfTestDataRoot = previous;
            Console.WriteLine("in-flight evidence root: " + root);
        }
    }

    private sealed class WaitingHost : ILocalPluginHost
    {
        public HostIdentity CurrentIdentity { get; set; } = HostIdentity.Pi("1.1.0");
        public HostIdentity Identity => CurrentIdentity;
        public string[] LoadedPaths { get; set; } = [];
        public int ApplyCalls { get; private set; }
        public Func<Task<HostPluginSnapshot>>? OnSnapshot { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HostApplyResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<HostPluginSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
            => OnSnapshot?.Invoke() ?? Task.FromResult(new HostPluginSnapshot(true, Identity, LoadedPaths));
        public Task<HostApplyResult> ApplyAsync(string entry, string id, string version, CancellationToken cancellationToken = default)
        { ApplyCalls++; Entered.TrySetResult(); return Release.Task; }
    }
}
