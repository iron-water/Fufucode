using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Fufucode.App.Integration;

namespace Fufucode.App.SelfTest;

internal static class LocalPluginStartupSelfTest
{
    internal static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool ok, string name) { if (ok) pass("Startup plugins: " + name); else fail("Startup plugins: " + name); }
        var previous = Program.SelfTestDataRoot;
        var root = Path.Combine(Path.GetTempPath(), "fufucode-startup-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "index.js"), "export default () => {}; // never executed\n");
        File.WriteAllText(Path.Combine(source, "fufucode-plugin.json"), JsonSerializer.Serialize(new
        { id = "startup.plugin", displayName = "离线启动测试", version = "1.0", backend = "pi", entryRelativePath = "index.js", supportedHostVersions = new[] { "1.1.0" } }));
        try
        {
            foreach (var failedStartup in new[] { false, true })
            {
                Program.SelfTestDataRoot = Path.Combine(root, failedStartup ? "failure-data" : "picker-data");
                var window = new MainWindow(new AppOptions(AppSessionMode.Real,
                    failedStartup ? root : null, null, null, null, Program.SelfTestDataRoot)) { Width = 800, Height = 640 };
                var opens = 0;
                var opening = new TaskCompletionSource<RealSessionHost>(TaskCreationOptions.RunContinuationsAsynchronously);
                window.OpenHostForTesting = token =>
                {
                    opens++;
                    return opens == 1 ? Task.FromException<RealSessionHost>(new InvalidOperationException("offline-startup-failure"))
                        : opening.Task.WaitAsync(token);
                };
                window.Show();
                try
                {
                    await Until(() => !window.FindControl<ProgressBar>("StartupProgress")!.IsVisible);
                    var button = window.FindControl<Button>("StartupLocalPluginsButton");
                    Check(button?.IsVisible == true, failedStartup ? "failure exposes an offline plugin entry" : "no project exposes an offline plugin entry");
                    if (button is null) continue; // Red baseline: missing product entry, never invent a test-only route.
                    var text = window.FindControl<TextBlock>("StartupText")!.Text;
                    var picker = window.FindControl<StackPanel>("StartupPickProject")!;
                    var retry = window.FindControl<Button>("StartupRetryButton")!;
                    var originalPicker = picker.IsVisible;
                    var originalRetry = retry.IsVisible;
                    var plugins = window.LocalPluginViewModelForTesting;
                    var section = window.ExtensionsPageForTesting.LocalPluginsForTesting;
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(30); window.UpdateLayout();
                    var panel = window.FindControl<Grid>("StartupPluginPanel")!;
                    var back = window.FindControl<Button>("StartupPluginReturnButton")!;
                    Check(panel.IsVisible && !window.FindControl<StackPanel>("StartupSummary")!.IsVisible &&
                        ReferenceEquals(TopLevel.GetTopLevel(section), window), "offline entry hosts the original compiled section in the same window");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(ReferenceEquals(window.FindControl<ContentControl>("StartupPluginHost")!.Content, section),
                        "duplicate entry click keeps one parent and one original section");
                    Check(back.IsVisible && back.Bounds.Width > 0 && back.Bounds.Height > 0,
                        "minimum window size retains a visible return action");
                    Check(window.DataContext is null && window.CurrentSessionId is null && !plugins.HasHost &&
                        opens == (failedStartup ? 1 : 0), "offline navigation creates neither a mock session nor an engine attempt");
                    plugins.SourceDirectory = source;
                    await plugins.PreviewCommand.ExecuteAsync(null);
                    await plugins.ImportCommand.ExecuteAsync(null);
                    Check(plugins.HasPlugins && plugins.Versions.Count == 1 && !plugins.CanApply && !plugins.CanRollback,
                        "offline preview and import remain usable while engine actions stay unavailable");
                    Check(window.LocalPluginsForTesting.Root == Path.Combine(Program.SelfTestDataRoot, "plugins", "local"),
                        "offline view and normal extensions share the same isolated catalog root");
                    Check(section.FindControl<ListBox>("PluginList")!.ItemCount == 1,
                        "offline compiled list binding shows the imported plugin");
                    await Task.Delay(30); window.UpdateLayout();
                    using (var frame = window.CaptureRenderedFrame())
                        frame?.Save(Path.Combine(root, failedStartup ? "failure-offline.png" : "picker-offline.png"), PngBitmapEncoderOptions.Default);
                    back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(!panel.IsVisible && window.FindControl<StackPanel>("StartupSummary")!.IsVisible &&
                        window.FindControl<TextBlock>("StartupText")!.Text == text && picker.IsVisible == originalPicker && retry.IsVisible == originalRetry,
                        "return restores the exact original project or failure state without retry");
                    Check(!plugins.IsDisposed && ReferenceEquals(section.ViewModel, plugins),
                        "temporary return keeps the original view model and version selection alive");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(section.FindControl<ListBox>("PluginList")!.ItemCount == 1 && !plugins.IsDisposed,
                        "reopening reuses the existing section and imported catalog");
                    if (failedStartup)
                    {
                        back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Until(() => opens == 2);
                        Check(!button.IsVisible && !panel.IsVisible && window.FindControl<ProgressBar>("StartupProgress")!.IsVisible,
                            "explicit retry removes offline access during bootstrap");
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Check(!panel.IsVisible && opens == 2, "stale offline click cannot interrupt an in-progress startup");
                    }
                }
                finally
                {
                    window.Close(); await Task.Delay(100);
                    Check(window.LocalPluginViewModelForTesting.IsDisposed, "closing from offline or bootstrap releases the original plugin view model");
                    window.FindControl<Button>("StartupLocalPluginsButton")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(window.LocalPluginViewModelForTesting.IsDisposed,
                        "late startup click after close cannot revive the released view model");
                }
            }
            Program.SelfTestDataRoot = Path.Combine(root, "picker-data");
            var reopened = new MainWindow(new AppOptions(AppSessionMode.Real, null, null, null, null, Program.SelfTestDataRoot));
            reopened.Show();
            try
            {
                await Until(() => reopened.FindControl<Button>("StartupLocalPluginsButton")!.IsVisible);
                reopened.FindControl<Button>("StartupLocalPluginsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var restored = reopened.LocalPluginViewModelForTesting;
                await restored.RefreshCommand.ExecuteAsync(null);
                Check(restored.Versions.Count == 1 && !restored.HasHost && reopened.CurrentSessionId is null,
                    "new offline window restores the catalog without launching or fabricating an engine");
            }
            finally { reopened.Close(); await Task.Delay(100); }
        }
        finally { Program.SelfTestDataRoot = previous; Console.WriteLine("startup plugin evidence root: " + root); }

        static async Task Until(Func<bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!predicate()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("startup state not reached"); await Task.Delay(20); }
        }
    }
}
