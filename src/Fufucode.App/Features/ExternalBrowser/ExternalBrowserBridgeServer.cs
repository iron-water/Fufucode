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
using System.Threading.Channels;
using System.Threading.Tasks;
using Fufucode.App.Features.BrowserExperience;
using Fufucode.App.Infrastructure;
using Fufucode.App.Infrastructure.Browser;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Features.ExternalBrowser;

/// <summary>外部浏览器桥的运行状态；设置页只展示真实值。</summary>
public sealed record ExternalBrowserBridgeStatus(
    bool Running, int Port, bool Connected, DateTimeOffset? LastSeenUtc, string? LastError)
{
    public static ExternalBrowserBridgeStatus Stopped(string? error = null) =>
        new(false, 0, false, null, error);
}

public sealed record ExternalBrowserPageReference(string Title, string Url, string Content, string? Selector);

/// <summary>
/// 设备自带浏览器（Chrome/Edge）控制桥：本地回环 HTTP 服务 + 扩展长轮询。
/// 与手机远控服务完全独立，不依赖 service.remote-control 插件。
/// </summary>
public sealed class ExternalBrowserBridgeServer : ExternalBrowserBridge, IAsyncDisposable
{
    public const int DefaultPort = 54321;
    private static readonly TimeSpan ConnectedWindow = TimeSpan.FromSeconds(20);

    private readonly Func<BrowserSettings> _settingsAccessor;
    private readonly string _token;
    private readonly Channel<ExternalBrowserCommand> _outbox = Channel.CreateUnbounded<ExternalBrowserCommand>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _executeGate = new(1, 1);
    private readonly object _pinGate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private string _pin;
    private int _pinFailures;
    private DateTimeOffset _pinLockedUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSeenUtc = DateTimeOffset.MinValue;
    private string? _lastHost;
    private string? _lastUrl;
    private string? _lastError;

    public ExternalBrowserBridgeServer(Func<BrowserSettings> settingsAccessor, string token, string pin)
    {
        _settingsAccessor = settingsAccessor ?? throw new ArgumentNullException(nameof(settingsAccessor));
        _token = string.IsNullOrWhiteSpace(token) ? Guid.NewGuid().ToString("N") : token.Trim();
        _pin = NormalizePin(pin);
    }

    public event Action? StatusChanged;

    public string Token => _token;
    public string Pin { get { lock (_pinGate) return _pin; } }
    public int Port { get; private set; }
    public bool Running => _listener is not null;
    public override bool IsConnected => Running && DateTimeOffset.UtcNow - _lastSeenUtc < ConnectedWindow;
    public override string? LastPageHost => _lastHost;

    public ExternalBrowserBridgeStatus Status => new(
        Running, Port, IsConnected,
        _lastSeenUtc == DateTimeOffset.MinValue ? null : _lastSeenUtc,
        _lastError);

    public Func<ExternalBrowserPageReference, Task<string>>? PageReferenceHandler { get; set; }
    public Func<BrowserApprovalRequest, CancellationToken, Task<bool>>? ApprovalHandler { get; set; }

