using System;
using System.Collections.Generic;

namespace Fufucode.App.Infrastructure;

/// <summary>
/// 内置浏览器的权威持久化模型（BRW-1）。与 <see cref="WorkbenchAppearance"/> 里的旧
/// 浏览器字段保持可迁移但不再作为真源：本模型原子落盘到 <c>&lt;dataRoot&gt;/browser-settings.json</c>。
/// 所有枚举型取值用稳定字符串表示，写入前经 <see cref="Normalize"/> 收敛到白名单，
/// 未知/损坏值回退到默认值而不是静默写入非法状态。
/// </summary>
public sealed record BrowserSettings
{
    public const int CurrentVersion = 1;

    // ── 常规 ──
    /// <summary>让智能体控制内置浏览器的主开关；关闭时浏览器仍可手动使用。</summary>
    public bool BrowserUseEnabled { get; init; } = true;

    public string WebUrlOpenTarget { get; init; } = "builtin";
    public string LocalUrlOpenTarget { get; init; } = "builtin";
    public bool ShowFullUrl { get; init; } = true;
    public string ScreenshotMode { get; init; } = "ask";
    public string DefaultDownloadDirectory { get; init; } = "";

    // ── 下载 ──
    public bool PromptDownloadLocation { get; init; }
    public bool DownloadHistoryEnabled { get; init; } = true;

    // ── 权限与智能体控制 ──
    public string DefaultBrowsePermission { get; init; } = "approval";
    public string DefaultDownloadPermission { get; init; } = "approval";
    public bool EnableSiteTools { get; init; }
    public bool EnableFullCdpAccess { get; init; }
    public IReadOnlyList<BrowserSitePermission> AllowedSites { get; init; } = Array.Empty<BrowserSitePermission>();

    public static readonly string[] WebUrlTargets = { "builtin", "system" };
    public static readonly string[] ScreenshotModes = { "always", "ask", "never" };
    public static readonly string[] PermissionModes = { "approval", "allow", "block" };

    public static BrowserSettings Default => new();

    public BrowserSettings Normalize() => this with
    {
        WebUrlOpenTarget = Pick(WebUrlOpenTarget, "builtin", WebUrlTargets),
        LocalUrlOpenTarget = Pick(LocalUrlOpenTarget, "builtin", WebUrlTargets),
        ScreenshotMode = Pick(ScreenshotMode, "ask", ScreenshotModes),
        DefaultBrowsePermission = Pick(DefaultBrowsePermission, "approval", PermissionModes),
        DefaultDownloadPermission = Pick(DefaultDownloadPermission, "approval", PermissionModes),
        DefaultDownloadDirectory = (DefaultDownloadDirectory ?? string.Empty).Trim(),
        AllowedSites = NormalizeSites(AllowedSites),
    };

    /// <summary>从旧 WorkbenchAppearance 迁移浏览器偏好（首次创建 browser-settings.json 时）。</summary>
    public static BrowserSettings FromLegacy(WorkbenchAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return new BrowserSettings
        {
            BrowserUseEnabled = appearance.BrowserUseEnabled,
            WebUrlOpenTarget = appearance.WebUrlOpenTarget,
            LocalUrlOpenTarget = appearance.LocalUrlOpenTarget,
            ShowFullUrl = appearance.ShowFullUrl,
            ScreenshotMode = appearance.ScreenshotMode,
            DefaultBrowsePermission = appearance.DefaultBrowsePermission,
            DefaultDownloadPermission = appearance.DefaultDownloadPermission,
            EnableSiteTools = appearance.EnableSiteTools,
            EnableFullCdpAccess = appearance.EnableFullCdpAccess,
            PromptDownloadLocation = appearance.PromptDownloadLocation,
        }.Normalize();
    }

    private static string Pick(string? value, string fallback, string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        foreach (var candidate in allowed)
            if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase)) return candidate;
        return fallback;
    }

    private static IReadOnlyList<BrowserSitePermission> NormalizeSites(IReadOnlyList<BrowserSitePermission>? sites)
    {
        if (sites is null || sites.Count == 0) return Array.Empty<BrowserSitePermission>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<BrowserSitePermission>();
        foreach (var site in sites)
        {
            if (site is null || !site.IsUsable) continue;
            if (!seen.Add(site.Host)) continue;
            result.Add(site.Normalize());
        }
        return result;
    }
}

/// <summary>单个站点的智能体授权例外；与全局默认权限分开记录。</summary>
public sealed record BrowserSitePermission(string Host, string Permission = "approval")
{
    public const string ListSeparator = "\u001F";

    /// <summary>单个站点行是否可用：主机名非空且不含通配/协议（只存主机名，不存完整 URL）。</summary>
    public bool IsUsable
    {
        get
        {
            var host = Host?.Trim() ?? string.Empty;
            if (host.Length == 0 || host.Length > 253) return false;
            if (host.Contains('/', StringComparison.Ordinal) || host.Contains('\\', StringComparison.Ordinal)) return false;
            if (host.Contains(' ', StringComparison.Ordinal)) return false;
            return true;
        }
    }

    public BrowserSitePermission Normalize() => new(
        (Host ?? string.Empty).Trim().ToLowerInvariant(),
        BrowserSettings.PermissionModes is { Length: 3 } modes && Array.IndexOf(modes, Permission) >= 0 ? Permission : "approval");

    /// <summary>导出为单行文本；<see cref="ListSeparator"/> 由 UI 文本框用逗号/分号/换行分隔后重新组装。</summary>
    public string ToLine() => Normalize().Host + ListSeparator + Normalize().Permission;
}
