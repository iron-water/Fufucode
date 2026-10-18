using System;
using Fufucode.App.Features.Backend;
using Fufucode.Core.Backend;

namespace Fufucode.App.Backend.Tests;

/// <summary>会话后端徽标映射：名称、图标与专属颜色。</summary>
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

        checks.Equal("#2EA043", pi.BadgeColor, "Pi 徽标为绿色");
        checks.Equal("#D97706", omp.BadgeColor, "OMP 徽标为橙色");
        checks.Equal("#2563EB", codex.BadgeColor, "Codex 徽标为蓝色");
        checks.Equal("#0891B2", dsh.BadgeColor, "DSH 徽标为青色");
        checks.Equal("#C15F3C", claude.BadgeColor, "Claude 徽标为暖橙红");
        checks.Equal("#8B5CF6", native.BadgeColor, "NativeCSharp 徽标为紫色");

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
            checks.Check(badge.BadgeColor.StartsWith('#') && badge.BadgeColor.Length == 7,
                $"{id} 徽标颜色为 #RRGGBB");
        }

        checks.Equal("#8B5CF6", BackendBadgeCatalog.FromStorageId("fumo").BadgeColor,
            "按持久化标识查找 C# Native 徽标");
        checks.Equal("claude", BackendBadgeCatalog.FromStorageId("claude").BackendId,
            "按持久化标识查找 Claude 徽标");
        checks.Throws<ArgumentException>(() => BackendBadgeCatalog.FromStorageId("nope"),
            "未知持久化标识查找徽标抛出");
    }
}
