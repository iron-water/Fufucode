using System.Text.Json;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.ComputerUse;

public sealed partial class ComputerUsePlugin
{
    public const string WaitTool = "computer_wait";
    private const string WaitSchema = """
        {"type":"object","properties":{"processId":{"type":"integer","minimum":1},"windowId":{"type":"integer","minimum":1},"label":{"type":"string","minLength":1,"maxLength":200},"role":{"type":"string","minLength":1,"maxLength":80},"enabled":{"type":"boolean"},"timeoutMs":{"type":"integer","minimum":100,"maximum":15000,"default":5000},"session":{"type":"string"}},"required":["processId","windowId","label"],"additionalProperties":false}
        """;

    private async Task<PureToolResult> WaitAsync(PureToolCall call, CancellationToken ct)
    {
        if (!ScreenCaptureEnabled) return new(call.Id, false, "窗口观察能力当前已关闭。");
        using var doc = JsonDocument.Parse(ArgumentsOrEmpty(call));
        var args = doc.RootElement;
        var pid = ReadInt(args, "processId");
        var window = ReadLong(args, "windowId");
        if (pid is not > 0 || window is not > 0) return Missing(call.Id, "条件等待必须指定 processId 和 windowId。");
        var label = ReadString(args, "label");
        var role = ReadString(args, "role");
        if (string.IsNullOrWhiteSpace(label) || label.Length > 200) return Missing(call.Id, "label 必须为 1–200 字符的精确控件标签。");
        if (args.TryGetProperty("role", out _) && (string.IsNullOrWhiteSpace(role) || role.Length > 80)) return Missing(call.Id, "role 无效。");
        if (args.TryGetProperty("enabled", out var flag) && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Missing(call.Id, "enabled 必须是布尔值。");
        var ms = args.TryGetProperty("timeoutMs", out _) ? ReadInt(args, "timeoutMs") : 5000;
        if (ms is not (>= 100 and <= 15000)) return Missing(call.Id, "timeoutMs 必须为 100–15000。");

        var target = new ComputerUseTarget(pid, window, Session: ReadString(args, "session"));
        var condition = new WindowCondition(label, role, flag.ValueKind == JsonValueKind.Undefined ? null : flag.GetBoolean());
        var options = new WindowReadOptions(label, 100, Condition: condition);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ms.Value);
        try
        {
            while (true)
            {
                var result = await ReadObservationAsync(target, options, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (result.Success) return new(call.Id, true, result.Message);
                if (result.ErrorCode != "condition_not_seen") return Failure(call.Id, result);
                // Release the per-window observation lease between polls; never execute or retry input here.
                await Task.Delay(300, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failure(call.Id, new(false, "期限内没有观察到匹配控件；这不证明它不存在。", ErrorCode: "condition_timeout"));
        }
    }

    private async Task<PureToolResult> DescribeCapabilitiesAsync(PureToolCall call, CancellationToken ct)
    {
        var summary = DescribeCapabilities(call.Id);
        using var doc = JsonDocument.Parse(ArgumentsOrEmpty(call));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return Missing(call.Id, "需要参数对象。");
        if (!doc.RootElement.TryGetProperty("probe", out var probe) || probe.ValueKind == JsonValueKind.False) return summary;
        if (probe.ValueKind != JsonValueKind.True) return Missing(call.Id, "probe 必须是布尔值。");
        if (CuaObserver is not { } cua) return Failure(call.Id, new(false, "未找到 CUA CLI。", ErrorCode: "backend_unavailable"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Windows 0.28.2 can take ~4s for a healthy check_permissions round trip.
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var result = await cua.ProbeAsync(deadline.Token).ConfigureAwait(false);
            return result.Success ? new(call.Id, true, summary.Content + "\n" + result.Message) : Failure(call.Id, result);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failure(call.Id, new(false, "10 秒内没有得到 CUA 服务回执。", ErrorCode: "probe_timeout"));
        }
    }
}

public sealed partial class CuaDriverComputerUseBackend
{
    internal async Task<ComputerUseBackendResult> ProbeAsync(CancellationToken ct)
    {
        var result = await CallAsync("check_permissions", [], ct).ConfigureAwait(false);
        if (!result.Success) return Failure("已找到 CLI，但服务探测失败；检查 daemon 是否运行、socket 是否匹配。", "probe_failed");
        using var doc = JsonDocument.Parse(result.RawJson!);
        var data = doc.RootElement.TryGetProperty("structuredContent", out var sc) ? sc : doc.RootElement;
        if (Flag(data, "uia") is not bool uia || Flag(data, "post_message") is not bool post)
            return Failure("CUA 未返回可识别的 Windows 能力回执。", "invalid_probe");
        return new(true, $"CUA daemon=reachable; uia={uia}; postMessage={post}。这不是对指定窗口可操作的保证。");
    }
}
