using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;

namespace Fufucode.App.SelfTest;

internal static class NotificationPageSelfTest
{
    internal static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool value, string name) { if (value) pass("NotificationPage: " + name); else fail("NotificationPage: " + name); }
        var root = Path.Combine(Path.GetTempPath(), "fufucode-notification-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var legacyPath = Path.Combine(root, "floating-window.json");
        new FloatingWindowPreferencesStore(legacyPath).Save(new(Pinned: false, Delivery: FloatingDelivery.Floating | FloatingDelivery.Windows,
            Left: 100, Top: 120, Width: 360, Height: 440));
        var original = File.ReadAllBytes(legacyPath);
        var store = new NotificationDeliveryPreferencesStore(root);
        var migrated = store.Load();
        Check(!migrated.Toast && migrated.Page && migrated.Windows && migrated.LegacyPosition == new NotificationWindowPosition(100, 120, 360, 440),
            "legacy channel flags and valid detached position migrate to the new schema");
        Check(File.Exists(store.Path) && original.SequenceEqual(File.ReadAllBytes(legacyPath)), "migration preserves the exact original preference bytes");
        var newBytes = File.ReadAllBytes(store.Path);
        Check(store.Load() == migrated && newBytes.SequenceEqual(File.ReadAllBytes(store.Path)), "replayed migration reads the new authority without rewriting either file");
        var closedRoot = Path.Combine(root, "legacy-closed"); Directory.CreateDirectory(closedRoot);
        new FloatingWindowPreferencesStore(Path.Combine(closedRoot, "floating-window.json")).Save(new(IsOpen: false));
        var closed = new NotificationDeliveryPreferencesStore(closedRoot).Load();
        Check(!closed.Page && closed.Toast && closed.LegacyPosition is null, "a previously closed legacy channel remains off and creates no window");
        var badRoot = Path.Combine(root, "bad-legacy"); Directory.CreateDirectory(badRoot);
        var badLegacy = Path.Combine(badRoot, "floating-window.json"); File.WriteAllText(badLegacy, "{\"SchemaVersion\":99}");
        var badStore = new NotificationDeliveryPreferencesStore(badRoot);
        var badVm = new NotificationDeliveryViewModel(badStore); badVm.DeliversToToast = false;
        Check(badVm.SaveError is not null && !badStore.CanWrite && !File.Exists(badStore.Path) && File.ReadAllText(badLegacy) == "{\"SchemaVersion\":99}",
            "unsupported legacy data stays intact instead of being overwritten with defaults");
        var futureRoot = Path.Combine(root, "future"); Directory.CreateDirectory(futureRoot);
        var futurePath = Path.Combine(futureRoot, "notification-delivery.json"); File.WriteAllText(futurePath, "{\"Version\":99}");
        var futureVm = new NotificationDeliveryViewModel(new(futureRoot)); futureVm.DeliversToWindows = true;
        Check(futureVm.SaveError is not null && File.ReadAllText(futurePath) == "{\"Version\":99}", "unsupported new versions are not silently downgraded");
        new WorkbenchAppearanceStore(root).Save(new() { Layout = new() { Selected = new()
            { [DockZone.Left] = "overview", [DockZone.Right] = "browser", [DockZone.Bottom] = "terminal" } } }, false);
        var previousRoot = Program.SelfTestDataRoot;
        Program.SelfTestDataRoot = root;
        var window = new MainWindow { Width = 1440, Height = 900 }; window.Show();
        try
        {
            await Ready();
            var layout = window.DockLayoutForTesting;
            var state = layout.FindFloating("notifications")!;
            Check(state is { Hidden: true, X: 100, Y: 120 } && layout.NotificationMigrationApplied &&
                layout.Selected[DockZone.Right] == "browser", "legacy position seeds one hidden generic container without changing the selected page");
            Check(window.FloatingDockWindowForTesting(state.Id)?.IsVisible == false && window.FindControl<Border>("CustomStatusBar")!.IsVisible == false,
                "migration opens neither an unsolicited window nor the default-off status bar");
            await window.ActionsForTesting.ExecuteAsync("notifications"); await Ready();
            var center = window.NotificationsForTesting;
            var page = (NotificationCenterView)window.PageForTesting("notifications");
            var floating = window.FloatingDockWindowForTesting(state.Id)!;
            Check(floating.IsVisible && ReferenceEquals(TopLevel.GetTopLevel(page), floating) && ReferenceEquals(page.DataContext, center),
                "opening migrated notifications reveals the original function page and shared center");
            var delivery = window.NotificationDeliveryForTesting;
            var pending = new Dictionary<string, TaskCompletionSource<WindowsToastResult>>();
            window.WindowsNoticeDeliveryForTesting = notice =>
            {
                var gate = new TaskCompletionSource<WindowsToastResult>(); pending.Add(notice.Key, gate); return gate.Task;
            };
            center.SetWindowFocused(true);
            var a = Notice("A"); var b = Notice("B");
            center.Items.Insert(0, a); center.LatestPrompt = a;
            center.Items.Insert(0, b); center.LatestPrompt = b;
            center.SetWindowFocused(true);
            Check(ReferenceEquals(center.LatestPrompt, b), "a repeated already-focused observation does not dismiss the active notification");
            await Ready();
            Check(pending.Count == 2 && page.HasChannelPromptForTesting && page.ChannelPromptForTesting!.Contains("notice-B", StringComparison.Ordinal),
                "independent system delivery starts once per notice while the page shows the latest content");
            Check(!page.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "暂无通知" && text.IsEffectivelyVisible),
                "adding notices updates the real empty-state binding without waiting for unrelated notifications");
            var badge = floating.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "dock-tab-notifications");
            Check(badge.Content?.ToString() == "通知 · 2", "floating auxiliary tabs display their actual unread count");
            pending["B"].SetResult(new(false, "B 回退")); await Ready();
            Console.WriteLine($"Notification fallback diagnostic B: latest={center.LatestPrompt?.Key}, hasPrompt={center.HasPrompt}, windows={delivery.DeliversToWindows}, status={delivery.WindowsDeliveryStatus}, visible={window.FindControl<Border>("NotificationPrompt")!.IsVisible}");
            Check(window.FindControl<Border>("NotificationPrompt")!.IsVisible, "failed system delivery falls back to the app even with toast disabled");
            pending["A"].SetResult(new(false, "A 回退")); await Ready();
            Check(window.FindControl<Border>("NotificationPrompt")!.IsVisible && delivery.WindowsDeliveryStatus == "B 回退",
                "an older delayed system failure cannot erase the newer fallback or status");
            delivery.DeliversToWindows = false; await Ready();
            Check(!window.FindControl<Border>("NotificationPrompt")!.IsVisible && page.HasChannelPromptForTesting,
                "turning system delivery off removes its fallback while leaving the page channel on");
            delivery.DeliversToPage = false; await Ready();
            Check(!page.HasChannelPromptForTesting && badge.Content?.ToString() == "通知" && center.Items.Count == 2,
                "page delivery off clears only its prompt and badge, retaining all history");
            delivery.DeliversToPage = true; a.IsRead = true; await Ready();
            Check(badge.Content?.ToString() == "通知 · 1", "reading a notice updates floating badges without replacing pages");
            floating.Close(); await Ready();
            Check(!floating.IsVisible && center.Items.Count == 2, "closing the floating presentation preserves its notification history");
            await window.ActionsForTesting.ExecuteAsync("notifications"); await Ready();
            Check(ReferenceEquals(window.PageForTesting("notifications"), page) && floating.IsVisible && center.Items.Count == 2,
                "reopening the page preserves control identity and history");
            await window.ActionsForTesting.ExecuteAsync("usage"); await Ready();
            var usage = window.PageForTesting("usage");
            Check(usage.IsEffectivelyVisible && ReferenceEquals(usage.DataContext, window.UsageForTesting) && window.UsageForTesting.IsOpen,
                "usage uses the same data source in a normal function page");
            var usageId = window.FloatDockPage("usage")!; await Ready();
            var usageWindow = window.FloatingDockWindowForTesting(usageId)!;
            usageWindow.Width = 320; usageWindow.Height = 560; await Ready();
            Check(ReferenceEquals(usage, window.PageForTesting("usage")) && ReferenceEquals(TopLevel.GetTopLevel(usage), usageWindow),
                "usage floats through the common host without creating another data source");
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                usageWindow.RequestedThemeVariant = theme; floating.RequestedThemeVariant = theme;
                floating.Width = 320; await Ready();
                using var notificationFrame = floating.CaptureRenderedFrame();
                using var usageFrame = usageWindow.CaptureRenderedFrame();
                notificationFrame?.Save(Path.Combine(root, "notifications-320-" + theme + ".png"), PngBitmapEncoderOptions.Default);
                usageFrame?.Save(Path.Combine(root, "usage-320-" + theme + ".png"), PngBitmapEncoderOptions.Default);
                Check(notificationFrame?.PixelSize.Width == 320 && usageFrame?.PixelSize.Width == 320, "320-DIP notification and usage pages render in " + theme);
            }
            window.ResetDockLayout(); await Ready();
            Check(layout != window.DockLayoutForTesting && window.DockLayoutForTesting.NotificationMigrationApplied &&
                window.DockLayoutForTesting.FindFloating("notifications") is null, "explicit layout reset never resurrects a migrated legacy window");
            Check(original.SequenceEqual(File.ReadAllBytes(legacyPath)), "UI changes and reset still preserve the original legacy file");
            Console.WriteLine("Notification page evidence: " + root);
        }
        finally { window.Close(); await Task.Delay(120); Program.SelfTestDataRoot = previousRoot; }
        async Task Ready() { await Task.Delay(70); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        static NotificationItemViewModel Notice(string id) => new(id, "fixture", Guid.NewGuid(), 1, AppNotificationKind.Completed, "notice-" + id, "local fixture", null, true);
    }
}
