using System.Text;
using System.Threading.Channels;
using Fufucode.Fui;

namespace Fufucode.Fui.Tests;

internal static class MouseTests
{
    internal static void Run()
    {
        Parse();
        PasteInterop();
        LayoutHits();
        ControllerInput();
        ApprovalSafety();
        LifecycleAndBounds();
    }

    private static void Parse()
    {
        Eq(true, MouseInput.TryParse("\x1b[<64;10;5M", out var up), "wheel up parses");
        Eq(MouseWheel.Up, up.Wheel, "wheel up direction");
        Eq(9, up.Column, "SGR column is zero-based");
        Eq(4, up.Row, "SGR row is zero-based");
        Eq(false, up.LeftClick, "wheel is not a click");

        Eq(true, MouseInput.TryParse("\x1b[<65;1;2M", out var down), "wheel down parses");
        Eq(MouseWheel.Down, down.Wheel, "wheel down direction");
        Eq(true, MouseInput.TryParse("\x1b[<0;3;4M", out var click), "left click parses");
        Eq(true, click.LeftClick, "left click flag");
        Eq(false, click.Release, "press is not release");
        Eq(true, MouseInput.TryParse("\x1b[<0;3;4m", out var release), "release parses");
        Eq(true, release.Release, "release flag");
        Eq(false, release.LeftClick, "release is not a click");
        Eq(true, MouseInput.TryParse("\x1b[<32;3;4M", out var motion), "motion parses");
        Eq(true, motion.Motion, "motion flag");
        Eq(false, motion.LeftClick, "motion is not a click");

        Eq(true, MouseInput.TryParse("\x1b[<66;1;1M", out var horizontal), "horizontal wheel parses");
        Eq(MouseWheel.None, horizontal.Wheel, "horizontal wheel has no vertical action");
        Eq(null, MouseInput.WheelKey(horizontal), "horizontal wheel has no page key");

        foreach (var invalid in new[]
        {
            "", "\x1b[<0;1;1X", "\x1b[<0;0;1M", "\x1b[<0;1;0M",
            "\x1b[<0;1;2147483648M", "\x1b[<0;1;1M\u0000", "0;1;1"
        })
            Eq(false, MouseInput.TryParse(invalid, out _), "invalid mouse report is rejected");
    }

    private static void PasteInterop()
    {
        var parser = new PasteKeys();
        var now = 0L;
        void Feed(string text, bool keyIdentities = false)
        {
            foreach (var c in text)
            {
                var key = keyIdentities ? TypedKey(c) : c == '\x1b' ? ConsoleKey.Escape : (ConsoleKey)0;
                parser.Feed(new(c, key, false, false, false), ++now);
            }
        }

        Feed("\x1b[<64;3;2M\x1b[<0;4;3M", keyIdentities: true);
        Eq(true, parser.TryReadMouse(out var wheel), "VT mouse event reaches mouse queue");
        Eq(MouseWheel.Up, wheel.Wheel, "paste parser preserves wheel");
        Eq(true, parser.TryReadMouse(out var click), "mouse press reaches mouse queue");
        Eq(true, click.LeftClick, "paste parser preserves click");
        Eq(null, parser.Read(), "mouse reports never become keyboard shortcuts");

        var altCsi = new PasteKeys();
        altCsi.Feed(new('[', (ConsoleKey)0, false, true, false), ++now);
        foreach (var c in "<64;1;1M") altCsi.Feed(new(c, TypedKey(c), false, false, false), ++now);
        Eq(true, altCsi.TryReadMouse(out var altWheel), "Alt+[ mouse introducer is reassembled");
        Eq(MouseWheel.Up, altWheel.Wheel, "Alt+[ mouse wheel direction");

        Feed("\x1b[200~hello\r\nworld\x1b[201~");
        var pasted = new StringBuilder();
        while (parser.Read() is { } key)
        {
            Eq((ConsoleKey)0, key.Key, "bracketed paste remains literal");
            pasted.Append(key.KeyChar);
        }
        Eq("hello\nworld", pasted.ToString(), "bracketed paste still works after mouse input");
        Feed("\x1b", keyIdentities: true);
        parser.FlushExpired(now + 100);
        Eq(ConsoleKey.Escape, parser.Read()?.Key, "mouse parser does not swallow Escape");
        Feed("\x1bOQ", keyIdentities: true);
        Eq(ConsoleKey.F2, parser.Read()?.Key, "function keys survive mouse input");
    }

