using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.App.Features.SettingsExperience;

/// <summary>「相关设置」卡片中的一条跳转。</summary>
public sealed record SettingsRelatedLink(string Id, string Title, string Reason);

/// <summary>
/// 相关设置关系图：每个设置页底部据此渲染「相关设置」卡片，点击平滑跳转到对应设置页。
/// 关系是双向的——若 A 列出 B，则 B 也列出 A，避免单向死胡同。
/// </summary>
public static class SettingsRelatedSettings
{
    private static readonly (string From, string To, string Reason)[] Edges =
    [
        ("appearance", "functions", "会话显示与外观同属阅读体验"),
        ("appearance", "modules", "工作台模块决定页面入口位置"),
        ("network", "models", "自定义端点决定可用模型"),
        ("network", "config", "端点与配置可整体导入导出"),
        ("models", "shortcuts", "切换模型可绑定快捷键"),
        ("storage", "about", "缓存与日志目录在关于页可打开"),
        ("browser", "network", "浏览器下载与代理走网络设置"),
        ("browser", "files", "下载文件在文件管理中查看"),
        ("terminal", "shortcuts", "终端开关可绑定快捷键"),
        ("files", "storage", "文件缓存占用在储存页清理"),
        ("automation", "browser", "自动开发可驱动浏览器能力"),
        ("automation", "terminal", "自动开发可驱动终端能力"),
        ("notifications", "functions", "通知渠道与会话消息显示相关"),
        ("usage", "models", "用量与所选模型和思考等级相关"),
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<SettingsRelatedLink>> Map = BuildMap();

    /// <summary>某设置页（分类 id 或二级页 id）的相关设置；未知 id 返回空。</summary>
    public static IReadOnlyList<SettingsRelatedLink> For(string? pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId)) return Array.Empty<SettingsRelatedLink>();
        var categoryId = CategoryOf(pageId);
        return Map.TryGetValue(categoryId, out var links) ? links : Array.Empty<SettingsRelatedLink>();
    }

    /// <summary>二级页 id 归属的一级分类（取点号前缀）。</summary>
    public static string CategoryOf(string pageId)
    {
        var separator = pageId.IndexOf('.', StringComparison.Ordinal);
        return separator < 0 ? pageId : pageId[..separator];
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<SettingsRelatedLink>> BuildMap()
    {
        var map = new Dictionary<string, List<SettingsRelatedLink>>(StringComparer.Ordinal);
        foreach (var (from, to, reason) in Edges)
        {
            Add(map, from, to, reason);
            Add(map, to, from, reason);
        }
        return map.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<SettingsRelatedLink>)pair.Value
                .GroupBy(link => link.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray(),
            StringComparer.Ordinal);
    }

    private static void Add(Dictionary<string, List<SettingsRelatedLink>> map, string from, string to, string reason)
    {
        var definition = SettingsNavCatalog.Find(to);
        if (definition is null) return;
        if (!map.TryGetValue(from, out var list))
        {
            list = new List<SettingsRelatedLink>();
            map[from] = list;
        }
        if (list.Any(link => string.Equals(link.Id, to, StringComparison.Ordinal))) return;
        list.Add(new SettingsRelatedLink(to, definition.Title, reason));
    }
}
