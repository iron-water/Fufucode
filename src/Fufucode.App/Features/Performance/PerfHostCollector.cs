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
    private string? _currentModel;
    private string? _sessionName;
    private string? _sessionId;
    private string? _backend;
    private bool _isFumo;
    private IPerfSink? _publishedSink;
    private readonly Dictionary<string, MessageEstimate> _messageEstimates = new(StringComparer.Ordinal);
    private UsageInfo? _latestUsage;
    private bool _contextDirty;

    // 外部后端只有会话事件可观察，耗时边界明确标为 host-events。
    private long _currentTurnTraceId;
    private long _currentTurnSpanId;
    private long _turnStartTicks;
    private readonly Dictionary<string, OpenTool> _openTools = new(StringComparer.Ordinal);
    // 回合内已计入 turns 记录的工具调用 ID：与 span 生命周期解耦，性能追踪关闭时也能计数。
    private readonly HashSet<string> _countedTools = new(StringComparer.Ordinal);
    private OpenModel? _openModel;
    private readonly Dictionary<string, long> _modelByMessage = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usageMessages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);

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
        if (active)
        {
            ResetWindow();
            lock (_gate) PublishContextLocked(force: true);
        }
    }

    /// <summary>切换当前会话：结束上一会话里未闭合的回合，并重新订阅；同时通知接收端会话标识并回放历史。</summary>
    public void AttachSession(IChatSession? session, string? sessionName = null, string? model = null,
        IReadOnlyList<ChatMessage>? history = null, string? sessionId = null)
    {
        lock (_gate)
        {
            if (_session is not null) _session.Event -= OnSessionEvent;
            if (!ReferenceEquals(_session, session))
            {
                foreach (var turn in _openTurns.Values.ToArray()) CompleteLocked(turn, "abandoned", null);
                FinishExternalTurnLocked(SessionOutcome.Interrupted, "会话已切换");
                _openTurns.Clear();
                _messageEstimates.Clear();
                _toolNames.Clear();
                _latestUsage = null;
                _sessionId = sessionId ?? (session is null ? null : Guid.NewGuid().ToString("N"));
            }
            _session = session;
            _sessionName = sessionName;
            _currentModel = session?.CurrentModel ?? model;
            _backend = (session as IBackendChatSession)?.BackendId;
            _isFumo = _backend == "fumo";
            _sessionActive = session is { Activity: not SessionActivity.Idle };
            if (history is not null)
                foreach (var message in history) RememberMessageLocked(message);
            _contextDirty = true;
            PublishContextLocked(force: true);
            if (session is not null) session.Event += OnSessionEvent;
        }
    }

    private void RememberMessageLocked(ChatMessage message)
    {
        var estimate = new MessageEstimate();
        foreach (var (block, index) in message.Blocks.Select((block, index) => (block, index)))
            estimate.SetBlock(index, block);
        _messageEstimates[message.Id] = estimate;
        if (message.Role == MessageRole.Assistant && message.CumulativeUsage is { IsUnknown: false } usage)
            _latestUsage = usage;
        _contextDirty = true;
    }

    private void PublishContextLocked(bool force = false)
    {
        var sink = PerfTracer.Sink;
        if (sink is not IPerfSessionSink sessionSink) return;
        if (!force && !_contextDirty && ReferenceEquals(sink, _publishedSink)) return;
        _publishedSink = sink;
        long chat = 0, results = 0;
        foreach (var message in _messageEstimates.Values)
        {
            chat += message.ChatTokens;
            results += message.ResultTokens;
        }
        sessionSink.OnSessionContext(new PerfSessionContext(_sessionId, _sessionName,
            _session?.CurrentModel ?? _currentModel, _backend, System.Diagnostics.Stopwatch.GetTimestamp(),
            [new("chat", "会话文本", chat), new("results", "工具结果", results)],
            _latestUsage?.InputTokens, _latestUsage?.OutputTokens, _latestUsage?.CacheReadTokens));
        _contextDirty = false;
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
            if (sender is not null && !ReferenceEquals(sender, _session)) return;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            switch (e)
            {
                case SessionEvent.MessageStarted started:
                    _messageEstimates.TryAdd(started.MessageId, new MessageEstimate());
                    _currentModel = started.Model ?? _session?.CurrentModel ?? _currentModel;
                    if (started.Role == MessageRole.User)
                    {
                        GetOpenTurnLocked(started.MessageId).StartedUtc = DateTimeOffset.UtcNow;
                        _sessionActive = true;
                        if (!_isFumo) EnsureExternalTurnLocked(now);
                        PublishContextLocked(force: true);
                    }
                    else if (!_isFumo && PerfTracer.Enabled)
                    {
                        EnsureExternalTurnLocked(now);
                        if (_openModel is { } previous) FinishModelLocked(previous, now);
                        _openModel = new OpenModel(PerfTracer.NewId(), now, started.MessageId);
                        _modelByMessage[started.MessageId] = _openModel.SpanId;
                        StartLiveSpan("model.request", _openModel.SpanId, _currentTurnSpanId, now);
                    }
                    break;
                case SessionEvent.TextAppended text:
                    AppendEstimateLocked(text.MessageId, text.ContentIndex, text.Delta);
                    break;
                case SessionEvent.ThinkingAppended thinking:
                    AppendEstimateLocked(thinking.MessageId, thinking.ContentIndex, thinking.Delta);
                    break;
                case SessionEvent.ToolCallAppended call:
                    AppendEstimateLocked(call.MessageId, call.ContentIndex, call.JsonFragment);
                    break;
                case SessionEvent.BlockStarted block when block.Kind == MessageBlockKind.ToolCall:
                    // 工具名只在 BlockStarted/ToolCallBlock 上可靠；按调用 id 记住，供 Running/Completed 使用。
                    if (!string.IsNullOrEmpty(block.ToolCallId))
                        _toolNames[block.ToolCallId] = block.ToolName ?? string.Empty;
                    break;
                case SessionEvent.BlockFinished block:
                    if (block.FinalBlock is ToolCallBlock startedTool && !string.IsNullOrEmpty(startedTool.ToolCallId))
                        _toolNames[startedTool.ToolCallId] = startedTool.ToolName;
                    if (_messageEstimates.TryGetValue(block.MessageId, out var estimate))
                    {
                        estimate.SetBlock(block.ContentIndex, block.FinalBlock);
                        _contextDirty = true;
                    }
                    if (!_isFumo && block.FinalBlock is ToolCallBlock tool)
                        ObserveToolLocked(block.MessageId, tool, now);
                    break;
                case SessionEvent.ToolCallStatusChanged status:
                    if (status.Status == ToolCallStatus.Running && !_isFumo)
                    {
                        var name = _toolNames.GetValueOrDefault(status.ToolCallId)
                                   ?? _messageEstimates.GetValueOrDefault(status.MessageId)?.ToolName(status.ContentIndex);
                        StartToolLocked(status.MessageId, status.ToolCallId, name, now);
                    }
                    else if (status.Status is ToolCallStatus.Completed or ToolCallStatus.Failed or ToolCallStatus.Cancelled)
                    {
                        if (!_isFumo) FinishToolLocked(status.ToolCallId, status.Status,
                            status.StatusText, status.DurationMs, now);
                    }
                    break;
                case SessionEvent.MessageFinished finished:
                    RememberMessageLocked(finished.FinalMessage with
                    {
                        CumulativeUsage = finished.CumulativeUsage ?? finished.FinalMessage.CumulativeUsage,
                    });
                    if (finished.FinalMessage.Role == MessageRole.User)
                    {
                        if (_openTurns.TryGetValue(finished.MessageId, out var userTurn))
                            userTurn.UserChars = finished.FinalMessage.Blocks.OfType<TextBlock>().Sum(block => block.Text.Length);
                    }
                    else
                    {
                        if (_openTurns.Values.LastOrDefault() is { } turn)
                        {
                            turn.AssistantMessages++;
                            turn.InputTokens = _latestUsage?.InputTokens;
                            turn.OutputTokens = _latestUsage?.OutputTokens;
                        }
                        if (!_isFumo && PerfTracer.Enabled)
                        {
                            if (_openModel is { } model && model.MessageId == finished.MessageId)
                                FinishModelLocked(model, now);
                            PublishUsageLocked(finished.MessageId, finished.CumulativeUsage ?? finished.FinalMessage.CumulativeUsage, now);
                        }
                    }
                    PublishContextLocked(force: true);
                    break;
                case SessionEvent.TurnSettled settled:
                    foreach (var turn in _openTurns.Values.ToArray()) CompleteLocked(turn, settled.Outcome.ToString(), settled.Summary);
                    _openTurns.Clear();
                    if (!_isFumo) FinishExternalTurnLocked(settled.Outcome, settled.Summary);
                    _sessionActive = false;
                    PublishContextLocked(force: true);
                    break;
                case SessionEvent.ActivityChanged activity:
                    _sessionActive = activity.Activity != SessionActivity.Idle;
                    break;
            }
            if (_openTurns.Values.LastOrDefault() is { } current) current.Events++;
        }
        Interlocked.Increment(ref _events);
    }

    private void AppendEstimateLocked(string messageId, int index, string delta)
    {
        if (!_messageEstimates.TryGetValue(messageId, out var estimate)) return;
        estimate.Append(index, delta);
        _contextDirty = true;
    }

    private void EnsureExternalTurnLocked(long now)
    {
        if (_currentTurnTraceId != 0 || !PerfTracer.Enabled) return;
        _currentTurnTraceId = PerfTracer.NewId();
        _currentTurnSpanId = PerfTracer.NewId();
        _turnStartTicks = now;
        StartLiveSpan("turn", _currentTurnSpanId, 0, now);
    }

    private List<KeyValuePair<string, string>> SessionTags() =>
    [
        new("model", _session?.CurrentModel ?? _currentModel ?? string.Empty),
        new("backend", _backend ?? string.Empty),
        new("sessionId", _sessionId ?? string.Empty),
        new("source", "host-events"),
    ];

    private void StartLiveSpan(string name, long id, long parent, long started)
    {
        if (PerfTracer.Sink is IPerfLiveSink live)
            live.OnSpanProgress(new PerfSpanStart(_currentTurnTraceId, id, parent, name, started), SessionTags());
    }

    private void FinishModelLocked(OpenModel model, long now)
    {
        PerfTracer.EmitCompleted(_currentTurnTraceId, model.SpanId, _currentTurnSpanId, "model.request",
            model.StartTicks, now, tags: SessionTags());
        _openModel = null;
    }

    private void PublishUsageLocked(string messageId, UsageInfo? usage, long now)
    {
        if (usage is null || usage.IsUnknown || !_usageMessages.Add(messageId)) return;
        EnsureExternalTurnLocked(now);
        if (_currentTurnTraceId == 0) return;
        var tags = SessionTags();
        if (usage.InputTokens is { } input) tags.Add(new("promptTokens", PerfTracer.Invariant(input)));
        if (usage.OutputTokens is { } output) tags.Add(new("completionTokens", PerfTracer.Invariant(output)));
        if (usage.CacheReadTokens is { } cached) tags.Add(new("cachedTokens", PerfTracer.Invariant(cached)));
        tags.Add(new("usageSource", "message"));
        PerfTracer.EmitCompleted(_currentTurnTraceId, PerfTracer.NewId(),
            _modelByMessage.GetValueOrDefault(messageId, _currentTurnSpanId), "model.usage", now, now, tags: tags);
    }

    private void ObserveToolLocked(string messageId, ToolCallBlock tool, long now)
    {
        if (tool.Status == ToolCallStatus.Running)
        {
            StartToolLocked(messageId, tool.ToolCallId, tool.ToolName, now);
            return;
        }

        if (tool.Status is not (ToolCallStatus.Completed or ToolCallStatus.Failed or ToolCallStatus.Cancelled))
            return;

        // 计数与 span 发射解耦：性能追踪关闭时工具不会被 StartToolLocked 记录，
        // 之前因此在 turns 记录里写出 tools=0（实际 345 次调用）。
        if (_countedTools.Add(tool.ToolCallId) && _openTurns.Values.LastOrDefault() is { } turn)
            turn.Tools++;

        FinishToolLocked(tool.ToolCallId, tool.Status, tool.StatusText, tool.DurationMs, now);
    }

    private void StartToolLocked(string messageId, string toolId, string? name, long now)
    {
        if (!PerfTracer.Enabled || _openTools.ContainsKey(toolId)) return;
        EnsureExternalTurnLocked(now);
        if (_openModel is { } model && model.MessageId == messageId) FinishModelLocked(model, now);
        name ??= _toolNames.GetValueOrDefault(toolId);
        var tool = new OpenTool(PerfTracer.NewId(), name, now);
        _openTools.Add(toolId, tool);
        var tags = SessionTags();
        tags.Add(new("tool", name ?? string.Empty));
        if (PerfTracer.Sink is IPerfLiveSink live)
            live.OnSpanProgress(new PerfSpanStart(_currentTurnTraceId, tool.SpanId, _currentTurnSpanId, "tool", now), tags);
    }

    private void FinishToolLocked(string toolId, ToolCallStatus status, string? detail, long? durationMs, long now)
    {
        if (!_openTools.Remove(toolId, out var tool)) return;
        var tags = SessionTags();
        tags.Add(new("tool", tool.Name ?? string.Empty));
        tags.Add(new("ok", status == ToolCallStatus.Completed ? "true" : "false"));
        if (durationMs is { } duration) tags.Add(new("backendDurationMs", PerfTracer.Invariant(duration)));
        var start = new PerfSpanStart(_currentTurnTraceId, tool.SpanId, _currentTurnSpanId, "tool", tool.StartTicks);
        PerfTracer.Sink?.OnSpanEnd(new PerfSpanEnd(start,
            status == ToolCallStatus.Cancelled ? PerfSpanStatus.Cancelled :
            status == ToolCallStatus.Failed ? PerfSpanStatus.Error : PerfSpanStatus.Ok,
            status == ToolCallStatus.Failed ? detail : null, tags, now));
    }

    private void FinishExternalTurnLocked(SessionOutcome outcome, string? detail)
    {
        if (_currentTurnTraceId == 0) return;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_openModel is { } model) FinishModelLocked(model, now);
        foreach (var id in _openTools.Keys.ToArray()) FinishToolLocked(id, ToolCallStatus.Cancelled, null, null, now);
        var tags = SessionTags();
        tags.Add(new("outcome", outcome.ToString()));
        PerfTracer.Sink?.OnSpanEnd(new PerfSpanEnd(
            new PerfSpanStart(_currentTurnTraceId, _currentTurnSpanId, 0, "turn", _turnStartTicks),
            outcome == SessionOutcome.Interrupted ? PerfSpanStatus.Cancelled :
            outcome == SessionOutcome.Failed ? PerfSpanStatus.Error : PerfSpanStatus.Ok,
            outcome == SessionOutcome.Failed ? detail : null, tags, now));
        _currentTurnTraceId = _currentTurnSpanId = 0;
        _modelByMessage.Clear();
        _usageMessages.Clear();
        _toolNames.Clear();
        _countedTools.Clear();
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
            if (_sinkActive)
            {
                lock (_gate) PublishContextLocked();
                ReportHostMetrics();
            }
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

    private sealed record OpenTool(long SpanId, string? Name, long StartTicks);
    private sealed record OpenModel(long SpanId, long StartTicks, string MessageId);

    private sealed class MessageEstimate
    {
        private readonly Dictionary<int, TextEstimate> _blocks = [];
        public long ChatTokens => _blocks.Values.Sum(block => block.Tokens);
        public long ResultTokens => _blocks.Values.Sum(block => block.ResultTokens);

        public void Append(int index, string text)
        {
            if (!_blocks.TryGetValue(index, out var block)) _blocks[index] = block = new TextEstimate();
            block.Append(text);
        }

        public void SetBlock(int index, MessageBlock value)
        {
            var block = new TextEstimate();
            switch (value)
            {
                case TextBlock text: block.Append(text.Text); break;
                case ThinkingBlock thinking: block.Append(thinking.Text); break;
                case ToolCallBlock tool:
                    block.Append(tool.InputJson);
                    block.ToolName = tool.ToolName;
                    block.ResultTokens = PerfTracer.TokensOf(tool.Output);
                    break;
            }
            _blocks[index] = block;
        }

        public string? ToolName(int index) => _blocks.GetValueOrDefault(index)?.ToolName;
    }

    private sealed class TextEstimate
    {
        private long _ascii;
        private long _other;
        public long Tokens => (_ascii + 3) / 4 + _other;
        public long ResultTokens { get; set; }
        public string? ToolName { get; set; }
        public void Append(string text)
        {
            foreach (var character in text)
                if (character < 128) _ascii++; else _other++;
        }
    }

    private sealed class OpenTurn(string userMessageId)
    {
        public string UserMessageId { get; } = userMessageId;
        public DateTimeOffset StartedUtc { get; set; }
        public int UserChars { get; set; }
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
