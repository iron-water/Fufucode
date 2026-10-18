using System.Text.Json;
using Fufucode.Claude;

namespace Fufucode.Claude.Tests;

/// <summary>
/// stream-json 帧解码：使用 2026-10-16 真实抓取到的帧形状，正负双向断言。
/// </summary>
internal static class ClaudeFrameTests
{
    public static void Run(Checks checks)
    {
        Classify(checks);
        InitHandshake(checks);
        AssistantBlocks(checks);
        ToolResults(checks);
        ResultFrame(checks);
        PermissionRequest(checks);
        Tolerance(checks);
        Outbound(checks);
    }

    private static void Classify(Checks checks)
    {
        checks.Equal(ClaudeFrameKind.System, ClaudeProtocol.Classify(Parse("""{"type":"system","subtype":"init"}""")),
            "system 帧分类为 System");
        checks.Equal(ClaudeFrameKind.Assistant, ClaudeProtocol.Classify(Parse("""{"type":"assistant"}""")),
            "assistant 帧分类为 Assistant");
        checks.Equal(ClaudeFrameKind.User, ClaudeProtocol.Classify(Parse("""{"type":"user"}""")),
            "user 帧分类为 User");
        checks.Equal(ClaudeFrameKind.Result, ClaudeProtocol.Classify(Parse("""{"type":"result"}""")),
            "result 帧分类为 Result");
        checks.Equal(ClaudeFrameKind.ControlRequest, ClaudeProtocol.Classify(Parse("""{"type":"control_request"}""")),
            "control_request 帧分类为 ControlRequest");
        checks.Equal(ClaudeFrameKind.StreamEvent, ClaudeProtocol.Classify(Parse("""{"type":"stream_event"}""")),
            "stream_event 帧分类为 StreamEvent");
        checks.Equal(ClaudeFrameKind.Unknown, ClaudeProtocol.Classify(Parse("""{"type":"mystery"}""")),
            "未知帧类型不抛出，分类为 Unknown");
        checks.Equal(ClaudeFrameKind.Unknown, ClaudeProtocol.Classify(Parse("""{"no_type":1}""")),
            "缺 type 的帧分类为 Unknown 而不抛出");
    }

    private static void InitHandshake(Checks checks)
    {
        var init = ClaudeProtocol.ParseInit(Parse(
            """{"type":"system","subtype":"init","cwd":"C:\\Users\\mio","session_id":"16043382-ffe6-4efc-9a27-cc33ace582fa","tools":["Task","Bash","Edit","Read","Write"],"mcp_servers":[{"name":"tavily","status":"connected"}],"model":"group/auto-deepseek-v4-1-flash[1m]","permissionMode":"manual","claude_code_version":"2.1.296","capabilities":["interrupt_receipt_v1","msg_lifecycle_v1"],"messaging_socket_path":"pipe"}"""));
        checks.Check(init is not null, "init 帧解析成功");
        checks.Equal("16043382-ffe6-4efc-9a27-cc33ace582fa", init!.SessionId, "init 暴露 session_id");
        checks.Equal("group/auto-deepseek-v4-1-flash[1m]", init.Model, "含 [1m] 后缀的模型按通用字符串保留");
        checks.Equal("2.1.296", init.CliVersion, "init 暴露 CLI 版本");
        checks.Check(init.Tools.Count == 5 && init.Tools.Contains("Write"), "init 工具清单来自 CLI 自报");
        checks.Check(init.Capabilities.Contains("interrupt_receipt_v1"), "init 能力清单保留");

        checks.Check(ClaudeProtocol.ParseInit(Parse("""{"type":"system","subtype":"hook_started","session_id":"x"}""")) is null,
            "hook_started 不被当作 init");
        checks.Check(ClaudeProtocol.ParseInit(Parse("""{"type":"system","subtype":"init"}""")) is null,
            "缺少 session_id 的 init 解析为 null，不伪造会话标识");
        checks.Check(ClaudeProtocol.ParseInit(Parse("""{"type":"assistant","subtype":"init","session_id":"x"}""")) is null,
            "assistant 帧即使带 init 子类型也不被当作握手");
    }

