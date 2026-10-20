using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.BrowserUse;

/// <summary>
/// 浏览器控制插件（agent.browser-use）的工具集：CDP 调用与 Browser Use 动作契约。
/// 作为独立物理插件包编译，不静态依赖或内置进核心默认二进制。
/// </summary>
public sealed class BrowserUsePlugin : IPluginToolHandler
{
    public const string CdpTool = "browser_cdp";
    public const string EvaluateTool = "browser_evaluate";
    public const string NavigateTool = "browser_navigate";
    public const string ClickTool = "browser_click";
    public const string TypeTool = "browser_type";
    public const string ScrollTool = "browser_scroll";
    public const string HoverTool = "browser_hover";
    public const string KeyTool = "browser_key";
    public const string SelectTool = "browser_select";
    public const string WaitTool = "browser_wait";
    public const string GetDomTool = "browser_get_dom";
    public const string ScreenshotTool = "browser_screenshot";
    public const string GetStateTool = "browser_get_state";

    /// <summary>设备自带浏览器（Chrome/Edge）的只读状态工具。</summary>
    public const string ExternalStateTool = "external_browser_state";
    /// <summary>设备自带浏览器的通用动作工具；action 取值见 <see cref="ExternalBrowserActions"/>。</summary>
    public const string ExternalActionTool = "external_browser_action";

    private const string CdpSchema =
        "{\"type\":\"object\",\"properties\":{\"method\":{\"type\":\"string\",\"description\":\"CDP 方法名，例如 Runtime.evaluate。\"}," +
        "\"params\":{\"type\":\"object\",\"description\":\"CDP 方法参数对象。\"}},\"required\":[\"method\"]}";

    private const string EvaluateSchema =
        "{\"type\":\"object\",\"properties\":{\"script\":{\"type\":\"string\"}},\"required\":[\"script\"]}";

    private const string NavigateSchema =
        "{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\"}},\"required\":[\"url\"]}";

    private const string ClickSchema =
        "{\"type\":\"object\",\"properties\":{\"selector\":{\"type\":\"string\",\"description\":\"CSS 选择器，例如 #submit 或 .card > a。\"}},\"required\":[\"selector\"]}";

    private const string TypeSchema =
        "{\"type\":\"object\",\"properties\":{\"selector\":{\"type\":\"string\",\"description\":\"目标输入元素的 CSS 选择器。\"}," +
        "\"text\":{\"type\":\"string\",\"description\":\"要输入的文本。\"}," +
        "\"clear\":{\"type\":\"boolean\",\"description\":\"是否先清空已有内容，默认 false。\"}}," +
        "\"required\":[\"selector\",\"text\"]}";

    private const string ScrollSchema =
        "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"integer\",\"description\":\"水平滚动像素，默认 0。\"}," +
        "\"y\":{\"type\":\"integer\",\"description\":\"垂直滚动像素，正数向下。\"}}}";

    private const string HoverSchema =
        "{\"type\":\"object\",\"properties\":{\"selector\":{\"type\":\"string\"}},\"required\":[\"selector\"]}";

    private const string KeySchema =
        "{\"type\":\"object\",\"properties\":{\"key\":{\"type\":\"string\",\"description\":\"例如 Enter、Escape、ArrowDown。\"}," +
        "\"selector\":{\"type\":\"string\",\"description\":\"可选目标元素；缺省为当前焦点。\"}," +
        "\"ctrl\":{\"type\":\"boolean\"},\"shift\":{\"type\":\"boolean\"},\"alt\":{\"type\":\"boolean\"},\"meta\":{\"type\":\"boolean\"}}," +
        "\"required\":[\"key\"]}";

    private const string SelectSchema =
        "{\"type\":\"object\",\"properties\":{\"selector\":{\"type\":\"string\"},\"value\":{\"type\":\"string\"}}," +
        "\"required\":[\"selector\",\"value\"]}";

    private const string WaitSchema =
        "{\"type\":\"object\",\"properties\":{\"selector\":{\"type\":\"string\"}," +
        "\"timeoutMs\":{\"type\":\"integer\",\"description\":\"最长等待毫秒数，默认 5000。\"}},\"required\":[\"selector\"]}";

