namespace Fufucode.Pi;

/// <summary>Safe diagnostic metadata. Raw RPC payloads and exception messages are excluded.</summary>
/// <param name="Outcome">Bounded result of an extension UI dialog: value/confirmed/declined/cancelled/expired/closed/notified/ignored.</param>
public sealed record PiDiagnosticEvent(DateTimeOffset Timestamp, string EventType, string? RequestId,
    string? Command, long? DurationMs, int? ExitCode, string? ErrorCode, string? Outcome = null);

public interface IPiDiagnosticSink
{
    void Record(PiDiagnosticEvent entry);
}
