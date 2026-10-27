using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Fufucode.Core.Diagnostics;

/// <summary>
/// 进程级性能埋点入口。插件激活时写入 <see cref="Sink"/>；未启用时一切退化为 null 判断，
/// 不创建 span、不分配 tag 列表、不经过 AsyncLocal。
/// </summary>
public static class PerfTracer
{
    private static IPerfSink? s_sink;
    private static IPerfModeProvider? s_modeProvider;

    /// <summary>每个异步流中最多保留的 tag 数；超出部分被忽略，避免误埋点撑大记录。</summary>
    public const int MaxTagsPerSpan = 16;

    /// <summary>单个 tag 值的最大字符数。</summary>
    public const int MaxTagLength = 256;

    /// <summary>当前接收端；未启用时为 null。</summary>
    public static IPerfSink? Sink => Volatile.Read(ref s_sink);

    public static bool Enabled => Sink is not null;

    /// <summary>把数值转成不变区域字符串（span 起始 tag 只有字符串形式）。</summary>
    public static string Invariant(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static PerfMode Mode => Sink is null ? PerfMode.Off : s_modeProvider?.Mode ?? PerfMode.Standard;

    /// <summary>安装接收端。插件必须用与 <see cref="IPerfSink"/> 相同的程序集身份实现该接口。</summary>
    public static void Attach(IPerfSink sink, IPerfModeProvider? modeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (Sink is not null) throw new InvalidOperationException("已有性能接收端处于激活状态。");
        Volatile.Write(ref s_modeProvider, modeProvider);
        Volatile.Write(ref s_sink, sink);
    }

    /// <summary>只移除同一个接收端；返回是否真的移除了。</summary>
    public static bool Detach(IPerfSink sink)
    {
        if (!ReferenceEquals(Sink, sink)) return false;
        Volatile.Write(ref s_sink, null);
        Volatile.Write(ref s_modeProvider, null);
        return true;
    }

    /// <summary>通知性能接收端当前会话标识（项目名或会话标题）。</summary>
    public static void SetSessionId(string? sessionId)
    {
        Record(PerfEventKind.SessionEvent, "session.attach", 0, sessionId);
    }

    /// <summary>开始一个 span；未启用时返回默认值（Dispose 无操作）。</summary>
    public static PerfSpan Step(string name, string? tagKey = null, string? tagValue = null)
    {
        var sink = Sink;
        if (sink is null) return default;
        return PerfSpan.Start(sink, Mode, name, tagKey, tagValue);
    }

    /// <summary>记录游离事件；未启用时无操作。</summary>
    public static void Record(PerfEventKind kind, string name, double value, string? detail = null)
    {
        Sink?.OnEvent(new PerfEvent(kind, name, value, Stopwatch.GetTimestamp(), detail));
    }

    /// <summary>
    /// 直接投递一条已完成的 span（带真实起止时间）。用于启动追踪这类“事后回放”的记录：
    /// 接收端不必在事件发生时在线，也能得到准确的时长。
    /// </summary>
    public static void EmitCompleted(long traceId, long spanId, long parentSpanId, string name,
        long startTicks, long endTicks, string? error = null,
        IReadOnlyList<KeyValuePair<string, string>>? tags = null)
    {
        var sink = Sink;
        if (sink is null) return;
        var start = new PerfSpanStart(traceId, spanId, parentSpanId, name, startTicks);
        sink.OnSpanEnd(new PerfSpanEnd(start, error is null ? PerfSpanStatus.Ok : PerfSpanStatus.Error,
            error, tags, endTicks));
    }

    /// <summary>生成一个非零 id（启动回放等调用方需要自行组装 trace 树时使用）。</summary>
    public static long NewId() => NextId();

    /// <summary>生成 64 位随机 id（trace/span 共用），保证非零。</summary>
    internal static long NextId()
    {
        var bytes = RandomNumberGenerator.GetBytes(8);
        var id = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes) & long.MaxValue;
        return id == 0 ? 1 : id;
    }

