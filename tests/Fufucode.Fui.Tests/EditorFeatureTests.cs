using System.Text;
using System.Threading.Channels;
using Fufucode.Fui;

namespace Fufucode.Fui.Tests;

internal static class EditorFeatureTests
{
    internal static void Run()
    {
        SelectionAndGraphemes();
        SelectionDisplayAndMouse();
        Completions();
        Emoji();
        Vim();
        ClipboardIntegration();
        ControllerCompletionAndVim();
    }

    private static void SelectionAndGraphemes()
    {
        var c = new Composer();
        Type(c, "A中e\u0301👩‍💻Z");
        Key(c, ConsoleKey.A, ctrl: true);
        Eq(c.Text, c.SelectedText, "Ctrl+A selects the whole draft");
        Eq(0, c.SelectionStart, "select-all start");
        Eq(c.Text.Length, c.SelectionEnd, "select-all end");

        Key(c, ConsoleKey.LeftArrow, shift: true);
        Eq(c.Text.Length - 1, c.Cursor, "Shift+Left moves one grapheme from the end");
        Eq("A中e\u0301👩‍💻", c.SelectedText, "selection keeps a whole surrogate pair");
        c.ClearSelection();
        Key(c, ConsoleKey.Home);
        Key(c, ConsoleKey.RightArrow, shift: true);
        Key(c, ConsoleKey.RightArrow, shift: true);
        Eq("A中", c.SelectedText, "Shift+Right extends through wide glyphs");
        Type(c, "换");
        Eq("换e\u0301👩‍💻Z", c.Text, "typing replaces the selected wide-character range");
        Key(c, ConsoleKey.Z, ctrl: true);
        Eq("A中e\u0301👩‍💻Z", c.Text, "selection replacement is one undo step");
        Eq("A中", c.SelectedText, "undo restores the replaced selection");
        Key(c, ConsoleKey.Z, ctrl: true, shift: true);
        Eq("换e\u0301👩‍💻Z", c.Text, "selection replacement redo");

        var lines = new Composer();
        lines.InsertPastedText("one\ntwo\nthree\nfour");
        Key(lines, ConsoleKey.Home, ctrl: true);
        Key(lines, ConsoleKey.PageDown, shift: true);
        Eq(0, lines.SelectionStart, "Shift+PageDown starts at the caret");
        Require(lines.SelectionEnd > "one\ntwo".Length, "Shift+PageDown extends by viewport rows");
        var pageEnd = lines.SelectionEnd;
        lines.Edit(Key(ConsoleKey.PageUp, shift: true), 1);
        Require(lines.SelectionEnd < pageEnd, "Shift+PageUp moves the active selection edge back");
        lines.ClearSelection();
        Key(lines, ConsoleKey.Home, ctrl: true);
        Key(lines, ConsoleKey.End);
        Key(lines, ConsoleKey.Home, shift: true);
        Eq("one", lines.SelectedText, "Shift+Home selects back to the line start");
        Key(lines, ConsoleKey.Home);
        Key(lines, ConsoleKey.End, shift: true);
        Eq("one", lines.SelectedText, "Shift+End selects to the line end");
        Key(lines, ConsoleKey.DownArrow, shift: true);
        Require(lines.SelectionEnd > 3, "Shift+Down extends a visual selection");

        var delete = new Composer();
        Type(delete, "中文👩‍💻");
        Key(delete, ConsoleKey.A, ctrl: true);
        Key(delete, ConsoleKey.Backspace);
        Eq("", delete.Text, "Backspace removes a complete selection");
        Key(delete, ConsoleKey.Z, ctrl: true);
        Eq("中文👩‍💻", delete.Text, "selection deletion undo restores Chinese and emoji");
    }

