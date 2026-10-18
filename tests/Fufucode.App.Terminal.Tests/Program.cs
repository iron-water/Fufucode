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
using Fufucode.App.Features.TerminalExperience;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;

namespace Fufucode.App.Terminal.Tests;

// T1-TERMINAL 定向测试（独立入口，未注册到共享 SelfTestRunner）：
//   dotnet build tests/Fufucode.App.Terminal.Tests
//   dotnet run   --project tests/Fufucode.App.Terminal.Tests
// 覆盖：首字符/焦点捕获原子性、剪贴板复制粘贴往返与右键菜单、命令历史导航、
//       IME 组合预览与组合期回车守卫。真实 IME 候选框跟随、原生指针与 ConPTY 由原生验收覆盖。
internal static class Program
{
    private static int _passed;

    private static int Main()
    {
        // Avalonia 的 avares 资源解析按“已加载程序集名的最短前缀匹配”选程序集
        // （Avalonia.Base AssemblyDescriptorResolver.GetAssembly）。本测试程序集名
        // Fufucode.App.Terminal.Tests 同样以 Fufucode.App 开头；若产品程序集尚未加载，
        // 解析会命中错误程序集并使 FufucodeTheme.axaml 无法预编译加载。先显式触达产品类型。
        _ = typeof(TerminalScreenControl).Assembly;
        AppBuilder.Configure<SmokeApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
            .SetupWithoutStarting();
        try
        {
            RunFirstCharacterChecks();
            RunClipboardChecks();
            RunHistoryChecks();
            RunCompositionChecks();
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

        // Shift+Insert 同义。
        harness.ClipboardReader = () => "shift-insert";
        harness.Surface.Focus();
        harness.Ready();
        harness.Press(Key.Insert, RawInputModifiers.Shift, PhysicalKey.Insert);
        harness.Ready();
        Check(harness.Tab.InputText.EndsWith("shift-insert", StringComparison.Ordinal) && session.Writes.Count == 1,
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

        // 右键菜单结构：复制/粘贴/清屏。
        var items = harness.ContextMenu.Items.OfType<Control>().ToArray();
        Check(items.Length == 4 && items[0] is MenuItem { Header: "复制" } && items[1] is MenuItem { Header: "粘贴" } &&
              items[2] is Separator && items[3] is MenuItem { Header: "清屏" },
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
                : TerminalScreenControl.OriginX + column * TerminalScreenControl.CellWidth + 2;
            var y = TerminalScreenControl.OriginY + row * TerminalScreenControl.CellHeight + 8;
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