    /// <summary>
    /// 把任意用户/模型文本压缩成单个 tag 值：折叠换行、截断到上限。
    /// 脱敏由插件 sink 统一负责，核心不在此处猜测敏感格式。
    /// </summary>
    public static string TagText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Length <= MaxTagLength) return value;
        return string.Concat(value.AsSpan(0, MaxTagLength - 1), "…");
    }
}

/// <summary>
/// 单个 span 句柄。结构体 + <c>using</c>，无分配；默认值是无操作句柄。
/// 父子关系由 AsyncLocal 自动传播，跨 await、Task.Run 与 Task.WhenAll 都成立。
/// </summary>
public struct PerfSpan : IDisposable
{
    private static readonly AsyncLocal<SpanContext?> s_current = new();

    private IPerfSink? _sink;
    private PerfSpanStart _start;
    private List<KeyValuePair<string, string>>? _tags;
    private SpanContext? _previous;
    private SpanContext? _installed;
    private PerfSpanStatus _status;
    private string? _error;
    private bool _started;
    private bool _finished;

    internal static PerfSpan Start(IPerfSink sink, PerfMode mode, string name, string? tagKey, string? tagValue)
    {
        // 根 span：调用方显式开启（回合、后台任务）。子 span 继承当前 trace。
        var previous = s_current.Value;
        var traceId = previous?.TraceId ?? PerfTracer.NextId();
        var spanId = PerfTracer.NextId();
        // span id 为 0 在协议里表示“根”，碰撞极少但仍规避。
        if (spanId == 0) spanId = PerfTracer.NextId() | 1;
        var start = new PerfSpanStart(traceId, spanId, previous?.SpanId ?? 0, name, Stopwatch.GetTimestamp());
        var context = new SpanContext(traceId, spanId);
        s_current.Value = context;

        var span = new PerfSpan
        {
            _sink = sink,
            _start = start,
            _previous = previous,
            _installed = context,
            _started = true,
        };
        if (tagKey is not null) span.Tag(tagKey, tagValue);
        if (mode == PerfMode.Detailed) Safe(sink, () => sink.OnSpanStart(start));
        return span;
    }

    /// <summary>当前 span 的 trace id（未启用时为 0），用于把外部事件挂到同一回合。</summary>
    public long TraceId => _started ? _start.TraceId : 0;

    public long SpanId => _started ? _start.SpanId : 0;

    /// <summary>附加字符串 tag；超长截断，数量超出上限后静默忽略。</summary>
    public void Tag(string key, string? value)
    {
        if (!_started) return;
        _tags ??= new List<KeyValuePair<string, string>>(4);
        if (_tags.Count >= PerfTracer.MaxTagsPerSpan) return;
        _tags.Add(new(key, PerfTracer.TagText(value)));
    }

    public void Tag(string key, long value) => Tag(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void Tag(string key, bool value) => Tag(key, value ? "true" : "false");

    /// <summary>把 span 标记为失败；多次调用保留第一个错误。</summary>
    public void Fail(string? error)
    {
        if (!_started || _finished) return;
        _status = PerfSpanStatus.Error;
        _error ??= error;
    }

    /// <summary>标记为用户取消（不影响错误分类）。</summary>
    public void Cancel()
    {
        if (!_started || _finished || _status == PerfSpanStatus.Error) return;
        _status = PerfSpanStatus.Cancelled;
    }

    public void Dispose()
    {
        if (!_started || _finished) return;
        _finished = true;
        // 恢复 AsyncLocal：即使调用方忘了 using，嵌套 span 也不会把父关系挂到已结束的 span 上。
        if (ReferenceEquals(s_current.Value, _installed)) s_current.Value = _previous;
        var sink = _sink;
        _sink = null;
        if (sink is null) return;
        var end = new PerfSpanEnd(_start, _status, _error,
            _tags is { Count: > 0 } ? _tags.ToArray() : null);
        Safe(sink, () => sink.OnSpanEnd(end));
    }

    private static void Safe(IPerfSink sink, Action action)
    {
        try { action(); }
        catch (Exception error) { Trace.TraceWarning("Perf sink failed: {0}", error.Message); }
    }

    private sealed record SpanContext(long TraceId, long SpanId);
}