    private static void LayoutHits()
    {
        var choices = new[]
        {
            new FuiChoice("a", "第一项"), new FuiChoice("b", "第二项"), new FuiChoice("c", "第三项")
        };
        var view = new TuiView("/demo", "S1", "model", "就绪", [new("info", "对话")], "", 0, 0, null,
            Overlay: new("选择器", "", choices, 1));
        var layout = new TerminalLayout();
        var narrow = layout.Arrange(view, 60, 17);
        var regions = narrow.Mouse!;
        var first = new MouseEvent(0, 1, regions.PickerTop + 1, false, MouseWheel.None, false, true);
        var hit = MouseInput.HitTest(regions, first);
        Eq(MouseHitKind.PickerItem, hit.Kind, "picker row hit");
        Eq(regions.PickerStart, hit.Index, "picker hit maps visible row to item index");
        Eq(true, narrow.Rows[first.Row].Text.Contains(choices[hit.Index].Label, StringComparison.Ordinal),
            "picker hit row matches the painted item");
        Eq(MouseHitKind.None, MouseInput.HitTest(regions,
            first with { Row = regions.PickerTop + regions.PickerVisible + 1 }).Kind, "outside picker rows is safe");
        Eq(MouseHitKind.None, MouseInput.HitTest(regions, first with { Column = 10_000 }).Kind, "out-of-range column is safe");

        var wide = layout.Arrange(view, 100, 17);
        var wideRegions = wide.Mouse!;
        Eq(MouseHitKind.None, MouseInput.HitTest(wideRegions,
            first with { Column = wideRegions.PickerColumns, Row = wideRegions.PickerTop + 1 }).Kind,
            "split picker leaves the right detail pane non-clickable");
        Eq(MouseHitKind.PickerItem, MouseInput.HitTest(wideRegions,
            first with { Column = 0, Row = wideRegions.PickerTop + 1 }).Kind, "split picker left pane remains clickable");

        var approval = new TuiView("/demo", "S1", "model", "审批", [], "", 0, 0,
            "是否允许执行工具 write_file？\n{\"path\":\"review.txt\"}");
        var armed = layout.Arrange(approval, 66, 7);
        Eq(true, armed.CanApprove, "approval details are painted");
        var allow = MouseInput.HitTest(armed.Mouse, new MouseEvent(0, armed.Mouse!.ApprovalAllowStart,
            armed.Mouse.ApprovalRow, false, MouseWheel.None, false, true));
        Eq(MouseHitKind.ApprovalAllow, allow.Kind, "exact approval allow hit");
        var reject = MouseInput.HitTest(armed.Mouse, new MouseEvent(0, armed.Mouse.ApprovalRejectStart,
            armed.Mouse.ApprovalRow, false, MouseWheel.None, false, true));
        Eq(MouseHitKind.ApprovalReject, reject.Kind, "exact approval reject hit");
        var tooShort = layout.Arrange(approval, 66, 6);
        Eq(false, tooShort.CanApprove, "approval without arguments is not armed");
        Eq(MouseHitKind.None, MouseInput.HitTest(tooShort.Mouse,
            new MouseEvent(0, tooShort.Mouse!.ApprovalAllowStart, tooShort.Mouse.ApprovalRow, false,
                MouseWheel.None, false, true)).Kind, "unarmed approval has no allow hit");
    }

    private static void ControllerInput()
    {
        var session = new TestSession();
        var ui = new TuiController(session, CancellationToken.None);
        ui.Mouse(new MouseEvent(0, 0, 0, false, MouseWheel.Up, false, false), 10, 30, 5, MouseHit.None);
        Eq(9, ui.View().Scroll, "wheel up maps to PageUp");
        ui.Mouse(new MouseEvent(0, 0, 0, false, MouseWheel.Down, false, false), 10, 30, 5, MouseHit.None);
        Eq(0, ui.View().Scroll, "wheel down maps to PageDown");

        Type(ui, "/commands");
        Key(ui, ConsoleKey.Enter);
        var picker = ui.View().Overlay!;
        Eq(true, picker.Items.Count > 1, "command picker opened");
        var target = 1;
        ui.Mouse(new MouseEvent(0, 1, 1, false, MouseWheel.None, false, true), 10, 30, picker.Items.Count,
            new MouseHit(MouseHitKind.PickerItem, target));
        var afterClick = ui.View().Overlay!;
        Eq(target, afterClick.Selected, "click changes picker focus");
        Eq(picker.Items.Count, afterClick.Items.Count, "click does not execute picker choice");
        Eq(0, session.Sent.Count, "picker focus is not submission");

        MouseInput.ClearPending();
        MouseInput.Stage(new MouseEvent(0, 1, 1, false, MouseWheel.None, false, true),
            new MouseHit(MouseHitKind.PickerItem, 2));
        ui.Key(MouseInput.RoutedKeyInfo, 10, 30, picker.Items.Count);
        Eq(2, ui.View().Overlay!.Selected, "routed mouse key reaches picker focus");
        Eq(0, session.Sent.Count, "routed click still does not execute");
    }

