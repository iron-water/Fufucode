using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Plugins;

/// <summary>
/// 设备自带浏览器（Chrome/Edge）的控制桥。与内置浏览器使用完全不同的执行路径：
/// 命令经本地回环 HTTP 发给浏览器扩展，扩展在用户真实登录态里执行并回传结果。
/// </summary>
public abstract class ExternalBrowserBridge
{
    /// <summary>扩展最近一次心跳/轮询是否仍在有效窗口内。</summary>
    public abstract bool IsConnected { get; }

    /// <summary>最近一次已知页面主机名；用于站点权限与审批提示。</summary>
    public abstract string? LastPageHost { get; }

    /// <summary>
    /// 执行一个外部浏览器动作。返回值是 JSON 文本；失败时包含 <c>{"error":...}</c>。
    /// action 取值见 <see cref="ExternalBrowserActions"/>。
    /// </summary>
    public abstract Task<string> ExecuteAsync(string action, string argumentsJson,
        CancellationToken cancellationToken = default);
}

/// <summary>外部浏览器扩展支持的动作名（与 service-worker 中的 dispatcher 保持一致）。</summary>
public static class ExternalBrowserActions
{
    public const string State = "state";
    public const string Navigate = "navigate";
    public const string Click = "click";
    public const string Type = "type";
    public const string Scroll = "scroll";
    public const string Hover = "hover";
    public const string Key = "key";
    public const string Select = "select";
    public const string Wait = "wait";
    public const string GetDom = "get_dom";
    public const string Screenshot = "screenshot";
    public const string Evaluate = "evaluate";
    public const string Tabs = "tabs";
    public const string ActivateTab = "activate_tab";
    public const string OpenTab = "open_tab";
    public const string CloseTab = "close_tab";
}
