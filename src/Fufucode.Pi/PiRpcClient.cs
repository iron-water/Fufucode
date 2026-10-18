using Fufucode.Core;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Fufucode.Pi;


/// <summary>One RPC connection owns exactly one Pi process and one project directory.</summary>
public sealed class PiRpcClient : IRpcClient
{
    private readonly Process _process;
    private readonly PiRpcOptions _options;
    private readonly ConcurrentDictionary<string, (string Command, TaskCompletionSource<RpcResponse> Waiter)> _pending = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly Channel<JsonElement> _records;
    private readonly Channel<RpcEvent> _events;
    private readonly StringBuilder _stderr = new();
    private readonly object _diagnosticLock = new();
    private readonly Task _stdoutTask;
    private readonly Task _stderrTask;
    private readonly Task _dispatchTask;
    private readonly Task _exitTask;
    private Exception? _failure;
    private readonly object _shutdownGate = new();
    private readonly int _processId;
    private Task? _stopTask;
    private Task? _disposeTask;
    private RpcProcessExit? _processExit;

    private void Record(string eventType, string? requestId = null, string? command = null,
        long? durationMs = null, int? exitCode = null, string? errorCode = null)
    {
        try { _options.DiagnosticSink?.Record(new PiDiagnosticEvent(DateTimeOffset.UtcNow, eventType,
            requestId, command, durationMs, exitCode, errorCode)); }
        catch { /* Diagnostics must never interrupt protocol transport. */ }
    }

    public Guid Generation { get; } = Guid.NewGuid();
    public ChannelReader<RpcEvent> Events => _events.Reader;
    public string StderrTail { get { lock (_diagnosticLock) return _stderr.ToString(); } }
    public RpcProcessExit? ProcessExit => Volatile.Read(ref _processExit);
    public Exception? FailureReason => Volatile.Read(ref _failure);
    public int? ExitCode => ProcessExit?.ExitCode ?? (_process.HasExited ? _process.ExitCode : null);

