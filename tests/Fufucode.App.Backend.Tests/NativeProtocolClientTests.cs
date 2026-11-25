using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core;
using Fufucode.Core.Backend;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// FUMO 原生 Anthropic Messages / OpenAI Responses 协议：请求体形状、SSE 解析、
/// 签名思考与加密推理的跨轮回传、网关拒绝可选字段后的降级，以及完整工具回合。
/// 全部离线：脚本化 HttpMessageHandler 捕获真实请求体并返回真实 SSE 字节流。
/// </summary>
internal static class NativeProtocolClientTests
{
    public static async Task RunAsync(Checks checks)
    {
        Catalog(checks);
        await AnthropicRequestShape(checks);
        await AnthropicStreamAndReplay(checks);
        await AnthropicRecoveries(checks);
        await AnthropicNonStreamingAndErrors(checks);
        await ResponsesRequestShape(checks);
        await ResponsesStreamAndReplay(checks);
        await ResponsesRecoveriesAndFailures(checks);
        await EngineRoundTrip(checks);
    }

    // ───────────────────────── 夹具 ─────────────────────────

    private sealed class Scripted : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();
        public List<(string Url, Dictionary<string, string> Headers, JsonDocument Body)> Requests { get; } = new();

        public Scripted Then(int status, string body, string contentType = "application/json")
        {
            _responses.Enqueue(() => new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent(body, Encoding.UTF8, contentType) });
            return this;
        }

        public Scripted ThenSse(params string[] frames) =>
            Then(200, string.Concat(frames.Select(frame => "data: " + frame + "\n\n")), "text/event-stream");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
            var text = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.ToString(), headers, JsonDocument.Parse(text)));
            if (_responses.Count == 0) throw new InvalidOperationException("没有更多脚本化响应。");
            return _responses.Dequeue()();
        }
    }

    private static PureModelRequest Request(IReadOnlyList<PureAgentMessage> messages, string model,
        string? effort = "high", params string[] tools) =>
        new(messages, tools, model, ReasoningEffort: effort);

    private static readonly PureAgentMessage[] Conversation =
    [
        new(PureAgentRole.System, "你是 FUMO。"),
        new(PureAgentRole.User, "读两个文件"),
        new(PureAgentRole.Assistant, "好的", ToolCalls:
        [
            new PureToolCall("toolu_1", "read_file", "{\"path\":\"a.txt\"}"),
            new PureToolCall("toolu_2", "read_file", "{\"path\":\"b.txt\"}"),
        ]),
        new(PureAgentRole.Tool, "AAA", ToolCallId: "toolu_1"),
        new(PureAgentRole.Tool, "BBB", ToolCallId: "toolu_2"),
        new(PureAgentRole.User, "继续"),
    ];

    private static void Catalog(Checks checks)
    {
        checks.Equal(ModelApi.OpenAiCompletions, ModelApi.Normalize(null), "未设置协议时按 Chat Completions，旧配置不变");
        checks.Check(ModelApi.CreateClient("anthropic-messages", "https://api.anthropic.com", "k", "m") is AnthropicMessagesModelClient &&
                     ModelApi.CreateClient("openai-responses", "https://api.openai.com/v1", "k", "m") is OpenAiResponsesModelClient &&
                     ModelApi.CreateClient(null, "https://x/v1", "k", "m") is OpenAiCompatibleModelClient,
            "协议工厂按配置创建对应原生客户端");
        checks.Throws<ArgumentException>(() => ModelApi.CreateClient("grpc", "https://x", null, "m"), "未知协议直接报错，不退回 Chat Completions");

        var root = Path.Combine(Path.GetTempPath(), "fufucode-api-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AppSettingsStore(new AppDataLayout(root));
            store.Update(s => s with { Endpoint = new CompatibleEndpoint("anthropic", "https://api.anthropic.com/v1", "claude-opus-5-5", "ANTHROPIC_API_KEY", Api: ModelApi.AnthropicMessages) });
            checks.Equal(ModelApi.AnthropicMessages, new AppSettingsStore(new AppDataLayout(root)).Current.Endpoint?.Api, "端点协议写盘后可读回");
            checks.Check(store.BuildPiModelsJson()!.Contains("\"api\":\"anthropic-messages\""), "导出给 Pi 的 models.json 使用同名协议");
            checks.Throws<ArgumentException>(() => store.Update(s => s with { Endpoint = s.Endpoint! with { Api = "bogus" } }), "设置拒绝未知协议");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }

        checks.Equal("https://api.anthropic.com/v1/messages", new AnthropicMessagesModelClient("https://api.anthropic.com").Endpoint, "Anthropic 根地址补全 /v1/messages");
        checks.Equal("https://gw.example/v1/messages", new AnthropicMessagesModelClient("https://gw.example/v1/").Endpoint, "Anthropic …/v1 地址补全 /messages");
        checks.Equal("https://api.openai.com/v1/responses", new OpenAiResponsesModelClient("https://api.openai.com/v1").Endpoint, "Responses 地址补全 /responses");
    }

    // ───────────────────────── Anthropic ─────────────────────────

    private const string AnthropicFinal =
        """{"type":"message_start","message":{"model":"claude-opus-5-5","usage":{"input_tokens":5,"output_tokens":1}}}""";

    private static string[] AnthropicText(string text) =>
    [
        AnthropicFinal,
        """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
        "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + JsonSerializer.Serialize(text) + "}}",
        """{"type":"content_block_stop","index":0}""",
        """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}""",
        """{"type":"message_stop"}""",
    ];

    private static async Task AnthropicRequestShape(Checks checks)
    {
        var handler = new Scripted().ThenSse(AnthropicText("ok")).ThenSse(AnthropicText("ok"));
        using var client = new AnthropicMessagesModelClient("https://api.anthropic.com", "sk-test", "claude-opus-5-5", new HttpClient(handler));
        await client.GenerateStreamingAsync(Request(Conversation, "claude-opus-5-5", "max", "read_file"));
        var (url, headers, body) = handler.Requests[0];
        var root = body.RootElement;
        checks.Check(url == "https://api.anthropic.com/v1/messages" && headers.GetValueOrDefault("x-api-key") == "sk-test" &&
                     headers.GetValueOrDefault("anthropic-version") == "2023-06-01" && !headers.ContainsKey("authorization"),
            "官方端点用 x-api-key + anthropic-version，不发 Bearer");
        checks.Check(root.GetProperty("system").GetString() == "你是 FUMO。" &&
                     root.GetProperty("messages").EnumerateArray().All(m => m.GetProperty("role").GetString() is "user" or "assistant"),
            "系统提示进顶层 system，messages 只有 user/assistant");
        var messages = root.GetProperty("messages").EnumerateArray().ToArray();
        checks.Equal(3, messages.Length, "消息按角色交替合并");
        var toolTurn = messages[2].GetProperty("content").EnumerateArray().ToArray();
        checks.Check(toolTurn.Length == 3 && toolTurn[0].GetProperty("type").GetString() == "tool_result" &&
                     toolTurn[1].GetProperty("tool_use_id").GetString() == "toolu_2" && toolTurn[2].GetProperty("text").GetString() == "继续",
            "并行工具结果合并进同一条 user 消息，结果在前、正文在后");
        var assistant = messages[1].GetProperty("content").EnumerateArray().ToArray();
        checks.Check(assistant[1].GetProperty("type").GetString() == "tool_use" &&
                     assistant[1].GetProperty("input").GetProperty("path").GetString() == "a.txt",
            "工具调用以 tool_use 回放，input 是对象而不是 JSON 字符串");
        checks.Check(root.GetProperty("thinking").GetProperty("type").GetString() == "adaptive" &&
                     root.GetProperty("thinking").GetProperty("display").GetString() == "summarized" &&
                     root.GetProperty("output_config").GetProperty("effort").GetString() == "max",
            "Claude 5.x 用自适应思考 + output_config.effort，显示摘要");
        checks.Check(!root.TryGetProperty("temperature", out _) && root.GetProperty("cache_control").GetProperty("type").GetString() == "ephemeral",
            "不发送 temperature，开启顶层提示缓存");
        var tool = root.GetProperty("tools")[0];
        checks.Check(tool.GetProperty("name").GetString() == "read_file" && tool.GetProperty("input_schema").GetProperty("type").GetString() == "object" &&
                     tool.GetProperty("eager_input_streaming").GetBoolean() && root.GetProperty("max_tokens").GetInt32() == 64000,
            "工具声明 input_schema；官方端点流式开启 eager_input_streaming；max_tokens 64000");

        using var legacy = new AnthropicMessagesModelClient("https://gw.example/v1", "sk-gw", "claude-sonnet-4-5", new HttpClient(handler));
        await legacy.GenerateStreamingAsync(Request(Conversation, "claude-sonnet-4-5", "low", "read_file"));
        var legacyRequest = handler.Requests[1];
        var legacyRoot = legacyRequest.Body.RootElement;
        checks.Check(legacyRoot.GetProperty("thinking").GetProperty("type").GetString() == "enabled" &&
                     legacyRoot.GetProperty("thinking").GetProperty("budget_tokens").GetInt32() == 4096 &&
                     !legacyRoot.TryGetProperty("output_config", out _),
            "Claude 4.5 及更早用 budget_tokens，不发 effort");
        checks.Check(legacyRequest.Headers.GetValueOrDefault("authorization") == "Bearer sk-gw" &&
                     !legacyRoot.GetProperty("tools")[0].TryGetProperty("eager_input_streaming", out _),
            "第三方网关同时带 Bearer，且不发官方专属字段");
    }

    private static readonly string[] AnthropicToolStream =
    [
        """{"type":"message_start","message":{"model":"claude-opus-5-5","usage":{"input_tokens":10,"cache_read_input_tokens":90,"cache_creation_input_tokens":20,"output_tokens":1}}}""",
        """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"先读"}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"文件"}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"SIG-abc"}}""",
        """{"type":"content_block_stop","index":0}""",
        """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""",
        """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"我来读。"}}""",
        """{"type":"content_block_stop","index":1}""",
        """{"type":"ping"}""",
        """{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_9","name":"read_file","input":{}}}""",
        """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"pa"}}""",
        """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"th\":\"x.txt\"}"}}""",
        """{"type":"content_block_stop","index":2}""",
        """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":42}}""",
        """{"type":"message_stop"}""",
    ];

    private static async Task AnthropicStreamAndReplay(Checks checks)
    {
        var handler = new Scripted().ThenSse(AnthropicToolStream).ThenSse(AnthropicText("done"));
        using var client = new AnthropicMessagesModelClient("https://api.anthropic.com", "k", "claude-opus-5-5", new HttpClient(handler));
        var thinking = new StringBuilder();
        var text = new StringBuilder();
        var fragments = 0;
        var response = await client.GenerateStreamingAsync(Request([new(PureAgentRole.User, "读 x")], "claude-opus-5-5", "high", "read_file"),
            new ModelStreamCallbacks
            {
                OnThinkingDelta = d => thinking.Append(d),
                OnTextDelta = d => text.Append(d),
                OnToolCallChunkIndexed = (_, _, _, _) => fragments++,
            });
        checks.Check(thinking.ToString() == "先读文件" && text.ToString() == "我来读。" && response.ReasoningContent == "先读文件",
            "思考与正文增量分别派发");
        checks.Check(response.ToolCalls.Single() is { Id: "toolu_9", Name: "read_file", ArgumentsJson: "{\"path\":\"x.txt\"}" } && fragments == 3,
            "tool_use 参数按 input_json_delta 分片拼接");
        checks.Check(response.FinishReason == "tool_calls" && response.Usage is { InputTokens: 120, OutputTokens: 42, CacheReadTokens: 90, CacheWriteTokens: 20 },
            "stop_reason 映射为 tool_calls；输入用量含缓存读写");
        checks.Check(response.ProviderState is { } state && state.Contains("SIG-abc") && state.Contains("\"anthropic-messages\""),
            "签名思考块进入 ProviderState");

        var history = new List<PureAgentMessage>
        {
            new(PureAgentRole.User, "读 x"),
            new(PureAgentRole.Assistant, response.Text ?? "", ToolCalls: response.ToolCalls) { ProviderState = response.ProviderState },
            new(PureAgentRole.Tool, "XXX", ToolCallId: "toolu_9"),
        };
        await client.GenerateStreamingAsync(Request(history, "claude-opus-5-5", "high", "read_file"));
        var replayed = handler.Requests[1].Body.RootElement.GetProperty("messages")[1].GetProperty("content").EnumerateArray().ToArray();
        checks.Check(replayed.Length == 3 && replayed[0].GetProperty("type").GetString() == "thinking" &&
                     replayed[0].GetProperty("signature").GetString() == "SIG-abc" && replayed[0].GetProperty("thinking").GetString() == "先读文件" &&
                     replayed[2].GetProperty("input").GetProperty("path").GetString() == "x.txt",
            "下一轮按原顺序逐字节回放签名思考块、正文与工具调用");

        var edited = history[1] with { Content = "被压缩改写的正文" };
        handler.ThenSse(AnthropicText("ok"));
        await client.GenerateStreamingAsync(Request([history[0], edited, history[2]], "claude-opus-5-5", "high", "read_file"));
        var rebuilt = handler.Requests[2].Body.RootElement.GetProperty("messages")[1].GetProperty("content").EnumerateArray().ToArray();
        checks.Check(rebuilt.All(block => block.GetProperty("type").GetString() != "thinking"),
            "消息被改写后不再回放旧思考块（签名已不匹配）");
    }

    private static async Task AnthropicRecoveries(Checks checks)
    {
        var history = new List<PureAgentMessage>
        {
            new(PureAgentRole.User, "hi"),
            new(PureAgentRole.Assistant, "我来读。", ToolCalls: [new PureToolCall("toolu_9", "read_file", "{\"path\":\"x.txt\"}")])
            {
                ProviderState = """{"api":"anthropic-messages","content":[{"type":"thinking","thinking":"t","signature":"S"},{"type":"text","text":"我来读。"},{"type":"tool_use","id":"toolu_9","name":"read_file","input":{"path":"x.txt"}}]}""",
            },
            new(PureAgentRole.Tool, "X", ToolCallId: "toolu_9"),
        };
        const string signatureError = """{"type":"error","error":{"type":"invalid_request_error","message":"messages.1.content.0: Invalid `signature` in `thinking` block. The block is bound to a different conversation."}}""";
        var handler = new Scripted()
            .Then(400, signatureError)
            .ThenSse(AnthropicText("ok"));
        using var client = new AnthropicMessagesModelClient("https://api.anthropic.com", "k", "claude-opus-5-5", new HttpClient(handler)) { MaxAttempts = 1 };
        var result = await client.GenerateStreamingAsync(Request(history, "claude-opus-5-5"));
        var second = handler.Requests[1];
        checks.Check(result.Text == "ok" && second.Headers.GetValueOrDefault("anthropic-beta") == "thinking-binding-controls-2026-08-01" &&
                     second.Body.RootElement.GetProperty("thinking").GetProperty("block_binding").GetProperty("prefix_mismatch_behavior").GetString() == "drop_block",
            "思考签名失效：改用 drop_block 让服务端丢弃失效块后重发");

        var gateway = new Scripted()
            .Then(400, signatureError)
            .Then(400, """{"type":"error","error":{"type":"invalid_request_error","message":"Unexpected value(s) for the `anthropic-beta` header"}}""")
            .ThenSse(AnthropicText("ok"));
        using var gatewayClient = new AnthropicMessagesModelClient("https://gw.example/v1", "k", "claude-opus-5-5", new HttpClient(gateway)) { MaxAttempts = 1 };
        await gatewayClient.GenerateStreamingAsync(Request(history, "claude-opus-5-5"));
        var stripped = gateway.Requests[2].Body.RootElement;
        checks.Check(!gateway.Requests[2].Headers.ContainsKey("anthropic-beta") &&
                     stripped.GetProperty("messages")[1].GetProperty("content").EnumerateArray().All(b => b.GetProperty("type").GetString() != "thinking"),
            "网关不认 beta：剥离回放的思考块后重发");

        var limits = new Scripted()
            .Then(400, """{"type":"error","error":{"type":"invalid_request_error","message":"max_tokens: 64000 > 32000, which is the maximum allowed number of output tokens for claude-x"}}""")
            .Then(400, """{"type":"error","error":{"type":"invalid_request_error","message":"cache_control: Extra inputs are not permitted"}}""")
            .ThenSse(AnthropicText("ok"));
        using var limited = new AnthropicMessagesModelClient("https://gw.example", "k", "claude-x", new HttpClient(limits)) { MaxAttempts = 1 };
        await limited.GenerateStreamingAsync(Request([new(PureAgentRole.User, "hi")], "claude-x", null));
        var final = limits.Requests[2].Body.RootElement;
        checks.Check(final.GetProperty("max_tokens").GetInt32() == 32000 && !final.TryGetProperty("cache_control", out _) &&
                     !final.TryGetProperty("thinking", out _),
            "按错误给出的上限收紧 max_tokens；网关不认 cache_control 时去掉；思考关闭时不发 thinking");

        var reject = new Scripted().Then(400, """{"type":"error","error":{"type":"invalid_request_error","message":"messages: roles must alternate"}}""");
        using var rejecting = new AnthropicMessagesModelClient("https://gw.example", "k", "m", new HttpClient(reject)) { MaxAttempts = 1 };
        await checks.ThrowsAsync<ModelClientException>(() => rejecting.GenerateStreamingAsync(Request([new(PureAgentRole.User, "hi")], "m")),
            "与可选字段无关的 400 不重发，如实失败");
        checks.Equal(1, reject.Requests.Count, "不可恢复的 400 只请求一次");
    }

    private static async Task AnthropicNonStreamingAndErrors(Checks checks)
    {
        var handler = new Scripted().Then(200, """
            {"type":"message","model":"claude-haiku-5-5","stop_reason":"tool_use",
             "content":[{"type":"text","text":"看看"},{"type":"tool_use","id":"t1","name":"list_dir","input":{"path":"."}}],
             "usage":{"input_tokens":7,"output_tokens":3}}
            """);
        using var client = new AnthropicMessagesModelClient("https://api.anthropic.com", "k", "claude-haiku-5-5", new HttpClient(handler));
        var response = await client.GenerateAsync(Request([new(PureAgentRole.User, "hi")], "claude-haiku-5-5", null, "list_dir"));
        checks.Check(response.Text == "看看" && response.ToolCalls.Single().ArgumentsJson == "{\"path\":\".\"}" &&
                     response.FinishReason == "tool_calls" && handler.Requests[0].Body.RootElement.GetProperty("max_tokens").GetInt32() == 16000,
            "一次性响应解析正文与工具调用；非流式 max_tokens 16000");

        var overloaded = new Scripted()
            .ThenSse(AnthropicFinal, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""")
            .ThenSse(AnthropicText("恢复"));
        var restarts = 0;
        using var retrying = new AnthropicMessagesModelClient("https://api.anthropic.com", "k", "m", new HttpClient(overloaded)) { MaxAttempts = 2 };
        var retried = await retrying.GenerateStreamingAsync(Request([new(PureAgentRole.User, "hi")], "m", null),
            new ModelStreamCallbacks { OnRestart = () => restarts++ });
        checks.Check(retried.Text == "恢复" && overloaded.Requests.Count == 2, "流内 overloaded_error 按可重试处理");

        var cut = new Scripted().ThenSse(AnthropicToolStream.Take(12).ToArray());
        using var cutClient = new AnthropicMessagesModelClient("https://api.anthropic.com", "k", "m", new HttpClient(cut)) { MaxAttempts = 1 };
        await checks.ThrowsAsync<ModelClientException>(() => cutClient.GenerateStreamingAsync(Request([new(PureAgentRole.User, "hi")], "m")),
            "流在 message_stop 前断开时报错，不执行半截工具调用");

        var truncated = new Scripted().ThenSse(AnthropicToolStream.Take(12)
            .Append("""{"type":"message_delta","delta":{"stop_reason":"max_tokens"},"usage":{"output_tokens":9}}""")
            .Append("""{"type":"message_stop"}""").ToArray());
        using var truncatedClient = new AnthropicMessagesModelClient("https://api.anthropic.com", "k", "m", new HttpClient(truncated)) { MaxAttempts = 1 };
        await checks.ThrowsAsync<ModelClientException>(() => truncatedClient.GenerateStreamingAsync(Request([new(PureAgentRole.User, "hi")], "m")),
            "max_tokens 截断了工具参数时报错，不执行");
    }

    // ───────────────────────── OpenAI Responses ─────────────────────────

    private static string[] ResponsesText(string text) =>
    [
        """{"type":"response.created","response":{"model":"gpt-5.5"}}""",
        "{\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":" + JsonSerializer.Serialize(text) + "}",
        """{"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":4,"output_tokens":2,"total_tokens":6}}}""",
    ];

    private static async Task ResponsesRequestShape(Checks checks)
    {
        var handler = new Scripted().ThenSse(ResponsesText("ok"));
        using var client = new OpenAiResponsesModelClient("https://api.openai.com/v1", "sk-o", "gpt-5.5", new HttpClient(handler));
        await client.GenerateStreamingAsync(Request(Conversation, "gpt-5.5", "max", "read_file"));
        var (url, headers, body) = handler.Requests[0];
        var root = body.RootElement;
        checks.Check(url == "https://api.openai.com/v1/responses" && headers.GetValueOrDefault("authorization") == "Bearer sk-o",
            "Responses 请求 /responses，Bearer 鉴权");
        checks.Check(root.GetProperty("instructions").GetString() == "你是 FUMO。" && root.GetProperty("store").GetBoolean() == false &&
                     root.GetProperty("include")[0].GetString() == "reasoning.encrypted_content",
            "系统提示进 instructions；store=false 无状态并索取加密推理");
        var input = root.GetProperty("input").EnumerateArray().ToArray();
        checks.Check(input[0].GetProperty("content")[0].GetProperty("type").GetString() == "input_text" &&
                     input[2].GetProperty("type").GetString() == "function_call" && input[2].GetProperty("call_id").GetString() == "toolu_1" &&
                     input[2].GetProperty("arguments").GetString() == "{\"path\":\"a.txt\"}" &&
                     input[4].GetProperty("type").GetString() == "function_call_output" && input[5].GetProperty("output").GetString() == "BBB",
            "历史转为 input 项：function_call 与 function_call_output 按 call_id 对应");
        var tool = root.GetProperty("tools")[0];
        checks.Check(tool.GetProperty("type").GetString() == "function" && tool.GetProperty("name").GetString() == "read_file" &&
                     tool.GetProperty("strict").GetBoolean() == false && tool.GetProperty("parameters").GetProperty("type").GetString() == "object",
            "函数工具扁平声明且显式 strict=false");
        checks.Check(root.GetProperty("reasoning").GetProperty("effort").GetString() == "xhigh" &&
                     root.GetProperty("reasoning").GetProperty("summary").GetString() == "auto",
            "max 档映射为 xhigh，并请求推理摘要");
    }

    private static readonly string[] ResponsesToolStream =
    [
        """{"type":"response.created","response":{"model":"gpt-5.5"}}""",
        """{"type":"response.output_item.added","output_index":0,"item":{"type":"reasoning","id":"rs_1"}}""",
        """{"type":"response.reasoning_summary_text.delta","output_index":0,"summary_index":0,"delta":"计划"}""",
        """{"type":"response.reasoning_summary_text.delta","output_index":0,"summary_index":1,"delta":"第二段"}""",
        """{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"rs_1","status":"completed","summary":[{"type":"summary_text","text":"计划"}],"encrypted_content":"ENC-xyz"}}""",
        """{"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","id":"fc_1","call_id":"call_A","name":"read_file","arguments":""}}""",
        """{"type":"response.function_call_arguments.delta","output_index":1,"item_id":"fc_1","delta":"{\"path\":"}""",
        """{"type":"response.function_call_arguments.delta","output_index":1,"item_id":"fc_1","delta":"\"y.txt\"}"}""",
        """{"type":"response.output_item.done","output_index":1,"item":{"type":"function_call","id":"fc_1","call_id":"call_A","name":"read_file","arguments":"{\"path\":\"y.txt\"}"}}""",
        """{"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":100,"input_tokens_details":{"cached_tokens":64},"output_tokens":30,"total_tokens":130}}}""",
    ];

    private static async Task ResponsesStreamAndReplay(Checks checks)
    {
        var handler = new Scripted().ThenSse(ResponsesToolStream).ThenSse(ResponsesText("done"));
        using var client = new OpenAiResponsesModelClient("https://api.openai.com/v1", "k", "gpt-5.5", new HttpClient(handler));
        var thinking = new StringBuilder();
        var response = await client.GenerateStreamingAsync(Request([new(PureAgentRole.User, "读 y")], "gpt-5.5", "high", "read_file"),
            new ModelStreamCallbacks { OnThinkingDelta = d => thinking.Append(d) });
        checks.Check(thinking.ToString() == "计划\n\n第二段", "推理摘要流入思考区，多段之间空行分隔");
        checks.Check(response.ToolCalls.Single() is { Id: "call_A", Name: "read_file", ArgumentsJson: "{\"path\":\"y.txt\"}" } &&
                     response.FinishReason == "tool_calls", "函数调用按 call_id 聚合参数增量，完成项不重复拼接");
        checks.Check(response.Usage is { InputTokens: 100, OutputTokens: 30, CacheReadTokens: 64 }, "用量含缓存命中");
        checks.Check(response.ProviderState is { } state && state.Contains("ENC-xyz") && !state.Contains("\"status\""),
            "加密推理项进入 ProviderState（去掉只读的 status）");

        var history = new List<PureAgentMessage>
        {
            new(PureAgentRole.User, "读 y"),
            new(PureAgentRole.Assistant, "", ToolCalls: response.ToolCalls) { ProviderState = response.ProviderState },
            new(PureAgentRole.Tool, "YYY", ToolCallId: "call_A"),
        };
        await client.GenerateStreamingAsync(Request(history, "gpt-5.5", "high", "read_file"));
        var input = handler.Requests[1].Body.RootElement.GetProperty("input").EnumerateArray().ToArray();
        checks.Check(input.Length == 4 && input[1].GetProperty("type").GetString() == "reasoning" &&
                     input[1].GetProperty("encrypted_content").GetString() == "ENC-xyz" &&
                     input[2].GetProperty("type").GetString() == "function_call" && input[3].GetProperty("call_id").GetString() == "call_A",
            "下一轮先回传推理项，再回传函数调用与结果");
    }

    private static async Task ResponsesRecoveriesAndFailures(Checks checks)
    {
        var handler = new Scripted()
            .Then(400, """{"error":{"message":"Unsupported value: 'xhigh' is not supported with this model.","type":"invalid_request_error","param":"reasoning.effort"}}""")
            .ThenSse(ResponsesText("ok"));
        using var client = new OpenAiResponsesModelClient("https://api.openai.com/v1", "k", "gpt-5", new HttpClient(handler)) { MaxAttempts = 1 };
        await client.GenerateStreamingAsync(Request([new(PureAgentRole.User, "hi")], "gpt-5", "max"));
        checks.Equal("high", handler.Requests[1].Body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString(),
            "模型不支持 xhigh 时降到 high 重发");

        var noReasoning = new Scripted()
            .Then(400, """{"error":{"message":"Encrypted content is not supported with this model.","param":"include"}}""")
            .Then(400, """{"error":{"message":"Unsupported parameter: 'reasoning.effort' is not supported with this model."}}""")
            .ThenSse(ResponsesText("ok"));
        using var plain = new OpenAiResponsesModelClient("https://api.openai.com/v1", "k", "gpt-4.1", new HttpClient(noReasoning)) { MaxAttempts = 1 };
        await plain.GenerateStreamingAsync(Request([new(PureAgentRole.User, "hi")], "gpt-4.1", "high"));
        var final = noReasoning.Requests[2].Body.RootElement;
        checks.Check(!final.TryGetProperty("include", out _) && !final.TryGetProperty("reasoning", out _) && final.GetProperty("store").GetBoolean() == false,
            "非推理模型拒绝 include / reasoning 时去掉后重发，仍保持 store=false");

        var failed = new Scripted().ThenSse(
            """{"type":"response.created","response":{"model":"gpt-5.5"}}""",
            """{"type":"response.failed","response":{"status":"failed","error":{"code":"invalid_prompt","message":"bad prompt"}}}""");
        using var failing = new OpenAiResponsesModelClient("https://api.openai.com/v1", "k", "gpt-5.5", new HttpClient(failed)) { MaxAttempts = 1 };
        ModelClientException? error = null;
        try { await failing.GenerateStreamingAsync(Request([new(PureAgentRole.User, "hi")], "gpt-5.5")); }
        catch (ModelClientException caught) { error = caught; }
        checks.Check(error?.Message.Contains("bad prompt") == true, "response.failed 以结构化错误抛出并保留服务端原因");

        var nonStreaming = new Scripted().Then(200, """
            {"id":"resp_1","model":"gpt-5.5","status":"completed",
             "output":[{"type":"reasoning","id":"rs_2","summary":[{"type":"summary_text","text":"想"}],"encrypted_content":"E2"},
                       {"type":"message","role":"assistant","content":[{"type":"output_text","text":"你好"}]}],
             "usage":{"input_tokens":3,"output_tokens":1,"total_tokens":4}}
            """);
        using var once = new OpenAiResponsesModelClient("https://api.openai.com/v1", "k", "gpt-5.5", new HttpClient(nonStreaming));
        var single = await once.GenerateAsync(Request([new(PureAgentRole.User, "hi")], "gpt-5.5"));
        checks.Check(single.Text == "你好" && single.ReasoningContent == "想" && single.FinishReason == "stop" &&
                     single.ProviderState!.Contains("E2"), "一次性 Responses 响应解析正文、推理摘要与加密推理");
    }

    // ───────────────────────── 完整回合 ─────────────────────────

    private static async Task EngineRoundTrip(Checks checks)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "fufucode-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "x.txt"), "FILE-CONTENT-7");
            var handler = new Scripted().ThenSse(AnthropicToolStream).ThenSse(AnthropicText("内容是 FILE-CONTENT-7"));
            var client = new AnthropicMessagesModelClient("https://api.anthropic.com", "k", "claude-opus-5-5", new HttpClient(handler));
            var engine = new PureCSharpAgentEngine(new ProductionToolDispatcher(workspace), client);
            var result = await engine.RunTurnAsync(new PureModelRequest(
                [new PureAgentMessage(PureAgentRole.User, "读 x.txt")], ["read_file"], "claude-opus-5-5", ReasoningEffort: "high"));
            checks.Check(result.Response.Text?.Contains("FILE-CONTENT-7") == true, "FUMO 引擎经 Anthropic 协议完成一次真实工具回合");
            var messages = handler.Requests[1].Body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            var assistant = messages[1].GetProperty("content").EnumerateArray().ToArray();
            var toolResult = messages[2].GetProperty("content")[0];
            checks.Check(assistant[0].GetProperty("signature").GetString() == "SIG-abc" &&
                         toolResult.GetProperty("type").GetString() == "tool_result" &&
                         toolResult.GetProperty("content").GetString()!.Contains("FILE-CONTENT-7"),
                "引擎历史保留 ProviderState：工具续轮回放签名思考块并附带真实工具结果");
            var saved = engine.GetState().History!.Single(m => m.Role == PureAgentRole.Assistant && m.ToolCalls is { Count: > 0 });
            var json = JsonSerializer.Serialize(saved);
            checks.Check(json.Contains("ProviderState") && JsonSerializer.Deserialize<PureAgentMessage>(json)!.ProviderState == saved.ProviderState,
                "ProviderState 随会话序列化往返");
        }
        finally { try { Directory.Delete(workspace, true); } catch (IOException) { } }
    }
}
