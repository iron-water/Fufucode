using System.Text;
using System.Threading.Channels;

namespace Fufucode.Fui.Tests;

internal static class TranscriptWorkbenchTests
{
    internal static async Task RunAsync()
    {
        ExtractionAndCopyTargets();
        await OutlineAndTranscriptAsync();
        await CopyAndInfoAsync();
        await RetryAndLargeListAsync();
        Layout();
    }

    private static void ExtractionAndCopyTargets()
    {
        TuiLine[] lines =
        [
            new("user", "请分析这个问题\n第二行"),
            new("assistant", "# 设计\n正文 https://example.com/a?x=1.\n```cs\nConsole.WriteLine(1);\n```\n## 结论"),
            new("tool", "read [完成] {\"path\":\"a.cs\"}\nTOOL-RESULT-ONLY"),
            new("error", "模型请求失败"),
            new("approval", "write_file\n{\"path\":\"a.cs\"}")
        ];
        string?[] sources = ["u1", "a1", "t1", "e1", "p1"];
        var catalog = FuiTranscriptWorkbench.Build(lines, sources);
        Eq(5, catalog.Entries.Count, "all outline-bearing transcript kinds");
        Eq(FuiTranscriptKind.Assistant, catalog.Entries[1].Kind, "assistant type");
        Eq("设计", catalog.Entries[1].Summary, "markdown heading precedes body summary");
        Eq("cs", catalog.Entries[1].CodeBlocks.Single().Language, "fenced code language");
        Eq("Console.WriteLine(1);", catalog.Entries[1].CodeBlocks.Single().Text, "code body excludes fences");
        Eq("https://example.com/a?x=1", catalog.Entries[1].Urls.Single(), "URL trims prose punctuation");
        Eq("工具 · read a.cs", $"{FuiTranscriptWorkbench.KindLabel(catalog.Entries[2].Kind)} · {catalog.Entries[2].Summary}",
            "tool summary carries name and target");
        Eq(FuiTranscriptKind.Approval, catalog.Entries[4].Kind, "approval type");
        Eq(1, catalog.Choices(FuiTranscriptFilter.Error).Count, "error filter");
        Eq("transcript:2", catalog.Choices(FuiTranscriptFilter.Tool).Single().Value, "real line target survives filtering");

        var search = new FuiPicker("对话", catalog.Choices(FuiTranscriptFilter.All), options: new(
            PreserveOrder: true, Footer: "footer", Kind: FuiOverlayKind.Transcript));
        Type(search, "TOOL-RESULT-ONLY");
        Eq("transcript:2", search.Current?.Value, "search reaches body text not only visible summary");

        var copy = FuiTranscriptWorkbench.BuildCopy(lines, sources, "session-identity");
        Eq(true, copy.Targets.ContainsKey("copy:recent-assistant"), "recent assistant target");
        Eq(true, copy.Targets.ContainsKey("copy:user:0"), "arbitrary user target");
        Eq(true, copy.Targets.ContainsKey("copy:assistant:1"), "arbitrary assistant target");
        Eq(true, copy.Targets.ContainsKey("copy:tool:2"), "tool result target");
        Eq(true, copy.Targets.ContainsKey("copy:code:current"), "current code target");
        Eq(true, copy.Targets.Keys.Any(id => id.StartsWith("copy:code:1:", StringComparison.Ordinal)), "specific code target");
        Eq(true, copy.Targets.Keys.Any(id => id.StartsWith("copy:url:", StringComparison.Ordinal)), "URL target");
        Eq("session-identity", copy.Targets["copy:session-id"].Text, "session ID target");
        Eq("Console.WriteLine(1);", copy.Targets["copy:code:current"].Text, "code copies exact body");
    }

