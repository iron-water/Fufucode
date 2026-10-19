using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Infrastructure.Browser;
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
    /// <summary>智能体动作等待用户批准的上限；超时按拒绝处理，不静默放行。</summary>
    public static readonly TimeSpan ApprovalTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 审批请求：动作名 + 目标站点 + 可读说明。返回 true = 允许，false = 拒绝。
    /// 由组合根接到聊天窗口的确认卡片；未接线时按“需要批准”的站点一律拒绝。
    /// </summary>
    public Func<BrowserApprovalRequest, CancellationToken, Task<bool>>? ApprovalHandler { get; set; }

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
        var view = _viewAccessor();
        if (view is null) return Error("browser_evaluate 失败：当前没有可用的内置浏览器页面。");
        if (!await ApproveAsync(view, BrowserUseActionNames.Evaluate, null, cancellationToken).ConfigureAwait(false))
            return Error("browser_evaluate 被拒绝：站点需要批准或已禁止智能体操作。");
        var host = ActiveHost();
        if (host is null) return Error("browser_evaluate 失败：当前没有可用的内置浏览器页面。");
        var result = await host.ExecuteScriptAsync(javaScript).ConfigureAwait(false);
        return result ?? Error("browser_evaluate 失败：浏览器运行时尚未就绪。");
    }

    public override async Task<bool> NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!_gateAccessor().AgentControlAllowed) return false;
        var view = _viewAccessor();
        if (view is null || string.IsNullOrWhiteSpace(url)) return false;
        if (!await ApproveAsync(view, BrowserUseActionNames.Navigate, url, cancellationToken).ConfigureAwait(false)) return false;
        // 导航必须发生在 UI 线程：已在该线程时同步执行（不制造 sync-over-async 死锁），
        // 否则把真实调用投递到 UI 线程并返回可等待任务，调用方可以在后台线程等待。
        var dispatcher = Avalonia.Threading.Dispatcher.UIThread;
        return dispatcher.CheckAccess()
            ? Task.FromResult(view.NavigateFromAgent(url)).Result
            : await dispatcher.InvokeAsync(() => view.NavigateFromAgent(url));
    }

    /// <summary>
    /// 站点权限闸门：目标（或当前页面）站点为“禁止访问”直接拒绝；“需要批准”等待用户；
    /// “始终允许”放行。检查在每次动作执行前发生，不受聊天执行模式（auto/full）影响。
    /// </summary>
    /// <summary>本进程内用户已批准过智能体操作的站点；避免每次点击都重复确认。</summary>
    private readonly HashSet<string> _approvedHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _approvalGate = new();

    private async Task<bool> ApproveAsync(BrowserView view, string action, string? targetUri, CancellationToken token,
        bool forceApproval = false)
    {
        var rules = view.SiteRules;
        var address = string.IsNullOrWhiteSpace(targetUri) ? view.ViewModel.ActiveTab.NavigationAddress : targetUri;
        var permission = rules.PermissionFor(address);
        if (!forceApproval && string.Equals(permission, "allow", StringComparison.OrdinalIgnoreCase)) return true;
        var host = BrowserSiteRules.HostOf(address) ?? view.ViewModel.ActiveTab.SecureHostForAgent;
        if (!forceApproval && host is { Length: > 0 })
        {
            lock (_approvalGate)
                if (_approvedHosts.Contains(host)) return true;
        }
        if (string.Equals(permission, "block", StringComparison.OrdinalIgnoreCase))
        {
            view.ViewModel.ReportStatus($"智能体动作 {action} 被拒绝：{host} 的站点权限为“禁止访问”。");
            return false;
        }
        if (ApprovalHandler is null)
        {
            view.ViewModel.ReportStatus($"智能体动作 {action} 需要批准，但当前会话没有可用的确认通道，已拒绝。");
            return false;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ApprovalTimeout);
        try
        {
            var allowed = await ApprovalHandler(new BrowserApprovalRequest(action, host ?? "当前页面", address ?? string.Empty), timeout.Token)
                .ConfigureAwait(false);
            if (allowed && host is { Length: > 0 })
            {
                lock (_approvalGate) _approvedHosts.Add(host);
            }
            view.ViewModel.ReportStatus(allowed
                ? $"已批准智能体动作 {action}（{host}）。"
                : $"智能体动作 {action} 被拒绝（{host}）。");
            return allowed;
        }
        catch (OperationCanceledException)
        {
            view.ViewModel.ReportStatus($"智能体动作 {action} 等待批准超时，已拒绝。");
            return false;
        }
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
        var view = _viewAccessor()!;
        if (!await ApproveAsync(view, BrowserUseActionNames.Click, null, cancellationToken).ConfigureAwait(false))
            return Error("browser_click 被拒绝：站点需要批准或已禁止智能体操作。");
        // 用真实 DOM 查询确认元素存在再点击，避免把“没找到”当成成功。
        var probe = await host.ExecuteScriptAsync(
            "(function(sel){var el=document.querySelector(sel);return el? '{\"found\":true}' : '{\"found\":false}';})(" +
            Ser(selector) + ")").ConfigureAwait(false);
        if (probe is null) return Error("browser_click 失败：浏览器运行时尚未就绪。");
        if (!probe.Contains("\"found\":true", StringComparison.Ordinal))
            return Error($"browser_click 失败：页面中没有匹配 '{selector}' 的元素。");
        var click = await host.ExecuteScriptAsync(
            "(function(sel){var el=document.querySelector(sel);if(!el)return '{\"clicked\":false}';el.click();return '{\"clicked\":true}';})(" +
            Ser(selector) + ")").ConfigureAwait(false);
        return click is not null && click.Contains("\"clicked\":true", StringComparison.Ordinal)
            ? $"已点击 {selector}。"
            : Error($"browser_click 失败：点击 '{selector}' 未生效。");
    }

    /// <summary>向匹配元素输入文本：先聚焦，可选清空，再写入并派发 input/change 事件。</summary>
    public override async Task<string> TypeAsync(string selector, string text, bool clear,
        CancellationToken cancellationToken = default)
    {
        if (!_gateAccessor().AgentControlAllowed)
            return Error("browser_type 被拒绝：未启用“让智能体控制内置浏览器”。");
        if (string.IsNullOrWhiteSpace(selector))
            return Error("browser_type 失败：缺少 selector。");
        var view = _viewAccessor();
        var host = ActiveHost();
        if (view is null || host is null) return Error("browser_type 失败：当前没有可用的内置浏览器页面。");
        if (!await ApproveAsync(view, BrowserUseActionNames.Type, null, cancellationToken).ConfigureAwait(false))
            return Error("browser_type 被拒绝：站点需要批准或已禁止智能体操作。");

        var script =
            "(function(sel,value,clear){var el=document.querySelector(sel);" +
            "if(!el)return '{\"typed\":false,\"reason\":\"missing\"}';" +
            "el.focus();if(clear){if('value' in el)el.value='';else el.textContent='';}" +
            "if('value' in el){el.value=(clear?'':el.value)+value;}else{el.textContent=(clear?'':el.textContent)+value;}" +
            "el.dispatchEvent(new Event('input',{bubbles:true}));" +
            "el.dispatchEvent(new Event('change',{bubbles:true}));" +
            "return '{\"typed\":true}';})(" +
            Ser(selector) + "," + Ser(text ?? string.Empty) + "," + (clear ? "true" : "false") + ")";
        var result = await host.ExecuteScriptAsync(script).ConfigureAwait(false);
        return result is not null && result.Contains("\"typed\":true", StringComparison.Ordinal)
            ? $"已在 {selector} 输入文本。"
            : Error($"browser_type 失败：未找到 '{selector}' 或输入未生效。");
    }

    /// <summary>
    /// 截取可视区 PNG（base64），走真实 CDP Page.captureScreenshot。
    /// 截图是只读能力：不要求“完整 CDP 权限”，但必须遵守设置里的截图策略与站点审批。
    /// </summary>
    public override async Task<string> ScreenshotAsync(string? format = null, CancellationToken cancellationToken = default)
    {
        if (!_gateAccessor().AgentControlAllowed)
            return Error("browser_screenshot 被拒绝：未启用“让智能体控制内置浏览器”。");
        var view = _viewAccessor();
        if (view is null) return Error("browser_screenshot 失败：当前没有可用的内置浏览器页面。");
        var mode = view.CurrentSettings.ScreenshotMode;
        if (string.Equals(mode, "never", StringComparison.OrdinalIgnoreCase))
            return Error("browser_screenshot 被拒绝：设置中的截图策略为“从不”。");
        var forceApproval = string.Equals(mode, "ask", StringComparison.OrdinalIgnoreCase);
        if (!await ApproveAsync(view, BrowserUseActionNames.Screenshot, null, cancellationToken, forceApproval).ConfigureAwait(false))
            return Error("browser_screenshot 被拒绝：站点需要批准、截图策略为询问或已禁止智能体操作。");
        var host = ActiveHost();
        if (host is null) return Error("browser_screenshot 失败：浏览器运行时尚未就绪。");
        var target = string.Equals(format, "jpeg", StringComparison.OrdinalIgnoreCase) ? "jpeg" : "png";
        var result = await host.CallDevToolsProtocolMethodAsync("Page.captureScreenshot",
            "{\"format\":\"" + target + "\"}").ConfigureAwait(false);
        return result ?? Error("browser_screenshot 失败：浏览器运行时尚未就绪。");
    }

    /// <summary>当前页面状态：地址、标题与是否加载中，全部取自真实 WebView2 会话。</summary>
    public override async Task<string> GetStateAsync(CancellationToken cancellationToken = default)
    {
        if (!_gateAccessor().AgentControlAllowed)
            return Error("browser_get_state 被拒绝：未启用“让智能体控制内置浏览器”。");
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

    /// <summary>把参数序列化为 JS 字面量（可安全内嵌到脚本字符串里）。</summary>
    private static string Ser(string value) =>
        JsonSerializer.Serialize(value, Json);

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

/// <summary>智能体浏览器动作需要用户批准时的请求描述。</summary>
public sealed record BrowserApprovalRequest(string Action, string Host, string Address);
