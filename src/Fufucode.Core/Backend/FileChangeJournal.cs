using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Fufucode.Core.Backend;

/// <summary>
/// 文件改动备份账本：每次写入/编辑前保存该工具的上一版内容，供"撤销本轮变更"恢复。
///
/// 备份按工具调用 id 命名，存放在应用数据目录；刻意不做落库，
/// 避免把用户文件内容混进会话存档。备份失败绝不能阻断正常写入。
/// </summary>
public static class FileChangeJournal
{
    private sealed record Entry(string Path, bool Existed);

    /// <summary>备份保留时长：只服务最近几轮撤销，过期自动清理。</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(2);

    public static string Root { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Fufucode", "turn-backups");

    /// <summary>写入前记录原样内容。文件不存在时也记录一条，撤销时按"删除新文件"处理。</summary>
    public static void Capture(string toolCallId, string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(toolCallId) || string.IsNullOrWhiteSpace(absolutePath)) return;
        try
        {
            Directory.CreateDirectory(Root);
            var existed = File.Exists(absolutePath);
            var backup = BackupPath(toolCallId);
            if (existed) File.Copy(absolutePath, backup, overwrite: true);
            File.WriteAllText(MetaPath(toolCallId), JsonSerializer.Serialize(new Entry(absolutePath, existed)));
        }
        catch (Exception)
        {
            // 备份是尽力而为：磁盘满、权限不足都不该让 agent 的写入失败。
        }
    }

    /// <summary>
    /// 按传入顺序的反向撤销（后写入的先恢复）。返回实际恢复的文件数。
    /// 只有确实存在备份的工具调用才会被恢复，其余静默跳过。
    /// </summary>
    public static int Undo(IEnumerable<string> toolCallIds)
    {
        var restored = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in toolCallIds.Reverse())
        {
            if (!seen.Add(id)) continue;
            try
            {
                var metaPath = MetaPath(id);
                if (!File.Exists(metaPath)) continue;
                var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(metaPath));
                if (entry is null || string.IsNullOrWhiteSpace(entry.Path)) continue;

                if (entry.Existed)
                {
                    var backup = BackupPath(id);
                    if (!File.Exists(backup)) continue;
                    var directory = System.IO.Path.GetDirectoryName(entry.Path);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    File.Copy(backup, entry.Path, overwrite: true);
                }
                else if (File.Exists(entry.Path))
                {
                    // 本回合新建的文件：撤销后应回到"不存在"。
                    File.Delete(entry.Path);
                }

                restored++;
            }
            catch (Exception)
            {
                // 单个文件恢复失败不阻断其余文件。
            }
        }

        return restored;
    }

    /// <summary>读取某次工具调用保存的"改动前"内容；文件原本不存在时返回空串且 existed=false。</summary>
    public static bool TryReadBackup(string toolCallId, out string content, out bool existed)
    {
        content = string.Empty;
        existed = false;
        if (string.IsNullOrWhiteSpace(toolCallId)) return false;
        try
        {
            var metaPath = MetaPath(toolCallId);
            if (!File.Exists(metaPath)) return false;
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(metaPath));
            if (entry is null) return false;
            existed = entry.Existed;
            if (!existed) return true;
            var backup = BackupPath(toolCallId);
            if (!File.Exists(backup)) return false;
            content = File.ReadAllText(backup);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 取出某次改动的前后内容，供界面生成内联 diff：
    /// 前 = 改动前备份（新建文件为空串），后 = 当前磁盘内容。
    /// </summary>
    public static bool TryReadDiffInputs(string toolCallId, out string before, out string after)
    {
        before = string.Empty;
        after = string.Empty;
        if (string.IsNullOrWhiteSpace(toolCallId)) return false;
        try
        {
            var metaPath = MetaPath(toolCallId);
            if (!File.Exists(metaPath)) return false;
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(metaPath));
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path)) return false;

            if (entry.Existed)
            {
                var backup = BackupPath(toolCallId);
                if (!File.Exists(backup)) return false;
                before = File.ReadAllText(backup);
            }

            after = File.Exists(entry.Path) ? File.ReadAllText(entry.Path) : string.Empty;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>清理过期备份；在启动或空闲时调用一次即可。</summary>
    public static void PruneExpired()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            var cutoff = DateTime.UtcNow - Retention;
            foreach (var file in Directory.EnumerateFiles(Root))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    try { File.Delete(file); } catch (Exception) { }
                }
            }
        }
        catch (Exception)
        {
            // 清理失败不影响功能。
        }
    }

    private static string BackupPath(string toolCallId) => System.IO.Path.Combine(Root, toolCallId + ".bak");
    private static string MetaPath(string toolCallId) => System.IO.Path.Combine(Root, toolCallId + ".json");
}
