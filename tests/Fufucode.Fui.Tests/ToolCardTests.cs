using System.Text.Json;
using System.Threading.Channels;
using Fufucode.App.Abstractions;
using Fufucode.Fui;

namespace Fufucode.Fui.Tests;

internal static class ToolCardTests
{
    internal static void Run()
    {
        Registry();
        ReadWriteEdit();
        SearchAndPlan();
        CommandFacts();
        StreamingJsonAndSanitization();
        Fallbacks();
        BoundedOutput();
        Layout();
        EventProjection();
    }

    private static void Registry()
    {
        var expected = new Dictionary<string, ToolCardKind>(StringComparer.Ordinal)
        {
            ["read_file"] = ToolCardKind.Read,
            ["write_file"] = ToolCardKind.Write,
            ["edit_file"] = ToolCardKind.Edit,
            ["list_dir"] = ToolCardKind.List,
            ["grep_search"] = ToolCardKind.Grep,
            ["glob_files"] = ToolCardKind.Glob,
            ["run_command"] = ToolCardKind.Command,
            ["update_plan"] = ToolCardKind.Plan,
            ["task"] = ToolCardKind.Task,
            ["subagent"] = ToolCardKind.Task,
            ["mcp__browser_click"] = ToolCardKind.Plugin,
            ["plugin.search"] = ToolCardKind.Plugin,
            ["web_search"] = ToolCardKind.WebSearch,
            ["fetch"] = ToolCardKind.Fetch,
            ["unmapped_tool"] = ToolCardKind.Unknown
        };
        foreach (var (name, kind) in expected) Eq(kind, ToolCardRegistry.Classify(name), "registry " + name);
    }

    private static void ReadWriteEdit()
    {
        var read = Project("read_file", """{"path":"src/中文.cs"}""", ToolCallStatus.Completed,
            "1\tclass 中 { }\n2\t// 内容\n3\tend", null);
        Eq(ToolCardKind.Read, read.Kind, "read kind");
        Eq("src/中文.cs", read.Target, "read target");
        Check(read.Stats.Contains("内容 3 行"), "read output line count");
        Check(read.Sections.Any(section => section.Lines.Any(line => line.Language == "csharp")),
            "read path selects C# highlighting");

        var content = "第一行\nsecond line\n";
        var write = Project("write_file", JsonSerializer.Serialize(new { path = "docs/说明.md", content }),
            ToolCallStatus.Completed, $"已成功写入文件 'docs/说明.md'（{content.Length} 字符）。", null);
        Eq(ToolCardKind.Write, write.Kind, "write kind");
        Eq("docs/说明.md", write.Target, "write target");
        Check(write.Stats.Contains("内容 2 行"), "write content line count");
        Check(write.Sections.Any(section => section.Title == "写入内容" &&
            string.Join('\n', section.Lines.Select(line => line.Text)) == "第一行\nsecond line"), "write content preview");

        var editArgs = JsonSerializer.Serialize(new
        {
            path = "src/Program.cs",
            old_string = "old",
            new_string = "new\nsecond"
        });
        var edit = Project("edit_file", editArgs, ToolCallStatus.Completed,
            "已成功更新 'src/Program.cs'（替换了 2 处）。\n@@ 第 1 行附近变更 @@\n- old\n+ new\n+ second", null);
        Eq(ToolCardKind.Edit, edit.Kind, "edit kind");
        Check(edit.Stats.Contains("1 处编辑"), "edit argument count");
        Check(edit.Stats.Contains("替换 2 处"), "edit replacement count from output");
        Check(edit.Stats.Contains("+2 / -1"), "edit diff line counts from output");
        Check(edit.Sections.Any(section => section.Title == "改动参数" && section.Truncated == false),
            "edit argument diff preview");
    }

