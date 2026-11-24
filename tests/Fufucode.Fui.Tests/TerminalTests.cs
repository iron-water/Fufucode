using System.Text;
using Fufucode.Fui;

namespace Fufucode.Fui.Tests;

internal static class TerminalTests
{
    internal static void Run()
    {
        CleanAndMeasure();
        EditAndRecall();
        LiteralPaste();
        LayoutAndDiff();
    }

    private static void CleanAndMeasure()
    {
        var hostile = "A\x1b[31mB\x1b[0m\u009b2J\x1b]52;c;discard\a\x1bPdata\x1b\\\u009fdiscard\u009c\b\u202eC\tD\r\nE\rF\x1b[12";
        Eq("ABC    D\nE\nF", TerminalText.Clean(hostile), "clean control strings and CRLF");
        Eq("safe", TerminalText.Clean("safe\x1b]unterminated"), "unfinished OSC is never displayed");
        Eq("A    中", TerminalText.Clean("A\t\x1b[31m中", 7, out var caret), "clean draft");
        Eq(5, caret, "map raw caret to sanitized cells");
        Eq(7, TerminalText.Width("中e\u0301👩‍💻🇹🇼"), "grapheme display widths");
        Eq(2, TerminalText.Width("❤️"), "variation selector");
        Eq("a…", TerminalText.Clip("a中c", 2), "no half CJK in clipping");
        Eq("…中", TerminalText.Clip("path/中", 3, tail: true), "path tail");
        var lines = new List<TerminalText.Slice>();
        TerminalText.Wrap("中a\n👩‍💻", 1, 0, lines);
        Eq("…|a|…", string.Join('|', lines.Select(x => x.Text)), "one-cell viewport progresses");
    }

    private static void EditAndRecall()
    {
        var c = new Composer();
        Type(c, "中e\u0301👩‍💻");
        c.Edit(Key(ConsoleKey.LeftArrow));
        Eq(3, c.Cursor, "skip entire emoji cluster");
        c.Edit(Key(ConsoleKey.Backspace));
        Eq("中👩‍💻", c.Text, "remove combining cluster");
        c.Edit(Key(ConsoleKey.Delete));
        Eq("中", c.Take(), "right delete and take");
        Type(c, "aa");
        c.Edit(new('\r', ConsoleKey.Enter, false, true, false));
        Type(c, "中b");
        c.Edit(Key(ConsoleKey.UpArrow));
        Eq(2, c.Cursor, "multiline up before recalling history");
        c.Edit(Key(ConsoleKey.DownArrow));
        Eq(5, c.Cursor, "restore preferred visual column");
        c.Edit(Key(ConsoleKey.Home));
        c.Edit(Key(ConsoleKey.LeftArrow));
        c.Edit(Key(ConsoleKey.UpArrow));
        Eq("中", c.Text, "older history");
        c.Edit(Key(ConsoleKey.DownArrow));
        Eq("aa\n中b", c.Text, "restore unsent draft");
        Eq(2, c.Cursor, "restore draft caret");
        c.Clear();
        Type(c, "foo bar");
        c.Edit(Key(ConsoleKey.LeftArrow, control: true));
        Eq(4, c.Cursor, "word left");
        c.Edit(Key(ConsoleKey.Backspace, control: true));
        Eq("bar", c.Text, "delete preceding word");
        c.Edit(Key(ConsoleKey.End));
        c.Edit(new('\n', ConsoleKey.J, false, false, true));
        Eq("bar\n", c.Take(), "Ctrl+J is not a submission");
    }

    private static void LiteralPaste()
    {
        var parser = new PasteKeys();
        var now = 0L;
        void Raw(string s) { foreach (var c in s) parser.Feed(new(c, c == '\x1b' ? ConsoleKey.Escape : (ConsoleKey)0, false, false, false), ++now); }
        Raw("\x1b[200~中\r\ny\n/quit\x1b[31m");
        parser.Feed(Key(ConsoleKey.F2), ++now);
        if (parser.Read() != null) throw new Exception("incomplete paste dispatched a command");
        Raw("\x1b[201~");
        parser.Feed(Key(ConsoleKey.F2), ++now);
        var actual = new StringBuilder();
        var functionKey = false;
        while (parser.Read() is { } key)
        {
            if (key.Key == ConsoleKey.F2) { functionKey = true; break; }
            Eq((ConsoleKey)0, key.Key, "pasted keystrokes have no command identity");
            actual.Append(key.KeyChar);
        }
        Eq(true, functionKey, "physical F2 follows literal paste");
        Eq("中\ny\n/quit", actual.ToString(), "bracketed paste content");
        Raw("\x1b"); parser.FlushExpired(now + 100);
        Eq(ConsoleKey.Escape, parser.Read()?.Key, "standalone Escape is eventually delivered");
        void Vt(string s) { foreach (var c in s) parser.Feed(new(c, (ConsoleKey)0, false, false, false), ++now); }
        Vt("\x1b[A\x1b[1;5D\x1b[6~\x1bOP\x1bOQ\x1bOR\x1b\r\n\x03\x04");
        foreach (var (code, mods) in new (ConsoleKey, ConsoleModifiers)[]
        {
            (ConsoleKey.UpArrow, 0), (ConsoleKey.LeftArrow, ConsoleModifiers.Control), (ConsoleKey.PageDown, 0),
            (ConsoleKey.F1, 0), (ConsoleKey.F2, 0), (ConsoleKey.F3, 0), (ConsoleKey.Enter, ConsoleModifiers.Alt),
            (ConsoleKey.J, ConsoleModifiers.Control), (ConsoleKey.C, ConsoleModifiers.Control), (ConsoleKey.D, ConsoleModifiers.Control)
        })
        {
            var key = parser.Read();
            Eq(code, key?.Key, "VT key"); Eq(mods, key?.Modifiers, "VT modifier");
        }
        Vt("\x1b[200~\x1bOQ\r/quit\x1b[201~");
        while (parser.Read() is { } key) Eq((ConsoleKey)0, key.Key, "pasted VT function keys never become commands");
        Vt("\x1b"); parser.FlushExpired(now + 100);
        Eq(ConsoleKey.Escape, parser.Read()?.Key, "raw Escape");
    }

