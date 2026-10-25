using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Fufucode.Claude;

public sealed record ClaudeStreamOptions(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    int MaxRecordBytes = 16 * 1024 * 1024,
    int EventCapacity = 4096,
    TimeSpan? StartupTimeout = null,
    TimeSpan? ShutdownTimeout = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    IClaudeDiagnosticSink? DiagnosticSink = null);

/// <summary>Observed exit of this exact Claude child process, retained after handles are disposed.</summary>
public sealed record ClaudeProcessExit(Guid Generation, int ProcessId, int ExitCode, DateTimeOffset ObservedAtUtc);

public sealed class ClaudeStreamException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Safe diagnostic metadata; raw payloads and stderr text are excluded.</summary>
public sealed record ClaudeDiagnosticEvent(DateTimeOffset Timestamp, string EventType, string? RequestId,
    string? FrameType, long? DurationMs, int? ExitCode, string? ErrorCode, string? Outcome = null);

public interface IClaudeDiagnosticSink
{
    void Record(ClaudeDiagnosticEvent entry);
}

/// <summary>
/// 官方 Claude Code CLI 的 stream-json 传输：一个连接拥有一个子进程、一个工作目录。
/// 单向事件流（无请求配对字典），出站为逐行 JSON。审批回灌走 control_response。
/// </summary>
public sealed class ClaudeStreamClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ClaudeStreamOptions _options;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly ArrayBufferWriter<byte> _writeBuffer = new(4096);
    private readonly Channel<JsonElement> _records;
    private readonly Channel<ClaudeStreamEvent> _events;
    private readonly StringBuilder _stderr = new();
    private readonly object _diagnosticLock = new();
    private readonly Task _stdoutTask;
    private readonly Task _stderrTask;
    private readonly Task _dispatchTask;
    private readonly Task _exitTask;
    private readonly int _processId;
    private Exception? _failure;
    private ClaudeProcessExit? _processExit;
    private Task? _disposeTask;

    public Guid Generation { get; } = Guid.NewGuid();

    public ChannelReader<ClaudeStreamEvent> Events => _events.Reader;

    public Exception? FailureReason => Volatile.Read(ref _failure);

    public ClaudeProcessExit? ProcessExit => Volatile.Read(ref _processExit);

    public bool HasExited => _process.HasExited;

    public int? ExitCode => ProcessExit?.ExitCode ?? (_process.HasExited ? _process.ExitCode : null);

    public string StderrTail { get { lock (_diagnosticLock) return _stderr.ToString(); } }

    private ClaudeStreamClient(Process process, ClaudeStreamOptions options)
    {
        _process = process;
        _processId = process.Id;
        _options = options;
        _records = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(options.EventCapacity)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        _events = Channel.CreateBounded<ClaudeStreamEvent>(new BoundedChannelOptions(options.EventCapacity)
        { SingleReader = false, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        _stdoutTask = ReadStdoutAsync();
        _stderrTask = ReadStderrAsync();
        _dispatchTask = DispatchAsync();
        _exitTask = WatchExitAsync();
    }

    public static ClaudeStreamClient Start(ClaudeStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxRecordBytes < 1024 || options.EventCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid stream limits.");
        if (!File.Exists(options.ExecutablePath))
            throw new FileNotFoundException("Claude Code executable is missing.", options.ExecutablePath);
        if (!Directory.Exists(options.WorkingDirectory))
            throw new DirectoryNotFoundException(options.WorkingDirectory);
        var start = new ProcessStartInfo(options.ExecutablePath)
        {
            WorkingDirectory = options.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in options.Arguments) start.ArgumentList.Add(argument);
        if (options.EnvironmentVariables is { } environment)
            foreach (var (name, value) in environment) start.Environment[name] = value;
        Process process;
        try
        {
            process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.Start();
        }
        catch (Exception error)
        {
            throw new ClaudeStreamException("Unable to start the Claude Code process.", error);
        }
        return new ClaudeStreamClient(process, options);
    }

    /// <summary>写出 initialize 控制请求；CLI 随后回 control_response 并（在首条用户消息时）发 init。</summary>
    public Task SendInitializeAsync(CancellationToken cancellationToken = default) =>
        WriteLineAsync(ClaudeProtocol.BuildInitialize(Guid.NewGuid().ToString("N")), cancellationToken);

    /// <summary>提交一条用户消息；返回只表示已写出，不代表回合结束。</summary>
    public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return WriteLineAsync(ClaudeProtocol.BuildUserMessage(text), cancellationToken);
    }

    /// <summary>请求中断当前回合。</summary>
    public Task SendInterruptAsync(CancellationToken cancellationToken = default) =>
        WriteLineAsync(ClaudeProtocol.BuildInterrupt(Guid.NewGuid().ToString("N")), cancellationToken);

    /// <summary>
    /// 回灌审批结论。request_id 必须原样来自 <see cref="ClaudePermissionRequest.RequestId"/>；
    /// 允许时原样回填 CLI 给出的 input，不改写工具参数。
    /// </summary>
    public Task SendPermissionResponseAsync(ClaudePermissionRequest request, ClaudePermissionDecision decision,
        string? message = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var allowInput = decision == ClaudePermissionDecision.Allow ? ParseInput(request.InputJson) : null;
        var line = ClaudeProtocol.BuildPermissionResponse(request.RequestId, decision, message, allowInput);
        Record("permission_response", request.RequestId, "can_use_tool",
            outcome: decision == ClaudePermissionDecision.Allow ? "allow" : "deny");
        return WriteLineAsync(line, cancellationToken);
    }

    private static JsonElement? ParseInput(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken);
        try
        {
            if (_failure is { } failure) throw new ClaudeStreamException("Claude stream connection has failed.", failure);
            _writeBuffer.Clear();
            var maxBytes = Encoding.UTF8.GetMaxByteCount(line.Length) + 1;
            var span = _writeBuffer.GetSpan(maxBytes);
            var written = Encoding.UTF8.GetBytes(line, span);
            span[written] = (byte)'\n';
            _writeBuffer.Advance(written + 1);

            await _process.StandardInput.BaseStream.WriteAsync(_writeBuffer.WrittenMemory, cancellationToken);
            await _process.StandardInput.BaseStream.FlushAsync(cancellationToken);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            throw new ClaudeStreamException("Claude stream transport closed while writing.", error);
        }
        finally { _writer.Release(); }
    }

    private async Task ReadStdoutAsync()
    {
        var pipeReader = PipeReader.Create(_process.StandardOutput.BaseStream,
            new StreamPipeReaderOptions(bufferSize: 64 * 1024, leaveOpen: true));
        try
        {
            while (true)
            {
                var readResult = await pipeReader.ReadAsync().ConfigureAwait(false);
                var buffer = readResult.Buffer;

                while (TryReadLine(ref buffer, out var lineSequence))
                {
                    if (TryParseObject(lineSequence, out var parsed))
                    {
                        if (!_records.Writer.TryWrite(parsed))
                            throw new ClaudeStreamException("Stream dispatch queue exceeded its capacity.");
                    }
                    else
                    {
                        Record("stdout_non_json", errorCode: "ignored");
                    }
                }

                pipeReader.AdvanceTo(buffer.Start, buffer.End);

                if (readResult.IsCompleted)
                {
                    if (!buffer.IsEmpty && TryParseObject(buffer, out var tail))
                    {
                        if (!_records.Writer.TryWrite(tail))
                            throw new ClaudeStreamException("Stream dispatch queue exceeded its capacity.");
                    }
                    break;
                }
            }
        }
        catch (Exception error) { Fail(error); }
        finally
        {
            await pipeReader.CompleteAsync().ConfigureAwait(false);
            _records.Writer.TryComplete();
        }
    }

    private bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> lineSequence)
    {
        var position = buffer.PositionOf((byte)'\n');
        if (position is null)
        {
            if (buffer.Length > _options.MaxRecordBytes)
                throw new ClaudeStreamException("Stream record exceeds the configured size limit.");
            lineSequence = default;
            return false;
        }

        var line = buffer.Slice(0, position.Value);
        if (line.Length > _options.MaxRecordBytes)
            throw new ClaudeStreamException("Stream record exceeds the configured size limit.");

        if (line.Length > 0 && line.Slice(line.Length - 1).FirstSpan[0] == (byte)'\r')
        {
            line = line.Slice(0, line.Length - 1);
        }

        lineSequence = line;
        buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
        return true;
    }

    private static bool TryParseObject(in ReadOnlySequence<byte> lineSequence, out JsonElement element)
    {
        element = default;
        if (lineSequence.IsEmpty) return false;
        // 快速前缀检查：必须以 '{' 开头
        var firstByte = (byte)0;
        foreach (var segment in lineSequence)
        {
            var span = segment.Span;
            for (var i = 0; i < span.Length; i++)
            {
                if (char.IsWhiteSpace((char)span[i])) continue;
                firstByte = span[i];
                break;
            }
            if (firstByte != 0) break;
        }
        if (firstByte != (byte)'{') return false;

        try
        {
            using var document = JsonDocument.Parse(lineSequence);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }

    private async Task DispatchAsync()
    {
        try
        {
            await foreach (var record in _records.Reader.ReadAllAsync())
            {
                var kind = ClaudeProtocol.Classify(record);
                ClaudeStreamEvent? mapped = kind switch
                {
                    ClaudeFrameKind.System => MapSystem(record),
                    ClaudeFrameKind.Assistant => new ClaudeAssistantEvent(Generation,
                        ClaudeProtocol.ParseAssistant(record), record),
                    ClaudeFrameKind.User => new ClaudeToolResultEvent(Generation,
                        ClaudeProtocol.ParseToolResults(record), record),
                    ClaudeFrameKind.Result => ClaudeProtocol.ParseResult(record) is { } result
                        ? new ClaudeResultEvent(Generation, result, record) : null,
                    ClaudeFrameKind.StreamEvent => ClaudeProtocol.ParseTextDelta(record) is { } delta
                        ? new ClaudeTextDeltaEvent(Generation, delta, record) : null,
                    ClaudeFrameKind.ControlRequest => ClaudeProtocol.ParsePermissionRequest(record) is { } permission
                        ? new ClaudePermissionRequestEvent(Generation, permission, record) : null,
                    ClaudeFrameKind.ControlResponse => MapControlResponse(record),
                    _ => null,
                };
                if (mapped is null) mapped = new ClaudeIgnoredEvent(Generation,
                    ClaudeProtocol.OptionalString(record, "subtype") ?? ClaudeProtocol.OptionalString(record, "type") ?? "unknown",
                    record);
                if (!_events.Writer.TryWrite(mapped))
                    throw new ClaudeStreamException("Stream event queue exceeded its capacity; consumer is too slow.");
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private ClaudeStreamEvent? MapSystem(JsonElement record)
    {
        var subtype = ClaudeProtocol.OptionalString(record, "subtype");
        if (subtype == "init" && ClaudeProtocol.ParseInit(record) is { } init)
            return new ClaudeInitEvent(Generation, init, record);
        if (subtype == "permission_denied")
        {
            Record("permission_denied");
            return new ClaudePermissionDeniedEvent(Generation, record);
        }
        // hook_started / hook_response / commands_changed / thinking_tokens 等必须宽容跳过。
        return new ClaudeIgnoredEvent(Generation, subtype ?? "system", record);
    }

    private ClaudeControlResponseEvent MapControlResponse(JsonElement record)
    {
        var response = record.ValueKind == JsonValueKind.Object && record.TryGetProperty("response", out var payload)
            ? payload : default;
        var requestId = ClaudeProtocol.OptionalString(response, "request_id");
        var success = ClaudeProtocol.OptionalString(response, "subtype") == "success";
        return new ClaudeControlResponseEvent(Generation, requestId, success, record);
    }

    private async Task ReadStderrAsync()
    {
        var chars = new char[4096];
        try
        {
            int count;
            while ((count = await _process.StandardError.ReadAsync(chars)) > 0)
                lock (_diagnosticLock)
                {
                    _stderr.Append(chars, 0, count);
                    if (_stderr.Length > 65536) _stderr.Remove(0, _stderr.Length - 65536);
                }
        }
        catch (Exception error) { Fail(error); }
    }

    private async Task WatchExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync();
            Volatile.Write(ref _processExit, new ClaudeProcessExit(Generation, _processId, _process.ExitCode, DateTimeOffset.UtcNow));
            // Drain the final frames before reporting failure, so a closing result frame is not lost.
            await _stdoutTask;
            await _dispatchTask;
            await _stderrTask;
            Record("process_exited", exitCode: _process.ExitCode);
            Fail(new ClaudeStreamException($"Claude Code process exited with code {_process.ExitCode}. stderr: {StderrTail}"));
        }
        catch (Exception error) { Fail(error); }
    }

    private void Fail(Exception error)
    {
        if (Interlocked.CompareExchange(ref _failure, error, null) is not null) return;
        Record("connection_failed", exitCode: _process.HasExited ? _process.ExitCode : null,
            errorCode: error is ClaudeStreamException ? "protocol_or_process" : "io_error");
        _records.Writer.TryComplete(error);
        _events.Writer.TryComplete(error);
    }

    private void Record(string eventType, string? requestId = null, string? frameType = null,
        long? durationMs = null, int? exitCode = null, string? errorCode = null, string? outcome = null)
    {
        try
        {
            _options.DiagnosticSink?.Record(new ClaudeDiagnosticEvent(DateTimeOffset.UtcNow, eventType, requestId,
                frameType, durationMs, exitCode, errorCode, outcome));
        }
        catch { /* Diagnostics must never interrupt protocol transport. */ }
    }

    /// <summary>关闭 stdin 让 CLI 收尾；超时后强制结束进程。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        try { _process.StandardInput.Close(); }
        catch (Exception) { /* stdin already closed */ }
        var timeout = _options.ShutdownTimeout ?? TimeSpan.FromSeconds(5);
        try
        {
            await _process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (Exception) { /* best effort */ }
        }
    }

    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeAsyncCore());

    private async Task DisposeAsyncCore()
    {
        try { await StopAsync(); } catch (Exception) { /* shutdown is best effort */ }
        foreach (var task in new[] { _stdoutTask, _stderrTask, _dispatchTask, _exitTask })
            try { await task; } catch (Exception) { /* observed via events channel */ }
        _process.Dispose();
        _writer.Dispose();
    }
}
