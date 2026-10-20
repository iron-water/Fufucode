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
using Fufucode.App.Abstractions;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;

namespace Fufucode.App.SelfTest;

/// <summary>Isolated preview over the product controls. Only navigation is writable.</summary>
public static class HeadlessUiRunner
{
    public static int RunProcess(string[] args)
    {
        // --results <dir>：保留隔离数据根并把性能诊断输出写进去（用于性能监控端到端验证）。
        var resultsIndex = Array.IndexOf(args, "--results");
        var results = resultsIndex >= 0 && resultsIndex + 1 < args.Length ? args[resultsIndex + 1] : null;
        var data = results is null
            ? Path.Combine(Path.GetTempPath(), "fufucode-ui-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(results, "data");
        // DESK-1：在窗口创建前把选定皮肤/主题写进隔离数据根，让真实渲染截图对准目标皮肤。
        var previewSkin = ValueAfter(args, "--skin") ?? "fluent";
        var previewTheme = ValueAfter(args, "--theme") ?? "dark";
        // 预览走 SelfTest 数据根（MainWindow 的皮肤存储用它），因此把种子写在同一个根上。
        Program.SelfTestDataRoot = data;
        try
        {
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "ui.json"),
                "{\"version\":1,\"skin\":\"" + previewSkin + "\",\"theme\":\"" + previewTheme + "\"}");
        }
        catch (Exception error) { Console.Error.WriteLine("Preview skin seed failed: " + error.Message); }
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
            try
            {
                if (results is not null)
                {
                    Directory.CreateDirectory(results);
                    var perfDir = Path.Combine(data, "perf");
                    var perf = Directory.Exists(perfDir);
                    File.WriteAllText(Path.Combine(results, "perf-exists.txt"),
                        perf + " | " + (perf ? string.Join(",", Directory.GetFiles(perfDir).Select(p => Path.GetFileName(p) + ":" + new FileInfo(p).Length)) : "-"));
                    var pages = File.Exists(Path.Combine(data, "plugins.json")) ? File.ReadAllText(Path.Combine(data, "plugins.json")) : "";
                    File.WriteAllText(Path.Combine(results, "plugins.json"), pages);
                }
                else if (Directory.Exists(data))
                {
                    Directory.Delete(data, recursive: true);
                }
            }
            catch (IOException error) { Console.Error.WriteLine("Preview data cleanup failed: " + error.Message); }
            catch (UnauthorizedAccessException error) { Console.Error.WriteLine("Preview data cleanup failed: " + error.Message); }
        }
    }

    private static async Task<int> ExecuteAsync(string[] args)
    {
        var arguments = args.ToList();
        var page = "chat";
        // DESK-1：预览可以带 --skin/--theme，便于把真实渲染截图对准指定皮肤，
        // 而不是只能在默认 Fluent 上看外壳。只写隔离的临时数据根，不碰产品配置。
        var skin = "fluent";
        var skinIndex = arguments.IndexOf("--skin");
        if (skinIndex >= 0)
        {
            if (skinIndex + 1 >= arguments.Count) throw new ArgumentException("Missing --skin value.");
            skin = arguments[skinIndex + 1]; arguments.RemoveRange(skinIndex, 2);
        }
        var theme = "dark";
        var themeIndex = arguments.IndexOf("--theme");
        if (themeIndex >= 0)
        {
            if (themeIndex + 1 >= arguments.Count) throw new ArgumentException("Missing --theme value.");
            theme = arguments[themeIndex + 1]; arguments.RemoveRange(themeIndex, 2);
        }
        var resultsIndex = arguments.IndexOf("--results");
        if (resultsIndex >= 0) arguments.RemoveRange(resultsIndex, Math.Min(2, arguments.Count - resultsIndex));
        var pageIndex = arguments.IndexOf("--page");
        if (pageIndex >= 0)
        {
            if (pageIndex + 1 >= arguments.Count) throw new ArgumentException("Missing --page value.");
            page = arguments[pageIndex + 1]; arguments.RemoveRange(pageIndex, 2);
        }
        if (page is not ("chat" or "extensions" or "settings")) throw new ArgumentException("Unknown preview page.");
        string? settingsCategory = null;
        var catIndex = arguments.IndexOf("--settings-category");
        if (catIndex >= 0)
        {
            if (catIndex + 1 >= arguments.Count) throw new ArgumentException("Missing --settings-category value.");
            settingsCategory = arguments[catIndex + 1]; arguments.RemoveRange(catIndex, 2);
        }
        string? settingsPage = null;
        var pageSubIndex = arguments.IndexOf("--settings-page");
        if (pageSubIndex >= 0)
        {
            if (pageSubIndex + 1 >= arguments.Count) throw new ArgumentException("Missing --settings-page value.");
            settingsPage = arguments[pageSubIndex + 1]; arguments.RemoveRange(pageSubIndex, 2);
        }
        var perfTurnIndex = arguments.IndexOf("--perf-turn");
        if (perfTurnIndex >= 0) arguments.RemoveAt(perfTurnIndex);
        string? shortcutsGroup = null;
        var groupIndex = arguments.IndexOf("--shortcuts-group");
        if (groupIndex >= 0)
        {
            if (groupIndex + 1 >= arguments.Count) throw new ArgumentException("Missing --shortcuts-group value.");
            shortcutsGroup = arguments[groupIndex + 1]; arguments.RemoveRange(groupIndex, 2);
        }
        var allowAction = arguments.Remove("--allow-action");
        var valid = arguments.Count == 1 && arguments[0] == "snapshot" ||
            arguments.Count == 2 && arguments[0] is "screenshot" or "invoke";
        if (!valid) throw new ArgumentException("Usage: ui snapshot | screenshot <new.png> | invoke nav.<page>|action [--allow-action] [--page <page>].");
        if (arguments[0] == "invoke" && !allowAction && arguments[1] is not ("nav.chat" or "nav.extensions" or "nav.settings"))
            throw new ArgumentException("Only nav.chat, nav.extensions and nav.settings can be invoked in preview.");
        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var window = lifetime.MainWindow as MainWindow ?? throw new InvalidOperationException("Preview window is missing.");
        // Keep navigation deterministic; do not change product styles or view code.
        window.FindControl<TransitioningContentControl>("PageHost")!.PageTransition = null;
        await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Background);
        var navigation = new Dictionary<string, string>
        { ["nav.chat"] = "chat", ["nav.extensions"] = "extensions", ["nav.settings"] = "settings" };
        await NavigateAsync("nav." + page);
        if (arguments[0] == "invoke")
        {
            var target = arguments[1];
            if (navigation.ContainsKey(target)) await NavigateAsync(target);
            else if (window.ActionsForTesting.GetDisabledReason(target) is { } reason) throw new InvalidOperationException(reason);
            else await window.ActionsForTesting.ExecuteAsync(target);
            await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Background);
        }
        var currentPage = ((MainViewModel)window.DataContext!).CurrentPage.ToString().ToLowerInvariant();
        if (perfTurnIndex >= 0)
        {
            // 性能监控 e2e：先跑一个 mock 回合，再打开性能停靠页并截图。
            var session = ((MainViewModel)window.DataContext!).Session;
            await session.SendAsync("性能验证回合");
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(200);
                if (session.Activity == SessionActivity.Idle) break;
            }
            await Task.Delay(2500);
        }
        if (settingsCategory is not null && ((MainViewModel)window.DataContext!).Settings is { } settingsVm)
        {
            settingsVm.SelectCategory(settingsCategory);
            if (settingsPage is not null) settingsVm.OpenPage(settingsPage);
            await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Background);
        }
        if (shortcutsGroup is not null &&
            ((MainViewModel)window.DataContext!).Settings.ShortcutsContent is Features.Shortcuts.ShortcutsPage shortcutsPage)
        {
            shortcutsPage.OpenGroupForTesting(shortcutsGroup);
            await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Background);
        }
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

    private static string? ValueAfter(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
