using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace Fufucode.Core.RemoteControl;

/// <summary>
/// 轻量内置局域网服务：只依赖 .NET 内置网络库，自包含 HTTP + WebSocket，
/// 提供 <see cref="Start"/>／<see cref="Stop"/>／<see cref="Status"/> 生命周期、
/// 4 位 PIN 配对认证、会话控制 API 与 Web UI 静态资源。
/// </summary>
public sealed class RemoteControlServer : IAsyncDisposable
{
    private readonly RemoteControlOptions _options;
    private readonly IRemoteSessionBridge _bridge;
    private readonly IRemoteWebAssets? _assets;
    private readonly RemoteControlAuth _auth;
    private readonly object _gate = new();
    private readonly object _lifecycle = new();
    private readonly List<ClientConnection> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private string? _address;
    private int _port;
    private string? _pairingUrl;
    private DateTimeOffset? _startedAt;
    private int _connectionCount;
    private bool _disposed;
    private Func<RemoteSessionCatalog>? _sessionCatalog;
    private Func<string, CancellationToken, Task<RemoteCommandResult>>? _switchSession;

    public RemoteControlServer(
        IRemoteSessionBridge bridge,
        RemoteControlOptions? options = null,
        IRemoteWebAssets? assets = null,
        TimeProvider? clock = null,
        Func<RemoteSessionCatalog>? sessionCatalog = null,
        Func<string, CancellationToken, Task<RemoteCommandResult>>? switchSession = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _options = options ?? new RemoteControlOptions();
        _assets = assets;
        _auth = new RemoteControlAuth(_options, clock);
        _sessionCatalog = sessionCatalog;
        _switchSession = switchSession;
        _bridge.Updated += OnBridgeUpdated;
        _bridge.Approvals.Requested += OnApprovalRequested;
        _bridge.Approvals.Resolved += OnApprovalResolved;
    }

    /// <summary>更新会话目录提供器；服务运行中也可安全替换。</summary>
    public void ConfigureSessionDirectory(
        Func<RemoteSessionCatalog>? catalog,
        Func<string, CancellationToken, Task<RemoteCommandResult>>? switchSession)
    {
        lock (_gate)
        {
            _sessionCatalog = catalog;
            _switchSession = switchSession;
        }
    }

    /// <summary>当前服务状态（IP、端口、连接数、配对 URL 与 PIN）。</summary>
    public RemoteControlStatus Status
    {
        get
        {
            lock (_gate)
            {
                if (_listener is null)
                    return RemoteControlStatus.Stopped();
                return new RemoteControlStatus(RemoteControlState.Running, _address, _port, _connectionCount,
                    _pairingUrl, _auth.Pin, _auth.TokenLifetime, _startedAt);
            }
        }
    }

    /// <summary>服务是否运行中。</summary>
    public bool IsRunning => _listener is not null;

    /// <summary>配对 URL（含 PIN 提示页地址）；未启动时为 null。</summary>
    public string? PairingUrl => Status.PairingUrl;

    /// <summary>启动服务；<paramref name="port"/> 为 0 时由系统自动分配端口。</summary>
    public RemoteControlStatus Start(int port = 0)
    {
        lock (_lifecycle)
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null)
                throw new InvalidOperationException("远程控制服务已在运行。");
            if (port is < 0 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(port), "端口必须在 0-65535 之间。");

