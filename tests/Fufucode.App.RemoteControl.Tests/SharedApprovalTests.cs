using System.Text.Json;
using Fufucode.Core;
using Fufucode.Core.Approvals;

namespace Fufucode.App.RemoteControl.Tests;

/// <summary>
/// 单一审批 gate 的回归：两个入口竞争同一个决定，只有第一个生效；
/// 会话关闭如实标记失效；请求对象与 generation 原样回传。
/// </summary>
internal static class SharedApprovalTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        // 1. 先到先得：手机先批准，FUFU 后到得到已处理，且不产生第二次 Respond。
        var session = new GateSession();
        var gate = SharedApprovalRegistry.For(session);
        check(ReferenceEquals(gate, SharedApprovalRegistry.For(session)),
            "同一会话始终复用同一个共享 gate");

        var asked = 0;
        gate.Requested += _ => asked++;
        session.RaiseRequest();
        check(asked == 1, "共享 gate 只广播一次待审批请求");
        check(gate.Pending.Count == 1, "共享 gate 的待审批列表来自真实会话");

        var approvalId = gate.Pending[0].ApprovalId;
        var jiii = await gate.DecideAsync(approvalId, approved: true, "jiii");
        check(jiii.Accepted && jiii.Source == "jiii", "Jiii 先决定时被接受");
        check(session.RespondCount == 1 && session.Confirmed == true,
            "决定回传真实会话且只回传一次");

        var fufu = await gate.DecideAsync(approvalId, approved: false, "fufu");
        check(!fufu.Accepted, "FUFU 后到决定被拒绝");
        check(fufu.Message?.Contains("jiii", StringComparison.OrdinalIgnoreCase) == true,
            "后到者收到『已由 jiii 处理』的说明");
        check(session.RespondCount == 1, "后到者不会产生第二次 Respond");

        // 2. 未知请求不伪造批准。
        var unknown = await gate.DecideAsync("missing", approved: true, "fufu");
        check(!unknown.Accepted && session.RespondCount == 1, "未知审批不会回传真实会话");

        // 3. 会话关闭：未决请求如实标记失效。
        var closing = new GateSession();
        var closingGate = SharedApprovalRegistry.For(closing);
        var decided = new List<SharedApprovalResolution>();
        closingGate.Decided += decided.Add;
        closing.RaiseRequest();
        var closingId = closingGate.Pending[0].ApprovalId;
        closing.RaiseClosed();
        check(decided.Any(item => item.ApprovalId == closingId && !item.Accepted),
            "会话关闭后未决请求被标记失效");
        check(closingGate.Pending.Count == 0, "失效后不再出现在待审批列表");

        // 4. 已决定后再关闭，不覆盖既有成功决定。
        var ordered = new GateSession();
        var orderedGate = SharedApprovalRegistry.For(ordered);
        var orderedDecisions = new List<SharedApprovalResolution>();
        orderedGate.Decided += orderedDecisions.Add;
        ordered.RaiseRequest();
        var orderedId = orderedGate.Pending[0].ApprovalId;
        var accepted = await orderedGate.DecideAsync(orderedId, approved: true, "jiii");
        ordered.RaiseClosed();
        check(accepted.Accepted, "决定成功");
        check(orderedDecisions.Count(item => item.ApprovalId == orderedId) == 1,
            "已决定的请求不会被随后的关闭事件覆盖成失效");

        // 5. 请求对象与 generation 原样回传。
        check(ReferenceEquals(ordered.RespondedRequest, ordered.Original),
            "回传的是真实会话的原始请求对象");

        // 6. 不同会话不共享 gate。
        var other = new GateSession();
        check(!ReferenceEquals(SharedApprovalRegistry.For(other), orderedGate),
            "不同会话使用不同 gate，不串审批");

        await Task.CompletedTask;
    }

    private sealed class GateSession : IExtensionUiSession
    {
        public ExtensionUiRequest Original { get; } = new(Guid.NewGuid(), "request", "confirm",
            JsonSerializer.SerializeToElement(new { title = "允许工具？", toolName = "write_file" }),
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));

        public ExtensionUiRequest? RespondedRequest { get; private set; }
        public bool? Confirmed { get; private set; }
        public int RespondCount { get; private set; }
        private bool _closed;
        private bool _raised;

        public IReadOnlyList<ExtensionUiRequest> PendingExtensionUiRequests =>
            _raised && !_closed && RespondedRequest is null ? [Original] : [];

        public ApprovalAvailability ApprovalAvailability => ApprovalAvailability.Verified;
        public string? ApprovalDiagnostic => null;

        public event Action<ExtensionUiRequest>? ExtensionUiRequestReceived;
        public event Action<ExtensionUiRequest>? ExtensionUiRequestClosed;
        public event Action<PiExtensionUiRequestEvent>? ExtensionUiNotification;
        public event Action<PiExtensionErrorEvent>? ExtensionError;

        public void RaiseRequest()
        {
            _raised = true;
            ExtensionUiRequestReceived?.Invoke(Original);
        }

        public void RaiseClosed()
        {
            _closed = true;
            ExtensionUiRequestClosed?.Invoke(Original);
        }

        public Task RespondToExtensionUiAsync(ExtensionUiRequest request, PiExtensionUiResponse response,
            CancellationToken cancellationToken = default)
        {
            RespondCount++;
            RespondedRequest = request;
            Confirmed = response.Confirmed;
            RaiseClosed();
            return Task.CompletedTask;
        }
    }
}
