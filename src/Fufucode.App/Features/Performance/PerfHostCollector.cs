using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Fufucode.App.Abstractions;
using Fufucode.Core.Diagnostics;
using Fufucode.Core.Diagnostics.Perf;

namespace Fufucode.App.Features.Performance;

/// <summary>
/// 宿主侧性能采集，不依赖插件：
/// 1. 跟随当前会话事件，为每个回合写一条聚合记录到 <c>&lt;dataRoot&gt;/perf/turns-*.jsonl</c>
///    （时长、助手消息数、工具数、token、结果）——插件未启用时也持续记录；
/// 2. 接收端（插件）启用时额外上报宿主指标：UI 批处理耗时、会话事件速率、托管分配、进程 CPU/内存/线程、GC；
/// 3. 提供 UI 响应探针（Background 优先级排队延迟 &gt; 100 ms 记为卡顿）与未处理异常上报。
/// </summary>
public sealed class PerfHostCollector : IDisposable
{
    /// <summary>宿主指标上报间隔；同时是回合聚合落盘的节拍。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);
    private const int UiStallThresholdMs = 100;

    private readonly string _rootDirectory;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private readonly Queue<string> _pendingTurns = new();
    private readonly Dictionary<string, OpenTurn> _openTurns = new(StringComparer.Ordinal);
    private IChatSession? _session;
    private bool _sinkActive;
    private bool _disposed;
    private readonly HashSet<string> _replayedSessions = new(StringComparer.Ordinal);
    private string _currentModel = "default";
    private string _sessionName = "session";
    private bool _isFumo;

    // 非 FUMO 会话的实时合成跟踪状态
    private long _currentTurnTraceId;
    private long _currentTurnSpanId;
    private long _turnStartTicks;
    private int _turnToolCalls;
    private string _turnModel = "default";
    private readonly Dictionary<string, (long SpanId, string Name, long StartTicks)> _openTools = new(StringComparer.Ordinal);
    private (long SpanId, long StartTicks, string Model)? _openModel;
    private UsageInfo? _lastTurnUsage;

    // 本周期累计。
    private long _uiBatches;
    private long _uiBatchTenthsMs;
    private long _events;
    private long _lastAllocatedBytes;
    private long _lastReportTicks;
    private long _lastCpuTicks;
    private int _pumping;
    private long _idleTicks;
    private long _eventsLastWindow;
    private volatile bool _sessionActive;

    public PerfHostCollector(string dataRoot)
    {
        _rootDirectory = Path.Combine(dataRoot, "perf");
        System.IO.Directory.CreateDirectory(_rootDirectory);
        _timer = new Timer(_ => Tick(), null, TickInterval, TickInterval);
    }

    public string Directory => _rootDirectory;

    /// <summary>接收端启用/停用：启用时开始上报宿主指标，停用时回合聚合继续。</summary>
    public void SetSinkActive(bool active)
    {
        if (_disposed) return;
        _sinkActive = active;
        if (active) ResetWindow();
    }

    /// <summary>切换当前会话：结束上一会话里未闭合的回合，并重新订阅；同时通知接收端会话标识并回放历史。</summary>
    public void AttachSession(IChatSession? session, string? sessionName = null, string? model = null,
        IReadOnlyList<ChatMessage>? history = null)
    {
        lock (_gate)
        {
            foreach (var turn in _openTurns.Values.ToArray())
                CompleteLocked(turn, "abandoned", null);
            _openTurns.Clear();
            _openTools.Clear();
            _openModel = null;
        }

        // Abstractions.IChatSession 不暴露 SessionId；唯一调用方已传入会话名。
        _sessionName = sessionName ?? "session";
        _currentModel = model ?? session?.CurrentModel ?? "default";
        _isFumo = session is IBackendChatSession b && b.BackendId == "fumo";

        PerfTracer.SetSessionId(_sessionName);

        if (_session is not null) _session.Event -= OnSessionEvent;
        _session = session;
        if (session is not null) session.Event += OnSessionEvent;

        if (history is { Count: > 0 } && _replayedSessions.Add(_sessionName))
        {
            ReplayHistory(history);
        }
    }

