using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Fufucode.App.Abstractions;
using Fufucode.Fufu.Pet;

namespace Fufucode.Fufu.Integration;

/// <summary>
/// FUFU 的命名管道服务端：监听 \\.\pipe\fufu.fufucode.v1，接受 Fufucode Bridge 插件连接。
/// 协议 = <see cref="FufuBridgeProtocol"/>（JSON Lines）。身份校验 = 客户端进程镜像名
/// 必须为 Fufucode.exe（同机管道 + PID 检查，无凭据场景下的真实校验）。
/// </summary>
public sealed class FufuPipeServer : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafeHandle pipeHandle, out uint clientProcessId);

    /// <summary>会话目录/事件/审批到达（线程不定；UI 订阅自行封送）。</summary>
    public event Action<IReadOnlyList<Fufucode.App.Abstractions.FufuBridgeProtocol.SessionEntry>>? SnapshotReceived;
    public event Action<string, string, string>? AgentEventReceived; // sessionId, type, summary
    public event Action<string, string, string, string>? ApprovalReceived; // requestId, sessionId, tool, message
    public event Action<string>? ApprovalClosed; // requestId
    public event Action<bool>? ConnectionChanged;

    /// <summary>连接状态与当前会话目录（UI 轮询/推送两用）。</summary>
    public bool IsConnected { get; private set; }

    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly List<Fufucode.App.Abstractions.FufuBridgeProtocol.SessionEntry> _sessions = new();
    private StreamWriter? _out;
    private NamedPipeServerStream? _pipeStream;
    private NamedPipeServerStream? _current;
    private NamedPipeServerStream? _currentOut;
    private string? _currentSessionId;
    private bool _disposed;

    public IReadOnlyList<Fufucode.App.Abstractions.FufuBridgeProtocol.SessionEntry> Sessions
    {
        get { lock (_sessions) return _sessions.ToArray(); }
    }

    public string? CurrentSessionId { get { lock (_sessions) return _currentSessionId; } }

    public FufuPipeServer()
    {
        _ = Task.Run(ListenLoopAsync);
    }

    private async Task ListenLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            // 双管道：同一管道句柄上挂起读会阻塞写（本环境实测），读写各走一条单向管道。
            NamedPipeServerStream? pipe = null, pipeOut = null;
            try
            {
                pipe = new NamedPipeServerStream(FufuBridgeProtocol.PipeName,
                    PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None, 8192, 0);
                pipeOut = new NamedPipeServerStream(FufuBridgeProtocol.PipeNameBack,
                    PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.None, 0, 8192);
                lock (_sessions) { _current = pipe; _currentOut = pipeOut; }
                await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                await pipeOut.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);

                // 身份校验：客户端进程必须是 Fufucode 本体。
                if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
                {
                    try
                    {
                        var name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
                        FufuLog.Write($"bridge client pid={pid} process={name}");
                        // 发布产物进程名为 Fufucode.App（dev/main 构建可能叫 Fufucode）。
                        if (!name.StartsWith("Fufucode", StringComparison.OrdinalIgnoreCase))
                        {
                            FufuLog.Write($"bridge client rejected: process {name} is not Fufucode");
                            pipe.Disconnect();
                            continue;
                        }
                    }
                    catch (Exception ex) { FufuLog.Write($"bridge pid check failed: {ex.Message}"); pipe.Disconnect(); continue; }
                }

                lock (_sessions) { _pipeStream = pipeOut; _out = new StreamWriter(pipeOut) { AutoFlush = true }; }
                IsConnected = true;
                ConnectionChanged?.Invoke(true);
                FufuLog.Write("bridge connected");
                try { await ReadLoopAsync(pipe).ConfigureAwait(false); }
                catch { }
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(500, _shutdown.Token).ContinueWith(_ => { }); }

            IsConnected = false;
            // 对端已死时 Dispose 冲刷缓冲会抛 IOException，逐个容忍，监听循环不能死。
            lock (_sessions)
            {
                try { _out?.Dispose(); } catch { }
                _out = null; _pipeStream = null;
                try { _current?.Dispose(); } catch { }
                try { _currentOut?.Dispose(); } catch { }
                _current = null; _currentOut = null;
            }
            ConnectionChanged?.Invoke(false);
            FufuLog.Write("bridge disconnected");
        }
    }

    private async Task ReadLoopAsync(NamedPipeServerStream pipe)
    {
        using var reader = new StreamReader(pipe);
        while (!_shutdown.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);
            if (line is null) { FufuLog.Write("bridge read: pipe closed"); return; }
            var message = FufuBridgeProtocol.ReadLine(line);
            if (message is null) continue;
            switch (FufuBridgeProtocol.Kind(message))
            {
                case FufuBridgeProtocol.KindHello:
                {
                    var version = message["pluginVersion"]?.GetValue<int>() ?? 0;
                    var accepted = version == FufuBridgeProtocol.ProtocolVersion;
                    await SendAsync(new JsonObject
                    {
                        ["kind"] = FufuBridgeProtocol.KindHelloAck,
                        ["protocolVersion"] = FufuBridgeProtocol.ProtocolVersion,
                        ["accepted"] = accepted,
                        ["error"] = accepted ? null : $"协议版本不兼容：期望 {FufuBridgeProtocol.ProtocolVersion}，收到 {version}",
                    }).ConfigureAwait(false);
                    FufuLog.Write($"bridge hello: v{version} pid={message["pid"]} fufucode={message["fufucodeVersion"]}");
                    if (!accepted) return;
                    break;
                }
                case FufuBridgeProtocol.KindSnapshot:
                {
                    var entries = new List<Fufucode.App.Abstractions.FufuBridgeProtocol.SessionEntry>();
                    if (message["sessions"] is JsonArray array)
                    {
                        foreach (var node in array.OfType<JsonObject>())
                        {
                            entries.Add(new(
                                node["id"]?.GetValue<string>() ?? "",
                                node["title"]?.GetValue<string>() ?? "",
                                node["project"]?.GetValue<string>() ?? "",
                                node["activity"]?.GetValue<string>() ?? "Idle",
                                node["connection"]?.GetValue<string>() ?? "Stopped",
                                node["model"]?.GetValue<string>(),
                                node["canSend"]?.GetValue<bool>() == true,
                                node["canStop"]?.GetValue<bool>() == true,
                                node["queued"]?.GetValue<int>() ?? 0,
                                node["isCurrent"]?.GetValue<bool>() == true));
                        }
                    }
                    lock (_sessions)
                    {
                        _sessions.Clear();
                        _sessions.AddRange(entries);
                        _currentSessionId = entries.FirstOrDefault(e => e.IsCurrent)?.Id
                            ?? _currentSessionId;
                    }
                    SnapshotReceived?.Invoke(entries);
                    break;
                }
                case FufuBridgeProtocol.KindEvent:
                {
                    var sessionId = message["sessionId"]?.GetValue<string>() ?? "";
                    var type = message["type"]?.GetValue<string>() ?? "";
                    var summary = message["summary"]?.GetValue<string>() ?? "";
                    AgentEventReceived?.Invoke(sessionId, type, summary);
                    break;
                }
                case FufuBridgeProtocol.KindApproval:
                    ApprovalReceived?.Invoke(
                        message["requestId"]?.GetValue<string>() ?? "",
                        message["sessionId"]?.GetValue<string>() ?? "",
                        message["tool"]?.GetValue<string>() ?? "",
                        message["message"]?.GetValue<string>() ?? "");
                    break;
                case FufuBridgeProtocol.KindApprovalClosed:
                    ApprovalClosed?.Invoke(message["requestId"]?.GetValue<string>() ?? "");
                    break;
                case FufuBridgeProtocol.KindPing:
                    await SendAsync(new JsonObject { ["kind"] = FufuBridgeProtocol.KindPong }).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>受控提交：FUFU 授权把 text 投递到 sessionId 对应会话。</summary>
    public Task SubmitAsync(string sessionId, string text) =>
        SendAsync(new JsonObject
        {
            ["kind"] = FufuBridgeProtocol.KindSubmit, ["sessionId"] = sessionId, ["text"] = text,
        });

    public Task SelectAsync(string sessionId) =>
        SendAsync(new JsonObject
        {
            ["kind"] = FufuBridgeProtocol.KindSelect, ["sessionId"] = sessionId,
        });

    public Task ApproveAsync(string requestId, bool approved) =>
        SendAsync(new JsonObject
        {
            ["kind"] = FufuBridgeProtocol.KindApprove, ["requestId"] = requestId, ["approved"] = approved,
        });

    private async Task SendAsync(JsonObject message)
    {
        NamedPipeServerStream? stream;
        lock (_sessions) stream = _pipeStream;
        if (stream is null) throw new InvalidOperationException("Fufucode 未连接");
        var bytes = System.Text.Encoding.UTF8.GetBytes(FufuBridgeProtocol.Write(message) + "\n");
        await _writer.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        // 同步管道上 WriteAsync 会卸载到线程池执行，不阻塞调用方（UI 线程）。
        try { await stream.WriteAsync(bytes, _shutdown.Token).ConfigureAwait(false); await stream.FlushAsync(_shutdown.Token).ConfigureAwait(false); }
        finally { _writer.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        lock (_sessions) { _out?.Dispose(); _out = null; _current?.Dispose(); _current = null; }
        _shutdown.Dispose();
    }
}
