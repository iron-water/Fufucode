namespace Fufucode.Core;

/// <summary>Product-owned deletion with project, path and cross-process lease validation.</summary>
public static class SessionDeletion
{
    public static void Delete(AppDataLayout layout, DesktopMetadataStore metadata, string projectPath, string sessionId)
    {
        var session = metadata.GetSession(sessionId);
        var project = metadata.GetProject(session.ProjectId);
        if (!AppDataLayout.PathKey(AppDataLayout.NormalizeProjectPath(projectPath)).Equals(AppDataLayout.PathKey(project.Path)))
            throw new InvalidOperationException("Session belongs to another project.");
        var projectDirectory = layout.ProjectSessionsDirectory(project.Id);
        // 后端会话目录必须与打开会话时的租约目录一致（OMP/FUMO/Claude/Codex/DSH 各有子目录，
        // Pi 使用项目根）。否则删除正在使用的会话不会被租约阻止。
        var directory = session.Backend switch
        {
            BackendId.Omp => Path.Combine(projectDirectory, "omp"),
            BackendId.NativeCSharp => Path.Combine(projectDirectory, "fumo"),
            BackendId.Claude => Path.Combine(projectDirectory, "claude"),
            BackendId.Codex => Path.Combine(projectDirectory, "codex"),
            BackendId.Dsh => Path.Combine(projectDirectory, "dsh"),
            _ => projectDirectory,
        };
        if (!AppDataLayout.IsUnderDirectory(directory, layout.SessionsDirectory))
            throw new InvalidOperationException("Session directory is outside the data root.");
        CheckNoLinks(directory, layout.Root);
        using var lease = SessionLease.Acquire(directory, session.Id);
        // Re-read after acquiring the lease; another process may have updated metadata while we waited.
        session = metadata.GetSession(sessionId);
        if (session.ProjectId != project.Id) throw new InvalidOperationException("Session project changed.");
        string? quarantine = null;
        if (session.FilePath is { } file)
        {
            // Pi 使用 .jsonl；FUMO 与协议适配插件使用 .json。两者都必须位于本会话目录内。
            var extension = Path.GetExtension(file);
            var supported = extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                (session.Backend != BackendId.Pi && extension.Equals(".json", StringComparison.OrdinalIgnoreCase));
            if (!AppDataLayout.IsUnderDirectory(file, directory) || !supported)
                throw new InvalidOperationException("Stored session path is outside its project or has an unsupported extension.");
            CheckNoLinks(file, layout.Root);
            if (File.Exists(file))
            {
                quarantine = file + ".deleting-" + Guid.NewGuid().ToString("N");
                File.Move(file, quarantine);
            }
        }
        try { metadata.DeleteSession(session); }
        catch
        {
            if (quarantine is not null) File.Move(quarantine, session.FilePath!);
            throw;
        }
        // If final unlink fails, report failure. The unique quarantine retains recoverable data.
        if (quarantine is not null) File.Delete(quarantine);
    }

    private static void CheckNoLinks(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (full != boundary && !AppDataLayout.IsUnderDirectory(full, boundary))
            throw new InvalidOperationException("Deletion path is outside the data root.");
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing session deletion through a link or junction.");
            if (current.Equals(boundary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
        }
    }
}
