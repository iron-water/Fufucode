using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Plugins;

/// <summary>
/// Seep 逆向工程工作台的插件生命周期协调器：把插件市场的状态变化翻译成
/// 「启动 MCP 子进程 + 注册 23 工具」或「注销工具 + 终止子进程 + 停止中继」。
///
/// 关键约束（幽灵工具防线）：
/// - 只有 <c>IsActive</c>（已安装 **且** 已启用）才启动子进程并注册工具；
/// - 停用/卸载时先 <see cref="PluginToolRegistry.Unregister"/> 再终止子进程：
///   两道闸都到位后，工具名不再出现在 <see cref="PluginToolRegistry.ActiveToolNames"/> 中；
/// - 工具清单来自真实 <c>tools/list</c>，服务端握手失败则不注册任何工具，
///   也不会留下半套注册（启动失败即不注册）；
/// - 子进程未启动时不再重复启动；重复启用是幂等的。
/// </summary>
public sealed class SeepReverseLabPluginHost : IAsyncDisposable
{
    private readonly PluginStateStore _store;
    private readonly PluginToolRegistry _registry;
    private readonly SeepMcpClientBridge _bridge;
    private readonly ReverseLabRelayHost _relay;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _gate = new();

    private bool _relayEnabled;
    private bool _disposed;
    private Task _pending = Task.CompletedTask;

    public SeepReverseLabPluginHost(
        PluginStateStore store,
        PluginToolRegistry registry,
        SeepMcpClientBridge? bridge = null,
        ReverseLabRelayHost? relay = null,
        bool relayEnabled = true)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _bridge = bridge ?? new SeepMcpClientBridge();
        _relay = relay ?? new ReverseLabRelayHost();
        _relayEnabled = relayEnabled;

        // 桥提前注册进调度表：那时它还没有工具契约（子进程未启动），
        // 好处是“未安装/未启用时被拒”能给出真实原因，而不是泛化的“未注册”。
        // 工具契约在 tools/list 成功后由 Refresh() 动态补上。
        _registry.Register(_bridge);
        _store.Changed += OnStoreChanged;
        // 设置页等其它 PluginStateStore 实例写入后广播：先刷新本实例快照，再对齐运行时。
        PluginStateNotifier.Changed += OnNotifiedChanged;
    }

    /// <summary>插件 ID。</summary>
    public const string PluginId = SeepReverseLabAssets.PluginId;

    /// <summary>MCP 客户端桥；定向测试可直接读取其真实状态。</summary>
    public SeepMcpClientBridge Bridge => _bridge;

    /// <summary>合规转译中继宿主。</summary>
    public ReverseLabRelayHost Relay => _relay;

    /// <summary>
    /// 是否在插件启用时联动启动合规转译中继。默认 true；
    /// 置 false 时插件只提供工具链与人格，不占用 17890 端口。
    /// </summary>
    public bool RelayEnabled
    {
        get => _relayEnabled;
        set => _relayEnabled = value;
    }

    /// <summary>
    /// 按当前插件状态对齐运行时：已启用则启动并注册，未启用则停止并注销。
    /// 宿主在组合根初始化后与每次状态变化时调用；等待本次对齐完成再返回。
    /// </summary>
    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Task pending;
        lock (_gate) pending = _pending;
        // 先把上一次对齐排干，避免两次状态变化交叉执行注册/注销。
        try { await pending.ConfigureAwait(false); } catch (Exception) { }

        var task = RunSynchronizeAsync(cancellationToken);
        lock (_gate) _pending = task;
        await task.ConfigureAwait(false);
    }

    private async Task RunSynchronizeAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_store.IsActive(PluginId))
                await ActivateAsync(cancellationToken).ConfigureAwait(false);
            else
                await DeactivateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task ActivateAsync(CancellationToken cancellationToken)
    {
        var registrations = await _bridge.StartAsync(cancellationToken).ConfigureAwait(false);
        if (registrations.Count == 0)
        {
            // 子进程启动失败或工作台未部署：不注册任何工具契约（工具清单为空即不会出现），
            // 但保留处理器在调度表里，使调用被拒时能给出“工作台未部署”等真实原因。
            _registry.Refresh();
            return;
        }

        // ActivateAsync 在生命周期闸内串行执行；此处不会与其它注册竞争。
        _registry.Refresh();

        if (_relayEnabled) await _relay.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DeactivateAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        await _bridge.StopAsync().ConfigureAwait(false);
        // 停用/卸载后清空工具契约；处理器本身仍认领 seep_ 名字，使拒绝原因指向真实状态。
        _registry.Refresh();
        await _relay.StopAsync().ConfigureAwait(false);
    }

    private void OnStoreChanged()
    {
        if (_disposed) return;
        Schedule();
    }

    /// <summary>
    /// 其它实例写入 plugins.json 时：先 reload 本实例快照（读到真实最新状态），再对齐运行时。
    /// </summary>
    private void OnNotifiedChanged()
    {
        if (_disposed) return;
        // Reload 检测到差异时会触发本实例的 Changed（进而 Schedule）；
        // 没有差异说明本次写入来自本实例或与本次插件无关，无需重复对齐。
        _store.Reload();
    }

    private void Schedule()
    {
        // Changed 是同步事件：这里只投递对齐任务，绝不在事件回调里阻塞等待子进程。
        var task = RunSynchronizeAsync(CancellationToken.None);
        lock (_gate) _pending = task;
        _ = task.ContinueWith(
            t => System.Diagnostics.Trace.TraceError("Seep plugin synchronize: {0}", t.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>等待最近一次状态变化触发的对齐完成；宿主收尾与定向测试使用。</summary>
    public Task WaitForPendingAsync()
    {
        Task pending;
        lock (_gate) pending = _pending;
        return pending;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }
        _store.Changed -= OnStoreChanged;
        PluginStateNotifier.Changed -= OnNotifiedChanged;
        await WaitForPendingAsync().ConfigureAwait(false);
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _bridge.StopAsync().ConfigureAwait(false);
            _registry.Unregister(_bridge);
            await _relay.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
            lock (_gate) _disposed = true;
            _lifecycleGate.Dispose();
            await _bridge.DisposeAsync().ConfigureAwait(false);
            await _relay.DisposeAsync().ConfigureAwait(false);
        }
    }
}
