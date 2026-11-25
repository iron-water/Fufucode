using System.Threading.Channels;
using Fufucode.App.Abstractions;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Fui.Tests;

internal static class CatalogWorkbenchTests
{
    public static async Task RunAsync()
    {
        ContextFactsAndGroups();
        ContextLayout();
        RuntimeCatalogProjection();
        JobsProjection();
        await ControllerFlowsAsync();
    }

    private static void ContextFactsAndGroups()
    {
        var lines = new[]
        {
            new TuiLine("user", "inspect this"),
            new TuiLine("thinking", "private reasoning"),
            new TuiLine("tool", "read_file [完成] {\"path\":\"a.txt\"}",
                new ToolCallSnapshot("read_file", "{\"path\":\"a.txt\"}", ToolCallStatus.Completed,
                    "read complete", "file body", DurationMs: 5)),
            new TuiLine("assistant", "reply\n[图片 × 1]", Images: [new TuiImage("pic.png", "image/png", [1, 2, 3])])
        };
        string?[] sources = ["user-1", "thinking-1", "tool-1", "assistant-1"];
        var attachments = new[]
        {
            new FileAttachment("notes/a.txt", "hello 中文", [1], ByteCount: 20),
            new FileAttachment("shots/photo.png", "", [2], "image/png", ByteCount: 2048)
        };
        var plan = FuiPlanProjection.FromSnippet(
            "[计划已更新]\n[>] 1. Inspect context (in_progress)\n[ ] 2. Report facts (pending)");
        var info = new FuiSessionInfo("/work", "session-1", null, null, 4, 1, 0, 0, 2,
            null, false, Thinking: 1, ContextWindowUnknownReason: "端点目录未报告",
            ExecutionMode: "manual", QueueEntries: [new("follow", false), new("redirect", true)],
            RecoverableQueue: ["cancelled"]);
        var choices = FuiContextWorkbench.Build(info, attachments, plan, lines, sources);

        Check(choices.Any(choice => choice.Label.Contains("上下文窗口 · 未知", StringComparison.Ordinal)),
            "unknown context window was not labelled unknown");
        Check(choices.Any(choice => choice.Label.Contains("用量 · 未知", StringComparison.Ordinal)),
            "unreported usage became a fabricated token count");
        Check(!choices.Any(choice => choice.Label.Contains("tokens", StringComparison.Ordinal)),
            "unknown token usage was estimated from characters");
        Check(choices.Any(choice => choice.Label.Contains("排队", StringComparison.Ordinal)) &&
            choices.Any(choice => choice.Label.Contains("插话", StringComparison.Ordinal)) &&
            choices.Any(choice => choice.Label.Contains("已取消", StringComparison.Ordinal)),
            "queue, steering and recoverable queue are not distinct");
        Check(choices.Any(choice => choice.Label.Contains("Inspect context", StringComparison.Ordinal)),
            "structured plan is missing");

        var text = choices.Single(choice => choice.Value == FuiContextWorkbench.AttachmentPrefix + "0");
        Check(text.Detail!.Contains("字节 20", StringComparison.Ordinal) &&
            text.Detail.Contains("字符 8", StringComparison.Ordinal) &&
            text.Detail.Contains("图片 否", StringComparison.Ordinal),
            "text attachment did not expose bytes/chars/image facts");
        var image = choices.Single(choice => choice.Value == FuiContextWorkbench.AttachmentPrefix + "1");
        Check(image.Detail!.Contains("字节 2,048", StringComparison.Ordinal) &&
            image.Detail.Contains("字符 不适用", StringComparison.Ordinal) &&
            image.Detail.Contains("图片 是", StringComparison.Ordinal),
            "image attachment metadata is incomplete");
        var tool = choices.Single(choice => choice.Value == FuiContextWorkbench.LinePrefix + "2");
        Check(FuiContextWorkbench.TryLineIndex(tool, out var index) && index == 2,
            "tool activity did not retain its transcript jump target");
        Check(tool.Detail!.Contains("read_file", StringComparison.Ordinal) &&
            tool.Detail.Contains("read complete", StringComparison.Ordinal) &&
            tool.Detail.Contains("来源 tool-1", StringComparison.Ordinal),
            "context projection dropped the structured tool snapshot or source id");
        var imageLine = choices.Single(choice => choice.Value == FuiContextWorkbench.LinePrefix + "3");
        Check(imageLine.Detail!.Contains("pic.png", StringComparison.Ordinal) &&
            imageLine.Detail.Contains("image/png", StringComparison.Ordinal) &&
            imageLine.Detail.Contains("来源 assistant-1", StringComparison.Ordinal),
            "context projection dropped structured image metadata or source id");

        var picker = new FuiPicker(FuiContextWorkbench.Title, choices, options: new(
            GroupHeader: FuiContextWorkbench.IsGroupChoice,
            PreserveOrder: true,
            Kind: FuiOverlayKind.Context));
        Check(picker.Current is not null && !FuiContextWorkbench.IsGroupChoice(picker.Current),
            "context picker initially selected a non-actionable group heading");
        Type(picker, "notes/a.txt");
        Check(picker.View.Items.Count == 1 &&
            picker.View.Items[0].Value == FuiContextWorkbench.AttachmentPrefix + "0",
            "context search did not narrow to the attachment without group headings");

        var known = FuiContextWorkbench.Build(info with
        {
            ContextWindow = 128_000,
            ContextWindowSource = "端点模型目录",
            ContextWindowUnknownReason = null
        }, attachments, plan, lines, sources);
        Check(known.Any(choice => choice.Label.Contains("128,000 tokens", StringComparison.Ordinal) &&
            choice.Detail!.Contains("端点模型目录", StringComparison.Ordinal)),
            "proved context window or its evidence source was hidden");
    }

