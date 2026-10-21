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
        var summary = session.History.Entries.Where(e => e.Message is not null || e.Summary is not null).TakeLast(100)
            .Select(e => new { id = e.Id, role = e.Message?.Role, onCurrentBranch = e.OnCurrentBranch,
                beforeCompaction = e.BeforeCompaction, text = Bound(e.Message is null ? e.Summary ?? "" :
                    string.Concat(e.Message.Content.OfType<PiTextBlock>().Select(b => b.Text))) }).ToArray();
        if (json) await output.WriteLineAsync(JsonSerializer.Serialize(new
        { id = session.Session.Id, name = session.Session.Name, projectId = session.Session.ProjectId,
            project = session.Project.Path, backend = BackendCapabilities.StorageId(session.Session.Backend),
            lastOpenedUtc = session.Session.LastOpened, active = session.Session.WasActive,
            leafId = session.History.LeafId, totalEntries = session.History.Entries.Count, summary }));
        else
        {
            await output.WriteLineAsync($"{session.Session.Id}  {BackendCapabilities.StorageId(session.Session.Backend)}  {session.Session.Name ?? "(unnamed)"}\n{session.Project.Path}");
            foreach (var entry in summary) await output.WriteLineAsync($"{entry.id} [{entry.role ?? "summary"}]: {entry.text}");
        }
    }

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
