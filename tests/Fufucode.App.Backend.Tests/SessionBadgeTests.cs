using System;
using Fufucode.App.Features.Backend;
using Fufucode.Core.Backend;

namespace Fufucode.App.Backend.Tests;

/// <summary>会话后端徽标映射：名称与图标标识（不按颜色区分内核，统一走三档底色）。</summary>
internal static class SessionBadgeTests
{
    public static void Run(Checks checks)
    {
        var pi = BackendBadgeCatalog.For(EngineId.Pi);
        var omp = BackendBadgeCatalog.For(EngineId.Omp);
        var codex = BackendBadgeCatalog.For(EngineId.Codex);
        var dsh = BackendBadgeCatalog.For(EngineId.Dsh);
        var claude = BackendBadgeCatalog.For(EngineId.Claude);
        var native = BackendBadgeCatalog.For(EngineId.NativeCSharp);

        checks.Equal("Pi", pi.DisplayName, "Pi 徽标显示名为 Pi");
        checks.Equal("OMP", omp.DisplayName, "OMP 徽标显示名为 OMP");
        checks.Equal("Codex", codex.DisplayName, "Codex 徽标显示名为 Codex");
        checks.Equal("DSH", dsh.DisplayName, "DSH 徽标显示名为 DSH");
        checks.Equal("Claude", claude.DisplayName, "Claude 徽标显示名为 Claude");
        checks.Equal("FUMO", native.DisplayName, "FUMO 徽标显示名为 FUMO");

        checks.Equal(6, BackendBadgeCatalog.All.Count, "徽标目录包含六个后端");

        foreach (var id in Enum.GetValues<EngineId>())
        {
            var badge = BackendBadgeCatalog.For(id);
            checks.Equal(EngineIdMap.ToStorageId(id), badge.BackendId, $"{id} 徽标标识与持久化标识一致");
            checks.Check(!string.IsNullOrWhiteSpace(badge.IconKey), $"{id} 徽标提供图标键");
        }

        checks.Equal("fumo", BackendBadgeCatalog.FromStorageId("fumo").BackendId,
            "按持久化标识查找 C# Native 徽标");
        checks.Equal("claude", BackendBadgeCatalog.FromStorageId("claude").BackendId,
            "按持久化标识查找 Claude 徽标");
        checks.Throws<ArgumentException>(() => BackendBadgeCatalog.FromStorageId("nope"),
            "未知持久化标识查找徽标抛出");
    }
}
