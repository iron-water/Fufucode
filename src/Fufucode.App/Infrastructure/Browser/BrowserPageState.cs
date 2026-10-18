using System.Collections.Generic;

namespace Fufucode.App.Infrastructure.Browser;

/// <summary>单个页签的可持久化状态。地址草稿与真实导航地址分开保存，切换/重开不回写别的页签。</summary>
internal sealed record BrowserTabState(
    string Id,
    string AddressDraft,
    string? NavigationAddress,
    string? FilePath,
    string Title);

/// <summary>
/// 版本化浏览器页面状态。
/// v1 是旧的单页状态（AddressDraft/NavigationAddress/FilePath）；v2 增加多页签集合与活动页签。
/// 未支持的版本必须诚实降级到空页，不得假装恢复成功。
/// </summary>
internal sealed record BrowserPageState(
    int Version = 1,
    string AddressDraft = "",
    string? NavigationAddress = null,
    string? FilePath = null,
    string? ActiveTabId = null,
    IReadOnlyList<BrowserTabState>? Tabs = null)
{
    internal const int CurrentVersion = 2;
}
