using System;
using Fufucode.App.Features.Plugins;
using Fufucode.Core.Diagnostics;
using Fufucode.Core.Diagnostics.Perf;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Features.Performance;

/// <summary>
/// 性能监控插件的宿主侧接线（页面贡献）：
/// 1. 实现 <see cref="IPerfHostServices"/> 与 <see cref="IPerfHostBridge"/>，交给插件创建页面；
/// 2. 页面内容由插件提供，宿主只把它包装成停靠页契约。
/// 宿主指标的上报由各窗口自己的 <see cref="PerfHostCollector"/> 负责，不经过本桥。
/// 宿主只依赖 Core 契约，不引用插件程序集。
/// </summary>
internal sealed class PerfHostBridge : IPerfHostServices, IPerfHostBridge
{
    internal static PerfHostBridge? Current { get; private set; }

    private PerfHostBridge() { }

    public IPerfHostBridge Bridge => this;

    /// <summary>插件登记进来的设置/页面对象。</summary>
    public IPerfPageHost? Page { get; private set; }

    /// <summary>插件初始化时调用过服务解析器的时间（用于诊断“为什么没有性能页”）。</summary>
    public DateTimeOffset? WiredUtc { get; private set; }

    /// <summary>最近一次插件页面交付失败的原因；正常为 null。</summary>
    public string? LastError { get; private set; }

    public IPerfAnomalySource? Anomalies => Page as IPerfAnomalySource;

    /// <summary>
    /// 进程级单例：同一进程只保留一个宿主桥。多个窗口（主窗口、重启窗口、无头预览）各自调用时复用，
    /// 避免后创建者覆盖前者，使先创建窗口的页面与设置丢失。
    /// </summary>
    public static PerfHostBridge Create()
    {
        if (Current is { } existing)
        {
            PerfHostAccess.Set(existing);
            return existing;
        }
        var bridge = new PerfHostBridge();
        Current = bridge;
        // 同一程序集身份下的静态入口：插件的 InitializeAsync 通过它取到宿主桥。
        PerfHostAccess.Set(bridge);
        return bridge;
    }

    /// <summary>
    /// 插件创建页面时调用。页面内容来自插件自己的 <c>IPerfPageViewSource</c>（在 RegisterSettings 时登记），
    /// 宿主只把它包装成停靠页契约。
    /// </summary>
    public object? CreatePageView()
    {
        var source = Page as IPerfPageViewSource;
        if (source is null)
        {
            LastError = Page is null ? "插件没有登记设置对象。" : "插件设置对象未提供页面内容。";
            return null;
        }
        return new PluginPageView("perf", "性能", "Speedometer", "performance.monitor",
            createPluginPage: () => source.PageContent);
    }

    /// <summary>插件登记设置对象（设置页内容、导出动作、异常摘要）。</summary>
    public void RegisterSettings(object? settings)
    {
        Page = settings as IPerfPageHost;
        LastError = Page is null ? "插件交付的设置对象没有实现 IPerfPageHost。" : null;
    }

    /// <summary>服务解析器被插件调用：记为接通。</summary>
    public void MarkWired() => WiredUtc ??= DateTimeOffset.UtcNow;

    /// <summary>执行导出 / 打开目录 / 清空。</summary>
    public string Execute(PerfUiRequest request) =>
        Page?.Execute(request) ?? "未安装或未启用性能监控插件。";

    /// <summary>异常摘要（命令面板入口使用）；插件未启用时为 null。</summary>
    public IPerfAnomalySource? AnomalySource => Page as IPerfAnomalySource;

    /// <summary>插件停止：撤销服务，采集器停止宿主指标上报（回合聚合继续）。</summary>
    public void Dispose()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        PerfHostAccess.Set(null);
        Page = null;
        WiredUtc = null;
        LastError = null;
    }
}
