using System.Threading.Channels;
using Fufucode.Fui;

namespace Fufucode.Fui.Tests;

internal static class MarkdownFeatureTests
{
    internal static async Task RunAsync()
    {
        SyntaxHighlighting();
        WideCharacters();
        StreamingFence();
        LinksAndHyperlinks();
        Mermaid();
        await LinkPickerAndCopyAsync();
    }

    private static void SyntaxHighlighting()
    {
        var samples = new (string Language, string Code, Ink Expected)[]
        {
            ("csharp", "public string Name = \"值\"; // note", Ink.SyntaxKeyword),
            ("powershell", "$name = Get-Process -Name pwsh # note", Ink.SyntaxProperty),
            ("json", "{\"name\": \"值\", \"ok\": true, \"n\": 1}", Ink.SyntaxProperty),
            ("javascript", "const fn = (x) => `值${x}`; // note", Ink.SyntaxKeyword),
            ("typescript", "interface User { name: string }", Ink.SyntaxType),
            ("python", "def f(x):\n    return \"值\" # note", Ink.SyntaxKeyword),
            ("bash", "for f in *.sh; do echo \"$f\"; done # note", Ink.SyntaxKeyword),
            ("rust", "fn main() { let x: i32 = 1; }", Ink.SyntaxKeyword),
            ("go", "func main() { fmt.Println(\"x\") }", Ink.SyntaxKeyword),
            ("sql", "SELECT id FROM users WHERE id = 1; -- note", Ink.SyntaxKeyword),
            ("diff", "+added\n-removed\n@@ -1 +1 @@", Ink.DiffAdded)
        };
        foreach (var sample in samples)
        {
            var source = "```" + sample.Language + "\n" + sample.Code + "\n```";
            var rows = Render(source, 80);
            var codeRows = rows.Where(row => row.Ink == Ink.Code).ToArray();
            Require(codeRows.Length != 0, sample.Language + " emitted code rows");
            Require(codeRows.SelectMany(row => row.Spans is null ? Enumerable.Empty<StyledSpan>() : row.Spans)
                    .Any(span => span.Ink == sample.Expected),
                sample.Language + " uses " + sample.Expected);
            CheckSpans(rows, "syntax " + sample.Language);
        }

        var csharp = Render("```csharp\npublic string Name = \"值\";\n```", 30);
        var frame = new TerminalLayout().Arrange(
            new TuiView("/p", "s", "m", "就绪", [new("assistant", "```csharp\npublic string Name = \"值\";\n```")],
                "", 0, 0, null), 60, 16);
        Require(csharp.Any(row => row.Spans?.Any(span => span.Ink == Ink.SyntaxType) == true),
            "C# type has a semantic span");
        var trueColor = TuiTheme.Resolve("dark", colorTerm: "truecolor");
        var ansi256 = TuiTheme.Resolve("dark", term: "xterm-256color");
        var ansi16 = TuiTheme.Resolve("dark", term: "vt100");
        var noColor = TuiTheme.Resolve("dark", noColor: true, colorTerm: "truecolor");
        Require(trueColor.Style(Ink.SyntaxKeyword).Contains("38;2;", StringComparison.Ordinal), "truecolor syntax");
        Require(ansi256.Style(Ink.SyntaxKeyword).Contains("38;5;", StringComparison.Ordinal), "256-color syntax");
        Require(!ansi16.Style(Ink.SyntaxKeyword).Contains("38;", StringComparison.Ordinal), "16-color syntax");
        Require(!noColor.Style(Ink.SyntaxKeyword).Contains("38;", StringComparison.Ordinal), "NO_COLOR syntax");
        var hyperlinks = TuiHyperlinkPolicy.Disabled;
        var rich = TerminalSurface.Diff(null, frame, trueColor, hyperlinks);
        var degraded = TerminalSurface.Diff(null, frame, ansi16, hyperlinks);
        var plain = TerminalSurface.Diff(null, frame, noColor, hyperlinks);
        var frameCode = frame.Rows.Single(row => row.Text.Contains("public string", StringComparison.Ordinal));
        Require(frameCode.Spans is { Count: >= 2 } &&
            string.Concat(frameCode.Spans.Select(span => span.Text)) == frameCode.Text &&
            frameCode.Spans.Select(span => span.Ink).Distinct().Count() >= 2,
            "card framing preserves multiple syntax spans");
        Require(rich.Contains("38;2;", StringComparison.Ordinal), "painted truecolor spans");
        Require(degraded.Contains("[9", StringComparison.Ordinal) && !degraded.Contains("38;5;", StringComparison.Ordinal),
            "painted 16-color spans");
        Require(!plain.Contains("38;", StringComparison.Ordinal) && plain.Contains("public", StringComparison.Ordinal),
            "NO_COLOR keeps readable code");
    }