    private static void SearchAndPlan()
    {
        var list = Project("list_dir", """{"path":"src"}""", ToolCallStatus.Completed,
            "[DIR]  src\n[FILE] a.cs\n[FILE] 中.txt\n[列表已截断，仅显示前 500 项]", null);
        Check(list.Stats.Contains("目录 1 · 文件 2"), "list group counts");
        Check(list.Stats.Contains("含截断标记"), "list truncation marker");

        var grep = Project("grep_search", """{"pattern":"TODO","path":"src"}""", ToolCallStatus.Completed,
            "## a.cs (2 处命中)\n1: TODO\n2: TODO\n## b.cs (1 处命中)\n9: TODO", null);
        Check(grep.Stats.Contains("3 处 · 2 文件"), "grep grouped counts");
        Eq("TODO", grep.Target, "grep pattern target");

        var glob = Project("glob_files", """{"pattern":"**/*.cs","path":"."}""", ToolCallStatus.Completed,
            "a.cs\nsrc/中.cs\n[已达到最大数量（100 条），列表被截断。]", null);
        Check(glob.Stats.Contains("2 个文件"), "glob file count");
        Check(glob.Stats.Contains("含截断标记"), "glob truncation marker");

        var planArgs = """{"steps":[{"title":"检查投影","status":"completed"},{"title":"接入布局","status":"in_progress"},{"title":"真实回放","status":"pending"}],"notes":"保留证据"}""";
        var plan = Project("update_plan", planArgs, ToolCallStatus.Completed,
            "[计划已更新]\n[x] 1. 检查投影 (completed)\n[>] 2. 接入布局 (in_progress)\n[ ] 3. 真实回放 (pending)\n备注: 保留证据",
            null);
        Eq(ToolCardKind.Plan, plan.Kind, "plan kind");
        Eq("接入布局", plan.Target, "plan active step");
        Check(plan.Stats.Contains("计划 1/3"), "plan progress");
        Check(plan.Sections.Any(section => section.Title == "计划" &&
            section.Lines.Any(line => line.Text.Contains("检查投影", StringComparison.Ordinal))), "plan steps");
    }

    private static void CommandFacts()
    {
        const string args = """{"command":"dotnet build","cwd":"src","shell":"pwsh","timeout_seconds":60}""";
        var failure = Project("run_command", args, ToolCallStatus.Failed,
            "ExitCode: 1\nSTDOUT:\nbuild started\nSTDERR:\nerror CS1000", null);
        Eq("dotnet build", failure.Target, "command target");
        Eq("失败", failure.StatusLabel, "command failure state");
        Check(failure.Stats.Contains("退出码 1"), "command exit code");
        Check(failure.Stats.Contains("stdout 1 行"), "command stdout count");
        Check(failure.Stats.Contains("stderr 1 行"), "command stderr count");
        Check(failure.Sections.Select(section => section.Title)
            .SequenceEqual(new[] { "参数", "命令", "ExitCode", "STDOUT", "STDERR" }), "command fact sections");
        Check(failure.Sections.Single(section => section.Title == "命令")
            .Lines.All(line => line.Language == "powershell"), "command syntax highlighting");

        var plain = Project("run_command", args, ToolCallStatus.Completed,
            "ordinary text mentions ExitCode: 7 but is not FUMO's structured body", null);
        Check(!plain.Sections.Any(section => section.Title is "STDOUT" or "STDERR" or "ExitCode"),
            "plain command output never fabricates split streams");
        Check(!plain.Stats.Any(stat => stat.StartsWith("退出码", StringComparison.Ordinal)),
            "plain command output never fabricates an exit code");
    }

    private static void StreamingJsonAndSanitization()
    {
        const string hostileArgs = "{\"path\":\"src/中\x1b]52;c;discard\a";
        var pending = Project("read_file", hostileArgs, ToolCallStatus.Pending, null, null);
        Eq(null, pending.Target, "incomplete JSON does not fabricate a target");
        Eq("待执行", pending.StatusLabel, "pending state");
        Check(pending.Sections.Any(section => section.Title == "参数（未完整）"), "partial JSON fallback");
        var all = AllToolText(pending);
        Check(!all.Contains('\x1b') && !all.Contains('\a') && !all.Contains('\u009b'),
            "tool card sanitizes ANSI and OSC fragments");

        var hostileOutput = "safe\x1b[31m red\x1b]0;owned\a\n中\u009b2J";
        var running = Project("grep_search", """{"pattern":"中","path":"."}""", ToolCallStatus.Running,
            hostileOutput, null);
        Eq("执行中", running.StatusLabel, "running state");
        all = AllToolText(running);
        Check(!all.Contains('\x1b') && !all.Contains('\a') && !all.Contains('\u009b'),
            "tool output cannot inject controls");
        Check(all.Contains("safe"), "sanitized output remains visible");
    }

