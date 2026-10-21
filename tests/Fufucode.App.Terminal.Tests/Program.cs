using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using Fufucode.Plugins.Terminal.Features.TerminalExperience;
using Fufucode.Plugins.Terminal.Infrastructure;
using Fufucode.Plugins.Terminal.ViewModels;
using Fufucode.Plugins.Terminal.Views;

namespace Fufucode.App.Terminal.Tests;

// T1-TERMINAL 定向测试（独立入口，未注册到共享 SelfTestRunner）：
//   dotnet build tests/Fufucode.App.Terminal.Tests
//   dotnet run   --project tests/Fufucode.App.Terminal.Tests
// 覆盖：首字符/焦点捕获原子性、剪贴板复制粘贴往返与右键菜单、命令历史导航、
//       IME 组合预览与组合期回车守卫。真实 IME 候选框跟随、原生指针与 ConPTY 由原生验收覆盖。
internal static class Program
{
    private static int _passed;

    private static int Main(string[] args)
    {
        // Avalonia 的 avares 资源解析按“已加载程序集名的最短前缀匹配”选程序集
        // （Avalonia.Base AssemblyDescriptorResolver.GetAssembly）。本测试程序集名
        // Fufucode.App.Terminal.Tests 同样以 Fufucode.App 开头；若产品程序集尚未加载，
        // 解析会命中错误程序集并使 FufucodeTheme.axaml 无法预编译加载。先显式触达产品类型。
        _ = typeof(Fufucode.App.MainWindow).Assembly;
        _ = typeof(TerminalScreenControl).Assembly;
        AppBuilder.Configure<SmokeApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
            .SetupWithoutStarting();
        try
        {
            if (args.Contains("--conpty-p0"))
            {
                // Run off the headless dispatcher thread: blocking it would deadlock the async smoke.
                Console.WriteLine(Task.Run(() => Fufucode.Plugins.Terminal.SelfTest.TerminalSelfTest
                    .RunConPtyP0SmokeAsync(Environment.CurrentDirectory)).GetAwaiter().GetResult());
                return 0;
            }
            if (args.Contains("--conpty"))
            {
                Console.WriteLine(Task.Run(() => Fufucode.Plugins.Terminal.SelfTest.TerminalSelfTest
                    .RunConPtySmokeAsync(Environment.CurrentDirectory)).GetAwaiter().GetResult());
                return 0;
            }
            RunFirstCharacterChecks();
            RunClipboardChecks();
            RunHistoryChecks();
            RunCompositionChecks();
            RunVtP0Checks();
            RunPluginPageChecks();
            RunEnhancementChecks();
            RunHostWithoutTerminalPlugin();
            RunMainWindowPluginIntegration();
            Console.WriteLine($"\n全部通过：{_passed} 项断言");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    // ---------- 1. 首字符保护：焦点确立与字符捕获原子同步 ----------
    private static void RunFirstCharacterChecks()
    {
        using var harness = new Harness();
        harness.Ready();
        var session = harness.Session;

        // 点击输出面后立刻输入：第一个字符必须到达 shell（echo → cho 回归）。
        var surfacePoint = harness.PointOnSurface(20, 20);
        harness.Window.MouseDown(surfacePoint, MouseButton.Left, RawInputModifiers.None);
        harness.Window.MouseUp(surfacePoint, MouseButton.Left, RawInputModifiers.None);
        harness.Ready();
        Check(harness.Surface.IsFocused, "terminal-click-focuses-surface");
        harness.Window.KeyTextInput("e");
        harness.Ready();
        Check(session.Writes.SequenceEqual(new[] { "e" }), "terminal-first-char-after-click-reaches-shell");

        // 点击停靠页空白处（页签条留白），也要把键盘焦点交给输出面。
        harness.OutsideSurfaceButton.Focus();
        harness.Ready();
        var blankPoint = harness.PointOnDockHeaderBackground();
        Check(!harness.Surface.IsFocused, "terminal-dock-blank-click-precondition");
        harness.Window.MouseDown(blankPoint, MouseButton.Left, RawInputModifiers.None);
        harness.Window.MouseUp(blankPoint, MouseButton.Left, RawInputModifiers.None);
        harness.Ready();
        Check(harness.Surface.IsFocused, "terminal-click-dock-blank-focuses-surface");

        // 焦点仍在停靠条按钮上时到达的字符：同一个处理里聚焦输出面并写入该字符。
        harness.FindButton.Focus();
        harness.Ready();
        Check(!harness.Surface.IsFocused && harness.FindButton.IsFocused, "terminal-dock-button-holds-focus");
        var writesBefore = session.Writes.Count;
        harness.Window.KeyTextInput("x");
        harness.Ready();
        Check(session.Writes.Count == writesBefore + 1 && session.Writes[^1] == "x",
            "terminal-first-char-with-dock-focus-reaches-shell");
        Check(harness.Surface.IsFocused, "terminal-capture-establishes-focus");

        // 命令框自己的文本输入不能被终端捕获抢占。
        harness.Input.Focus();
        harness.Ready();
        var shellWrites = session.Writes.Count;
        harness.Window.KeyTextInput("a");
        harness.Ready();
        Check(harness.Tab.InputText == "a" && session.Writes.Count == shellWrites,
            "terminal-command-box-keeps-its-own-input");

        // 停靠页之外的控件持有焦点时，终端不得抢走字符。
        harness.Tab.InputText = string.Empty;
        harness.OutsideSurfaceButton.Focus();
        harness.Ready();
        shellWrites = session.Writes.Count;
        harness.Window.KeyTextInput("z");
        harness.Ready();
        Check(session.Writes.Count == shellWrites && harness.Tab.InputText.Length == 0,
            "terminal-does-not-steal-input-outside-dock");
    }

    // ---------- 2. 剪贴板复制/粘贴与右键菜单 ----------
    private static void RunClipboardChecks()
    {
        using var harness = new Harness();
        harness.Ready();
        var session = harness.Session;
        harness.Tab.Buffer.Append("\r\nclip-target-中文😀ZZ\r\nsecond-line\r\n");
        harness.Ready();
        var row = harness.FindRow("clip-target");
        Check(row >= 0, "terminal-clipboard-target-row-visible");

        // 无选区 Ctrl+C = SIGINT。
        harness.Surface.Focus();
        harness.Ready();
        harness.Press(Key.C, RawInputModifiers.Control, PhysicalKey.C);
        harness.Ready();
        Check(session.Writes.Count == 1 && session.Writes[0] == "\u0003", "terminal-ctrl-c-without-selection-interrupts");

        // 有选区 Ctrl+C = 复制到剪贴板。
        harness.DragSelect(row, 0, row, int.MaxValue);
        harness.Ready();
        Check(harness.Tab.OutputSelection.Text == "clip-target-中文😀ZZ", "terminal-drag-selects-exact-row");
        harness.Press(Key.C, RawInputModifiers.Control, PhysicalKey.C);
        harness.Ready();
        Check(harness.Clipboard.Count == 1 && harness.Clipboard[0] == "clip-target-中文😀ZZ",
            "terminal-ctrl-c-copies-selection");

        // Ctrl+V 把剪贴板并入命令草稿（CRLF 归一化），且不写 shell、不自动执行。
        harness.ClipboardReader = () => "paste-😀\r\nline2";
        harness.Tab.InputText = "keep-me";
        harness.Surface.Focus();
        harness.Ready();
        harness.Press(Key.V, RawInputModifiers.Control, PhysicalKey.V);
        harness.Ready();
        Check(harness.Tab.InputText == "keep-me\npaste-😀\nline2" && session.Writes.Count == 1,
            "terminal-ctrl-v-pastes-into-draft-only");

        // Direct paste to the shell: without bracketed paste line breaks become CR; with bracketed paste
        // the text is wrapped so interactive programs receive it as one paste.
        var writesBeforeDirect = session.Writes.Count;
        harness.ClipboardReader = () => "line1\nline2";
        Check(harness.Surface.PasteToShellAsync().GetAwaiter().GetResult() &&
              session.Writes[^1] == "line1\rline2", "terminal-direct-paste-sends-to-shell");
        harness.Tab.Buffer.Append("\u001b[?2004h");
        harness.Surface.PasteToShellAsync().GetAwaiter().GetResult();
        Check(session.Writes[^1] == "\u001b[200~line1\nline2\u001b[201~", "terminal-direct-paste-honors-bracketed-paste");
        Check(session.Writes.Count == writesBeforeDirect + 2, "terminal-direct-paste-count");

        // Shift+Insert 同义。
        harness.ClipboardReader = () => "shift-insert";
        harness.Surface.Focus();
        harness.Ready();
        harness.Press(Key.Insert, RawInputModifiers.Shift, PhysicalKey.Insert);
        harness.Ready();
        Check(harness.Tab.InputText.EndsWith("shift-insert", StringComparison.Ordinal) && session.Writes.Count == writesBeforeDirect + 2,
            "terminal-shift-insert-pastes-into-draft");

        // 复制 → 粘贴往返：粘贴内容与冻结选区文本逐字一致。
        harness.Tab.InputText = string.Empty;
        harness.DragSelect(row, 0, row, int.MaxValue);
        harness.Ready();
        harness.Press(Key.C, RawInputModifiers.Control, PhysicalKey.C);
        harness.Ready();
        var captured = harness.Clipboard[^1];
        harness.ClipboardReader = () => captured;
        harness.Surface.Focus();
        harness.Ready();
        harness.Press(Key.V, RawInputModifiers.Control, PhysicalKey.V);
        harness.Ready();
        Check(harness.Tab.InputText == captured && harness.Tab.OutputSelection.Text == captured,
            "terminal-copy-paste-round-trip");

        // 右键菜单结构：复制/粘贴草稿/直接粘贴/全选/打开链接/清屏。
        var items = harness.ContextMenu.Items.OfType<Control>().ToArray();
        Check(items.Length == 7 && items[0] is MenuItem { Header: "复制" } &&
              items[1] is MenuItem { Header: "粘贴到命令草稿" } && items[2] is MenuItem { Header: "粘贴到终端（直接发送）" } &&
              items[3] is MenuItem { Header: "全选" } && items[4] is MenuItem { Header: "打开链接" } &&
              items[5] is Separator && items[6] is MenuItem { Header: "清屏" },
            "terminal-context-menu-has-copy-paste-clear");
        Check(harness.CopyItem.Header?.ToString() == "复制" && harness.ClearItem.Header?.ToString() == "清屏",
            "terminal-context-menu-items-named");

        // 复制项只在有选区时可用。
        harness.Tab.OutputSelection.Clear();
        harness.View.SyncContextMenuState();
        Check(!harness.CopyItem.IsEnabled, "terminal-context-copy-disabled-without-selection");
        harness.DragSelect(row, 0, row, 3);
        harness.Ready();
        harness.View.SyncContextMenuState();
        Check(harness.CopyItem.IsEnabled, "terminal-context-copy-enabled-with-selection");

        // 清屏：本地清空屏幕与滚动缓冲，不向 shell 写入。
        var writesBeforeClear = session.Writes.Count;
        harness.ClearItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        harness.Ready();
        Check(harness.Tab.Buffer.PlainText.Length == 0 && session.Writes.Count == writesBeforeClear,
            "terminal-context-clear-clears-buffer-locally");

        // 右键菜单动作与键盘动作等价。
        harness.ClipboardReader = () => "menu-paste";
        harness.Tab.InputText = string.Empty;
        harness.PasteItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        harness.Ready();
        Check(harness.Tab.InputText == "menu-paste", "terminal-context-paste-runs-same-action");
    }

    // ---------- 3. 命令历史与提示符导航 ----------
    private static void RunHistoryChecks()
    {
        using var harness = new Harness();
        harness.Ready();
        var tab = harness.Tab;

        // 空历史：上下键不改变草稿。
        harness.Input.Focus();
        harness.Ready();
        tab.InputText = "draft-keep";
        harness.Press(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp);
        harness.Ready();
        Check(tab.InputText == "draft-keep" && !tab.History.IsBrowsing, "terminal-history-empty-up-is-inert");

        tab.InputText = "first-cmd";
        tab.SendInput();
        tab.InputText = "second-cmd";
        tab.SendInput();
        tab.InputText = "draft-keep";
        harness.Input.Focus();
        harness.Ready();
        harness.Press(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp);
        harness.Ready();
        Check(tab.InputText == "second-cmd" && harness.Input.CaretIndex == "second-cmd".Length,
            "terminal-history-up-returns-newest-with-caret-at-end");
        harness.Press(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp);
        harness.Ready();
        Check(tab.InputText == "first-cmd", "terminal-history-up-reaches-oldest");
        harness.Press(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp);
        harness.Ready();
        Check(tab.InputText == "first-cmd", "terminal-history-stops-at-oldest");
        harness.Press(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown);
        harness.Ready();
        Check(tab.InputText == "second-cmd", "terminal-history-down-steps-forward");
        harness.Press(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown);
        harness.Ready();
        Check(tab.InputText == "draft-keep" && !tab.History.IsBrowsing,
            "terminal-history-down-restores-unsent-draft");

        // 输出面上的方向键仍属于 shell；只有命令框浏览本地历史。
        harness.Surface.Focus();
        harness.Ready();
        harness.Press(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp);
        harness.Ready();
        Check(harness.Session.Writes.Contains("\u001b[A") && tab.InputText == "draft-keep",
            "terminal-surface-arrow-still-reaches-shell");

        // 历史按页签隔离。
        harness.Dock.CreateTerminal();
        harness.Ready();
        var second = harness.Dock.SelectedTab!;
        Check(second.History.Entries.Count == 0, "terminal-history-is-per-tab");
        harness.Dock.SelectedTab = tab;
        harness.Ready();
        Check(tab.History.Entries.Count == 2 && tab.History.Entries[0] == "first-cmd",
            "terminal-history-preserved-across-tab-switch");
    }

    // ---------- 4. IME 组合预览 ----------
    private static void RunCompositionChecks()
    {
        using var harness = new Harness();
        harness.Ready();
        var presenter = harness.Presenter;
        Check(presenter is not null, "terminal-command-box-exposes-text-presenter");
        if (presenter is null) return;

        presenter.PreeditText = "nihao";
        harness.Ready();
        Check(harness.Composition.IsComposing && harness.CompositionPreview.IsVisible &&
              harness.CompositionPreviewText.Text == "组合中：nihao",
            "terminal-ime-composition-shows-preview");

        // 组合期回车不提交命令，草稿保留。
        harness.Input.Focus();
        harness.Ready();
        harness.Tab.InputText = "未提交草稿";
        presenter.PreeditText = "nihao";
        var composingAtEnter = harness.Composition.IsComposing;
        var writesBefore = harness.Session.Writes.Count;
        harness.Press(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter);
        harness.Ready();
        Check(composingAtEnter && harness.Session.Writes.Count == writesBefore && harness.Tab.InputText == "未提交草稿",
            "terminal-ime-enter-during-composition-does-not-submit");

        // 提交：组合预览消失，提交文本完整并入命令草稿（不分裂、不乱码）。
        presenter.PreeditText = null;
        harness.Ready();
        Check(!harness.Composition.IsComposing && !harness.CompositionPreview.IsVisible &&
              (harness.CompositionPreviewText.Text?.Length ?? 0) == 0,
            "terminal-ime-commit-hides-preview");
        harness.Input.Focus();
        harness.Input.SelectAll();
        harness.Ready();
        harness.Window.KeyTextInput("你好😀");
        harness.Ready();
        Check(harness.Tab.InputText == "你好😀" && harness.Tab.InputText.Length == 4,
            "terminal-ime-committed-text-appended-intact");

        // 预览永不渲染半个代理对。
        harness.Composition.Update("a\ud83d");
        Check(harness.Composition.IsComposing && harness.Composition.PreviewText == "a",
            "terminal-ime-preview-holds-lone-high-surrogate");
        harness.Composition.Update("a\ud83d\ude00");
        Check(harness.Composition.PreviewText == "a😀", "terminal-ime-preview-completes-surrogate-pair");
        harness.Composition.Clear();
        Check(!harness.Composition.IsComposing && harness.Composition.PreviewText.Length == 0,
            "terminal-ime-preview-clears");
    }

    // ---------- 5. P0：备用屏、DSR/DA 应答、滚动区域与行/字符编辑 ----------
    private static void RunVtP0Checks()
    {
        // DSR/DA: prompt frameworks and full-screen apps wait for these replies.
        var replies = new TerminalBuffer(20, 6);
        replies.Append("abc\u001b[2;3H\u001b[6n");
        Check(replies.TakePendingResponse() == "\u001b[2;3R", "terminal-dsr-cursor-position-reply");
        replies.Append("\u001b[c");
        Check(replies.TakePendingResponse() == "\u001b[?1;2c", "terminal-da-primary-reply");
        replies.Append("\u001b[>c");
        Check(replies.TakePendingResponse() == "\u001b[>0;276;0c", "terminal-da-secondary-reply");
        Check(replies.TakePendingResponse().Length == 0, "terminal-replies-drain-once");

        // The view model must answer a DSR query by writing the reply back to the shell.
        var recording = new RecordingSession();
        using (var tab = new TerminalTabViewModel(1, isMock: false, recording))
        {
            tab.Start(Environment.CurrentDirectory);
            recording.Emit("\u001b[1;1H\u001b[6n");
            Check(recording.Writes.Count > 0 && recording.Writes[^1] == "\u001b[1;1R",
                "terminal-session-writes-dsr-reply");
        }

        // Alternate screen: full-screen apps get a clean screen, and leaving it restores the primary
        // content and its scrollback unchanged.
        var alt = new TerminalBuffer(20, 4);
        alt.Append("primary-line\r\nsecond");
        var primaryText = alt.PlainText;
        var primaryLines = alt.Snapshot().Lines.Length;
        alt.Append("\u001b[?1049h");
        Check(alt.AlternateScreen && alt.PlainText.Length == 0, "terminal-alt-screen-starts-empty");
        alt.Append("\u001b[1;1Hvim-screen\r\nrow2\r\nrow3\r\nrow4\r\nrow5");
        Check(alt.PlainText.StartsWith("vim-screen", StringComparison.Ordinal), "terminal-alt-screen-renders");
        alt.Append("\u001b[?1049l");
        Check(!alt.AlternateScreen && alt.PlainText == primaryText && alt.Snapshot().Lines.Length == primaryLines,
            "terminal-alt-screen-restores-primary");

        // Scrolling inside an alternate screen must never enter the primary scrollback.
        var isolation = new TerminalBuffer(12, 3);
        for (var i = 0; i < 10; i++) isolation.Append($"p{i}\r\n");
        var isolatedText = isolation.PlainText;
        var isolatedLines = isolation.Snapshot().Lines.Length;
        isolation.Append("\u001b[?1049h");
        for (var i = 0; i < 20; i++) isolation.Append($"a{i}\r\n");
        isolation.Append("\u001b[?1049l");
        Check(isolation.PlainText == isolatedText && isolation.Snapshot().Lines.Length == isolatedLines,
            "terminal-alt-screen-keeps-primary-scrollback");

        // Scroll region (DECSTBM): a line feed at the region bottom scrolls only the region.
        var region = new TerminalBuffer(12, 6);
        region.Append("\u001b[1;1HrowA\u001b[2;1HrowB\u001b[3;1HrowC\u001b[4;1HrowD\u001b[5;1HrowE\u001b[6;1HrowF");
        region.Append("\u001b[3;5r\u001b[5;1H\n");
        var regionSnapshot = region.Snapshot();
        Check(RowText(regionSnapshot, 0) == "rowA" && RowText(regionSnapshot, 1) == "rowB" && RowText(regionSnapshot, 5) == "rowF",
            "terminal-scroll-region-leaves-outside-rows");
        Check(RowText(regionSnapshot, 2) == "rowD" && RowText(regionSnapshot, 3) == "rowE" && RowText(regionSnapshot, 4).Length == 0
            && regionSnapshot.Lines.Length == 6,
            "terminal-scroll-region-scrolls-only-inside");

        // IL/DL: insert and delete whole lines inside the active region.
        var lines = new TerminalBuffer(10, 4);
        lines.Append("a\r\nb\r\nc\r\nd");
        lines.Append("\u001b[2;1H\u001b[L");
        var inserted = lines.Snapshot();
        Check(RowText(inserted, 0) == "a" && RowText(inserted, 1).Length == 0 && RowText(inserted, 2) == "b" && RowText(inserted, 3) == "c",
            "terminal-insert-line");
        lines.Append("\u001b[2;1H\u001b[M");
        var deleted = lines.Snapshot();
        Check(RowText(deleted, 0) == "a" && RowText(deleted, 1) == "b" && RowText(deleted, 2) == "c" && RowText(deleted, 3).Length == 0,
            "terminal-delete-line");

        // ICH/DCH/ECH: edit characters in place without touching the rest of the row.
        var chars = new TerminalBuffer(10, 2);
        chars.Append("abcdef");
        chars.Append("\u001b[1;3H\u001b[2P");
        Check(RowText(chars.Snapshot(), 0).StartsWith("abef", StringComparison.Ordinal), "terminal-delete-chars");
        chars.Append("\u001b[1;3H\u001b[2@");
        Check(RowText(chars.Snapshot(), 0).StartsWith("ab  ef", StringComparison.Ordinal), "terminal-insert-chars");
        chars.Append("\u001b[1;4H\u001b[2X");
        Check(RowText(chars.Snapshot(), 0).StartsWith("ab   f", StringComparison.Ordinal), "terminal-erase-chars");

        // Deferred wrap (xterm semantics): the cursor stays on the last column until the next printable char.
        var wrap = new TerminalBuffer(10, 3);
        wrap.Append("abcdefghij");
        var filled = wrap.Snapshot();
        Check(filled.CursorLine == 0 && filled.CursorColumn == 9, "terminal-wrap-pending-cursor-stays-on-last-column");
        wrap.Append("k");
        var wrapped = wrap.Snapshot();
        Check(RowText(wrapped, 0) == "abcdefghij" && RowText(wrapped, 1) == "k" && wrapped.CursorLine == 1,
            "terminal-wrap-moves-on-next-printable");

        // Resize keeps each line's text (cropping trailing blanks first) and the scrollback stays usable.
        var shrinking = new TerminalBuffer(20, 4);
        shrinking.Append("keep-this-text\r\nsecond-line\r\nthird");
        shrinking.Resize(12, 4);
        var resized = shrinking.Snapshot();
        Check(RowText(resized, 0) == "keep-this-te" && RowText(resized, 1) == "second-line" && RowText(resized, 2) == "third"
            && resized.Lines.All(line => line.Length == 12),
            "terminal-resize-crops-trailing-blank-cells-first");
        shrinking.Resize(12, 2);
        var shrunk = shrinking.Snapshot();
        Check(shrunk.Lines.Length == 4 && RowText(shrunk, 0) == "keep-this-te" && RowText(shrunk, 2) == "third",
            "terminal-resize-shrinks-into-scrollback");

        // Origin mode (DECOM): cursor addressing is relative to the scroll region.
        var origin = new TerminalBuffer(12, 6);
        origin.Append("\u001b[2;5r\u001b[?6h\u001b[1;1Horigin");
        Check(RowText(origin.Snapshot(), 1).StartsWith("origin", StringComparison.Ordinal), "terminal-origin-mode-addressing");
    }

    // ---------- 5b. 增强：SGR、标题、链接、设置、选择 ----------
    private static void RunEnhancementChecks()
    {
        // SGR attributes survive into the snapshot and reset correctly.
        var sgr = new TerminalBuffer(20, 3);
        sgr.Append("\u001b[4;3;9mA\u001b[0mB\u001b[7mC\u001b[27mD");
        var line = sgr.Snapshot().Lines[0];
        Check(line[0].Underline && line[0].Italic && line[0].Strike, "terminal-sgr-underline-italic-strike");
        Check(!line[1].Underline && !line[1].Italic, "terminal-sgr-reset-clears-attributes");
        Check(line[2].Reverse && !line[3].Reverse, "terminal-sgr-reverse-toggle");
        var dim = new TerminalBuffer(10, 2);
        dim.Append("\u001b[2;8mE");
        Check(dim.Snapshot().Lines[0][0].Dim && dim.Snapshot().Lines[0][0].Conceal, "terminal-sgr-dim-conceal");

        // OSC title and hyperlink.
        var osc = new TerminalBuffer(20, 3);
        osc.Append("\u001b]2;Build · 构建\a\u001b]8;;https://example.com/x\u001b\\link\u001b]8;;\u001b\\ tail");
        var oscSnapshot = osc.Snapshot();
        Check(oscSnapshot.Title == "Build · 构建", "terminal-osc-title");
        Check(oscSnapshot.Lines[0][0].Link == "https://example.com/x", "terminal-osc8-link-attached");
        Check(oscSnapshot.Lines[0].Skip(4).All(cell => cell.Link is null), "terminal-osc8-link-ended");

        // Modes and cursor shape are reported to the page.
        var modes = new TerminalBuffer(20, 3);
        modes.Append("\u001b[?2004h\u001b[?1000h\u001b[3 q");
        var modesSnapshot = modes.Snapshot();
        Check(modesSnapshot.BracketedPaste && modesSnapshot.MouseTracking && modesSnapshot.MouseMode == 1000 && modesSnapshot.CursorStyle == 3,
            "terminal-modes-and-cursor-style");

        // BEL raises the bell event.
        var bell = 0;
        var bellBuffer = new TerminalBuffer(10, 2);
        bellBuffer.Bell += () => bell++;
        bellBuffer.Append("a\ab");
        Check(bell == 1, "terminal-bel-event");

        // Combining marks stay in the owning cell; overwriting a wide continuation clears the owner.
        var combining = new TerminalBuffer(10, 2);
        combining.Append("é");
        Check(combining.Snapshot().Lines[0][0].Text == "é" && combining.Snapshot().Lines[0][1].Text.Trim().Length == 0,
            "terminal-combining-mark-joins-cell");
        var wide = new TerminalBuffer(10, 2);
        wide.Append("你");
        wide.Append("\u001b[1;2Hx");
        Check(wide.Snapshot().Lines[0][0].Text == " " && wide.Snapshot().Lines[0][1].Text == "x",
            "terminal-overwrite-wide-continuation-clears-owner");

        // Palette override (OSC 4) affects SGR 31 output.
        var palette = new TerminalBuffer(10, 2);
        palette.Append("\u001b]4;1;#123456\u001b\\\u001b[31mR");
        Check(palette.Snapshot().Lines[0][0].Foreground == 0x123456, "terminal-osc4-palette-override");

        // Selection helpers: select-all, double-click word, triple-click row.
        var text = new TerminalBuffer(20, 3);
        text.Append("alpha beta gamma\r\nsecond line");
        var textSnapshot = text.Snapshot();
        var selection = new TerminalOutputSelection();
        Check(selection.SelectAll(textSnapshot) && selection.Text.Contains("alpha beta gamma", StringComparison.Ordinal)
            && selection.Text.Contains("second line", StringComparison.Ordinal), "terminal-selection-select-all");
        var word = new TerminalOutputSelection();
        Check(word.SelectWord(textSnapshot, new TerminalCellPosition(0, 7)) && word.Text == "beta", "terminal-selection-double-click-word");
        var row = new TerminalOutputSelection();
        Check(row.SelectRow(textSnapshot, 1) && row.Text == "second line", "terminal-selection-triple-click-row");

        // Settings normalize unknown values and round-trip through the plugin store.
        var normalized = (TerminalSettings.Default with
        {
            ShellProfile = "no-such-shell", FontSize = 999, ScrollbackLines = -4, Bell = "loud"
        }).Normalize();
        Check(normalized.ShellProfile == ShellProfiles.AutoId && normalized.FontSize == 28 && normalized.ScrollbackLines == 0 && normalized.Bell == "visual",
            "terminal-settings-normalize");
        Check(TerminalSettings.Default.ShellProfile == ShellProfiles.AutoId, "terminal-settings-default-is-auto");
        Check((new TerminalSettings { Version = 1, ShellProfile = "cmd" }).Normalize().ShellProfile == ShellProfiles.AutoId,
            "terminal-settings-v1-cmd-migrates-to-auto");
        Check((new TerminalSettings { Version = 2, ShellProfile = "cmd" }).Normalize().ShellProfile == "cmd",
            "terminal-settings-explicit-v2-cmd-is-kept");
        var settingsRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fufucode-terminal-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TerminalSettingsStore(settingsRoot);
            Check(store.Load() == TerminalSettings.Default, "terminal-settings-default-when-missing");
            store.Save(TerminalSettings.Default with { ShellProfile = "powershell", FontSize = 16, ConfirmClose = false });
            var loaded = store.Load();
            Check(loaded.ShellProfile == "powershell" && loaded.FontSize == 16 && !loaded.ConfirmClose, "terminal-settings-round-trip");
        }
        finally
        {
            try { if (System.IO.Directory.Exists(settingsRoot)) System.IO.Directory.Delete(settingsRoot, true); }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Built-in shell profiles resolve their real executable paths on Windows.
        if (OperatingSystem.IsWindows())
        {
            var cmd = ShellProfiles.Find("cmd")!;
            var powershell = ShellProfiles.Find("powershell")!;
            Check(ShellProfiles.Resolve(cmd) is { Length: > 0 }, "terminal-shell-cmd-resolves");
            Check(ShellProfiles.Resolve(powershell) is { Length: > 0 }, "terminal-shell-powershell-resolves");
            Check(ShellProfiles.BuildCommandLine(cmd, ShellProfiles.Resolve(cmd), null).Contains("cmd.exe", StringComparison.OrdinalIgnoreCase),
                "terminal-shell-command-line");
            var automatic = ShellProfiles.ResolveAuto();
            Check(ShellProfiles.Resolve(automatic) is { Length: > 0 } && automatic.Id is "pwsh" or "powershell" or "cmd",
                "terminal-shell-auto-prefers-newer-shells");
            Check(ShellProfiles.DescribeResolution(ShellProfiles.AutoId).Length > 0,
                "terminal-shell-auto-describes-resolution");
        }

        // A shell that cannot be resolved produces an explicit start error, not a silent cmd fallback.
        var profile = ShellProfiles.Find("definitely-not-installed");
        Check(profile is null, "terminal-unknown-shell-profile-is-null");
        var unavailable = new UnavailableSession("所选 shell 不可用：测试");
        using var unavailableTab = new TerminalTabViewModel(1, isMock: false, unavailable);
        unavailableTab.Start(Environment.CurrentDirectory);
        Check(!unavailableTab.IsRunning && unavailableTab.Buffer.PlainText.Contains("所选 shell 不可用", StringComparison.Ordinal),
            "terminal-unavailable-shell-reports-reason");

        // The plugin page persists settings and applies display values.
        var pageRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fufucode-terminal-page-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var page = new Fufucode.Plugins.Terminal.TerminalDockPage(pageRoot);
            page.ApplySettings(TerminalSettings.Default with { FontSize = 18, ScrollbackLines = 42, ShellProfile = "powershell" });
            var persisted = new TerminalSettingsStore(pageRoot).Load();
            Check(persisted.FontSize == 18 && persisted.ScrollbackLines == 42 && persisted.ShellProfile == "powershell",
                "terminal-page-persists-settings");
            page.SetProject(Environment.CurrentDirectory, useMock: true);
            page.CreateTerminal();
            Check(page.HasTabs, "terminal-page-uses-settings-for-new-tab");
        }
        finally
        {
            try { if (System.IO.Directory.Exists(pageRoot)) System.IO.Directory.Delete(pageRoot, true); }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Tab titles are user-editable; the shell title remains separate.
        using (var titled = new TerminalTabViewModel(1, isMock: true, new TerminalMockSession()))
        {
            titled.Title = "构建终端";
            Check(titled.Title == "构建终端", "terminal-tab-title-renamable");
        }

        // Scrollback limit is configurable per buffer.
        var bounded = new TerminalBuffer(10, 3) { ScrollbackLimit = 5 };
        for (var i = 0; i < 40; i++) bounded.Append($"r{i}\r\n");
        Check(bounded.Snapshot().Lines.Length <= 8, "terminal-scrollback-limit-configurable");
    }

    // ---------- 6. 插件页面契约：插件包 → 宿主页面 → 项目级状态 ----------
    private static void RunPluginPageChecks()
    {
        var package = new Fufucode.Plugins.Terminal.TerminalPluginPackage();
        Check(package.Id == "workspace.terminal" && package.Version == "2.0.0", "terminal-plugin-identity");
        var contribution = package.CreatePageContribution(
            new Fufucode.Core.Plugins.PluginPackageContext("workspace.terminal", Environment.CurrentDirectory, null));
        Check(contribution is { PageId: "terminal", Title: "终端", IconKey: "Terminal" }, "terminal-plugin-page-contribution");

        using var page = (Fufucode.Plugins.Terminal.TerminalDockPage)contribution!.CreateView();
        page.SetProject(Environment.CurrentDirectory, useMock: true);
        Check(!page.HasTabs && page.CommandInput is not null, "terminal-page-starts-empty-with-command-input");
        page.CreateTerminal();
        Check(page.HasTabs && page.IsVisible && page.HasRunningTab, "terminal-page-creates-running-tab");
        var stateEvents = 0;
        page.StateChanged += (_, _) => stateEvents++;
        page.Hide();
        Check(!page.IsVisible && stateEvents > 0, "terminal-page-state-changes-reach-host");
        page.Show();
        Check(page.IsVisible, "terminal-page-show-restores-visibility");
        page.Dispose();

        // Full host path: marketplace install -> page contribution -> workbench page registration.
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fufucode-terminal-plugin-" + Guid.NewGuid().ToString("N"));
        try
        {
            var layout = new Fufucode.Core.AppDataLayout(root);
            var store = new Fufucode.Core.Plugins.PluginStateStore(layout);
            Fufucode.Core.Plugins.PluginPackageManager.RegisterPackageFactory(
                "workspace.terminal", () => new Fufucode.Plugins.Terminal.TerminalPluginPackage());
            var manager = new Fufucode.Core.Plugins.PluginPackageManager(
                layout, new Fufucode.Core.Plugins.PluginToolRegistry(new Fufucode.Core.Plugins.PluginCapabilityGateway(store)), store);
            manager.InstallPackage("workspace.terminal");
            using var registry = new Fufucode.App.Views.WorkbenchPageRegistry();
            using var host = new Fufucode.App.Features.Plugins.PluginWorkspacePageHost(root, registry, () => true, manager);
            host.Start(Environment.CurrentDirectory);
            Check(host.TryGet("terminal", out var hosted) && hosted.HasTabs == false, "terminal-plugin-host-registers-page");
            Check(registry.ContainsKey("terminal"), "terminal-plugin-page-is-in-workbench-registry");
        }
        finally
        {
            try { if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true); }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ---------- 7. 真实宿主：未安装插件时终端不存在 ----------
    private static void RunHostWithoutTerminalPlugin()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fufucode-no-terminal-" + Guid.NewGuid().ToString("N"));
        var previousRoot = Fufucode.App.Program.SelfTestDataRoot;
        try
        {
            Fufucode.App.Program.SelfTestDataRoot = root;
            var window = new Fufucode.App.MainWindow(Fufucode.App.Integration.AppOptions.Mock with { AppDataRootOverride = root })
            {
                Width = 1100,
                Height = 760
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Check(!window.TerminalPluginAvailableForTesting, "host-without-terminal-plugin-has-no-page");
            Check(!window.PageAvailableForTesting("terminal"), "host-without-terminal-plugin-has-no-workbench-page");
            Check(window.ActionsForTesting.GetDisabledReason("terminal.interrupt") == "没有运行中的终端",
                "host-without-terminal-plugin-disables-terminal-actions-honestly");
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            Fufucode.App.Program.SelfTestDataRoot = previousRoot;
            try { if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true); }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ---------- 7. 真实宿主：安装插件后 MainWindow 动态接入终端页 ----------
    private static void RunMainWindowPluginIntegration()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fufucode-terminal-host-" + Guid.NewGuid().ToString("N"));
        var previousRoot = Fufucode.App.Program.SelfTestDataRoot;
        try
        {
            var configured = Environment.GetEnvironmentVariable("FUFUCODE_TERMINAL_PLUGIN_DIR");
            var target = System.IO.Path.Combine(root, "plugins", "workspace.terminal");
            System.IO.Directory.CreateDirectory(target);
            if (!string.IsNullOrWhiteSpace(configured) && System.IO.Directory.Exists(configured))
            {
                // Verify a real (lean, published) plugin payload loaded by the host.
                foreach (var file in System.IO.Directory.GetFiles(configured))
                    System.IO.File.Copy(file, System.IO.Path.Combine(target, System.IO.Path.GetFileName(file)), true);
            }
            else
            {
                var pluginSource = System.IO.Path.GetDirectoryName(typeof(Fufucode.Plugins.Terminal.TerminalPluginPackage).Assembly.Location)!;
                foreach (var file in new[] { "Fufucode.Plugins.Terminal.dll", "manifest.json" })
                    System.IO.File.Copy(System.IO.Path.Combine(pluginSource, file), System.IO.Path.Combine(target, file), true);
            }
            var layout = new Fufucode.Core.AppDataLayout(root);
            new Fufucode.Core.Plugins.PluginStateStore(layout).Install("workspace.terminal");
            Fufucode.App.Program.SelfTestDataRoot = root;

            var window = new Fufucode.App.MainWindow(Fufucode.App.Integration.AppOptions.Mock with { AppDataRootOverride = root })
            {
                Width = 1280,
                Height = 820
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Check(window.TerminalPluginAvailableForTesting, "host-loads-installed-terminal-plugin");
            Check(window.PageAvailableForTesting("terminal"), "host-registers-terminal-workbench-page");
            window.OpenDockPage("terminal", Fufucode.App.Infrastructure.DockZone.Bottom);
            Dispatcher.UIThread.RunJobs();
            var page = window.TerminalPageForTesting!;
            page.CreateTerminal();
            Dispatcher.UIThread.RunJobs();
            Check(page.HasTabs && page.HasRunningTab, "host-terminal-page-owns-a-running-tab");
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            Fufucode.App.Program.SelfTestDataRoot = previousRoot;
            try { if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true); }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string RowText(TerminalSnapshot snapshot, int row) =>
        row < 0 || row >= snapshot.Lines.Length ? string.Empty
            : string.Concat(snapshot.Lines[row].Select(cell => cell.Text)).TrimEnd();

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL " + name);
        _passed++;
        Console.WriteLine("PASS " + name);
    }

    /// <summary>真实控件 + 录制 shell 的终端停靠工装。</summary>
    private sealed class Harness : IDisposable
    {
        private readonly List<RecordingSession> _sessions = new();

        public Harness()
        {
            Dock = new TerminalDockViewModel(workspacePath: Environment.CurrentDirectory, sessionFactory: () =>
            {
                var session = new RecordingSession();
                _sessions.Add(session);
                return session;
            });
            Dock.Show();
            View = new TerminalDockView { DataContext = Dock };
            OutsideSurfaceButton = new Button { Content = "停靠页之外的按钮" };
            var root = new Grid();
            root.Children.Add(View);
            root.Children.Add(OutsideSurfaceButton);
            OutsideSurfaceButton.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            OutsideSurfaceButton.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            Window = new Window { Width = 780, Height = 430, Content = root };
            Window.Show();
            Surface = View.FindControl<TerminalScreenControl>("TerminalSurface")!;
            Input = View.FindControl<TextBox>("TerminalInput")!;
            FindButton = View.FindControl<Button>("FindButton")!;
            Tabs = View.FindControl<ListBox>("TerminalTabs")!;
            OutputScroller = View.FindControl<ScrollViewer>("OutputScroller")!;
            CompositionPreview = View.FindControl<Border>("CompositionPreview")!;
            CompositionPreviewText = View.FindControl<TextBlock>("CompositionPreviewText")!;
            CopyItem = View.FindControl<MenuItem>("ContextCopyItem")!;
            PasteItem = View.FindControl<MenuItem>("ContextPasteItem")!;
            ClearItem = View.FindControl<MenuItem>("ContextClearItem")!;
            Clipboard = new List<string>();
            Surface.ClipboardWriterForTesting = text => { Clipboard.Add(text); return System.Threading.Tasks.Task.CompletedTask; };
            Surface.ClipboardReaderForTesting = () => System.Threading.Tasks.Task.FromResult<string?>(null);
        }

        public TerminalDockViewModel Dock { get; }
        public TerminalDockView View { get; }
        public Window Window { get; }
        public Button OutsideSurfaceButton { get; }
        public TerminalScreenControl Surface { get; }
        public TextBox Input { get; }
        public Button FindButton { get; }
        public ListBox Tabs { get; }
        public ScrollViewer OutputScroller { get; }
        public Border CompositionPreview { get; }
        public TextBlock CompositionPreviewText { get; }
        public MenuItem CopyItem { get; }
        public MenuItem PasteItem { get; }
        public MenuItem ClearItem { get; }
        public List<string> Clipboard { get; set; }
        public RecordingSession Session => _sessions[0];
        public TerminalTabViewModel Tab => Dock.SelectedTab!;
        public TerminalCompositionState Composition => View.Composition;
        public ContextMenu ContextMenu => Surface.ContextMenu!;
        public TextPresenter? Presenter =>
            Input.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();

        /// <summary>替换剪贴板读取端（粘贴来源）。</summary>
        public Func<string?> ClipboardReader
        {
            set => Surface.ClipboardReaderForTesting = () => System.Threading.Tasks.Task.FromResult(value());
        }

        public void Ready()
        {
            for (var index = 0; index < 6; index++)
            {
                Dispatcher.UIThread.RunJobs();
                Window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Thread.Sleep(10);
            }
            Dispatcher.UIThread.RunJobs();
        }

        public void Press(Key key, RawInputModifiers modifiers, PhysicalKey physicalKey) =>
            Window.KeyPress(key, modifiers, physicalKey, null);

        public Point PointOnSurface(double x, double y) =>
            Surface.TranslatePoint(new Point(x, y), Window) ?? new Point(x, y);

        /// <summary>页签条留白处（既不在页签项、也不在工具按钮上）的窗口坐标点。</summary>
        public Point PointOnDockHeaderBackground()
        {
            var x = Math.Min(Tabs.Bounds.Width + 30, Math.Max(20, View.Bounds.Width - 200));
            var local = new Point(x, 20);
            return View.TranslatePoint(local, Window) ?? local;
        }

        public void DragSelect(int startRow, int startColumn, int endRow, int endColumn)
        {
            var start = ToWindow(startRow, startColumn);
            var end = ToWindow(endRow, endColumn);
            Window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
            Window.MouseMove(end, RawInputModifiers.None);
            Window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
        }

        public int FindRow(string needle)
        {
            var snapshot = Tab.Snapshot;
            for (var row = 0; row < snapshot.Lines.Length; row++)
                if (string.Concat(snapshot.Lines[row].Select(cell => cell.Text)).Contains(needle, StringComparison.Ordinal))
                    return row;
            return -1;
        }

        private Point ToWindow(int row, int column)
        {
            var x = column == int.MaxValue
                ? Math.Max(TerminalScreenControl.OriginX, Surface.Bounds.Width - 2)
                : TerminalScreenControl.OriginX + column * Surface.CellWidth + 2;
            var y = TerminalScreenControl.OriginY + row * Surface.CellHeight + 8;
            return Surface.TranslatePoint(new Point(x, y), Window) ?? new Point(x, y);
        }

        public void Dispose()
        {
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            Dock.Dispose();
        }
    }

    /// <summary>录制 shell 替身：记录写入 PTY 的字节，不做任何命令执行。</summary>
    private sealed class RecordingSession : ITerminalSession
    {
        public List<string> Writes { get; } = new();
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

        /// <summary>Feed bytes as if the shell had produced them (used for DSR/DA reply checks).</summary>
        public void Emit(string text) => Output?.Invoke(text);

        public void Dispose()
        {
            if (!IsRunning) return;
            IsRunning = false;
            Exited?.Invoke(0, null);
        }
    }

    private sealed class SmokeApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
            Styles.Add(new StyleInclude(new Uri("avares://Fufucode.App/"))
            {
                Source = new Uri("avares://Fufucode.App/Styles/FufucodeTheme.axaml"),
            });
            Styles.Add(new StyleInclude(new Uri("avares://Fufucode.App/"))
            {
                Source = new Uri("avares://Fufucode.App/Styles/WorkbenchStyles.axaml"),
            });
        }
    }
}