    private const string GetDomSchema =
        "{\"type\":\"object\",\"properties\":{\"selector\":{\"type\":\"string\",\"description\":\"可选根元素；缺省为 body。\"}," +
        "\"maxText\":{\"type\":\"integer\"},\"maxHtml\":{\"type\":\"integer\"}}}";

    private const string ScreenshotSchema =
        "{\"type\":\"object\",\"properties\":{\"format\":{\"type\":\"string\",\"enum\":[\"png\",\"jpeg\"],\"description\":\"图片格式，默认 png。\"}}}";

    private const string GetStateSchema =
        "{\"type\":\"object\",\"properties\":{}}";

    private const string ExternalStateSchema =
        "{\"type\":\"object\",\"properties\":{}}";

    private const string ExternalActionSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"action\":{\"type\":\"string\",\"enum\":[\"navigate\",\"click\",\"type\",\"scroll\",\"hover\",\"key\",\"select\",\"wait\",\"get_dom\",\"screenshot\",\"evaluate\",\"tabs\",\"activate_tab\",\"open_tab\",\"close_tab\"]," +
        "\"description\":\"要执行的设备浏览器动作。\"}," +
        "\"arguments\":{\"type\":\"object\",\"description\":\"动作参数，例如 url、selector、text、x/y、key、tabId。\"}}," +
        "\"required\":[\"action\"]}";

    public BrowserUsePlugin(BrowserUseBridge? bridge = null, ExternalBrowserBridge? externalBridge = null)
    {
        Bridge = bridge;
        ExternalBridge = externalBridge;
        var tools = new List<PluginToolRegistration>
        {
            new(CdpTool, "通过 Chrome DevTools Protocol 调用内置浏览器内部方法（DOM、网络、性能等）。", CdpSchema, PluginCapability.BrowserControl),
            new(EvaluateTool, "在内置浏览器当前页面上下文执行 JavaScript 并返回结果。", EvaluateSchema, PluginCapability.BrowserControl),
            new(NavigateTool, "驱动内置浏览器导航到指定地址。", NavigateSchema, PluginCapability.BrowserControl),
            new(ClickTool, "在内置浏览器当前页面里点击匹配 CSS 选择器的元素。", ClickSchema, PluginCapability.BrowserControl),
            new(TypeTool, "向内置浏览器匹配 CSS 选择器的输入元素输入文本（可先清空）。", TypeSchema, PluginCapability.BrowserControl),
            new(ScrollTool, "按像素滚动内置浏览器页面。", ScrollSchema, PluginCapability.BrowserControl),
            new(HoverTool, "把鼠标悬停到内置浏览器匹配元素上。", HoverSchema, PluginCapability.BrowserControl),
            new(KeyTool, "向内置浏览器派发按键（Enter 会触发敏感操作确认）。", KeySchema, PluginCapability.BrowserControl),
            new(SelectTool, "选择内置浏览器下拉框选项。", SelectSchema, PluginCapability.BrowserControl),
            new(WaitTool, "等待内置浏览器中的元素出现。", WaitSchema, PluginCapability.BrowserControl),
            new(GetDomTool, "读取内置浏览器当前页面的文本与 HTML 快照。", GetDomSchema, PluginCapability.BrowserControl),
            new(ScreenshotTool, "截取内置浏览器当前可视区域的图片（base64）。", ScreenshotSchema, PluginCapability.BrowserControl),
            new(GetStateTool, "读取内置浏览器当前地址、标题、加载状态与权限状态。", GetStateSchema, PluginCapability.BrowserControl),
        };
        if (ExternalBridge is not null)
        {
            tools.Add(new PluginToolRegistration(ExternalStateTool,
                "读取设备自带浏览器（Chrome/Edge）的当前页签、地址、标题与连接状态。",
                ExternalStateSchema, PluginCapability.BrowserControl));
            tools.Add(new PluginToolRegistration(ExternalActionTool,
                "在设备自带浏览器中执行动作（导航、点击、输入、滚动、悬停、按键、选择、等待、读取 DOM、截图、求值、页签管理）。",
                ExternalActionSchema, PluginCapability.BrowserControl));
        }
        Registrations = tools;
    }

    public BrowserUseBridge? Bridge { get; set; }
    public ExternalBrowserBridge? ExternalBridge { get; set; }

    public PluginCapability? Capability => PluginCapability.BrowserControl;

    public IReadOnlyList<PluginToolRegistration> Registrations { get; }