    /// <summary>启动或重启服务；端口被占用时退回系统分配端口，实际端口从 <see cref="Port"/> 读取。</summary>
    public async Task<bool> StartAsync(int preferredPort = DefaultPort, string? pin = null)
    {
        await StopAsync().ConfigureAwait(false);
        if (pin is not null) SetPin(pin);
        var cts = new CancellationTokenSource();
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, NormalizePort(preferredPort));
            listener.Start();
            _listener = listener;
            _cts = cts;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _lastError = null;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, cts.Token), cts.Token);
            StatusChanged?.Invoke();
            return true;
        }
        catch (SocketException)
        {
            // 首选端口被占用时退回系统分配；扩展从 /api/status 或设置页读取实际端口。
            try
            {
                var fallback = new TcpListener(IPAddress.Loopback, 0);
                fallback.Start();
                _listener = fallback;
                _cts = cts;
                Port = ((IPEndPoint)fallback.LocalEndpoint).Port;
                _lastError = null;
                _acceptLoop = Task.Run(() => AcceptLoopAsync(fallback, cts.Token), cts.Token);
                StatusChanged?.Invoke();
                return true;
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                cts.Dispose();
                _lastError = "无法启动外部浏览器桥：" + error.Message;
                _listener = null;
                Port = 0;
                StatusChanged?.Invoke();
                return false;
            }
        }
    }

    public Task StopAsync()
    {
        var cts = _cts;
        var listener = _listener;
        _cts = null;
        _listener = null;
        Port = 0;
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { listener?.Stop(); } catch (SocketException) { }
        foreach (var pending in _pending.Values) pending.TrySetResult(Error("外部浏览器桥已停止。"));
        _pending.Clear();
        StatusChanged?.Invoke();
        return Task.CompletedTask;
    }

    public override async Task<string> ExecuteAsync(string action, string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            return Error("设备浏览器扩展未连接：请安装扩展、填写端口/配对码并保持 Chrome/Edge 运行。");
        if (string.IsNullOrWhiteSpace(action)) return Error("缺少 action。");

        var settings = _settingsAccessor();
        var rules = new BrowserSiteRules(settings);
        var args = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
        var targetUrl = ReadString(args, "url");
        var host = BrowserSiteRules.HostOf(targetUrl) ?? _lastHost;
        var permission = rules.PermissionFor(targetUrl ?? _lastUrl);
        var needsApproval = action switch
        {
            ExternalBrowserActions.State or ExternalBrowserActions.Tabs => false,
            ExternalBrowserActions.Evaluate => true,
            _ => !string.Equals(permission, "allow", StringComparison.OrdinalIgnoreCase),
        };

        if (host is { Length: > 0 } && string.Equals(permission, "block", StringComparison.OrdinalIgnoreCase))
            return Error($"{host} 的站点权限为“禁止访问”，已拒绝 {action}。");

        if (needsApproval)
        {
            if (ApprovalHandler is null)
                return Error($"外部浏览器动作 {action} 需要批准，但当前会话没有可用的确认通道。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                var allowed = await ApprovalHandler(
                    new BrowserApprovalRequest(action, host ?? _lastHost ?? "设备浏览器", targetUrl ?? _lastUrl ?? string.Empty),
                    timeout.Token).ConfigureAwait(false);
                if (!allowed) return Error($"外部浏览器动作 {action} 被用户拒绝。");
            }
            catch (OperationCanceledException)
            {
                return Error($"外部浏览器动作 {action} 等待批准超时，已拒绝。");
            }
        }

        await _executeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await SendAsync(action, args, cancellationToken).ConfigureAwait(false);
            if (result.Contains("\"needsConfirmation\":true", StringComparison.Ordinal))
            {
                var text = ReadString(result, "text");
                if (ApprovalHandler is null)
                    return Error($"该操作看起来是敏感操作（{text}），当前没有确认通道，已拒绝。");
                var allowed = await ApprovalHandler(
                    new BrowserApprovalRequest("敏感操作：" + (text ?? action), host ?? _lastHost ?? "设备浏览器", _lastUrl ?? string.Empty),
                    cancellationToken).ConfigureAwait(false);
                if (!allowed) return Error("用户拒绝了敏感操作。");
                args = AddConfirmed(args);
                result = await SendAsync(action, args, cancellationToken).ConfigureAwait(false);
            }
            UpdateKnownPage(result);
            return result;
        }
        finally { _executeGate.Release(); }
    }

    private async Task<string> SendAsync(string action, string args, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            if (!_outbox.Writer.TryWrite(new ExternalBrowserCommand(id, action, args)))
                return Error("外部浏览器桥命令队列不可用。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try { return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                return Error($"外部浏览器动作 {action} 超时（扩展未在 45 秒内回传结果）。");
            }
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private void UpdateKnownPage(string result)
    {
        try
        {
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
            {
                _lastUrl = url.GetString();
                _lastHost = BrowserSiteRules.HostOf(_lastUrl) ?? _lastHost;
            }
            if (root.TryGetProperty("host", out var host) && host.ValueKind == JsonValueKind.String)
                _lastHost = host.GetString();
        }
        catch (JsonException) { }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                if (request is null) { await WriteJsonAsync(stream, 400, Error("请求格式无效。"), cancellationToken); return; }
                if (request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteEmptyAsync(stream, 204, cancellationToken).ConfigureAwait(false);
                    return;
                }

                _lastSeenUtc = DateTimeOffset.UtcNow;
                switch (request.Path)
                {
                    case "/api/status":
                        await WriteJsonAsync(stream, 200, JsonSerializer.Serialize(new
                        {
                            ok = true,
                            paired = IsAuthorized(request),
                            connected = IsConnected,
                            port = Port,
                            version = typeof(ExternalBrowserBridgeServer).Assembly.GetName().Version?.ToString(),
                        }), cancellationToken).ConfigureAwait(false);
                        return;
                    case "/api/pair":
                        await HandlePairAsync(stream, request, cancellationToken).ConfigureAwait(false);
                        return;
                    case "/api/next":
                        if (!IsAuthorized(request)) { await WriteJsonAsync(stream, 401, Error("未配对。"), cancellationToken); return; }
                        await HandleNextAsync(stream, cancellationToken).ConfigureAwait(false);
                        return;
                    case "/api/result":
                        if (!IsAuthorized(request)) { await WriteJsonAsync(stream, 401, Error("未配对。"), cancellationToken); return; }
                        await HandleResultAsync(stream, request, cancellationToken).ConfigureAwait(false);
                        return;
                    case "/api/extension/collect":
                    case "/api/collect":
                        if (!IsAuthorized(request)) { await WriteJsonAsync(stream, 401, Error("未配对。"), cancellationToken); return; }
                        await HandleCollectAsync(stream, request, cancellationToken).ConfigureAwait(false);
                        return;
                    default:
                        await WriteJsonAsync(stream, 404, Error("未找到端点。"), cancellationToken).ConfigureAwait(false);
                        return;
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // 客户端断开是长轮询的正常结束，不写日志、不重试。
            }
        }
    }

    private async Task HandlePairAsync(NetworkStream stream, ExternalHttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(stream, 405, Error("仅支持 POST。"), cancellationToken).ConfigureAwait(false);
            return;
        }
        bool lockedOut;
        lock (_pinGate) lockedOut = DateTimeOffset.UtcNow < _pinLockedUntil;
        if (lockedOut)
        {
            await WriteJsonAsync(stream, 429, Error("配对尝试过多，请稍后再试。"), cancellationToken).ConfigureAwait(false);
            return;
        }
        var pin = ReadString(request.Body, "pin") ?? string.Empty;
        if (!FixedEquals(NormalizePin(pin), Pin))
        {
            lock (_pinGate)
            {
                _pinFailures++;
                if (_pinFailures >= 5) { _pinLockedUntil = DateTimeOffset.UtcNow.AddMinutes(5); _pinFailures = 0; }
            }
            await WriteJsonAsync(stream, 401, Error("PIN 不正确。"), cancellationToken).ConfigureAwait(false);
            return;
        }
        lock (_pinGate) { _pinFailures = 0; }
        await WriteJsonAsync(stream, 200, JsonSerializer.Serialize(new { ok = true, token = _token }), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleNextAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            if (!await _outbox.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false) ||
                !_outbox.Reader.TryRead(out var command))
            {
                await WriteEmptyAsync(stream, 204, cancellationToken).ConfigureAwait(false);
                return;
            }
            await WriteJsonAsync(stream, 200,
                $"{{\"id\":{JsonSerializer.Serialize(command.Id)},\"action\":{JsonSerializer.Serialize(command.Action)},\"arguments\":{command.ArgumentsJson}}}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteEmptyAsync(stream, 204, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task HandleResultAsync(NetworkStream stream, ExternalHttpRequest request, CancellationToken cancellationToken)
    {
        var id = ReadString(request.Body, "id");
        if (string.IsNullOrWhiteSpace(id) || !_pending.TryGetValue(id, out var completion))
        {
            await WriteJsonAsync(stream, 404, Error("未知的命令 ID。"), cancellationToken).ConfigureAwait(false);
            return;
        }
        var ok = ReadBool(request.Body, "ok");
        var error = ReadString(request.Body, "error");
        var result = ReadRaw(request.Body, "result");
        var payload = ok
            ? (string.IsNullOrWhiteSpace(result) ? "{\"ok\":true}" : result!)
            : Error(error ?? "扩展执行失败。");
        completion.TrySetResult(payload);
        UpdateKnownPage(payload);
        await WriteJsonAsync(stream, 200, "{\"ok\":true}", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleCollectAsync(NetworkStream stream, ExternalHttpRequest request, CancellationToken cancellationToken)
    {
        var reference = new ExternalBrowserPageReference(
            ReadString(request.Body, "title") ?? "网页引用",
            ReadString(request.Body, "url") ?? string.Empty,
            ReadString(request.Body, "content") ?? string.Empty,
            ReadString(request.Body, "selector"));
        _lastUrl = reference.Url;
        _lastHost = BrowserSiteRules.HostOf(reference.Url) ?? _lastHost;
        var message = PageReferenceHandler is null
            ? "外部浏览器引用已接收，但当前没有可写入的聊天会话。"
            : await PageReferenceHandler(reference).ConfigureAwait(false);
        await WriteJsonAsync(stream, 200, JsonSerializer.Serialize(new { ok = true, message }), cancellationToken)
            .ConfigureAwait(false);
    }

    private bool IsAuthorized(ExternalHttpRequest request)
    {
        if (request.Headers.TryGetValue("authorization", out var authorization) &&
            authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return FixedEquals(authorization[7..].Trim(), _token);
        return request.Query.TryGetValue("token", out var query) && FixedEquals(query, _token);
    }

    private void SetPin(string pin)
    {
        var normalized = NormalizePin(pin);
        if (normalized.Length != 4) return;
        lock (_pinGate) { _pin = normalized; _pinFailures = 0; _pinLockedUntil = DateTimeOffset.MinValue; }
    }

    // 0 保留给自测（系统分配）；设置页的 0 已在 BrowserSettings.Normalize 收敛为默认端口。
    private static int NormalizePort(int port) => port == 0 || port is >= 1024 and <= 65535 ? port : DefaultPort;

    private static string NormalizePin(string? pin)
    {
        var digits = new string((pin ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits.Length == 4 ? digits : string.Empty;
    }

    private static bool FixedEquals(string left, string right)
    {
        if (left.Length != right.Length) return false;
        var diff = 0;
        for (var i = 0; i < left.Length; i++) diff |= left[i] ^ right[i];
        return diff == 0;
    }

    private static string AddConfirmed(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var values = doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText(), StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            values["confirmed"] = "true";
            return "{" + string.Join(",", values.Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + pair.Value)) + "}";
        }
        catch (JsonException) { return "{\"confirmed\":true}"; }
    }

    private static string? ReadString(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool ReadBool(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty(name, out var value) &&
                   value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                   value.GetBoolean();
        }
        catch (JsonException) { return false; }
    }

    private static string? ReadRaw(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty(name, out var value)
                ? value.GetRawText()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static string Error(string message) =>
        "{\"error\":" + JsonSerializer.Serialize(message) + "}";

    private static async Task<ExternalHttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var header = new MemoryStream();
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0) return null;
            header.Write(buffer, 0, read);
            if (header.Length > 128 * 1024) return null;
            var bytes = header.GetBuffer();
            var length = (int)header.Length;
            for (var i = Math.Max(0, length - read - 3); i <= length - 4; i++)
            {
                if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
                {
                    headerEnd = i + 4;
                    break;
                }
            }
        }

        var headerText = Encoding.UTF8.GetString(header.GetBuffer(), 0, headerEnd);
        var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
        if (lines.Length == 0) return null;
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;
        var method = requestLine[0];
        var target = requestLine[1];
        var queryIndex = target.IndexOf('?');
        var path = queryIndex >= 0 ? target[..queryIndex] : target;
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (queryIndex >= 0)
        {
            foreach (var pair in target[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=');
                if (equals <= 0) continue;
                query[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var contentLength = headers.TryGetValue("content-length", out var rawLength) &&
                            int.TryParse(rawLength, out var parsedLength) && parsedLength >= 0
            ? Math.Min(parsedLength, 4 * 1024 * 1024)
            : 0;
        var body = string.Empty;
        if (contentLength > 0)
        {
            var bodyBytes = new byte[contentLength];
            var alreadyRead = (int)header.Length - headerEnd;
            if (alreadyRead > 0) Array.Copy(header.GetBuffer(), headerEnd, bodyBytes, 0, Math.Min(alreadyRead, contentLength));
            var remaining = contentLength - Math.Min(alreadyRead, contentLength);
            var offset = contentLength - remaining;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(bodyBytes.AsMemory(offset, remaining), cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;
                offset += read;
                remaining -= read;
            }
            body = Encoding.UTF8.GetString(bodyBytes, 0, offset);
        }
        return new ExternalHttpRequest(method, path, query, headers, body);
    }

    private static async Task WriteJsonAsync(NetworkStream stream, int status, string json, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var reason = status switch { 200 => "OK", 204 => "No Content", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 405 => "Method Not Allowed", 429 => "Too Many Requests", _ => "Internal Server Error" };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Headers: Authorization, Content-Type\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteEmptyAsync(NetworkStream stream, int status, CancellationToken cancellationToken)
    {
        var reason = status == 204 ? "No Content" : "OK";
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Length: 0\r\nConnection: close\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Headers: Authorization, Content-Type\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _executeGate.Dispose();
    }

    private sealed record ExternalBrowserCommand(string Id, string Action, string ArgumentsJson);
    private sealed record ExternalHttpRequest(string Method, string Path,
        Dictionary<string, string> Query, Dictionary<string, string> Headers, string Body);
}
