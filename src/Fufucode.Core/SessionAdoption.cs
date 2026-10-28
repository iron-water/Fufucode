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
                if (Directory.Exists(fumoDirectory))
                {
                    foreach (var file in Directory.EnumerateFiles(fumoDirectory, "*.json"))
                    {
                        var sessionId = Path.GetFileNameWithoutExtension(file);
                        if (metadata.AdoptSession(sessionId, targetId, file)) sessions++;
                    }
                }

                if (!string.Equals(targetId, directoryId, StringComparison.Ordinal))
                {
                    RelocateFiles(fumoDirectory, layout, targetId);
                    relocated++;
                }
            }
        }
        catch
        {
            // 收养是补救路径：任何异常都不能让启动失败，下次启动还会再试。
        }

        return new Result(projects, sessions, relocated, unreadable);
    }

    private static void RelocateFiles(string fromFumo, AppDataLayout layout, string targetId)
    {
        try
        {
            if (!Directory.Exists(fromFumo)) return;
            var toFumo = Path.Combine(layout.ProjectSessionsDirectory(targetId), "fumo");
            Directory.CreateDirectory(toFumo);
            foreach (var file in Directory.EnumerateFiles(fromFumo, "*.json"))
            {
                var destination = Path.Combine(toFumo, Path.GetFileName(file));
                if (File.Exists(destination)) continue; // 目标已有同名会话，不覆盖
                File.Move(file, destination);
            }
            if (Directory.GetFiles(fromFumo, "*.json").Length == 0) Directory.Delete(fromFumo, recursive: false);
            var orphan = Path.GetDirectoryName(fromFumo);
            if (orphan is null || !Directory.Exists(orphan)) return;
            // 只剩标记与 .locks 时才收掉；还有别的东西说明它有别的用途，留着。
            foreach (var entry in Directory.GetFileSystemEntries(orphan))
            {
                var name = Path.GetFileName(entry);
                if (name != MarkerFileName && name != ".locks") return;
            }
            Directory.Delete(orphan, recursive: true);
        }
        catch
        {
            // 搬不动就留在原地：索引已修好，下次启动再搬。
        }
    }
}
