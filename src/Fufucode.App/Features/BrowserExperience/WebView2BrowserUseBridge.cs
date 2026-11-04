using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Fufucode.App.Infrastructure.Browser;
using Fufucode.App.Views;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Features.BrowserExperience;

/// <summary>真实 WebView2 控制桥；观察与执行共用即时设置、站点和审批门禁。</summary>
public sealed class WebView2BrowserUseBridge : BrowserUseBridge
{
    public static readonly TimeSpan ApprovalTimeout = TimeSpan.FromSeconds(60);
    public Func<BrowserApprovalRequest, CancellationToken, Task<bool>>? ApprovalHandler { get; set; }

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Func<BrowserView?> _viewAccessor;
    private readonly Func<BrowserSettingsGate> _gateAccessor;

    public WebView2BrowserUseBridge(Func<BrowserView?> viewAccessor, Func<BrowserSettingsGate> gateAccessor)
    {
        _viewAccessor = viewAccessor ?? throw new ArgumentNullException(nameof(viewAccessor));
        _gateAccessor = gateAccessor ?? throw new ArgumentNullException(nameof(gateAccessor));
    }

    // 审批只授权捕获的视图、页签和地址。等待期间切页/导航后必须重新请求，不能打到另一个页面。
    private sealed record Page(BrowserView View, object Tab, string? Address, string? Target);

    private string? Denial(Page page, bool fullCdp, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var gate = _gateAccessor();
        if (!gate.AgentControlAllowed) return "未启用“让智能体控制内置浏览器”。";
        if (fullCdp && !gate.FullCdpAllowed) return "完整 CDP 访问权限未开启。";
        if (!ReferenceEquals(_viewAccessor(), page.View) ||
            !ReferenceEquals(page.View.ViewModel.ActiveTab, page.Tab) ||
            page.View.ViewModel.ActiveTab.NavigationAddress != page.Address)
            return "审批目标页面已改变，请重新发起操作。";
        // 不缓存站点批准；最新 block 永远先于 allow/人工批准生效。
        if (page.View.SiteRules.PermissionFor(page.Target ?? page.Address) == "block")
            return "站点权限为“禁止访问”。";
        return null;
    }