    private static async Task OutlineAndTranscriptAsync()
    {
        var session = new TestSession([
            new("user", "START-USER"),
            new("assistant", "# UNIQUE-OUTLINE\nfirst body"),
            new("tool", "read [完成] {\"path\":\"src/a.cs\"}"),
            new("error", "UNIQUE-ERROR")
        ]);
        var ui = new TuiController(session, CancellationToken.None, clipboard: new FakeClipboard());
        Type(ui, "/outline");
        Key(ui, ConsoleKey.Enter);
        await WaitAsync(ui, () => ui.View().Overlay?.Kind == FuiOverlayKind.Outline, "outline opens");
        Eq(true, ui.View().Overlay!.Items.Any(item => item.Label.Contains("错误 · UNIQUE-ERROR", StringComparison.Ordinal)),
            "outline includes errors");
        Type(ui, "UNIQUE-OUTLINE");
        Key(ui, ConsoleKey.U, ctrl: true);
        Key(ui, ConsoleKey.DownArrow);
        var selected = ui.View().Overlay!.Items[ui.View().Overlay!.Selected].Value;

        session.Emit(new TuiEvent.Message("stream-1", "assistant", "# STREAM-HEADING\nnew body"));
        ui.Update();
        await WaitAsync(ui, () => ui.View().Overlay?.Items.Any(item => item.Label.Contains("STREAM-HEADING", StringComparison.Ordinal)) == true,
            "streaming outline refreshes");
        Eq(selected, ui.View().Overlay!.Items[ui.View().Overlay!.Selected].Value,
            "stream refresh keeps the selected stable value");
        Key(ui, ConsoleKey.Enter);
        Eq(1, ui.TakeScrollTarget(), "outline jump uses original stable row after append");
        Eq(true, ui.View().Lines[1].Text.Contains("UNIQUE-OUTLINE", StringComparison.Ordinal), "outline target text is correct");

        Type(ui, "/transcript");
        Key(ui, ConsoleKey.Enter);
        await WaitAsync(ui, () => ui.View().Overlay?.Kind == FuiOverlayKind.Transcript, "transcript opens asynchronously");
        Eq("对话 · 全部", ui.View().Overlay!.Title, "initial transcript filter");
        Key(ui, ConsoleKey.Tab);
        Eq("对话 · 用户", ui.View().Overlay!.Title, "Tab advances to user filter");
        Eq(true, ui.View().Overlay!.Items.All(item => item.Label.Contains("用户 ·", StringComparison.Ordinal)), "user filter excludes other kinds");
        Key(ui, ConsoleKey.Tab);
        Eq("对话 · 助手", ui.View().Overlay!.Title, "assistant filter");
        Type(ui, "UNIQUE-OUTLINE");
        Key(ui, ConsoleKey.Enter);
        Eq(1, ui.TakeScrollTarget(), "filtered transcript search resolves the assistant row");

        Type(ui, "/transcript");
        Key(ui, ConsoleKey.Enter);
        await WaitAsync(ui, () => ui.View().Overlay?.Kind == FuiOverlayKind.Transcript, "transcript reopens");
        Eq("对话 · 助手", ui.View().Overlay!.Title, "last valid filter remains active across reopen");
        Key(ui, ConsoleKey.Tab, shift: true);
        Eq("对话 · 用户", ui.View().Overlay!.Title, "Shift+Tab moves filters backward");
        Key(ui, ConsoleKey.Tab, shift: true);
        Eq("对话 · 全部", ui.View().Overlay!.Title, "Shift+Tab crosses all");
        Key(ui, ConsoleKey.Tab, shift: true);
        Eq("对话 · 审批", ui.View().Overlay!.Title, "Shift+Tab wraps filters backward");
        Key(ui, ConsoleKey.Escape);
        Eq(null, ui.View().Overlay, "Escape closes transcript browser");
    }

    private static async Task CopyAndInfoAsync()
    {
        var session = new TestSession([
            new("user", "USER-COPY"),
            new("assistant", "# COPY-HEAD\nbody\n```http\nhttps://copy.example/path\n```\nMAGIC_CODE\n```txt\nMAGIC_CODE\n```")
        ])
        {
            Info = new("project", "session-copy", "model-copy", "high", 4, 2, 1, 3, 5,
                new(null, 7, null, null, 2))
        };
        var clipboard = new FakeClipboard();
        var ui = new TuiController(session, CancellationToken.None, clipboard: clipboard);

        Type(ui, "/copy");
        Key(ui, ConsoleKey.Enter);
        await WaitAsync(ui, () => ui.View().Overlay?.Kind == FuiOverlayKind.Copy, "copy picker opens");
        Type(ui, "当前代码块");
        Key(ui, ConsoleKey.Enter);
        Eq("MAGIC_CODE", clipboard.Text, "selected exact code copied");
        Eq(true, ui.View().Lines.Any(line => line.Text.Contains("10 字符 · 10 字节", StringComparison.Ordinal)),
            "copy confirms character and UTF-8 byte counts");
        Eq(0, session.Sent.Count, "copy never sends a prompt");

        clipboard.Fail = "剪贴板被占用";
        Type(ui, "/copy");
        Key(ui, ConsoleKey.Enter);
        await WaitAsync(ui, () => ui.View().Overlay?.Kind == FuiOverlayKind.Copy, "copy reopens");
        Type(ui, "USER-COPY");
        Key(ui, ConsoleKey.Enter);
        Eq(true, ui.View().Lines.Any(line => line.Kind == "error" && line.Text.Contains("剪贴板被占用", StringComparison.Ordinal)),
            "copy failure explains the platform error");

        Type(ui, "/info");
        Key(ui, ConsoleKey.Enter);
        await WaitAsync(ui, () => ui.View().Lines.Any(line => line.Text.Contains("session-copy", StringComparison.Ordinal)), "info reports real facts");
        var info = ui.View().Lines.Last(line => line.Text.Contains("session-copy", StringComparison.Ordinal)).Text;
        Eq(true, info.Contains("模型  model-copy", StringComparison.Ordinal) &&
            info.Contains("思考  high", StringComparison.Ordinal) &&
            info.Contains("工具  2", StringComparison.Ordinal) &&
            info.Contains("队列  5", StringComparison.Ordinal) &&
            info.Contains("输入 未知 · 输出 7 · 合计 未知", StringComparison.Ordinal) &&
            info.Contains("读取 未知 · 写入 2", StringComparison.Ordinal), "known and unknown usage stay distinct");
    }

