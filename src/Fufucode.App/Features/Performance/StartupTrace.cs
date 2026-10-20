using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Fufucode.Core;
using Fufucode.Core.Diagnostics;
using Fufucode.Core.Diagnostics.Perf;

namespace Fufucode.App.Features.Performance;

/// <summary>
/// 启动追踪：进程启动 → Program.Main → Avalonia 初始化 → 主窗口构造 → 插件加载 →
/// 首次会话打开 → 主窗口首次渲染（Loaded）的顺序里程碑，写入 `<c>&lt;dataRoot&gt;/perf/startup-*.jsonl</c>`。
/// 插件未启用时也记录（用户要求默认监控启动用时），启用后同时以 `startup.milestone` span 进入接收端。
/// </summary>
public sealed class StartupTrace : IDisposable
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private readonly List<StartupMilestone> _milestones = new();
    private readonly string? _filePath;
    private readonly long _startedTicks;
    private readonly DateTimeOffset _startedUtc;
    private volatile bool _disposed;
    private bool _finished;
    private bool _replayed;

    private StartupTrace(string dataRoot)
    {
        _startedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _startedUtc = DateTimeOffset.UtcNow;
        try
        {
            var directory = Path.Combine(dataRoot, "perf");
            Directory.CreateDirectory(directory);
            _filePath = Path.Combine(directory, $"startup-{_startedUtc:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _filePath = null;
        }
    }

    /// <summary>进程最早可得的创建点；越早建误差越小。</summary>
    public static StartupTrace? Start(AppDataLayout layout)
    {
        try { return new StartupTrace(layout.Root); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>标记一个启动里程碑。先写文件；插件就绪后由 <see cref="ReplayToSink"/> 回放成 span。</summary>
    public void Mark(string name, string? detail = null)
    {
        if (_disposed) return;
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_startedTicks);
        var milestone = new StartupMilestone(name, elapsed.TotalMilliseconds, DateTimeOffset.UtcNow, detail);
        lock (_gate) _milestones.Add(milestone);
        Write(milestone);
    }

    /// <summary>到本里程碑为止的总耗时与逐段耗时（按调用顺序）。</summary>
    public IReadOnlyList<StartupMilestone> Snapshot()
    {
        lock (_gate) return _milestones.ToArray();
    }

    /// <summary>
    /// 把全部里程碑回放成一棵启动 trace：根 span 覆盖进程启动到当前，子 span 为相邻里程碑之间的耗时。
    /// 插件在启动过程中就绪时调用一次；中途启用插件时也会补发已缓冲的里程碑。
    /// </summary>
    public void ReplayToSink()
    {
        // 只在启动结束后回放一次：启动中途回放会把同一批里程碑重复成多棵 trace。
        if (!_finished || _replayed || PerfTracer.Sink is null) return;
        _replayed = true;
        var milestones = Snapshot();
        if (milestones.Count == 0) return;
        var frequency = System.Diagnostics.Stopwatch.Frequency;
        long Ticks(double ms) => _startedTicks + (long)Math.Round(ms / 1000d * frequency);
        var traceId = PerfTracer.NewId();
        var rootId = PerfTracer.NewId();
        var rootTags = new List<KeyValuePair<string, string>>
        {
            new("totalMs", PerfTracer.Invariant((long)Math.Round(milestones[^1].ElapsedMs))),
            new("milestones", PerfTracer.Invariant(milestones.Count)),
            new("startedUtc", _startedUtc.ToString("O")),
        };
        PerfTracer.EmitCompleted(traceId, rootId, 0, "startup", _startedTicks,
            Ticks(milestones[^1].ElapsedMs), tags: rootTags);
        var previousMs = 0d;
        foreach (var milestone in milestones)
        {
            var tags = new List<KeyValuePair<string, string>>
            {
                new("elapsedMs", PerfTracer.Invariant((long)Math.Round(milestone.ElapsedMs))),
                new("segmentMs", PerfTracer.Invariant((long)Math.Round(milestone.ElapsedMs - previousMs))),
            };
            if (milestone.Detail is { Length: > 0 } detail) tags.Add(new("detail", PerfTracer.TagText(detail)));
            PerfTracer.EmitCompleted(traceId, PerfTracer.NewId(), rootId, "startup." + milestone.Name,
                Ticks(previousMs), Ticks(milestone.ElapsedMs), tags: tags);
            previousMs = milestone.ElapsedMs;
        }
    }

    /// <summary>
    /// 在 UI 队列依次排入“渲染 / 布局 / 空闲”三个里程碑后结束追踪：
    /// 分别对应“首帧出现”和“全部布局与渲染队列排空”，比单一的 session-ready 更接近用户感知。
    /// </summary>
    public void FinishAfterRender()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Mark("ui.first-frame", "first render tick after session ready"),
            Avalonia.Threading.DispatcherPriority.Render);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Mark("ui.layout", "layout after session ready"),
            Avalonia.Threading.DispatcherPriority.Background);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Mark("ui.idle", "render and layout queues drained");
            Finish("first-render");
        }, Avalonia.Threading.DispatcherPriority.ContextIdle);
    }

    /// <summary>结束启动追踪：写汇总并回放为 span（插件未启用时只写文件）。</summary>
    public void Finish(string? note = null)
    {
        if (_disposed) return;
        _disposed = true;
        var total = System.Diagnostics.Stopwatch.GetElapsedTime(_startedTicks).TotalMilliseconds;
        var milestones = Snapshot();
        _finished = true;
        ReplayToSink();
        try
        {
            if (_filePath is not null)
            {
                var summary = new StartupSummary(_startedUtc, milestones, total, note, PerfTracer.Enabled);
                File.AppendAllText(_filePath,
                    JsonSerializer.Serialize(summary, StartupTraceJsonContext.Default.StartupSummary) + "\n");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 启动追踪是低优先级数据：写失败不影响启动。
        }
    }

    private void Write(StartupMilestone milestone)
    {
        if (_filePath is null) return;
        try
        {
            File.AppendAllText(_filePath,
                JsonSerializer.Serialize(milestone, StartupTraceJsonContext.Default.StartupMilestone) + "\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => Finish();

    /// <summary>启动完成的判定：主窗口首次 Loaded 事件。</summary>
    public void FinishOnFirstRender() => Finish("first-render");
}

public sealed record StartupMilestone(string Name, double ElapsedMs, DateTimeOffset TimestampUtc, string? Detail);

public sealed record StartupSummary(DateTimeOffset StartedUtc, IReadOnlyList<StartupMilestone> Milestones,
    double TotalMs, string? Note, bool SinkActive);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StartupMilestone))]
[JsonSerializable(typeof(StartupSummary))]
internal partial class StartupTraceJsonContext : JsonSerializerContext;