    private static void ApprovalSafety()
    {
        var session = new TestSession();
        var ui = new TuiController(session, CancellationToken.None);
        session.Channel.Writer.TryWrite(new TuiEvent.Approval("a", "是否允许执行工具 write_file？\n{\"path\":\"review.txt\"}"));
        ui.Update();
        var unarmed = new TerminalLayout().Arrange(ui.View(), 66, 6);
        Eq(false, unarmed.CanApprove, "short frame cannot arm approval");
        ui.Painted(30, canApprove: false);
        ui.Mouse(new MouseEvent(0, 0, 0, false, MouseWheel.None, false, true), 10, 30, 5,
            new MouseHit(MouseHitKind.ApprovalAllow));
        Eq(0, session.Answers.Count, "mouse cannot bypass an unarmed approval");

        ui.Mouse(new MouseEvent(0, 0, 0, false, MouseWheel.None, false, true), 10, 30, 5,
            new MouseHit(MouseHitKind.PickerItem, 0));
        Eq(0, session.Answers.Count, "picker click cannot approve an approval");

        var armed = new TerminalLayout().Arrange(ui.View(), 66, 7);
        Eq(true, armed.CanApprove, "tall frame paints approval arguments");
        ui.Painted(30, canApprove: true);
        var allow = new MouseHit(MouseHitKind.ApprovalAllow);
        ui.Mouse(new MouseEvent(0, 0, 0, false, MouseWheel.None, false, true), 10, 30, 5, allow);
        Eq(1, session.Answers.Count, "exact approved mouse hit answers once");
        Eq(("a", true), session.Answers[0], "mouse allow uses the shared approval gate");
        ui.Mouse(new MouseEvent(0, 0, 0, false, MouseWheel.None, false, true), 10, 30, 5, allow);
        Eq(1, session.Answers.Count, "duplicate mouse click cannot answer twice");
    }

    private static void LifecycleAndBounds()
    {
        Eq(true, TerminalSurface.MouseOnSequence.Contains("?1000h", StringComparison.Ordinal) &&
            TerminalSurface.MouseOnSequence.Contains("?1006h", StringComparison.Ordinal), "startup enables SGR mouse");
        Eq(true, TerminalSurface.RestoreSequence(true).Contains(TerminalSurface.MouseOffSequence, StringComparison.Ordinal),
            "dispose restoration turns mouse off");
        Eq(true, TerminalSurface.MouseOffSequence.IndexOf("?1006l", StringComparison.Ordinal) <
            TerminalSurface.MouseOffSequence.IndexOf("?1000l", StringComparison.Ordinal), "SGR is disabled before tracking");
        Eq(false, MouseInput.CanEnableMouse(true, false, true, true, null), "redirected input cannot enable mouse");
        Eq(false, MouseInput.CanEnableMouse(false, true, true, true, null), "redirected output cannot enable mouse");
        Eq(false, MouseInput.CanEnableMouse(false, false, false, true, null), "unattached Windows console cannot enable mouse");
        Eq(false, MouseInput.CanEnableMouse(false, false, false, false, "dumb"), "dumb terminal cannot enable mouse");
        Eq(true, MouseInput.CanEnableMouse(false, false, false, false, "xterm-256color"), "interactive terminal can enable mouse");

        var layout = new TerminalLayout();
        var tiny = layout.Arrange(new TuiView("/", "S", "M", "", [], "", 0, 0, null), 1, 1);
        Eq(false, MouseInput.HitTest(tiny.Mouse, new MouseEvent(0, 0, 0, false, MouseWheel.None, false, true)).Kind
            == MouseHitKind.PickerItem, "tiny frame cannot fabricate a picker hit");
        Eq(false, MouseInput.TryParse("\x1b[<0;1;1M".Replace('0', '\u0001'), out _), "control character is rejected");
    }

    private static void Type(TuiController ui, string text)
    {
        foreach (var c in text) ui.Key(new(c, (ConsoleKey)0, false, false, false), 10, 30);
    }

    private static void Key(TuiController ui, ConsoleKey key) =>
        ui.Key(new('\0', key, false, false, false), 10, 30);

    private static ConsoleKey TypedKey(char c) => c switch
    {
        >= 'a' and <= 'z' => ConsoleKey.A + (c - 'a'),
        >= 'A' and <= 'Z' => ConsoleKey.A + (c - 'A'),
        >= '0' and <= '9' => ConsoleKey.D0 + (c - '0'),
        '[' => ConsoleKey.Oem4,
        '<' => ConsoleKey.OemComma,
        ';' => ConsoleKey.Oem1,
        _ => (ConsoleKey)0
    };

    private static void Eq<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected [{expected}], got [{actual}]");
    }

    private sealed class TestSession : ITuiSession
    {
        public string Project => "project";
        public string Id => "session-id";
        public string? Model => "loopback";
        public IReadOnlyList<TuiLine> History => [];
        public Channel<TuiEvent> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<TuiEvent>();
        public ChannelReader<TuiEvent> Events => Channel.Reader;
        public List<string> Sent { get; } = [];
        public List<(string Id, bool Allow)> Answers { get; } = [];
        public Task SendAsync(string text, CancellationToken ct) { Sent.Add(text); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(string id, bool allow, CancellationToken ct) { Answers.Add((id, allow)); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