    private static void SelectionDisplayAndMouse()
    {
        var c = new Composer();
        c.Replace("a\t中e\u0301", 2);
        c.SetCursor(0);
        c.SetCursor(c.Text.Length, extend: true);
        var view = new TuiView("/p", "s", "m", "就绪", [], c.Text, c.Cursor, 0, null,
            SelectionStart: c.SelectionStart, SelectionEnd: c.SelectionEnd, EditorMode: "INSERT");
        var frame = new TerminalLayout().Arrange(view, 40, 8);
        var selected = frame.Rows.SelectMany(row => (IEnumerable<StyledSpan>?)row.Spans ?? [])
            .Where(span => span.Ink == Ink.Selection).Select(span => span.Text).ToArray();
        Require(selected.Any(text => text.Contains("中", StringComparison.Ordinal)),
            "layout paints the selected wide glyph with the selection style");
        Require(selected.Any(text => text.Contains("e\u0301", StringComparison.Ordinal)),
            "layout paints the selected combining grapheme with the selection style");
        var merged = new TerminalLayout().Arrange(view with
        {
            Overlay = new("选择器", "", [new FuiChoice("one", "候选")], 0)
        }, 60, 16);
        Require(merged.Rows.Any(row => row.Text.Contains("选择器", StringComparison.Ordinal)),
            "selection layout keeps the picker overlay");
        Require(merged.Rows.SelectMany(row => (IEnumerable<StyledSpan>?)row.Spans ?? [])
            .Any(span => span.Ink == Ink.Selection), "selection spans coexist with picker rows");

        var regions = frame.Mouse!;
        Eq(MouseHitKind.Composer, MouseInput.HitTest(regions, new MouseEvent(
            0, regions.ComposerPromptWidth + 1, regions.ComposerTop, false, MouseWheel.None, false, true)).Kind,
            "composer text is a mouse hit target");
        var click = MouseInput.HitTest(regions, new MouseEvent(
            0, regions.ComposerPromptWidth + 1, regions.ComposerTop, false, MouseWheel.None, false, true));
        Eq(1, click.Index, "mouse column maps through the prompt and tab expansion");

        var root = NewTempProject();
        try
        {
            var controller = new TuiController(new Session(root), CancellationToken.None);
            Type(controller, "abc");
            var controllerFrame = new TerminalLayout().Arrange(controller.View(), 30, 8);
            var controllerRegions = controllerFrame.Mouse!;
            var mouse = new MouseEvent(0, controllerRegions.ComposerPromptWidth + 2, controllerRegions.ComposerTop,
                false, MouseWheel.None, false, true);
            var hit = MouseInput.HitTest(controllerRegions, mouse);
            Eq(MouseHitKind.Composer, hit.Kind, "controller frame exposes composer clicks");
            controller.Mouse(mouse, 24, 40, 5, hit);
            Eq(hit.Index, controller.View().Cursor, "mouse click moves the composer caret");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Completions()
    {
        Require(FuiCompletionCatalog.TryWordToken("前面 term", 7, out var token), "word token extraction");
        Eq("term", token.Query, "word token query");
        var choices = FuiCompletionCatalog.WordChoices(token,
            ["terminal transcript"], ["terminal history"], ["src/widget-terminals.cs"]);
        Require(choices.Any(choice => choice.Value == "terminal"), "transcript/history produce word candidates");
        Require(choices.Any(choice => choice.Value == "src/widget-terminals.cs"), "project path produces a candidate");
        Eq(0, FuiCompletionCatalog.WordChoices(new CompletionToken(token.Start, token.End, "zz"),
            ["terminal transcript"], ["terminal history"], ["src/widget-terminals.cs"]).Count,
            "word candidates are filtered by the typed token");

        var root = NewTempProject();
        var dataRoot = NewTempDataRoot();
        try
        {
            var src = Directory.CreateDirectory(Path.Combine(root, "src"));
            var file = Path.Combine(src.FullName, "locked-widget.cs");
            File.WriteAllText(file, new string('x', 200_000));
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var files = new ProjectFiles(root, dataRoot);
                var listing = files.List(".", CancellationToken.None);
                Require(listing.Items.Any(item => item.Value == "src/"),
                    "baseline listing sees src: " + string.Join(',', listing.Items.Select(item => item.Value)));
                var paths = files.CompletionPaths(CancellationToken.None);
                Require(paths.Contains("src/locked-widget.cs", StringComparer.Ordinal),
                    "project path completion enumerates metadata without opening file contents: " + string.Join(',', paths));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static void Emoji()
    {
        Eq(true, EmojiShortcodes.All.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 30,
            "at least thirty stable emoji shortcodes");
        Require(EmojiShortcodes.Suggest("smi").Any(item => item.Name == "smile"), "emoji prefix suggestions");
        Eq("😄 done", EmojiShortcodes.ExpandCompletedShortcodes(":smile: done"), "known shortcode expands at submit");
        Eq("10:30 :unknown: http://x", EmojiShortcodes.ExpandCompletedShortcodes("10:30 :unknown: http://x"),
            "times, unknown codes and URLs keep literal colons");

        var root = NewTempProject();
        try
        {
            var controller = new TuiController(new Session(root), CancellationToken.None);
            Paste(controller, ":smi");
            Thread.Sleep(80);
            controller.Update();
            Eq("Emoji 短码 · 本地", controller.View().Overlay?.Title, "emoji popup opens from a local shortcode");
            Key(controller, ConsoleKey.Tab);
            Eq("😄", controller.View().Draft, "Tab accepts an emoji candidate");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Vim()
    {
        var c = new Composer();
        c.SetVimEnabled(true);
        Type(c, "abc");
        VimKey(c, '\x1b', ConsoleKey.Escape);
        Eq(VimEditorMode.Normal, c.VimMode, "Escape enters Normal");
        Eq("abc", c.Text, "Escape does not edit the buffer");
        VimKey(c, '\r', ConsoleKey.Enter);
        Eq("abc", c.Text, "Normal Enter is consumed");
        VimKey(c, 'z', ConsoleKey.Z);
        Eq("abc", c.Text, "unknown Normal keys do not edit or submit");
        VimKey(c, 'h', ConsoleKey.H);
        Eq(2, c.Cursor, "h moves left by one grapheme");
        VimKey(c, 'l', ConsoleKey.L);
        Eq(3, c.Cursor, "l moves right");
        VimKey(c, '0', ConsoleKey.D0);
        Eq(0, c.Cursor, "0 moves to line start");
        VimKey(c, '$', ConsoleKey.D4, shift: true);
        Eq(3, c.Cursor, "$ moves to line end");

        c.Replace("one two");
        c.SetCursor(0);
        VimKey(c, 'w', ConsoleKey.W);
        Eq(4, c.Cursor, "w moves to the next word");
        VimKey(c, 'b', ConsoleKey.B);
        Eq(0, c.Cursor, "b moves back by a word");
        VimKey(c, 'x', ConsoleKey.X);
        Eq("ne two", c.Text, "x deletes one grapheme");
        VimKey(c, 'u', ConsoleKey.U);
        Eq("one two", c.Text, "u restores an x deletion");
        VimKey(c, '$', ConsoleKey.D4, shift: true);
        VimKey(c, 'x', ConsoleKey.X);
        Eq("one tw", c.Text, "$ then x deletes the final grapheme, not the newline boundary");
        VimKey(c, 'u', ConsoleKey.U);
        VimKey(c, 'd', ConsoleKey.D);
        VimKey(c, 'd', ConsoleKey.D);
        Eq("", c.Text, "dd deletes the line");
        VimKey(c, 'p', ConsoleKey.P);
        Eq("one two", c.Text, "p pastes the linewise Vim register");
        VimKey(c, 'i', ConsoleKey.I);
        Eq(VimEditorMode.Insert, c.VimMode, "i returns to Insert");
        Type(c, "!");
        VimKey(c, '\x1b', ConsoleKey.Escape);
        Eq(VimEditorMode.Normal, c.VimMode, "Insert Escape returns to Normal");
        Eq("one two!", c.Text, "Insert text remains intact");

        var append = new Composer();
        append.SetVimEnabled(true);
        Type(append, "ac");
        VimKey(append, '\x1b', ConsoleKey.Escape);
        VimKey(append, '0', ConsoleKey.D0);
        VimKey(append, 'l', ConsoleKey.L);
        VimKey(append, 'a', ConsoleKey.A);
        Eq(VimEditorMode.Insert, append.VimMode, "a enters Insert after the cursor");
        Type(append, "b");
        Eq("acb", append.Text, "a inserts after the current grapheme");

        var open = new Composer();
        open.SetVimEnabled(true);
        Type(open, "one");
        VimKey(open, '\x1b', ConsoleKey.Escape);
        VimKey(open, 'o', ConsoleKey.O);
        Eq(VimEditorMode.Insert, open.VimMode, "o enters Insert on a new line");
        Type(open, "two");
        Eq("one\ntwo", open.Text, "o opens a line below");

        var vertical = new Composer();
        vertical.SetVimEnabled(true);
        vertical.InsertPastedText("abc\ndef");
        VimKey(vertical, '\x1b', ConsoleKey.Escape);
        VimKey(vertical, '0', ConsoleKey.D0);
        VimKey(vertical, 'j', ConsoleKey.J);
        Eq(4, vertical.Cursor, "j moves to the next line");
        VimKey(vertical, 'k', ConsoleKey.K);
        Eq(0, vertical.Cursor, "k moves to the previous line");
    }

    private static void ClipboardIntegration()
    {
        var root = NewTempProject();
        try
        {
            var clipboard = new FakeClipboard();
            var ui = new TuiController(new Session(root), CancellationToken.None,
                Path.Combine(root, ".data"), clipboard: clipboard);
            Type(ui, "中文e\u0301👩‍💻");
            Key(ui, ConsoleKey.A, ctrl: true);
            Key(ui, ConsoleKey.Escape);
            Eq(-1, ui.View().SelectionStart, "Esc clears the selection before any stop action");
            Eq("中文e\u0301👩‍💻", ui.View().Draft, "Esc clearing selection preserves the draft");
            Key(ui, ConsoleKey.A, ctrl: true);
            Key(ui, ConsoleKey.C, ctrl: true);
            Eq("中文e\u0301👩‍💻", clipboard.Text, "Ctrl+C copies a selected Unicode range");
            Eq("中文e\u0301👩‍💻", ui.View().Draft, "Ctrl+C does not clear the selected draft");
            Eq(false, ui.Exiting, "Ctrl+C with a selection does not exit");
            Key(ui, ConsoleKey.X, ctrl: true);
            Eq("", ui.View().Draft, "Ctrl+X cuts the selection");
            Key(ui, ConsoleKey.Z, ctrl: true);
            Eq("中文e\u0301👩‍💻", ui.View().Draft, "undo after cut preserves Unicode");
            Key(ui, ConsoleKey.Z, ctrl: true, shift: true);
            Eq("", ui.View().Draft, "redo after cut");
            Key(ui, ConsoleKey.Y, ctrl: true);
            Eq("中文e\u0301👩‍💻", ui.View().Draft, "kill ring remains usable after undo/redo");

            Key(ui, ConsoleKey.A, ctrl: true);
            clipboard.Read = "粘贴👩‍💻";
            Key(ui, ConsoleKey.V, ctrl: true);
            Eq("粘贴👩‍💻", ui.View().Draft, "Ctrl+V reads the Windows-compatible clipboard path and replaces selection");
            Key(ui, ConsoleKey.A, ctrl: true);
            clipboard.Read = "插入";
            Key(ui, ConsoleKey.Insert, shift: true);
            Eq("插入", ui.View().Draft, "Shift+Insert replaces the selection");

            Key(ui, ConsoleKey.A, ctrl: true);
            Paste(ui, "块粘贴");
            Eq("块粘贴", ui.View().Draft, "bracketed/literal paste replaces the selection");
            Key(ui, ConsoleKey.Z, ctrl: true);
            Eq("插入", ui.View().Draft, "pasted selection replacement is one undo step");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void ControllerCompletionAndVim()
    {
        var root = NewTempProject();
        try
        {
            var store = FuiSettingsStore.Load(Path.Combine(root, ".data"));
            var session = new Session(root);
            session.Transcript = "terminal completion source";
            var ui = new TuiController(session, CancellationToken.None, Path.Combine(root, ".data"), settings: store);
            Type(ui, "term");
            Key(ui, ConsoleKey.Spacebar, ctrl: true);
            Eq("词补全 · 本地", ui.View().Overlay?.Title, "Ctrl+Space opens local word candidates");
            Type(ui, "i");
            Require(ui.View().Overlay!.Items.Any(item => item.Value == "terminal"),
                "typing narrows the visible word candidates");
            Key(ui, ConsoleKey.Enter);
            Eq("terminal", ui.View().Draft, "Enter accepts a word candidate instead of sending");
            Eq(0, session.Sent.Count, "local completion never sends a message");

            Type(ui, " ");
            Key(ui, ConsoleKey.Spacebar, ctrl: true);
            Key(ui, ConsoleKey.Escape);
            Eq(null, ui.View().Overlay, "Escape closes word completion");

            store.Update(settings => settings with { VimMode = true, EmojiAutocomplete = "off" });
            var vimSession = new Session(root);
            var vim = new TuiController(vimSession, CancellationToken.None, Path.Combine(root, ".data"), store);
            Eq("INSERT", vim.View().EditorMode, "persisted Vim mode starts in Insert");
            Type(vim, "draft");
            Key(vim, ConsoleKey.Escape);
            Eq("NORMAL", vim.View().EditorMode, "Escape exposes Normal mode");
            Key(vim, ConsoleKey.Enter);
            Eq("draft", vim.View().Draft, "Normal Enter does not submit");
            Eq(0, vimSession.Sent.Count, "Normal Enter never reaches the session");
            VimKey(vim, 'z', ConsoleKey.Z);
            Eq("draft", vim.View().Draft, "unknown Normal keys are consumed");

            store.Update(settings => settings with { VimMode = false });
            var command = new TuiController(new Session(root), CancellationToken.None, Path.Combine(root, ".data"),
                FuiSettingsStore.Load(Path.Combine(root, ".data")));
            Type(command, "/vim");
            Key(command, ConsoleKey.Enter);
            Eq("INSERT", command.View().EditorMode, "/vim persistently enables Vim mode");
            Eq(true, FuiSettingsStore.Load(Path.Combine(root, ".data")).Current.VimMode,
                "/vim writes the Vim mode setting to disk");

            store.Update(settings => settings with { VimMode = false, EmojiAutocomplete = "off" });
            var offSession = new Session(root);
            var off = new TuiController(offSession, CancellationToken.None, Path.Combine(root, ".data"), store);
            Type(off, ":smile:");
            Eq(null, off.View().Overlay, "emoji completion can be disabled");
            Key(off, ConsoleKey.Enter);
            Eq(":smile:", offSession.Sent.Single(), "disabled emoji completion preserves literal shortcode");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewTempProject()
    {
        var basePath = Environment.GetEnvironmentVariable("FUFUCODE_TEST_ROOT") ?? Path.GetTempPath();
        var path = Path.Combine(basePath, "fui-editor4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "README.md"), "fixture");
        return path;
    }

    private static string NewTempDataRoot()
    {
        var basePath = Environment.GetEnvironmentVariable("FUFUCODE_TEST_ROOT") ?? Path.GetTempPath();
        var path = Path.Combine(basePath, "fui-editor4-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, bool ctrl = false, bool shift = false, bool alt = false) =>
        new('\0', key, shift, alt, ctrl);

    private static void Key(Composer composer, ConsoleKey key, bool ctrl = false, bool shift = false, bool alt = false) =>
        composer.Edit(Key(key, ctrl, shift, alt));

    private static void VimKey(Composer composer, char character, ConsoleKey key, bool shift = false) =>
        Require(composer.HandleVimKey(new(character, key, shift, false, false)), "Vim key was not consumed");

    private static void VimKey(TuiController controller, char character, ConsoleKey key, bool shift = false) =>
        controller.Key(new(character, key, shift, false, false), 24, 100, 8);

    private static void Key(TuiController controller, ConsoleKey key, bool ctrl = false, bool shift = false,
        bool alt = false) => controller.Key(Key(key, ctrl, shift, alt), 24, 100, 8);

    private static void Type(Composer composer, string text)
    {
        foreach (var ch in text) composer.Edit(new(ch, TypedKey(ch), false, false, false));
    }

    private static void Type(TuiController controller, string text)
    {
        foreach (var ch in text) controller.Key(new(ch, TypedKey(ch), false, false, false), 24, 100, 8);
    }

    private static void Paste(Composer composer, string text)
    {
        foreach (var ch in text) composer.Edit(new(ch, 0, false, false, false));
    }

    private static void Paste(TuiController controller, string text)
    {
        foreach (var ch in text) controller.Key(new(ch, 0, false, false, false), 24, 100, 8);
    }

    private static ConsoleKey TypedKey(char c) => c switch
    {
        >= 'a' and <= 'z' => ConsoleKey.A + (c - 'a'),
        >= 'A' and <= 'Z' => ConsoleKey.A + (c - 'A'),
        >= '0' and <= '9' => ConsoleKey.D0 + (c - '0'),
        ' ' => ConsoleKey.Spacebar,
        '\n' or '\r' => ConsoleKey.Enter,
        ':' => ConsoleKey.Oem1,
        '$' => ConsoleKey.D4,
        _ => ConsoleKey.Oem1
    };

    private static void Eq<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidDataException($"{context}: expected [{expected}], got [{actual}]");
    }

    private static void Require(bool condition, string context)
    {
        if (!condition) throw new InvalidDataException(context);
    }

    private sealed class FakeClipboard : IFuiClipboard
    {
        internal string Text { get; private set; } = "";
        internal string Read { get; set; } = "";
        public FuiClipboardResult CopyText(string text)
        {
            Text = text;
            return new(true, text.EnumerateRunes().Count(), Encoding.UTF8.GetByteCount(text), null);
        }

        public bool TryReadText(out string text, out string? error)
        {
            text = Read;
            error = null;
            return Read.Length != 0;
        }
    }

    private sealed class Session(string project) : ITuiSession
    {
        public string Project { get; } = project;
        public string Id { get; } = "editor4";
        public string? Model => "local";
        public string Transcript { get; set; } = "";
        public IReadOnlyList<TuiLine> History => Transcript.Length == 0 ? [] : [new("assistant", Transcript)];
        public Channel<TuiEvent> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<TuiEvent>();
        public ChannelReader<TuiEvent> Events => Channel.Reader;
        public List<string> Sent { get; } = [];
        public Task SendAsync(string text, CancellationToken ct) { Sent.Add(text); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(string id, bool allow, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
