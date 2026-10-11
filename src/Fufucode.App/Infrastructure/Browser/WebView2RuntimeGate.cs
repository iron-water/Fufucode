using System;
using System.IO;
using Microsoft.Web.WebView2.Core;

namespace Fufucode.App.Infrastructure.Browser;

public enum BrowserRuntimeState
{
    /// <summary>运行时可用，可以创建 CoreWebView2。</summary>
    Available,

    /// <summary>未检测到运行时（或 Loader 不可用），需要用户安装 Evergreen Runtime。</summary>
    Missing,

    /// <summary>检测本身失败（权限、损坏的安装等），原因未知。</summary>
    Unknown,
}

/// <summary>一次运行时探测的结果。Version 只在 Available 时有值，界面不得凭空补全。</summary>
public sealed record BrowserRuntimeProbe(BrowserRuntimeState State, string? Version, string Message)
{
    public bool IsAvailable => State == BrowserRuntimeState.Available;

    /// <summary>可操作提示：引导用户到官方下载页，而不是只报一个错误码。</summary>
    public string Guidance => State switch
    {
        BrowserRuntimeState.Missing =>
            "未检测到 WebView2 运行时。安装 Microsoft Edge WebView2 Evergreen Runtime 后重启 Fufucode 即可启用预览。",
        BrowserRuntimeState.Unknown =>
            "WebView2 运行时状态未知。可重新安装 Evergreen Runtime 后重启 Fufucode。",
        _ => "WebView2 运行时可用。",
    };
}

/// <summary>
/// 在任何 WebView2 对象创建之前探测运行时。探测本身永不抛出：缺运行时要给可操作提示，而不是崩。
/// </summary>
internal static class WebView2RuntimeGate
{
    /// <summary>Microsoft Learn 记录的最终用户下载入口（Evergreen Runtime）。不臆造短链。</summary>
    public const string RuntimeDownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/consumer/";

    public static BrowserRuntimeProbe Probe()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrWhiteSpace(version))
                return new(BrowserRuntimeState.Unknown, null, "WebView2 运行时版本号为空，无法确认可渲染。");
            return new(BrowserRuntimeState.Available, version.Trim(), $"WebView2 运行时 {version.Trim()}");
        }
        catch (WebView2RuntimeNotFoundException error)
        {
            return new(BrowserRuntimeState.Missing, null, "未检测到 WebView2 运行时：" + error.Message);
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or FileNotFoundException or FileLoadException)
        {
            return new(BrowserRuntimeState.Missing, null, "WebView2 Loader 未加载：" + error.Message);
        }
        catch (Exception error)
        {
            return new(BrowserRuntimeState.Unknown, null, "WebView2 运行时检测失败：" + error.Message);
        }
    }
}
