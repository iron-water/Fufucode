using System.Diagnostics;
using System.Text.Json;

namespace Live2D.Core;

/// <summary>
/// 采样器：主进程 + 关联子进程（WebView2 的 msedgewebview2）的 CPU%/内存句柄数，
/// FPS 由被测方上报。每秒一行 CSV，结束写 JSON 摘要。
/// </summary>
public sealed class MetricsCollector : IDisposable
{
    private readonly Process _self = Process.GetCurrentProcess();
    private readonly string _childProcessName;
    private readonly StreamWriter _csv;
    private TimeSpan _prevCpuTotal;
    private DateTime _prevSample;
    private readonly List<double> _fps = new(), _cpu = new();
    private long _peakWs;
    public int RenderedFrames;
    private DateTime _startTime = DateTime.UtcNow;

    public MetricsCollector(string csvPath, string childProcessName = "")
    {
        _childProcessName = childProcessName;
        _csv = new StreamWriter(csvPath);
        _csv.WriteLine("t_s,fps,cpu_pct,ws_mb,private_mb,child_procs,child_ws_mb,handles,threads");
        _prevCpuTotal = _self.TotalProcessorTime;
        _prevSample = DateTime.UtcNow;
    }

    /// <summary>被测方每渲染一帧调用一次。</summary>
    public void Frame() => RenderedFrames++;

    /// <summary>每秒调用一次采样。</summary>
    public void Sample()
    {
        var now = DateTime.UtcNow;
        double elapsed = (now - _startTime).TotalSeconds;
        double dt = (now - _prevSample).TotalSeconds;
        var cpuTotal = _self.TotalProcessorTime + ChildCpu();
        double cpuPct = (cpuTotal - _prevCpuTotal).TotalSeconds / Math.Max(dt, 1e-9) / Environment.ProcessorCount * 100;
        double fps = RenderedFrames / Math.Max(dt, 1e-9);
        RenderedFrames = 0;

        var (childN, childWs) = ChildProcs();
        long wsMb = _self.WorkingSet64 >> 20;
        long privMb = _self.PrivateMemorySize64 >> 20;
        _peakWs = Math.Max(_peakWs, wsMb + childWs);
        _fps.Add(fps); _cpu.Add(cpuPct);

        _csv.WriteLine($"{elapsed:F1},{fps:F1},{cpuPct:F1},{wsMb},{privMb},{childN},{childWs},{_self.HandleCount},{_self.Threads.Count}");
        _csv.Flush();
        _prevCpuTotal = cpuTotal; _prevSample = now;
    }

    private TimeSpan ChildCpu()
    {
        if (_childProcessName == "") return TimeSpan.Zero;
        var t = TimeSpan.Zero;
        foreach (var p in Process.GetProcessesByName(_childProcessName))
            try { t += p.TotalProcessorTime; } catch { }
        return t;
    }

    private (int n, long wsMb) ChildProcs()
    {
        if (_childProcessName == "") return (0, 0);
        int n = 0; long ws = 0;
        foreach (var p in Process.GetProcessesByName(_childProcessName))
            try { n++; ws += p.WorkingSet64 >> 20; } catch { }
        return (n, ws);
    }

    public void WriteSummary(string jsonPath, Dictionary<string, object?> extra)
    {
        var summary = new Dictionary<string, object?>
        {
            ["duration_s"] = (DateTime.UtcNow - _startTime).TotalSeconds,
            ["avg_fps"] = _fps.Count > 0 ? _fps.Average() : 0,
            ["avg_cpu_pct"] = _cpu.Count > 0 ? _cpu.Average() : 0,
            ["peak_ws_mb"] = _peakWs,
            ["final_ws_mb"] = _self.WorkingSet64 >> 20,
            ["final_handles"] = _self.HandleCount,
        };
        foreach (var kv in extra) summary[kv.Key] = kv.Value;
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Dispose() { _csv.Dispose(); _self.Dispose(); }
}
