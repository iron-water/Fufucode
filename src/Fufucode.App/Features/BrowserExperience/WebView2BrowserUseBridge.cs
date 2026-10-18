using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Views;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Features.BrowserExperience;

/// <summary>
/// Browser Use 的真实宿主桥（BRW-1）：把 <see cref="BrowserUseBridge"/> 的动作接到
/// 真实 WebView2 的 CDP 与页面脚本执行，不伪造任何结果。
///
/// 门禁在两层：插件市场（已安装且已启用）由 Core 的 <c>PluginToolRegistry</c> 控制；
/// 用户开关（让智能体控制内置浏览器 / 完整 CDP 权限）由本类在调用前再校验一次。
/// 两者任一未通过都如实拒绝，并给出可读原因。
/// </summary>
public sealed class WebView2BrowserUseBridge : BrowserUseBridge
{
    /// <summary>中文提示保持可读（不转义成 \uXXXX），便于模型与日志直接理解拒绝原因。</summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Func<BrowserView?> _viewAccessor;
    private readonly Func<BrowserSettingsGate> _gateAccessor;

    public WebView2BrowserUseBridge(Func<BrowserView?> viewAccessor, Func<BrowserSettingsGate> gateAccessor)
    {
        _viewAccessor = viewAccessor ?? throw new ArgumentNullException(nameof(viewAccessor));
        _gateAccessor = gateAccessor ?? throw new ArgumentNullException(nameof(gateAccessor));
    }

    public override async Task<string?> CallCdpAsync(string method, string parametersJson,
        CancellationToken cancellationToken = default)
    {
        if (!_gateAccessor().FullCdpAllowed)
            return Error("browser_cdp 被拒绝：完整 CDP 访问权限未开启（设置 → 浏览器 → 开发者模式）。");
        var host = ActiveHost();
        if (host is null) return Error("browser_cdp 失败：当前没有可用的内置浏览器页面。");
        var result = await host.CallDevToolsProtocolMethodAsync(method, parametersJson).ConfigureAwait(false);
        return result ?? Error("browser_cdp 失败：浏览器运行时尚未就绪。");
    }

    public override async Task<string?> EvaluateAsync(string javaScript, CancellationToken cancellationToken = default)
    {
        if (!_gateAccessor().AgentControlAllowed)
            return Error("browser_evaluate 被拒绝：未启用“让智能体控制内置浏览器”。");
        var host = ActiveHost();
        if (host is null) return Error("browser_evaluate 失败：当前没有可用的内置浏览器页面。");
        var result = await host.ExecuteScriptAsync(javaScript).ConfigureAwait(false);
        return result ?? Error("browser_evaluate 失败：浏览器运行时尚未就绪。");
    }

    public override Task<bool> NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!_gateAccessor().AgentControlAllowed) return Task.FromResult(false);
        var view = _viewAccessor();
        if (view is null || string.IsNullOrWhiteSpace(url)) return Task.FromResult(false);
        // 导航必须发生在 UI 线程：已在该线程时同步执行（不制造 sync-over-async 死锁），
        // 否则把真实调用投递到 UI 线程并返回可等待任务，调用方可以在后台线程等待。
        var dispatcher = Avalonia.Threading.Dispatcher.UIThread;
        return dispatcher.CheckAccess()
            ? Task.FromResult(view.NavigateFromAgent(url))
            : dispatcher.InvokeAsync(() => view.NavigateFromAgent(url)).GetTask();
    }

    /// <summary>在页面里真实点击一个元素（selector）；成功返回描述，失败返回可读原因。</summary>
    public override async Task<string> ClickAsync(string selector, CancellationToken cancellationToken = default)
    {
        if (!_gateAccessor().AgentControlAllowed)
            return Error("browser_click 被拒绝：未启用“让智能体控制内置浏览器”。");
        if (string.IsNullOrWhiteSpace(selector))
            return Error("browser_click 失败：缺少 selector。");
        var host = ActiveHost();
        if (host is null) return Error("browser_click 失败：当前没有可用的内置浏览器页面。");
        // 用真实 DOM 查询确认元素存在再点击，避免把“没找到”当成成功。
        var probe = await host.ExecuteScriptAsync(
            "(function(sel){var el=document.querySelector(sel);return el? '{\"found\":true}' : '{\"found\":false}';})(" +
            JsonSerializer.Serialize(selector, Json) + ")").ConfigureAwait(false);
        if (probe is null) return Error("browser_click 失败：浏览器运行时尚未就绪。");
        if (!probe.Contains("\"found\":true", StringComparison.Ordinal))
            return Error($"browser_click 失败：页面中没有匹配 '{selector}' 的元素。");
        var click = await host.ExecuteScriptAsync(
            "(function(sel){var el=document.querySelector(sel);if(!el)return '{\"clicked\":false}';el.click();return '{\"clicked\":true}';})(" +
            JsonSerializer.Serialize(selector, Json) + ")").ConfigureAwait(false);
        return click is not null && click.Contains("\"clicked\":true", StringComparison.Ordinal)
            ? $"已点击 {selector}。"
            : Error($"browser_click 失败：点击 '{selector}' 未生效。");
    }

    /// <summary>截取可视区 PNG（base64），走真实 CDP Page.captureScreenshot。</summary>
    public override async Task<string> ScreenshotAsync(string? format = null, CancellationToken cancellationToken = default)
    {
        var target = string.Equals(format, "jpeg", StringComparison.OrdinalIgnoreCase) ? "jpeg" : "png";
        var result = await CallCdpAsync("Page.captureScreenshot",
            "{\"format\":\"" + target + "\"}", cancellationToken).ConfigureAwait(false);
        return result ?? Error("browser_screenshot 失败：浏览器运行时尚未就绪。");
    }

    /// <summary>当前页面状态：地址、标题与是否加载中，全部取自真实 WebView2 会话。</summary>
    public override async Task<string> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var view = _viewAccessor();
        if (view is null) return Error("browser_get_state 失败：当前没有可用的内置浏览器页面。");
        var host = ActiveHost();
        var title = host is null ? null : await host.ExecuteScriptAsync("document.title").ConfigureAwait(false);
        var tab = view.ViewModel.ActiveTab;
        var payload = new
        {
            url = tab.NavigationAddress,
            title = StripQuotes(title),
            loading = tab.IsLoading,
            secure = tab.Security == BrowserSecurityState.Secure,
            agentControl = _gateAccessor().AgentControlAllowed,
            fullCdp = _gateAccessor().FullCdpAllowed,
        };
        return JsonSerializer.Serialize(payload, Json);
    }

    private BrowserViewHost? ActiveHost()
    {
        var view = _viewAccessor();
        if (view is null) return null;
        var host = view.HostForTabForTesting(view.ViewModel.ActiveTab);
        return host.IsReady ? host : null;
    }

    private static string Error(string message) =>
        "{\"error\":" + JsonSerializer.Serialize(message, Json) + "}";

    private static string? StripQuotes(string? raw) =>
        raw is { Length: >= 2 } && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;
}

/// <summary>来自用户设置的门禁快照；由浏览器视图在设置变化时更新。</summary>
public readonly record struct BrowserSettingsGate(bool AgentControlAllowed, bool FullCdpAllowed)
{
    public static BrowserSettingsGate Disabled => new(false, false);
}
