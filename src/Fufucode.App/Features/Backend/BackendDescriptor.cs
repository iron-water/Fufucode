using System;
using System.Collections.Generic;
using System.Linq;
using Fufucode.Core.Backend;

namespace Fufucode.App.Features.Backend;

/// <summary>
/// 会话后端徽标：显示名称与图标标识。
/// 不使用颜色区分内核——默认内核与插件内核一律走同一套三档底色。
/// </summary>
public sealed record BackendBadge(string BackendId, string DisplayName, string IconKey);

/// <summary>
/// 后端徽标目录。
/// </summary>
public static class BackendBadgeCatalog
{
    public static BackendBadge For(EngineId engine) => engine switch
    {
        EngineId.Pi => new BackendBadge("pi", "Pi", "pi"),
        EngineId.Omp => new BackendBadge("omp", "OMP", "omp"),
        EngineId.Codex => new BackendBadge("codex", "Codex", "codex"),
        EngineId.Dsh => new BackendBadge("dsh", "DSH", "dsh"),
        EngineId.Claude => new BackendBadge("claude", "Claude", "claude"),
        EngineId.NativeCSharp => new BackendBadge("fumo", "FUMO", "code"),
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown backend engine id."),
    };

    /// <summary>全部徽标（按引擎枚举顺序）。</summary>
    public static IReadOnlyList<BackendBadge> All =>
        Enum.GetValues<EngineId>().Select(For).ToArray();

    /// <summary>按持久化标识查找徽标；未知标识抛出而不回退。</summary>
    public static BackendBadge FromStorageId(string id) => For(EngineIdMap.Parse(id));
}

/// <summary>
/// 会话后端展示视图：把会话绑定与内核运行时合并成界面所需的一行数据。
/// </summary>
public sealed record SessionBackendView(
    string SessionId,
    EngineId Engine,
    BackendBadge Badge,
    bool IsAvailable,
    string? RuntimePath,
    string? Version)
{
    public string BackendId => Badge.BackendId;

    public static SessionBackendView For(SessionBackendBinding binding, IBackendRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(registry);
        var engine = registry.Resolve(binding.Engine);
        var runtime = engine.Runtime;
        return new SessionBackendView(binding.SessionId, binding.Engine, BackendBadgeCatalog.For(binding.Engine),
            runtime.IsAvailable, runtime.RuntimePath, runtime.Version);
    }
}
