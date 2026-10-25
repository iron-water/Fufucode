using Fufucode.Core;

namespace Fufucode.Pi;

/// <summary>
/// Pi/OMP RPC 进程的工厂：把 Core 组装的 <see cref="BackendProcessSpec"/> 翻译为
/// PiRpcOptions 并启动 <see cref="PiRpcClient"/>。Core 只通过 IRpcClientFactory 使用。
/// </summary>
public sealed class PiRpcClientFactory : IRpcClientFactory
{
    private readonly PiRpcOptions _options;

    public PiRpcClientFactory(BackendProcessSpec spec, RpcSessionOptions? sessionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        _options = new PiRpcOptions(
            spec.ExecutablePath,
            spec.Arguments,
            spec.WorkingDirectory,
            EnvironmentVariables: spec.EnvironmentVariables,
            RequireApprovalBridge: sessionOptions?.RequireApprovalBridge ?? false,
            UiDefaultTimeout: sessionOptions?.UiDefaultTimeout,
            PromptAcknowledgementTimeout: sessionOptions?.PromptAcknowledgementTimeout,
            ExtensionCommandAcknowledgementTimeout: sessionOptions?.ExtensionCommandAcknowledgementTimeout,
            DiagnosticSink: sessionOptions?.DiagnosticSink,
            Backend: spec.Backend);
    }

    public async Task<IRpcClient> StartAsync(CancellationToken cancellationToken = default) =>
        await PiRpcClient.StartAsync(_options, cancellationToken).ConfigureAwait(false);
}