    private static void ContextLayout()
    {
        var info = new FuiSessionInfo("/work", "session-1", "model-1", "medium", 4, 1, 0, 0, 0,
            new FuiUsageInfo(10, 5, 15, null, null), false, Thinking: 1,
            ContextWindow: 32_000, ContextWindowSource: "Core", ExecutionMode: "plan");
        var choices = FuiContextWorkbench.Build(info, [], FuiPlanSnapshot.Empty,
        [
            new("user", "inspect"), new("tool", "grep_search [完成] {}")
        ]);
        var picker = new FuiPicker(FuiContextWorkbench.Title, choices, options: new(
            GroupHeader: FuiContextWorkbench.IsGroupChoice,
            PreserveOrder: true,
            Footer: FuiContextWorkbench.Footer,
            Kind: FuiOverlayKind.Context));
        var model = Model([new("info", "transcript")]) with { Overlay = picker.View };
        var layout = new TerminalLayout();
        var wide = layout.Arrange(model, 100, 35);
        Check(wide.Rows.Any(row => row.Text.Contains("│", StringComparison.Ordinal) &&
                row.Text.Contains("上下文 · 详情", StringComparison.Ordinal)),
            "wide context viewer did not expose a detail column");
        Check(wide.Rows.Any(row => row.Ink == Ink.Selection && row.Text.Contains("模型 · model-1", StringComparison.Ordinal)),
            "wide context viewer lost the selected fact");

        var narrow = layout.Arrange(model, 60, 22);
        Check(!narrow.Rows.Any(row => row.Text.Contains("上下文 · 详情", StringComparison.Ordinal)),
            "narrow context viewer did not collapse to one column");
        Check(narrow.Rows.Any(row => row.Text.Contains("模型 · model-1", StringComparison.Ordinal)),
            "narrow context viewer lost the selected fact");
        Check(narrow.Rows.All(row => TerminalText.Width(row.Text) < 60 && !row.Text.Any(char.IsControl)),
            "context viewport overflowed or leaked terminal controls");
    }

