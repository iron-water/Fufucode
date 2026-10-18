using System;

namespace Fufucode.Core;

/// <summary>Safe diagnostic metadata. Raw RPC payloads and exception messages are excluded.</summary>
/// <param name="Outcome">Bounded result of an extension UI dialog: value/confirmed/declined/cancelled/expired/closed/notified/ignored.</param>
public sealed record PiDiagnosticEvent(DateTimeOffset Timestamp, string EventType, string? RequestId,
    string? Command, long? DurationMs, int? ExitCode, string? ErrorCode, string? Outcome = null);

/// <summary>后端诊断汇聚入口（保留历史类型名 PiDiagnosticEvent，schema 与日志文件兼容）。</summary>
public interface IPiDiagnosticSink
{
    void Record(PiDiagnosticEvent entry);
}

/// <summary>中立别名：新代码用 IBackendDiagnosticSink，旧的 IPiDiagnosticSink 继续可用。</summary>
public interface IBackendDiagnosticSink : IPiDiagnosticSink
{
}
