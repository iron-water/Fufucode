#nullable enable
// 测试夹具：模拟 `claude -p --input-format stream-json --output-format stream-json --verbose`
// 的可观测行为（帧形状按 2026-10-16 的真实抓取），用于离线验证 Claude 适配器。
// 仅用于测试，不参与产品运行时。
using System.Text;
using System.Text.Json;

var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
var reader = new StreamReader(input, new UTF8Encoding(false));
var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true };

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

var resumeId = Option("--resume");
var sessionId = resumeId ?? Guid.NewGuid().ToString();
var mode = Environment.GetEnvironmentVariable("FAKE_CLAUDE_MODE") ?? Option("--fake-mode") ?? "text";
var usedTool = false;
var pendingToolId = (string?)null;

void Emit(object frame) => writer.WriteLine(JsonSerializer.Serialize(frame));

void EmitHook()
{
    var hookId = Guid.NewGuid().ToString();
    Emit(new { type = "system", subtype = "hook_started", hook_id = hookId, hook_name = "SessionStart:startup",
        hook_event = "SessionStart", uuid = Guid.NewGuid().ToString("D"), session_id = sessionId });
    Emit(new { type = "system", subtype = "hook_response", hook_id = hookId, hook_name = "SessionStart:startup",
        hook_event = "SessionStart", output = "", stdout = "", stderr = "", exit_code = 0, outcome = "success",
        uuid = Guid.NewGuid().ToString("D"), session_id = sessionId });
}

void EmitInit()
{
    Emit(new
    {
        type = "system", subtype = "init", cwd = Environment.CurrentDirectory, session_id = sessionId,
        tools = new[] { "Bash", "Read", "Write" },
        mcp_servers = Array.Empty<object>(), model = "fake/claude-model[1m]", permissionMode = "manual",
        claude_code_version = "2.1.296",
        capabilities = new[] { "interrupt_receipt_v1", "msg_lifecycle_v1" },
        uuid = Guid.NewGuid().ToString("D"),
    });
}

while (reader.ReadLine() is { } line)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    JsonElement frame;
    try { frame = JsonDocument.Parse(line).RootElement.Clone(); }
    catch (JsonException) { continue; }
    var type = frame.TryGetProperty("type", out var t) ? t.GetString() : null;
    if (type == "control_request")
    {
        var requestId = frame.TryGetProperty("request_id", out var rid) ? rid.GetString() : null;
        var subtype = frame.TryGetProperty("request", out var request) && request.TryGetProperty("subtype", out var sub)
            ? sub.GetString() : null;
        if (subtype == "initialize")
        {
            Emit(new { type = "control_response", response = new { subtype = "success", request_id = requestId,
                response = new { commands = Array.Empty<object>() } } });
            EmitHook();
            Emit(new { type = "system", subtype = "commands_changed", commands = Array.Empty<object>(), session_id = sessionId });
        }
        else if (subtype == "interrupt")
        {
            Emit(new { type = "control_response", response = new { subtype = "success", request_id = requestId,
                response = new { still_queued = Array.Empty<string>() } } });
            Emit(new { type = "result", subtype = "success", stop_reason = "interrupt",
                session_id = sessionId, total_cost_usd = 0.0, num_turns = 1, is_error = false,
                usage = new { input_tokens = 1, output_tokens = 1 } });
        }
        continue;
    }
    if (type == "control_response")
    {
        // 审批回灌：把真实 decision 转成 user/tool_result 帧（拒绝路径不得静默吞掉）。
        var decision = frame.TryGetProperty("response", out var response) && response.TryGetProperty("response", out var payload)
            ? payload : default;
        var behavior = decision.ValueKind == JsonValueKind.Object && decision.TryGetProperty("behavior", out var b)
            ? b.GetString() : "deny";
        if (behavior == "allow")
        {
            Emit(new { type = "user", message = new { role = "user",
                content = new object[] { new { type = "tool_result", tool_use_id = pendingToolId, content = "File created successfully.", is_error = false } } },
                tool_use_result = new { stdout = "File created successfully.", stderr = "", interrupted = false },
                tool_result_meta = new object[] { new { id = pendingToolId,
                    permission_decision = new { decision = "accept", source = "user" } } },
                session_id = sessionId });
            Emit(new { type = "assistant", message = new { content = new object[] { new { type = "text", text = "OK" } },
                id = "msg_fake_" + Guid.NewGuid().ToString("N"), model = "claude-fake", role = "assistant" }, session_id = sessionId });
        }
        else
        {
            var denyMessage = decision.ValueKind == JsonValueKind.Object && decision.TryGetProperty("message", out var m)
                ? m.GetString() : "Denied.";
            Emit(new { type = "user", message = new { role = "user",
                content = new object[] { new { type = "tool_result", tool_use_id = pendingToolId,
                    content = denyMessage, is_error = true } } },
                tool_result_meta = new object[] { new { id = pendingToolId,
                    non_execution_kind = "user-rejected", permission_decision = new { decision = "reject", source = "host" } } },
                session_id = sessionId });
        }
        Emit(new { type = "result", subtype = "success", stop_reason = "end_turn", session_id = sessionId,
            total_cost_usd = 0.01, num_turns = 1, is_error = false,
            usage = new { input_tokens = 10, output_tokens = 5 } });
        continue;
    }
    if (type != "user") continue;
    var text = frame.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) &&
        content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0 &&
        content[0].TryGetProperty("text", out var textValue) ? textValue.GetString() ?? "" : "";
    EmitInit();
    if (mode == "tool" && !usedTool)
    {
        usedTool = true;
        pendingToolId = "call_fake_tool";
        Emit(new { type = "assistant", message = new { content = new object[] { new { type = "text", text = "Working." } },
            id = "msg_fake_" + Guid.NewGuid().ToString("N"), model = "claude-fake", role = "assistant" }, session_id = sessionId });
        Emit(new { type = "assistant", message = new { content = new object[] { new { type = "tool_use", name = "Write",
            id = pendingToolId, input = new { file_path = "fake-out.txt", content = "OK" } } },
            id = "msg_fake_" + Guid.NewGuid().ToString("N"), model = "claude-fake", role = "assistant" }, session_id = sessionId });
        Emit(new { type = "control_request", request_id = Guid.NewGuid().ToString(),
            request = new { subtype = "can_use_tool", tool_name = "Write", display_name = "Write",
                input = new { file_path = "fake-out.txt", content = "OK" }, description = "fake-out.txt",
                tool_use_id = pendingToolId,
                permission_suggestions = new object[] { new { type = "setMode", mode = "acceptEdits", destination = "session" } } } });
        continue;
    }
    Emit(new { type = "system", subtype = "thinking_tokens", estimated_tokens = 3, estimated_tokens_delta = 3, session_id = sessionId });
    Emit(new { type = "assistant", message = new { content = new object[] { new { type = "text", text = $"echo:{text}" } },
        id = "msg_fake_" + Guid.NewGuid().ToString("N"), model = "claude-fake", role = "assistant" }, session_id = sessionId });
    Emit(new { type = "result", subtype = "success", stop_reason = "end_turn", session_id = sessionId,
        total_cost_usd = 0.002, num_turns = 1, is_error = false, result = $"echo:{text}",
        usage = new { input_tokens = 7, output_tokens = 3, cache_read_input_tokens = 11, cache_creation_input_tokens = 0 } });
}
