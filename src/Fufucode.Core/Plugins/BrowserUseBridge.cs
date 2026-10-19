using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Plugins;

/// <summary>
/// 浏览器宿主桥：由 App 侧把插件能力接到真实 WebView2 页面与共享动作表。
/// 核心仅依赖此抽象基类，不直接引用或编译具体 BrowserUsePlugin 插件实现。
/// </summary>
public abstract class BrowserUseBridge
{
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
}
