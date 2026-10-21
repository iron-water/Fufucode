using System;

namespace Fufucode.App.Features.SettingsExperience;

/// <summary>
/// 设置目录的搜索语义。侧栏分组由 <see cref="ViewModels.SettingsPageViewModel"/> 按真实运行模型
/// （基础/大功能平铺 + 「其他」收纳桶）构建，这里不再保留第二套分组实现。
/// </summary>
public static class SettingsNavLayout
{
    /// <summary>跨层级搜索：标题或搜索词命中即为匹配。</summary>
    public static bool Matches(SettingsNavDefinition item, string? term)
    {
        var query = term?.Trim() ?? string.Empty;
        if (query.Length == 0) return true;
        return item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.SearchTerms.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