    private async Task<bool> ApproveAsync(Page page, string action, bool fullCdp, bool forceApproval, CancellationToken token)
    {
        var permission = await OnUiAsync(() => Task.FromResult(
            Denial(page, fullCdp, token) is null ? page.View.SiteRules.PermissionFor(page.Target ?? page.Address) : "block"), token);
        if (permission == "block") return false;
        if (!forceApproval && permission == "allow") return true;
        var handler = ApprovalHandler;
        if (handler is null) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ApprovalTimeout);
        try
        {
            var address = page.Target ?? page.Address ?? string.Empty;
            // WaitAsync 强制执行超时；handler 即便忽略 token 或迟到批准，也不能继续执行。
            return await handler(new BrowserApprovalRequest(action, BrowserSiteRules.HostOf(address) ?? "当前页面", address), timeout.Token)
                .WaitAsync(timeout.Token).ConfigureAwait(false) && !timeout.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return false; } // 审批通道异常同样关闭门禁。
    }

    private async Task<string> RunAsync(string action, CancellationToken token,
        Func<Page, BrowserViewHost, Task<string>> execute, string? target = null,
        bool fullCdp = false, bool forceApproval = false, bool screenshot = false, bool requireReady = true)
    {
        token.ThrowIfCancellationRequested();
        if (!_gateAccessor().AgentControlAllowed) return Error(action + " 被拒绝：未启用智能体控制。");
        if (fullCdp && !_gateAccessor().FullCdpAllowed) return Error(action + " 被拒绝：完整 CDP 访问权限未开启。");
        var page = await OnUiAsync(() =>
        {
            var view = _viewAccessor();
            return Task.FromResult(view is null ? null : new Page(view, view.ViewModel.ActiveTab,
                view.ViewModel.ActiveTab.NavigationAddress, target));
        }, token);
        if (page is null) return Error(action + " 失败：当前没有可用的内置浏览器页面。");
        var mode = await OnUiAsync(() => Task.FromResult(page.View.CurrentSettings.ScreenshotMode), token);
        if (screenshot && mode == "never") return Error(action + " 被拒绝：截图策略为“从不”。");
        if (!await ApproveAsync(page, action, fullCdp, forceApproval || (screenshot && mode == "ask"), token).ConfigureAwait(false))
            return Error(action + " 被拒绝：站点禁止访问、审批未通过或审批已超时。");
        return await OnUiAsync(async () =>
        {
            if (Denial(page, fullCdp, token) is { } denial) return Error(action + " 被拒绝：" + denial);
            if (screenshot && (page.View.CurrentSettings.ScreenshotMode == "never" ||
                (mode != "ask" && page.View.CurrentSettings.ScreenshotMode == "ask")))
                return Error(action + " 被拒绝：截图策略已改变，请重新请求。");
            var host = page.View.HostForTabForTesting(page.View.ViewModel.ActiveTab);
            if (requireReady && !host.IsReady) return Error(action + " 失败：浏览器运行时尚未就绪。");
            var result = await execute(page, host).WaitAsync(token);
            // 结果返回前也检查；导航命令本身会改变地址，由其执行前门禁保证。
            if (target is null && Denial(page, fullCdp, token) is { } changed)
                return Error(action + " 被拒绝：" + changed);
            return result;
        }, token).ConfigureAwait(false);
    }

    private static Task<T> OnUiAsync<T>(Func<Task<T>> operation, CancellationToken token)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            token.ThrowIfCancellationRequested();
            return operation();
        }
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                completion.TrySetResult(await operation());
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        return completion.Task.WaitAsync(token);
    }

    private static async Task<string> ScriptAsync(BrowserViewHost host, string script, string? successField = null)
    {
        var raw = await host.ExecuteScriptAsync(script);
        if (raw is null) return Error("浏览器运行时尚未就绪。");
        try
        {
            var result = NormalizeScriptResult(raw);
            using var document = JsonDocument.Parse(result);
            if (successField is not null && (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(successField, out var success) || success.ValueKind != JsonValueKind.True))
                return Error("浏览器动作未生效：" + result);
            return result;
        }
        catch (JsonException) { return Error("浏览器脚本返回了无效的结构化结果。"); }
    }

    public override Task<string?> CallCdpAsync(string method, string parametersJson, CancellationToken cancellationToken = default) =>
        AsNullable(RunAsync("browser_cdp", cancellationToken, async (_, host) =>
            await host.CallDevToolsProtocolMethodAsync(method, parametersJson) ?? Error("浏览器运行时尚未就绪。"),
            fullCdp: true, forceApproval: true));

    // 任意 JavaScript 与 Runtime.evaluate 等价，必须同时拥有总开关、完整 CDP 和逐次批准。
    public override Task<string?> EvaluateAsync(string javaScript, CancellationToken cancellationToken = default) =>
        AsNullable(RunAsync("browser_evaluate", cancellationToken, async (_, host) =>
            await host.ExecuteScriptAsync(javaScript) ?? Error("浏览器运行时尚未就绪。"),
            fullCdp: true, forceApproval: true));

    private static async Task<string?> AsNullable(Task<string> result) => await result.ConfigureAwait(false);

    public override async Task<bool> NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var result = await RunAsync("browser_navigate", cancellationToken, (page, _) =>
            Task.FromResult(page.View.NavigateFromAgent(url) ? "{\"navigated\":true}" : Error("导航被宿主拒绝。")), target: url, requireReady: false);
        return !IsErrorResult(result);
    }

    public override Task<string> ClickAsync(string selector, CancellationToken cancellationToken = default) =>
        RunAsync("browser_click", cancellationToken, async (page, host) =>
        {
            var probe = await ScriptAsync(host,
                "(function(sel){var el=document.querySelector(sel);if(!el)return {found:false};" +
                "var t=((el.innerText||el.value||el.getAttribute('aria-label')||'')+'').trim();" +
                "return {found:true,sensitive:/(submit|buy|pay|purchase|checkout|place order|delete|remove|confirm|提交|购买|支付|下单|删除|移除|确认)/i.test(t),text:t.slice(0,120)};})(" + Ser(selector) + ")", "found");
            if (IsErrorResult(probe)) return probe;
            using var document = JsonDocument.Parse(probe);
            if (document.RootElement.GetProperty("sensitive").GetBoolean() &&
                !await ApproveAsync(page, "敏感点击：" + document.RootElement.GetProperty("text").GetString(), false, true, cancellationToken))
                return Error("用户未确认敏感点击。");
            if (Denial(page, false, cancellationToken) is { } denial) return Error(denial);
            var point = await PointerTargetAsync(host, selector);
            if (IsErrorResult(point)) return point;
            using var coordinates = JsonDocument.Parse(point);
            var x = coordinates.RootElement.GetProperty("x").GetDouble();
            var y = coordinates.RootElement.GetProperty("y").GetDouble();
            if (Denial(page, false, cancellationToken) is { } changed) return Error(changed);
            var down = await host.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mousePressed", x, y, button = "left", clickCount = 1 }));
            if (down is null || IsErrorResult(down)) return down ?? Error("鼠标输入失败。");
            var up = await host.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }));
            return up is null || IsErrorResult(up) ? up ?? Error("鼠标输入失败。") : "{\"clicked\":true}";
        });

    public override Task<string> TypeAsync(string selector, string text, bool clear, CancellationToken cancellationToken = default) =>
        RunAsync("browser_type", cancellationToken, async (page, host) =>
        {
            var focused = await ScriptAsync(host,
                "((sel,clear)=>{const el=document.querySelector(sel);if(!el||el.disabled||el.readOnly||el.type==='file'||" +
                "!(el instanceof HTMLInputElement||el instanceof HTMLTextAreaElement||el.isContentEditable))return {focused:false};" +
                "el.scrollIntoView({block:'center',behavior:'instant'});el.focus();" +
                "if(clear){if(el.isContentEditable){const r=document.createRange();r.selectNodeContents(el);const s=getSelection();s.removeAllRanges();s.addRange(r);}else el.select();}" +
                "else if(el.isContentEditable){const r=document.createRange();r.selectNodeContents(el);r.collapse(false);const s=getSelection();s.removeAllRanges();s.addRange(r);}" +
                "else{try{el.setSelectionRange(el.value.length,el.value.length);}catch{}}return {focused:document.activeElement===el};})(" +
                Ser(selector) + "," + (clear ? "true" : "false") + ")", "focused");
            if (IsErrorResult(focused)) return focused;
            if (Denial(page, false, cancellationToken) is { } denial) return Error(denial);
            var result = await host.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = text ?? "" }));
            if (result is null || IsErrorResult(result)) return result ?? Error("浏览器输入未完成。");
            return "{\"typed\":true}";
        });

    public override Task<string> ScrollAsync(int deltaX, int deltaY, CancellationToken cancellationToken = default) =>
        RunAsync("browser_scroll", cancellationToken, (_, host) => ScriptAsync(host,
            "(function(x,y){window.scrollBy({left:x,top:y,behavior:'instant'});return {scrollX:window.scrollX,scrollY:window.scrollY};})(" + deltaX + "," + deltaY + ")"));

    public override Task<string> HoverAsync(string selector, CancellationToken cancellationToken = default) =>
        RunAsync("browser_hover", cancellationToken, async (page, host) =>
        {
            var point = await PointerTargetAsync(host, selector);
            if (IsErrorResult(point)) return point;
            using var coordinates = JsonDocument.Parse(point);
            if (Denial(page, false, cancellationToken) is { } denial) return Error(denial);
            var result = await host.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
            {
                type = "mouseMoved", x = coordinates.RootElement.GetProperty("x").GetDouble(), y = coordinates.RootElement.GetProperty("y").GetDouble(),
            }));
            return result is null || IsErrorResult(result) ? result ?? Error("鼠标输入失败。") : "{\"hovered\":true}";
        });

    private static Task<string> PointerTargetAsync(BrowserViewHost host, string selector) => ScriptAsync(host,
        "((sel)=>{const el=document.querySelector(sel);if(!el||el.disabled)return {found:false};" +
        "el.scrollIntoView({block:'center',inline:'center',behavior:'instant'});const r=el.getBoundingClientRect();" +
        "const x=Math.max(0,r.left)+Math.min(innerWidth-Math.max(0,r.left),r.width)/2;" +
        "const y=Math.max(0,r.top)+Math.min(innerHeight-Math.max(0,r.top),r.height)/2;const hit=document.elementFromPoint(x,y);" +
        "return {found:r.width>0&&r.height>0&&(hit===el||el.contains(hit)),x,y};})(" + Ser(selector) + ")", "found");

    public override Task<string> KeyAsync(string key, string? selector = null, bool ctrl = false, bool shift = false,
        bool alt = false, bool meta = false, CancellationToken cancellationToken = default) =>
        RunAsync("browser_key", cancellationToken, async (page, host) =>
        {
            var normalized = key.Equals("Return", StringComparison.OrdinalIgnoreCase) ? "Enter" : key;
            var virtualKey = normalized switch
            {
                "Enter" => 13, "Tab" => 9, "Backspace" => 8, "Delete" => 46, "Escape" => 27,
                "ArrowLeft" => 37, "ArrowUp" => 38, "ArrowRight" => 39, "ArrowDown" => 40,
                "Home" => 36, "End" => 35, "PageUp" => 33, "PageDown" => 34,
                _ when normalized.Length == 1 => (int)char.ToUpperInvariant(normalized[0]), _ => 0,
            };
            if (virtualKey == 0) return Error("不支持该按键。");
            if (selector is not null)
            {
                var focused = await ScriptAsync(host, "(sel=>{const el=document.querySelector(sel);if(!el)return {focused:false};el.focus();return {focused:document.activeElement===el};})(" + Ser(selector) + ")", "focused");
                if (IsErrorResult(focused)) return focused;
            }
            if (Denial(page, false, cancellationToken) is { } denial) return Error(denial);
            var modifiers = (alt ? 1 : 0) | (ctrl ? 2 : 0) | (meta ? 4 : 0) | (shift ? 8 : 0);
            var text = !alt && !ctrl && !meta ? normalized == "Enter" ? "\r" : normalized.Length == 1 ? normalized : "" : "";
            var down = await host.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
            {
                type = text.Length > 0 ? "keyDown" : "rawKeyDown", key = normalized, windowsVirtualKeyCode = virtualKey, modifiers, text,
            }));
            if (down is null || IsErrorResult(down)) return down ?? Error("按键输入失败。");
            var up = await host.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
            {
                type = "keyUp", key = normalized, windowsVirtualKeyCode = virtualKey, modifiers,
            }));
            return up is null || IsErrorResult(up) ? up ?? Error("按键输入失败。") : "{\"keyed\":true}";
        }, forceApproval: key.Equals("Enter", StringComparison.OrdinalIgnoreCase) || key.Equals("Return", StringComparison.OrdinalIgnoreCase));

    public override Task<string> SelectAsync(string selector, string value, CancellationToken cancellationToken = default) =>
        RunAsync("browser_select", cancellationToken, (_, host) => ScriptAsync(host,
            "(function(sel,value){var el=document.querySelector(sel);if(!el)return {selected:false};el.focus();el.value=value;" +
            "el.dispatchEvent(new Event('input',{bubbles:true}));el.dispatchEvent(new Event('change',{bubbles:true}));" +
            "return {selected:el.value===value,value:el.value};})(" + Ser(selector) + "," + Ser(value ?? string.Empty) + ")", "selected"));

    public override Task<string> WaitAsync(string selector, int timeoutMs, CancellationToken cancellationToken = default) =>
        RunAsync("browser_wait", cancellationToken, async (page, host) =>
        {
            var started = DateTime.UtcNow;
            var deadline = started.AddMilliseconds(Math.Clamp(timeoutMs <= 0 ? 5000 : timeoutMs, 100, 30000));
            while (DateTime.UtcNow < deadline)
            {
                if (Denial(page, false, cancellationToken) is { } denial) return Error(denial);
                var probe = await ScriptAsync(host, "({found:!!document.querySelector(" + Ser(selector) + ")})");
                if (IsErrorResult(probe)) return probe;
                using var document = JsonDocument.Parse(probe);
                if (document.RootElement.GetProperty("found").GetBoolean())
                    return "{\"found\":true,\"elapsedMs\":" + (int)(DateTime.UtcNow - started).TotalMilliseconds + "}";
                await Task.Delay(150, cancellationToken);
            }
            return Error("browser_wait 超时：未出现匹配元素。");
        });

    public override Task<string> GetDomAsync(string? selector, int maxText, int maxHtml, CancellationToken cancellationToken = default) =>
        RunAsync("browser_get_dom", cancellationToken, (_, host) => ScriptAsync(host,
            "(function(sel,maxText,maxHtml){var root=sel?document.querySelector(sel):document.body;if(!root)return {error:'missing'};" +
            "return {url:location.href,title:document.title,text:(root.innerText||'').slice(0,maxText),html:(root.outerHTML||'').slice(0,maxHtml)};})(" +
            (selector is null ? "null" : Ser(selector)) + "," + Math.Clamp(maxText <= 0 ? 8000 : maxText, 200, 50000) + "," +
            Math.Clamp(maxHtml <= 0 ? 20000 : maxHtml, 500, 200000) + ")"));

    public override Task<string> ScreenshotAsync(string? format = null, CancellationToken cancellationToken = default) =>
        RunAsync("browser_screenshot", cancellationToken, async (_, host) =>
            await host.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{\"format\":\"" +
                (string.Equals(format, "jpeg", StringComparison.OrdinalIgnoreCase) ? "jpeg" : "png") + "\"}") ?? Error("浏览器运行时尚未就绪。"),
            screenshot: true);

    public override Task<string> GetStateAsync(CancellationToken cancellationToken = default) =>
        RunAsync("browser_get_state", cancellationToken, async (page, host) =>
        {
            var rawTitle = await host.ExecuteScriptAsync("document.title");
            var title = rawTitle is null ? null : JsonSerializer.Deserialize<string>(rawTitle);
            var tab = page.View.ViewModel.ActiveTab;
            return JsonSerializer.Serialize(new { url = tab.NavigationAddress, title, loading = tab.IsLoading,
                secure = tab.Security == BrowserSecurityState.Secure, agentControl = _gateAccessor().AgentControlAllowed,
                fullCdp = _gateAccessor().FullCdpAllowed }, Json);
        });

    private static string Ser(string value) => JsonSerializer.Serialize(value, Json);
    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, Json);
}

public readonly record struct BrowserSettingsGate(bool AgentControlAllowed, bool FullCdpAllowed)
{
    public static BrowserSettingsGate Disabled => new(false, false);
}

public sealed record BrowserApprovalRequest(string Action, string Host, string Address);