    private static void RuntimeCatalogProjection()
    {
        var input = new FuiRuntimeCatalogInput(
            false,
            [
                new("read_file", "read", "{}"),
                new("write_file", "write", "{}")
            ],
            [new("memory_save", "save memory", "{}", PluginCapability.Memory)],
            [
                new("fixture-skill", "demonstration", "/skills/fixture", "body", false),
                new("user-only", "manual", "/skills/user", "body", true)
            ],
            true,
            true,
            [
                new("fixture-mcp", "stdio", Command: "server.exe", Args: ["--flag"], Auth: new("bearer")),
                new("remote-mcp", "http", Url: "https://example.invalid/mcp?token=SECRET",
                    Auth: new("oauth"))
            ],
            true,
            [new("reviewer", "review evidence", "read")],
            [new("tool.memory", 1, true, false)],
            new Dictionary<string, string> { ["agent.browser-use"] = "manifest invalid" });
        var catalog = FuiRuntimeCatalogBuilder.Build(input);

        Check(Enum.GetValues<FuiCapabilitySource>().All(source =>
                catalog.Capabilities.Any(entry => entry.Source == source)),
            "runtime catalogue lost a proven capability source");
        Check(catalog.Capabilities.Any(entry => entry.ChoiceValue == "read_file" &&
                FuiRuntimeCatalogWorkbench.CapabilityDetail(entry).StartsWith("native", StringComparison.Ordinal)),
            "native capability detail lost its provenance");
        var pluginChoices = Selectable(catalog.CapabilityChoices(FuiCapabilitySource.Plugin));
        Check(pluginChoices.Count == 1 && pluginChoices[0].Value == "memory_save",
            "source filter did not isolate plugin tools");
        var skillChoices = Selectable(catalog.CapabilityChoices(kind: FuiCapabilityKind.Skill));
        Check(skillChoices.Count == 1 && skillChoices[0].Value == "skill:fixture-skill",
            "type filter admitted a user-only skill or lost the invocable one");

        var plan = FuiRuntimeCatalogBuilder.Build(input with { PlanMode = true });
        Check(plan.Capabilities.All(entry => entry.Source == FuiCapabilitySource.Native) &&
            plan.Capabilities.Any(entry => entry.ChoiceValue == "read_file") &&
            plan.Capabilities.All(entry => entry.ChoiceValue != "write_file"),
            "plan mode did not project the actual read-only capability set");

        var extensions = catalog.ExtensionChoices();
        Check(extensions.Any(choice => choice.Value == "extension:plugin:tool.memory" &&
                choice.Detail!.Contains("能力 · 工具 1", StringComparison.Ordinal)),
            "loaded plugin capability count is missing");
        Check(extensions.Any(choice => choice.Value == "extension:failure:agent.browser-use" &&
                choice.Detail!.Contains("manifest invalid", StringComparison.Ordinal)),
            "plugin load failure reason is missing");
        Check(extensions.Any(choice => choice.Value == "extension:source:mcp" ||
                choice.Label.Contains("MCP", StringComparison.Ordinal)),
            "extension tool-source summary is missing");
        Check(extensions.All(choice => !choice.Value.StartsWith("install:", StringComparison.Ordinal) &&
                !choice.Value.StartsWith("uninstall:", StringComparison.Ordinal)),
            "read-only extension catalogue exposed a fake install/uninstall action");

        var mcp = catalog.McpChoices();
        Check(mcp.Any(choice => choice.Value == "mcp:server:fixture-mcp" &&
                choice.Detail!.Contains("stdio", StringComparison.Ordinal) &&
                choice.Detail.Contains("未执行", StringComparison.Ordinal)),
            "MCP server facts or the no-probe boundary are missing");
        Check(mcp.Any(choice => choice.Value == "mcp:server:remote-mcp" &&
                choice.Detail!.Contains("https://example.invalid/mcp", StringComparison.Ordinal)) &&
            mcp.All(choice => !choice.Detail?.Contains("SECRET", StringComparison.Ordinal) ?? true),
            "MCP view exposed credential-like URL query data");
        var unavailable = FuiRuntimeCatalog.Unavailable("no source", true);
        Check(unavailable.McpChoices().Count == 0 && unavailable.McpUnavailableReason == "no source",
            "MCP without a fact source did not degrade to an explicit empty state");
    }