    private static void AssistantBlocks(Checks checks)
    {
        var frame = ClaudeProtocol.ParseAssistant(Parse(
            """{"type":"assistant","message":{"content":[{"type":"text","text":"ok"},{"type":"thinking","thinking":"hmm"},{"type":"tool_use","name":"Bash","id":"call_00_1","input":{"command":"dir"}}],"model":"deepseek-v4.1-flash","role":"assistant","id":"msg_1"},"session_id":"s1"}"""));
        checks.Equal("msg_1", frame.MessageId, "assistant 暴露 message.id");
        checks.Equal(3, frame.Blocks.Count, "assistant 解析全部内容块");
        checks.Equal("ok", ((ClaudeTextBlock)frame.Blocks[0]).Text, "text 块文本正确");
        checks.Equal("hmm", ((ClaudeThinkingBlock)frame.Blocks[1]).Text, "thinking 块文本正确");
        var tool = (ClaudeToolUseBlock)frame.Blocks[2];
        checks.Equal("call_00_1", tool.Id, "tool_use 保留工具调用 ID");
        checks.Equal("Bash", tool.Name, "tool_use 保留工具名");
        checks.Equal("""{"command":"dir"}""", tool.InputJson, "tool_use 保留原始参数 JSON");

        var empty = ClaudeProtocol.ParseAssistant(Parse("""{"type":"assistant"}"""));
        checks.Equal(0, empty.Blocks.Count, "缺少 message.content 的 assistant 帧解析为空块而不抛出");
        var weird = ClaudeProtocol.ParseAssistant(Parse("""{"type":"assistant","message":{"content":[{"type":"future_block","x":1}]}}"""));
        checks.Check(weird.Blocks[0] is ClaudeOtherBlock { Type: "future_block" }, "未知内容块保留类型而不丢弃");
    }

