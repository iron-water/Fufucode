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
        UndoAndRedo();
        PasteUndo();
        KillAndYank();
        UnicodeKillAndYank();
        HistoryAndTake();
        UndoLimit();
        LayoutAndDiff();
        FormulaTests.Run();
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

    private static void UndoAndRedo()
    {
        var c = new Composer();
        Type(c, "abc");
        c.Edit(Key(ConsoleKey.Z, control: true));
        Eq("", c.Text, "a continuous typed run is one undo unit");
        Eq(0, c.Cursor, "undo restores its caret");

        c.Edit(Key(ConsoleKey.Z, control: true, shift: true));
        Eq("abc", c.Text, "redo restores the typed run");
        Eq(3, c.Cursor, "redo restores its caret");

        c.Edit(Key(ConsoleKey.LeftArrow));
        c.Edit(Key(ConsoleKey.Z, control: true));
        Eq("", c.Text, "moving the caret does not add an undo unit");

        c.Edit(Key(ConsoleKey.Z, control: true, shift: true));
        c.Edit(Key(ConsoleKey.Z, control: true));
        Type(c, "d");
        c.Edit(Key(ConsoleKey.Z, control: true, shift: true));
        Eq("d", c.Text, "new input clears redo");

        var multiple = new Composer();
        Type(multiple, "a");
        multiple.Edit(Key(ConsoleKey.LeftArrow));
        multiple.Edit(Key(ConsoleKey.RightArrow));
        Type(multiple, "b");
        multiple.Edit(Key(ConsoleKey.Z, control: true));
        multiple.Edit(Key(ConsoleKey.Z, control: true));
        Eq("", multiple.Text, "multiple edit units undo in order");
        multiple.Edit(Key(ConsoleKey.Z, control: true, shift: true));
        multiple.Edit(Key(ConsoleKey.Z, control: true, shift: true));
        Eq("ab", multiple.Text, "multiple edit units redo in order");
    }

    private static void PasteUndo()
    {
        var c = new Composer();
        Paste(c, "first\nsecond");
        c.Edit(Key(ConsoleKey.Z, control: true));
        Eq("", c.Text, "a literal paste is one undo unit");
        c.Edit(Key(ConsoleKey.Z, control: true, shift: true));
        Eq("first\nsecond", c.Text, "redo restores the paste");

        Type(c, "!");
        c.Edit(Key(ConsoleKey.Z, control: true));
        Eq("first\nsecond", c.Text, "typed input after a paste is its own unit");
        c.Edit(Key(ConsoleKey.Z, control: true));
        Eq("", c.Text, "paste remains independently undoable");

        var unicode = new Composer();
        Paste(unicode, "👩‍💻");
        unicode.Edit(Key(ConsoleKey.Z, control: true));
        Eq("", unicode.Text, "pasted surrogate cluster undoes whole");
        unicode.Edit(Key(ConsoleKey.Z, control: true, shift: true));
        Eq("👩‍💻", unicode.Text, "pasted surrogate cluster redoes whole");

        var parser = new PasteKeys();
        var now = 0L;
        foreach (var ch in "\x1b[200~中\r\nsecond\x1b[201~")
            parser.Feed(new(ch, ch == '\x1b' ? ConsoleKey.Escape : (ConsoleKey)0, false, false, false), ++now);
        var bracketed = new Composer();
        while (parser.Read() is { } key) bracketed.Edit(key);
        Eq("中\nsecond", bracketed.Text, "bracketed paste reaches the composer as literal text");
        bracketed.Edit(Key(ConsoleKey.Z, control: true));
        Eq("", bracketed.Text, "bracketed paste is one undo unit");
    }

    private static void KillAndYank()
    {
        var lineStart = new Composer();
        Type(lineStart, "alpha beta");
        lineStart.Edit(Key(ConsoleKey.U, control: true));
        Eq("", lineStart.Text, "Ctrl+U kills to line start");
        lineStart.Edit(Key(ConsoleKey.Y, control: true));
        Eq("alpha beta", lineStart.Text, "Ctrl+Y yanks Ctrl+U text");

        var lineEnd = new Composer();
        Type(lineEnd, "gamma delta");
        lineEnd.Edit(Key(ConsoleKey.Home));
        lineEnd.Edit(Key(ConsoleKey.K, control: true));
        Eq("", lineEnd.Text, "Ctrl+K kills to line end");
        lineEnd.Edit(Key(ConsoleKey.Y, control: true));
        Eq("gamma delta", lineEnd.Text, "Ctrl+Y yanks Ctrl+K text");

        var word = new Composer();
        Type(word, "one two");
        word.Edit(Key(ConsoleKey.W, control: true));
        Eq("one ", word.Text, "Ctrl+W kills the previous word");
        word.Edit(Key(ConsoleKey.Y, control: true));
        Eq("one two", word.Text, "Ctrl+Y yanks Ctrl+W text");

        var merged = new Composer();
        Type(merged, "one two three");
        merged.Edit(Key(ConsoleKey.W, control: true));
        merged.Edit(Key(ConsoleKey.W, control: true));
        Eq("one ", merged.Text, "consecutive Ctrl+W kills both words");
        merged.Edit(Key(ConsoleKey.Y, control: true));
        Eq("one two three", merged.Text, "same-direction kills merge in the ring");

        var pop = new Composer();
        Type(pop, "new");
        pop.Edit(Key(ConsoleKey.U, control: true));
        Type(pop, "old");
        pop.Edit(Key(ConsoleKey.U, control: true));
        pop.Edit(Key(ConsoleKey.Y, control: true));
        Eq("old", pop.Text, "yank starts with the newest kill");
        pop.Edit(Key(ConsoleKey.Y, alt: true));
        Eq("new", pop.Text, "Alt+Y replaces it with the older kill");
        pop.Edit(Key(ConsoleKey.Y, alt: true));
        Eq("old", pop.Text, "Alt+Y wraps through the ring");
        pop.Edit(Key(ConsoleKey.Z, control: true));
        Eq("new", pop.Text, "yank-pop is undoable");
        pop.Edit(Key(ConsoleKey.Z, control: true, shift: true));
        Eq("old", pop.Text, "yank-pop is redoable");
    }

    private static void UnicodeKillAndYank()
    {
        const string text = "中e\u0301👩‍💻";
        var line = new Composer();
        Type(line, text);
        line.Edit(Key(ConsoleKey.Home));
        line.Edit(Key(ConsoleKey.K, control: true));
        Eq("", line.Text, "Ctrl+K removes complete Unicode clusters");
        line.Edit(Key(ConsoleKey.Y, control: true));
        Eq(text, line.Text, "yank restores complete Unicode clusters");
        Eq(text.Length, line.Cursor, "yank caret stays after the complete cluster");
        line.Edit(Key(ConsoleKey.Z, control: true));
        Eq("", line.Text, "Unicode yank is undoable");
        line.Edit(Key(ConsoleKey.Z, control: true, shift: true));
        Eq(text, line.Text, "Unicode yank is redoable");

        var word = new Composer();
        Type(word, "前 e\u0301 👩‍💻");
        word.Edit(Key(ConsoleKey.W, control: true));
        Eq("前 e\u0301 ", word.Text, "Ctrl+W removes the whole emoji cluster");
        word.Edit(Key(ConsoleKey.Y, control: true));
        Eq("前 e\u0301 👩‍💻", word.Text, "yank restores the whole emoji cluster");

        var composing = new Composer();
        Type(composing, "前 e\u0301");
        composing.Edit(Key(ConsoleKey.W, control: true));
        Eq("前 ", composing.Text, "Ctrl+W removes the whole combining cluster");
        composing.Edit(Key(ConsoleKey.Y, control: true));
        Eq("前 e\u0301", composing.Text, "yank restores the combining cluster");
    }

    private static void HistoryAndTake()
    {
        var c = new Composer();
        Type(c, "submitted");
        Eq("submitted", c.Take(), "take records the submitted draft");
        c.Edit(Key(ConsoleKey.Z, control: true));
        Eq("", c.Text, "undo cannot revive a taken draft");

        Type(c, "working");
        c.Edit(Key(ConsoleKey.UpArrow));
        Eq("submitted", c.Text, "up recalls submitted history");
        c.Edit(Key(ConsoleKey.Z, control: true));
        Eq("submitted", c.Text, "history recall is not an undo unit");
        c.Edit(Key(ConsoleKey.DownArrow));
        Eq("working", c.Text, "down restores the unfinished draft");
        c.Edit(Key(ConsoleKey.Z, control: true));
        Eq("working", c.Text, "history navigation clears the prior draft edit stack");

        Eq("working", c.Take(), "take the second draft");
        c.Edit(Key(ConsoleKey.Z, control: true));
        Eq("", c.Text, "a second take also clears undo");
    }

    private static void UndoLimit()
    {
        var c = new Composer();
        for (var i = 0; i < 101; i++)
        {
            Type(c, "a");
            if (i < 100)
            {
                c.Edit(Key(ConsoleKey.LeftArrow));
                c.Edit(Key(ConsoleKey.RightArrow));
            }
        }
        Eq(101, c.Text.Length, "one edit unit per character");
        for (var i = 0; i < 100; i++) c.Edit(Key(ConsoleKey.Z, control: true));
        Eq("a", c.Text, "undo stack keeps its newest hundred snapshots");
        c.Edit(Key(ConsoleKey.Z, control: true));
        Eq("a", c.Text, "undo beyond the retained depth is a no-op");
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
        var write = view with { Approval = "是否允许执行工具 write_file？\n{\"path\":\"review.txt\"}" };
        var titleOnly = layout.Arrange(write, 66, 6);
        Eq(true, titleOnly.Rows.Any(r => r.Ink == Ink.Approval && r.Text.Contains("write_file")), "six rows show the tool name");
        Eq(false, titleOnly.CanApprove, "a tool name alone cannot arm a grant");
        Eq(false, titleOnly.Rows.Any(r => r.Text.Contains("F2")), "a title-only frame offers no allow shortcut");
        Eq(true, layout.Arrange(write, 66, 7).CanApprove, "the grant is available when the argument row is painted");
        var wrappedTitle = write with { Approval = new string('中', 60) + "\n{\"path\":\"review.txt\"}" };
        Eq(false, layout.Arrange(wrappedTitle, 24, 11).CanApprove, "wrapping a title does not turn its continuation into arguments");
        Eq(true, layout.Arrange(wrappedTitle, 24, 30).CanApprove, "a taller viewport can show arguments below a wrapped title");
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

    private static ConsoleKeyInfo Key(ConsoleKey key, bool control = false, bool shift = false, bool alt = false) =>
        new('\0', key, shift, alt, control);

    private static void Type(Composer c, string text)
    {
        foreach (var ch in text) c.Edit(new(ch, TypedKey(ch), false, false, false));
    }

    private static void Paste(Composer c, string text)
    {
        foreach (var ch in text) c.Edit(new(ch, (ConsoleKey)0, false, false, false));
    }

    private static ConsoleKey TypedKey(char c) => c switch
    {
        >= 'a' and <= 'z' => ConsoleKey.A + (c - 'a'),
        >= 'A' and <= 'Z' => ConsoleKey.A + (c - 'A'),
        ' ' => ConsoleKey.Spacebar,
        '\t' => ConsoleKey.Tab,
        '\n' or '\r' => ConsoleKey.Enter,
        _ => ConsoleKey.Oem1
    };

    private static void Eq<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{context}: expected [{expected}], got [{actual}]");
    }
}
