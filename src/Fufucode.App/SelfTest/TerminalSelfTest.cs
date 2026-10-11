using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;

namespace Fufucode.App.SelfTest;

public static class TerminalSelfTest
{
    /// <summary>Called by the existing UI selftest runner; this path never starts a real shell.</summary>
    public static void Run(Action<string, bool> check)
    {
        var buffer = new TerminalBuffer(40, 5);
        buffer.Append("hello\rworld\r\n\u001b[31mred\u001b[0m\r\n");
        check("terminal-vt-carriage-color", buffer.PlainText.StartsWith("world\nred", StringComparison.Ordinal) && buffer.Snapshot().Lines[1][0].Foreground == 0xE06C75);
        buffer.Append("abcdef\r\u001b[2Kok");
        check("terminal-vt-erase-line", buffer.PlainText.EndsWith("ok", StringComparison.Ordinal));
        buffer.Append("\u001b["); buffer.Append("32m绿\u001b[0m");
        check("terminal-vt-split-sequence-wide", buffer.PlainText.EndsWith("ok绿", StringComparison.Ordinal) && buffer.Snapshot().Lines[2][2].Foreground == 0x98C379 && buffer.Snapshot().Lines[2][3].Text == string.Empty);
        for (var i = 0; i < 1700; i++) buffer.Append("\r\nrow");
        check("terminal-scrollback-bounded", buffer.Snapshot().Lines.Length <= 1505);
        buffer.Resize(60, 8);
        check("terminal-buffer-resize", buffer.Columns == 60 && buffer.Rows == 8 && buffer.PlainText.Contains("row", StringComparison.Ordinal));
        var restoredCursor = new TerminalBuffer(80, 12);
        restoredCursor.Append("\u001b[12;80H\u001b7");
        restoredCursor.Resize(10, 3);
        restoredCursor.Append("\u001b8A");
        var restoredSnapshot = restoredCursor.Snapshot();
        check("terminal-esc-cursor-restore-after-shrink", restoredCursor.PlainText.Contains("A", StringComparison.Ordinal) && restoredSnapshot.CursorColumn <= restoredCursor.Columns && restoredSnapshot.CursorLine < restoredSnapshot.Lines.Length);

        using var dock = new TerminalDockViewModel(useMock: true);
        dock.Show();
        var original = dock.SelectedTab!;
        var originalText = original.Buffer.PlainText;
        dock.Hide(); dock.Show();
        check("terminal-hide-reopen-preserves-output", ReferenceEquals(original, dock.SelectedTab) && original.Buffer.PlainText == originalText && original.IsRunning);
        dock.CreateTerminal();
        check("terminal-independent-tab-ids", dock.Tabs.Count == 2 && dock.Tabs[0].TerminalSessionId != dock.Tabs[1].TerminalSessionId);
        var second = dock.SelectedTab!;
        second.InputText = "echo demo"; second.SendInput();
        check("terminal-mock-input-is-explicit", second.Buffer.PlainText.Contains("未执行命令", StringComparison.Ordinal) && second.InputText.Length == 0 && !original.Buffer.PlainText.Contains("echo demo", StringComparison.Ordinal));
        dock.CloseTerminal(second);
        check("terminal-close-keeps-other-tab", dock.Tabs.Count == 1 && ReferenceEquals(original, dock.SelectedTab) && !second.IsRunning && original.IsRunning);
        dock.CloseTerminal(original);
        check("terminal-last-close-hides", dock.Tabs.Count == 0 && !dock.IsVisible && !original.IsRunning);
    }

    /// <summary>Run on the headless UI thread. Exercises queued Loaded callbacks with real scroll view layout.</summary>
    public static async Task RunScrollIsolationAsync(Action<string, bool> check)
    {
        using var dock = new TerminalDockViewModel(useMock: true);
        for (var i = 0; i < 3; i++) dock.CreateTerminal();
        foreach (var tab in dock.Tabs)
            for (var row = 0; row < 80; row++) tab.Buffer.Append("\r\nscroll-row-" + row);
        var a = dock.Tabs[0]; var b = dock.Tabs[1]; var c = dock.Tabs[2];
        a.FollowOutput = true;
        b.FollowOutput = false; b.ScrollOffsetY = 45;
        c.FollowOutput = false; c.ScrollOffsetY = 90;
        dock.SelectedTab = a;
        var view = new TerminalDockView { DataContext = dock };
        var window = new Window { Width = 900, Height = 280, Content = view };
        window.Show();
        try
        {
            await Task.Delay(100);
            var scroller = view.FindControl<ScrollViewer>("OutputScroller")!;
            dock.SelectedTab = b;
            dock.SelectedTab = c; // Deliberately no dispatcher drain between switches.
            await Task.Delay(100);
            check("terminal-rapid-tab-switch-keeps-inactive-offset", Math.Abs(b.ScrollOffsetY - 45) < 1);
            check("terminal-rapid-tab-switch-restores-final-offset", Math.Abs(scroller.Offset.Y - 90) < 1 && !c.FollowOutput);
            dock.SelectedTab = a;
            await Task.Delay(80);
            a.Buffer.Append("\r\nqueued-new-output"); // Queues ScrollToEnd for A.
            dock.SelectedTab = b;
            await Task.Delay(100);
            check("terminal-old-output-callback-does-not-scroll-new-tab", Math.Abs(scroller.Offset.Y - 45) < 1 && !b.FollowOutput);
        }
        finally { window.Close(); }
    }

    /// <summary>Separate opt-in real Windows PTY check. Verifies command output, Unicode, resize and process exit.</summary>
    public static async Task<string> RunConPtySmokeAsync(string workingDirectory)
    {
        if (!OperatingSystem.IsWindows()) return "SKIP: Windows ConPTY is unavailable on this platform.";
        using var session = new ConPtyTerminalSession();
        var collected = new TerminalBuffer(80, 15);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object();
        session.Output += text =>
        {
            lock (gate)
            {
                collected.Append(text);
                if (collected.PlainText.Contains("R2-PTY-OUTPUT-验证", StringComparison.Ordinal)) completed.TrySetResult(true);
            }
        };
        session.Exited += (code, error) => { if (error is not null) completed.TrySetException(new InvalidOperationException(error)); exited.TrySetResult(code); };
        session.Start(workingDirectory, 80, 15);
        // Avoid a false pass from terminal input echo: the expected text is assembled by cmd variable expansion.
        session.Write("set R2_TEST_TOKEN=R2-PTY-OUTPUT-\r");
        session.Write("echo %R2_TEST_TOKEN%验证\r");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(12));
        session.Resize(100, 20);
        session.Write("exit\r");
        var exitCode = await exited.Task.WaitAsync(TimeSpan.FromSeconds(12));
        if (exitCode != 0) throw new InvalidOperationException("PTY shell exit code: " + exitCode);
        return "PASS: real ConPTY cmd command output + Unicode + resize + exit(0).";
    }
}