    private static void WideCharacters()
    {
        const string source = "```csharp\nvar 文本 = \"中文🙂e\\u0301\";\n```";
        var rows = Render(source, 12);
        Require(rows.All(row => TerminalText.Width(row.Text) <= 12), "code wrap respects wide glyph cells");
        Require(rows.Any(row => row.Text.Contains('中')) && rows.Any(row => row.Text.Contains("🙂", StringComparison.Ordinal)),
            "wide glyphs remain visible");
        CheckSpans(rows, "wide syntax rows");
    }

    private static void StreamingFence()
    {
        var markdown = new MarkdownLines();
        var source = "```csharp\npublic class Sample";
        markdown.Project(source, 36);
        var before = Rows(markdown);
        var body = before[1];
        Require(!before.Any(row => row.Text == "```" && row != before[0]), "unclosed fence has no fabricated closer");

        source += "\n    public void Run() => Console.WriteLine(\"x\");";
        markdown.Project(source, 36);
        var afterAppend = Rows(markdown);
        Require(afterAppend[1] == body, "streamed syntax keeps completed body rows stable");
        Require(afterAppend[1].Spans is not null, "streamed body is highlighted");

        source += "\n```";
        markdown.Project(source, 36);
        var final = Rows(markdown);
        Require(final[1] == body, "final fence does not restyle a settled line");
        Require(final.Count(row => row.Ink == Ink.CodeFence) == 2, "final frame has both fences");
        Require(source.Contains("public class Sample", StringComparison.Ordinal), "source text is not rewritten");
    }

