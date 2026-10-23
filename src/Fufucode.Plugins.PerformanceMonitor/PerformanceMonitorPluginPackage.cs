using Fufucode.Core.Diagnostics;
using Fufucode.Core.Diagnostics.Perf;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// 性能监控插件包入口：
/// <list type="number">
/// <item><c>InitializeAsync</c>：建立 <see cref="PerfRecorder"/> 并安装为 <see cref="PerfTracer"/> 接收端；
///       通过宿主服务取得页面工厂，并在创建页面时把设置对象交给宿主。</item>
/// <item><c>CreatePageContribution</c>：返回“性能”停靠页。</item>
/// <item><c>ShutdownAsync</c>：移除接收端并刷新 JSONL；之后埋点开销回到一次 null 判断。</item>
/// </list>
/// </summary>
public sealed class PerformanceMonitorPluginPackage : IPluginPackage, IPerfPluginPackage
{
    public const string PluginId = "performance.monitor";
    public const string PageId = "perf";

    private PerfRecorder? _recorder;
    private object? _page;
    private PerfPageHost? _host;
    private IPerfHostBridge? _bridge;
    private string? _dataRoot;
    /// <summary>共享计数：两个管理器各自从默认加载上下文创建包实例，计数必须跨实例共享。</summary>
    private static int s_instances;
    private static object? s_page;
    private static int s_pageOwners;


    public string Id => PluginId;
    public string Version => "1.0.0";

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public Task InitializeAsync(PluginPackageContext context, CancellationToken cancellationToken = default)
    {
        _dataRoot = string.IsNullOrEmpty(context.DataRoot)
            ? Path.GetDirectoryName(context.PackageDirectory) ?? context.PackageDirectory
            : context.DataRoot;

        // 宿主服务：测试或 CLI 环境没有宿主时为 null，接收端照常工作，只是没有页面。
        _bridge = ResolveBridge(context);
        var settings = PerfSettings.Load(_dataRoot);
        var recorder = new PerfRecorder(settings, Path.Combine(_dataRoot, "perf"));
        // 宿主样本（回合耗时、进程指标）即时追加落盘，并在内存留一份给概览/时间线；不依赖页面是否打开。
        PerfHostSamples.Subscribe(new HostSampleSink(Path.Combine(_dataRoot, "perf"), recorder));
        // 同一进程里可能有两个插件管理器（页面宿主与工具宿主）各加载一份本程序集：
        // 第一个接收端获胜，其余按程序集身份判定为同一插件而沿用，只有真正不同的实现才失败。
        if (PerfOwnership.TryInstall(recorder, recorder) is { } failure)
        {
            recorder.Dispose();
            throw new InvalidOperationException(failure);
        }
        // 本实例也已激活（无论是否拥有接收端），供 Shutdown 计数。
        Interlocked.Increment(ref s_instances);
        if (PerfTracer.Sink is PerfRecorder owner) _recorder = owner;
        else _recorder = recorder;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        // 两个管理器共享同一个包实例时，等最后一个停用再移除接收端。
        if (Interlocked.Decrement(ref s_instances) > 0) return Task.CompletedTask;
        Interlocked.Exchange(ref s_instances, 0);
        PerfHostSamples.Subscribe(null);
        var recorder = _recorder;
        _recorder = null;
        (_page as IDisposable)?.Dispose();
        _page = null;
        _host = null;
        _bridge = null;
        if (recorder is not null)
        {
            PerfOwnership.TryUninstall(recorder);
            recorder.Dispose();
        }
        return Task.CompletedTask;
    }

    public PluginPageContribution? CreatePageContribution(PluginPackageContext context)
    {
        var recorder = _recorder;
        var bridge = _bridge;
        if (recorder is null || string.IsNullOrEmpty(_dataRoot) || bridge is null) return null;
        var page = _page as PerfDockPage;
        if (page is null) _page = page = new PerfDockPage(recorder, _dataRoot);
        _host ??= new PerfPageHost(recorder, _dataRoot, () => page.Content);
        bridge.RegisterSettings(_host);
        var content = page.Content;
        // 多个插件管理器（页面宿主 + 浏览器工具宿主）会各自激活本插件并各建一份页面；
        // 只保留一个共享页面实例，否则同一控件会被挂到两处。
        if (s_page is Avalonia.Controls.Control shared) content = shared;
        else s_page = content;
        Interlocked.Increment(ref s_pageOwners);
        return new PluginPageContribution(
            PageId: PageId,
            Title: "性能",
            IconKey: "Speedometer",
            ModuleId: PluginId,
            // 每次调用都回到宿主桥取页面提供者：它实现宿主侧 IPluginPageProvider，宿主据此得到停靠页契约。
            CreateView: () => bridge.CreatePageView() ?? content,
            DetachView: _ =>
            {
                // 只在最后一个拥有者释放时停掉页面计时器；多个管理器可能同时持有本贡献。
                if (Interlocked.Decrement(ref s_pageOwners) <= 0)
                {
                    Interlocked.Exchange(ref s_pageOwners, 0);
                    (_page as IDisposable)?.Dispose();
                }
            });
    }

    /// <summary>
    /// 宿主样本 → JSONL：每 2 秒若干行，直接同步追加；与页面和异步写入器解耦。
    /// </summary>
    private sealed class HostSampleSink(string directory, PerfRecorder recorder) : IPerfHostSampleSink
    {
        private readonly object _gate = new();

        public void Publish(PerfHostSample sample)
        {
            recorder.AddHostSample(sample);
            try
            {
                lock (_gate)
                {
                    Directory.CreateDirectory(directory);
                    File.AppendAllText(Path.Combine(directory, $"host-{DateTime.UtcNow:yyyyMMdd}.jsonl"),
                        System.Text.Json.JsonSerializer.Serialize(sample, PerfHostSampleJsonContext.Default.PerfHostSample) + "\n");
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // 磁盘不可写：放弃本样本，不影响运行。
            }
        }
    }

    /// <summary>
    /// 取宿主服务。宿主类型不可用（例如无界面测试环境缺少 Avalonia 程序集）时退化为 null：
    /// 接收端照常工作，只是没有停靠页；不因缺少界面依赖让整个插件激活失败。
    /// </summary>
    private static IPerfHostBridge? ResolveBridge(PluginPackageContext context)
    {
        try { return context.GetService<IPerfHostServices>()?.Bridge ?? PerfHostAccess.Bridge; }
        catch (Exception error) when (error is TypeLoadException or FileNotFoundException or FileLoadException
                                         or MissingMethodException)
        {
            return null;
        }
    }
}
