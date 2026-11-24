using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Fufucode.Plugins.Terminal.Infrastructure;
using Fufucode.Plugins.Terminal.ViewModels;
using Fufucode.Plugins.Terminal.Views;

namespace Fufucode.App.Terminal.Tests;

internal static partial class Program
{
    private static void RunViewportChecks()
    {
        var buffer = new TerminalBuffer(60, 12);
        buffer.Resize(60, 3); // the first layout pass can be shorter than the default PTY
        buffer.Append("PS test> ");
        foreach (var rows in new[] { 14, 5, 21, 6, 20 })
        {
            buffer.Resize(60, rows);
            buffer.Append("\x1b[HPS test> \x1b[K"); // same live cells, ConPTY redraw, not new output
            var screen = buffer.Snapshot();
            Check(screen.Lines.Length == rows && screen.CursorLine == 0 && buffer.PlainText == "PS test>",
                $"terminal-resize-keeps-first-prompt-on-row-one-{rows}");
        }
        buffer.Append("\r\n\r\nPS test> ");
        buffer.Resize(60, 4);
        Check(buffer.PlainText == "PS test>\n\nPS test>", "terminal-resize-preserves-intentional-blank-and-repeated-lines");
        buffer.Append("\x1b[6n\x1b[5n\x1b[0m");
        Check(buffer.TakePendingResponse() == "\x1b[3;10R\x1b[0n", "terminal-batched-queries-survive-trailing-repaint");
        buffer.Append("\x1b[?1049h\x1b[4;1H\x1b" + "7");
        buffer.Resize(60, 3); buffer.Append("\x1b[?1049lX");
        Check(buffer.PlainText == "PS test>\n\nPS test> X" && buffer.Snapshot().CursorLine == 2,
            "terminal-alt-resize-and-save-restore-main-cursor");
    }

