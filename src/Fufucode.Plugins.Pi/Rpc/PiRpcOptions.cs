using Fufucode.Core;

namespace Fufucode.Pi;

public sealed record PiRpcOptions(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    int MaxRecordBytes = 8 * 1024 * 1024,
    int EventCapacity = 1024,
    TimeSpan? StartupTimeout = null,
    TimeSpan? ShutdownTimeout = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    TimeSpan? UiDefaultTimeout = null,
    bool RequireApprovalBridge = false,
    TimeSpan? PromptAcknowledgementTimeout = null,
    TimeSpan? ExtensionCommandAcknowledgementTimeout = null,
    IPiDiagnosticSink? DiagnosticSink = null,
    BackendId Backend = BackendId.Pi);
