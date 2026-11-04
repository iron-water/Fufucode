using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Plugins;

/// <summary>
/// 浏览器宿主桥：由 App 侧把插件能力接到真实 WebView2 页面与共享动作表。
/// 核心仅依赖此抽象基类，不直接引用或编译具体 BrowserUsePlugin 插件实现。
/// </summary>
public abstract class BrowserUseBridge
{
    /// <summary>解开旧宿主脚本返回的 JSON 字符串外层，保留真实 JSON 类型。</summary>
    public static string NormalizeScriptResult(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.String) return document.RootElement.GetRawText();
        var inner = document.RootElement.GetString() ?? "null";
        using var decoded = JsonDocument.Parse(inner);
        return decoded.RootElement.GetRawText();
    }

    /// <summary>只识别结果对象的错误字段，不把网页文本中的 error 单词当作失败。</summary>
    public static bool IsErrorResult(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            // 兼容仍返回 JSON 字符串包装的旧桥。
            if (root.ValueKind == JsonValueKind.String)
            {
                var inner = root.GetString();
                if (string.IsNullOrWhiteSpace(inner) || !inner.TrimStart().StartsWith("{", StringComparison.Ordinal)) return false;
                using var decoded = JsonDocument.Parse(inner);
                return HasError(decoded.RootElement);
            }
            return HasError(root);
        }
        catch (JsonException) { return false; } // 旧桥的成功说明允许为纯文本。
    }

    private static bool HasError(JsonElement root) => root.ValueKind == JsonValueKind.Object &&
        ((root.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False)) ||
         (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) ||
         (root.TryGetProperty("exceptionDetails", out var exception) && exception.ValueKind != JsonValueKind.Null));

    /// <summary>在匹配元素中输入文本（可选先清空）；默认实现返回“未实现”，不伪造成功。</summary>
    public virtual Task<string> TypeAsync(string selector, string text, bool clear, CancellationToken cancellationToken = default)
        => Task.FromResult("{\"error\":\"browser_type 未实现：当前桥不提供文本输入。\"}");
    /// <summary>调用 CDP 方法并返回原始 JSON 结果。</summary>
    public abstract Task<string?> CallCdpAsync(string method, string parametersJson,
        CancellationToken cancellationToken = default);

    /// <summary>在页面上下文执行 JavaScript 并返回结果。</summary>
    public abstract Task<string?> EvaluateAsync(string javaScript, CancellationToken cancellationToken = default);

    /// <summary>导航到指定地址；返回是否被受理。</summary>
    public abstract Task<bool> NavigateAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>点击匹配 CSS 选择器的元素；返回结果 JSON（成功或可读错误）。</summary>
    public virtual Task<string> ClickAsync(string selector, CancellationToken cancellationToken = default) =>
        Task.FromResult("{\"error\":\"宿主未实现 browser_click。\"}");

    /// <summary>截取当前可视区，返回包含 base64 图片的 JSON。</summary>
    public virtual Task<string> ScreenshotAsync(string? format = null, CancellationToken cancellationToken = default) =>
        Task.FromResult("{\"error\":\"宿主未实现 browser_screenshot。\"}");

    /// <summary>读取当前浏览器状态 JSON。</summary>
    public virtual Task<string> GetStateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult("{\"error\":\"宿主未实现 browser_get_state。\"}");

    /// <summary>按像素滚动页面。</summary>
    public virtual Task<string> ScrollAsync(int deltaX, int deltaY, CancellationToken cancellationToken = default) =>
        Task.FromResult("{\"error\":\"宿主未实现 browser_scroll。\"}");

    /// <summary>把鼠标悬停到匹配元素上。</summary>
    public virtual Task<string> HoverAsync(string selector, CancellationToken cancellationToken = default) =>
        Task.FromResult("{\"error\":\"宿主未实现 browser_hover。\"}");

    /// <summary>向当前焦点/指定元素派发按键。</summary>
    public virtual Task<string> KeyAsync(string key, string? selector = null, bool ctrl = false, bool shift = false,
        bool alt = false, bool meta = false, CancellationToken cancellationToken = default) =>
        Task.FromResult("{\"error\":\"宿主未实现 browser_key。\"}");

    /// <summary>选择下拉框选项。</summary>
    public virtual Task<string> SelectAsync(string selector, string value, CancellationToken cancellationToken = default) =>
        Task.FromResult("{\"error\":\"宿主未实现 browser_select。\"}");

    /// <summary>等待元素出现。</summary>
    public virtual Task<string> WaitAsync(string selector, int timeoutMs, CancellationToken cancellationToken = default) =>
        Task.FromResult("{\"error\":\"宿主未实现 browser_wait。\"}");

    /// <summary>读取页面 DOM 文本/HTML 快照。</summary>
    public virtual Task<string> GetDomAsync(string? selector, int maxText, int maxHtml, CancellationToken cancellationToken = default) =>
        Task.FromResult("{\"error\":\"宿主未实现 browser_get_dom。\"}");
}