    public bool IsConnected => Bridge is not null || ExternalBridge is not null;

    public async Task<PureToolResult> ExecuteAsync(PureToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsConnected is false)
            return new PureToolResult(call.Id, false, "浏览器控制工具尚未接线到真实浏览器宿主。");

        try
        {
            var arguments = call.ArgumentsJson;
            return call.Name switch
            {
                ExternalStateTool => await ExternalStateAsync(call, cancellationToken).ConfigureAwait(false),
                ExternalActionTool => await ExternalActionAsync(call, arguments, cancellationToken).ConfigureAwait(false),
                _ when Bridge is null => new PureToolResult(call.Id, false, "内置浏览器控制尚未接线到真实浏览器宿主。"),
                CdpTool => await CallCdpAsync(call, arguments, cancellationToken).ConfigureAwait(false),
                EvaluateTool => await EvaluateAsync(call, arguments, cancellationToken).ConfigureAwait(false),
                NavigateTool => await NavigateAsync(call, arguments, cancellationToken).ConfigureAwait(false),
                ClickTool => await ClickAsync(call, arguments, cancellationToken).ConfigureAwait(false),
                TypeTool => await TypeAsync(call, arguments, cancellationToken).ConfigureAwait(false),
                ScrollTool => await ScrollAsync(call, arguments, cancellationToken).ConfigureAwait(false),
                HoverTool => await HoverAsync(call, arguments, cancellationToken).ConfigureAwait(false),
                KeyTool => await KeyAsync(call, arguments, cancellationToken).ConfigureAwait(false),
                SelectTool => await SelectAsync(call, arguments, cancellationToken).ConfigureAwait(false),
                WaitTool => await WaitAsync(call, arguments, cancellationToken).ConfigureAwait(false),
                GetDomTool => await GetDomAsync(call, arguments, cancellationToken).ConfigureAwait(false),
                ScreenshotTool => await ScreenshotAsync(call, arguments, cancellationToken).ConfigureAwait(false),
                GetStateTool => await GetStateAsync(call, cancellationToken).ConfigureAwait(false),
                _ => new PureToolResult(call.Id, false, $"未知的浏览器控制工具：'{call.Name}'。"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            return new PureToolResult(call.Id, false, $"工具参数不是合法 JSON：{ex.Message}");
        }
        catch (Exception ex)
        {
            return new PureToolResult(call.Id, false, $"浏览器控制执行失败：{ex.Message}");
        }
    }

    private async Task<PureToolResult> ExternalStateAsync(PureToolCall call, CancellationToken cancellationToken)
    {
        if (ExternalBridge is null)
            return new PureToolResult(call.Id, false, "设备自带浏览器控制桥尚未接线。");
        var payload = await ExternalBridge.ExecuteAsync(ExternalBrowserActions.State, "{}", cancellationToken)
            .ConfigureAwait(false);
        return new PureToolResult(call.Id, !IsErrorPayload(payload), payload);
    }

    private async Task<PureToolResult> ExternalActionAsync(PureToolCall call, string arguments, CancellationToken cancellationToken)
    {
        if (ExternalBridge is null)
            return new PureToolResult(call.Id, false, "设备自带浏览器控制桥尚未接线。");
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("action", out var actionElement) ||
            actionElement.ValueKind != JsonValueKind.String ||
            actionElement.GetString() is not { Length: > 0 } action)
            return new PureToolResult(call.Id, false, "参数错误：'external_browser_action' 需要 action。");
        var args = root.TryGetProperty("arguments", out var argsElement) && argsElement.ValueKind == JsonValueKind.Object
            ? argsElement.GetRawText()
            : "{}";
        var payload = await ExternalBridge.ExecuteAsync(action, args, cancellationToken).ConfigureAwait(false);
        return new PureToolResult(call.Id, !IsErrorPayload(payload), payload);
    }

    private async Task<PureToolResult> CallCdpAsync(PureToolCall call, string arguments, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(arguments);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("method", out var methodElement) ||
            methodElement.ValueKind != JsonValueKind.String ||
            methodElement.GetString() is not { Length: > 0 } method)
        {
            return new PureToolResult(call.Id, false, "参数错误：'browser_cdp' 需要 method。");
        }

