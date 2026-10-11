using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fufucode.Core;

namespace Fufucode.App.Features.Storage;

/// <summary>
/// 存储区域清单。路径全部来自 <see cref="AppDataLayout"/>（或调用方显式给出的同根路径），
/// 不拼接 UserProfile、不猜测目录；每个区域只覆盖自己的子树，互不重叠，因此求和等于数据根实际占用。
/// </summary>
public sealed class StorageCatalog
{
    private readonly List<string> _rootExcludedDirectoryNames;

    public StorageCatalog(AppDataLayout layout, string? browserDataRoot = null, string? libraryRoot = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        DataRoot = layout.Root;
        BrowserDataRoot = Path.GetFullPath(browserDataRoot ?? Path.Combine(layout.Root, "webview"));
        LibraryRoot = Path.GetFullPath(libraryRoot ?? Path.Combine(layout.Root, "library"));

        Areas = new List<StorageArea>
        {
            new("app-root", "应用根目录（配置与数据库）", layout.Root, StorageAreaKind.AppRoot,
                StorageAreaCleanupPolicy.Protected,
                "settings.json、ui.json、keybindings.json、desktop.db 等根下文件；删除会丢失偏好与索引，不参与清理"),
            new("runtime", "Pi 运行时", layout.RuntimeDirectory, StorageAreaKind.Runtime,
                StorageAreaCleanupPolicy.Protected,
                "应用管理的 Pi 与 Node 运行时；重建需要重新下载，不参与清理"),
            new("pi-agent", "Pi 配置与认证", layout.AgentDirectory, StorageAreaKind.AgentConfig,
                StorageAreaCleanupPolicy.Protected,
                "模型配置与凭据目录；与浏览器登录数据分开管理，不参与清理"),
            new("sessions", "会话数据", layout.SessionsDirectory, StorageAreaKind.Sessions,
                StorageAreaCleanupPolicy.Protected,
                "Pi 持久化的对话与分支；删除不可恢复，不参与清理"),
            new("logs", "诊断日志", layout.LogsDirectory, StorageAreaKind.Logs,
                StorageAreaCleanupPolicy.ExpiredLogs,
                "脱敏诊断记录；仅清理超过保留天数的日志文件"),
            new("exports", "用户导出", layout.ExportsDirectory, StorageAreaKind.Exports,
                StorageAreaCleanupPolicy.Protected,
                "用户主动导出的诊断包等资料；不参与清理"),
            new("webview", "浏览器数据", BrowserDataRoot, StorageAreaKind.BrowserData,
                StorageAreaCleanupPolicy.CacheAndSiteData,
                "WebView2 独立 profile：HTTP/着色器等可重建缓存默认清理；cookie、登录态与 Service Worker 离线存储需明确确认，清理后要重新登录或离线内容需重新下载"),
            new("library", "资料库", LibraryRoot, StorageAreaKind.Library,
                StorageAreaCleanupPolicy.Protected,
                "资料库数据库与副本；不参与清理"),
        };

        _rootExcludedDirectoryNames = Areas
            .Where(area => area.Kind != StorageAreaKind.AppRoot)
            .Select(area => Path.GetFileName(Path.TrimEndingDirectorySeparator(area.Path)))
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToList();
    }

    /// <summary>应用数据根目录（<c>%LocalAppData%\Fufucode</c> 或自测隔离根）。</summary>
    public string DataRoot { get; }

    /// <summary>浏览器 profile 根目录（数据根下的 <c>webview</c>）。</summary>
    public string BrowserDataRoot { get; }

    /// <summary>资料库根目录（数据根下的 <c>library</c>）。</summary>
    public string LibraryRoot { get; }

    /// <summary>参与计量与展示的存储区域（互不重叠，求和即数据根实际占用）。</summary>
    public IReadOnlyList<StorageArea> Areas { get; }

    /// <summary>根目录区域计量时要排除的子目录名（它们各自单独计量，避免重复计数）。</summary>
    internal IReadOnlyList<string> RootExcludedDirectoryNames => _rootExcludedDirectoryNames;

    /// <summary>日志区域中参与保留策略的文件模式。</summary>
    public static IReadOnlyList<string> LogFilePatterns { get; } = new[] { "*.jsonl", "*.log" };

    /// <summary>Chromium/WebView2 中可重建的纯运行时缓存目录名（默认清理）。</summary>
    public static IReadOnlyList<string> BrowserCacheDirectoryNames { get; } = new[]
    {
        "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache",
        "GrShaderCache", "ShaderCache", "GraphiteDawnCache",
    };

    /// <summary>
    /// 浏览器登录/站点数据名（默认不清理，需明确确认；清理后网站登录状态与离线可用性失效）。
    /// 含 Service Worker 离线存储（CacheStorage、Service Worker 目录），它们不是纯可重建缓存：
    /// 删除会丢失站点离线内容，因此归入站点数据而非默认缓存范围。
    /// </summary>
    public static IReadOnlyList<string> BrowserSiteDataNames { get; } = new[]
    {
        "Cookies", "Cookies-journal", "Login Data", "Login Data-journal",
        "History", "History-journal", "Web Data", "Web Data-journal",
        "Network", "Local Storage", "Session Storage", "IndexedDB",
        "Service Worker", "CacheStorage",
    };

    public StorageArea? FindArea(string id) => Areas.FirstOrDefault(area => area.Id == id);

    public StorageArea LogsArea => Areas.First(area => area.Kind == StorageAreaKind.Logs);

    public StorageArea BrowserArea => Areas.First(area => area.Kind == StorageAreaKind.BrowserData);
}