    private static void LinksAndHyperlinks()
    {
        var lines = new[]
        {
            new TuiLine("assistant",
                "[Docs](https://example.com/a_(b)) and https://example.org/x?q=1.\n" +
                "`https://inline.example/not-a-link`\n```\nhttps://code.example/not-a-link\n```")
        };
        var targets = MarkdownLinks.Extract(lines);
        Eq(2, targets.Count, "Markdown and bare URL targets are indexed once");
        Eq("Docs", targets[0].Label, "Markdown label retained");
        Eq("https://example.com/a_(b)", targets[0].Url, "balanced URL parentheses retained");
        Eq("https://example.org/x?q=1", targets[1].Url, "bare URL sentence punctuation trimmed");
        var choices = MarkdownLinks.Choices(lines);
        Eq("https://example.com/a_(b)", choices[0].Detail, "picker detail is the exact URL");
        var extras = MarkdownLinks.Extract([
            new("assistant", "[Site](https://www.example.com \"title\") and www.example.net/path.")
        ]);
        Eq(2, extras.Count, "titled Markdown link and www target are indexed");
        Eq("https://www.example.com", extras[0].Url, "Markdown title is not copied into the URL");
        Eq("https://www.example.net/path", extras[1].Url, "www target is normalized for copying");

        var rows = Render("[Docs](https://example.com/a_(b)) and `https://inline.example`", 90);
        CheckSpans(rows, "link spans");
        var linked = rows.SelectMany(row => row.Spans is null ? Enumerable.Empty<StyledSpan>() : row.Spans)
            .Where(span => span.Link is not null).ToArray();
        Require(linked.Length != 0 && linked.All(span => span.Link == "https://example.com/a_(b)"),
            "only prose URL is decorated as a link");

        var spans = StyledSpanList.From([
            new StyledSpan("Docs ", Ink.Assistant),
            new StyledSpan("https://example.com/a_(b)", Ink.Link, "https://example.com/a_(b)")
        ]);
        var frame = new TerminalFrame(80, [new("Docs https://example.com/a_(b)", Ink.Assistant, Spans: spans)],
            -1, 0, 1, 0);
        var enabled = TuiHyperlinkPolicy.Resolve("on", noColor: false, terminalSupportsLinks: false);
        var disabled = TuiHyperlinkPolicy.Resolve("off", noColor: false, terminalSupportsLinks: true);
        var blocked = TuiHyperlinkPolicy.Resolve("on", noColor: true, terminalSupportsLinks: true);
        var on = TerminalSurface.Diff(null, frame, TuiTheme.Unstyled, enabled);
        var off = TerminalSurface.Diff(null, frame, TuiTheme.Unstyled, disabled);
        var noColor = TerminalSurface.Diff(null, frame, TuiTheme.Unstyled, blocked);
        Require(on.Contains("\x1b]8;;https://example.com", StringComparison.Ordinal), "OSC 8 emitted when forced on");
        Require(!off.Contains("\x1b]8;", StringComparison.Ordinal), "off keeps readable text only");
        Require(!noColor.Contains("\x1b]8;", StringComparison.Ordinal), "NO_COLOR suppresses OSC 8");
        Require(enabled.Wrap("visible", "javascript:alert(1)") == "visible", "unsafe scheme is not linked");
        Require(!enabled.Wrap("visible", "https://x/\x1b]8;;inject").Contains('\x1b'), "control injection is refused");
        var cleaned = enabled.Wrap("safe\x1b[31m text", "https://example.com");
        Require(!cleaned.Contains("[31m", StringComparison.Ordinal) && cleaned.Contains("safe text", StringComparison.Ordinal),
            "display controls are stripped before OSC wrapping");
    }

    private static void Mermaid()
    {
        const string source = """
            flowchart TD
              A[开始] -->|成功| B{可用?}
              B -->|是| C[完成]
              B -->|否| D[重试]
            """;
        Require(MermaidDiagram.TryRender(source, 80, out var diagram, out _), "TD flowchart renders");
        var td = string.Join('\n', diagram);
        Require(td.Contains("flowchart TD", StringComparison.Ordinal) && td.Contains("成功", StringComparison.Ordinal) &&
            td.Contains("可用?", StringComparison.Ordinal) && td.Contains("↓", StringComparison.Ordinal),
            "TD direction, nodes and conditional edges are visible");
        Require(diagram.All(line => TerminalText.Width(line) <= 80 && !line.Any(char.IsControl)),
            "TD diagram is terminal safe");

        const string lr = "flowchart LR\n A[输入] -->|确认| B[输出]";
        Require(MermaidDiagram.TryRender(lr, 60, out var horizontal, out _), "LR flowchart renders");
        Require(horizontal.Any(line => line.Contains("flowchart LR", StringComparison.Ordinal)) &&
            horizontal.Any(line => line.Contains('→')), "LR direction uses horizontal arrows");
        Require(MermaidDiagram.TryRender("flowchart TD\nA[开始]-->B[结束]", 40, out var compact, out _) &&
            compact.Any(line => line.Contains("B[结束]", StringComparison.Ordinal)),
            "compact --> edges without surrounding spaces render");

        var unsupported = Render("```mermaid\nsequenceDiagram\nA->>B: hi\n```", 80);
        var unsupportedText = string.Join('\n', unsupported.Select(row => row.Text));
        Require(unsupportedText.Contains("此 Mermaid 子集不支持", StringComparison.Ordinal) &&
            unsupportedText.Contains("sequenceDiagram", StringComparison.Ordinal) &&
            unsupportedText.Contains("A->>B: hi", StringComparison.Ordinal),
            "unsupported Mermaid states the limit and preserves source");

        var supported = Render("```mermaid\n" + source + "\n```", 80);
        var supportedText = string.Join('\n', supported.Select(row => row.Text));
        Require(supportedText.Contains("flowchart TD", StringComparison.Ordinal) &&
            supportedText.Contains("A[开始]", StringComparison.Ordinal), "Mermaid fence reaches Markdown frame");
    }

