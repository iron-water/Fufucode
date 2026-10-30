using System;
using System.IO;
using System.Text.Json;

namespace Fufucode.Core;

/// <summary>
/// 会话目录的收养。
///
/// 项目 id 是随机 GUID，会话文件放在 <c>sessions/&lt;projectId&gt;/</c> 下。
/// <c>desktop.db</c> 一旦丢行（迁移、重置、并发写坏、被别的构建目录顶掉），
/// 目录里完好的历史就永久打不开——实测发生过一次，只能手写 SQL 救回。
/// 这里让每个项目目录自带一个 <c>project.json</c> 标记，启动时按标记把项目与会话收养回索引。
/// </summary>
public static class SessionAdoption
{
    public const string MarkerFileName = "project.json";

    public sealed record Result(int ProjectsAdopted, int SessionsAdopted, int Relocated, int Unreadable)
    {
        public bool Changed => ProjectsAdopted > 0 || SessionsAdopted > 0 || Relocated > 0;
    }

    public static string MarkerPath(AppDataLayout layout, string projectId) =>
        Path.Combine(layout.ProjectSessionsDirectory(projectId), MarkerFileName);

    /// <summary>写入目录标记；内容一致时不写盘（这条在打开会话的热路径上）。</summary>
    public static void WriteMarker(AppDataLayout layout, string projectId, string projectPath)
    {
        try
        {
            var path = MarkerPath(layout, projectId);
            if (TryReadMarker(path, out var existingId, out var existingPath) &&
                existingId == projectId &&
                string.Equals(existingPath, projectPath, StringComparison.OrdinalIgnoreCase))
                return;
            AtomicJsonFile.Write(path, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("id", projectId);
                writer.WriteString("path", projectPath);
                writer.WriteEndObject();
            });
        }
        catch
        {
            // 标记只是救援线索，写不进去不能影响会话打开。
        }
    }

    private static bool TryReadMarker(string path, out string? id, out string? projectPath)
    {
        id = projectPath = null;
        try
        {
            if (!File.Exists(path)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String) id = i.GetString();
            if (root.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String) projectPath = p.GetString();
            return !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(projectPath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 扫描会话目录，把索引里不存在的项目/会话收养回来。
    /// 幂等：已有行不动；路径已经不存在的目录不猜；同一项目在另一个 id 下时把文件搬过去，
    /// 因为读取路径由项目 id 推导（<c>SessionFilePath</c>），不搬就永远打不开。
    /// </summary>
    public static Result Reconcile(AppDataLayout layout, DesktopMetadataStore metadata)
    {
        var projects = 0;
        var sessions = 0;
        var relocated = 0;
        var unreadable = 0;
        try
        {
            // 先给已登记的项目补标记：没有标记，将来丢行时收养就无从推断路径。
            foreach (var project in metadata.ListProjects())
                WriteMarker(layout, project.Id, project.Path);

            if (!Directory.Exists(layout.SessionsDirectory)) return new Result(0, 0, 0, 0);
            foreach (var directory in Directory.EnumerateDirectories(layout.SessionsDirectory))
            {
                var directoryId = Path.GetFileName(directory);
                // .locks 等隐藏目录不是项目目录，也没有标记可依据。
                if (directoryId.StartsWith(".", StringComparison.Ordinal)) continue;
                if (!TryReadMarker(MarkerPath(layout, directoryId), out _, out var projectPath))
                {
                    continue; // 没有标记就无从推断路径，不做猜测式收养
                }

                // 路径已不在磁盘上就无从打开，先拒掉：TryGetProject 内部会规范化路径，
                // 目录不存在时它会直接抛，不能让它替我们做这个判断。
                if (!Directory.Exists(projectPath))
                {
                    unreadable++;
                    continue;
                }

                var targetId = metadata.TryGetProject(projectPath!)?.Id;
                if (targetId is null)
                {
                    if (!metadata.TryInsertProjectWithId(directoryId, projectPath!))
                    {
                        unreadable++;
                        continue;
                    }
                    targetId = directoryId;
                    projects++;
                }

                var fumoDirectory = Path.Combine(directory, "fumo");
                var sameProject = string.Equals(targetId, directoryId, StringComparison.Ordinal);
                // 目标项目与目录 id 不一致时，文件要搬到目标目录，索引里的 file_path 必须一起改；
                // 只在原目录登记而把文件搬走，会让会话行指向不存在的路径——侧栏看不到、点开也失败。
                var targetFumo = sameProject
                    ? fumoDirectory
                    : Path.Combine(layout.ProjectSessionsDirectory(targetId), "fumo");
                if (!sameProject && Directory.Exists(fumoDirectory))
                {
                    Directory.CreateDirectory(targetFumo);
                    relocated++;
                }

                if (Directory.Exists(fumoDirectory))
                {
                    foreach (var file in Directory.EnumerateFiles(fumoDirectory, "*.json"))
                    {
                        var sessionId = Path.GetFileNameWithoutExtension(file);
                        var finalPath = file;
                        if (!sameProject)
                        {
                            var destination = Path.Combine(targetFumo, Path.GetFileName(file));
                            if (!File.Exists(destination))
                            {
                                try { File.Move(file, destination); finalPath = destination; }
                                catch (IOException) { finalPath = file; } // 搬不动就留在原地，索引仍指向真实文件
                            }
                            else
                            {
                                finalPath = destination; // 目标已有同名文件：以它为准，不覆盖
                            }
                        }

                        if (sameProject)
                        {
                            // 目录与项目一致：只补缺失行，已有一律不动（启动期不产生无谓写盘）。
                            if (metadata.AdoptSession(sessionId, targetId, finalPath)) sessions++;
                            continue;
                        }

                        // 已有行随文件一起改挂，而不是只更新文件、留下指向旧路径的孤立行。
                        if (metadata.TryRepointSession(sessionId, targetId, finalPath)) continue;
                        if (metadata.AdoptSession(sessionId, targetId, finalPath)) sessions++;
                    }
                }

                if (!sameProject) TryRemoveEmptyOrphan(fumoDirectory);
            }
        }
        catch
        {
            // 收养是补救路径：任何异常都不能让启动失败，下次启动还会再试。
        }

        return new Result(projects, sessions, relocated, unreadable);
    }

    /// <summary>
    /// 搬空后的孤儿项目目录收掉。只剩标记与 .locks 时才删；还有别的东西说明它有别的用途，留着。
    /// 删除失败不影响索引状态，下次启动再试。
    /// </summary>
    private static void TryRemoveEmptyOrphan(string fromFumo)
    {
        try
        {
            if (!Directory.Exists(fromFumo)) return;
            if (Directory.GetFiles(fromFumo, "*.json").Length == 0) Directory.Delete(fromFumo, recursive: false);
            var orphan = Path.GetDirectoryName(fromFumo);
            if (orphan is null || !Directory.Exists(orphan)) return;
            foreach (var entry in Directory.GetFileSystemEntries(orphan))
            {
                var name = Path.GetFileName(entry);
                if (name != MarkerFileName && name != ".locks") return;
            }
            Directory.Delete(orphan, recursive: true);
        }
        catch
        {
            // 搬不动/删不掉就留在原地：索引已修好，下次启动再试。
        }
    }
}