    private static void JobsProjection()
    {
        var start = new DateTimeOffset(2026, 11, 26, 5, 0, 0, TimeSpan.Zero);
        var snapshot = new FuiAgentActivitySnapshot(
        [
            new("job-run", "reviewer", FuiAgentState.Running, "grep done", start,
                start.AddSeconds(8), null, 2, 1, 3,
                [new(start, "started", "inspect"), new(start.AddSeconds(8), "tool_end", "grep")]),
            new("job-done", "writer", FuiAgentState.Completed, "done", start.AddSeconds(-30),
                start.AddSeconds(-20), start.AddSeconds(-20), 1, 1, 2,
                [new(start.AddSeconds(-30), "started", "write")]),
            new("job-failed", "tester", FuiAgentState.Failed, "failed", start.AddSeconds(-10),
                start.AddSeconds(-5), start.AddSeconds(-5), 1, 0, 2,
                [new(start.AddSeconds(-5), "failed", "exit 1")])
        ], start.AddSeconds(10));
        var choices = FuiJobsWorkbench.Choices(snapshot);
        Check(Selectable(choices).Count == 3, "jobs view did not list every running/completed/failed job");
        Check(choices.Count(choice => FuiJobsWorkbench.IsGroupChoice(choice)) == 3,
            "jobs view lost status grouping");
        var running = snapshot.Find("job-run")!;
        var detail = FuiJobsWorkbench.Detail(snapshot, running);
        Check(detail.Contains("工具 开始 2 / 完成 1", StringComparison.Ordinal) &&
            detail.Contains("已运行", StringComparison.Ordinal) &&
            detail.Contains("父作业 ID · 未提供", StringComparison.Ordinal) &&
            detail.Contains("取消/恢复 · 不可用", StringComparison.Ordinal),
            "jobs detail lost counts/duration or invented parent/cancel facts");

        var picker = new FuiPicker(FuiJobsWorkbench.Title, choices, options: new(
            Detail: choice => snapshot.Find(choice.Value) is { } job ? FuiJobsWorkbench.Detail(snapshot, job) : null,
            GroupHeader: FuiJobsWorkbench.IsGroupChoice,
            PreserveOrder: true,
            Footer: FuiJobsWorkbench.Footer,
            Kind: FuiOverlayKind.Jobs));
        var model = Model([new("info", "transcript")]) with { Overlay = picker.View };
        var frame = new TerminalLayout().Arrange(model, 100, 35);
        Check(frame.Rows.Any(row => row.Text.Contains("作业 · 详情", StringComparison.Ordinal)) &&
            frame.Rows.Any(row => row.Text.Contains("运行中", StringComparison.Ordinal)),
            "wide jobs view did not expose the read-only detail pane");
    }