    private static async Task RetryAndLargeListAsync()
    {
        var session = new TestSession([new("user", "previous")]) { RetryError = "当前回合仍在运行；历史未修改。" };
        var ui = new TuiController(session, CancellationToken.None, clipboard: new FakeClipboard());
        Type(ui, "/retry");
        Key(ui, ConsoleKey.Enter);
        await WaitAsync(ui, () => ui.View().Lines.Any(line => line.Text.Contains("当前回合仍在运行", StringComparison.Ordinal)), "running retry refusal");
        Eq(1, session.RetryCalls, "retry interface was consulted");
        Eq(0, session.Sent.Count, "refused retry does not issue a send");

        session.RetryError = null;
        Type(ui, "/retry");
        Key(ui, ConsoleKey.Enter);
        await WaitAsync(ui, () => ui.View().Lines.Any(line => line.Text.Contains("已重发上一条用户消息", StringComparison.Ordinal)), "retry can start");
        Eq(2, session.RetryCalls, "retry retried once");

        var noHistory = new TestSession([]) { RetryError = "当前分支没有可重发的用户文本消息，历史未修改。" };
        var noHistoryUi = new TuiController(noHistory, CancellationToken.None, clipboard: new FakeClipboard());
        Type(noHistoryUi, "/retry");
        Key(noHistoryUi, ConsoleKey.Enter);
        await WaitAsync(noHistoryUi, () => noHistoryUi.View().Lines.Any(line => line.Text.Contains("没有可重发", StringComparison.Ordinal)),
            "empty history refusal");
        Eq(0, noHistory.Sent.Count, "empty-history retry leaves session unchanged");

        const int count = 40_000;
        var lines = Enumerable.Range(0, count)
            .Select(i => new TuiLine(i % 2 == 0 ? "user" : "assistant",
                i == count - 1 ? "# LARGE-END\nneedle at the end" : $"message-{i}"))
            .ToArray();
        var sources = Enumerable.Range(0, count).Select(i => "m" + i).ToArray();
        var large = FuiTranscriptWorkbench.Build(lines, sources);
        Eq(count, large.Entries.Count, "large transcript built completely");
        var picker = new FuiPicker("对话", large.Choices(FuiTranscriptFilter.All), options: new(
            PreserveOrder: true, Footer: "footer", Kind: FuiOverlayKind.Transcript));
        Type(picker, "LARGE-END");
        Eq($"transcript:{count - 1}", picker.Current?.Value, "search reaches the final row in a large list");

        var largeSession = new TestSession(lines);
        var largeUi = new TuiController(largeSession, CancellationToken.None, clipboard: new FakeClipboard());
        Type(largeUi, "/transcript");
        Key(largeUi, ConsoleKey.Enter);
        largeSession.Emit(new TuiEvent.Message("while-opening", "assistant", "EVENT-WHILE-OPENING"));
        largeUi.Update();
        Eq(true, largeUi.View().Lines.Any(line => line.Text == "EVENT-WHILE-OPENING"),
            "event queue remains usable while transcript indexing runs off-thread");
        await WaitAsync(largeUi, () => largeUi.View().Overlay?.Kind == FuiOverlayKind.Transcript, "large transcript opens");
        Key(largeUi, ConsoleKey.Escape);
    }

