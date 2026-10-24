using System;
using Fufucode.App.Features.Plugins;
using Fufucode.Core.Diagnostics;
using Fufucode.Core.Diagnostics.Perf;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Features.Performance;

/// <summary>
/// 性能监控插件宿主侧组合根：创建采集器与宿主桥，并把服务解析器接到页面宿主管理器上。
/// 页面内容、设置视图、导出动作都在插件内完成；宿主只负责交付能力与放置页面。
/// </summary>
internal sealed class PerfHostWiring : IDisposable
{
    internal static PerfHostWiring? Current { get; private set; }

    public PerfHostCollector Collector { get; }
    public PerfHostBridge Bridge { get; }
    /// <summary>本窗口实测的 UI 批处理耗时（毫秒）；插件停用时也保留。</summary>
    public double LastUiBatchMs { get; private set; }

    /// <summary>当前项目路径：插件激活时随上下文传入。</summary>
    public string? PluginProjectPath
    {
        get => _pluginProjectPath;
        set => _pluginProjectPath = value;
    }

    private string? _pluginProjectPath;
    private bool _disposed;

    private PerfHostWiring(PerfHostCollector collector, PerfHostBridge bridge)
    {
        Collector = collector;
        Bridge = bridge;
    }

    public static PerfHostWiring Create(string dataRoot)
    {
        var collector = new PerfHostCollector(dataRoot);
        // 异常处理器是进程级的：每个窗口都会调用，重复安装会让同一异常被上报多次。
        if (Current is null) collector.InstallExceptionHandlers();
        var bridge = PerfHostBridge.Create();
        var wiring = new PerfHostWiring(collector, bridge);
        Current = wiring;
        return wiring;
    }

    /// <summary>把宿主服务交给由本对象管理的插件管理器。</summary>
    public void Attach(PluginPackageManager? manager)
    {
        // 调用发生时页面宿主字段尚未赋值（构造器中），因此管理器由页面宿主在 Start 后再接一次。
        if (manager is null) return;
        _manager = manager;
        var priorResolver = manager.ServiceResolver;
        manager.ServiceResolver = type =>
        {
            if (type == typeof(IPerfHostServices))
            {
                Bridge.MarkWired();
                return Bridge;
            }
            return priorResolver?.Invoke(type);
        };
        // 管理器在构造时读过状态快照（插件可能在之后才登记安装）：重读磁盘并补一次激活扫描。
        manager.StateStore.Reload();
        manager.InitializeInstalledPlugins(_pluginProjectPath);
    }

    /// <summary>
    /// 页面宿主即将重新同步时调用：让已激活的插件重新构造页面贡献（此时服务解析器已就位）。
    /// 由插件页宿主在 UI 线程驱动，页面注册仍然走宿主正常的 Sync 路径。
    /// </summary>
    public void PrepareForResync()
    {
        _manager?.InitializeInstalledPlugins(_pluginProjectPath);
        _manager?.RequestPageResync();
    }

    private PluginPackageManager? _manager;

    public void AttachSession(Fufucode.App.Abstractions.IChatSession? session) => Collector.AttachSession(session);

    /// <summary>插件启停后：同步接收端状态；插件刚启用时补发启动阶段的里程碑。</summary>
    public void OnPluginsSynced()
    {
        var active = PerfTracer.Sink is not null;
        Collector.SetSinkActive(active);
        if (active) Program.Startup?.ReplayToSink();
    }

    /// <summary>时间线批处理耗时（由 TimelineViewModel 在每批结束时调用）。</summary>
    public void RecordUiBatch(double milliseconds)
    {
        LastUiBatchMs = milliseconds;
        Collector.RecordUiBatch(milliseconds);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(Current, this)) Current = null;
        Bridge.Dispose();
        Collector.Dispose();
    }
}
