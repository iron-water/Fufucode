using System;
using System.Collections.Generic;
using System.IO;

namespace Fufucode.App.Infrastructure.Browser;

/// <summary>
/// 浏览数据（缓存、cookie、历史）的唯一落点：应用数据根目录下的 <c>webview</c> 子目录。
/// 不写入项目目录、不写入安装目录；自测模式重定向到隔离临时根，避免触碰真实 %LocalAppData%。
/// </summary>
internal static class BrowserUserDataLayout
{
    internal const string AppFolderName = "Fufucode";
    internal const string FolderName = "webview";

    /// <summary>
    /// 解析 UserDataFolder。<paramref name="overrideRoot"/> 非空（自测隔离）时在其下建子目录，
    /// 否则使用 <c>%LocalAppData%\Fufucode\webview</c>。
    /// </summary>
    public static string Resolve(string? overrideRoot)
    {
        var root = string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName)
            : Path.GetFullPath(overrideRoot);
        var folder = Path.GetFullPath(Path.Combine(root, FolderName));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!folder.StartsWith(prefix, comparison))
            throw new InvalidOperationException("浏览器数据目录必须位于应用数据根目录之内。");
        return folder;
    }

    /// <summary>创建目录；失败时返回可读原因而不是抛出，供界面显示。</summary>
    public static string? TryEnsure(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return error.Message;
        }
    }

    /// <summary>清除应用内 WebView2 缓存与网站数据（保留数据目录框架）。</summary>
    public static void ClearBrowsingData(string? overrideRoot = null)
    {
        ClearBrowsingData(overrideRoot, BrowserDataScope.All);
    }

    /// <summary>按范围清除浏览数据；每个范围真实删除目标目录，失败项逐条如实报告。</summary>
    public static BrowserDataClearReport ClearBrowsingData(string? overrideRoot, BrowserDataScope scope)
    {
        var folder = Resolve(overrideRoot);
        var removed = new List<string>();
        var failures = new List<string>();
        if (!Directory.Exists(folder))
            return new BrowserDataClearReport(true, removed, failures, folder);

        var ebwebview = Path.Combine(folder, "EBWebView");
        if (!Directory.Exists(ebwebview))
            return new BrowserDataClearReport(true, removed, failures, folder);

        // WebView2 的缓存/站点数据都落在 EBWebView 下的具名子目录；Crashpad 与运行时元数据保留，
        // 因为删除它们只会破坏崩溃报告与运行时版本握手，不影响用户的缓存/Cookie/站点数据。
        var targets = scope switch
        {
            BrowserDataScope.Cache => new[] { "Default\\Cache", "Default\\Code Cache", "Default\\GPUCache", "ShaderCache", "GrShaderCache", "Default\\Service Worker\\CacheStorage" },
            BrowserDataScope.Cookies => new[] { "Default\\Network\\Cookies", "Default\\Network\\Cookies-journal" },
            BrowserDataScope.SiteData => new[] { "Default\\Local Storage", "Default\\Session Storage", "Default\\IndexedDB", "Default\\WebStorage", "Default\\databases", "Default\\Service Worker" },
            _ => Array.Empty<string>(),
        };

        if (scope == BrowserDataScope.All)
        {
            foreach (var dir in SafeEnumerateDirectories(ebwebview, failures))
            {
                if (Path.GetFileName(dir).Equals("Crashpad", StringComparison.OrdinalIgnoreCase)) continue;
                TryDelete(dir, ebwebview, removed, failures);
            }
            return new BrowserDataClearReport(failures.Count == 0, removed, failures, folder);
        }

        foreach (var relative in targets) TryDelete(Path.Combine(ebwebview, relative), ebwebview, removed, failures);
        return new BrowserDataClearReport(failures.Count == 0, removed, failures, folder);
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path, List<string> failures)
    {
        try { return Directory.GetDirectories(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            failures.Add(path + "：" + error.Message);
            return Array.Empty<string>();
        }
    }

    private static void TryDelete(string target, string allowedRoot, List<string> removed, List<string> failures)
    {
        if (!File.Exists(target) && !Directory.Exists(target)) return;
        var full = Path.GetFullPath(target);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add(full + "：拒绝删除数据目录之外的路径。");
            return;
        }
        try
        {
            if (Directory.Exists(full)) Directory.Delete(full, true);
            else File.Delete(full);
            removed.Add(full);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // WebView 正在运行时锁定的文件会失败；如实报告而不是假装已清除。
            failures.Add(full + "：" + error.Message);
        }
    }
}

/// <summary>清除范围：缓存 / Cookie / 站点数据 / 全部。</summary>
public enum BrowserDataScope
{
    Cache,
    Cookies,
    SiteData,
    All,
}

/// <summary>一次清除的结果：真正删掉的路径、失败项与数据目录。</summary>
public sealed record BrowserDataClearReport(
    bool Success,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Failures,
    string DataFolder)
{
    public string Summary => Success
        ? $"已清除 {Removed.Count} 项浏览数据"
        : $"已清除 {Removed.Count} 项；{Failures.Count} 项失败（可能有页面正在使用）";
}
