using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// SEEP-1 测试夹具：真实临时目录/真实回环端口 + 脚本化子进程替身。
/// 替身回放 2026-10-16 从真实 <c>seep_mcp_server.py</c> 录制的 tools/list 清单，
/// 因此 23 工具映射断言仍绑定真实服务端输出，而不是手抄名单。
/// </summary>
internal static class SeepFixtures
{
    private static readonly ConcurrentDictionary<int, TcpListener> Listeners = new();

    /// <summary>录制清单的相对路径（相对测试程序集工作目录）。</summary>
    private static string ManifestPath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "seep-tools-list.json");

    /// <summary>读取录制的真实工具清单原文。</summary>
    public static JsonDocument ReadManifest() => JsonDocument.Parse(File.ReadAllText(ManifestPath));

    /// <summary>录制清单里的工具数量（真实服务端输出）。</summary>
    public static int RecordedToolCount()
    {
        using var doc = ReadManifest();
        return doc.RootElement.GetProperty("tools").GetArrayLength();
    }

    /// <summary>录制清单里的工具名。</summary>
    public static IReadOnlyList<string> RecordedToolNames()
    {
        using var doc = ReadManifest();
        return doc.RootElement.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString() ?? string.Empty)
            .ToArray();
    }

    /// <summary>录制清单里的完整 tools 数组（供替身按真实契约应答）。</summary>
    public static string RecordedToolsJson()
    {
        using var doc = ReadManifest();
        return doc.RootElement.GetProperty("tools").GetRawText();
    }

    /// <summary>录制清单里的 serverInfo。</summary>
    public static string RecordedServerInfoJson()
    {
        using var doc = ReadManifest();
        return doc.RootElement.GetProperty("serverInfo").GetRawText();
    }

    /// <summary>在临时目录里造一个“工作台布局”，让真实文件系统探测通过。</summary>
    public static string CreateFakeLab(string root, bool createRelay, bool createMcp = true)
    {
        var labRoot = Path.Combine(root, "fake-lab-" + Guid.NewGuid().ToString("N")[..8]);
        var mcpDirectory = Path.Combine(labRoot, "Tool", "mcp");
        Directory.CreateDirectory(mcpDirectory);
        Directory.CreateDirectory(Path.Combine(labRoot, "Tool", "scripts", "compliance-relay"));
        if (createMcp)
            File.WriteAllText(Path.Combine(mcpDirectory, "seep_mcp_server.py"), "# fake fixture\n", Encoding.UTF8);
        if (createRelay)
        {
            File.WriteAllText(
                Path.Combine(labRoot, "Tool", "scripts", "compliance-relay", "relay.py"),
                "# fake fixture\n", Encoding.UTF8);
        }
        return labRoot;
    }

    /// <summary>构造指向该工作台的 MCP 启动参数。</summary>
    public static SeepProcessSpec Spec(string labRoot) =>
        SeepReverseLabAssets.CreateMcpProcessSpec(labRoot);

    /// <summary>取一个当前空闲的回环端口。</summary>
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>真实占用一个回环端口，模拟“外部进程已在监听中继端口”。</summary>
    public static int StartLoopbackListener()
    {
        while (true)
        {
            var listener = new TcpListener(IPAddress.Loopback, FreePort());
            try
            {
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Listeners[port] = listener;
                return port;
            }
            catch (SocketException)
            {
                // 端口刚被抢占：换一个再试。
            }
        }
    }

    /// <summary>释放本用例占用的回环监听端口。</summary>
    public static void StopLoopbackListener(int port)
    {
        if (Listeners.TryRemove(port, out var listener)) listener.Stop();
    }

    /// <summary>端口当前是否可连接（真实 TCP 探测，不用进程存在冒充）。</summary>
    public static bool IsPortOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// 脚本化 seep MCP 子进程替身工厂。每 <see cref="Start"/> 一次创建一个共享统计的新进程对象：
/// 真实读请求行、真实按 MCP 协议回响应，从而验证桥的协议实现本身。
/// </summary>
internal sealed class SeepFakeProcess : ISeepProcessFactory
{
    private readonly string _toolsJson;
    private readonly string _serverInfoJson;
    private readonly List<FakeChild> _children = new();
    private int _startCount;

    private SeepFakeProcess(string toolsJson, string serverInfoJson)
    {
        _toolsJson = toolsJson;
        _serverInfoJson = serverInfoJson;
    }

    /// <summary>用真实录制的 tools/list 清单构造替身。</summary>
    public static SeepFakeProcess FromRecordedManifest() =>
        new(SeepFixtures.RecordedToolsJson(), SeepFixtures.RecordedServerInfoJson());

