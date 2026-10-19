using System;
using System.Collections.Generic;

namespace Fufucode.App.Infrastructure.Browser;

/// <summary>
/// 浏览器站点规则的纯判定层（不依赖 WebView2 与 UI，便于无头断言）。
/// 规则来源是 <see cref="BrowserSettings"/>：站点白名单按主机名匹配，默认权限是全网默认值。
/// 权限取值与设置页一致：allow / approval / block。
/// </summary>
public sealed class BrowserSiteRules
{
    private readonly BrowserSettings _settings;

    public BrowserSiteRules(BrowserSettings? settings) => _settings = (settings ?? BrowserSettings.Default).Normalize();

    public BrowserSettings Settings => _settings;

    /// <summary>本次运行内用户选择“允许/拒绝”的站点权限；key = host + 权限种类。</summary>
    private readonly Dictionary<string, bool> _permissionDecisions = new(StringComparer.OrdinalIgnoreCase);

    public void RememberPermission(string? host, string kind, bool allow)
    {
        if (NormalizeHost(host) is not { } normalized) return;
        lock (_permissionDecisions) _permissionDecisions[normalized + "\u001F" + kind] = allow;
    }

    /// <summary>查找本进程内已有的站点权限决定；没有返回 null（交给用户）。</summary>
    public bool? PermissionDecision(string? host, string kind)
    {
        if (NormalizeHost(host) is not { } normalized) return false;
        lock (_permissionDecisions)
            return _permissionDecisions.TryGetValue(normalized + "\u001F" + kind, out var allow) ? allow : null;
    }

    /// <summary>
    /// 目标主机的实际权限：白名单命中即用其权限，否则用全网默认。
    /// 无效/非 web 地址一律 block——不给脚本协议、file: 等非网页来源任何放行。
    /// </summary>
    public string PermissionFor(string? address, string? fallbackHost = null)
    {
        var host = HostOf(address) ?? NormalizeHost(fallbackHost);
        if (host is null) return "block";
        foreach (var site in _settings.AllowedSites)
        {
            if (string.Equals(site.Host, host, StringComparison.OrdinalIgnoreCase))
                return site.Permission;
        }
        return _settings.DefaultBrowsePermission;
    }

    /// <summary>目标地址是否允许导航：allow 直接放行，approval 由调用方请求用户，block 拒绝。</summary>
    public bool CanNavigate(string? address) => PermissionFor(address) != "block";

    /// <summary>下载来源是否允许：规则同上，但用下载默认权限。</summary>
    public string DownloadPermissionFor(string? address)
    {
        var host = HostOf(address);
        if (host is null) return "allow";   // 本地文件触发的下载没有网页来源，不受站点规则限制
        foreach (var site in _settings.AllowedSites)
        {
            if (string.Equals(site.Host, host, StringComparison.OrdinalIgnoreCase)) return site.Permission;
        }
        return _settings.DefaultDownloadPermission;
    }

    /// <summary>UI 跳转（页面里的链接）是否改由系统默认浏览器打开：WebUrlOpenTarget = system。</summary>
    public bool OpensWebExternally => string.Equals(_settings.WebUrlOpenTarget, "system", StringComparison.OrdinalIgnoreCase);

    /// <summary>本地文件/本地服务跳转是否改由系统默认浏览器打开。</summary>
    public bool OpensLocalExternally => string.Equals(_settings.LocalUrlOpenTarget, "system", StringComparison.OrdinalIgnoreCase);

    /// <summary>去掉协议与端口后的主机名；非 http/https 地址返回 null。</summary>
    public static string? HostOf(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? NormalizeHost(uri.Host)
            : null;

    internal static string? NormalizeHost(string? raw)
    {
        var host = (raw ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        return host.Length is > 0 and <= 253 ? host : null;
    }
}
