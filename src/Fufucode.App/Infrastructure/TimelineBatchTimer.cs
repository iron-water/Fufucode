using System;
using Avalonia.Threading;
using Fufucode.App.Features.Performance;
using Fufucode.Core.Diagnostics;

namespace Fufucode.App.Infrastructure;

/// <summary>
/// 时间线批处理的轻量计时器：接收端未启用时 <see cref="Measure"/> 返回默认值，Dispose 无操作。
/// </summary>
public readonly struct TimelineBatchTimer : IDisposable
{
    private readonly long _started;
    private readonly bool _active;

    private TimelineBatchTimer(long started, bool active)
    {
        _started = started;
        _active = active;
    }

    public static TimelineBatchTimer Measure() =>
        PerfTracer.Enabled ? new TimelineBatchTimer(System.Diagnostics.Stopwatch.GetTimestamp(), true) : default;

    public void Dispose()
    {
        if (!_active) return;
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        PerfHostWiring.Current?.RecordUiBatch(ms);
    }
}
