using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Plugins;

/// <summary>合规转译中继的运行状态；每一项都对应真实探测结果，不用推测填充。</summary>
public enum RelayHostState
{
    /// <summary>尚未启动（插件未启用或宿主未接线）。</summary>
    Stopped,

    /// <summary>由本插件启动并已监听。</summary>
    Running,

    /// <summary>端口已被外部进程占用：本插件不接管、不终止该进程，如实报告。</summary>
    PortOccupiedExternally,

    /// <summary>工作台脚本缺失，无法启动。</summary>
    NotDeployed,

    /// <summary>启动失败（解释器缺失、脚本报错等）。</summary>
    Failed,
}

/// <summary>一次中继启动尝试的结果。</summary>
public sealed record RelayStartResult(RelayHostState State, string Reason)
{
    /// <summary>127.0.0.1:17890 上是否真的可用转发（本插件启动或外部已在监听均可）。</summary>
    public bool IsForwardingAvailable =>
        State is RelayHostState.Running or RelayHostState.PortOccupiedExternally;
}

/// <summary>
/// 合规转译中继宿主桥（ReverseLabRelayHost）：
/// 管理 <c>Tool/scripts/compliance-relay/relay.py</c> 的启停，为会话模型请求提供
/// <c>127.0.0.1:17890</c> 的本地合规转译转发支撑。
///
/// 边界（与插件市场状态对齐）：
/// - 插件未“已安装且已启用”时本宿主不启动任何进程；
/// - 插件停用/卸载时终止**本宿主自己启动的**中继进程；
/// - 端口若已被外部进程占用（例如用户手动运行的中继），本宿主只如实报告，
///   既不接管也不终止，避免误杀用户进程；
/// - 工作台未部署时返回 <see cref="RelayHostState.NotDeployed"/> 并给出缺失路径。
/// </summary>
public sealed class ReverseLabRelayHost : IAsyncDisposable
{
    private readonly ISeepProcessFactory _factory;
    private readonly SeepProcessSpec _processSpec;
    private readonly object _gate = new();
    private ISeepProcess? _process;
    private bool _disposed;

    public ReverseLabRelayHost(
        int port = SeepReverseLabAssets.RelayPort,
        string? labRoot = null,
        ISeepProcessFactory? processFactory = null)
    {
        Port = port;
        LabRoot = SeepReverseLabAssets.ResolveLabRoot(labRoot);
        _processSpec = SeepReverseLabAssets.CreateRelayProcessSpec(port, LabRoot);
        _factory = processFactory ?? new SeepProcessFactory();
    }

    /// <summary>监听端口（默认 17890）。</summary>
    public int Port { get; }

    /// <summary>工作台根目录。</summary>
    public string LabRoot { get; }

    /// <summary>转译中继脚本绝对路径。</summary>
    public string RelayScriptPath => SeepReverseLabAssets.CreateRelayProcessSpec(Port, LabRoot).Arguments[0];

    /// <summary>当前状态。</summary>
    public RelayHostState State { get; private set; } = RelayHostState.Stopped;

    /// <summary>最近一次状态说明（含失败原因或外部占用说明）。</summary>
    public string Status { get; private set; } = "未启动。";

    /// <summary>本宿主是否持有正在运行的中继子进程。</summary>
    public bool OwnsProcess
    {
        get
        {
            lock (_gate) return _process is { HasExited: false };
        }
    }

    /// <summary>127.0.0.1:<see cref="Port"/> 当前是否真的可接受连接。</summary>
    public bool IsListening() => ProbePort(Port, TimeSpan.FromMilliseconds(400));

    /// <summary>
    /// 启动中继（按需）。已在监听（无论本宿主启动还是外部）时不会重复启动；
    /// 工作台未部署或启动失败时如实返回，不抛异常打断插件启用流程。
    /// </summary>
    public async Task<RelayStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_process is { HasExited: false })
            {
                State = RelayHostState.Running;
                Status = $"本插件启动的中继正在监听 127.0.0.1:{Port}。";
                return new RelayStartResult(State, Status);
            }
        }

        var probe = SeepReverseLabAssets.Probe(LabRoot);
        if (!probe.RelayAvailable)
        {
            State = RelayHostState.NotDeployed;
            Status = $"未启动合规转译中继：{probe.Describe()}";
            return new RelayStartResult(State, Status);
        }

        // 端口已被外部进程占用：只报告，不接管、不终止用户进程。
        if (await WaitForPortAsync(Port, TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false))
        {
            State = RelayHostState.PortOccupiedExternally;
            Status = $"127.0.0.1:{Port} 已被外部进程占用，按外部中继可用处理（本插件不接管、不终止该进程）。";
            return new RelayStartResult(State, Status);
        }

        try
        {
            var process = _factory.Start(_processSpec);
            lock (_gate) _process = process;

            // 等子进程真的开始监听再判定成功；只确认“进程存在”不算转发可用。
            if (!await WaitForPortAsync(Port, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false))
            {
                StopCore();
                State = RelayHostState.Failed;
                Status = $"合规转译中继启动后未在 127.0.0.1:{Port} 监听（已回收子进程）。";
                return new RelayStartResult(State, Status);
            }

            State = RelayHostState.Running;
            Status = $"合规转译中继已启动并监听 127.0.0.1:{Port}。";
            return new RelayStartResult(State, Status);
        }
        catch (OperationCanceledException)
        {
            StopCore();
            throw;
        }
        catch (Exception error)
        {
            StopCore();
            State = RelayHostState.Failed;
            Status = $"启动合规转译中继失败：{error.Message}";
            return new RelayStartResult(State, Status);
        }
    }

    /// <summary>终止本宿主启动的中继进程；幂等。不触碰外部占用的端口。已退出后状态为 Stopped。</summary>
    public Task StopAsync()
    {
        StopCore();
        State = RelayHostState.Stopped;
        Status = "已停止（本插件启动的中继子进程已回收）。";
        return Task.CompletedTask;
    }

    private void StopCore()
    {
        ISeepProcess? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }
        if (process is null) return;
        try { process.Dispose(); }
        catch (Exception error) { Status = $"回收中继子进程时发生异常：{error.GetType().Name}"; }
    }

    private static async Task<bool> WaitForPortAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ProbePort(port, TimeSpan.FromMilliseconds(300))) return true;
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
        return ProbePort(port, TimeSpan.FromMilliseconds(300));
    }

    private static bool ProbePort(int port, TimeSpan timeout)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync("127.0.0.1", port);
            return connect.Wait(timeout) && client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }
        await StopAsync().ConfigureAwait(false);
        lock (_gate) _disposed = true;
    }
}