    private static void Fallbacks()
    {
        var unknown = Project("fancy_tool", """{"job":"作业名","secret":"值"}""", ToolCallStatus.Completed,
            "UNKNOWN-OUTPUT", false);
        Eq(ToolCardKind.Unknown, unknown.Kind, "unknown kind");
        Eq("fancy_tool", unknown.Verb, "unknown semantic label is its real name");
        Eq("作业名", unknown.Target, "unknown target from a concrete string argument");
        Check(unknown.Sections.Any(section => section.Title == "参数"), "unknown bounded arguments");
        Check(unknown.Sections.Any(section => section.Title == "输出"), "unknown bounded output");

        var plugin = Project("mcp__browser_click", """{"selector":"#save"}""", ToolCallStatus.Completed,
            "clicked", false);
        Eq(ToolCardKind.Plugin, plugin.Kind, "MCP kind");
        Eq("插件工具", plugin.Verb, "MCP verb");

        var task = Project("subagent", """{"agent":"reviewer","task":"检查中文路径","prompt":"只读" }""",
            ToolCallStatus.Running, null, null);
        Eq(ToolCardKind.Task, task.Kind, "subagent kind");
        Eq("reviewer", task.Target, "subagent job name");
        Check(task.Sections.Any(section => section.Title == "任务"), "subagent assignment facts");

        Eq(ToolCardKind.WebSearch, Project("web_search", """{"query":"FUMO 中文"}""",
            ToolCallStatus.Running, null, null).Kind, "web search kind");
        Eq("https://example.test/中", Project("fetch", """{"url":"https://example.test/中"}""",
            ToolCallStatus.Running, null, null).Target, "fetch URL target");
    }

    private static void BoundedOutput()
    {
        var lines = Enumerable.Range(1, 400).Select(i => $"第 {i} 行 " + new string('中', 40));
        var output = string.Join('\n', lines);
        var card = Project("read_file", """{"path":"large.txt"}""", ToolCallStatus.Completed, output, null);
        var section = card.Sections.Single(s => s.Title == "内容");
        Check(section.Truncated, "large output marks section truncated");
        var text = string.Join('\n', section.Lines.Select(line => line.Text));
        Check(text.Contains("已截断", StringComparison.Ordinal), "large output has explicit Chinese truncation marker");
        Check(text.Contains("共 400 行", StringComparison.Ordinal), "large output reports exact source line count");
        Check(section.Lines.Count < 160, "large output is row-bounded");

        var hugeArgs = """{"path":"x","content":""" + JsonSerializer.Serialize(new string('中', 20_000)) + "}";
        var write = Project("write_file", hugeArgs, ToolCallStatus.Pending, null, null);
        var contentPreview = write.Sections.First(s => s.Title == "写入内容");
        Check(contentPreview.Truncated, "large arguments are bounded");
        Check(string.Join('\n', contentPreview.Lines.Select(line => line.Text)).Contains("已截断", StringComparison.Ordinal),
            "large arguments keep the explicit truncation marker");
    }

    private static void Layout()
    {
        var snapshot = new ToolCallSnapshot("read_file", """{"path":"src/中文.cs"}""",
            ToolCallStatus.Running, "读取中", "1\tclass 中 { }\nSECRET-BODY", false, 1234);
        var lines = new[] { new TuiLine("tool", "read_file", snapshot) };
        var view = new TuiView("C:/项目", "S1", "model", "运行中", lines, "", 0, 0, null);
        var layout = new TerminalLayout();

        var collapsed = layout.Arrange(view, 80, 30);
        var collapsedText = string.Join('\n', collapsed.Rows.Select(row => row.Text));
        Check(collapsedText.Contains("工具 ▸", StringComparison.Ordinal) &&
              collapsedText.Contains("读取", StringComparison.Ordinal) &&
              collapsedText.Contains("执行中", StringComparison.Ordinal) &&
              collapsedText.Contains("src/中文.cs", StringComparison.Ordinal), "collapsed tool header facts");
        Check(!collapsedText.Contains("SECRET-BODY", StringComparison.Ordinal), "collapsed tool hides body");

        var expandedView = view with { ExpandTools = true };
        var expanded = layout.Arrange(expandedView, 80, 40);
        var expandedText = string.Join('\n', expanded.Rows.Select(row => row.Text));
        Check(expandedText.Contains("工具 ▾", StringComparison.Ordinal), "expanded tool disclosure marker");
        Check(expandedText.Contains("参数", StringComparison.Ordinal) &&
              expandedText.Contains("内容", StringComparison.Ordinal), "expanded tool sections");
        Check(expandedText.Contains("SECRET-BODY", StringComparison.Ordinal), "expanded tool shows output");
        Check(expanded.Rows.Any(row => row.Ink == Ink.Code || row.Spans?.Any(span => span.Ink != Ink.Tool) == true),
            "expanded code uses semantic syntax spans");

        var failed = new TuiView("C:/项目", "S1", "model", "失败",
            [new TuiLine("tool", "run_command", new ToolCallSnapshot("run_command",
                """{"command":"dotnet build"}""", ToolCallStatus.Failed, "退出码 1",
                "ExitCode: 1\nSTDOUT:\n\nSTDERR:\nerror", true))], "", 0, 0, null,
            ExpandTools: true);
        Check(layout.Arrange(failed, 80, 30).Rows.Any(row => row.Ink == Ink.Error &&
            row.Text.Contains("失败", StringComparison.Ordinal)), "failed tool uses error tone");

        foreach (var width in new[] { 12, 20, 40, 80, 120 })
        {
            foreach (var expand in new[] { false, true })
            {
                var frame = layout.Arrange(view with { ExpandTools = expand }, width, 32);
                Check(frame.Rows.All(row => TerminalText.Width(row.Text) < width && !row.Text.Any(char.IsControl)),
                    $"tool card stays within width {width}, expand={expand}");
                Check(frame.CursorRow >= -1 && frame.CursorRow < 32 && frame.CursorColumn >= 0 &&
                      frame.CursorColumn < width, $"caret stays inside {width}x32");
            }
        }

        var stream = new TuiLine("tool", "read_file", snapshot);
        var live = new TerminalLayout();
        foreach (var fragment in new[] { "{\"pa", "th\":\"中", "文.cs\"}", "\x1b[3", "1m" })
        {
            var next = fragment.StartsWith('{') ? fragment : stream.Tool!.ArgumentsJson + fragment;
            stream = stream with { Tool = stream.Tool! with { ArgumentsJson = next } };
            var sample = view with { Lines = [stream], ExpandTools = true };
            Check(new TerminalLayout().Arrange(sample, 55, 30).Rows
                .SequenceEqual(live.Arrange(sample, 55, 30).Rows), "streaming tool card equals a fresh card");
        }
    }