    // Real PowerShell + ConPTY feeding the same control/layout as the dock (no prompt or PTY mock).
    private static void RunLiveDockChecks()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP: Windows ConPTY required"); return; }
        using var dock = new TerminalDockViewModel(workspacePath: Environment.CurrentDirectory,
            settings: TerminalSettings.Default with { ShellProfile = "powershell", ConfirmClose = false });
        var view = new TerminalDockView { DataContext = dock, Height = 203, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom };
        var window = new Window { Title = "Fufucode · 终端隔离验收", Width = 980, Height = 700, Content = new Grid { Children = { view } },
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark };
        var surface = view.FindControl<TerminalScreenControl>("TerminalSurface")!;
        var scroller = view.FindControl<ScrollViewer>("OutputScroller")!;
        var prompt = "PS " + Environment.CurrentDirectory + ">";
        try
        {
            dock.Show(); window.Show();
            var tab = dock.SelectedTab!;
            WaitUntil(() => tab.Buffer.PlainText.Contains(prompt), "startup prompt");
            ExpectSinglePrompt("startup"); Capture("startup");
            foreach (var height in new[] { 413, 607, 280, 523, 188, 463 })
            {
                var oldHeight = scroller.Viewport.Height;
                view.Height = height; Pump(220);
                Check(Math.Abs(scroller.Viewport.Height - oldHeight) > 10, "live-dock-viewport-really-resized-" + height);
                WaitUntil(() => tab.Buffer.Rows == (int)(scroller.Viewport.Height / surface.CellHeight), "PTY matches height " + height);
                ExpectSinglePrompt("height-" + height);
            }
            for (var height = 463; height >= 167; height -= 8) { view.Height = height; Pump(5); }
            for (var height = 167; height < 490; height += 7) { view.Height = height; Pump(5); }
            Pump(400); ExpectSinglePrompt("rapid-drag"); Capture("after-drag");

            tab.SendRaw("1..32 | % { 'line-' + $_ }; 'dock-' + 'done'\r");
            WaitUntil(() => tab.Buffer.PlainText.Contains("\ndock-done\n" + prompt), "numbered output and next prompt");
            var history = tab.Buffer.PlainText;
            foreach (var height in new[] { 204, 580, 243, 508 }) { view.Height = height; Pump(260); }
            Check(history == tab.Buffer.PlainText, "live-dock-resize-preserves-all-command-history");
            Check(Enumerable.Range(1, 32).All(n => history.Split('\n').Count(s => s == "line-" + n) == 1), "live-dock-real-output-not-input-echo");

            tab.SendRaw("exit 7\r");
            WaitUntil(() => !tab.IsRunning, "shell exited");
            Check(tab.StatusText == "已退出 7", "live-dock-shell-exit-status");
            tab.Restart();
            WaitUntil(() => tab.Buffer.PlainText == prompt, "restarted shell shows clean prompt");
            tab.SendRaw("'alive-' + $PID\r");
            WaitUntil(() => tab.Buffer.PlainText.Contains("\nalive-") && tab.Buffer.PlainText.EndsWith(prompt), "restarted shell accepts input");
            var pid = int.Parse(tab.Buffer.PlainText.Split('\n').Single(s => s.StartsWith("alive-"))[6..]);
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            dock.CreateTerminal();
            var survivor = dock.SelectedTab!;
            WaitUntil(() => survivor.Buffer.PlainText == prompt, "second shell");
            view.ApplyDisplaySettings(dock.Settings);
            var tabs = view.FindControl<ListBox>("TerminalTabs")!;
            var close = tabs.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.DataContext, tab));
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(100);
            Check(dock.Tabs.Count == 1 && ReferenceEquals(dock.SelectedTab, survivor) && survivor.IsRunning && tab.IsDisposed,
                "live-dock-background-tab-x-closes-only-that-shell-with-confirmation-off");
            Check(process.WaitForExit(3000), "live-dock-closed-shell-process-is-gone");
            view.CloseSelectedAsync().GetAwaiter().GetResult();
            Check(!dock.IsVisible && !dock.HasTabs && survivor.IsDisposed, "live-dock-close-last-real-shell");

            void ExpectSinglePrompt(string stage)
            {
                var snapshot = tab.Snapshot;
                var origin = surface.TranslatePoint(new Point(0, TerminalScreenControl.OriginY), scroller)!.Value.Y;
                Console.WriteLine($"{stage}: viewport={scroller.Viewport}, grid={tab.Buffer.Columns}x{tab.Buffer.Rows}, history={snapshot.Lines.Length-tab.Buffer.Rows}, cursor={snapshot.CursorLine}, originY={origin:N1}");
                Check(tab.Buffer.PlainText == prompt && snapshot.CursorLine == 0 && RowText(snapshot, 0) == prompt,
                    "live-dock-single-prompt-first-row-" + stage);
                Check(origin >= -1 && origin < surface.CellHeight && scroller.Extent.Height <= scroller.Viewport.Height + 1,
                    "live-dock-first-row-visible-without-scrollbar-" + stage);
            }
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }

        void Pump(int ms)
        {
            // Run a real dispatcher loop: RunJobs alone doesn't service platform timers (resize debounce).
            using var interval = new CancellationTokenSource(ms);
            Dispatcher.UIThread.MainLoop(interval.Token);
            window.UpdateLayout();
        }
        void Capture(string stage)
        {
            if (Environment.GetEnvironmentVariable("FUFUCODE_TERMINAL_EVIDENCE") is not { Length: > 0 } dir) return;
            Directory.CreateDirectory(dir);
            using var bitmap = new RenderTargetBitmap(PixelSize.FromSize(view.Bounds.Size, 1));
            bitmap.Render(view); bitmap.Save(Path.Combine(dir, stage + ".png"), PngBitmapEncoderOptions.Default);
        }
        void WaitUntil(Func<bool> done, string description)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!done() && clock.Elapsed < TimeSpan.FromSeconds(30)) Pump(16);
            Check(done(), "live-dock-" + description);
        }
    }
}
