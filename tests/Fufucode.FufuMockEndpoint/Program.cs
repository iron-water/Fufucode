using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// FUFU-03 验证用模拟模型端点：实现 OpenAI 兼容 /chat/completions 的最小子集。
// 行为确定：普通消息回显；含 RUN_TOOL 触发一次 run_command 工具调用；工具结果回填后给出终态回复。
// 不进入发布产物，仅供本机/CI 自动化验收 FUFU 的 FUMO 会话链路。

var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 8787;
var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();
Console.WriteLine($"[mock] listening http://127.0.0.1:{port}/chat/completions");

while (true)
{
    var ctx = await listener.GetContextAsync();
    _ = Task.Run(() => Handle(ctx));
}

static void Handle(HttpListenerContext ctx)
{
    try
    {
        if (ctx.Request.HttpMethod != "POST" ||
            !ctx.Request.Url!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        var body = new StreamReader(ctx.Request.InputStream).ReadToEnd();
        var root = JsonNode.Parse(body)!.AsObject();
        var streaming = root["stream"]?.GetValue<bool>() == true;
        var messages = root["messages"]!.AsArray();
        var last = messages[^1]!.AsObject();
        var role = last["role"]?.GetValue<string>() ?? "";
        var lastText = last["content"]?.GetValue<string>() ?? "";

        JsonObject message;
        string? finishToolCall = null;
        if (role == "tool")
        {
            message = new JsonObject { ["role"] = "assistant", ["content"] = "FUFU_MOCK：工具结果已收到，回合结束。" };
        }
        else if (lastText.Contains("RUN_TOOL", StringComparison.Ordinal))
        {
            message = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = (string?)null,
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["id"] = "call_fufu_mock_1",
                    ["type"] = "function",
                    ["index"] = 0,
                    ["function"] = new JsonObject
                    {
                        ["name"] = "run_command",
                        ["arguments"] = "{\"command\":\"echo FUFU_TOOL_OK\"}"
                    }
                })
            };
            finishToolCall = "tool_calls";
        }
        else
        {
            message = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = $"FUFU_MOCK 收到：{lastText}"
            };
        }

        var usage = new JsonObject { ["prompt_tokens"] = 8, ["completion_tokens"] = 4, ["total_tokens"] = 12 };

        if (!streaming)
        {
            var payload = new JsonObject
            {
                ["id"] = "chatcmpl-fufu-mock",
                ["object"] = "chat.completion",
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = message,
                    ["finish_reason"] = finishToolCall ?? "stop"
                }),
                ["usage"] = usage
            };
            WriteJson(ctx, 200, payload.ToJsonString());
            return;
        }

        // SSE：逐 token 文本 或 一次性 tool_calls 分片，最后 finish_reason + [DONE]。
        var sb = new StringBuilder();
        void Delta(JsonObject delta, string? finish = null)
        {
            var chunk = new JsonObject
            {
                ["id"] = "chatcmpl-fufu-mock",
                ["object"] = "chat.completion.chunk",
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = delta,
                    ["finish_reason"] = finish
                })
            };
            sb.Append("data: ").Append(chunk.ToJsonString()).Append("\n\n");
        }

        if (message["content"]?.GetValue<string>() is { } text)
        {
            foreach (var piece in ChunkText(text, 12))
                Delta(new JsonObject { ["role"] = "assistant", ["content"] = piece });
        }
        if (message["tool_calls"] is JsonArray calls)
            Delta(new JsonObject { ["role"] = "assistant", ["tool_calls"] = calls.DeepClone() });
        Delta(new JsonObject(), finishToolCall ?? "stop");
        sb.Append("data: [DONE]\n\n");

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.Add("Cache-Control", "no-cache");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
        Console.WriteLine($"[mock] sse reply sent ({(finishToolCall is null ? "text" : "tool_call")})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[mock] error: {ex.Message}");
        try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
    }
}

static IEnumerable<string> ChunkText(string text, int size)
{
    for (var i = 0; i < text.Length; i += size)
        yield return text.Substring(i, Math.Min(size, text.Length - i));
}

static void WriteJson(HttpListenerContext ctx, int status, string json)
{
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = "application/json";
    var bytes = Encoding.UTF8.GetBytes(json);
    ctx.Response.OutputStream.Write(bytes);
    ctx.Response.Close();
}
