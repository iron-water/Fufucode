using System.Text.Json;

namespace Fufucode.Core;

/// <summary>
/// 会话协调器的统一契约。UI 与视图模型只依赖本接口：
/// Pi/OMP 的 RPC 协调器与 FUMO 原生协调器都实现同一组会话能力，
/// 调用方不再知道当前会话走的是外部进程还是纯 C# 引擎。
/// </summary>
public interface ISessionCoordinator : IAsyncDisposable
{
    /// <summary>会话后端标识（持久化口径）。</summary>
    BackendId Backend { get; }

    /// <summary>后端能力清单。</summary>
    BackendCapabilities Capabilities { get; }

    /// <summary>当前会话快照（连接、活动、消息、工具、排队、审批、错误）。</summary>
    SessionSnapshot Snapshot { get; }

    /// <summary>快照变化通知；可能在非 UI 线程触发。</summary>
    event Action<SessionSnapshot>? SnapshotChanged;

    /// <summary>扩展 UI 对话框请求（Pi 通道使用；FUMO 不产生）。</summary>
    event Action<ExtensionUiRequest>? ExtensionUiRequestReceived;
    event Action<ExtensionUiRequest>? ExtensionUiRequestClosed;
    event Action<PiExtensionErrorEvent>? ExtensionError;

    /// <summary>审批桥状态文本（FUMO 恒为 NotApplicable 语义）。</summary>
    ApprovalAvailability ApprovalAvailability { get; }
    string? ApprovalDiagnostic { get; }

    /// <summary>是否正在进行安全维护（重启/扩展变更）。</summary>
    bool IsInIdleMaintenance { get; }

    /// <summary>是否允许提交新 prompt。</summary>
    bool CanSendPrompt { get; }

    /// <summary>当前会话累计用量。</summary>
    SessionUsageReport GetUsage();

    /// <summary>最近一次进程退出（FUMO 恒为 null）。</summary>
    RpcProcessExit? LastProcessExit { get; }

    /// <summary>启动会话（FUMO 为幂等的空操作）。</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>发送 prompt；在命令被接受时返回，不等待模型运行结束。</summary>
    Task SendPromptAsync(string message, PromptDelivery delivery = PromptDelivery.Immediate,
        TimeSpan? acknowledgementTimeout = null, CancellationToken cancellationToken = default,
        IReadOnlyList<PiPromptImage>? images = null);

    /// <summary>清空队列并中止进行中的运行。</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>关闭底层连接（FUMO 释放引擎）。</summary>
    Task CloseAsync();

    /// <summary>兼容脚本/诊断的会话状态查询。</summary>
    Task<RpcResponse> QueryStateAsync(CancellationToken cancellationToken = default);
    Task<RpcResponse> QueryAvailableModelsAsync(CancellationToken cancellationToken = default);
    Task<RpcResponse> SetModelAsync(string provider, string modelId, CancellationToken cancellationToken = default);
    Task<RpcResponse> SetThinkingLevelAsync(string level, CancellationToken cancellationToken = default);
    Task<ThinkingLevelAvailability> QueryThinkingLevelsAsync(CancellationToken cancellationToken = default);
    Task<RpcResponse> QueryCommandsAsync(CancellationToken cancellationToken = default);
    Task<RpcResponse> QueryEntriesAsync(string? since = null, CancellationToken cancellationToken = default);
    Task<RpcResponse> SetSessionNameAsync(string name, CancellationToken cancellationToken = default);
    Task<RpcResponse> ForkAsync(string entryId, CancellationToken cancellationToken = default);
    Task SetExecutionModeAsync(string mode, CancellationToken cancellationToken = default);
    Task RespondToExtensionUiAsync(ExtensionUiRequest request, PiExtensionUiResponse response,
        CancellationToken cancellationToken = default);

    /// <summary>从持久历史恢复时间线。</summary>
    void RestoreHistory(SessionHistory history, bool previousRunInterrupted);

    /// <summary>原子保留空闲连接供安全重启。</summary>
    IDisposable BeginIdleMaintenance();
}
