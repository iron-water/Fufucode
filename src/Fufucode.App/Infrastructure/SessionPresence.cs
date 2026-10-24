using System;
using System.Collections.Concurrent;
using System.IO;
using Fufucode.Core;

namespace Fufucode.App.Infrastructure;

/// <summary>
/// 会话是否已经“真正成立”：至少发送过一条用户消息。
/// 新建但还没发第一条 prompt 的会话不进入会话列表，行为对齐 Codex：
/// 只有发了第一条消息，会话才在项目里出现。
/// </summary>
public static class SessionPresence
{
    private static readonly ConcurrentDictionary<string, (long Length, DateTime WriteUtc, bool HasUser)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool HasUserMessage(DesktopMetadataStore store, SessionRecord session) =>
        SessionPresentation.Load(store, session.Id).HasUserMessage || HasUserMessageInFile(session);

    public static bool IsEstablished(DesktopMetadataStore store, SessionRecord session) =>
        HasUserMessage(store, session) || !string.IsNullOrWhiteSpace(session.Name);

    /// <summary>把会话标记为已成立（发送第一条消息时调用，无需等文件落盘）。</summary>
    public static void MarkEstablished(DesktopMetadataStore store, string sessionId)
    {
        var presentation = SessionPresentation.Load(store, sessionId);
        if (presentation.HasUserMessage) return;
        SessionPresentation.Save(store, sessionId, presentation with { HasUserMessage = true });
    }

    private static bool HasUserMessageInFile(SessionRecord session)
    {
        var path = session.FilePath;
        if (path is not { Length: > 0 } || !File.Exists(path)) return false;
        try
        {
            var info = new FileInfo(path);
            if (Cache.TryGetValue(path, out var cached) &&
                cached.Length == info.Length && cached.WriteUtc == info.LastWriteTimeUtc)
                return cached.HasUser;
            var text = File.ReadAllText(path);
            var hasUser = path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                ? text.Contains("\"role\":\"user\"", StringComparison.Ordinal) ||
                  text.Contains("\"role\": \"user\"", StringComparison.Ordinal)
                : text.Contains("\"Role\": 1", StringComparison.Ordinal) ||
                  text.Contains("\"Role\":1", StringComparison.Ordinal);
            Cache[path] = (info.Length, info.LastWriteTimeUtc, hasUser);
            return hasUser;
        }
        catch (Exception)
        {
            // 读不到时按“已成立”处理，宁可多显示一条，也不静默吞掉用户会话。
            return true;
        }
    }
}
