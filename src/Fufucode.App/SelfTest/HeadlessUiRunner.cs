using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;

namespace Fufucode.App.SelfTest;

/// <summary>Isolated preview over the product controls. Only navigation is writable.</summary>
public static class HeadlessUiRunner
{
    public static int RunProcess(string[] args)
    {
        var data = Path.Combine(Path.GetTempPath(), "fufucode-ui-" + Guid.NewGuid().ToString("N"));
        var completed = new TaskCompletionSource<int>();
        Dispatcher.UIThread.Post(async () =>
        {
            try { completed.TrySetResult(await ExecuteAsync(args)); }
            catch (ArgumentException error) { Console.Error.WriteLine(error.Message); completed.TrySetResult(2); }
            catch (Exception error) { Console.Error.WriteLine(error.GetBaseException().Message); completed.TrySetResult(3); }
            finally
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                    lifetime.Shutdown();
            }
        }, DispatcherPriority.Background);
        try
        {
            AppBuilder.Configure(() => new App(AppOptions.Mock with { AppDataRootOverride = data }))
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia().WithInterFont().StartWithClassicDesktopLifetime([]);
            return completed.Task.IsCompleted ? completed.Task.Result : 3;
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetBaseException().Message); return 3; }
        finally
        {
            try { if (Directory.Exists(data)) Directory.Delete(data, recursive: true); }
            catch (IOException) { Console.Error.WriteLine("Preview data cleanup failed: " + data); }
            catch (UnauthorizedAccessException) { Console.Error.WriteLine("Preview data cleanup failed: " + data); }
        }
    }

    private static async Task<int> ExecuteAsync(string[] args)
    {
        var arguments = args.ToList();
        var page = "chat";
        var pageIndex = arguments.IndexOf("--page");
        if (pageIndex >= 0)
        {
            if (pageIndex + 1 >= arguments.Count) throw new ArgumentException("Missing --page value.");
            page = arguments[pageIndex + 1]; arguments.RemoveRange(pageIndex, 2);
        }
        if (page is not ("chat" or "extensions" or "settings")) throw new ArgumentException("Unknown preview page.");
        var valid = arguments.Count == 1 && arguments[0] == "snapshot" ||
            arguments.Count == 2 && arguments[0] is "screenshot" or "invoke";
        if (!valid) throw new ArgumentException("Usage: ui snapshot | screenshot <new.png> | invoke nav.<page> [--page <page>].");
        if (arguments[0] == "invoke" && arguments[1] is not ("nav.chat" or "nav.extensions" or "nav.settings"))
            throw new ArgumentException("Only nav.chat, nav.extensions and nav.settings can be invoked in preview.");
        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var window = lifetime.MainWindow as MainWindow ?? throw new InvalidOperationException("Preview window is missing.");
        // Keep navigation deterministic; do not change product styles or view code.
        window.FindControl<TransitioningContentControl>("PageHost")!.PageTransition = null;
        await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Background);
        var navigation = new Dictionary<string, string>
        { ["nav.chat"] = "chat", ["nav.extensions"] = "extensions", ["nav.settings"] = "settings" };
        await NavigateAsync("nav." + page);
        if (arguments[0] == "invoke") await NavigateAsync(arguments[1]);
        var currentPage = ((MainViewModel)window.DataContext!).CurrentPage.ToString().ToLowerInvariant();
        if (arguments[0] == "screenshot")
        {
            var path = Path.GetFullPath(arguments[1]);
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Screenshot target must be a new .png file.");
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame.");
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                frame.Save(output, PngBitmapEncoderOptions.Default);
            Write(new { schemaVersion = 1, mode = "headless-preview", sessionSource = "mock", page = currentPage,
                path, width = frame.PixelSize.Width, height = frame.PixelSize.Height });
        }
        else
        {
            var remaining = 4000; var truncated = false;
            var tree = Node(window, "root", 0);
            Write(new { schemaVersion = 1, mode = "headless-preview", sessionSource = "mock", page = currentPage,
                invoked = arguments[0] == "invoke" ? arguments[1] : null, truncated, tree,
                navigation = navigation.Select(pair => new { id = pair.Key, action = pair.Value,
                    enabled = window.ActionsForTesting.GetDisabledReason(pair.Value) is null,
                    actions = new[] { "invoke" }, source = "shared-action-registry" }).ToArray() });

            object Node(Control control, string address, int depth)
            {
                remaining--;
                var peer = ControlAutomationPeer.CreatePeerForElement(control);
                var secret = control is TextBox { PasswordChar: not '\0' };
                var id = AutomationProperties.GetAutomationId(control);
                var name = secret ? null : peer?.GetName();
                var bounds = control.Bounds;
                var children = new List<object>();
                var index = 0;
                foreach (var child in control.GetVisualChildren().OfType<Control>())
                {
                    if (remaining <= 0 || depth >= 40) { truncated = true; break; }
                    children.Add(Node(child, address + "/" + index++, depth + 1));
                }
                return new { id = string.IsNullOrEmpty(id) ? address : id, type = control.GetType().Name,
                    role = peer?.GetAutomationControlType().ToString(), automationId = id, name,
                    enabled = control.IsEffectivelyEnabled, visible = control.IsEffectivelyVisible,
                    focused = control.IsFocused, redacted = secret,
                    bounds = new { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height },
                    actions = Array.Empty<string>(), children };
            }
        }
        return 0;

        async Task NavigateAsync(string id)
        {
            if (!navigation.TryGetValue(id, out var action)) throw new ArgumentException("Navigation action not found: " + id);
            if (window.ActionsForTesting.GetDisabledReason(action) is { } reason) throw new InvalidOperationException(reason);
            // Settings is a toggle for users; a preview page request must be idempotent.
            if (((MainViewModel)window.DataContext!).CurrentPage.ToString().ToLowerInvariant() != id[4..])
                await window.ActionsForTesting.ExecuteAsync(action);
            await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Background);
            var expected = id[4..];
            if (((MainViewModel)window.DataContext!).CurrentPage.ToString().ToLowerInvariant() != expected)
                throw new InvalidOperationException("Navigation did not reach the requested page.");
        }
    }

    private static void Write(object result) => Console.Out.WriteLine(JsonSerializer.Serialize(result,
        new JsonSerializerOptions { MaxDepth = 128 }));
}