    private static void EventProjection()
    {
        var channel = Channel.CreateUnbounded<TuiEvent>();
        var projection = new FumoEventProjection(channel.Writer);
        projection.OnEvent(null, new SessionEvent.MessageStarted("m", MessageRole.Assistant, null, 1));
        projection.OnEvent(null, new SessionEvent.BlockStarted("m", 0, MessageBlockKind.ToolCall, "call-1", "read_file"));
        projection.OnEvent(null, new SessionEvent.ToolCallAppended("m", 0, "call-1", "{\"path\":\"a"));
        projection.OnEvent(null, new SessionEvent.ToolCallAppended("m", 0, "call-1", ".txt\"}"));
        projection.OnEvent(null, new SessionEvent.ToolCallStatusChanged("m", 0, "call-1",
            ToolCallStatus.Running, "读取中", 5));

        var messages = Drain(channel.Reader);
        var running = messages.Last(message => message.Tool is not null);
        Eq("{\"path\":\"a.txt\"}", running.Tool!.ArgumentsJson, "streaming event exposes assembled arguments");
        Eq(ToolCallStatus.Running, running.Tool.Status, "streaming event exposes running status");
        Eq<long?>(5L, running.Tool.DurationMs, "streaming event preserves reported duration");

        projection.OnEvent(null, new SessionEvent.BlockFinished("m", 0,
            new ToolCallBlock("call-1", "read_file", "{\"path\":\"a.txt\"}",
                ToolCallStatus.Completed, "已读取 1 行", "alpha", false, 12)));
        var finished = Drain(channel.Reader).Last(message => message.Tool is not null);
        Eq(ToolCallStatus.Completed, finished.Tool!.Status, "BlockFinished supplies terminal status");
        Eq("alpha", finished.Tool.Output, "BlockFinished supplies exact output");
        Eq<long?>(12L, finished.Tool.DurationMs, "BlockFinished supplies reported duration");
    }

    private static ToolCardView Project(string name, string arguments, ToolCallStatus? status,
        string? output, bool? isError, long? duration = null, string? statusText = null) =>
        ToolCallProjection.Project(new ToolCallSnapshot(name, arguments, status, statusText,
            output, isError ?? false, duration));

    private static List<TuiEvent.Message> Drain(ChannelReader<TuiEvent> reader)
    {
        var messages = new List<TuiEvent.Message>();
        while (reader.TryRead(out var item))
            if (item is TuiEvent.Message message) messages.Add(message);
        return messages;
    }

    private static string AllToolText(ToolCardView card) =>
        string.Join('\n', card.Sections.SelectMany(section => section.Lines.Select(line => line.Text))
            .Prepend(card.Target ?? ""));

    private static void Eq<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{context}: expected [{expected}], got [{actual}]");
    }

    private static void Check(bool condition, string context)
    {
        if (!condition) throw new Exception(context);
    }
}
