using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.App.Features.SettingsExperience;

/// <summary>一级分类分组：标题 + 该组内的条目。</summary>
public sealed record SettingsNavGroup(string Tier, string Title, IReadOnlyList<SettingsNavDefinition> Items);

/// <summary>
/// 把目录与升降级偏好合成为可渲染的侧栏结构，并实现跨层级搜索过滤。
/// 升级的小功能并入「大功能」组，降级的大功能并入「其他」组；空组不输出。
/// </summary>
public static class SettingsNavLayout
{
    public static IReadOnlyList<SettingsNavGroup> Build(SettingsNavPreference? preference, string? search = null)
    {
        preference ??= SettingsNavPreference.Empty;
        var term = search?.Trim() ?? string.Empty;
        var groups = new List<SettingsNavGroup>();
        foreach (var tier in SettingsNavCatalog.TierOrder)
        {
            var items = SettingsNavCatalog.All
                .Where(item => preference.EffectiveTier(item.Id) == tier)
                .Where(item => Matches(item, term))
                .ToArray();
            if (items.Length == 0) continue;
            groups.Add(new SettingsNavGroup(TierId(tier), SettingsNavCatalog.TierTitle(tier), items));
        }
        return groups;
    }

    /// <summary>跨层级搜索：标题或搜索词命中即为匹配。</summary>
    public static bool Matches(SettingsNavDefinition item, string? term)
    {
        var query = term?.Trim() ?? string.Empty;
        if (query.Length == 0) return true;
        return item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.SearchTerms.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>一级分类的稳定标识（用于 UI 绑定与测试断言）。</summary>
    public static string TierId(SettingsNavTier tier) => tier switch
    {
        SettingsNavTier.Basic => "basic",
        SettingsNavTier.Features => "features",
        _ => "other",
    };
}
