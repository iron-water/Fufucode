using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using System.Linq;
using Avalonia.Controls;
using Fufucode.App.Features.Plugins;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

internal static class LocalPluginReceiptSelfTest
{
    internal static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool ok, string name) { if (ok) pass("Plugin receipts: " + name); else fail("Plugin receipts: " + name); }
        var previous = Program.SelfTestDataRoot;
        var root = Path.Combine(Path.GetTempPath(), "fufucode-plugin-receipts-" + Guid.NewGuid().ToString("N"));
        Program.SelfTestDataRoot = Path.Combine(root, "data");
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "index.js"), "export default () => {}; // never executed\n");
        File.WriteAllText(Path.Combine(source, "fufucode-plugin.json"), JsonSerializer.Serialize(new
        { id = "receipt.plugin", displayName = "回执测试", version = "1.0", backend = "pi", entryRelativePath = "index.js", supportedHostVersions = new[] { "1.1.0" } }));
        var layout = new AppDataLayout(Program.SelfTestDataRoot);
        using var cancel = new CancellationTokenSource();
        var host = new CancelledHost(cancel);
        var service = new LocalPluginService(layout, host);
        var version = await service.ImportAsync(new LocalPluginImportRequest(source));
        var result = await service.ApplyVersionAsync("receipt.plugin", version.VersionId, cancel.Token);
        Check(!result.Success && PluginCatalogStore.Load(service.Root).State!.Plugins[0].LastApplyReceipt?.Outcome == "cancelled_unknown",
            "existing service preserves the original unknown receipt instead of declaring success");
        var bytes = File.ReadAllBytes(Path.Combine(service.Root, PluginCatalogStore.CatalogFileName));
        var original = PluginCatalogStore.Load(service.Root).State!.Plugins[0];
        var receipt = original.LastApplyReceipt!;
        var entry = Path.Combine(service.Root, original.StorageKey, version.DirectoryName, version.EntryRelativePath);
        host.LoadedPaths.Add(entry);
        var loaded = await service.InspectReceiptAsync(original.Id, receipt.Token);
        Check(loaded.Kind == PluginReceiptObservationKind.Loaded && loaded.Receipt == receipt &&
            loaded.Receipt.Outcome == "cancelled_unknown", "current loaded observation does not rewrite the original cancelled-unknown outcome");
        host.LoadedPaths.Clear();
        var notLoaded = await service.InspectReceiptAsync(original.Id, receipt.Token);
        Check(notLoaded.Kind == PluginReceiptObservationKind.NotLoaded && notLoaded.Receipt == receipt,
            "currently not loaded never certifies the original request failed or never ran");
        host.LoadedPaths.Add(Path.Combine(Path.GetDirectoryName(entry)!, "other.js"));
        Check((await service.InspectReceiptAsync(original.Id, receipt.Token)).Kind == PluginReceiptObservationKind.NotLoaded,
            "a different file in the same package is not proof that the original entry loaded");
        host.LoadedPaths.Clear(); host.LoadedPaths.Add(entry);
        var saved = File.ReadAllBytes(entry); File.AppendAllText(entry, "// changed");
        Check((await service.InspectReceiptAsync(original.Id, receipt.Token)).Kind == PluginReceiptObservationKind.Unknown,
            "tampered original package remains unknown even when the host reports its entry");
        File.WriteAllBytes(entry, saved);
        host.CurrentIdentity = HostIdentity.Pi("9.9.9");
        Check((await service.InspectReceiptAsync(original.Id, receipt.Token)).Kind == PluginReceiptObservationKind.Unknown,
            "incompatible host cannot confirm the original receipt version");
        host.CurrentIdentity = HostIdentity.Pi("1.1.0");
        var missing = false;
        try { await service.InspectReceiptAsync(original.Id, "missing-token"); } catch (InvalidOperationException) { missing = true; }
        Check(missing, "missing original token is rejected instead of inspecting the newest receipt");
        Check(host.ApplyCalls == 1 && bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(service.Root, PluginCatalogStore.CatalogFileName))),
            "all inspections preserve exact catalog bytes and never replay an apply callback");
        var window = new MainWindow { Width = 1100, Height = 800 }; window.Show();
        try
        {
            var plugins = window.LocalPluginViewModelForTesting;
            await plugins.RefreshCommand.ExecuteAsync(null);
            using var surface = new LocalPluginTestSurface(plugins);
            var section = surface.Section;
            surface.Window.UpdateLayout();
            var list = section.FindControl<ListBox>("ReceiptList");
            Check(list?.ItemCount == 1, "compiled test host exposes the original unknown receipt for explicit read-only inspection");
            await plugins.InspectReceiptCommand.ExecuteAsync(null);
            Check(plugins.SelectedReceipt?.Receipt.Token == receipt.Token && plugins.ReceiptObservationText.Contains("原结果保持未知"),
                "shared compiled command reports offline unknown for the exact selected original token");
            var observation = section.FindControl<TextBox>("ReceiptObservation")!;
            Check(observation.IsReadOnly && observation.Text == plugins.ReceiptObservationText,
                "actual compiled observation binding remains selectable and read-only");
            surface.Window.UpdateLayout(); observation.BringIntoView(); await Task.Delay(50); surface.Window.UpdateLayout();
            var observationTop = observation.TranslatePoint(default, surface.Window);
            var viewport = surface.Viewport;
            var viewportBottom = viewport.TranslatePoint(new Avalonia.Point(0, viewport.Bounds.Height), surface.Window);
            Check(observationTop is { } top && viewportBottom is { } bottom && observation.Bounds.Height > 0 &&
                top.Y >= 0 && top.Y + observation.Bounds.Height <= bottom.Y + 1,
                "scrolling exposes the full read-only observation inside the host viewport");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Console.WriteLine("Receipt scroll before: " + string.Join("; ", observation.GetVisualAncestors().OfType<ScrollViewer>().Select(s => $"offset={s.Offset}; extent={s.Extent}; viewport={s.Viewport}; anchor={s.CurrentAnchor?.GetType().Name}")));
            await Task.Delay(100);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(100);
            var settledTop = observation.TranslatePoint(default, surface.Window);
            Console.WriteLine("Receipt scroll after: " + string.Join("; ", observation.GetVisualAncestors().OfType<ScrollViewer>().Select(s => $"offset={s.Offset}; extent={s.Extent}; viewport={s.Viewport}; anchor={s.CurrentAnchor?.GetType().Name}")));
            Check(settledTop is { } settled && viewportBottom is { } visibleBottom &&
                settled.Y + observation.Bounds.Height <= visibleBottom.Y + 1,
                "read-only observation remains in the viewport after deferred layout and scrolling settle");
            using (var frame = surface.Window.CaptureRenderedFrame()) frame?.Save(Path.Combine(root, "receipt-page.png"), PngBitmapEncoderOptions.Default);
            var details = section.FindControl<TextBox>("ReceiptOriginalDetails")!;
            details.BringIntoView(); await Task.Delay(200); surface.Window.UpdateLayout();
            var detailsTop = details.TranslatePoint(default, surface.Window);
            Check(details.IsReadOnly && details.Text!.Contains(receipt.Token) && detailsTop is { } detailPoint &&
                viewportBottom is { } detailBottom && detailPoint.Y >= 0 && detailPoint.Y + details.Bounds.Height <= detailBottom.Y + 1,
                "original receipt details remain scrollable and fully accessible for copying after observation");
            surface.Window.Width = 800; surface.Window.Height = 640;
            await Task.Delay(200); surface.Window.UpdateLayout(); observation.BringIntoView();
            await Task.Delay(200); surface.Window.UpdateLayout();
            var narrowTop = observation.TranslatePoint(default, surface.Window);
            var narrowBottom = viewport.TranslatePoint(new Avalonia.Point(0, viewport.Bounds.Height), surface.Window);
            Check(narrowTop is { } narrowPoint && narrowBottom is { } narrowLimit && narrowPoint.Y >= 0 &&
                narrowPoint.Y + observation.Bounds.Height <= narrowLimit.Y + 1,
                "minimum window size retains the complete read-only result after scrolling settles");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(); await Task.Delay(100);
            using (var frame = surface.Window.CaptureRenderedFrame()) frame?.Save(Path.Combine(root, "receipt-page-minimum.png"), PngBitmapEncoderOptions.Default);
            Check(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(service.Root, PluginCatalogStore.CatalogFileName))),
                "opening the receipt page leaves original catalog bytes unchanged");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<HostPluginSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.OnSnapshot = () => { entered.TrySetResult(); return release.Task; };
            var inspecting = service.InspectReceiptAsync(original.Id, receipt.Token);
            await entered.Task;
            File.WriteAllText(Path.Combine(source, "fufucode-plugin.json"), JsonSerializer.Serialize(new
            { id = "receipt.plugin", displayName = "回执测试", version = "2.0", backend = "pi", entryRelativePath = "index.js", supportedHostVersions = new[] { "1.1.0" } }));
            await service.ImportAsync(new LocalPluginImportRequest(source));
            var changedBytes = File.ReadAllBytes(Path.Combine(service.Root, PluginCatalogStore.CatalogFileName));
            release.SetResult(new HostPluginSnapshot(true, host.Identity, new[] { entry }));
            Check((await inspecting).Kind == PluginReceiptObservationKind.Stale && changedBytes.AsSpan().SequenceEqual(
                File.ReadAllBytes(Path.Combine(service.Root, PluginCatalogStore.CatalogFileName))),
                "catalog change during a host observation refuses stale evidence without overwriting the new catalog");
            host.OnSnapshot = null;
            using var detached = new LocalPluginViewModel(service);
            await detached.RefreshCommand.ExecuteAsync(null);
            entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new TaskCompletionSource<HostPluginSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.OnSnapshot = () => { entered.TrySetResult(); return release.Task; };
            var viewing = detached.InspectReceiptCommand.ExecuteAsync(null);
            await entered.Task;
            detached.SelectedPlugin = null;
            release.SetResult(new HostPluginSnapshot(true, host.Identity, new[] { entry }));
            await viewing;
            Check(detached.ReceiptObservationText.Length == 0 && detached.SelectedReceipt is null && !detached.IsBusy,
                "late receipt observation cannot attach to a changed selection");
        }
        finally { window.Close(); await Task.Delay(100); Program.SelfTestDataRoot = previous; Console.WriteLine("receipt evidence root: " + root); }
    }

    private sealed class CancelledHost(CancellationTokenSource cancel) : ILocalPluginHost
    {
        public HostIdentity CurrentIdentity { get; set; } = HostIdentity.Pi("1.1.0");
        public HostIdentity Identity => CurrentIdentity;
        public List<string> LoadedPaths { get; } = [];
        public Func<Task<HostPluginSnapshot>>? OnSnapshot { get; set; }
        public int ApplyCalls { get; private set; }
        public Task<HostPluginSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
            => OnSnapshot?.Invoke() ?? Task.FromResult(new HostPluginSnapshot(true, Identity, LoadedPaths.ToArray()));
        public Task<HostApplyResult> ApplyAsync(string entry, string id, string version, CancellationToken cancellationToken = default)
        { ApplyCalls++; cancel.Cancel(); return Task.FromResult(new HostApplyResult(true)); }
    }
}
