using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Fufucode.App.Infrastructure;

/// <summary>
/// 轮次收藏（左侧刻度固定项）。按会话 id 存消息 id，放在应用数据目录的小 JSON 里。
///
/// 不写进会话存档：收藏是界面偏好，不该改变历史消息本身；
/// 会话被删除后条目会自然失效，读取时按消息 id 匹配，不会误标到别的轮次。
/// </summary>
public static class PinnedTurnsStore
{
    private static readonly object Gate = new();
    private static Dictionary<string, List<string>>? _cache;

    private static string FilePath => Path.Combine(
        Fufucode.Core.AppDataLayout.DefaultRoot(), "pinned-turns.json");

    /// <summary>读取某会话的固定轮次 id；文件损坏或不存在时返回空集合，不影响对话。</summary>
    public static IReadOnlySet<string> Load(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return new HashSet<string>(StringComparer.Ordinal);
        lock (Gate)
        {
            var all = ReadAll();
            return all.TryGetValue(sessionId, out var ids)
                ? new HashSet<string>(ids, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public static void Set(string sessionId, string messageId, bool pinned)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(messageId)) return;
        lock (Gate)
        {
            var all = ReadAll();
            var ids = all.TryGetValue(sessionId, out var existing)
                ? new List<string>(existing)
                : new List<string>();

            if (pinned)
            {
                if (!ids.Contains(messageId, StringComparer.Ordinal)) ids.Add(messageId);
            }
            else
            {
                ids.RemoveAll(id => string.Equals(id, messageId, StringComparison.Ordinal));
            }

            if (ids.Count == 0) all.Remove(sessionId);
            else all[sessionId] = ids;

            WriteAll(all);
        }
    }

    private static Dictionary<string, List<string>> ReadAll()
    {
        if (_cache is not null) return _cache;
        try
        {
            if (File.Exists(FilePath))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(FilePath));
                if (parsed is not null)
                {
                    _cache = parsed;
                    return _cache;
                }
            }
        }
        catch (Exception)
        {
            // 损坏的收藏文件不应阻断对话；下次写入会重建。
        }

        _cache = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        return _cache;
    }

    private static void WriteAll(Dictionary<string, List<string>> all)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all));
        }
        catch (Exception)
        {
            // 写盘失败只影响持久化；本次会话内的收藏仍然有效。
        }
    }
}
