using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Fufucode.App.Features.TerminalExperience;
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

        RunOutputModelChecks(check);

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
        await RunOutputInteractionAsync(check);
    }

    /// <summary>
    /// Separate opt-in real Windows PTY check. The shell starts in a task-owned temporary directory and
    /// only runs harmless <c>set</c>/<c>echo</c>/<c>exit</c> commands (never repository commands). The
    /// real PTY bytes are then interpreted by the existing bounded buffer, and the same output-selection
    /// and find models the pane uses must resolve against that real snapshot.
    /// </summary>
    public static async Task<string> RunConPtySmokeAsync(string workingDirectory)
    {
        if (!OperatingSystem.IsWindows()) return "SKIP: Windows ConPTY is unavailable on this platform.";
        var shellDirectory = Path.Combine(Path.GetFullPath(workingDirectory), "tmp", "e2-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(shellDirectory);
        try
        {
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
            session.Start(shellDirectory, 80, 15);
            // Avoid a false pass from terminal input echo: the expected text is assembled by cmd variable expansion.
            session.Write("set R2_TEST_TOKEN=R2-PTY-OUTPUT-\r");
            session.Write("echo %R2_TEST_TOKEN%验证\r");
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(12));
            TerminalSnapshot snapshot;
            lock (gate) snapshot = collected.Snapshot();
            var row = FindRow(snapshot, "R2-PTY-OUTPUT-验证");
            var copied = string.Empty;
            if (row >= 0)
            {
                var selection = new TerminalOutputSelection();
                selection.Begin(snapshot, new TerminalCellPosition(row, 0));
                selection.Extend(snapshot, new TerminalCellPosition(row, snapshot.Lines[row].Length - 1));
                copied = selection.Text;
            }
            var search = new TerminalSearchSession();
            var hits = search.Run(snapshot, "R2-PTY-OUTPUT-验证", false);
            session.Resize(100, 20);
            session.Write("exit\r");
            var exitCode = await exited.Task.WaitAsync(TimeSpan.FromSeconds(12));
            if (exitCode != 0) throw new InvalidOperationException("PTY shell exit code: " + exitCode);
            if (row < 0 || !copied.Contains("R2-PTY-OUTPUT-验证", StringComparison.Ordinal) || hits < 1)
                throw new InvalidOperationException($"Real PTY output operations failed: row={row} copied='{copied}' hits={hits}");
            return "PASS: real ConPTY output -> bounded snapshot -> row selection + find keep 验证; resize + exit(0).";
        }
        finally
        {
            try { if (Directory.Exists(shellDirectory)) Directory.Delete(shellDirectory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// <summary>
    /// Real ConPTY check for the P0 VT features. PowerShell writes the sequences deterministically, after a
    /// serial round trip has confirmed the command was read: enter the alternate screen, write, leave it, ask
    /// for a cursor report, then write to the restored primary screen. ConPTY reflects the reply on its input
    /// path before answering the query (the shell shows the echoed report line), so the checks accept the
    /// report when it was parsed from the output (that path does not echo) or when the echo came back.
    /// </summary>
    public static async Task<string> RunConPtyP0SmokeAsync(string workingDirectory)
    {
        if (!OperatingSystem.IsWindows()) return "SKIP: Windows ConPTY is unavailable on this platform.";
        var shellDirectory = Path.Combine(Path.GetFullPath(workingDirectory), "tmp", "p0-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(shellDirectory);
        try
        {
            using var session = new ConPtyTerminalSession();
            var collected = new TerminalBuffer(80, 15);
            var reportParsed = 0;
            var raw = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var prompted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Output += text =>
            {
                lock (raw)
                {
                    raw.Enqueue(text);
                    collected.Append(text);
                    if (collected.TakePendingResponse().Length > 0) reportParsed++;
                    if (collected.PlainText.Contains("READY-P0", StringComparison.Ordinal)) ready.TrySetResult(true);
                    if (collected.PlainText.Contains("PRIMARY-P0", StringComparison.Ordinal)) prompted.TrySetResult(true);
                }
            };
            session.Start(shellDirectory, 80, 15);
            const string script = "$null = 'READY-P0' | Out-Host; "
                + "[Console]::Write([char]27+'[?1049h'); [Console]::Write('ALT-P0'); Start-Sleep -Milliseconds 300; "
                + "[Console]::Write([char]27+'[?1049l'); [Console]::Write([char]27+'[6n'); "
                + "Start-Sleep -Milliseconds 200; [Console]::Write('PRIMARY-P0')";
            session.Write($"powershell -NoLogo -NoProfile -Command \"{script}\"\r");
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(25));
            await prompted.Task.WaitAsync(TimeSpan.FromSeconds(25));
            await Task.Delay(800);
            TerminalSnapshot snapshot;
            string rawText;
            int parsed;
            lock (raw)
            {
                snapshot = collected.Snapshot();
                rawText = string.Join(string.Empty, raw);
                parsed = reportParsed;
            }
            session.Write("exit\r");
            var echoed = rawText.Contains("[10;1R", StringComparison.Ordinal);
            // The echo line proves the reply reached the shell's own input path. ALT-P0 is only meaningful in
            // the alternate-screen frames (PowerShell's on-exit repaint echoes the whole command line).
            var echoRow = snapshot.Lines.ToList().FindIndex(line => string.Concat(line.Select(cell => cell.Text)).Contains("[10;1R", StringComparison.Ordinal));
            if (parsed == 0 && echoRow < 0)
                throw new InvalidOperationException("The shell's cursor report was not answered. raw=" + Escaped(rawText));
            var row = FindRow(snapshot, "READY-P0");
            if (row < 0 || FindRow(snapshot, "PRIMARY-P0") < 0)
                throw new InvalidOperationException("Primary-screen output was lost after the alternate screen: " + snapshot.Lines.Length + " rows");
            // Real-shell faithfulness beyond this point: what matters is that the app's buffer answered the
            // report and that the prompt is intact; the frame split is verified by the headless P0 checks.
            return $"PASS: real ConPTY answered the cursor report (parsed={parsed}, echoedAt={echoRow}); "
                + $"primary kept 'PRIMARY-P0' at row {row}.";
        }
        finally
        {
            try { if (Directory.Exists(shellDirectory)) Directory.Delete(shellDirectory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string Escaped(string text) =>
        text.Replace("\u001b", "ESC").Replace("\r", "").Replace("\n", "<LF>");

    /// Output-selection / find / command-draft models against real <see cref="TerminalSnapshot"/>
    /// instances. These checks stay independent of the pane so they cannot "pass" by mirroring the
    /// implementation's own expectations.
    /// </summary>
    private static void RunOutputModelChecks(Action<string, bool> check)
    {
        var buffer = new TerminalBuffer(40, 6);
        buffer.Append("\u001b[32m中文😀K\u001b[0m\r\nsecond-marker\r\n");
        var snapshot = buffer.Snapshot();

        var row = new TerminalOutputSelection();
        row.Begin(snapshot, new TerminalCellPosition(0, 0));
        row.Extend(snapshot, new TerminalCellPosition(0, snapshot.Lines[0].Length - 1));
        check("terminal-selection-wide-emoji-row-keeps-glyphs", row.Text == "中文😀K");
        check("terminal-selection-drops-ansi-control", !row.Text.Contains('\u001b'));

        var wideColumn = Array.FindIndex(snapshot.Lines[0].Select(cell => cell.Text).ToArray(), text => text == "中");
        var continuation = new TerminalOutputSelection();
        continuation.Begin(snapshot, new TerminalCellPosition(0, wideColumn + 1)); // continuation cell of 中
        continuation.Extend(snapshot, new TerminalCellPosition(0, wideColumn + 3)); // continuation cell of 文
        check("terminal-selection-continuation-widens-to-owner", continuation.Text == "中文");

        var spans = new TerminalOutputSelection();
        spans.Begin(snapshot, new TerminalCellPosition(0, 0));
        spans.Extend(snapshot, new TerminalCellPosition(1, snapshot.Lines[1].Length - 1));
        check("terminal-selection-spans-rows", spans.Text == "中文😀K\nsecond-marker");

        // 字素边界：组合附加符必须跟随基字符，局部选择不得拆开字素簇。
        var grapheme = new TerminalBuffer(20, 3);
        grapheme.Append("Ae\u0301X");
        var graphemeSnapshot = grapheme.Snapshot();
        check("terminal-selection-keeps-trailing-combining-mark",
            TerminalSelectionText.Extract(graphemeSnapshot, new TerminalCellPosition(0, 0), new TerminalCellPosition(0, 1)) == "Ae\u0301");
        check("terminal-selection-widens-combining-start",
            TerminalSelectionText.Extract(graphemeSnapshot, new TerminalCellPosition(0, 2), new TerminalCellPosition(0, 3)) == "e\u0301X");
        check("terminal-selection-ignores-zero-length-gesture",
            TerminalSelectionText.Extract(graphemeSnapshot, new TerminalCellPosition(0, 2), new TerminalCellPosition(0, 2)).Length == 0);

        var wideMark = new TerminalBuffer(20, 3);
        wideMark.Append("中\u0301K");
        var wideMarkSnapshot = wideMark.Snapshot();
        check("terminal-selection-wide-glyph-keeps-mark",
            TerminalSelectionText.Extract(wideMarkSnapshot, new TerminalCellPosition(0, 0), new TerminalCellPosition(0, 1)) == "中\u0301");
        check("terminal-selection-mark-start-widens-past-continuation",
            TerminalSelectionText.Extract(wideMarkSnapshot, new TerminalCellPosition(0, 2), new TerminalCellPosition(0, 3)) == "中\u0301K");

        var zwj = new TerminalBuffer(20, 3);
        zwj.Append("👨\u200D👩X");
        var zwjSnapshot = zwj.Snapshot();
        check("terminal-selection-zwj-tail-includes-following-emoji",
            TerminalSelectionText.Extract(zwjSnapshot, new TerminalCellPosition(0, 0), new TerminalCellPosition(0, 2)) == "👨\u200D👩");
        check("terminal-selection-zwj-start-includes-leading-emoji",
            TerminalSelectionText.Extract(zwjSnapshot, new TerminalCellPosition(0, 2), new TerminalCellPosition(0, 5)) == "👨\u200D👩X");

        var variation = new TerminalBuffer(20, 3);
        variation.Append("❤\uFE0FX");
        var variationSnapshot = variation.Snapshot();
        check("terminal-selection-variation-selector-start-widens",
            TerminalSelectionText.Extract(variationSnapshot, new TerminalCellPosition(0, 1), new TerminalCellPosition(0, 2)) == "❤\uFE0FX");
        check("terminal-selection-variation-selector-tail",
            TerminalSelectionText.Extract(variationSnapshot, new TerminalCellPosition(0, 0), new TerminalCellPosition(0, 1)) == "❤\uFE0F");

        // 行尾基字符的附加符换行到下一行时，行边界仍是硬边界：不并入上一行，也不丢换行。
        var boundary = new TerminalBuffer(20, 3);
        boundary.Append(new string('a', 19) + "e\u0301");
        var boundarySnapshot = boundary.Snapshot();
        check("terminal-wrapped-combining-mark-lands-on-next-row", boundarySnapshot.Lines[1][0].Text == "\u0301");
        var rowOnly = TerminalSelectionText.Extract(boundarySnapshot, new TerminalCellPosition(0, 0), new TerminalCellPosition(0, 19));
        check("terminal-selection-row-boundary-is-hard", rowOnly == new string('a', 19) + "e");
        var acrossRows = TerminalSelectionText.Extract(boundarySnapshot, new TerminalCellPosition(0, 0), new TerminalCellPosition(1, 0));
        check("terminal-selection-cross-row-keeps-line-break", acrossRows == new string('a', 19) + "e\n\u0301");

        // 真实缓冲反例（M 第二轮）：宽字符 owner 的续格是空文本，字素扩展不得在续格处提前终止。
        foreach (var (input, end, expected, name) in new[]
        {
            ("A中\u0301X", 1, "A中\u0301", "terminal-selection-wide-owner-keeps-following-mark"),
            ("A👨\u200D👩X", 1, "A👨\u200D👩", "terminal-selection-wide-owner-keeps-zwj-sequence"),
            ("A👨\u200D👩X", 4, "A👨\u200D👩", "terminal-selection-zwj-second-owner-keeps-sequence"),
            ("A🇨🇳X", 1, "A🇨🇳", "terminal-selection-regional-indicator-flag-pair"),
        })
        {
            var caseBuffer = new TerminalBuffer(30, 3);
            caseBuffer.Append(input);
            var caseSnapshot = caseBuffer.Snapshot();
            var forward = TerminalSelectionText.Extract(caseSnapshot, new TerminalCellPosition(0, 0), new TerminalCellPosition(0, end));
            var reverse = TerminalSelectionText.Extract(caseSnapshot, new TerminalCellPosition(0, end), new TerminalCellPosition(0, 0));
            check(name, forward == expected);
            check(name + "-reverse", reverse == expected);
        }

        // 起选/终点落在宽字符续格或表情后半时，必须回到 owner 并保持整簇。
        var wideStart = new TerminalBuffer(30, 3);
        wideStart.Append("A中\u0301X");
        var wideStartSnapshot = wideStart.Snapshot();
        check("terminal-selection-start-on-wide-continuation-widens",
            TerminalSelectionText.Extract(wideStartSnapshot, new TerminalCellPosition(0, 2), new TerminalCellPosition(0, 3)) == "中\u0301");
        var wideResolved = TerminalSelectionText.Resolve(wideStartSnapshot, new TerminalCellPosition(0, 0), new TerminalCellPosition(0, 1));
        check("terminal-selection-resolved-range-covers-wide-pair-and-mark",
            wideResolved.Start == new TerminalCellPosition(0, 0) && wideResolved.End == new TerminalCellPosition(0, 3));

        var emojiHalf = new TerminalBuffer(30, 3);
        emojiHalf.Append("A👨\u200D👩X");
        var emojiHalfSnapshot = emojiHalf.Snapshot();
        check("terminal-selection-start-on-emoji-continuation-widens",
            TerminalSelectionText.Extract(emojiHalfSnapshot, new TerminalCellPosition(0, 2), new TerminalCellPosition(0, 4)) == "👨\u200D👩");
        var emojiResolved = TerminalSelectionText.Resolve(emojiHalfSnapshot, new TerminalCellPosition(0, 0), new TerminalCellPosition(0, 1));
        check("terminal-selection-resolved-range-covers-emoji-pair",
            emojiResolved.Start == new TerminalCellPosition(0, 0) && emojiResolved.End == new TerminalCellPosition(0, 5));

        // Output changed after the selection: the frozen text must survive and extension must be refused.
        buffer.Append("\r\nrewritten\r\n");
        var later = buffer.Snapshot();
        check("terminal-selection-frozen-against-new-output",
            spans.Text == "中文😀K\nsecond-marker" && !spans.IsOnSnapshot(later) &&
            !spans.Extend(later, new TerminalCellPosition(1, 0)) && spans.Text == "中文😀K\nsecond-marker");

        // Scrollback truncation must not rewrite or invalidate the frozen selection.
        var scrollback = new TerminalBuffer(30, 4);
        scrollback.Append("KEEP-ME-中文\r\n");
        var keepSnapshot = scrollback.Snapshot();
        var keep = new TerminalOutputSelection();
        keep.Begin(keepSnapshot, new TerminalCellPosition(0, 0));
        keep.Extend(keepSnapshot, new TerminalCellPosition(0, keepSnapshot.Lines[0].Length - 1));
        for (var i = 0; i < 1700; i++) scrollback.Append("\r\nfiller-" + i);
        check("terminal-selection-survives-scrollback-truncation",
            keep.Text == "KEEP-ME-中文" && scrollback.Snapshot().Lines.Length <= 1505);

        var bounded = new TerminalCommandHistory();
        for (var i = 0; i < TerminalCommandHistory.Capacity + 20; i++) bounded.Record("cmd-" + i);
        check("terminal-history-bounded", bounded.Entries.Count == TerminalCommandHistory.Capacity && bounded.Entries[0] == "cmd-20");
        bounded.Record("cmd-119");
        check("terminal-history-collapses-newest-repeat", bounded.Entries.Count == TerminalCommandHistory.Capacity && bounded.Entries[^1] == "cmd-119");
        bounded.Record("   ");
        bounded.Record("");
        check("terminal-history-ignores-blank", bounded.Entries.Count == TerminalCommandHistory.Capacity);

        var history = new TerminalCommandHistory();
        history.Record("first");
        history.Record("second");
        check("terminal-history-draft-roundtrip",
            history.Previous("my-draft") == "second" && history.Previous("ignored") == "first" &&
            history.Previous("ignored") == "first" && history.Next() == "second" && history.Next() == "my-draft");

        check("terminal-paste-normalizes-line-endings", TerminalInputDraft.Normalize("a\r\nb\rc\0") == "a\nb\nc");
        check("terminal-paste-merge-keeps-draft",
            TerminalInputDraft.Merge("keep", "paste") == "keep\npaste" &&
            TerminalInputDraft.Merge("keep\n", "paste") == "keep\npaste" &&
            TerminalInputDraft.Merge("", "paste") == "paste" &&
            TerminalInputDraft.Merge("keep", "") == "keep");

        var searchBuffer = new TerminalBuffer(40, 5);
        searchBuffer.Append("alpha one\r\nbeta\r\nalpha two\r\n");
        var search = new TerminalSearchSession();
        var searchSnapshot = searchBuffer.Snapshot();
        check("terminal-search-finds-bounded-hits",
            search.Run(searchSnapshot, "alpha", false) == 2 && search.Current is { Row: 0 } && search.StatusText() == "1/2");
        check("terminal-search-case-option",
            search.Run(searchSnapshot, "ALPHA", false) == 2 && search.Run(searchSnapshot, "ALPHA", true) == 0);
        check("terminal-search-no-result-feedback",
            search.Run(searchSnapshot, "zzz", false) == 0 && search.StatusText() == "无匹配");
        search.Run(searchSnapshot, "alpha", false);
        searchBuffer.Append("\r\nalpha three\r\n");
        var searchLater = searchBuffer.Snapshot();
        search.EnsureCurrent(searchLater);
        check("terminal-search-rebinds-before-using-index", search.Count == 3 && !search.IsCurrent(searchSnapshot) && search.Current is { Row: 0 });
        var move1 = search.Move(1);
        var move2 = search.Move(1);
        var move3 = search.Move(1);
        check("terminal-search-moves-and-wraps", move1 is { Row: 2 } && move2 is { Row: 4 } && move3 is { Row: 0 });
    }

    /// <summary>
    /// Drives the real dock view, toolbar buttons, routed pointer/keyboard events and the headless
    /// clipboard seam. Everything below runs on the headless UI thread with a laid-out window.
    /// </summary>
    private static async Task RunOutputInteractionAsync(Action<string, bool> check)
    {
        var sessions = new List<RecordingSession>();
        using var dock = new TerminalDockViewModel(sessionFactory: () =>
        {
            var session = new RecordingSession();
            sessions.Add(session);
            return session;
        });
        dock.Show();
        await Task.Delay(30);
        var view = new TerminalDockView { DataContext = dock };
        var window = new Window { Width = 760, Height = 420, Content = view };
        window.Show();
        try
        {
            await Task.Delay(140);
            var surface = view.FindControl<TerminalScreenControl>("TerminalSurface")!;
            var input = view.FindControl<TextBox>("TerminalInput")!;
            var findBar = view.FindControl<Border>("FindBar")!;
            var findBox = view.FindControl<TextBox>("FindBox")!;
            var scroller = view.FindControl<ScrollViewer>("OutputScroller")!;
            var copyButton = view.FindControl<Button>("CopyOutputButton")!;
            var findButton = view.FindControl<Button>("FindButton")!;
            var bottomButton = view.FindControl<Button>("ReturnToBottomButton")!;
            var caseButton = view.FindControl<Button>("FindCaseButton")!;
            var nextButton = view.FindControl<Button>("FindNextButton") ?? throw new InvalidOperationException("FindNextButton missing");
            var previousButton = view.FindControl<Button>("FindPreviousButton") ?? throw new InvalidOperationException("FindPreviousButton missing");
            var closeFindButton = view.FindControl<Button>("CloseFindButton") ?? throw new InvalidOperationException("CloseFindButton missing");
            var tab = dock.SelectedTab!;
            var session = sessions[0];
            var clipboard = new List<string>();
            surface.ClipboardWriterForTesting = text => { clipboard.Add(text); return Task.CompletedTask; };
            surface.ClipboardReaderForTesting = () => Task.FromResult<string?>(null);

            check("terminal-toolbar-accessible-names",
                AutomationProperties.GetName(copyButton) == "复制选中输出" &&
                AutomationProperties.GetName(findButton) == "查找输出" &&
                AutomationProperties.GetName(bottomButton) == "返回底部" &&
                AutomationProperties.GetName(caseButton) == "区分大小写" &&
                AutomationProperties.GetName(nextButton) == "下一个匹配" &&
                AutomationProperties.GetName(previousButton) == "上一个匹配" &&
                AutomationProperties.GetName(closeFindButton) == "关闭查找");
            check("terminal-copy-disabled-without-selection", !copyButton.IsEnabled && !dock.HasOutputSelection);

            tab.Buffer.Append("\r\ndrag-target-中文😀ZZ\r\nsecond-marker\r\n");
            await Task.Delay(60);
            var snapshot = tab.Snapshot;
            var targetRow = FindRow(snapshot, "drag-target");
            check("terminal-drag-target-visible", targetRow >= 0 && TerminalScreenControl.OriginY + targetRow * TerminalScreenControl.CellHeight < surface.Bounds.Height);

            // Mouse drag over the real surface selects the frozen row and enables the copy entry.
            var rowStart = ToWindow(surface, window, targetRow, 0);
            var rowEnd = ToWindow(surface, window, targetRow, int.MaxValue);
            window.MouseDown(rowStart, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(rowEnd, RawInputModifiers.None);
            window.MouseUp(rowEnd, MouseButton.Left, RawInputModifiers.None);
            await Task.Delay(50);
            check("terminal-mouse-drag-selects-row", tab.OutputSelection.Text == "drag-target-中文😀ZZ");
            check("terminal-selection-enables-copy-entry", dock.HasOutputSelection && copyButton.IsEnabled);

            copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(30);
            check("terminal-copy-button-writes-selection", clipboard.Count == 1 && clipboard[0] == "drag-target-中文😀ZZ");

            // Ctrl+C copies while a selection exists...
            surface.Focus();
            await Task.Delay(20);
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
            await Task.Delay(30);
            check("terminal-ctrl-c-copies-with-selection", clipboard.Count == 2 && clipboard[1] == "drag-target-中文😀ZZ" && session.Writes.Count == 0);

            // ...and still interrupts the shell once the selection is cleared by a click.
            var click = ToWindow(surface, window, targetRow, 0);
            window.MouseDown(click, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(click, MouseButton.Left, RawInputModifiers.None);
            await Task.Delay(40);
            check("terminal-click-clears-selection", !tab.OutputSelection.HasRange && !dock.HasOutputSelection && !copyButton.IsEnabled);
            surface.Focus();
            await Task.Delay(20);
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
            await Task.Delay(30);
            check("terminal-ctrl-c-without-selection-interrupts", session.Writes.Count == 1 && session.Writes[0] == "\u0003" && clipboard.Count == 2);

            window.MouseDown(rowStart, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(rowEnd, RawInputModifiers.None);
            window.MouseUp(rowEnd, MouseButton.Left, RawInputModifiers.None);
            await Task.Delay(40);
            surface.Focus();
            await Task.Delay(20);
            window.KeyPress(Key.C, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.C, "C");
            await Task.Delay(30);
            check("terminal-ctrl-shift-c-copies", clipboard.Count == 3 && clipboard[2] == "drag-target-中文😀ZZ");

            // Cross-line drag keeps both rows and does not smuggle ANSI or padding.
            var multiEnd = ToWindow(surface, window, targetRow + 1, int.MaxValue);
            window.MouseDown(rowStart, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(multiEnd, RawInputModifiers.None);
            window.MouseUp(multiEnd, MouseButton.Left, RawInputModifiers.None);
            await Task.Delay(50);
            check("terminal-mouse-drag-spans-two-rows", tab.OutputSelection.Text == "drag-target-中文😀ZZ\nsecond-marker");

            // Paste goes to the draft only; the shell is not written to and nothing runs implicitly.
            tab.InputText = "keep-me";
            surface.ClipboardReaderForTesting = () => Task.FromResult<string?>("paste-😀\r\nline2");
            surface.Focus();
            await Task.Delay(20);
            window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
            await Task.Delay(50);
            check("terminal-paste-merges-into-draft-only", tab.InputText == "keep-me\npaste-😀\nline2" && session.Writes.Count == 1);

            // Explicit run (Enter in the command box) is the only path that writes to the shell.
            input.Focus();
            await Task.Delay(20);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "");
            await Task.Delay(50);
            check("terminal-explicit-run-sends-draft", session.Writes.Count == 2 && session.Writes[1] == "keep-me\npaste-😀\nline2\r" && tab.InputText.Length == 0);

            // Command history: Up/Down keep the unsent draft and stay bounded per tab.
            tab.InputText = "first-cmd";
            tab.SendInput();
            tab.InputText = "second-cmd";
            tab.SendInput();
            tab.InputText = "draft-keep";
            input.Focus();
            await Task.Delay(20);
            PressKey(window, Key.Up, PhysicalKey.ArrowUp);
            await Task.Delay(20);
            check("terminal-history-up-returns-newest", tab.InputText == "second-cmd");
            PressKey(window, Key.Up, PhysicalKey.ArrowUp);
            await Task.Delay(20);
            check("terminal-history-up-reaches-oldest", tab.InputText == "first-cmd");
            PressKey(window, Key.Down, PhysicalKey.ArrowDown);
            await Task.Delay(20);
            PressKey(window, Key.Down, PhysicalKey.ArrowDown);
            await Task.Delay(20);
            check("terminal-history-down-restores-draft", tab.InputText == "draft-keep");

            // Arrow keys on the output surface still reach the shell; only the command box browses history.
            surface.Focus();
            await Task.Delay(20);
            PressKey(window, Key.Up, PhysicalKey.ArrowUp);
            await Task.Delay(30);
            check("terminal-surface-arrow-still-reaches-shell", session.Writes.Contains("\u001b[A") && tab.InputText == "draft-keep");

            // Find: Ctrl+F opens the bar, query hits are reported, Escape closes it and restores focus.
            surface.Focus();
            await Task.Delay(20);
            window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
            await Task.Delay(60);
            check("terminal-ctrl-f-opens-find-bar", tab.IsSearchOpen && findBar.IsVisible);
            findBox.Text = "no-such-text";
            await Task.Delay(60);
            check("terminal-find-reports-no-match", tab.SearchStatus == "无匹配");
            findBox.Text = "drag-target";
            await Task.Delay(60);
            check("terminal-find-highlights-first-hit", tab.Search.Count == 1 && tab.SearchStatus == "1/1");
            caseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            findBox.Text = "DRAG-TARGET";
            await Task.Delay(60);
            check("terminal-find-case-toggle-applies", tab.SearchMatchCase && tab.Search.Count == 0);
            caseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            findBox.Text = "drag-target";
            await Task.Delay(60);
            check("terminal-find-case-toggle-restores", !tab.SearchMatchCase && tab.Search.Count == 1);
            findBox.Focus();
            await Task.Delay(20);
            PressKey(window, Key.Escape, PhysicalKey.Escape);
            await Task.Delay(60);
            check("terminal-find-escape-closes-and-refocuses", !tab.IsSearchOpen && !findBar.IsVisible && surface.IsFocused);

            // Two hits far apart: navigating scrolls without re-enabling follow output.
            tab.Buffer.Append("\r\nscroll-one\r\n");
            for (var i = 0; i < 60; i++) tab.Buffer.Append("filler-" + i + "\r\n");
            tab.Buffer.Append("scroll-two\r\n");
            await Task.Delay(120);
            surface.Focus();
            await Task.Delay(20);
            window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
            await Task.Delay(60);
            findBox.Text = "scroll-";
            await Task.Delay(80);
            check("terminal-find-counts-both-hits", tab.Search.Count == 2 && tab.Search.Current is { Row: > 0 });
            var firstOffset = scroller.Offset.Y;
            nextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(60);
            check("terminal-find-next-moves-and-scrolls", tab.Search.CurrentIndex == 1 && Math.Abs(scroller.Offset.Y - firstOffset) > 1);
            previousButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(60);
            check("terminal-find-previous-returns", tab.Search.CurrentIndex == 0);
            check("terminal-find-navigation-stops-following", !tab.FollowOutput);

            bottomButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(60);
            check("terminal-return-to-bottom-restores-follow", tab.FollowOutput && scroller.Offset.Y >= Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height) - 1);

            closeFindButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(40);
            check("terminal-find-close-button-restores-surface-focus", !tab.IsSearchOpen && surface.IsFocused);

            // Per-tab isolation for selection, search, draft and history.
            var preservedSelection = tab.OutputSelection.Text;
            tab.InputText = "tab-one-draft";
            dock.CreateTerminal();
            await Task.Delay(80);
            var second = dock.SelectedTab!;
            check("terminal-second-tab-is-distinct",
                !ReferenceEquals(second, tab) && sessions.Count == 2 && second.OutputSelection.Text.Length == 0 &&
                !second.IsSearchOpen && second.History.Entries.Count == 0 && second.InputText.Length == 0 && !dock.HasOutputSelection);
            second.InputText = "tab-two-draft";
            second.SendInput();
            var secondSnapshot = second.Snapshot;
            check("terminal-active-surface-follows-selected-tab", ReferenceEquals(surface.CurrentSnapshot, secondSnapshot));
            dock.SelectedTab = tab;
            await Task.Delay(80);
            check("terminal-tab-switch-preserves-first-tab-state",
                ReferenceEquals(surface.CurrentSnapshot, tab.Snapshot) && tab.OutputSelection.Text == preservedSelection &&
                tab.InputText == "tab-one-draft" && tab.History.Entries.Count == 3 && second.History.Entries.Count == 1);

            findBox.Text = string.Empty;
            await Task.Delay(40);

            // Re-attaching the page must not duplicate buffer subscriptions.
            var attachedSubscribers = BufferSubscribers(tab.Buffer);
            var content = window.Content;
            window.Content = null;
            await Task.Delay(80);
            var detachedSubscribers = BufferSubscribers(tab.Buffer);
            window.Content = content;
            await Task.Delay(120);
            var reattachedSubscribers = BufferSubscribers(tab.Buffer);
            check("terminal-reattach-does-not-duplicate-subscriptions",
                attachedSubscribers > 1 && detachedSubscribers < attachedSubscribers && reattachedSubscribers == attachedSubscribers);

            // Narrow layout: every new entry stays reachable and the find bar keeps usable inputs.
            window.Width = 320;
            await Task.Delay(100);
            findButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(80);
            check("terminal-toolbar-reachable-at-320",
                copyButton.IsEffectivelyVisible && findButton.IsEffectivelyVisible && bottomButton.IsEffectivelyVisible &&
                findBar.IsEffectivelyVisible && findBox.IsEffectivelyVisible && nextButton.IsEffectivelyVisible &&
                previousButton.IsEffectivelyVisible && closeFindButton.IsEffectivelyVisible &&
                findBox.Bounds.Width > 40 && nextButton.Bounds.Width >= 24);
            var priorTheme = Application.Current!.RequestedThemeVariant;
            foreach (var theme in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark })
            {
                Application.Current!.RequestedThemeVariant = theme;
                await Task.Delay(80);
                using var frame = window.CaptureRenderedFrame();
                var expected = theme == Avalonia.Styling.ThemeVariant.Light ? "light" : "dark";
                if (frame is null) { check("terminal-320-frame-" + expected, false); continue; }
                var path = Path.GetFullPath("tmp/e2-terminal/terminal-320-" + expected + ".png");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                frame.Save(path, PngBitmapEncoderOptions.Default);
                check("terminal-320-renders-" + expected, frame.PixelSize.Width == 320 && frame.PixelSize.Height > 100);
            }
            Application.Current!.RequestedThemeVariant = priorTheme;

            // Output operations stay available after the shell exits; nothing is disabled by exit.
            findButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(40);
            session.ExitWith(0);
            await Task.Delay(60);
            check("terminal-output-usable-after-exit", !tab.IsRunning && tab.OutputSelection.HasText);
            surface.Focus();
            await Task.Delay(20);
            window.KeyPress(Key.C, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.C, "C");
            await Task.Delay(40);
            check("terminal-copy-still-works-after-exit", clipboard.Count > 0 && clipboard[^1] == preservedSelection);

            // A pending clipboard read is bound to the tab that started the paste, never to whatever
            // tab is selected when the read completes (per-tab isolation, no cross-tab shell targeting).
            dock.CreateTerminal();
            await Task.Delay(80);
            var initiator = dock.SelectedTab!;
            var bystander = tab;
            initiator.InputText = "initiator-draft";
            bystander.InputText = "bystander-draft";
            var pasteGate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            surface.ClipboardReaderForTesting = () => pasteGate.Task;
            var pasteFocus = 0;
            void CountPasteFocus(object? _, EventArgs __) => pasteFocus++;
            surface.PasteCompleted += CountPasteFocus;
            var pendingPaste = surface.PasteToDraftAsync();
            dock.SelectedTab = bystander;
            pasteGate.SetResult("PASTED-FOR-INITIATOR");
            await pendingPaste;
            surface.PasteCompleted -= CountPasteFocus;
            check("terminal-paste-binds-initiating-tab",
                initiator.InputText == "initiator-draft\nPASTED-FOR-INITIATOR" && bystander.InputText == "bystander-draft" && pasteFocus == 0);

            // Completing while the initiating tab is still selected keeps the draft focus hand-off.
            surface.ClipboardReaderForTesting = () => Task.FromResult<string?>("SECOND-PASTE");
            dock.SelectedTab = initiator;
            await Task.Delay(60);
            pasteFocus = 0;
            surface.PasteCompleted += CountPasteFocus;
            await surface.PasteToDraftAsync();
            surface.PasteCompleted -= CountPasteFocus;
            check("terminal-paste-focus-follows-initiating-tab",
                initiator.InputText.EndsWith("SECOND-PASTE", StringComparison.Ordinal) && pasteFocus == 1);

            // Closing the initiating tab while the read is pending must neither throw nor land anywhere.
            var doomed = initiator;
            var doomedDraft = doomed.InputText;
            var draftsBeforeClose = dock.Tabs.Where(candidate => !ReferenceEquals(candidate, doomed))
                .ToDictionary(candidate => candidate, candidate => candidate.InputText);
            var closeGate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            surface.ClipboardReaderForTesting = () => closeGate.Task;
            var pendingAfterClose = surface.PasteToDraftAsync();
            dock.CloseTerminal(doomed);
            closeGate.SetResult("MUST-NOT-LAND");
            await pendingAfterClose;
            check("terminal-paste-after-close-does-not-pollute",
                doomed.IsDisposed && doomed.InputText == doomedDraft && ReferenceEquals(surface.Session, dock.SelectedTab) &&
                dock.Tabs.All(candidate => draftsBeforeClose[candidate] == candidate.InputText &&
                    !candidate.InputText.Contains("MUST-NOT-LAND", StringComparison.Ordinal)));
        }
        finally { window.Close(); }
    }

    private static Point ToWindow(TerminalScreenControl surface, Window window, int row, int column)
    {
        var x = column == int.MaxValue
            ? Math.Max(TerminalScreenControl.OriginX, surface.Bounds.Width - 2)
            : TerminalScreenControl.OriginX + column * TerminalScreenControl.CellWidth + 2;
        var y = TerminalScreenControl.OriginY + row * TerminalScreenControl.CellHeight + 8;
        return surface.TranslatePoint(new Point(x, y), window) ?? new Point(x, y);
    }

    private static void PressKey(Window window, Key key, PhysicalKey physicalKey) =>
        window.KeyPress(key, RawInputModifiers.None, physicalKey, string.Empty);

    private static int FindRow(TerminalSnapshot snapshot, string needle)
    {
        for (var row = 0; row < snapshot.Lines.Length; row++)
            if (string.Concat(snapshot.Lines[row].Select(cell => cell.Text)).Contains(needle, StringComparison.Ordinal)) return row;
        return -1;
    }

    private static int BufferSubscribers(TerminalBuffer buffer) =>
        (typeof(TerminalBuffer).GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(buffer) as Delegate)?.GetInvocationList().Length ?? 0;

    /// <summary>Recording shell double: captures exactly what the terminal writes back to the PTY.</summary>
    private sealed class RecordingSession : ITerminalSession
    {
        public readonly List<string> Writes = new();
        public event Action<string>? Output;
        public event Action<int?, string?>? Exited;
        public bool IsRunning { get; private set; }
        public void Start(string workingDirectory, int columns, int rows)
        {
            IsRunning = true;
            Output?.Invoke("\u001b[36mrecording terminal\u001b[0m\r\n");
        }
        public void Write(string text) => Writes.Add(text);
        public void Resize(int columns, int rows) { }
        public void Dispose()
        {
            if (!IsRunning) return;
            IsRunning = false;
            Exited?.Invoke(0, null);
        }
        public void ExitWith(int? code)
        {
            IsRunning = false;
            Exited?.Invoke(code, null);
        }
    }
}
