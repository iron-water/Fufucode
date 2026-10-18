using Fufucode.Claude;
using Fufucode.Core;

namespace Fufucode.Plugins.Claude;

/// <summary>
/// Claude 适配器的诊断接口 → Core 的统一诊断日志。
/// Core 不引用 Fufucode.Claude，因此这个桥接放在插件侧。
/// </summary>
internal sealed class ClaudeDiagnosticSink(DiagnosticLog log) : IClaudeDiagnosticSink
{
    public void Record(ClaudeDiagnosticEvent entry) =>
        log.Record(new PiDiagnosticEvent(
            entry.Timestamp,
            entry.EventType,
            entry.RequestId,
            entry.FrameType,
            entry.DurationMs,
            entry.ExitCode,
            entry.ErrorCode,
            entry.Outcome));
}