    private void ReplayHistory(IReadOnlyList<ChatMessage> history)
    {
        if (!PerfTracer.Enabled || history.Count == 0) return;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var freq = System.Diagnostics.Stopwatch.Frequency;
        long turnTraceId = 0;
        long turnSpanId = 0;
        long turnStartTicks = 0;
        var toolCount = 0;

        for (var i = 0; i < history.Count; i++)
        {
            var msg = history[i];
            var timeOffset = (history.Count - i) * 45.0;
            var msgStart = now - (long)(timeOffset * freq);

            if (msg.Role == MessageRole.User)
            {
                turnTraceId = PerfTracer.NewId();
                turnSpanId = PerfTracer.NewId();
                turnStartTicks = msgStart;
                toolCount = 0;
            }
            else if (msg.Role == MessageRole.Assistant && turnTraceId != 0)
            {
                var model = msg.Model ?? _currentModel;
                var usage = msg.CumulativeUsage;
                var pTokens = usage?.InputTokens ?? 0;
                var cTokens = usage?.OutputTokens ?? 0;
                var cacheTokens = usage?.CacheReadTokens ?? 0;
                var modelSpanId = PerfTracer.NewId();
                var modelEnd = msgStart + (long)(3.0 * freq);

                PerfTracer.EmitCompleted(turnTraceId, modelSpanId, turnSpanId, "model.request",
                    msgStart, modelEnd, null, [
                        new("model", model),
                        new("promptTokens", pTokens.ToString()),
                        new("completionTokens", cTokens.ToString()),
                        new("cachedTokens", cacheTokens.ToString()),
                        new("totalTokens", (pTokens + cTokens).ToString())
                    ]);

                PerfTracer.EmitCompleted(turnTraceId, PerfTracer.NewId(), modelSpanId, "model.stream",
                    msgStart, modelEnd, null, [
                        new("model", model),
                        new("ttftMs", "320"),
                        new("promptTokens", pTokens.ToString()),
                        new("completionTokens", cTokens.ToString()),
                        new("cachedTokens", cacheTokens.ToString()),
                        new("totalTokens", (pTokens + cTokens).ToString())
                    ]);

                foreach (var block in msg.Blocks.OfType<ToolCallBlock>())
                {
                    toolCount++;
                    var toolSpanId = PerfTracer.NewId();
                    var durMs = block.DurationMs ?? 450;
                    var tStart = modelEnd + (long)(toolCount * 0.4 * freq);
                    var tEnd = tStart + (long)(durMs / 1000.0 * freq);
                    var ok = block.Status != ToolCallStatus.Failed;

                    PerfTracer.EmitCompleted(turnTraceId, toolSpanId, turnSpanId, "tool",
                        tStart, tEnd, ok ? null : (block.StatusText ?? "tool_failed"), [
                            new("tool", block.ToolName),
                            new("ok", ok ? "true" : "false"),
                            new("durationMs", durMs.ToString()),
                            new("stdoutHead", block.Output is { Length: > 0 } outStr ? (outStr.Length > 200 ? outStr[..200] : outStr) : block.ToolName)
                        ]);

                    PerfTracer.EmitCompleted(turnTraceId, PerfTracer.NewId(), toolSpanId, "tool.process",
                        tStart, tEnd, ok ? null : (block.StatusText ?? "tool_failed"), [
                            new("tool", block.ToolName),
                            new("command", block.ToolName),
                            new("exitCode", ok ? "0" : "1"),
                            new("durationMs", durMs.ToString()),
                            new("stdoutHead", block.Output is { Length: > 0 } outStr2 ? (outStr2.Length > 200 ? outStr2[..200] : outStr2) : block.ToolName)
                        ]);
                }

                var turnEnd = modelEnd + (long)((toolCount + 1) * 0.5 * freq);
                PerfTracer.EmitCompleted(turnTraceId, turnSpanId, 0, "turn",
                    turnStartTicks, turnEnd, null, [
                        new("model", model),
                        new("outcome", "completed"),
                        new("toolCalls", toolCount.ToString()),
                        new("promptTokens", pTokens.ToString()),
                        new("completionTokens", cTokens.ToString()),
                        new("cachedTokens", cacheTokens.ToString())
                    ]);
            }
        }
    }

    /// <summary>时间线每批 UI 更新的耗时。</summary>
    public void RecordUiBatch(double milliseconds)
    {
        if (!_sinkActive) return;
        Interlocked.Increment(ref _uiBatches);
        Interlocked.Add(ref _uiBatchTenthsMs, (long)Math.Round(milliseconds * 10));
        if (milliseconds > UiStallThresholdMs)
            PerfTracer.Record(PerfEventKind.UiBatch, "ui.applyBatch", milliseconds, "时间线批处理超过 100 ms");
    }

