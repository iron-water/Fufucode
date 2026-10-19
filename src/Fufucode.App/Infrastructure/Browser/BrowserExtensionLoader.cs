using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Fufucode.App.Infrastructure.Browser;

/// <summary>
/// 内置浏览器的 WebView2 扩展加载器。扩展放在
/// <c>&lt;dataRoot&gt;/webview-extensions/&lt;name&gt;/manifest.json</c>，
/// 首次创建 controller 时加载一次；同一 user data folder 的多个页签共享 profile，不重复加载。
/// </summary>
internal static class BrowserExtensionLoader
{
    public const string FolderName = "webview-extensions";
    private static readonly object Gate = new();
    private static readonly HashSet<string> Loading = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>扩展根目录（与应用数据根同级，不写进 WebView profile 内部）。</summary>
    public static string ResolveDirectory(string userDataFolder)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(userDataFolder)) ?? Path.GetFullPath(userDataFolder);
        return Path.Combine(root, FolderName);
    }

    public static string EnsureDirectory(string userDataFolder)
    {
        var folder = ResolveDirectory(userDataFolder);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// 加载全部合法扩展。返回真实加载成功的扩展名；失败项不抛出到页面初始化路径，
    /// 由调用方把错误写进状态区。
    /// </summary>
    public static async Task<BrowserExtensionLoadResult> LoadAsync(string userDataFolder, CoreWebView2Profile profile)
    {
        var folder = ResolveDirectory(userDataFolder);
        if (!Directory.Exists(folder)) return BrowserExtensionLoadResult.Empty;

        lock (Gate)
        {
            if (!Loading.Add(Path.GetFullPath(folder))) return BrowserExtensionLoadResult.Empty;
        }

        var loaded = new List<string>();
        var failures = new List<string>();
        try
        {
            foreach (var directory in Directory.GetDirectories(folder))
            {
                if (!File.Exists(Path.Combine(directory, "manifest.json"))) continue;
                try
                {
                    await profile.AddBrowserExtensionAsync(directory).ConfigureAwait(false);
                    loaded.Add(Path.GetFileName(directory));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                    or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
                {
                    failures.Add(Path.GetFileName(directory) + "：" + error.Message);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            failures.Add("扩展目录不可读：" + error.Message);
        }
        return new BrowserExtensionLoadResult(loaded, failures);
    }
}

public sealed record BrowserExtensionLoadResult(IReadOnlyList<string> Loaded, IReadOnlyList<string> Failures)
{
    public static BrowserExtensionLoadResult Empty { get; } =
        new(Array.Empty<string>(), Array.Empty<string>());

    public bool IsEmpty => Loaded.Count == 0 && Failures.Count == 0;
}