    private static async Task LinkPickerAndCopyAsync()
    {
        var session = new LinkSession();
        var clipboard = new RecordingClipboard();
        var ui = new TuiController(session, CancellationToken.None, clipboard: clipboard);
        try
        {
            Type(ui, "/links");
            Key(ui, ConsoleKey.Enter);
            var first = ui.View().Overlay;
            Require(first is { Title: "链接目标" } && first.Items.Count == 2, "/links opens a searchable target picker");
            Require(first!.Message?.Contains("https://example.com", StringComparison.Ordinal) == true,
                "selected link detail shows its URL");
            Key(ui, ConsoleKey.Enter);
            var action = ui.View().Overlay;
            Require(action is not null, "copy action overlay is present");
            Require(action!.Title == "链接目标" && action.Items.Count == 1 &&
                action.Items[0].Label == "复制 URL" && action.Message == "https://example.com/a_(b)",
                "selection opens an explicit copy action with the URL");
            Key(ui, ConsoleKey.Enter);
            Eq("https://example.com/a_(b)", clipboard.Text, "copy action writes the exact URL");
            Require(ui.View().Overlay is null && ui.View().Lines.Any(line => line.Text.Contains("已复制链接", StringComparison.Ordinal)),
                "copy reports success without opening an external site");
            Eq(LinkSession.Original, session.History[0].Text, "link indexing never rewrites exported transcript text");
        }
        finally { await ui.FinishAsync(); }
    }

    private static void Type(TuiController ui, string text)
    {
        foreach (var ch in text) ui.Key(new(ch, (ConsoleKey)0, false, false, false), 20, 40);
    }

    private static void Key(TuiController ui, ConsoleKey key) =>
        ui.Key(new('\0', key, false, false, false), 20, 40);

    private static MarkdownLines.Row[] Render(string source, int width)
    {
        var markdown = new MarkdownLines();
        markdown.Project(source, width);
        return Rows(markdown);
    }

    private static MarkdownLines.Row[] Rows(MarkdownLines markdown) =>
        Enumerable.Range(0, markdown.Count).Select(index => markdown[index]).ToArray();

    private static void CheckSpans(IEnumerable<MarkdownLines.Row> rows, string context)
    {
        foreach (var row in rows)
        {
            if (row.Spans is null) continue;
            Eq(row.Text, string.Concat(row.Spans.Select(span => span.Text)), context + " spans cover text");
            Require(!row.Text.Any(char.IsControl), context + " text has no terminal controls");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Eq<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected [{expected}], got [{actual}]");
    }

    private sealed class RecordingClipboard : IFuiClipboard
    {
        internal string? Text { get; private set; }
        public FuiClipboardResult CopyText(string text)
        {
            Text = text;
            return new(true, text.EnumerateRunes().Count(), System.Text.Encoding.UTF8.GetByteCount(text), null);
        }
    }

    private sealed class LinkSession : ITuiSession
    {
        internal const string Original =
            "[Docs](https://example.com/a_(b)) and https://example.org/x?q=1";
        public string Project => "project";
        public string Id => "links";
        public string? Model => "loopback";
        public IReadOnlyList<TuiLine> History { get; } = [new("assistant", Original)];
        public ChannelReader<TuiEvent> Events { get; } = Channel.CreateUnbounded<TuiEvent>().Reader;
        public Task SendAsync(string text, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(string id, bool allow, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
