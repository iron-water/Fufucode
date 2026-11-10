using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Imports;

/// <summary>
/// Claude Code 转录导入：解析规则与落库行为。
/// 重点是"哪些内容不许进来"（工具结果、思考块、注入提醒、命令回显）与"重复执行不出错"，
/// 因为导入的历史会直接成为 FUMO 的上下文，脏数据会跟着用户聊下去。
/// </summary>
internal static class ClaudeImportTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private static string Scratch(string root, string tag) =>
        Path.Combine(root, "tmp", "ccimport-" + tag + "-" + Guid.NewGuid().ToString("N"));

    private static ClaudeTranscript Parse(params string[] lines)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
        return ClaudeTranscriptReader.Parse(stream);
    }

    private static string Transcript(string directory, string sessionId, params string[] lines)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, sessionId + ".jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private static string User(string? cwd, string text, string? timestamp = null) =>
        $"{{\"type\":\"user\"{(cwd is null ? "" : $",\"cwd\":{Quote(cwd)}")}{(timestamp is null ? "" : $",\"timestamp\":{Quote(timestamp)}")}," +
        $"\"message\":{{\"role\":\"user\",\"content\":{Quote(text)}}}}}";

    private static string Assistant(string text, string? timestamp = null) =>
        $"{{\"type\":\"assistant\"{(timestamp is null ? "" : $",\"timestamp\":{Quote(timestamp)}")}," +
        $"\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":{Quote(text)}}}]}}}}";

    private static string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    public static void Run(string root)
    {
        // ── 1) 正文抽取：工具调用压成一行；工具结果、思考块、注入提醒、快照行全部不进历史 ──
        var parsed = Parse(
            "{\"type\":\"file-history-snapshot\",\"snapshot\":{}}",
            "{\"type\":\"user\",\"cwd\":\"C:\\\\work\\\\proj\",\"timestamp\":\"2026-10-01T10:00:00.000Z\"," +
            "\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"<system-reminder>注入的规矩</system-reminder>修一下设置页\"}]}}",
            "{\"type\":\"assistant\",\"timestamp\":\"2026-10-01T10:00:05.000Z\",\"message\":{\"role\":\"assistant\"," +
            "\"content\":[{\"type\":\"thinking\",\"thinking\":\"内部推理不该进历史\"}," +
            "{\"type\":\"text\",\"text\":\"我先看代码\"}," +
            "{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Read\",\"input\":{\"file_path\":\"C:\\\\work\\\\proj\\\\a.cs\"}}]}}",
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"巨大输出\"}]}}",
            "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"role\":\"user\",\"content\":\"这是元信息\"}}",
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"继续\"}}",
            "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"好\"}]}}",
            "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"接着做\"}]}}",
            "{\"type\":\"cost-state\",\"total\":1}",
            "{\"type\":\"summary\",\"summary\":\"设置页修复\"}");
        Check(parsed.Messages.Count == 4, "只保留用户/助手正文：4 条");
        Check(parsed.Messages[0].Role == PureAgentRole.User && parsed.Messages[0].Content == "修一下设置页",
            "注入的 system-reminder 被剥掉，用户正文保留");
        Check(parsed.Messages[1].Role == PureAgentRole.Assistant &&
              parsed.Messages[1].Content == "我先看代码\n\n▸ Read: C:\\work\\proj\\a.cs",
            "助手正文与工具摘要同条合并，思考块被丢弃");
        Check(!parsed.Messages.Any(message => message.Content.Contains("巨大输出", StringComparison.Ordinal)),
            "工具结果不进历史");
        Check(!parsed.Messages.Any(message => message.Content.Contains("内部推理", StringComparison.Ordinal)),
            "思考块不进历史");
        Check(parsed.Messages[2].Content == "继续" && parsed.Messages[3].Content == "好\n\n接着做",
            "连续助手正文合并为一条，不与用户轮次串味");
        Check(parsed.Title == "设置页修复", "标题取转录自带的 summary");
        Check(parsed.WorkingDirectory == "C:\\work\\proj", "工作目录取自转录自身的 cwd");
        Check(parsed.StartedUtc == DateTimeOffset.Parse("2026-10-01T10:00:00.000Z") &&
              parsed.UpdatedUtc == DateTimeOffset.Parse("2026-10-01T10:00:05.000Z"),
            "起止时间取自转录时间戳");

        // ── 2) 命令回显、中断提示、图片占位 ──
        var noise = Parse(
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"<command-name>/model</command-name>\"}}",
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"<local-command-stdout>Set model</local-command-stdout>\"}}",
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"[Request interrupted by user]\"}}",
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"image\",\"source\":{}}]}}",
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"   \"}}");
        Check(noise.Messages.Count == 1 && noise.Messages[0].Content == "[图片]",
            "命令回显/中断提示/空白轮次被丢弃，图片留占位");

        // ── 3) 落库：写原生会话文件 + 登记项目与会话，且能被只读报告读回 ──
        var scratch = Scratch(root, "basic");
        var layout = new AppDataLayout(scratch);
        var projectPath = Path.Combine(scratch, "project");
        Directory.CreateDirectory(projectPath);
        var source = Path.Combine(scratch, "claude", "C--work-project");
        var sessionId = "11111111-2222-3333-4444-555555555555";
        Transcript(source, sessionId,
            User(projectPath, "把设置页的导航拆出来", "2026-10-02T08:00:00.000Z"),
            Assistant("先看现有代码", "2026-10-02T08:30:00.000Z"));
        var metadata = new DesktopMetadataStore(layout);
        var report = ClaudeCodeSessionImport.Run(layout, metadata,
            new ClaudeImportOptions(SourceDirectories: [source]));
        Check(report.Imported == 1 && report.Failed == 0, "一条转录被导入");
        var session = metadata.GetSession(sessionId);
        Check(session.ProjectId == metadata.GetProject(session.ProjectId).Id &&
              string.Equals(metadata.GetProject(session.ProjectId).Path, Path.GetFullPath(projectPath),
                  StringComparison.OrdinalIgnoreCase),
            "会话挂在转录 cwd 对应的项目下");
        Check(session.Name == "[CC] 把设置页的导航拆出来", "会话名带 [CC] 前缀，首条用户正文命名");
        Check(session.LastOpened == DateTimeOffset.Parse("2026-10-02T08:30:00.000Z"),
            "最后活动时间取转录时间，不冒充新会话");
        Check(session.Backend == BackendId.NativeCSharp && session.FilePath is not null &&
              File.Exists(session.FilePath), "落成本机原生会话文件");

        var native = PersistentSessionManager.LoadNativeMessages(session.FilePath!)!;
        Check(native.Messages.Count == 2 && native.Messages[0].Role == PureAgentRole.User &&
              native.Messages[1].Content == "先看现有代码", "会话文件是 FUMO 可解析的历史");
        var readBack = ReadOnlySessionReport.Load(layout, projectPath, sessionId);
        Check(readBack.History.Entries.Count == 2 &&
              readBack.History.CurrentBranchMessages.First().Role == "user",
            "只读报告能读回这段导入的历史（GUI 走同一条路径）");

        // 幂等：再跑一次不重复导入
        var again = ClaudeCodeSessionImport.Run(layout, metadata, new ClaudeImportOptions(SourceDirectories: [source]));
        Check(again.Imported == 0 && again.Skipped == 1, "重复导入是幂等的");
        Check(metadata.ListSessions(session.ProjectId).Count == 1, "重复导入不新增会话行");

        // ── 4) 范围过滤：别的项目的历史不进来 ──
        var otherSource = Path.Combine(scratch, "claude", "C--work-other");
        var otherProject = Path.Combine(scratch, "other-project");
        Directory.CreateDirectory(otherProject);
        Transcript(otherSource, "99999999-8888-7777-6666-555555555555", User(otherProject, "别的产品"));
        var scoped = ClaudeCodeSessionImport.Run(layout, metadata,
            new ClaudeImportOptions(SourceDirectories: [otherSource], ScopePath: projectPath));
        Check(scoped.Imported == 0 && scoped.Skipped == 1, "范围之外的转录被跳过");
        Check(metadata.ListSessions(metadata.GetProject(session.ProjectId).Id).Count == 1,
            "被跳过的转录没有污染本项目的会话列表");

        // ── 5) 目录已消失：折叠到最近的现存上级，并如实报告 ──
        var dead = Path.Combine(projectPath, "gone", "worktree");
        Transcript(Path.Combine(scratch, "claude", "C--work-project--gone-worktree"),
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", User(dead, "旧工作树里的会话"));
        var foldedReport = ClaudeCodeSessionImport.Run(layout, metadata,
            new ClaudeImportOptions(SourceDirectories: [Path.Combine(scratch, "claude", "C--work-project--gone-worktree")]));
        var folded = foldedReport.Entries.Single();
        Check(folded.Status == "imported" &&
              string.Equals(folded.ProjectPath, Path.GetFullPath(projectPath), StringComparison.OrdinalIgnoreCase),
            "消失的项目目录折叠到现存上级");
        Check(folded.Detail is { Length: > 0 } && folded.Detail.Contains("折叠", StringComparison.Ordinal),
            "折叠发生时如实记录原路径");

        // ── 6) 同一会话 id 出现在两个项目目录：只导最大的一份 ──
        var dupA = Path.Combine(scratch, "claude", "dup-a");
        var dupB = Path.Combine(scratch, "claude", "dup-b");
        Transcript(dupA, "0123456789abcdef0123456789abcdef", User(projectPath, "短的一份"));
        Transcript(dupB, "0123456789abcdef0123456789abcdef",
            User(projectPath, "长的一份"), Assistant("更完整的正文"), Assistant("补齐后半段"));
        var dedupe = ClaudeCodeSessionImport.Run(layout, metadata,
            new ClaudeImportOptions(SourceDirectories: [dupA, dupB], ProjectPath: projectPath));
        Check(dedupe.Entries.Count == 1, "同一 id 的多份转录只出一条");
        Check(dedupe.Imported == 1, "重复 id 取最大的一份导入");
        var dedupedSession = metadata.GetSession("0123456789abcdef0123456789abcdef");
        var dedupedMessages = PersistentSessionManager.LoadNativeMessages(dedupedSession.FilePath!)!.Messages;
        Check(dedupedMessages.Count == 2 &&
              dedupedMessages[1].Content.Contains("补齐后半段", StringComparison.Ordinal),
            "导入的是内容更完整的那份");

        // ── 7) dry-run 不写任何文件 ──
        var dryRoot = Scratch(root, "dry");
        var dryLayout = new AppDataLayout(dryRoot);
        var dryProject = Path.Combine(dryRoot, "project");
        Directory.CreateDirectory(dryProject);
        Transcript(Path.Combine(dryRoot, "claude"), "fedcba9876543210fedcba9876543210", User(dryProject, "试运行"));
        var dry = ClaudeCodeSessionImport.Run(dryLayout, new DesktopMetadataStore(dryLayout),
            new ClaudeImportOptions(SourceDirectories: [Path.Combine(dryRoot, "claude")], DryRun: true));
        Check(dry.Imported == 1 && dry.Skipped == 0, "dry-run 报告可导入");
        Check(!Directory.Exists(Path.Combine(dryLayout.SessionsDirectory, "dry-run")) &&
              !Directory.EnumerateFiles(dryLayout.SessionsDirectory, "*.json", SearchOption.AllDirectories).Any(),
            "dry-run 不写会话文件");
    }
}
