using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core;

/// <summary>
/// 按会话配置创建 RPC 客户端。Core 不感知可执行文件、参数与凭据；
/// 这些由后端插件在实现本接口时决定。
/// </summary>
public interface IRpcClientFactory
{
    /// <summary>启动一个 RPC 客户端；失败时抛出并保证已分配的资源被释放。</summary>
    Task<IRpcClient> StartAsync(CancellationToken cancellationToken = default);
}

/// <summary>协调器需要的超时与诊断配置（与具体后端无关）。</summary>
public sealed record RpcSessionOptions(
    TimeSpan? UiDefaultTimeout = null,
    bool RequireApprovalBridge = false,
    TimeSpan? PromptAcknowledgementTimeout = null,
    TimeSpan? ExtensionCommandAcknowledgementTimeout = null,
    IPiDiagnosticSink? DiagnosticSink = null);