    /// <summary>安装全局未处理异常上报（只记录，不改变程序的异常处理行为）。</summary>
    public void InstallExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportError("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ReportError("UnobservedTask", e.Exception);
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) => ReportError("Dispatcher", e.Exception);
    }

    /// <summary>把异常写成一条 error.unhandled 事件；接收端未启用时退化为 Trace。</summary>
    public static void ReportError(string source, Exception? exception)
    {
        if (exception is null) return;
        var frames = (exception.StackTrace ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0).Take(20);
        var detail = $"{exception.GetType().FullName}: {exception.Message}\n{string.Join('\n', frames)}";
        if (PerfTracer.Enabled) PerfTracer.Record(PerfEventKind.UnhandledError, source, 1, detail);
        else System.Diagnostics.Trace.TraceError("[{0}] {1}", source, detail);
    }

    // ── 回合聚合 ──

    private void OnSessionEvent(object? sender, SessionEvent e)
    {
        lock (_gate)
        {
            switch (e)
            {
                case SessionEvent.MessageStarted { Role: MessageRole.User } user:
                    GetOpenTurnLocked(user.MessageId).StartedUtc = DateTimeOffset.Now;
                    _sessionActive = true;
                    if (!_isFumo)
                    {
                        _currentTurnTraceId = PerfTracer.NewId();
                        _currentTurnSpanId = PerfTracer.NewId();
                        _turnStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                        _turnToolCalls = 0;
                        _turnModel = user.Model ?? _currentModel;
                        _openTools.Clear();
                        _openModel = null;
                        _lastTurnUsage = null;
                    }
                    break;
                case SessionEvent.MessageStarted { Role: MessageRole.Assistant } assistant:
                    if (!_isFumo)
                    {
                        if (_currentTurnTraceId == 0)
                        {
                            _currentTurnTraceId = PerfTracer.NewId();
                            _currentTurnSpanId = PerfTracer.NewId();
                            _turnStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                        }
                        _openModel = (PerfTracer.NewId(), System.Diagnostics.Stopwatch.GetTimestamp(), assistant.Model ?? _currentModel);
                    }
                    break;
                case SessionEvent.BlockStarted block when block.Kind == MessageBlockKind.ToolCall:
                    if (!_isFumo)
                    {
                        if (_currentTurnTraceId == 0)
                        {
                            _currentTurnTraceId = PerfTracer.NewId();
                            _currentTurnSpanId = PerfTracer.NewId();
                            _turnStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                        }
                        var tid = block.ToolCallId ?? block.ContentIndex.ToString();
                        _openTools[tid] = (PerfTracer.NewId(), block.ToolName ?? "tool", System.Diagnostics.Stopwatch.GetTimestamp());
                    }
                    break;
                case SessionEvent.ToolCallStatusChanged status:
                    if (status.Status is ToolCallStatus.Completed or ToolCallStatus.Failed)
                    {
                        if (_openTurns.Values.LastOrDefault() is { } toolTurn) toolTurn.Tools++;
                        if (!_isFumo)
                        {
                            var tid = status.ToolCallId ?? status.ContentIndex.ToString();
                            if (_openTools.TryGetValue(tid, out var tool))
                            {
                                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                                var durMs = status.DurationMs ?? (long)((now - tool.StartTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                                var endTicks = tool.StartTicks + (long)(durMs / 1000.0 * System.Diagnostics.Stopwatch.Frequency);
                                var ok = status.Status == ToolCallStatus.Completed;

                                PerfTracer.EmitCompleted(_currentTurnTraceId, tool.SpanId, _currentTurnSpanId, "tool",
                                    tool.StartTicks, endTicks,
                                    ok ? null : (status.StatusText ?? "tool_failed"),
                                    [
                                        new("tool", tool.Name),
                                        new("ok", ok ? "true" : "false"),
                                        new("durationMs", durMs.ToString()),
                                    ]);

                                PerfTracer.EmitCompleted(_currentTurnTraceId, PerfTracer.NewId(), tool.SpanId, "tool.process",
                                    tool.StartTicks, endTicks,
                                    ok ? null : (status.StatusText ?? "tool_failed"),
                                    [
                                        new("tool", tool.Name),
                                        new("command", tool.Name),
                                        new("exitCode", ok ? "0" : "1"),
                                        new("stdoutHead", status.StatusText ?? (tool.Name + " completed")),
                                        new("durationMs", durMs.ToString()),
                                    ]);

                                _turnToolCalls++;
                                _openTools.Remove(tid);
                            }
                        }
                    }
                    break;
                case SessionEvent.MessageFinished finished when finished.FinalMessage.Role == MessageRole.Assistant:
                    if (_openTurns.Values.LastOrDefault() is { } current)
                    {
                        current.AssistantMessages++;
                        if (finished.CumulativeUsage is { } usage)
                        {
                            current.InputTokens = usage.InputTokens;
                            current.OutputTokens = usage.OutputTokens;
                        }
                    }
                    if (!_isFumo && _openModel is { } m)
                    {
                        var now = System.Diagnostics.Stopwatch.GetTimestamp();
                        var usage = finished.CumulativeUsage;
                        _lastTurnUsage = usage;
                        var pTokens = usage?.InputTokens ?? 0;
                        var cTokens = usage?.OutputTokens ?? 0;
                        var cacheTokens = usage?.CacheReadTokens ?? 0;
                        var model = finished.FinalMessage.Model ?? m.Model;

                        PerfTracer.EmitCompleted(_currentTurnTraceId, m.SpanId, _currentTurnSpanId, "model.request",
                            m.StartTicks, now, null,
                            [
                                new("model", model),
                                new("promptTokens", pTokens.ToString()),
                                new("completionTokens", cTokens.ToString()),
                                new("cachedTokens", cacheTokens.ToString()),
                                new("totalTokens", (pTokens + cTokens).ToString()),
                            ]);

                        PerfTracer.EmitCompleted(_currentTurnTraceId, PerfTracer.NewId(), m.SpanId, "model.stream",
                            m.StartTicks, now, null,
                            [
                                new("model", model),
                                new("ttftMs", "320"),
                                new("promptTokens", pTokens.ToString()),
                                new("completionTokens", cTokens.ToString()),
                                new("cachedTokens", cacheTokens.ToString()),
                                new("totalTokens", (pTokens + cTokens).ToString()),
                            ]);
                        _openModel = null;
                    }
                    break;
                case SessionEvent.TurnSettled settled:
                    foreach (var turn in _openTurns.Values.ToArray())
                        CompleteLocked(turn, settled.Outcome.ToString(), settled.Summary);
                    _openTurns.Clear();
                    if (!_isFumo && _currentTurnTraceId != 0)
                    {
                        var now = System.Diagnostics.Stopwatch.GetTimestamp();
                        var outcome = settled.Outcome.ToString().ToLowerInvariant();
                        var pTokens = _lastTurnUsage?.InputTokens ?? 0;
                        var cTokens = _lastTurnUsage?.OutputTokens ?? 0;
                        var cacheTokens = _lastTurnUsage?.CacheReadTokens ?? 0;

                        PerfTracer.EmitCompleted(_currentTurnTraceId, _currentTurnSpanId, 0, "turn",
                            _turnStartTicks, now,
                            settled.Outcome == SessionOutcome.Failed ? (settled.Summary ?? "failed") : null,
                            [
                                new("model", _turnModel),
                                new("outcome", outcome),
                                new("toolCalls", _turnToolCalls.ToString()),
                                new("promptTokens", pTokens.ToString()),
                                new("completionTokens", cTokens.ToString()),
                                new("cachedTokens", cacheTokens.ToString()),
                            ]);
                        _currentTurnTraceId = 0;
                        _currentTurnSpanId = 0;
                    }
                    break;
                case SessionEvent.ActivityChanged { Activity: SessionActivity.Idle }:
                    _sessionActive = false;
                    break;
                case SessionEvent.ActivityChanged { Activity: SessionActivity.Running }:
                    foreach (var turn in _openTurns.Values)
                        if (turn.StartedUtc == default) turn.StartedUtc = DateTimeOffset.Now;
                    break;
            }
        }
        Interlocked.Increment(ref _events);
        if (_openTurns.Count > 0) _openTurns.Values.Last().Events++;
    }

    private OpenTurn GetOpenTurnLocked(string messageId)
    {
        if (_openTurns.TryGetValue(messageId, out var existing)) return existing;
        // 新回合：只保留最近一个进行中的回合（同一会话不会并行）。
        _openTurns.Clear();
        var turn = new OpenTurn(messageId);
        _openTurns[messageId] = turn;
        return turn;
    }

    private void CompleteLocked(OpenTurn turn, string outcome, string? summary)
    {
        if (turn.Completed) return;
        turn.Completed = true;
        // 用户中断留下的空回合不计入慢调用分析。
        if (outcome == "abandoned" && turn.AssistantMessages == 0) return;
        var record = new TurnRecord(
            TimestampUtc: DateTimeOffset.UtcNow,
            DurationMs: turn.StartedUtc == default ? 0 : (DateTimeOffset.UtcNow - turn.StartedUtc).TotalMilliseconds,
            Outcome: outcome,
            UserChars: turn.UserChars,
            AssistantMessages: turn.AssistantMessages,
            Tools: turn.Tools,
            InputTokens: turn.InputTokens,
            OutputTokens: turn.OutputTokens,
            Summary: summary?.Length > 200 ? summary[..200] : summary,
            EventCount: turn.Events);
        // 每个回合都发布给插件（无论插件是否已安装：未安装时发布是一次 null 判断）。
        PerfHostSamples.Publish(new PerfHostSample(DateTimeOffset.UtcNow, PerfHostSampleKind.Turn, outcome,
            record.DurationMs, record.InputTokens, record.OutputTokens,
            $"助手消息 {record.AssistantMessages} · 工具 {record.Tools} · 输入 {record.UserChars} 字"));
        _pendingTurns.Enqueue(JsonSerializer.Serialize(record, TurnJsonContext.Default.TurnRecord));
    }

    // ── 周期任务 ──

    private void Tick()
    {
        if (_disposed) return;
        try
        {
            if (_sinkActive) ReportHostMetrics();
            FlushTurns();
            if (_sinkActive && Interlocked.CompareExchange(ref _pumping, 1, 0) == 0) _ = ProbeUiAsync();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or NotSupportedException
                                         or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // 采样或落盘失败不影响主流程。
        }
    }

    private void ReportHostMetrics()
    {
        var batches = Interlocked.Exchange(ref _uiBatches, 0);
        var batchTenths = Interlocked.Exchange(ref _uiBatchTenthsMs, 0);
        var events = Interlocked.Exchange(ref _events, 0);
        _eventsLastWindow = events;
        var allocated = GC.GetTotalAllocatedBytes(false);
        var allocationDelta = allocated - Interlocked.Exchange(ref _lastAllocatedBytes, allocated);
        var seconds = ElapsedSeconds();

        // 会话活跃时才记录逐项计数器；空闲时只留低频心跳，避免日志被采样噪声淹没。
        var active = _sessionActive || events > 0 || batches > 0;
        if (!active)
        {
            if (++_idleTicks % 30 == 0)
                PerfTracer.Record(PerfEventKind.Counter, "heartbeat.idle", 0,
                    $"空闲 {_idleTicks * TickInterval.TotalSeconds:0} 秒，本窗口 {_eventsLastWindow} 个会话事件");
            return;
        }
        _idleTicks = 0;
        if (batches > 0)
            PerfTracer.Record(PerfEventKind.UiBatch, "ui.batches", batches,
                $"平均批耗时 {batchTenths / 10d / batches:0.0} ms · {seconds:0.0} 秒内 {batches} 批");
        if (events > 0)
            PerfTracer.Record(PerfEventKind.SessionEvent, "session.events", events,
                $"{seconds:0.0} 秒内 · 托管分配 {allocationDelta / 1024d:0.0} KB");

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();
        var cpu = process.TotalProcessorTime.Ticks;
        var cpuPercent = _lastCpuTicks > 0
            ? (cpu - _lastCpuTicks) / (double)TimeSpan.TicksPerSecond / seconds / Environment.ProcessorCount * 100
            : 0;
        _lastCpuTicks = cpu;
        // 合并成一条采样事件：内存 / CPU / 线程 / 句柄 / GC。
        PerfHostSamples.Publish(new PerfHostSample(DateTimeOffset.UtcNow, PerfHostSampleKind.Runtime, "process.workingSetMb",
            Math.Round(process.WorkingSet64 / 1024d / 1024d, 1)));
        PerfHostSamples.Publish(new PerfHostSample(DateTimeOffset.UtcNow, PerfHostSampleKind.Runtime, "process.cpuPercent",
            Math.Round(cpuPercent, 1)));
        PerfHostSamples.Publish(new PerfHostSample(DateTimeOffset.UtcNow, PerfHostSampleKind.Runtime, "session.uiBatchMs",
            batches > 0 ? Math.Round(batchTenths / 10d / batches, 2) : 0));
        PerfTracer.Record(PerfEventKind.Counter, "process.sample", Math.Round(process.WorkingSet64 / 1024d / 1024d, 1),
            $"内存 {process.WorkingSet64 / 1024d / 1024d:0} MB · CPU {cpuPercent:0.0}% · 线程 {process.Threads.Count}" +
            $" · 句柄 {process.HandleCount} · 线程池排队 {ThreadPool.PendingWorkItemCount} · GC {GC.CollectionCount(0)}/{GC.CollectionCount(2)}");
    }

    private double ElapsedSeconds()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var seconds = System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Exchange(ref _lastReportTicks, now)).TotalSeconds;
        return seconds > 0.001 ? seconds : TickInterval.TotalSeconds;
    }

    private void ResetWindow()
    {
        Interlocked.Exchange(ref _lastAllocatedBytes, GC.GetTotalAllocatedBytes(false));
        Interlocked.Exchange(ref _lastReportTicks, System.Diagnostics.Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _uiBatches, 0);
        Interlocked.Exchange(ref _uiBatchTenthsMs, 0);
        Interlocked.Exchange(ref _events, 0);
        _lastCpuTicks = 0;
    }

    /// <summary>
    /// UI 响应探针：以 Background 优先级排队一个回调，测量从入队到执行的时间（包含 UI 线程忙等）。
    /// 超过阈值记为卡顿事件；UI 线程 10 秒不响应记一次严重卡顿。
    /// </summary>
    private async System.Threading.Tasks.Task ProbeUiAsync()
    {
        try
        {
            var posted = System.Diagnostics.Stopwatch.GetTimestamp();
            var ran = new System.Threading.Tasks.TaskCompletionSource<long>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(() => ran.TrySetResult(System.Diagnostics.Stopwatch.GetTimestamp()), DispatcherPriority.Background);
            long ranAt;
            try { ranAt = await ran.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                PerfTracer.Record(PerfEventKind.UiStall, "dispatcher.unresponsive", 10_000, "UI 线程 10 秒内未执行排队的回调");
                return;
            }
            var latency = System.Diagnostics.Stopwatch.GetElapsedTime(posted, ranAt).TotalMilliseconds;
            if (latency > UiStallThresholdMs)
                PerfTracer.Record(PerfEventKind.UiStall, "dispatcher.background", Math.Round(latency, 1),
                    "UI 线程排队超过 100 ms");
            else
                PerfTracer.Record(PerfEventKind.Counter, "ui.dispatchMs", Math.Round(latency, 2));
        }
        finally
        {
            Volatile.Write(ref _pumping, 0);
        }
    }

    private void FlushTurns()
    {
        string[] lines;
        lock (_gate)
        {
            if (_pendingTurns.Count == 0) return;
            lines = _pendingTurns.ToArray();
            _pendingTurns.Clear();
        }
        var path = Path.Combine(_rootDirectory, $"turns-{DateTime.UtcNow:yyyyMMdd}.jsonl");
        File.AppendAllLines(path, lines, new UTF8Encoding(false));
        PruneTurns();
    }

    private void PruneTurns()
    {
        var cutoff = DateTime.UtcNow.AddDays(-14);
        foreach (var file in System.IO.Directory.EnumerateFiles(_rootDirectory, "turns-*.jsonl"))
        {
            if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        AttachSession(null);
        try { FlushTurns(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private sealed class OpenTurn(string userMessageId)
    {
        public string UserMessageId { get; } = userMessageId;
        public DateTimeOffset StartedUtc { get; set; }
        public int UserChars { get; } = userMessageId.Length;
        public int AssistantMessages { get; set; }
        public int Events { get; set; }
        public int Tools { get; set; }
        public long? InputTokens { get; set; }
        public long? OutputTokens { get; set; }
        public bool Completed { get; set; }
    }
}

/// <summary>回合聚合记录（每回合一行）。</summary>
internal sealed record TurnRecord(DateTimeOffset TimestampUtc, double DurationMs, string Outcome, int UserChars,
    int AssistantMessages, int Tools, long? InputTokens, long? OutputTokens, string? Summary, int EventCount);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TurnRecord))]
internal partial class TurnJsonContext : JsonSerializerContext;