    public int StartCount => Volatile.Read(ref _startCount);
    public bool Started => StartCount > 0;
    public bool SawInitialize => _children.Any(c => c.SawInitialize);
    public bool SawInitializedNotification => _children.Any(c => c.SawInitializedNotification);
    public bool SawToolsList => _children.Any(c => c.SawToolsList);
    public int KillCount => _children.Sum(c => c.KillCount);
    public string? LastToolCallName => _children.Where(c => c.LastToolCallName is not null)
        .Select(c => c.LastToolCallName).LastOrDefault();
    public string? LastToolArguments => _children.Where(c => c.LastToolArguments is not null)
        .Select(c => c.LastToolArguments).LastOrDefault();

    public ISeepProcess Start(SeepProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var child = new FakeChild(_toolsJson, _serverInfoJson);
        _children.Add(child);
        Interlocked.Increment(ref _startCount);
        return child;
    }

    /// <summary>单个子进程实例：内存双向管道 + 最小 MCP 服务端语义。</summary>
    private sealed class FakeChild : ISeepProcess
    {
        private readonly BlockingCollection<string> _requests = new();
        private readonly BlockingCollection<string> _responses = new();
        private readonly Task _server;
        private int _killCount;

        public FakeChild(string toolsJson, string serverInfoJson)
        {
            StandardInput = new LineTextWriter(OnRequestLine);
            StandardOutput = new QueueTextReader(_responses);
            StandardError = new QueueTextReader(new BlockingCollection<string>());
            _server = Task.Run(() => ServeAsync(toolsJson, serverInfoJson));
        }

        public bool SawInitialize { get; private set; }
        public bool SawInitializedNotification { get; private set; }
        public bool SawToolsList { get; private set; }
        public string? LastToolCallName { get; private set; }
        public string? LastToolArguments { get; private set; }
        public int KillCount => Volatile.Read(ref _killCount);
        public bool HasExited { get; private set; }
        public int? ExitCode => HasExited ? 0 : null;
        public int? ProcessId => null;
        public TextWriter StandardInput { get; }
        public TextReader StandardOutput { get; }
        public TextReader StandardError { get; }

        private void OnRequestLine(string line)
        {
            if (HasExited) return;
            try { _requests.Add(line); }
            catch (InvalidOperationException) { }
        }

        private async Task ServeAsync(string toolsJson, string serverInfoJson)
        {
            try
            {
                foreach (var line in _requests.GetConsumingEnumerable())
                {
                    if (!_responses.IsAddingCompleted) Handle(line, toolsJson, serverInfoJson);
                }
            }
            catch (Exception)
            {
                // 替身收尾：请求集合被终止即退出服务循环。
            }
            await Task.CompletedTask;
        }

        private void Handle(string line, string toolsJson, string serverInfoJson)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { return; }

            using (document)
            {
                var root = document.RootElement;
                var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
                switch (method)
                {
                    case "initialize":
                        SawInitialize = true;
                        Respond(root, "{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},"
                            + "\"serverInfo\":" + serverInfoJson + "}");
                        return;
                    case "notifications/initialized":
                        SawInitializedNotification = true;
                        return;
                    case "tools/list":
                        SawToolsList = true;
                        Respond(root, "{\"tools\":" + toolsJson + "}");
                        return;
                    case "tools/call":
                        HandleToolCall(root);
                        return;
                    default:
                        return;
                }
            }
        }

        private void HandleToolCall(JsonElement root)
        {
            if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
            {
                RespondError(root, -32602, "invalid params");
                return;
            }

            var name = parameters.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            LastToolCallName = name;
            var arguments = parameters.TryGetProperty("arguments", out var a) ? a : default;
            LastToolArguments = arguments.ValueKind == JsonValueKind.Object ? arguments.GetRawText() : null;

            // 复现“服务端返回 isError 的工具失败”，用于验证桥不把它当成功。
            var binaryPath = arguments.ValueKind == JsonValueKind.Object &&
                             arguments.TryGetProperty("binary_path", out var p)
                ? p.GetString() : null;
            if (string.Equals(binaryPath, "fail", StringComparison.Ordinal))
            {
                Respond(root, "{\"isError\":true,\"content\":[{\"type\":\"text\","
                    + "\"text\":\"FIXTURE-ERROR: 目标文件不存在\"}]}");
                return;
            }

            var text = string.Equals(name, "seep_status", StringComparison.Ordinal)
                ? "RTK-FAKE-STATUS 全部就绪"
                : $"RTK-FAKE-OK {name}";
            Respond(root, "{\"content\":[{\"type\":\"text\",\"text\":"
                + JsonSerializer.Serialize(text) + "}]}");
        }

        private void Respond(JsonElement request, string resultJson)
        {
            var id = request.TryGetProperty("id", out var i) ? i.GetRawText() : "null";
            Write("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + resultJson + "}");
        }

        private void RespondError(JsonElement request, int code, string message)
        {
            var id = request.TryGetProperty("id", out var i) ? i.GetRawText() : "null";
            Write("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":" + code
                + ",\"message\":\"" + message + "\"}}");
        }

        private void Write(string payload)
        {
            try { if (!_responses.IsAddingCompleted) _responses.Add(payload); }
            catch (InvalidOperationException) { }
        }

