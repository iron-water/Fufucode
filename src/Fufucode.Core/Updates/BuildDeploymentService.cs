using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Fufucode.Core.Updates;

/// <summary>
/// 本地构建部署：让"运行位置"等于"最新本机构建"。
///
/// 与更新（GitHub Release）的区别是设计边界，不是实现细节：
/// - 判断依据是构建身份 <see cref="BuildInfo.BuildId"/>，不是版本号；
/// - 不联网、不下载、不校验 SHA-256；
/// - 失败语义是"复制不完整，可回滚"，而不是"没拿到新版本，可重试网络"。
///
/// 流程：备份将被覆盖的文件 → 按内容哈希增量复制 → 锁定文件写入待重启清单 → 调用方重启。
/// 增量复制让"改完立刻部署"通常只复制几十到几百 KB，可稳定控制在秒级。
/// </summary>
public sealed class BuildDeploymentService
{
    private const string PendingFileName = "restart-pending.json";

    private readonly AppDataLayout _layout;

    public BuildDeploymentService(AppDataLayout layout) =>
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));

    /// <summary>一次部署的结果。</summary>
    /// <param name="Success">部署是否完成（含"无需部署"）。</param>
    /// <param name="RestartRequired">是否有本进程锁定的文件等待重启替换。</param>
    /// <param name="Message">面向用户的结果说明。</param>
    /// <param name="Copied">实际复制的文件数。</param>
    /// <param name="Skipped">内容一致而跳过的文件数。</param>
    /// <param name="Deferred">锁定后延到重启的文件数。</param>
    /// <param name="Build">部署目标构建；无法确定时为 null。</param>
    public sealed record Result(bool Success, bool RestartRequired, string Message,
        int Copied, int Skipped, int Deferred, BuildInfo? Build);

    /// <summary>
    /// 找到最新的本地构建目录。候选：环境变量 FUFUCODE_UPDATE_SOURCE_DIR、
    /// %LOCALAPPDATA%\Fufucode\updates\local、应用目录向上找的 out/app 与 out/build/app。
    /// 排除当前正在运行的目录与发布渠道构建。
    /// </summary>
    public BuildInfo? FindLatestBuild(out string? directory)
    {
        directory = null;
        var currentDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        BuildInfo? best = null;
        string? bestDirectory = null;
        foreach (var candidate in EnumerateLocalBuildDirectories())
        {
            var full = Path.GetFullPath(candidate);
            if (string.Equals(full, currentDirectory, StringComparison.OrdinalIgnoreCase)) continue;
            var info = BuildInfo.Read(full);
            if (info is null || !info.IsLocalChannel) continue;
            // 指针构建：真实构建目录可能在仓库里，而不是指针文件所在处。
            if (info.SourceDirectory is { Length: > 0 } pointer)
            {
                var resolved = Path.GetFullPath(pointer);
                if (!Directory.Exists(resolved)) continue;
                var realInfo = BuildInfo.Read(resolved);
                if (realInfo is null || !realInfo.IsLocalChannel) continue;
                full = resolved;
                info = realInfo;
                if (string.Equals(full, currentDirectory, StringComparison.OrdinalIgnoreCase)) continue;
            }
            if (best is null || IsNewer(info, best))
            {
                best = info;
                bestDirectory = full;
            }
        }

        directory = bestDirectory;
        return best;
    }

    /// <summary>
    /// 运行位置是否已经等于目标构建。
    /// 当前目录没有 build-info.json（例如 dotnet run）时一律视为"需要部署"，
    /// 因为无法证明运行的就是目标构建。
    /// </summary>
    public bool IsAlreadyDeployed(string targetDirectory)
    {
        var running = BuildInfo.Read(AppContext.BaseDirectory);
        var target = BuildInfo.Read(targetDirectory);
        return running is not null && target is not null && running.SameBuild(target);
    }

    /// <summary>
    /// 把 <paramref name="sourceDirectory"/> 部署到 <paramref name="targetDirectory"/>。
    /// 源目录必须带 build-info.json 且为本地渠道。
    /// </summary>
    public Result Deploy(string sourceDirectory, string targetDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        sourceDirectory = Path.GetFullPath(sourceDirectory);
        targetDirectory = Path.GetFullPath(targetDirectory);
        if (string.Equals(sourceDirectory, targetDirectory, StringComparison.OrdinalIgnoreCase))
            return new Result(true, false, "运行位置就是目标构建目录，无需部署。", 0, 0, 0,
                BuildInfo.Read(sourceDirectory));

        var build = BuildInfo.Read(sourceDirectory);
        if (build is null)
            return new Result(false, false, "构建目录缺少 build-info.json，无法确认构建身份。", 0, 0, 0, null);
        if (build.IsReleaseChannel)
            return new Result(false, false, "发布渠道构建不能通过本地部署覆盖。", 0, 0, 0, build);
        if (IsAlreadyDeployed(sourceDirectory))
            return new Result(true, false, $"运行中的已是构建 {build.BuildId}，无需部署。", 0, 0, 0, build);

        var backupDirectory = Path.Combine(_layout.UpdateBackupDirectory, build.BuildId);
        var copied = 0;
        var skipped = 0;
        var deferred = new List<string>();
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, source);
            // 待重启清单属于运行位置，不随构建分发。
            if (string.Equals(relative, PendingFileName, StringComparison.OrdinalIgnoreCase)) continue;

            var target = Path.Combine(targetDirectory, relative);
            var targetDir = Path.GetDirectoryName(target);
            if (targetDir is not null) Directory.CreateDirectory(targetDir);

            if (File.Exists(target) && SameContent(source, target))
            {
                skipped++;
                continue;
            }

            if (File.Exists(target))
            {
                var backup = Path.Combine(backupDirectory, relative);
                var backupDir = Path.GetDirectoryName(backup);
                if (backupDir is not null) Directory.CreateDirectory(backupDir);
                try { File.Copy(target, backup, overwrite: true); }
                catch (IOException) { /* 锁定文件的备份失败不阻止部署。 */ }
            }

            try
            {
                File.Copy(source, target, overwrite: true);
                copied++;
            }
            catch (IOException)
            {
                deferred.Add(relative);
            }
            catch (UnauthorizedAccessException)
            {
                deferred.Add(relative);
            }
        }

        if (deferred.Count > 0)
        {
            WritePending(targetDirectory, sourceDirectory, build, deferred);
        }
        else
        {
            TryDeleteFile(Path.Combine(targetDirectory, PendingFileName));
        }

        var message = deferred.Count > 0
            ? $"已部署 {copied} 个文件（{skipped} 个未变）；{deferred.Count} 个正在使用的文件将在重启后替换。"
            : $"已部署 {copied} 个文件（{skipped} 个未变），无需重启。";
        return new Result(true, deferred.Count > 0, message, copied, skipped, deferred.Count, build);
    }

    /// <summary>
    /// 启动时调用：若存在待重启替换清单，用源目录文件完成替换。
    /// 必须在加载被替换的程序集之前调用（见 Program.Main）。
    /// </summary>
    public bool ApplyPendingOnStartup(string? targetDirectory = null)
    {
        targetDirectory ??= AppContext.BaseDirectory;
        var pendingPath = Path.Combine(targetDirectory, PendingFileName);
        if (!File.Exists(pendingPath)) return false;

        PendingRestart? pending;
        try
        {
            pending = JsonSerializer.Deserialize<PendingRestart>(File.ReadAllText(pendingPath));
        }
        catch (Exception)
        {
            TryDeleteFile(pendingPath);
            return false;
        }

        if (pending is null || !Directory.Exists(pending.SourceDirectory))
        {
            TryDeleteFile(pendingPath);
            return false;
        }

        foreach (var relative in pending.Files)
        {
            var source = Path.Combine(pending.SourceDirectory, relative);
            var target = Path.Combine(targetDirectory, relative);
            if (!File.Exists(source)) continue;
            try
            {
                var dir = Path.GetDirectoryName(target);
                if (dir is not null) Directory.CreateDirectory(dir);
                File.Copy(source, target, overwrite: true);
            }
            catch (Exception)
            {
                // 仍然失败：保留清单，下次启动再试，不静默丢弃。
                return false;
            }
        }

        TryDeleteFile(pendingPath);
        return true;
    }

    private static bool IsNewer(BuildInfo candidate, BuildInfo current) =>
        candidate.BuiltAtUtc > current.BuiltAtUtc;

    /// <summary>
    /// 判断两个文件是否可视为相同。部署是本地行为，信任源目录的构建产物：
    /// 先比大小，再比修改时间（构建产物刚写出，时间必然更新），避免每次部署都对
    /// 上百个程序集做全量 SHA-256——那是发布更新的校验手段，不是部署的。
    /// </summary>
    private static bool SameContent(string left, string right)
    {
        try
        {
            var leftInfo = new FileInfo(left);
            var rightInfo = new FileInfo(right);
            if (leftInfo.Length != rightInfo.Length) return false;
            return leftInfo.LastWriteTimeUtc == rightInfo.LastWriteTimeUtc;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateLocalBuildDirectories()
    {
        var overrideDir = Environment.GetEnvironmentVariable("FUFUCODE_UPDATE_SOURCE_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir) && Directory.Exists(overrideDir))
            yield return Path.GetFullPath(overrideDir);

        var userLocal = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fufucode", "updates", "local");
        if (Directory.Exists(userLocal)) yield return userLocal;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 6; depth++, directory = directory.Parent)
        {
            foreach (var relative in new[]
            {
                Path.Combine("out", "app"),
                Path.Combine("out", "build", "app"),
                Path.Combine("out", "build-native", "app"),
            })
            {
                var candidate = Path.Combine(directory.FullName, relative);
                if (Directory.Exists(candidate)) yield return candidate;
            }
        }
    }

    private static void WritePending(string targetDirectory, string sourceDirectory, BuildInfo build,
        IEnumerable<string> files)
    {
        var pending = new PendingRestart(sourceDirectory, build.BuildId, DateTimeOffset.UtcNow, files.ToArray());
        var path = Path.Combine(targetDirectory, PendingFileName);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(pending,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record PendingRestart(
        string SourceDirectory,
        string BuildId,
        DateTimeOffset CreatedAtUtc,
        IReadOnlyList<string> Files);
}