    private static void LayoutAndDiff()
    {
        var layout = new TerminalLayout();
        var lines = Enumerable.Range(0, 14).Select(i => new TuiLine("info", $"message-{i:D2}")).ToList();
        var view = new TuiView("C:/projects/示例", "session-abc", "model-a", "生成中", lines, "中e\u0301", 3, 0, null);
        var frame = layout.Arrange(view, 80, 9);
        Eq(6, frame.ViewportRows, "rows reserved for header/status/composer");
        Eq(8, frame.MaxScroll, "scroll extent");
        Eq(true, frame.Rows[1].Text.EndsWith("message-08"), "follow newest");
        Eq(true, frame.Rows[6].Text.EndsWith("message-13"), "last visible message");
        Eq(true, frame.Rows[0].Text.Contains("示例") && frame.Rows[0].Text.Contains("model-a") && frame.Rows[0].Text.Contains("session-abc"), "header metadata");
        Eq(true, frame.Rows[0].Text.StartsWith("FUI ", StringComparison.Ordinal), "terminal client brand");
        Eq(5, frame.CursorColumn, "caret cell counts CJK, not UTF-16");
        Eq("", TerminalSurface.Diff(frame, layout.Arrange(view, 80, 9), true), "idle frame writes nothing");
        var older = layout.Arrange(view with { Scroll = int.MaxValue }, 80, 9);
        Eq(true, older.Rows[1].Text.EndsWith("message-00"), "scroll clamp at head");
        Eq(frame.Rows[^1], older.Rows[^1], "composer fixed during scrolling");
        var streaming = view with { Lines = [.. lines.Take(13), new("info", "message-13 revised")] };
        var next = layout.Arrange(streaming, 80, 9);
        var diff = TerminalSurface.Diff(frame, next, true);
        Eq(true, diff.Contains("message-13 revised") && !diff.Contains("message-08") && !diff.Contains("\x1b[2J") && !diff.Contains("示例"), "only changed line is painted");
        Eq(true, TerminalSurface.Diff(next, layout.Arrange(view, 35, 8), true).Contains("\x1b[2J"), "resize clears stale geometry");
        var approval = view with { Approval = new string('中', 240) };
        var prompt = layout.Arrange(approval, 38, 11);
        Eq(true, prompt.Rows.Any(r => r.Ink == Ink.Approval && r.Text.StartsWith("! F2")), "approval action visible");
        Eq(true, prompt.CanApprove, "painted request details enable an explicit grant");
        Eq(false, layout.Arrange(approval, 80, 2).CanApprove, "keys alone cannot arm a grant");
        Eq(false, layout.Arrange(approval, 7, 20).CanApprove, "clipped action keys cannot arm a grant");
        foreach (var (w, h) in new[] { (1, 1), (2, 2), (9, 4), (38, 11), (100, 25) })
        {
            var tiny = layout.Arrange(approval with { Draft = "中\n\t👩‍💻", Cursor = 100 }, w, h);
            Eq(h, tiny.Rows.Length, "window height");
            Eq(true, tiny.CursorRow >= -1 && tiny.CursorRow < h && tiny.CursorColumn >= 0 && tiny.CursorColumn < w, "caret inside viewport");
            Eq(true, tiny.Rows.All(r => TerminalText.Width(r.Text) < w && !r.Text.Any(char.IsControl)), "no overflow or terminal injection");
        }
        var working = new TerminalLayout();
        var text = "";
        foreach (var chunk in new[] { "abcdefghijklmno", "e", "\u0301", "\n中", "👨", "‍👩‍👧‍👦", "\x1b[3", "1mOK", "\x1b]0;", "hidden", "\aVISIBLE", "\r\nlast\n" })
        {
            text += chunk;
            var sample = view with { Lines = [new("assistant", text)] };
            var fresh = new TerminalLayout().Arrange(sample, 17, 14);
            Eq(true, fresh.Rows.SequenceEqual(working.Arrange(sample, 17, 14).Rows), "streaming equals fresh wrap, including split controls/graphemes");
        }
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, bool control = false) => new('\0', key, false, false, control);
    private static void Type(Composer c, string text) { foreach (var ch in text) c.Edit(new(ch, (ConsoleKey)0, false, false, false)); }
    private static void Eq<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{context}: expected [{expected}], got [{actual}]");
    }
}
