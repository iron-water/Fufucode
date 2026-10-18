using System;
using System.Collections.Generic;

namespace Fufucode.Core.Backend;

/// <summary>
/// 会话绑定的后端身份。会话切换后端时只改 <see cref="Engine"/>，
/// 时间线与消息历史保持在 <see cref="SessionBackendBinding"/> 自身。
/// </summary>
public sealed record SessionBackendBinding
{
    public SessionBackendBinding(string sessionId, EngineId engine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!Enum.IsDefined(engine)) throw new ArgumentOutOfRangeException(nameof(engine));
        SessionId = sessionId;
        Engine = engine;
    }

    /// <summary>会话稳定标识。</summary>
    public string SessionId { get; init; }

    /// <summary>当前绑定的执行内核。</summary>
    public EngineId Engine { get; private set; }

    /// <summary>持久化标识（pi / omp / codex / dsh）。</summary>
    public string BackendId => EngineIdMap.ToStorageId(Engine);

    /// <summary>会话时间线（切换后端时保持）。</summary>
    public IReadOnlyList<SessionTimelineEntry> Timeline => _timeline;

    private readonly List<SessionTimelineEntry> _timeline = new();

    /// <summary>追加一条时间线记录；历史不受后端切换影响。</summary>
    public void Append(SessionTimelineEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _timeline.Add(entry);
    }

    /// <summary>
    /// 平滑切换后端：先按目标内核解析（未注册则拒绝，保持原绑定不变），
    /// 再重绑执行引擎，已有时间线原样保留。
    /// </summary>
    public IBackendEngine Rebind(EngineId target, IBackendRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var engine = registry.Resolve(target);
        Engine = target;
        return engine;
    }
}

/// <summary>会话时间线中的一条记录（用户或助手消息的文本快照）。</summary>
public sealed record SessionTimelineEntry(SessionTimelineRole Role, string Text);

/// <summary>时间线条目角色。</summary>
public enum SessionTimelineRole
{
    User,
    Assistant,
    System,
}
