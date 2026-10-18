using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core;

/// <summary>
/// RPC 内核的进程与管道抽象。Core 的会话协调器只依赖本接口；
/// 具体实现（Fufucode.Pi 的 PiRpcClient、OMP 适配器等）由后端提供。
/// </summary>
public interface IRpcClient : IAsyncDisposable
{
    /// <summary>一次启动/重启的代号；用于把进程退出事件绑定到正确的一代。</summary>
    Guid Generation { get; }

    /// <summary>进程退出观察（未退出时为 null）。</summary>
    RpcProcessExit? ProcessExit { get; }

    /// <summary>连接失败原因（未失败时为 null）。</summary>
    Exception? FailureReason { get; }

    /// <summary>进程退出码；未退出时为 null。</summary>
    int? ExitCode { get; }

    /// <summary>最近一次写往 stderr 的尾部文本（诊断用）。</summary>
    string StderrTail { get; }

    /// <summary>发送一条 RPC 命令并等待响应。</summary>
    Task<RpcResponse> RequestAsync(string command, JsonElement? fields = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>回答扩展 UI 请求。</summary>
    Task SendExtensionUiResponseAsync(string requestId, PiExtensionUiResponse response,
        CancellationToken cancellationToken = default);

    /// <summary>事件通道；协调器在后台持续泵出。</summary>
    System.Threading.Channels.ChannelReader<RpcEvent> Events { get; }

    /// <summary>停止进程并等待退出。</summary>
    Task StopAsync();
}
