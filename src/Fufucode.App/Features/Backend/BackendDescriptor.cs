using System;
using System.Collections.Generic;
using System.Linq;
using Fufucode.Core.Backend;

namespace Fufucode.App.Features.Backend;

/// <summary>
/// 会话后端徽标：显示名称、图标标识与专属徽标颜色。
/// 颜色为十六进制字符串，界面不猜测、不按名称匹配。
/// </summary>
public sealed record BackendBadge(string BackendId, string DisplayName, string IconKey, string BadgeColor)
{
    /// <summary>用于绘制的颜色值（#RRGGBB）。</summary>
    public string Color => BadgeColor;
}

/// <summary>
/// 后端徽标目录。颜色约定：FUMO=紫色（默认内核）、Pi=绿色、OMP=橙色、Codex=蓝色、DSH=青色、Claude=暖橙红。
/// </summary>
public static class BackendBadgeCatalog
{
    public static BackendBadge For(EngineId engine) => engine switch
    {
        EngineId.Pi => new BackendBadge("pi", "Pi", "pi", "#2EA043"),
        EngineId.Omp => new BackendBadge("omp", "OMP", "omp", "#D97706"),
        EngineId.Codex => new BackendBadge("codex", "Codex", "codex", "#2563EB"),
        EngineId.Dsh => new BackendBadge("dsh", "DSH", "dsh", "#0891B2"),
        EngineId.Claude => new BackendBadge("claude", "Claude", "claude", "#C15F3C"),
        EngineId.NativeCSharp => new BackendBadge("fumo", "FUMO", "code", "#8B5CF6"),
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
