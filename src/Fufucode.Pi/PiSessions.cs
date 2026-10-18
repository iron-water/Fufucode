using Fufucode.Core;

namespace Fufucode.Pi;

/// <summary>
/// Pi/OMP 会话的便捷组合：把 PiRpcOptions 适配为 Core 的 IRpcClientFactory 并构造协调器/适配器。
/// Core 本身不引用本程序集；App 与测试只在需要 Pi 通道时显式引用这里。
/// </summary>
public static class PiSessions
{
    public static BackendProcessSpec ToSpec(this PiRpcOptions options) => new(
        options.ExecutablePath, options.Arguments, options.WorkingDirectory,
        options.EnvironmentVariables ?? new Dictionary<string, string>(), options.Backend);

    public static RpcSessionOptions ToSessionOptions(this PiRpcOptions options) => new(
        UiDefaultTimeout: options.UiDefaultTimeout,
        RequireApprovalBridge: options.RequireApprovalBridge,
        PromptAcknowledgementTimeout: options.PromptAcknowledgementTimeout,
        ExtensionCommandAcknowledgementTimeout: options.ExtensionCommandAcknowledgementTimeout,
        DiagnosticSink: options.DiagnosticSink);

    public static ChatSessionCoordinator Coordinator(PiRpcOptions options) =>
        new(new PiRpcClientFactory(options.ToSpec(), options.ToSessionOptions()), options.Backend,
            options.ToSessionOptions());

    public static Task<PiChatSessionAdapter> ConnectAsync(PiRpcOptions options,
        CancellationToken cancellationToken = default) =>
        PiChatSessionAdapter.ConnectAsync(new PiRpcClientFactory(options.ToSpec(), options.ToSessionOptions()),
            options.Backend, options.ToSessionOptions(), cancellationToken);
}