            var listener = new TcpListener(_options.BindAddress, port);
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            _listener = listener;
            _port = endpoint.Port;
            _address = RemoteControlNetwork.ResolveAdvertisedHost(_options, endpoint);
            var pin = _auth.Regenerate();
            _pairingUrl = $"http://{HostFor(_address)}:{_port}/";
            _startedAt = DateTimeOffset.UtcNow;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, token));
            _ = pin;
            return new RemoteControlStatus(RemoteControlState.Running, _address, _port, _connectionCount,
                _pairingUrl, pin, _auth.TokenLifetime, _startedAt);
        }
    }

    private static string HostFor(string host) =>
        host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;

    /// <summary>停止服务、关闭全部连接并撤销令牌。</summary>
    public void Stop()
    {
        lock (_lifecycle) StopCore();
    }

    private void StopCore()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        Task? loop;
        ClientConnection[] clients;
        lock (_gate)
        {
            listener = _listener;
            cts = _cts;
            loop = _acceptLoop;
            clients = _clients.ToArray();
            _listener = null;
            _cts = null;
            _acceptLoop = null;
            _clients.Clear();
            _connectionCount = 0;
            _address = null;
            _port = 0;
            _pairingUrl = null;
            _startedAt = null;
        }

        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { listener?.Stop(); } catch (SocketException) { }
        foreach (var client in clients) client.RequestClose();
        try { loop?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        cts?.Dispose();
        _auth.RevokeAll();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            var connection = new ClientConnection(
                ClientKey(tcp),
                tcp,
                HandleRequestAsync,
                Closed);
            lock (_gate)
            {
                if (!ReferenceEquals(_listener, listener) || _clients.Count >= 128)
                {
                    connection.RequestClose();
                    continue;
                }

                _clients.Add(connection);
                _connectionCount = _clients.Count;
            }

            _ = connection.RunAsync(cancellationToken);
        }
    }

    private void Closed(ClientConnection connection)
    {
        lock (_gate)
        {
            _clients.Remove(connection);
            _connectionCount = _clients.Count;
        }
    }

    /// <summary>
    /// 认证与锁定按客户端地址计数。HTTP 请求是「请求-响应即关闭」的短连接，
    /// 若按连接实例计数，每次请求都会得到新身份，连续错误 PIN 永远无法触发锁定。
    /// </summary>
    private static string ClientKey(TcpClient tcp)
    {
        try
        {
            return (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        }
        catch (SocketException)
        {
            return "unknown";
        }
        catch (ObjectDisposedException)
        {
            return "unknown";
        }
    }

    private void OnBridgeUpdated(object? sender, RemoteUpdate update) => Broadcast(update);

    private void OnApprovalRequested(object? sender, RemoteApprovalRequest request) =>
        Broadcast(new RemoteApprovalAddedUpdate(request));

    private void OnApprovalResolved(object? sender, RemoteApprovalResolution resolution) =>
        Broadcast(new RemoteApprovalResolvedUpdate(resolution));

    /// <summary>向全部已认证连接推送一个增量事件。</summary>
    public void Broadcast(RemoteUpdate update)
    {
        ClientConnection[] targets;
        lock (_gate) targets = _clients.ToArray();
        var payload = RemoteWire.SerializeUpdate(update);
        foreach (var client in targets) client.Enqueue(payload);
    }

    /// <summary>连接数与待审批数（供宿主诊断显示）。</summary>
    public int ConnectionCount => Status.ConnectionCount;

    private async Task WriteResponseAsync(Stream stream, RemoteHttpRequest request, int status, string? json,
        CancellationToken cancellationToken)
    {
        if (request.Method == "HEAD")
        {
            var body = json is null ? Array.Empty<byte>() : System.Text.Encoding.UTF8.GetBytes(json);
            await RemoteHttp.WriteAsync(stream, status, "application/json; charset=utf-8", body, cancellationToken, headOnly: true)
                .ConfigureAwait(false);
            return;
        }

        await RemoteHttp.WriteJsonAsync(stream, status, json ?? "{}", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleRequestAsync(ClientConnection connection, Stream stream, RemoteHttpRequest request,
        CancellationToken cancellationToken)
    {
        var path = request.Path;
        var host = request.Header("host");
        if (!Uri.TryCreate("http://" + host + "/", UriKind.Absolute, out var target) ||
            target.UserInfo.Length != 0 || target.AbsolutePath != "/" ||
            target.Query.Length != 0 || target.Fragment.Length != 0 ||
            target.Port != _port ||
            !(string.Equals(target.Host.Trim('[', ']'), connection.LocalAddress, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(target.Host, _address, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(target.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(target.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase)))
        {
            await WriteResponseAsync(stream, request, 403, null, cancellationToken).ConfigureAwait(false);
            return;
        }
        var origin = request.Header("origin");
        if (origin is not null &&
            (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) ||
             !string.Equals(originUri.Authority, request.Header("host"), StringComparison.OrdinalIgnoreCase) ||
             originUri.Scheme != "http"))
        {
            // 保留已安装扩展的配对流程；第三方 HTTP 网页不能跨站尝试 PIN。
            var extension = Uri.TryCreate(origin, UriKind.Absolute, out var extensionUri) &&
                extensionUri.Scheme is "chrome-extension" or "moz-extension";
            if (!extension)
            {
                await WriteResponseAsync(stream, request, 403, null, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (request.Method == "OPTIONS")
        {
            var optionsHeaders = "HTTP/1.1 204 No Content\r\n" +
                                 "Access-Control-Allow-Origin: *\r\n" +
                                 "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                                 "Access-Control-Allow-Headers: Content-Type, Authorization, X-Requested-With\r\n" +
                                 "Access-Control-Max-Age: 86400\r\n" +
                                 "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(optionsHeaders), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (path == "/api/service-info")
        {
            await WriteResponseAsync(stream, request, 200,
                RemoteWire.Serialize(new { service = "remote-control", version = "1", capabilities = new[] { "session-ui", "websocket", "approvals", "session-directory" } }),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (path == "/api/pair")
        {
            await HandlePairAsync(connection, stream, request, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Web UI 外壳（页面/CSS/JS）必须先于认证可访问：手机在配对前就要能打开页面输入 PIN。
        // 外壳本身不含任何会话数据；全部会话数据与指令端点仍走下面的令牌校验。
        if (!path.StartsWith("/api/", StringComparison.Ordinal) &&
            await TryWriteAssetAsync(stream, request, path, cancellationToken).ConfigureAwait(false))
            return;

        if (!IsAuthorized(request))
        {
            await WriteResponseAsync(stream, request, 401,
                RemoteWire.Serialize(new RemoteCommandResult(false, "未授权：请先使用 4 位配对码验证。")),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (path)
        {
            case "/api/status":
                await WriteResponseAsync(stream, request, 200, RemoteWire.Serialize(Status), cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "/api/snapshot":
                await WriteResponseAsync(stream, request, 200,
                    RemoteWire.Serialize(_bridge.Snapshot()), cancellationToken).ConfigureAwait(false);
                return;
            case "/api/sessions":
                var catalogProvider = _sessionCatalog;
                if (catalogProvider is null)
                {
                    await WriteResponseAsync(stream, request, 503,
                        RemoteWire.Serialize(new RemoteCommandResult(false, "桌面未提供会话目录。")),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
                try
                {
                    await WriteResponseAsync(stream, request, 200,
                        RemoteWire.Serialize(catalogProvider()), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    await WriteResponseAsync(stream, request, 500,
                        RemoteWire.Serialize(new RemoteCommandResult(false, "读取会话目录失败：" + error.Message)),
                        cancellationToken).ConfigureAwait(false);
                }
                return;
            case "/api/sessions/switch":
                if (request.Method != "POST")
                {
                    await WriteResponseAsync(stream, request, 405, null, cancellationToken).ConfigureAwait(false);
                    return;
                }
                var switchHandler = _switchSession;
                if (switchHandler is null)
                {
                    await WriteResponseAsync(stream, request, 503,
                        RemoteWire.Serialize(new RemoteCommandResult(false, "桌面未提供会话切换。")),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
                var sessionId = ReadField(request.Body, "sessionId");
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    await WriteResponseAsync(stream, request, 400,
                        RemoteWire.Serialize(new RemoteCommandResult(false, "会话 ID 为空。")),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
                var switched = await switchHandler(sessionId, cancellationToken).ConfigureAwait(false);
                await WriteResponseAsync(stream, request, switched.Accepted ? 200 : 409,
                    RemoteWire.Serialize(switched), cancellationToken).ConfigureAwait(false);
                return;
            case "/api/message":
                if (request.Method != "POST")
                {
                    await WriteResponseAsync(stream, request, 405, null, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var text = ReadField(request.Body, "text");
                if (string.IsNullOrWhiteSpace(text))
                {
                    await WriteResponseAsync(stream, request, 400,
                        RemoteWire.Serialize(new RemoteCommandResult(false, "消息文本为空。")), cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                var sent = await _bridge.SendAsync(text, cancellationToken).ConfigureAwait(false);
                await WriteResponseAsync(stream, request, 200, RemoteWire.Serialize(sent), cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "/api/stop":
                if (request.Method != "POST")
                {
                    await WriteResponseAsync(stream, request, 405, null, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var stopped = await _bridge.StopAsync(cancellationToken).ConfigureAwait(false);
                await WriteResponseAsync(stream, request, 200, RemoteWire.Serialize(stopped), cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "/api/approval":
                if (request.Method != "POST")
                {
                    await WriteResponseAsync(stream, request, 405, null, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await HandleApprovalAsync(stream, request, cancellationToken).ConfigureAwait(false);
                return;
                        case "/api/extension/collect":
                if (request.Method != "POST")
                {
                    await WriteResponseAsync(stream, request, 405, null, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var extTitle = ReadField(request.Body, "title") ?? "网页引用";
                var extUrl = ReadField(request.Body, "url") ?? "";
                var extContent = ReadField(request.Body, "content") ?? "";
                var extSelector = ReadField(request.Body, "selector");

                var sb = new StringBuilder();
                sb.AppendLine("【来自浏览器扩展引用】");
                sb.AppendLine($"标题：{extTitle}");
                if (!string.IsNullOrWhiteSpace(extUrl)) sb.AppendLine($"地址：{extUrl}");
                if (!string.IsNullOrWhiteSpace(extSelector)) sb.AppendLine($"选择器：{extSelector}");
                sb.AppendLine();
                sb.Append(extContent);

                var collectRes = await _bridge.SendAsync(sb.ToString(), cancellationToken).ConfigureAwait(false);
                await WriteResponseAsync(stream, request, 200, RemoteWire.Serialize(collectRes), cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "/api/events":
                if (!request.IsWebSocketUpgrade)
                {
                    await WriteResponseAsync(stream, request, 400, null, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await connection.AttachWebSocketAsync(stream, request, _bridge.Snapshot,
                    () => IsAuthorized(request), cancellationToken)
                    .ConfigureAwait(false);
                return;
        }

        await WriteResponseAsync(stream, request, 404, "null", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandlePairAsync(ClientConnection connection, Stream stream, RemoteHttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Method != "POST")
        {
            await WriteResponseAsync(stream, request, 405, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var pin = ReadField(request.Body, "pin");
        var result = _auth.Pair(pin, connection.Key);
        await WriteResponseAsync(stream, request, result.Success ? 200 : result.LockedOut ? 429 : 401,
            RemoteWire.Serialize(result), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleApprovalAsync(Stream stream, RemoteHttpRequest request, CancellationToken cancellationToken)
    {
        var approvalId = ReadField(request.Body, "approvalId");
        var decisionText = ReadField(request.Body, "decision");
        var decision = decisionText?.ToLowerInvariant() switch
        {
            "approve" or "allow" or "approved" => RemoteApprovalDecision.Approve,
            "deny" or "reject" or "denied" => RemoteApprovalDecision.Deny,
            _ => (RemoteApprovalDecision?)null,
        };
        if (string.IsNullOrWhiteSpace(approvalId) || decision is null)
        {
            await RemoteHttp.WriteJsonAsync(stream, 400,
                RemoteWire.Serialize(new RemoteCommandResult(false, "审批参数不完整。")), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var resolution = await _bridge.Approvals.ResolveAsync(approvalId, decision.Value, cancellationToken)
            .ConfigureAwait(false);
        await RemoteHttp.WriteJsonAsync(stream, resolution.Accepted ? 200 : 409,
            RemoteWire.Serialize(resolution), cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryWriteAssetAsync(Stream stream, RemoteHttpRequest request, string path,
        CancellationToken cancellationToken)
    {
        if (_assets is null || request.Method is not ("GET" or "HEAD")) return false;
        if (!_assets.TryGet(path, out var asset)) return false;
        await RemoteHttp.WriteAsync(stream, 200, asset.ContentType, asset.Content, cancellationToken, request.Method == "HEAD")
            .ConfigureAwait(false);
        return true;
    }

    private bool IsAuthorized(RemoteHttpRequest request)
    {
        var token = request.QueryValue("token");
        if (token is null && request.Headers.TryGetValue("authorization", out var header) &&
            header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = header["Bearer ".Length..].Trim();
        if (token is null && request.Body.Length > 0)
        {
            try
            {
                using var document = JsonDocument.Parse(request.Body);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("token", out var value) &&
                    value.ValueKind == JsonValueKind.String)
                    token = value.GetString();
            }
            catch (JsonException)
            {
                // 非法 JSON 主体不提供凭据，按未授权处理。
            }
        }

        return _auth.ValidateToken(token);
    }

    private static string? ReadField(byte[] body, string field)
    {
        if (body.Length == 0) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals(field, StringComparison.OrdinalIgnoreCase)) continue;
                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => null,
                };
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>启动指定端口的服务并返回状态。</summary>
    public async Task<RemoteControlStatus> StartAsync(int port = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => Start(port), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _bridge.Updated -= OnBridgeUpdated;
        _bridge.Approvals.Requested -= OnApprovalRequested;
        _bridge.Approvals.Resolved -= OnApprovalResolved;
        Stop();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>一条客户端 TCP 连接：承载请求/响应与其上的 WebSocket 推送。</summary>
    private sealed class ClientConnection
    {
        private readonly TcpClient _tcp;
        private readonly Func<ClientConnection, Stream, RemoteHttpRequest, CancellationToken, Task> _handler;
        private readonly Action<ClientConnection> _closed;
        private readonly Channel<string> _outbound = Channel.CreateBounded<string>(
            new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly object _eventGate = new();
        private Func<bool>? _authorized;
        private WebSocket? _socket;
        private Stream? _stream;
        private int _closedOnce;

        public ClientConnection(string key, TcpClient tcp,
            Func<ClientConnection, Stream, RemoteHttpRequest, CancellationToken, Task> handler,
            Action<ClientConnection> closed)
        {
            Key = key;
            _tcp = tcp;
            _handler = handler;
            _closed = closed;
        }

        public string Key { get; }
        public string? LocalAddress => (_tcp.Client.LocalEndPoint as IPEndPoint)?.Address.ToString();

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                _tcp.NoDelay = true;
                using var stream = _tcp.GetStream();
                _stream = stream;
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                var request = await RemoteHttp.ReadRequestAsync(stream, readTimeout.Token).ConfigureAwait(false);
                if (request is null) return;
                await _handler(this, stream, request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // 局域网客户端的断开/半包不上升为宿主异常；连接资源由 finally 释放。
            }
            finally
            {
                RequestClose();
                _closed(this);
            }
        }

        public void Enqueue(string payload)
        {
            lock (_eventGate)
            {
                // HTTP/未配对连接不接收历史；慢客户端断开后通过快照恢复，不无限占用内存。
                if (_authorized is null) return;
                if (!_authorized() || !_outbound.Writer.TryWrite(payload)) RequestClose();
            }
        }

        /// <summary>完成升级并进入推送循环，直到连接关闭。</summary>
        public async Task AttachWebSocketAsync(Stream stream, RemoteHttpRequest request,
            Func<RemoteSessionSnapshot> snapshot, Func<bool> authorized, CancellationToken cancellationToken)
        {
            var socket = await RemoteHttp.AcceptWebSocketAsync(stream, request, cancellationToken)
                .ConfigureAwait(false);
            if (socket is null) return;
            _socket = socket;
            lock (_eventGate)
            {
                if (!authorized()) { RequestClose(); return; }
                _outbound.Writer.TryWrite(RemoteWire.SerializeUpdate(new RemoteSnapshotUpdate(snapshot())));
                _authorized = authorized;
            }
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var expiry = WatchAuthorizationAsync(authorized, lifetime.Token);
            // 接收泵：手机端关闭页面/切网时不发送任何应用层消息，只有读侧能看到关闭或断链。
            // 没有它，空闲客户端的连接会一直被计入连接数并占用 TCP 资源。
            _ = Task.Run(() => ReceivePumpAsync(socket), CancellationToken.None);
            try
            {
                await foreach (var payload in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!authorized()) break;
                    var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // 手机浏览器关闭页面或切网：静默回收，不影响其他连接。
            }
            finally
            {
                lifetime.Cancel();
                _outbound.Writer.TryComplete();
                RequestClose();
                await expiry.ConfigureAwait(false);
            }
        }

        private async Task WatchAuthorizationAsync(Func<bool> authorized, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                    if (!authorized()) { RequestClose(); return; }
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>读侧泵：收到关闭帧或读写失败即结束推送循环，让连接被真正回收。</summary>
        private async Task ReceivePumpAsync(WebSocket socket)
        {
            var buffer = new byte[1024];
            try
            {
                while (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseSent)
                {
                    var result = await socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    // 本协议是服务端单向推送；客户端上行不承载内容，读到即忽略。
                }
            }
            catch (Exception)
            {
                // 连接重置、进程退出或主动关闭：统一按连接结束处理。
            }

            _outbound.Writer.TryComplete();
            RequestClose();
        }

        public void RequestClose()
        {
            if (Interlocked.Exchange(ref _closedOnce, 1) != 0) return;
            _outbound.Writer.TryComplete();
            var socket = _socket;
            if (socket is not null)
            {
                try { socket.Abort(); } catch (ObjectDisposedException) { } catch (WebSocketException) { }
            }

            try { _tcp.Close(); } catch (SocketException) { }
        }
    }
}