    private static void Layout()
    {
        FuiChoice[] choices =
        [
            new("transcript:1", "1  助手 · 标题", "正文详情"),
            new("transcript:2", "2  工具 · read", "工具详情")
        ];
        var overlay = new FuiOverlay("对话 · 全部", "", choices, 0, Footer: "Tab 过滤 · Enter 定位",
            Kind: FuiOverlayKind.Transcript);
        var view = new TuiView("/p", "s", "m", "就绪", [new("info", "transcript")], "", 0, 0, null, Overlay: overlay);
        var layout = new TerminalLayout();
        var wide = layout.Arrange(view, 100, 30);
        Eq(true, wide.Rows.Any(row => row.Ink == Ink.PanelTitle && row.Text.Contains("对话 · 正文", StringComparison.Ordinal)),
            "wide transcript browser has a detail column");
        Eq(true, wide.Rows.Any(row => row.Text.Contains("Tab 过滤", StringComparison.Ordinal)), "footer stays visible");
        Eq(true, wide.Rows.Any(row => row.Ink == Ink.Selection && row.Text.Contains("助手 · 标题", StringComparison.Ordinal)),
            "selected transcript row is visible");
        var narrow = layout.Arrange(view, 60, 20);
        Eq(false, narrow.Rows.Any(row => row.Ink == Ink.PanelTitle && row.Text.Contains('│')),
            "narrow transcript browser is single-column");
        Eq(true, narrow.Rows.Any(row => row.Text.Contains("正文详情", StringComparison.Ordinal)), "narrow selected detail remains visible");
    }

    private static ConsoleKeyInfo KeyInfo(ConsoleKey key, bool ctrl = false, bool shift = false) =>
        new('\0', key, shift, false, ctrl);

    private static void Key(TuiController ui, ConsoleKey key, bool ctrl = false, bool shift = false) =>
        ui.Key(KeyInfo(key, ctrl, shift), 30, 80, 8);

    private static void Type(TuiController ui, string text)
    {
        foreach (var c in text) ui.Key(new(c, (ConsoleKey)0, false, false, false), 30, 80, 8);
    }

    private static void Type(FuiPicker picker, string text)
    {
        foreach (var c in text) picker.Edit(new(c, (ConsoleKey)0, false, false, false), 8);
    }

    private static async Task WaitAsync(TuiController ui, Func<bool> condition, string what)
    {
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < 15_000)
        {
            ui.Update();
            if (condition()) return;
            await Task.Delay(20);
        }
        throw new InvalidOperationException("timed out: " + what);
    }

    private static void Eq<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{what}: expected [{expected}], got [{actual}]");
    }

    private sealed class FakeClipboard : IFuiClipboard
    {
        internal string? Text { get; private set; }
        internal string? Fail { get; set; }

        public FuiClipboardResult CopyText(string text)
        {
            Text = text;
            return Fail is null
                ? new(true, text.EnumerateRunes().Count(), Encoding.UTF8.GetByteCount(text), null)
                : FuiClipboardResult.Failed(Fail, text);
        }
    }

    private class TestSession : ITuiSession, IFuiSessionInspector
    {
        private readonly IReadOnlyList<TuiLine> _history;
        public TestSession(IReadOnlyList<TuiLine> history) => _history = history;
        public string Project => "project";
        public string Id => "session-id";
        public string? Model => "loopback";
        public IReadOnlyList<TuiLine> History => _history;
        public Channel<TuiEvent> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<TuiEvent>();
        public ChannelReader<TuiEvent> Events => Channel.Reader;
        public List<string> Sent { get; } = [];
        public int RetryCalls { get; private set; }
        public string? RetryError { get; set; }
        public FuiSessionInfo Info { get; set; } = new("project", "session-id", "loopback", "medium",
            1, 0, 0, 0, 0, null);

        public void Emit(TuiEvent value) => Channel.Writer.TryWrite(value);
        public Task SendAsync(string text, CancellationToken ct) { Sent.Add(text); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(string id, bool allow, CancellationToken ct) => Task.CompletedTask;
        public Task<FuiSessionInfo> InspectAsync(CancellationToken ct) => Task.FromResult(Info);

        public Task<string> RetryLastUserAsync(CancellationToken ct)
        {
            RetryCalls++;
            if (RetryError is { } error) throw new InvalidOperationException(error);
            var last = _history.LastOrDefault(line => line.Kind == "user")?.Text;
            return last is null
                ? throw new InvalidOperationException("当前分支没有可重发的用户文本消息，历史未修改。")
                : Task.FromResult(last);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
