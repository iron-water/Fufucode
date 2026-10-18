using System.Net;
using System.Net.Sockets;
using System.Text;
using Fufucode.App.Features.RemoteControl;
using Fufucode.Core.RemoteControl;
using Fufucode.Core;
using System.Text.Json;
using System.Net.WebSockets;
using Fufucode.App.Abstractions;

namespace Fufucode.App.RemoteControl.Tests;

internal static class RemoteAuditTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var clock = new Clock();
        var session = new FakeChatSession();
        using var adapter = new RemoteSessionAdapter(session, "audit");
        await using var server = new RemoteControlServer(adapter,
            new RemoteControlOptions { BindAddress = IPAddress.Loopback, TokenLifetime = TimeSpan.FromSeconds(2) },
            clock: clock);
        var status = server.Start();
        using var client = RemoteHttpClient.Create(status.Port);
        using (var rebound = new HttpRequestMessage(HttpMethod.Post, client.BaseUrl + "/api/pair"))
        {
            rebound.Headers.Host = $"attacker.example:{status.Port}";
            rebound.Headers.Add("Origin", $"http://attacker.example:{status.Port}");
            rebound.Content = new StringContent("{\"pin\":\"" + status.Pin + "\"}", Encoding.UTF8, "application/json");
            using var response = await client.Raw.SendAsync(rebound);
            check(response.StatusCode == HttpStatusCode.Forbidden, "DNS 重绑定 Host 不得配对");
        }
        check(RemoteControlNetwork.ResolveAdvertisedHost(new(), new IPEndPoint(IPAddress.IPv6Loopback, 1234)) == "::1",
            "IPv6 回环绑定公布实际 IPv6 地址");
        using (var request = new HttpRequestMessage(HttpMethod.Post, client.BaseUrl + "/api/pair"))
        {
            request.Headers.Add("Origin", "https://unrelated.example");
            request.Content = new StringContent("{\"pin\":\"" + status.Pin + "\"}", Encoding.UTF8, "application/json");
            using var response = await client.Raw.SendAsync(request);
            check(response.StatusCode == HttpStatusCode.Forbidden, "跨站网页不能调用配对接口");
        }
        client.Token = (await client.PairAsync(status.Pin!)).Token;
        using (var request = new HttpRequestMessage(HttpMethod.Post, client.BaseUrl + "/api/pair"))
        {
            request.Headers.Add("Origin", "chrome-extension://abcdefghijklmnop");
            request.Content = new StringContent("{\"pin\":\"" + status.Pin + "\"}", Encoding.UTF8, "application/json");
            using var response = await client.Raw.SendAsync(request);
            check(response.StatusCode == HttpStatusCode.OK, "保留浏览器扩展既有 PIN 配对流程");
        }
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, status.Port);
            var stream = tcp.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /api/events?token=" + client.Token + " HTTP/1.1\r\n"));
            await Task.Delay(40);
            session.SimulateUserMessage("before-upgrade", "已有事件");
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"Host: 127.0.0.1:{status.Port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n"));
            using var timeout = new CancellationTokenSource(3000);
            var header = new StringBuilder();
            var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n"))
            {
                if (await stream.ReadAsync(one, timeout.Token) == 0) throw new Exception("握手提前关闭");
                header.Append((char)one[0]);
            }
            using var ws = WebSocket.CreateFromStream(stream, false, null, TimeSpan.FromSeconds(30));
            var payload = new byte[8192];
            var received = await ws.ReceiveAsync(payload, timeout.Token);
            var update = JsonSerializer.Deserialize<RemoteUpdate>(Encoding.UTF8.GetString(payload, 0, received.Count), RemoteWire.Options);
            check(update is RemoteSnapshotUpdate, "升级前已有事件也不能排在首帧快照之前");
        }
        var (socket, first) = await client.OpenEventsAsync();
        using (socket)
        {
            check(first is RemoteSnapshotUpdate, "已配对事件流首帧为完整快照");
            clock.Now += TimeSpan.FromSeconds(3);
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (server.ConnectionCount > 0 && DateTime.UtcNow < deadline) await Task.Delay(30);
            check(server.ConnectionCount == 0, "令牌过期后空闲 WebSocket 被主动关闭");
            check((await client.GetAsync("/api/snapshot")).Status == HttpStatusCode.Unauthorized,
                "同一过期令牌的 HTTP 请求被拒绝");
        }
        // 半包不允许调用 bridge；用真实 TCP 的发送侧关闭制造 Content-Length 不足。
        client.Token = (await client.PairAsync(status.Pin!)).Token;
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, status.Port);
            var stream = tcp.GetStream();
            var body = "{\"text\":\"must-not-send\"}";
            var bytes = Encoding.ASCII.GetBytes($"POST /api/message HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer {client.Token}\r\nContent-Length: {body.Length + 5}\r\n\r\n{body}");
            await stream.WriteAsync(bytes);
            tcp.Client.Shutdown(SocketShutdown.Send);
            using var timeout = new CancellationTokenSource(3000);
            var count = await stream.ReadAsync(new byte[1024], timeout.Token);
            check(count == 0, "截断请求被直接关闭而不返回成功响应");
            check(session.Sent.Count == 0, "截断 HTTP 正文不会执行消息");
        }
        for (var i = 0; i < 20; i++) { server.Stop(); server.Start(); }
        using var restarted = RemoteHttpClient.Create(server.Status.Port);
        check((await restarted.PairAsync(server.Status.Pin!)).Success, "连续启停不串用旧 CTS 或撤销新代次令牌");
        session.CurrentModel = "changed";
        check(adapter.Snapshot().Model == "changed", "快照模型随真实会话变化");
        adapter.LoadHistory([new ChatMessage("historical", MessageRole.User, null, [new TextBlock("历史")], null, true)]);
        adapter.LoadHistory([new ChatMessage("historical", MessageRole.User, null, [new TextBlock("历史")], null, true)]);
        check(adapter.Snapshot().Entries.First().Id == "historical" &&
            adapter.Snapshot().Entries.Count(e => e.Id == "historical") == 1, "历史补齐保持顺序且不重复插入");
        adapter.Dispose();
        check(!(await adapter.SendAsync("late")).Accepted && session.Sent.Count == 0,
            "已释放适配器不能继续写入原会话");
        var approvals = new InMemoryToolApprovalGate();
        var early = approvals.Request("t", "read", "{}");
        await approvals.ResolveAsync(early.ApprovalId, RemoteApprovalDecision.Approve);
        check((await approvals.WaitAsync(early.ApprovalId)).Decision == RemoteApprovalDecision.Approve,
            "审批先于 Wait 到达仍返回原决定");
        var real = new ConfirmationSession();
        using var realAdapter = new RemoteSessionAdapter(real, "真实审批接缝");
        var pending = realAdapter.Approvals.Pending.Single();
        var result = await realAdapter.Approvals.ResolveAsync(pending.ApprovalId, RemoteApprovalDecision.Approve);
        check(result.Accepted && ReferenceEquals(real.Original, real.Responded) && real.Confirmed == true,
            "手机批准回传真实会话的原始请求对象与代次");
        check(!(await realAdapter.Approvals.ResolveAsync(pending.ApprovalId, RemoteApprovalDecision.Approve)).Accepted,
            "真实审批结束后不重复批准");
        check(realAdapter.Approvals.Pending.Count == 0, "真实宿主结束审批后手机不保留幽灵请求");
    }

    private sealed class ConfirmationSession : FakeChatSession, IExtensionUiSession
    {
        public ExtensionUiRequest Original { get; } = new(Guid.NewGuid(), "request", "confirm",
            JsonSerializer.SerializeToElement(new { title = "允许工具？" }), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        public ExtensionUiRequest? Responded;
        public bool? Confirmed;
        public IReadOnlyList<ExtensionUiRequest> PendingExtensionUiRequests => Responded is null ? [Original] : [];
        public ApprovalAvailability ApprovalAvailability => ApprovalAvailability.Verified;
        public string? ApprovalDiagnostic => null;
        public event Action<ExtensionUiRequest>? ExtensionUiRequestReceived { add { } remove { } }
        public event Action<ExtensionUiRequest>? ExtensionUiRequestClosed;
        public event Action<PiExtensionUiRequestEvent>? ExtensionUiNotification { add { } remove { } }
        public event Action<PiExtensionErrorEvent>? ExtensionError { add { } remove { } }
        public Task RespondToExtensionUiAsync(ExtensionUiRequest request, PiExtensionUiResponse response,
            CancellationToken cancellationToken = default)
        {
            Responded = request;
            Confirmed = response.Confirmed;
            ExtensionUiRequestClosed?.Invoke(request);
            return Task.CompletedTask;
        }
    }
}
