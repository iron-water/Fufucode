using System.Text;
using System.Text.Json;
using Fufucode.Core;

namespace Fufucode.Cli;

public static class CliSessionCommands
{
    public static void RequireProject(DesktopMetadataStore metadata, string projectPath, string id)
    {
        var session = metadata.GetSession(id);
        var project = metadata.GetProject(session.ProjectId);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!AppDataLayout.NormalizeProjectPath(projectPath).Equals(AppDataLayout.NormalizeProjectPath(project.Path), comparison))
            throw new InvalidOperationException("Session belongs to another project.");
    }

    public static async Task ShowAsync(PersistentSession session, bool json, TextWriter output)
    {
        var summary = Summarize(session.History);
        if (json) await output.WriteLineAsync(JsonSerializer.Serialize(new
        { id = session.Session.Id, name = session.Session.Name, projectId = session.Session.ProjectId,
            project = session.Project.Path, backend = BackendCapabilities.StorageId(session.Session.Backend),
            model = session.Chat.CurrentModel,
            lastOpenedUtc = session.Session.LastOpened, active = session.Session.WasActive,
            leafId = session.History.LeafId, totalEntries = session.History.Entries.Count, summary }));
        else
        {
            await output.WriteLineAsync($"{session.Session.Id}  {BackendCapabilities.StorageId(session.Session.Backend)}  {session.Session.Name ?? "(unnamed)"}\n{session.Project.Path}");
            if (session.Chat.CurrentModel is { Length: > 0 } model) await output.WriteLineAsync("model: " + model);
            foreach (var entry in summary) await output.WriteLineAsync($"{entry.id} [{entry.role ?? "summary"}]: {entry.text}");
        }
    }

    /// <summary>
    /// 不打开会话、不获取租约的只读查看。GUI 正持有该会话时也能工作；
    /// FUMO 与 Pi 历史由 <see cref="ReadOnlySessionReport"/> 统一解析。
    /// </summary>
    public static async Task ShowOfflineAsync(AppDataLayout layout, string projectPath, string id,
        bool json, TextWriter output)
    {
        var metadata = new DesktopMetadataStore(layout);
        RequireProject(metadata, projectPath, id);
        var record = metadata.GetSession(id);
        var project = metadata.GetProject(record.ProjectId);
        var report = ReadOnlySessionReport.Load(layout, projectPath, id);
        var summary = Summarize(report.History);
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                id = record.Id, name = record.Name, projectId = record.ProjectId, project = project.Path,
                backend = BackendCapabilities.StorageId(record.Backend), model = report.ModelId,
                thinkingLevel = report.ThinkingLevel, executionMode = report.ExecutionMode,
                lastOpenedUtc = record.LastOpened, active = record.WasActive,
                leafId = report.History.LeafId, totalEntries = report.History.Entries.Count,
                partialTail = report.PartialTail, summary
            }));
            return;
        }

        await output.WriteLineAsync($"{record.Id}  {BackendCapabilities.StorageId(record.Backend)}  {record.Name ?? "(unnamed)"}\n{project.Path}");
        if (report.ModelId is { Length: > 0 } model) await output.WriteLineAsync("model: " + model);
        if (report.ThinkingLevel is { Length: > 0 } thinking) await output.WriteLineAsync("thinking: " + thinking);
        if (report.PartialTail) await output.WriteLineAsync("warning: history tail is incomplete");
        foreach (var entry in summary) await output.WriteLineAsync($"{entry.id} [{entry.role ?? "summary"}]: {entry.text}");
    }

    /// <summary>
    /// 离线导出当前分支正文。Markdown 与 JSON 不依赖 App/Avalonia；
    /// PDF/Word 只在桌面端提供，CLI 不伪造这些格式。
    /// </summary>
    public static async Task<int> ExportAsync(AppDataLayout layout, string projectPath, string id,
        string format, string? destination, bool jsonStatus, TextWriter output, CancellationToken cancellationToken)
    {
        var normalized = format.ToLowerInvariant();
        if (normalized is "markdown") normalized = "md";
        if (normalized is "pdf" or "docx" or "doc")
            throw new ArgumentException("PDF/Word 导出仅在桌面端提供；CLI 支持 md 或 json。");
        if (normalized is not ("md" or "json"))
            throw new ArgumentException("Usage: sessions export <id> [--format md|json] [-o <path>].");
        var metadata = new DesktopMetadataStore(layout);
        RequireProject(metadata, projectPath, id);
        var record = metadata.GetSession(id);
        var project = metadata.GetProject(record.ProjectId);
        var report = ReadOnlySessionReport.Load(layout, projectPath, id);
        cancellationToken.ThrowIfCancellationRequested();

        var content = normalized == "md"
            ? BuildMarkdown(record, project, report)
            : BuildExportJson(record, project, report);
        if (destination is null)
        {
            await output.WriteAsync(content);
            return 0;
        }

        var path = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory))
            throw new DirectoryNotFoundException("Export destination directory does not exist: " + directory);
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("Export destination already exists.");
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await file.WriteAsync(bytes, cancellationToken);
        if (jsonStatus)
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            { exported = true, id, format = normalized, path, bytes = bytes.Length }));
        else
            await output.WriteLineAsync($"Exported {id} to {path} ({bytes.Length} bytes).");
        return 0;
    }

    private static string BuildMarkdown(SessionRecord record, ProjectRecord project,
        ReadOnlySessionReportResult report)
    {
        var text = new StringBuilder("# ").Append(record.Name ?? "会话").Append("\n\n");
        text.Append("- 会话 ID: ").Append(record.Id).Append('\n');
        text.Append("- 项目: ").Append(project.Path).Append('\n');
        text.Append("- 后端: ").Append(BackendCapabilities.StorageId(record.Backend)).Append('\n');
        if (report.ModelId is { Length: > 0 } model) text.Append("- 模型: ").Append(model).Append('\n');
        if (report.ThinkingLevel is { Length: > 0 } thinking) text.Append("- 思考等级: ").Append(thinking).Append('\n');
        text.Append("- 导出时间: ").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\n\n");
        if (report.PartialTail) text.Append("> 警告：会话历史尾部不完整，导出可能缺少最后一条。\n\n");
        foreach (var message in report.History.CurrentBranchMessages)
        {
            if (message.Role is not ("user" or "assistant")) continue;
            var content = string.Concat(message.Content.OfType<PiTextBlock>().Select(block => block.Text));
            if (string.IsNullOrWhiteSpace(content)) continue;
            text.Append("## ").Append(message.Role == "user" ? "用户" : "助手").Append("\n\n")
                .Append(content).Append("\n\n");
        }
        return text.ToString();
    }

    private static string BuildExportJson(SessionRecord record, ProjectRecord project,
        ReadOnlySessionReportResult report)
    {
        var messages = report.History.CurrentBranchMessages.Select(message => new
        {
            role = message.Role,
            text = string.Concat(message.Content.OfType<PiTextBlock>().Select(block => block.Text)),
            toolCalls = message.Content.OfType<PiToolCallBlock>().Select(call => new
            {
                id = call.Id,
                name = call.Name,
                arguments = call.Arguments?.GetRawText()
            }).ToArray()
        }).ToArray();
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            session = new
            {
                id = record.Id,
                name = record.Name,
                backend = BackendCapabilities.StorageId(record.Backend),
                project = project.Path,
                modelId = report.ModelId,
                thinkingLevel = report.ThinkingLevel,
                executionMode = report.ExecutionMode,
                partialTail = report.PartialTail
            },
            exportedAtUtc = DateTimeOffset.UtcNow,
            messages
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private sealed record SessionSummary(string id, string? role, bool onCurrentBranch,
        bool beforeCompaction, string text);

    private static SessionSummary[] Summarize(SessionHistory history) => history.Entries
        .Where(entry => entry.Message is not null || entry.Summary is not null).TakeLast(100)
        .Select(entry => new SessionSummary(
            entry.Id,
            entry.Message?.Role,
            entry.OnCurrentBranch,
            entry.BeforeCompaction,
            Bound(entry.Message is null ? entry.Summary ?? "" :
                string.Concat(entry.Message.Content.OfType<PiTextBlock>().Select(block => block.Text)))))
        .ToArray();

    /// <summary>未打开的会话改名只更新桌面元数据：外部后端运行时缺失不应阻止整理会话。</summary>
    public static async Task RenameAsync(AppDataLayout layout, string projectPath, string id, string name,
        bool json, TextWriter output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var metadata = new DesktopMetadataStore(layout);
        RequireProject(metadata, projectPath, id);
        metadata.SetSessionName(id, name);
        await PrintRenamedAsync(id, name, json, output);
    }

    public static async Task PrintRenamedAsync(string id, string name, bool json, TextWriter output) =>
        await output.WriteLineAsync(json
            ? JsonSerializer.Serialize(new { renamed = true, id, name })
            : $"Renamed {id} -> {name}.");

    public static async Task DeleteAsync(AppDataLayout layout, string projectPath, string id, bool json, TextWriter output)
    {
        SessionDeletion.Delete(layout, new DesktopMetadataStore(layout), projectPath, id);
        await output.WriteLineAsync(json ? JsonSerializer.Serialize(new { deleted = true, id }) : $"Deleted {id}.");
    }

    private static string Bound(string text) => text.Length > 2000 ? text[..2000] + "…" : text;
}