        var parameters = root.TryGetProperty("params", out var paramsElement) &&
            paramsElement.ValueKind == JsonValueKind.Object
            ? paramsElement.GetRawText()
            : "{}";
        var result = await Bridge!.CallCdpAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        var payload = result ?? "null";
        return new PureToolResult(call.Id, !IsErrorPayload(payload), payload);
    }

    private static bool IsErrorPayload(string payload) =>
        payload.Length > 0 && payload.Contains("\"error\"", StringComparison.Ordinal);

    private async Task<PureToolResult> EvaluateAsync(PureToolCall call, string arguments, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(arguments);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("script", out var scriptElement) ||
            scriptElement.ValueKind != JsonValueKind.String ||
            scriptElement.GetString() is not { Length: > 0 } script)
        {
            return new PureToolResult(call.Id, false, "参数错误：'browser_evaluate' 需要 script。");
        }

        var result = await Bridge!.EvaluateAsync(script, cancellationToken).ConfigureAwait(false);
        return new PureToolResult(call.Id, true, result ?? "null");
    }

    private async Task<PureToolResult> NavigateAsync(PureToolCall call, string arguments, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(arguments);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("url", out var urlElement) ||
            urlElement.ValueKind != JsonValueKind.String ||
            urlElement.GetString() is not { Length: > 0 } url)
        {
            return new PureToolResult(call.Id, false, "参数错误：'browser_navigate' 需要 url。");
        }

        var ok = await Bridge!.NavigateAsync(url, cancellationToken).ConfigureAwait(false);
        return new PureToolResult(call.Id, ok, ok ? $"已导航到 {url}。" : $"导航到 {url} 被宿主拒绝或失败。");
    }

    private async Task<PureToolResult> ClickAsync(PureToolCall call, string arguments, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(arguments);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("selector", out var selectorElement) ||
            selectorElement.ValueKind != JsonValueKind.String ||
            selectorElement.GetString() is not { Length: > 0 } selector)
        {
            return new PureToolResult(call.Id, false, "参数错误：'browser_click' 需要 selector。");
        }

        var result = await Bridge!.ClickAsync(selector, cancellationToken).ConfigureAwait(false);
        return new PureToolResult(call.Id, !result.Contains("\"error\"", StringComparison.Ordinal), result);
    }

    private async Task<PureToolResult> TypeAsync(PureToolCall call, string arguments, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(arguments);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("selector", out var selectorElement) ||
            selectorElement.ValueKind != JsonValueKind.String ||
            selectorElement.GetString() is not { Length: > 0 } selector)
        {
            return new PureToolResult(call.Id, false, "参数错误：'browser_type' 需要 selector。");
        }
        if (!root.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String)
            return new PureToolResult(call.Id, false, "参数错误：'browser_type' 需要 text。");
        var text = textElement.GetString() ?? string.Empty;
        var clear = root.TryGetProperty("clear", out var clearElement) && clearElement.ValueKind == JsonValueKind.True;

        var result = await Bridge!.TypeAsync(selector, text, clear, cancellationToken).ConfigureAwait(false);
        return new PureToolResult(call.Id, !result.Contains("\"error\"", StringComparison.Ordinal), result);
    }

    private async Task<PureToolResult> ScrollAsync(PureToolCall call, string arguments, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(arguments);
        var root = doc.RootElement;
        var x = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("x", out var xe) && xe.TryGetInt32(out var xi) ? xi : 0;
        var y = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("y", out var ye) && ye.TryGetInt32(out var yi) ? yi : 0;
        var result = await Bridge!.ScrollAsync(x, y, cancellationToken).ConfigureAwait(false);
        return new PureToolResult(call.Id, !result.Contains("\"error\"", StringComparison.Ordinal), result);
    }

    private async Task<PureToolResult> HoverAsync(PureToolCall call, string arguments, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(arguments);
        if (!TryString(doc.RootElement, "selector", out var selector))
            return new PureToolResult(call.Id, false, "参数错误：'browser_hover' 需要 selector。");
        var result = await Bridge!.HoverAsync(selector, cancellationToken).ConfigureAwait(false);
        return new PureToolResult(call.Id, !result.Contains("\"error\"", StringComparison.Ordinal), result);
    }

    private async Task<PureToolResult> KeyAsync(PureToolCall call, string arguments, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(arguments);
        if (!TryString(doc.RootElement, "key", out var key))
            return new PureToolResult(call.Id, false, "参数错误：'browser_key' 需要 key。");
        var selector = TryString(doc.RootElement, "selector", out var value) ? value : null;
        var result = await Bridge!.KeyAsync(key, selector,
            TryBool(doc.RootElement, "ctrl"), TryBool(doc.RootElement, "shift"),
            TryBool(doc.RootElement, "alt"), TryBool(doc.RootElement, "meta"),
            cancellationToken).ConfigureAwait(false);
        return new PureToolResult(call.Id, !result.Contains("\"error\"", StringComparison.Ordinal), result);
    }

    private async Task<PureToolResult> SelectAsync(PureToolCall call, string arguments, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(arguments);
        if (!TryString(doc.RootElement, "selector", out var selector) ||
            !TryString(doc.RootElement, "value", out var value))
            return new PureToolResult(call.Id, false, "参数错误：'browser_select' 需要 selector 与 value。");
        var result = await Bridge!.SelectAsync(selector, value, cancellationToken).ConfigureAwait(false);
        return new PureToolResult(call.Id, !result.Contains("\"error\"", StringComparison.Ordinal), result);
    }

    private async Task<PureToolResult> WaitAsync(PureToolCall call, string arguments, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(arguments);
        if (!TryString(doc.RootElement, "selector", out var selector))
            return new PureToolResult(call.Id, false, "参数错误：'browser_wait' 需要 selector。");
        var timeout = doc.RootElement.ValueKind == JsonValueKind.Object &&
                      doc.RootElement.TryGetProperty("timeoutMs", out var timeoutElement) &&
                      timeoutElement.TryGetInt32(out var parsed) ? parsed : 5000;
        var result = await Bridge!.WaitAsync(selector, timeout, cancellationToken).ConfigureAwait(false);
        return new PureToolResult(call.Id, !result.Contains("\"error\"", StringComparison.Ordinal), result);
    }

    private async Task<PureToolResult> GetDomAsync(PureToolCall call, string arguments, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(arguments);
        var selector = TryString(doc.RootElement, "selector", out var value) ? value : null;
        var maxText = doc.RootElement.ValueKind == JsonValueKind.Object &&
                      doc.RootElement.TryGetProperty("maxText", out var textElement) && textElement.TryGetInt32(out var text) ? text : 8000;
        var maxHtml = doc.RootElement.ValueKind == JsonValueKind.Object &&
                      doc.RootElement.TryGetProperty("maxHtml", out var htmlElement) && htmlElement.TryGetInt32(out var html) ? html : 20000;
        var result = await Bridge!.GetDomAsync(selector, maxText, maxHtml, cancellationToken).ConfigureAwait(false);
        return new PureToolResult(call.Id, !result.Contains("\"error\"", StringComparison.Ordinal), result);
    }

    private static bool TryString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryBool(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var element) &&
        element.ValueKind == JsonValueKind.True;

    private async Task<PureToolResult> ScreenshotAsync(PureToolCall call, string arguments, CancellationToken cancellationToken)
    {
        string? format = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("format", out var formatElement) &&
                formatElement.ValueKind == JsonValueKind.String)
                format = formatElement.GetString();
        }
        catch (JsonException ex)
        {
            return new PureToolResult(call.Id, false, $"工具参数不是合法 JSON：{ex.Message}");
        }

        var result = await Bridge!.ScreenshotAsync(format, cancellationToken).ConfigureAwait(false);
        return new PureToolResult(call.Id, !result.Contains("\"error\"", StringComparison.Ordinal), result);
    }

    private async Task<PureToolResult> GetStateAsync(PureToolCall call, CancellationToken cancellationToken)
    {
        var result = await Bridge!.GetStateAsync(cancellationToken).ConfigureAwait(false);
        return new PureToolResult(call.Id, !result.Contains("\"error\"", StringComparison.Ordinal), result);
    }
}

/// <summary>
/// 浏览器控制插件包入口，被 <see cref="PluginPackageManager"/> 动态实例化。
/// </summary>
public sealed class BrowserUsePluginPackage : IPluginPackage
{
    public string Id => "agent.browser-use";
    public string Version => "1.0.0";

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context)
    {
        var bridge = context.GetService<BrowserUseBridge>();
        var external = context.GetService<ExternalBrowserBridge>();
        return new BrowserUsePlugin(bridge, external);
    }
}
