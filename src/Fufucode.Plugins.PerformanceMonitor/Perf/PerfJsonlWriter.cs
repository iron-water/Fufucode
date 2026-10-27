using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// 有界通道 + 后台写入的 JSONL 记录器。容量满时丢弃最旧记录并计数，
/// 既不阻塞模型流/UI 线程，也不让磁盘慢把整个进程拖住。
/// 文件布局：<c>&lt;dataRoot&gt;/perf/perf-yyyyMMdd-segmentNNN.jsonl</c>，超过大小上限即轮转。
/// </summary>
public sealed class PerfJsonlWriter : IDisposable
{
    private const int Capacity = 65_536;
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    private readonly Channel<object> _queue;
    private int _disposed;
    private readonly Task _writerTask;
    private readonly string _rootDirectory;
    private readonly long _segmentBytes;
    private readonly object _fileGate = new();
    private FileStream? _stream;
    private long _dropped;
    private long _written;

    public PerfJsonlWriter(string directory, int segmentMb)
    {
        _rootDirectory = directory;
        _segmentBytes = segmentMb * 1024L * 1024L;
        _queue = Channel.CreateBounded<object>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        }, dropped =>
        {
            Interlocked.Increment(ref _dropped);
            if (dropped is TaskCompletionSource barrier) barrier.TrySetResult();
        });
        _writerTask = WriteLoopAsync(_queue.Reader);
    }

    public string Directory => _rootDirectory;

    public long Dropped => Interlocked.Read(ref _dropped);

    public long Written => Interlocked.Read(ref _written);

    public void Enqueue(SpanRecord record) { if (!_queue.Writer.TryWrite(record)) Interlocked.Increment(ref _dropped); }

    public void Enqueue(PerfEventRecord record) { if (!_queue.Writer.TryWrite(record)) Interlocked.Increment(ref _dropped); }

    /// <summary>等待此前记录写出，保持通道可用；导出后仍继续采集。</summary>
    public void Flush(TimeSpan timeout)
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(barrier)) return;
        try { barrier.Task.Wait(timeout); }
        catch (AggregateException) { }
    }

    /// <summary>把早于保留期的分片文件删除；文件仍被占用则保留到下次。</summary>
    public void Prune(DateTime cutoffUtc)
    {
        if (!System.IO.Directory.Exists(_rootDirectory)) return;
        foreach (var file in System.IO.Directory.EnumerateFiles(_rootDirectory, "perf-*.jsonl"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoffUtc) continue;
                if (Path.GetFileName(file) == CurrentFile()) continue;
                File.Delete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private string? _currentPath;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        try { _writerTask.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { }
    }

    private string CurrentFile()
    {
        lock (_fileGate) return _currentPath ?? string.Empty;
    }

    private async Task WriteLoopAsync(ChannelReader<object> reader)
    {
        try
        {
            using var stream = new MemoryStream(16 * 1024);
            using var writer = new Utf8JsonWriter(stream, WriterOptions);
            await foreach (var item in reader.ReadAllAsync())
            {
                if (item is TaskCompletionSource barrier)
                {
                    _stream?.Flush();
                    barrier.TrySetResult();
                    continue;
                }
                stream.SetLength(0);
                writer.Reset(stream);
                switch (item)
                {
                    case SpanRecord span: SpanJson.Write(writer, span); break;
                    case PerfEventRecord record: EventJson.Write(writer, record); break;
                    default: continue;
                }
                writer.Flush();
                var target = EnsureStream((int)stream.Length);
                stream.Position = 0;
                stream.CopyTo(target);
                target.WriteByte((byte)'\n');
                // 逐条刷新：FileStream 有 64KB 缓冲，不刷新时正在运行的进程日志会长时间显示 0 字节，
                // 也无法被其它工具实时读取。span 数量本身不大，这个代价可接受。
                target.Flush();
                Interlocked.Increment(ref _written);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 磁盘不可写时停止落盘，内存数据仍可用；不把异常抛回模型/UI 线程。
            _queue.Writer.TryComplete();
        }
        finally
        {
            _queue.Writer.TryComplete();
            while (reader.TryRead(out var pending))
                if (pending is TaskCompletionSource barrier) barrier.TrySetResult();
            lock (_fileGate)
            {
                _stream?.Dispose();
                _stream = null;
            }
        }
    }

    private FileStream EnsureStream(int pendingBytes)
    {
        lock (_fileGate)
        {
            System.IO.Directory.CreateDirectory(_rootDirectory);
            if (_stream is { } existing && existing.Length + pendingBytes <= _segmentBytes) return existing;
            _stream?.Dispose();
            var day = DateTime.UtcNow.ToString("yyyyMMdd");
            var segment = 0;
            string path;
            do { path = Path.Combine(_rootDirectory, $"perf-{day}-{segment:000}.jsonl"); segment++; }
            while (File.Exists(path) && new FileInfo(path).Length >= _segmentBytes);
            _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            _currentPath = path;
            return _stream;
        }
    }
}

/// <summary>游离事件落盘形态。</summary>
public sealed record PerfEventRecord(long TimestampTicks, DateTimeOffset TimestampUtc, string Kind, string Name,
    double Value, string? Detail);

internal static class SpanJson
{
    public static void Write(Utf8JsonWriter writer, SpanRecord span)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "span");
        writer.WriteNumber("traceId", span.TraceId);
        writer.WriteNumber("spanId", span.SpanId);
        writer.WriteNumber("parentId", span.ParentSpanId);
        writer.WriteString("name", span.Name);
        writer.WriteNumber("startTicks", span.StartTicks);
        writer.WriteNumber("endTicks", span.EndTicks);
        writer.WriteString("endUtc", span.EndUtc);
        writer.WriteNumber("durationMs", Math.Round(span.DurationMs, 3));
        writer.WriteString("status", span.Status);
        if (span.Error is { } error) writer.WriteString("error", error);
        if (span.SessionId is { } session) writer.WriteString("sessionId", session);
        writer.WriteStartObject("tags");
        foreach (var (key, value) in span.Tags)
            writer.WriteString(key, Redactor.Text(value));
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}

internal static class EventJson
{
    public static void Write(Utf8JsonWriter writer, PerfEventRecord record)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "event");
        writer.WriteNumber("timestampTicks", record.TimestampTicks);
        writer.WriteString("timestampUtc", record.TimestampUtc);
        writer.WriteString("kind", record.Kind);
        writer.WriteString("name", record.Name);
        writer.WriteNumber("value", record.Value);
        if (record.Detail is { } detail) writer.WriteString("detail", Redactor.Text(detail));
        writer.WriteEndObject();
    }
}
