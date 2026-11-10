using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using Fufucode.Core;
using Fufucode.Core.Backend;

namespace Fufucode.App.SelfTest;

internal static class LoadingUiRepairSelfTest
{
    internal static async Task<int> RunAsync(bool native = false)
    {
        var failures = new List<string>();
        var previousProbe = Core.Diagnostics.StartupProbe.Handler;
        Core.Diagnostics.StartupProbe.Handler = (name, detail) => Console.WriteLine($"TRACE {DateTime.UtcNow:HH:mm:ss.fff} {name} {detail}");
        void Check(bool value, string name)
        {
            Console.WriteLine((value ? "PASS " : "FAIL ") + name);
            if (!value) failures.Add(name);
        }
        var root = Path.GetFullPath(Path.Combine("tmp", "loading-ui-repair-" + Guid.NewGuid().ToString("N")));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var layout = new AppDataLayout(Path.Combine(root, "data"));
        var manager = new PersistentSessionManager("", "", layout);
        manager.Settings.Update(s => s with { AutoCheckUpdates = false, Endpoint = null });
        manager.SetProjectTrustDecision(project, ProjectTrustDecision.DoNotTrust);
        var record = manager.Metadata.CreateSession(manager.Metadata.GetOrCreateProject(project), "Large fixture", BackendId.NativeCSharp);
        // 单个很长回合 + 多轮历史：复现块排序、工具分组和整个消息回放的乘方开销。
        var messages = new List<PureAgentMessage> { new(PureAgentRole.User, "large turn") };
        for (var i = 0; i < 2500; i++)
        {
            messages.Add(new(PureAgentRole.Assistant, "", ToolCalls: [new PureToolCall("tool-" + i, "read_file", "{\"path\":\"fixture.txt\"}")]));
            messages.Add(new(PureAgentRole.Tool, new string('x', 4096), ToolCallId: "tool-" + i));
        }
        messages.Add(new(PureAgentRole.Assistant, "large-final-marker"));
        for (var i = 0; i < 800; i++)
        {
            messages.Add(new(PureAgentRole.User, "question-" + i));
            messages.Add(new(PureAgentRole.Assistant, "answer-" + i));
        }
        var file = NativeSessionFactory.SessionFilePath(layout, record.ProjectId, record.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { version = 2, messages, thinkingLevel = "high", executionMode = "manual" }));
        manager.Metadata.UpdateSessionFile(record.Id, file);
        SessionPresence.MarkEstablished(manager.Metadata, record.Id);
        var options = new AppOptions(AppSessionMode.Real, project, null, null, null, layout.Root, BackendId.NativeCSharp) { NewSession = true };
        var window = new MainWindow(options) { Width = 1200, Height = 850 };
        window.Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        try
        {
            await Until(() => window.CurrentSessionId is not null);
            // 等首次窗口/字体布局完成，再单独测“已打开应用内切换”的心跳，不混入冷启动。
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(150);
            var progress = window.FindControl<ProgressBar>("NavigationProgress")!;
            var actions = (AppActionRegistry)typeof(MainWindow).GetField("_actions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Check(!actions.Contains("window.newProject"), "obsolete project-window action is not registered");
            var ticks = 0;
            var maxGap = 0d;
            var previous = Stopwatch.GetTimestamp();
            var sawProgress = false;
            timer.Tick += (_, _) =>
            {
                var now = Stopwatch.GetTimestamp();
                var gap = Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds;
                if (gap > 100) Console.WriteLine($"UI-GAP {DateTime.UtcNow:HH:mm:ss.fff} gapMs={gap:F1}");
                maxGap = Math.Max(maxGap, gap);
                previous = now;
                ticks++;
                sawProgress |= progress.IsVisible;
            };
            timer.Start();
            var elapsed = Stopwatch.StartNew();
            var switching = window.SwitchSessionForOverviewTestingAsync(record.Id);
            Check(progress.IsVisible, "topbar indeterminate green line appears before session restoration");
            if (native) Save(window, "loading-start.png");
            await switching.WaitAsync(TimeSpan.FromSeconds(45));
            timer.Stop();
            Console.WriteLine($"PERF loadingMessages={messages.Count} bytes={new FileInfo(file).Length} elapsedMs={elapsed.ElapsedMilliseconds} uiTicks={ticks} maxUiGapMs={maxGap:F1}");
            Check(window.CurrentSessionId == record.Id, "large native session switch completes");
            Check(ticks >= 2 && sawProgress, "UI heartbeat and loading line continue while restoring large session");
            Check(maxGap < 1000, "large-session load does not block dispatcher for a Windows hung-window interval");
            Check(!progress.IsVisible, "topbar loading line stops only after history projection completes");
            var vm = (MainViewModel)window.DataContext!;
            Check(vm.Timeline.Entries.OfType<UserMessageViewModel>().Count() == 801, "all large-history user messages are retained");
            var firstTurn = vm.Timeline.Entries.OfType<AssistantMessageViewModel>().First();
            Check(firstTurn.Blocks.OfType<ToolCallBlockViewModel>().Count() == 2500, "all tool blocks survive nonquadratic replay");
            Check(vm.Timeline.Entries.OfType<AssistantMessageViewModel>().Last().Blocks.OfType<TextBlockViewModel>().Any(b => b.Text == "answer-799"),
                "last large-history message is not truncated");
            Check(vm.Timeline.VisibleEntries.Count <= 20, "large history only mounts recent timeline entries");

            var page = window.GetVisualDescendants().OfType<ChatPage>().First();
            var thinkingButton = page.FindControl<Button>("ThinkingButton")!;
            var menu = (MenuFlyout)thinkingButton.Flyout!;
            typeof(ChatPage).GetMethod("BuildThinkingMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [menu, vm]);
            var selected = menu.Items.OfType<MenuItem>().Where(i => i.Classes.Contains("permissionSelected")).ToArray();
            Check(selected.Length == 1 && !menu.Items.OfType<MenuItem>().Any(i => i.Header?.ToString()?.Contains('✓') == true),
                "thinking strength uses one selected outline and no textual checkmarks");
            menu.ShowAt(thinkingButton);
            await Task.Delay(100);
            Check(selected.Single().BorderThickness == new Thickness(1) && selected.Single().BorderBrush is ISolidColorBrush brush && brush.Color.A > 0,
                "thinking selected outline is present in the styled popup");
            if (native)
            {
                Save(window, "thinking-page.png");
                foreach (var item in selected)
                {
                    Save(item, "thinking-selected.png");
                    if (item.GetVisualAncestors().OfType<MenuFlyoutPresenter>().FirstOrDefault() is { } presenter)
                        Save(presenter, "thinking-popup.png");
                }
            }
            menu.Hide();

            // 在当前真实会话旁栏叠加完成状态，验证实际卡片和动画覆盖层的边界相同。
            var host = (RealSessionHost)typeof(MainWindow).GetField("_host", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var orbit = new SessionOrbit { Stroke = Brushes.LimeGreen };
            var card = new Border { Classes = { "overviewSession" }, Padding = new Thickness(0), CornerRadius = new CornerRadius(8) };
            var body = new Grid { Children = { new Avalonia.Controls.TextBlock { Text = "Orbit alignment fixture", Margin = new Thickness(8, 2) }, orbit } };
            card.Child = body;
            // 验证真实生产行，而不是只验证夹具中的布局。
            var current = host.Current.Chat;
            var field = typeof(NativeCSharpChatSession).GetField("_lastOutcome", BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(current, SessionOutcome.Completed);
            window.RenderOverview();
            window.UpdateLayout();
            await Task.Delay(100);
            var actual = window.GetVisualDescendants().OfType<SessionOrbit>().FirstOrDefault();
            var actualCard = actual?.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("overviewSession"));
            Check(actual is not null && actualCard is not null && Math.Abs(actual.Bounds.Width - actualCard.Bounds.Width) < .1 &&
                  Math.Abs(actual.Bounds.Height - actualCard.Bounds.Height) < .1 && actual.TranslatePoint(default, actualCard) == new Point(0, 0),
                "actual session orbit occupies the complete card bounds without content padding inset");
            Check(actual is not null && actualCard is not null && actual.CornerRadius == actualCard.CornerRadius,
                "orbit corner radius follows the actual themed card");
            if (native) Save(window, "orbit-aligned.png");
        }
        finally
        {
            timer.Stop();
            Core.Diagnostics.StartupProbe.Handler = previousProbe;
            window.Close();
            await Task.Delay(100);
        }
        return failures.Count == 0 ? 0 : 1;
    }

    private static async Task Until(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("window startup");
            await Task.Delay(20);
        }
    }
    private static void Save(Visual visual, string name)
    {
        var evidence = Path.GetFullPath("tmp/loading-ui-evidence");
        Directory.CreateDirectory(evidence);
        var size = visual.Bounds.Size;
        if (size.Width < 1 || size.Height < 1) return;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)), new Vector(96, 96));
        bitmap.Render(visual);
        bitmap.Save(Path.Combine(evidence, name), PngBitmapEncoderOptions.Default);
    }
}