    private static void ToolResults(Checks checks)
    {
        var allowed = ClaudeProtocol.ParseToolResults(Parse(
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"HELLO-FROM-BASH","is_error":false,"tool_use_id":"call_00_1"}]},"tool_use_result":{"stdout":"HELLO-FROM-BASH"},"tool_result_meta":[{"id":"call_00_1","permission_decision":{"decision":"accept","source":"config"}}]}"""));
        checks.Equal(1, allowed.Count, "user 帧解析出工具结果");
        checks.Check(!allowed[0].IsError, "批准路径 is_error=false");
        checks.Equal("HELLO-FROM-BASH", allowed[0].Text, "工具结果文本正确");
        checks.Equal("accept", allowed[0].PermissionDecision, "保留 permission_decision.decision");
        checks.Equal("config", allowed[0].PermissionSource, "保留 permission_decision.source");

        var rejected = ClaudeProtocol.ParseToolResults(Parse(
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"This command requires approval","is_error":true,"tool_use_id":"call_00_2"}]},"tool_result_meta":[{"id":"call_00_2","non_execution_kind":"user-rejected","permission_decision":{"decision":"reject","source":"config"}}]}"""));
        checks.Check(rejected[0].IsError, "拒绝路径 is_error=true，不静默吞掉");
        checks.Equal("reject", rejected[0].PermissionDecision, "拒绝路径保留 reject 决策");
        checks.Equal("user-rejected", rejected[0].NonExecutionKind, "拒绝路径保留 non_execution_kind");

        checks.Equal(0, ClaudeProtocol.ParseToolResults(Parse("""{"type":"assistant","message":{}}""")).Count,
            "非 user 帧不产出工具结果");
    }

    private static void ResultFrame(Checks checks)
    {
        var result = ClaudeProtocol.ParseResult(Parse(
            """{"type":"result","stop_reason":"end_turn","session_id":"s1","total_cost_usd":0.058888,"num_turns":1,"subtype":"success","is_error":false,"result":"ok","duration_ms":3737,"usage":{"input_tokens":7815,"output_tokens":109,"cache_read_input_tokens":34176}}"""));
        checks.Check(result is not null, "result 帧解析成功");
        checks.Check(!result!.IsError, "成功 result 的 is_error 为 false");
        checks.Equal("end_turn", result.StopReason, "保留 stop_reason");
        checks.Equal(0.058888m, result.TotalCostUsd, "保留 total_cost_usd");
        checks.Equal(1, result.NumTurns, "保留 num_turns");
        checks.Equal(7815L, result.Usage!.InputTokens, "保留 input_tokens");
        checks.Equal(34176L, result.Usage.CacheReadTokens, "保留 cache_read_input_tokens");

        var failure = ClaudeProtocol.ParseResult(Parse(
            """{"type":"result","subtype":"error_during_execution","is_error":true,"num_turns":0,"session_id":"00000000-0000-0000-0000-000000000000","total_cost_usd":0.0}"""));
        checks.Check(failure!.IsError, "失败 result 的 is_error 为 true");
        checks.Equal("error_during_execution", failure.Subtype, "保留失败子类型");
        checks.Check(failure.Usage is null, "缺少 usage 时不伪造用量");
        checks.Check(ClaudeProtocol.ParseResult(Parse("""{"type":"assistant"}""")) is null, "非 result 帧不产出结果");
    }

    private static void PermissionRequest(Checks checks)
    {
        var request = ClaudeProtocol.ParsePermissionRequest(Parse(
            """{"type":"control_request","request_id":"430d192c-8a19-4a54-bb64-db5625e16c91","request":{"subtype":"can_use_tool","tool_name":"Write","display_name":"Write","input":{"file_path":"x.txt","content":"OK"},"description":"x.txt","permission_suggestions":[{"type":"setMode","mode":"acceptEdits","destination":"session"}],"tool_use_id":"call_00_QmCX"}}"""));
        checks.Check(request is not null, "can_use_tool 控制请求解析成功");
        checks.Equal("430d192c-8a19-4a54-bb64-db5625e16c91", request!.RequestId, "审批请求保留 request_id（回灌令牌）");
        checks.Equal("Write", request.ToolName, "审批请求保留工具名");
        checks.Equal("call_00_QmCX", request.ToolUseId, "审批请求保留 tool_use_id");
        checks.Equal("""{"file_path":"x.txt","content":"OK"}""", request.InputJson, "审批请求保留原始 input");

        checks.Check(ClaudeProtocol.ParsePermissionRequest(Parse(
            """{"type":"control_request","request_id":"r1","request":{"subtype":"interrupt"}}""")) is null,
            "非 can_use_tool 控制请求不产出审批");
        checks.Check(ClaudeProtocol.ParsePermissionRequest(Parse(
            """{"type":"control_request","request":{"subtype":"can_use_tool","tool_name":"Write"}}""")) is null,
            "缺少 request_id 的审批请求解析为 null");
    }

    private static void Tolerance(Checks checks)
    {
        var frames = new[]
        {
            """{"type":"system","subtype":"hook_started","hook_id":"h","hook_name":"SessionStart:startup","session_id":"s"}""",
            """{"type":"system","subtype":"hook_response","hook_id":"h","exit_code":0,"outcome":"success","session_id":"s"}""",
            """{"type":"system","subtype":"commands_changed","commands":[],"session_id":"s"}""",
            """{"type":"system","subtype":"thinking_tokens","estimated_tokens":3,"session_id":"s"}""",
            """{"type":"system","subtype":"permission_denied","session_id":"s"}""",
            """{"type":"keep_alive"}""",
        };
        foreach (var raw in frames)
            checks.Check(ClaudeProtocol.Classify(Parse(raw)) is not ClaudeFrameKind.Assistant and not ClaudeFrameKind.User,
                "环境帧宽容跳过：" + raw[..Math.Min(48, raw.Length)]);

        var sessionId = ClaudeProtocol.ParseInit(Parse(
            """{"type":"system","subtype":"init","session_id":"s1","tools":[],"model":"m"}"""))!.SessionId;
        checks.Equal("s1", sessionId, "环境帧存在时 init 仍正确解析");
    }

    private static void Outbound(Checks checks)
    {
        var arguments = ClaudeProtocol.BuildArguments("group/auto-deepseek-v4-1-flash[1m]", "abc-123");
        checks.Equal("-p", arguments[0], "执行命令以 -p 开始");
        checks.Equal("stream-json", arguments[Array.IndexOf(arguments.ToArray(), "--output-format") + 1],
            "输出格式为 stream-json");
        checks.Equal("stream-json", arguments[Array.IndexOf(arguments.ToArray(), "--input-format") + 1],
            "输入格式为 stream-json");
        checks.Check(arguments.Contains("--verbose"), "包含 --verbose（stream-json 必需）");
        checks.Equal("stdio", arguments[Array.IndexOf(arguments.ToArray(), "--permission-prompt-tool") + 1],
            "审批走官方 stdio 桥，不绕过权限模型");
        checks.Check(!arguments.Contains("--dangerously-skip-permissions"), "不使用跳过权限的开关");
        checks.Equal("abc-123", arguments[Array.IndexOf(arguments.ToArray(), "--resume") + 1], "指定 --resume 会话标识");
        checks.Equal("group/auto-deepseek-v4-1-flash[1m]", arguments[Array.IndexOf(arguments.ToArray(), "--model") + 1],
            "模型字符串按通用值传递");
        checks.Check(!ClaudeProtocol.BuildArguments(null).Contains("--model"), "未给模型时不写 --model");
        checks.Check(!ClaudeProtocol.BuildArguments(null).Contains("--resume"), "未给会话标识时不写 --resume");

        var user = Parse(ClaudeProtocol.BuildUserMessage("hi"));
        checks.Equal("user", user.GetProperty("type").GetString(), "用户消息帧类型为 user");
        checks.Equal("hi", user.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString(),
            "用户消息文本正确传递");

        var allow = Parse(ClaudeProtocol.BuildPermissionResponse("r1", ClaudePermissionDecision.Allow, null,
            Parse("""{"file_path":"a.txt","content":"OK"}""")));
        checks.Equal("control_response", allow.GetProperty("type").GetString(), "审批应答帧类型为 control_response");
        checks.Equal("r1", allow.GetProperty("response").GetProperty("request_id").GetString(), "审批应答回填 request_id");
        var allowPayload = allow.GetProperty("response").GetProperty("response");
        checks.Equal("allow", allowPayload.GetProperty("behavior").GetString(), "允许路径 behavior=allow");
        checks.Equal("a.txt", allowPayload.GetProperty("updatedInput").GetProperty("file_path").GetString(),
            "允许路径原样回填 CLI 给出的 input");

        var deny = Parse(ClaudeProtocol.BuildPermissionResponse("r2", ClaudePermissionDecision.Deny, "nope", null));
        var denyPayload = deny.GetProperty("response").GetProperty("response");
        checks.Equal("deny", denyPayload.GetProperty("behavior").GetString(), "拒绝路径 behavior=deny");
        checks.Equal("nope", denyPayload.GetProperty("message").GetString(), "拒绝路径携带理由");

        var noInput = Parse(ClaudeProtocol.BuildPermissionResponse("r3", ClaudePermissionDecision.Allow, null, null));
        checks.Check(!noInput.GetProperty("response").GetProperty("response").TryGetProperty("updatedInput", out _),
            "无 input 时不伪造 updatedInput");
    }

    internal static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
