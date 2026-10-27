using System;
using System.Diagnostics;
using System.Threading;

namespace Fufucode.Core.Diagnostics.Perf;

/// <summary>
/// Fufu 桌宠与 Live2D 动画渲染的实时诊断探针（帧率、单帧绘制耗时、掉帧计数）。
/// 零开销静态探针，供 Fufu 渲染循环在每帧调用。
/// </summary>
public static class FufuRenderDiagnostics
{
    private static long s_frameCount;
    private static long s_windowFrames;
    private static long s_dropCount;
    private static long s_lastTick = Stopwatch.GetTimestamp();
    private static long s_lastFrameTicks;
    private static double s_fps = double.NaN;
    private static double s_frameTimeMs = double.NaN;

    /// <summary>至少收到过一帧，且已完成首个采样窗口（FPS 为有限值）。</summary>
    public static bool HasSamples => TotalFrames > 0 && double.IsFinite(CurrentFps);

    /// <summary>最近一次上报是否在给定时间内；跨进程不可达时返回 false。</summary>
    public static bool IsFresh(TimeSpan maxAge)
    {
        var last = Interlocked.Read(ref s_lastFrameTicks);
        return last != 0 && Stopwatch.GetElapsedTime(last, Stopwatch.GetTimestamp()) <= maxAge;
    }

    public static double CurrentFps => Volatile.Read(ref s_fps);
    public static double LastFrameTimeMs => Volatile.Read(ref s_frameTimeMs);
    public static long TotalFrames => Interlocked.Read(ref s_frameCount);
    public static long DroppedFrames => Interlocked.Read(ref s_dropCount);

    public static void ReportFrame(double frameTimeMs, bool dropped = false)
    {
        if (!PerfTracer.Enabled || !double.IsFinite(frameTimeMs) || frameTimeMs < 0) return;
        Volatile.Write(ref s_frameTimeMs, frameTimeMs);
        Interlocked.Exchange(ref s_lastFrameTicks, Stopwatch.GetTimestamp());
        Interlocked.Increment(ref s_frameCount);
        Interlocked.Increment(ref s_windowFrames);
        if (dropped || frameTimeMs > 33.3) Interlocked.Increment(ref s_dropCount);

        var now = Stopwatch.GetTimestamp();
        var last = Volatile.Read(ref s_lastTick);
        var elapsed = Stopwatch.GetElapsedTime(last, now).TotalSeconds;
        if (elapsed >= 1.0 && Interlocked.CompareExchange(ref s_lastTick, now, last) == last)
        {
            var frames = Interlocked.Exchange(ref s_windowFrames, 0);
            var fps = Math.Round(frames / Math.Max(0.1, elapsed), 1);
            Volatile.Write(ref s_fps, fps);
            if (PerfTracer.Enabled)
            {
                PerfTracer.Record(PerfEventKind.Counter, "fufu.fps", fps,
                    $"FPS {fps:0.0} · 单帧耗时 {frameTimeMs:0.1} ms · 掉帧 {Volatile.Read(ref s_dropCount)}");
            }
        }
    }
}