    private static async Task ControllerFlowsAsync()
    {
        var catalog = new FuiRuntimeCatalog(
        [
            new("read_file", "read", FuiCapabilitySource.Native, FuiCapabilityKind.Tool),
            new("plugin_tool", "plugin", FuiCapabilitySource.Plugin, FuiCapabilityKind.Tool),
            new("fixture-skill", "skill", FuiCapabilitySource.Skill, FuiCapabilityKind.Skill),
            new("mcp", "gateway", FuiCapabilitySource.Mcp, FuiCapabilityKind.Gateway)
        ],
        [new("tool.memory", "记忆", "1.0.0", "ToolExtension", 1, false, true, "local memory")],
        [new("agent.browser-use", "Browser Use", "manifest invalid")],
        [new("fixture-mcp", "stdio", "server.exe · 参数 1 项", "bearer", true)],
        true, null, false);
        var info = new FuiSessionInfo("/demo", "session-1", "model-1", "medium", 4, 1, 1, 0, 0,
            null, false, Thinking: 1, ContextWindowUnknownReason: "端点目录未报告",
            ExecutionMode: "manual", QueueEntries: [new("follow", false)], RecoverableQueue: []);
        var session = new CatalogSession(info, catalog);
        var ui = new TuiController(session, CancellationToken.None);

        await Command(ui, "/context", view => view.Overlay is { Kind: FuiOverlayKind.Context });
        var context = ui.View().Overlay!;
        Check(context.Items.Any(choice => choice.Label.Contains("上下文窗口 · 未知", StringComparison.Ordinal)) &&
            context.Items.Any(choice => choice.Value == "context:queue:0"),
            "controller did not render context facts and queue");
        ClosePanel(ui);

        await Command(ui, "/extensions", view => view.Overlay is { Kind: FuiOverlayKind.Extensions });
        Check(ui.View().Overlay!.Items.Any(choice => choice.Value == "extension:plugin:tool.memory"),
            "controller did not render loaded extensions");
        ClosePanel(ui);

        await Command(ui, "/mcp", view => view.Overlay is { Kind: FuiOverlayKind.Mcp });
        Check(ui.View().Overlay!.Items.Any(choice => choice.Value == "mcp:server:fixture-mcp"),
            "controller did not render MCP facts");
        ClosePanel(ui);

        await Command(ui, "/capabilities", view => view.Overlay?.Kind == FuiOverlayKind.Capabilities);
        Check(ui.View().Overlay!.Items.Any(choice => choice.Value == "read_file"),
            "capabilities panel lost native entries");
        Press(ui, ConsoleKey.Tab); Press(ui, ConsoleKey.Tab);
        Check(ui.View().Overlay!.Title.Contains("插件", StringComparison.Ordinal) &&
            Selectable(ui.View().Overlay!.Items).All(choice => choice.Value == "plugin_tool"),
            "source filter did not project only plugin capabilities");
        Press(ui, ConsoleKey.Tab, shift: true);
        Check(ui.View().Overlay!.Title.Contains("插件", StringComparison.Ordinal) &&
            ui.View().Overlay!.Title.Contains("工具", StringComparison.Ordinal),
            "type filter did not cycle independently of source");
        ClosePanel(ui);

        session.Emit(new TuiEvent.AgentsChanged(new FuiAgentActivitySnapshot(
        [
            new("job-1", "reviewer", FuiAgentState.Running, "working",
                DateTimeOffset.UtcNow.AddSeconds(-3), DateTimeOffset.UtcNow, null, 1, 0, 1,
                [new(DateTimeOffset.UtcNow, "started", "work")])
        ], DateTimeOffset.UtcNow)));
        ui.Update();
        await Command(ui, "/jobs", view => view.Overlay is { Kind: FuiOverlayKind.Jobs });
        Check(ui.View().Overlay!.Items.Any(choice => choice.Value == "job-1"),
            "jobs command reuses the roster incorrectly");
        ClosePanel(ui);

        var noSource = new PlainSession();
        var empty = new TuiController(noSource, CancellationToken.None);
        await Command(empty, "/context", view => view.Overlay is { Kind: FuiOverlayKind.Context });
        var contextEmpty = empty.View().Overlay!;
        Check(contextEmpty.Items.Any(choice => choice.Label.Contains("上下文窗口 · 未知", StringComparison.Ordinal)) &&
            contextEmpty.Items.Any(choice => choice.Value == "context:usage-unavailable") &&
            contextEmpty.Items.All(choice => !choice.Label.Contains("tokens", StringComparison.Ordinal)),
            "context without an inspector fabricated facts instead of showing limited unknowns");
        ClosePanel(empty);
        await Command(empty, "/mcp", view => view.Overlay is { Kind: FuiOverlayKind.Mcp });
        var mcpEmpty = empty.View().Overlay!;
        Check(mcpEmpty.Items.Count == 0 &&
            mcpEmpty.Message!.Contains("未提供 MCP 事实源", StringComparison.Ordinal),
            "MCP without a source did not degrade explicitly");
        ClosePanel(empty);
        await Command(empty, "/extensions", view => view.Overlay is { Kind: FuiOverlayKind.Extensions });
        var extensionsEmpty = empty.View().Overlay!;
        Check(extensionsEmpty.Items.Count == 0 &&
            extensionsEmpty.Message!.Contains("未提供插件运行事实源", StringComparison.Ordinal),
            "extensions without a source did not degrade explicitly");
        ClosePanel(empty);
        await Command(empty, "/capabilities", view => view.Overlay is { Kind: FuiOverlayKind.Capabilities });
        var capabilitiesEmpty = empty.View().Overlay!;
        Check(capabilitiesEmpty.Items.Count == 0 &&
            capabilitiesEmpty.Message!.Contains("未提供可证明的能力目录", StringComparison.Ordinal),
            "capabilities without a source did not degrade explicitly");
        ClosePanel(empty);
        await Command(empty, "/jobs", view => view.Overlay is { Kind: FuiOverlayKind.Jobs });
        var jobsEmpty = empty.View().Overlay!;
        Check(jobsEmpty.Items.Count == 0 &&
            jobsEmpty.Message!.Contains("尚无子代理作业", StringComparison.Ordinal),
            "jobs without events did not show an empty state");
    }

