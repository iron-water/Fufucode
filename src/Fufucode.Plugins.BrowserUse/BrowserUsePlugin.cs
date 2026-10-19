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
    public const string ScreenshotTool = "browser_screenshot";
    public const string GetStateTool = "browser_get_state";

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

    private const string ScreenshotSchema =
        "{\"type\":\"object\",\"properties\":{\"format\":{\"type\":\"string\",\"enum\":[\"png\",\"jpeg\"],\"description\":\"图片格式，默认 png。\"}}}";

    private const string GetStateSchema =
        "{\"type\":\"object\",\"properties\":{}}";

    public BrowserUsePlugin(BrowserUseBridge? bridge = null) => Bridge = bridge;

    public BrowserUseBridge? Bridge { get; set; }

    public PluginCapability? Capability => PluginCapability.BrowserControl;

    public IReadOnlyList<PluginToolRegistration> Registrations { get; } = new[]
    {
        new PluginToolRegistration(CdpTool,
            "通过 Chrome DevTools Protocol 调用浏览器内部方法（DOM、网络、性能等）。",
            CdpSchema, PluginCapability.BrowserControl),
        new PluginToolRegistration(EvaluateTool,
            "在当前页面上下文执行 JavaScript 并返回结果。",
            EvaluateSchema, PluginCapability.BrowserControl),
        new PluginToolRegistration(NavigateTool,
            "驱动内置浏览器导航到指定地址。",
            NavigateSchema, PluginCapability.BrowserControl),
        new PluginToolRegistration(ClickTool,
            "在内置浏览器当前页面里点击匹配 CSS 选择器的元素。",
            ClickSchema, PluginCapability.BrowserControl),
        new PluginToolRegistration(TypeTool,
            "向匹配 CSS 选择器的输入元素输入文本（可先清空）。",
            TypeSchema, PluginCapability.BrowserControl),
        new PluginToolRegistration(ScreenshotTool,
            "截取内置浏览器当前可视区域的图片（base64）。",
            ScreenshotSchema, PluginCapability.BrowserControl),
        new PluginToolRegistration(GetStateTool,
            "读取内置浏览器当前地址、标题、加载状态与权限状态。",
            GetStateSchema, PluginCapability.BrowserControl),
    };

    public bool IsConnected => Bridge is not null;

    public async Task<PureToolResult> ExecuteAsync(PureToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        cancellationToken.ThrowIfCancellationRequested();
        if (Bridge is null)
            return new PureToolResult(call.Id, false, "浏览器控制工具尚未接线到真实浏览器宿主。");

        try
        {
            var arguments = call.ArgumentsJson;
            return call.Name switch
            {
                CdpTool => await CallCdpAsync(call, arguments, cancellationToken).ConfigureAwait(false),
                EvaluateTool => await EvaluateAsync(call, arguments, cancellationToken).ConfigureAwait(false),
                NavigateTool => await NavigateAsync(call, arguments, cancellationToken).ConfigureAwait(false),
                ClickTool => await ClickAsync(call, arguments, cancellationToken).ConfigureAwait(false),
                TypeTool => await TypeAsync(call, arguments, cancellationToken).ConfigureAwait(false),
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
        return new BrowserUsePlugin(bridge);
    }
}
