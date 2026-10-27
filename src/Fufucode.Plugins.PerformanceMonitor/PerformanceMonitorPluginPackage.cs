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
    /// <summary>本实例是否真正持有进程级接收端；只有持有者提供页面与设置。</summary>
    private bool _ownsSink;
    private object? _page;
    private PerfPageHost? _host;
    private IPerfHostBridge? _bridge;
    private string? _dataRoot;
    /// <summary>共享计数：两个管理器各自从默认加载上下文创建包实例，计数必须跨实例共享。</summary>
    private static int s_instances;
    /// <summary>进程内唯一的页面与宿主；两个管理器共用同一实例，避免一份被释放、另一份仍在显示。</summary>
    private static PerfDockPage? s_sharedPage;
    private static Avalonia.Controls.Control? s_sharedContent;
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
        // 同一进程里可能有两个插件管理器（页面宿主与工具宿主）各加载一份本程序集：
        // 第一个接收端获胜，其余按程序集身份判定为同一插件而沿用，只有真正不同的实现才失败。
        if (PerfOwnership.TryInstall(recorder, recorder) is { } failure)
        {
            recorder.Dispose();
            throw new InvalidOperationException(failure);
        }
        // 本实例也已激活（无论是否拥有接收端），供 Shutdown 计数。
        Interlocked.Increment(ref s_instances);
        // 注意：接收端可能来自另一个 AssemblyLoadContext，其 PerfRecorder 与本实例的类型
        // 不是同一 Type（即使 DLL 路径相同）。因此只按“本实例是否成功安装”判断所有权，
        // 不能用 `is PerfRecorder` 判断；否则会拿到一个永远收不到数据的本地记录器。
        _ownsSink = ReferenceEquals(PerfTracer.Sink, recorder);
        if (!_ownsSink) recorder.Dispose();
        _recorder = recorder;
        // 只有接收端持有者可以订阅宿主样本：非持有者覆盖订阅会让持有者停止接收。
        if (_ownsSink) PerfHostSamples.Subscribe(new HostSampleSink(Path.Combine(_dataRoot, "perf"), recorder));
        // 不在这里创建页面：插件初始化可能运行在非 UI 线程的管理器上，Avalonia 控件必须延迟到页面请求时创建。
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        // 两个管理器共享同一个包实例时，等最后一个停用再移除接收端。
        if (Interlocked.Decrement(ref s_instances) > 0) return Task.CompletedTask;
        Interlocked.Exchange(ref s_instances, 0);
        if (!_ownsSink)
        {
            // 非持有者不能拆除别人安装的接收端、宿主样本订阅或页面。
            _page = null;
            _host = null;
            _bridge = null;
            return Task.CompletedTask;
        }
        PerfHostSamples.Subscribe(null);
        var recorder = _recorder;
        _recorder = null;
        // 页面只在最后一个插件实例停用时释放；中途停掉某个管理器不能停掉仍在显示的控件。
        s_sharedPage?.Dispose();
        s_sharedPage = null;
        s_sharedContent = null;
        Interlocked.Exchange(ref s_pageOwners, 0);
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

        if (!_ownsSink)
        {
            // 接收端在另一个加载上下文里：复用它的页面与设置宿主（都通过 Core 契约跨上下文），
            // 绝不能拿本地这个收不到数据的记录器再建一份页面。
            PerfDockPage? fallback = null;
            // 接收端由另一个加载上下文持有，但共享的 Core 契约可以跨上下文读取它的数据。
            var remote = PerfTracer.Sink as IPerfDataSource;
            return new PluginPageContribution(
                PageId: PageId,
                Title: "性能",
                IconKey: "Speedometer",
                ModuleId: PluginId,
                CreateView: () => bridge.CreatePageView() ??
                                 (fallback ??= new PerfDockPage(recorder, _dataRoot, remote)).Content,
                DetachView: _ => (fallback as IDisposable)?.Dispose());
        }

        // 持有接收端的实例独占页面、设置宿主与导出能力。
        s_sharedPage ??= new PerfDockPage(recorder, _dataRoot);
        s_sharedContent ??= s_sharedPage.Content;
        _page = s_sharedPage;
        _host ??= new PerfPageHost(recorder, _dataRoot, () => s_sharedContent!);
        bridge.RegisterSettings(_host);
        var content = s_sharedContent;
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
                // 页面生命周期由最后一个插件实例的 ShutdownAsync 统一结束，这里只归还引用计数。
                if (Interlocked.Decrement(ref s_pageOwners) < 0)
                    Interlocked.Exchange(ref s_pageOwners, 0);
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
