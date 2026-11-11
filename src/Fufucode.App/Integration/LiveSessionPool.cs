using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.App.Streaming;
using Fufucode.Core;

namespace Fufucode.App.Integration;

/// <summary>会话是唯一事实拥有者，窗口只持引用；切换视图不释放后台任务。</summary>
internal static class LiveSessionPool
{
    private sealed class Entry(PersistentSessionManager manager, PersistentSession session)
    {
        internal PersistentSessionManager Manager { get; } = manager;
        internal PersistentSession Session { get; } = session;
        internal int References { get; set; } = 1;
        internal object Sync { get; } = new();
        internal MessageRebuilder Timeline { get; } = new();
        internal long Sequence { get; set; }
        internal EventHandler<SessionEvent>? Changed { get; set; }
    }

    internal sealed record TranscriptSnapshot(IReadOnlyList<ChatMessage> Messages, long Sequence);
    private sealed record Marker(long Sequence);
    private static readonly ConditionalWeakTable<SessionEvent, Marker> Sequences = new();
    private static long _sequence;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly ConcurrentDictionary<string, Entry> Entries = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static string Key(PersistentSessionManager manager, string id) => manager.Layout.Root + "|" + id;

    internal static long SequenceOf(SessionEvent value) => Sequences.TryGetValue(value, out var marker) ? marker.Sequence : 0;

    internal static async Task<PersistentSession> ResumeAsync(PersistentSessionManager manager, string id,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var key = Key(manager, id);
            if (Entries.TryGetValue(key, out var entry))
            {
                entry.References++;
                return entry.Session;
            }
            // Resume 的前半段包含同步 JSON/SQLite/历史解析；不能借已完成的 await 回到 UI 线程。
            var session = await Task.Run(() => manager.ResumeAsync(id, cancellationToken), cancellationToken).ConfigureAwait(false);
            Register(key, manager, session);
            return session;
        }
        finally { Gate.Release(); }
    }

    internal static async Task<PersistentSession> TrackAsync(PersistentSessionManager manager, PersistentSession session)
    {
        await Gate.WaitAsync();
        try
        {
            var key = Key(manager, session.Session.Id);
            if (Entries.TryGetValue(key, out var entry))
            {
                if (!ReferenceEquals(entry.Session, session)) throw new InvalidOperationException("会话已有其他事实拥有者。");
                entry.References++;
            }
            else Register(key, manager, session);
            return session;
        }
        finally { Gate.Release(); }
    }

    private static void Register(string key, PersistentSessionManager manager, PersistentSession session)
    {
        var entry = new Entry(manager, session);
        foreach (var message in MessageBlockMapper.MapTranscript(session.History.CurrentBranchMessages, session.Chat.CurrentModel))
            entry.Timeline.RestoreMessage(message);
        entry.Timeline.Flush();
        entry.Changed = (_, value) =>
        {
            lock (entry.Sync)
            {
                entry.Sequence = Interlocked.Increment(ref _sequence);
                Sequences.GetValue(value, _ => new Marker(entry.Sequence));
                entry.Timeline.Apply(value);
            }
            // 不按 token 重建侧栏；所有窗口都在状态边界读取同一个真实会话。
            if (value is SessionEvent.ActivityChanged or SessionEvent.TurnSettled or SessionEvent.ConnectionChanged)
                SessionChangeBus.Publish(null);
        };
        Entries[key] = entry;
        session.Chat.Event += entry.Changed;
    }

    internal static PersistentSessionManager ManagerFor(PersistentSessionManager manager, string id) =>
        Entries.TryGetValue(Key(manager, id), out var entry) ? entry.Manager : manager;

    internal static PersistentSession? Find(PersistentSessionManager manager, string id) =>
        Entries.TryGetValue(Key(manager, id), out var entry) ? entry.Session : null;

    internal static TranscriptSnapshot Snapshot(PersistentSessionManager manager, PersistentSession session)
    {
        if (!Entries.TryGetValue(Key(manager, session.Session.Id), out var entry))
            return new(MessageBlockMapper.MapTranscript(session.History.CurrentBranchMessages, session.Chat.CurrentModel), 0);
        lock (entry.Sync)
        {
            // Flush 清掉批次标记，重建器仍保留完整状态；快照包含当前流式消息。
            entry.Timeline.Flush();
            return new(entry.Timeline.Timeline.Where(item => item.Message is not null)
                .Select(item => item.Message!).ToArray(), entry.Sequence);
        }
    }

    internal static async Task RequireExclusiveAsync(PersistentSessionManager manager, PersistentSession session)
    {
        await Gate.WaitAsync();
        try
        {
            if (Entries.TryGetValue(Key(manager, session.Session.Id), out var entry) && entry.References != 1)
                throw new InvalidOperationException("此后端操作会替换会话连接；请先关闭其他窗口中的此会话视图。");
        }
        finally { Gate.Release(); }
    }

    internal static async Task ReleaseAsync(PersistentSessionManager manager, PersistentSession session, bool exclusive = false)
    {
        await Gate.WaitAsync();
        try
        {
            var key = Key(manager, session.Session.Id);
            if (!Entries.TryGetValue(key, out var entry) || !ReferenceEquals(entry.Session, session)) return;
            if (exclusive && entry.References != 1)
                throw new InvalidOperationException("此会话仍在其他窗口中使用；请先关闭其他视图，再重启后端或应用扩展。");
            if (--entry.References != 0) return;
            Entries.TryRemove(key, out _);
            session.Chat.Event -= entry.Changed;
            await session.DisposeAsync();
        }
        finally { Gate.Release(); }
    }
}