    private static IReadOnlyList<FuiChoice> Selectable(IReadOnlyList<FuiChoice> choices) =>
        choices.Where(choice => !FuiChoiceGroups.IsGroup(choice)).ToArray();

    private static TuiView Model(IReadOnlyList<TuiLine> lines) =>
        new("/demo", "session-1", "model-1", "就绪", lines, "", 0, 0, null);

    private static async Task Command(TuiController ui, string command, Func<TuiView, bool> ready)
    {
        foreach (var c in command)
            ui.Key(new(c, (ConsoleKey)0, false, false, false), 30, 0, 12);
        Press(ui, ConsoleKey.Enter);
        for (var i = 0; i < 600; i++)
        {
            ui.Update();
            if (ready(ui.View())) return;
            await Task.Delay(5);
        }
        throw new InvalidDataException("command did not open its panel: " + command + "\n" + ui.View().Status);
    }

    private static void ClosePanel(TuiController ui)
    {
        Press(ui, ConsoleKey.Escape);
        Check(ui.View().Overlay is null, "panel did not close");
    }

    private static void Press(TuiController ui, ConsoleKey key, bool shift = false, bool ctrl = false) =>
        ui.Key(new('\0', key, shift, false, ctrl), 30, 0, 12);

    private static void Type(FuiPicker picker, string text)
    {
        foreach (var c in text)
            picker.Edit(new(c, (ConsoleKey)0, false, false, false), 12);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private class PlainSession : ITuiSession
    {
        private readonly Channel<TuiEvent> _events = Channel.CreateUnbounded<TuiEvent>();
        public string Project => "/plain";
        public string Id => "plain-session";
        public string? Model => null;
        public IReadOnlyList<TuiLine> History => [];
        public ChannelReader<TuiEvent> Events => _events.Reader;
        public Task SendAsync(string text, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(string id, bool allow, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CatalogSession(FuiSessionInfo info, FuiRuntimeCatalog catalog) :
        ITuiSession, IFuiSessionInspector, IFuiRuntimeCatalogSource, IFuiPlanSession
    {
        private readonly Channel<TuiEvent> _events = Channel.CreateUnbounded<TuiEvent>();
        public string Project => info.Project;
        public string Id => info.Id;
        public string? Model => info.Model;
        public string? CurrentPlanSnippet =>
            "[计划已更新]\n[>] 1. Inspect context (in_progress)\n[ ] 2. Report facts (pending)";
        public string ExecutionMode => info.ExecutionMode ?? "manual";
        public IReadOnlyList<TuiLine> History =>
        [
            new("user", "inspect"),
            new("thinking", "reasoning"),
            new("tool", "read_file [完成] {}"),
            new("assistant", "result [图片 × 1]")
        ];
        public ChannelReader<TuiEvent> Events => _events.Reader;
        public Task SendAsync(string text, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(string id, bool allow, CancellationToken ct) => Task.CompletedTask;
        public Task<FuiSessionInfo> InspectAsync(CancellationToken ct) => Task.FromResult(info);
        public Task<string> RetryLastUserAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<FuiRuntimeCatalog> ReadRuntimeCatalogAsync(CancellationToken ct) => Task.FromResult(catalog);
        public void Emit(TuiEvent value) => _events.Writer.TryWrite(value);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