        public void Kill()
        {
            if (HasExited) return;
            HasExited = true;
            Interlocked.Increment(ref _killCount);
            try { _requests.CompleteAdding(); } catch (InvalidOperationException) { }
            try { _responses.CompleteAdding(); } catch (InvalidOperationException) { }
        }

        public void Dispose()
        {
            Kill();
            try { _server.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        }
    }

    /// <summary>把每次 WriteLine 转成回调的 TextWriter。</summary>
    private sealed class LineTextWriter(Action<string> onLine) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value) => onLine(value ?? string.Empty);
        public override Task WriteLineAsync(string? value)
        {
            onLine(value ?? string.Empty);
            return Task.CompletedTask;
        }
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onLine(buffer.ToString());
            return Task.CompletedTask;
        }
        public override void Flush() { }
        public override Task FlushAsync() => Task.CompletedTask;
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>从阻塞队列按行读取的 TextReader；队列终止时返回 null（等价管道 EOF）。</summary>
    private sealed class QueueTextReader(BlockingCollection<string> lines) : TextReader
    {
        public override string? ReadLine()
        {
            try { return lines.Take(); }
            catch (InvalidOperationException) { return null; }
        }

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (lines.TryTake(out var line, 50)) return line;
                    if (lines.IsAddingCompleted && lines.Count == 0) return null;
                }
                catch (InvalidOperationException) { return null; }
                await Task.Yield();
            }
        }
    }
}

/// <summary>只记录启动/终止、从不监听的子进程替身（用于“未监听即失败”的反例）。</summary>
internal sealed class RecordingProcessFactory : ISeepProcessFactory
{
    private readonly List<StubProcess> _children = new();
    private int _startCount;

    public int StartCount => Volatile.Read(ref _startCount);
    public int KillCount => _children.Sum(c => c.KillCount);

    public ISeepProcess Start(SeepProcessSpec spec)
    {
        var child = new StubProcess();
        _children.Add(child);
        Interlocked.Increment(ref _startCount);
        return child;
    }

    private sealed class StubProcess : ISeepProcess
    {
        private int _killCount;
        public StubProcess()
        {
            StandardInput = new StringWriter();
            StandardOutput = new StringReader(string.Empty);
            StandardError = new StringReader(string.Empty);
        }
        public bool HasExited { get; private set; }
        public int? ExitCode => HasExited ? 0 : null;
        public int? ProcessId => null;
        public TextWriter StandardInput { get; }
        public TextReader StandardOutput { get; }
        public TextReader StandardError { get; }
        public int KillCount => Volatile.Read(ref _killCount);

        public void Kill()
        {
            if (HasExited) return;
            HasExited = true;
            Interlocked.Increment(ref _killCount);
        }
        public void Dispose() => Kill();
    }
}

/// <summary>启动即真实监听目标端口的子进程替身，用于验证“真的可连接才判 Running”。</summary>
internal sealed class LoopbackRelayProcessFactory(int? port = null) : ISeepProcessFactory
{
    private readonly List<ListenerProcess> _children = new();
    private int _startCount;

    public int StartCount => Volatile.Read(ref _startCount);
    public int KillCount => _children.Sum(c => c.KillCount);

    public ISeepProcess Start(SeepProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var child = new ListenerProcess(port ?? PortFromArguments(spec));
        _children.Add(child);
        Interlocked.Increment(ref _startCount);
        return child;
    }

    /// <summary>停止全部由本工厂启动的监听，避免测试结束后残留。</summary>
    public void Stop()
    {
        foreach (var child in _children) child.Kill();
    }

    private static int PortFromArguments(SeepProcessSpec spec)
    {
        var index = spec.Arguments.ToList().IndexOf("--port");
        return index >= 0 && index + 1 < spec.Arguments.Count &&
               int.TryParse(spec.Arguments[index + 1], out var value)
            ? value
            : SeepReverseLabAssets.RelayPort;
    }

    private sealed class ListenerProcess : ISeepProcess
    {
        private readonly TcpListener? _listener;
        private int _killCount;

        public ListenerProcess(int port)
        {
            StandardInput = new StringWriter();
            StandardOutput = new StringReader(string.Empty);
            StandardError = new StringReader(string.Empty);
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
            }
            catch (SocketException)
            {
                _listener = null;
            }
        }

        public bool HasExited { get; private set; }
        public int? ExitCode => HasExited ? 0 : null;
        public int? ProcessId => null;
        public TextWriter StandardInput { get; }
        public TextReader StandardOutput { get; }
        public TextReader StandardError { get; }
        public int KillCount => Volatile.Read(ref _killCount);

        public void Kill()
        {
            if (HasExited) return;
            HasExited = true;
            Interlocked.Increment(ref _killCount);
            try { _listener?.Stop(); } catch (SocketException) { }
        }

        public void Dispose() => Kill();
    }
}