    private PiRpcClient(Process process, PiRpcOptions options)
    {
        _process = process;
        _processId = process.Id;
        _options = options;
        _records = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(options.EventCapacity)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        _events = Channel.CreateBounded<RpcEvent>(new BoundedChannelOptions(options.EventCapacity)
        { SingleReader = false, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        _stdoutTask = ReadStdoutAsync();
        _stderrTask = ReadStderrAsync();
        _dispatchTask = DispatchAsync();
        _exitTask = WatchExitAsync();
    }

    public static async Task<PiRpcClient> StartAsync(PiRpcOptions options, CancellationToken cancellationToken = default)
    {
        if (options.MaxRecordBytes < 1024 || options.EventCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid RPC limits.");
        if (!File.Exists(options.ExecutablePath))
            throw new FileNotFoundException("Pi runtime executable is missing.", options.ExecutablePath);
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
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in options.Arguments) start.ArgumentList.Add(argument);
        if (options.EnvironmentVariables is { } environment)
            foreach (var (name, value) in environment) start.Environment[name] = value;
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try { process.Start(); }
        catch (Exception error) { process.Dispose(); throw new RpcException("Unable to start Pi process.", error); }
        var client = new PiRpcClient(process, options);
        try
        {
            var response = await client.RequestAsync("get_state", null, options.StartupTimeout ?? TimeSpan.FromSeconds(15), cancellationToken);
            if (!response.Success) throw new RpcException($"Pi startup query failed: {response.Error}");
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task<RpcResponse> RequestAsync(string command, JsonElement? fields = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (_failure is { } existing) throw new RpcException("Pi RPC connection has failed.", existing);
        var id = Guid.NewGuid().ToString("N");
        var started = Stopwatch.GetTimestamp();
        Record("request_started", id, command);
        var waiter = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A transport failure can fault the waiter before the write or timed wait completes.
        _ = waiter.Task.ContinueWith(task => { _ = task.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        if (!_pending.TryAdd(id, (command, waiter))) throw new RpcException("Duplicate request ID.");
        try
        {
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("id", id);
                json.WriteString("type", command);
                if (fields is { } payload)
                {
                    if (payload.ValueKind != JsonValueKind.Object) throw new ArgumentException("RPC fields must be an object.", nameof(fields));
                    foreach (var property in payload.EnumerateObject())
                    {
                        if (property.Name is "id" or "type") throw new ArgumentException("Reserved RPC field.", nameof(fields));
                        property.WriteTo(json);
                    }
                }
                json.WriteEndObject();
            }
            buffer.WriteByte((byte)'\n');
            await _writer.WaitAsync(cancellationToken);
            try
            {
                if (_failure is { } failure) throw new RpcException("Pi RPC connection has failed.", failure);
                await _process.StandardInput.BaseStream.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), cancellationToken);
                await _process.StandardInput.BaseStream.FlushAsync(cancellationToken);
            }
            finally { _writer.Release(); }
            var response = await waiter.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10), cancellationToken);
            Record("request_completed", id, command, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                errorCode: response.Success ? null : "command_rejected");
            return response;
        }
        catch (TimeoutException error)
        {
            Record("request_failed", id, command, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                errorCode: "timeout");
            throw new RpcException($"RPC {command} timed out; execution status is unknown. Do not automatically retry commands with side effects.", error);
        }
        catch (Exception error)
        {
            Record("request_failed", id, command, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                errorCode: error is OperationCanceledException ? "canceled" : "transport_error");
            if (error is IOException or ObjectDisposedException)
                throw new RpcException($"Pi RPC transport closed during {command}; execution status is unknown.", error);
            throw;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>Extension UI replies are a separate stdin subprotocol and have no command response.</summary>
    public async Task SendExtensionUiResponseAsync(string requestId, PiExtensionUiResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(response);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("type", "extension_ui_response");
            json.WriteString("id", requestId);
            switch (response.Kind)
            {
                case PiExtensionUiResponseKind.Value: json.WriteString("value", response.Value); break;
                case PiExtensionUiResponseKind.Confirmation: json.WriteBoolean("confirmed", response.Confirmed!.Value); break;
                case PiExtensionUiResponseKind.Cancelled: json.WriteBoolean("cancelled", true); break;
            }
            json.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        await _writer.WaitAsync(cancellationToken);
        try
        {
            if (_failure is { } failure) throw new RpcException("Pi RPC connection has failed.", failure);
            await _process.StandardInput.BaseStream.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), cancellationToken);
            await _process.StandardInput.BaseStream.FlushAsync(cancellationToken);
        }
        finally { _writer.Release(); }
    }

    private async Task ReadStdoutAsync()
    {
        var chunk = new byte[32 * 1024];
        using var record = new MemoryStream();
        try
        {
            int count;
            while ((count = await _process.StandardOutput.BaseStream.ReadAsync(chunk)) != 0)
            {
                for (var i = 0; i < count; i++)
                {
                    if (chunk[i] == (byte)'\n')
                    {
                        var length = (int)record.Length;
                        if (length > 0 && record.GetBuffer()[length - 1] == (byte)'\r') length--;
                        if (length == 0) throw new RpcException("Empty RPC record.");
                        var json = new UTF8Encoding(false, true).GetString(record.GetBuffer(), 0, length);
                        using var parsed = JsonDocument.Parse(json);
                        if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new RpcException("RPC record is not an object.");
                        if (!_records.Writer.TryWrite(parsed.RootElement.Clone())) throw new RpcException("RPC dispatch queue exceeded its capacity.");
                        record.SetLength(0);
                    }
                    else
                    {
                        if (record.Length >= _options.MaxRecordBytes) throw new RpcException("RPC record exceeds the configured size limit.");
                        record.WriteByte(chunk[i]);
                    }
                }
            }
            if (record.Length != 0) throw new RpcException("RPC stdout ended with an incomplete record.");
        }
        catch (Exception error) { Fail(error); }
        finally { _records.Writer.TryComplete(); }
    }

    private async Task DispatchAsync()
    {
        try
        {
            await foreach (var record in _records.Reader.ReadAllAsync())
            {
                if (!record.TryGetProperty("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String)
                    throw new RpcException("RPC record lacks a type.");
                var type = typeValue.GetString()!;
                if (type == "response")
                {
                    if (!record.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String)
                        throw new RpcException("RPC response lacks an ID (parse or protocol error).");
                    var id = idValue.GetString()!;
                    if (!record.TryGetProperty("command", out var commandValue) || commandValue.ValueKind != JsonValueKind.String ||
                        !record.TryGetProperty("success", out var successValue) || successValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new RpcException("RPC response lacks required fields.");
                    var command = commandValue.GetString()!;
                    if (!_pending.TryGetValue(id, out var pending)) continue; // Late response after deadline.
                    if (pending.Command != command) throw new RpcException($"RPC response command mismatch for {id}.");
                    var data = record.TryGetProperty("data", out var d) ? d.Clone() : (JsonElement?)null;
                    var error = record.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                    if (_pending.TryRemove(id, out var completed))
                        completed.Waiter.TrySetResult(new RpcResponse(id, command, successValue.GetBoolean(), data, error));
                }
                else if (!_events.Writer.TryWrite(new RpcEvent(Generation, type, record)))
                    throw new RpcException("RPC event queue exceeded its capacity; consumer is too slow.");
            }
        }
        catch (Exception error) { Fail(error); }
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
            Volatile.Write(ref _processExit, new RpcProcessExit(Generation, _processId, _process.ExitCode, DateTimeOffset.UtcNow));
            // Drain the final stdout records before failing requests that truly lack a response.
            await _stdoutTask;
            await _dispatchTask;
            await _stderrTask;
            Record("process_exited", exitCode: _process.ExitCode);
            Fail(new RpcException($"Pi process exited with code {_process.ExitCode}. stderr: {StderrTail}"));
        }
        catch (Exception error) { Fail(error); }
    }

    private void Fail(Exception error)
    {
        if (Interlocked.CompareExchange(ref _failure, error, null) is not null) return;
        Record("connection_failed", exitCode: _process.HasExited ? _process.ExitCode : null,
            errorCode: error is RpcException ? "protocol_or_process" : "io_error");
        foreach (var item in _pending)
            if (_pending.TryRemove(item.Key, out var pending)) pending.Waiter.TrySetException(new RpcException("Pi RPC request ended before a response.", error));
        _records.Writer.TryComplete(error);
        _events.Writer.TryComplete(error);
    }

    public Task StopAsync()
    {
        lock (_shutdownGate) return _stopTask ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        try { _process.StandardInput.Close(); } catch (IOException) { }
        var shutdown = _options.ShutdownTimeout ?? TimeSpan.FromSeconds(5);
        using var deadline = new CancellationTokenSource(shutdown);
        try { await _process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        Fail(new RpcException("Pi RPC connection closed."));
        await Task.WhenAll(_stdoutTask, _stderrTask, _dispatchTask, _exitTask);
    }

    public ValueTask DisposeAsync()
    {
        lock (_shutdownGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await StopAsync();
        _process.Dispose();
        _writer.Dispose();
    }
}
