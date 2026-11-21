using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.ComputerUse;

public sealed partial class ComputerUsePlugin
{
    public const string ObserveTool = "computer_observe";
    private const string ObserveSchema = """
        {"type":"object","properties":{"processId":{"type":"integer","minimum":1},"windowId":{"type":"integer","minimum":1},"query":{"type":"string","maxLength":256},"maxElements":{"type":"integer","minimum":1,"maximum":100,"default":40},"session":{"type":"string"}},"required":["processId"],"additionalProperties":false}
        """;

    private async Task<PureToolResult> ObserveAsync(PureToolCall call, CancellationToken ct)
    {
        if (!ScreenCaptureEnabled) return new(call.Id, false, "窗口观察能力当前已关闭。");
        using var doc = JsonDocument.Parse(ArgumentsOrEmpty(call));
        var root = doc.RootElement;
        var pid = ReadInt(root, "processId");
        if (pid is not > 0)
            return Missing(call.Id, "需要有效的 processId。");
        var window = ReadLong(root, "windowId");
        if (root.TryGetProperty("windowId", out _) && window is not > 0)
            return Missing(call.Id, "windowId 无效，不会改为枚举。");
        var limit = root.TryGetProperty("maxElements", out _) ? ReadInt(root, "maxElements") : 40;
        if (limit is not (>= 1 and <= 100)) return Missing(call.Id, "maxElements 必须为 1–100。");
        var query = ReadString(root, "query");
        if (root.TryGetProperty("query", out _) && (query is null || query.Length > 256))
            return Missing(call.Id, "query 必须是 256 字符以内的字符串。");
        if (window is null && (query is not null || root.TryGetProperty("maxElements", out _)))
            return Missing(call.Id, "按控件查询需要 windowId。");

        var cua = _backends.OfType<CuaDriverComputerUseBackend>().FirstOrDefault(b => b.IsAvailable);
        if (cua is null) return Failure(call.Id, new(false, "语义观察需要 CUA Driver，可设置 FUFUCODE_CUA_DRIVER_PATH。", ErrorCode: "backend_unavailable"));
        var target = new ComputerUseTarget(pid, window, Session: ReadString(root, "session"));
        await using var lease = await _operationGate.EnterAsync(target.HasWindow ? target.ResourceKey : $"windows:{pid}", ct).ConfigureAwait(false);
        var result = await cua.ObserveAsync(target, query, limit.Value, ct).ConfigureAwait(false);
        return result.Success ? new(call.Id, true, result.Message) : Failure(call.Id, result);
    }
}

public sealed partial class CuaDriverComputerUseBackend
{
    private static readonly JsonSerializerOptions CompactJson = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    internal async Task<ComputerUseBackendResult> ObserveAsync(ComputerUseTarget target, string? query, int limit, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?> { ["pid"] = target.ProcessId };
        if (target.HasWindow)
        {
            WindowPayload(target, payload);
            payload["include_screenshot"] = false;
            payload["include_accessibility_tree"] = true;
            payload["max_elements"] = limit;
            if (query is { Length: > 0 }) payload["query"] = query;
        }
        var result = await CallAsync(target.HasWindow ? "get_window_state" : "list_windows", payload, ct).ConfigureAwait(false);
        if (!result.Success) return result;
        using var doc = JsonDocument.Parse(result.RawJson!);
        var data = doc.RootElement;
        if (data.TryGetProperty("structuredContent", out var structured)) data = structured;
        if (data.ValueKind != JsonValueKind.Object) return InvalidObservation();
        if (!target.HasWindow)
        {
            if (!ArrayField(data, "windows", out var windows)) return InvalidObservation();
            var candidates = windows.EnumerateArray().Where(w => Number(w, "pid") == target.ProcessId && Number(w, "window_id") is > 0)
                .Select(w => new { windowId = Number(w, "window_id"), title = Text(w, "title"), minimized = Flag(w, "minimized") }).ToArray();
            return Compact(new { processId = target.ProcessId, total = candidates.Length, windows = candidates.Take(100) });
        }

        if (Number(data, "pid") != target.ProcessId || Number(data, "window_id") != target.WindowId ||
            !data.TryGetProperty("snapshot_id", out var snapshot) || snapshot.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(snapshot.GetString()) || !ArrayField(data, "elements", out var elements))
            return InvalidObservation();

        // Allowlist; never return the redundant Markdown, screenshot, edit values or raw driver response.
        var rows = elements.EnumerateArray().Take(limit).Select(e => new
        {
            role = Text(e, "role"), label = Text(e, "label"), enabled = Flag(e, "enabled"), selected = Flag(e, "selected"),
            actions = ArrayField(e, "actions", out var actions) ? actions.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String)
                .Take(12).Select(a => a.GetString()).ToArray() : [],
            elementToken = e.TryGetProperty("element_token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
        });
        return Compact(new { processId = target.ProcessId, windowId = target.WindowId, snapshotId = snapshot.GetString(),
            complete = Flag(data, "elements_complete") == true && elements.GetArrayLength() <= limit &&
                Number(data, "total_element_count") == elements.GetArrayLength(),
            total = Number(data, "total_element_count"), elements = rows });
    }

    private static bool ArrayField(JsonElement e, string key, out JsonElement values) =>
        e.TryGetProperty(key, out values) && values.ValueKind == JsonValueKind.Array;
    private static long? Number(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    private static bool? Flag(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    private static string? Text(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s
        ? (s.Length > 200 ? s[..200] + "…" : s) : null;
    private static ComputerUseBackendResult Compact<T>(T data) => new(true, JsonSerializer.Serialize(data, CompactJson));
    private static ComputerUseBackendResult InvalidObservation() => Failure("CUA 返回的窗口身份或快照结构与请求不符。", "invalid_observation");
}
