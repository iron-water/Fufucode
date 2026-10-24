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
    private static long s_dropCount;
    private static long s_lastTick = Stopwatch.GetTimestamp();
    private static double s_fps = 60.0;
    private static double s_frameTimeMs = 16.6;

    public static double CurrentFps => Volatile.Read(ref s_fps);
    public static double LastFrameTimeMs => Volatile.Read(ref s_frameTimeMs);
    public static long TotalFrames => Interlocked.Read(ref s_frameCount);
    public static long DroppedFrames => Interlocked.Read(ref s_dropCount);

    public static void ReportFrame(double frameTimeMs, bool dropped = false)
    {
        Volatile.Write(ref s_frameTimeMs, frameTimeMs);
        Interlocked.Increment(ref s_frameCount);
        if (dropped || frameTimeMs > 33.3) Interlocked.Increment(ref s_dropCount);

        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref s_lastTick), now).TotalSeconds;
        if (elapsed >= 1.0)
        {
            Volatile.Write(ref s_lastTick, now);
            var frames = Interlocked.Exchange(ref s_frameCount, 0);
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
