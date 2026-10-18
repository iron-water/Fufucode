using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Fufucode.Core.Backend;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// SSE 行协议解析：分帧、多行 data、注释/心跳、CRLF/LF/CR 行尾、UTF-8 跨分片。
/// </summary>
internal static class SseProtocolTests
{
    public static async Task RunAsync(Checks checks)
    {
        // 1. LF 分帧：两条事件 + DONE
        var lf = "data: {\"a\":1}\n\ndata: {\"b\":2}\n\ndata: [DONE]\n\n";
        var events = await CollectAsync(Encoding.UTF8.GetBytes(lf));
        checks.Equal(3, events.Count, "LF 分帧解析出 3 条事件");
        checks.Equal("{\"a\":1}", events[0].Data, "首条事件 data 正确");
        checks.Equal("{\"b\":2}", events[1].Data, "次条事件 data 正确");
        checks.Check(events[2].IsDone, "第三条事件识别为 [DONE]");

        // 2. CRLF 分帧（含最后一行只有 CRLF 的情况）
        var crlf = "data: {\"a\":1}\r\n\r\ndata: {\"b\":2}\r\n\r\ndata: [DONE]\r\n\r\n";
        var crlfEvents = await CollectAsync(Encoding.UTF8.GetBytes(crlf));
        checks.Equal(3, crlfEvents.Count, "CRLF 分帧解析出 3 条事件");
        checks.Equal("{\"a\":1}", crlfEvents[0].Data, "CRLF 下首条 data 不含多余回车");
        checks.Check(crlfEvents[2].IsDone, "CRLF 下识别 [DONE]");

        // 3. 多行 data 以 \n 连接（规范要求）
        var multi = "data: line1\ndata: line2\n\n";
        var multiEvents = await CollectAsync(Encoding.UTF8.GetBytes(multi));
        checks.Equal(1, multiEvents.Count, "多行 data 只派发一条事件");
        checks.Equal("line1\nline2", multiEvents[0].Data, "多行 data 以换行连接");

        // 4. 注释/心跳行被忽略且不劫持 data
        var comments = ": keep-alive\n\ndata: {\"a\":1}\n\n: ping\n\n";
        var commentEvents = await CollectAsync(Encoding.UTF8.GetBytes(comments));
        checks.Equal(1, commentEvents.Count, "纯注释块不派发事件");
        checks.Equal("{\"a\":1}", commentEvents[0].Data, "注释不会污染随后的 data");

        // 5. event/id 字段与 "data:" 无空格写法
        var fields = "event: message\nid: 7\ndata:{\"x\":1}\n\n";
        var fieldEvents = await CollectAsync(Encoding.UTF8.GetBytes(fields));
        checks.Equal(1, fieldEvents.Count, "event/id 字段事件派发一次");
        checks.Equal("message", fieldEvents[0].EventName, "event 字段被保留");
        checks.Equal("7", fieldEvents[0].Id, "id 字段被保留");
        checks.Equal("{\"x\":1}", fieldEvents[0].Data, "data: 无空格写法仍可解析");

        // 6. 任意字节切分：每 3 字节一片，覆盖行中断与多字节 UTF-8 截断
        var text = "data: {\"choices\":[{\"delta\":{\"content\":\"你好，原生内核\"}}]}\n\ndata: [DONE]\n\n";
        var splitEvents = await CollectAsync(Sse.Split(text, 3 + 1));
        checks.Equal(2, splitEvents.Count, "3 字节切分仍解析出 2 条事件");
        checks.Check(splitEvents[0].Data.Contains("你好，原生内核"), "UTF-8 跨字节分片未丢字或乱码");
        checks.Check(splitEvents[1].IsDone, "切分后仍识别 [DONE]");

        // 7. 流无结尾空行：已累积事件在流结束时仍被派发
        var noTrailer = "data: {\"a\":1}";
        var tailEvents = await CollectAsync(Encoding.UTF8.GetBytes(noTrailer));
        checks.Equal(1, tailEvents.Count, "无结尾空行时仍派发已累积事件");
        checks.Equal("{\"a\":1}", tailEvents[0].Data, "尾部事件 data 完整");

        // 8. 空流不产生事件
        var empty = await CollectAsync(Array.Empty<byte>());
        checks.Equal(0, empty.Count, "空流不派发事件");
    }

    private static async Task<List<ServerSentEvent>> CollectAsync(byte[] payload)
    {
        var chunks = payload.Length == 0 ? Array.Empty<byte[]>() : new[] { payload };
        return await CollectAsync(chunks);
    }

    private static async Task<List<ServerSentEvent>> CollectAsync(byte[][] chunks)
    {
        await using var stream = new ScriptedByteStream(chunks);
        var list = new List<ServerSentEvent>();
        await foreach (var sse in ServerSentEvents.ReadAsync(stream))
        {
            list.Add(sse);
        }
        return list;
    }
}

/// <summary>
/// OpenAI 兼容客户端的真实 SSE 消费：增量文本、工具调用分片拼装、错误映射。
/// 全部经由脚本化 HTTP 处理器回放真实报文，不发生任何网络请求。
/// </summary>
internal static class ModelClientStreamingTests
{
    private const string TextStream =
        "data: {\"id\":\"c1\",\"model\":\"deepseek-chat\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"原生\"}}]}\n\n" +
        "data: {\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\" SSE 内核\"}}]}\n\n" +
        "data: {\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
        Sse.Done;

    public static async Task RunAsync(Checks checks)
    {
        await TextDeltaStreamAsync(checks);
        await ToolCallStreamAsync(checks);
        await InterleavedTextAndToolStreamAsync(checks);
        await StreamErrorFrameAsync(checks);
        await HttpStatusMappingAsync(checks);
        await RequestBodyStreamFlagAsync(checks);
        await MissingDoneFallbackAsync(checks);
        await Base64ishMultibyteSplitAsync(checks);
    }

    private static async Task TextDeltaStreamAsync(Checks checks)
    {
        var handler = new ScriptedHttpMessageHandler((_, _) =>
            ScriptedHttpMessageHandler.SseResponse(Sse.Split(TextStream, 7)));
        var client = new OpenAiCompatibleModelClient("https://api.example.com/v1", "sk-test",
            defaultModelId: "sse-fixture-model", httpClient: new HttpClient(handler));

        var deltas = new List<string>();
        var completed = 0;
        var completion = await client.StreamAsync(
            new PureModelRequest(new[] { new PureAgentMessage(PureAgentRole.User, "你好") }, Array.Empty<string>()),
            new ModelStreamCallbacks
            {
                OnTextDelta = deltas.Add,
                OnCompleted = () => completed++,
            });

        checks.Equal(2, deltas.Count, "增量文本回调收到 2 个真实分片");
        checks.Equal("原生", deltas[0], "首个文本分片内容正确");
        checks.Equal(" SSE 内核", deltas[1], "第二个文本分片内容正确");
        checks.Equal("原生 SSE 内核", completion.Primary!.Text, "聚合文本等于各分片顺序拼接");
        checks.Equal("stop", completion.Primary!.FinishReason, "解析 finish_reason");
        checks.Check(completion.SawDone, "识别到 data: [DONE]");
        checks.Equal("deepseek-chat", completion.Model, "记录响应 model 字段");
        checks.Equal(1, completed, "流结束回调触发一次");

        // 一次性 API 也必须能用流式路径的结果
        var response = await client.GenerateStreamingAsync(
            new PureModelRequest(new[] { new PureAgentMessage(PureAgentRole.User, "你好") }, Array.Empty<string>()));
        checks.Equal("原生 SSE 内核", response.Text, "GenerateStreamingAsync 返回聚合文本");
        checks.Equal(0, response.ToolCalls.Count, "纯文本流不产生工具调用");
    }

    private static async Task ToolCallStreamAsync(Checks checks)
    {
        // 单个工具调用被切成语义上不同的 4 个分片：id/name 只在首片，arguments 逐片追加且被网络切成半段 JSON。
        const string frames =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_abc\",\"type\":\"function\",\"function\":{\"name\":\"read_file\",\"arguments\":\"\"}}]},\"finish_reason\":null}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"pa\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"th\\\":\\\"README\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\".md\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n" +
            Sse.Done;

        var handler = new ScriptedHttpMessageHandler((_, _) =>
            ScriptedHttpMessageHandler.SseResponse(Sse.Split(frames, 5)));
        var client = new OpenAiCompatibleModelClient("https://api.example.com/v1/chat/completions", "sk-test",
            defaultModelId: "sse-fixture-model", httpClient: new HttpClient(handler));

        var conformance = new List<string>();
        var indexed = new List<string>();
        var completion = await client.StreamAsync(
            new PureModelRequest(new[] { new PureAgentMessage(PureAgentRole.User, "读文件") },
                new[] { "read_file" }),
            new ModelStreamCallbacks
            {
                OnToolCallChunk = (id, name, args) => conformance.Add($"{id}|{name}|{args}"),
                OnToolCallChunkIndexed = (index, id, name, args) =>
                    indexed.Add($"{index}|{id ?? ""}|{name ?? ""}|{args}"),
            });

        checks.Equal(4, conformance.Count, "工具调用回调收到 4 个分片（与报文一致）");
        checks.Equal("call_abc|read_file|", conformance[0], "首片携带 id 与 name");
        checks.Equal("||{\"pa", conformance[1], "后续分片 id/name 为空、arguments 为增量");
        checks.Equal("||.md\"}", conformance[3], "末片 arguments 为尾部片段");

        var primary = completion.Primary!;
        checks.Equal(1, primary.ToolCalls.Count, "4 个分片聚合为 1 个工具调用");
        var call = primary.ToolCalls[0];
        checks.Equal("call_abc", call.Id, "聚合后保留工具调用 id");
        checks.Equal("read_file", call.Name, "聚合后保留工具名");
        checks.Equal("{\"path\":\"README.md\"}", call.ArgumentsJson, "分片 arguments 顺序拼接为完整合法 JSON");
        checks.Equal("tool_calls", primary.FinishReason, "finish_reason=tool_calls 被解析");
        checks.Equal(4, indexed.Count, "带索引回调同样收到 4 个分片");
        checks.Equal("0|call_abc|read_file|", indexed[0], "索引回调首片携带 index=0");

        // 参数拼接结果必须能被真实工具调度器解析
        using var doc = System.Text.Json.JsonDocument.Parse(call.ArgumentsJson);
        checks.Equal("README.md", doc.RootElement.GetProperty("path").GetString(), "拼接后的 JSON 可被真实解析");

        // 端到端：把流式聚合出的工具调用交给真实调度器执行
        var workspace = Path.Combine(Path.GetTempPath(), "cs2-sse-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "streamed-tool-content");
            var dispatcher = new ProductionToolDispatcher(workspace);
            var result = await dispatcher.DispatchAsync(new PureToolCall(call.Id, call.Name, call.ArgumentsJson));
            checks.Check(result.Success, "流式拼装出的工具调用被真实调度器成功执行");
            checks.Equal("streamed-tool-content", result.Content, "工具读取到的内容来自流式拼装的参数");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static async Task InterleavedTextAndToolStreamAsync(Checks checks)
    {
        const string frames =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"我先读文件：\"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c9\",\"function\":{\"name\":\"list_dir\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{}\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"c10\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"a.txt\\\"}\"}}]}}]}\n\n" +
            Sse.Done;

        var handler = new ScriptedHttpMessageHandler((_, _) =>
            ScriptedHttpMessageHandler.SseResponse(Sse.Split(frames, 11)));
        var client = new OpenAiCompatibleModelClient("https://api.example.com/v1", "sk-test",
            defaultModelId: "sse-fixture-model", httpClient: new HttpClient(handler));

        var textSeen = new StringBuilder();
        var completion = await client.StreamAsync(
            new PureModelRequest(new[] { new PureAgentMessage(PureAgentRole.User, "看看") }, Array.Empty<string>()),
            new ModelStreamCallbacks { OnTextDelta = d => textSeen.Append(d) });

        var primary = completion.Primary!;
        checks.Equal("我先读文件：", textSeen.ToString(), "文本与工具分片交错时文本仍完整");
        checks.Equal(2, primary.ToolCalls.Count, "两个不同 index 的工具调用分别聚合");
        checks.Equal("list_dir", primary.ToolCalls[0].Name, "下标 0 的工具名为 list_dir");
        checks.Equal("read_file", primary.ToolCalls[1].Name, "下标 1 的工具名为 read_file");
        checks.Equal("{\"path\":\"a.txt\"}", primary.ToolCalls[1].ArgumentsJson, "下标 1 的参数完整");
        checks.Equal("{}", primary.ToolCalls[0].ArgumentsJson, "空参数回落为 {} 而不是空串");
        checks.Equal(3, completion.Primary!.ToolCallChunkCount, "累计分片数为 3");
    }

    private static async Task StreamErrorFrameAsync(Checks checks)
    {
        // 先给一段正常增量，再在流中抛出标准 error 对象：必须中断而不是当成正常结束。
        const string frames =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"}}]}\n\n" +
            "data: {\"error\":{\"message\":\"rate limit exceeded\",\"type\":\"rate_limit_error\",\"code\":\"rate_limit_exceeded\"}}\n\n" +
            Sse.Done;

        var handler = new ScriptedHttpMessageHandler((_, _) =>
            ScriptedHttpMessageHandler.SseResponse(Sse.Split(frames, 9)));
        var client = new OpenAiCompatibleModelClient("https://api.example.com/v1", "sk-test",
            defaultModelId: "sse-fixture-model", httpClient: new HttpClient(handler));

        var deltas = new List<string>();
        var completed = 0;
        var error = await checks.CapturesAsync<ModelClientException>(() => client.StreamAsync(
            new PureModelRequest(new[] { new PureAgentMessage(PureAgentRole.User, "触发错误") }, Array.Empty<string>()),
            new ModelStreamCallbacks
            {
                OnTextDelta = deltas.Add,
                OnCompleted = () => completed++,
            }));

        checks.Check(error is not null, "流内 error 对象抛出 ModelClientException");
        checks.Equal(ModelClientErrorKind.StreamError, error!.Kind, "流内错误归类为 StreamError");
        checks.Equal("rate_limit_exceeded", error.ErrorCode, "提取 error.code");
        checks.Check(error.Message.Contains("rate limit exceeded"), "提取 error.message 进入异常消息");
        checks.Equal(1, deltas.Count, "错误发生前已派发的分片仍然送达");
        checks.Equal(0, completed, "出错后不触发 OnCompleted");
    }

    private static async Task HttpStatusMappingAsync(Checks checks)
    {
        await AssertStatusAsync(checks, HttpStatusCode.Unauthorized,
            "{\"error\":{\"message\":\"Invalid API key\",\"type\":\"invalid_request_error\",\"code\":\"invalid_api_key\"}}",
            ModelClientErrorKind.Authentication, "invalid_api_key", 401, retryable: false,
            expectedMessage: "Invalid API key");

        await AssertStatusAsync(checks, HttpStatusCode.TooManyRequests,
            "{\"error\":{\"message\":\"Rate limit reached\",\"type\":\"requests\",\"code\":\"rate_limit_exceeded\"}}",
            ModelClientErrorKind.RateLimited, "rate_limit_exceeded", 429, retryable: true,
            expectedMessage: "Rate limit reached");

        await AssertStatusAsync(checks, HttpStatusCode.InternalServerError,
            "{\"error\":{\"message\":\"upstream exploded\",\"type\":\"server_error\"}}",
            ModelClientErrorKind.ServerError, null, 500, retryable: true,
            expectedMessage: "upstream exploded");

        await AssertStatusAsync(checks, HttpStatusCode.BadRequest,
            "{\"error\":{\"message\":\"model not found\"}}",
            ModelClientErrorKind.InvalidRequest, null, 400, retryable: false,
            expectedMessage: "model not found");

        // 非 JSON 错误体也必须给出可判定类别，而不是解析崩溃。
        // 这种情况下没有任何 error.message 可用，保留有限长度的原始错误体是唯一诊断来源（因此就不做「不含原始标签」断言）。
        await AssertStatusAsync(checks, HttpStatusCode.BadGateway,
            "<html>bad gateway</html>",
            ModelClientErrorKind.ServerError, null, 502, retryable: true);
    }

    private static async Task AssertStatusAsync(
        Checks checks,
        HttpStatusCode status,
        string body,
        ModelClientErrorKind expectedKind,
        string? expectedCode,
        int expectedStatusCode,
        bool retryable,
        string? expectedMessage = null)
    {
        var handler = new ScriptedHttpMessageHandler((_, _) =>
            ScriptedHttpMessageHandler.JsonError(status, body));
        var client = new OpenAiCompatibleModelClient("https://api.example.com/v1", "sk-test",
            defaultModelId: "sse-fixture-model", httpClient: new HttpClient(handler));

        var error = await checks.CapturesAsync<ModelClientException>(() => client.GenerateAsync(
            new PureModelRequest(new[] { new PureAgentMessage(PureAgentRole.User, "hi") }, Array.Empty<string>())));

        var label = $"HTTP {(int)status}";
        checks.Check(error is not null, $"{label} 抛出结构化异常");
        checks.Equal(expectedKind, error!.Kind, $"{label} 映射为 {expectedKind}");
        checks.Equal(expectedStatusCode, error.StatusCode, $"{label} 保留状态码");
        checks.Equal(expectedCode, error.ErrorCode, $"{label} 提取 error.code");
        checks.Equal(retryable, error.IsRetryable, $"{label} 可重试性判定");
        if (expectedMessage is not null)
        {
            checks.Check(error.Message.Contains(expectedMessage), $"{label} 错误消息取自 error.message");
        }
        else
        {
            checks.Check(error.Message.Contains("<html>bad gateway</html>"), $"{label} 非 JSON 错误体按原始内容保留诊断");
        }

        // 流式路径下同一状态码必须同样映射（不能只在非流式分支处理）
        var streamError = await checks.CapturesAsync<ModelClientException>(() => client.GenerateStreamingAsync(
            new PureModelRequest(new[] { new PureAgentMessage(PureAgentRole.User, "hi") }, Array.Empty<string>())));
        checks.Check(streamError is not null, $"{label} 流式路径同样抛出结构化异常");
        checks.Equal(expectedKind, streamError!.Kind, $"{label} 流式路径类别一致");
    }

    private static async Task RequestBodyStreamFlagAsync(Checks checks)
    {
        var handler = new ScriptedHttpMessageHandler((_, _) =>
            ScriptedHttpMessageHandler.SseResponse(Sse.Utf8(Sse.Done)));
        var client = new OpenAiCompatibleModelClient("https://gateway.example.com/openai/v1", "sk-test",
            defaultModelId: "sse-fixture-model", httpClient: new HttpClient(handler));

        await client.GenerateStreamingAsync(new PureModelRequest(
            new[] { new PureAgentMessage(PureAgentRole.User, "hi") },
            new[] { "read_file" },
            "deepseek-chat"));

        checks.Equal("https://gateway.example.com/openai/v1/chat/completions",
            handler.RequestUris[0].ToString(), "自动补全 chat/completions 端点");

        using var doc = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody);
        var root = doc.RootElement;
        checks.Check(root.GetProperty("stream").GetBoolean(), "流式请求体携带 stream=true");
        checks.Equal("deepseek-chat", root.GetProperty("model").GetString(), "请求体使用指定模型");
        checks.Equal("auto", root.GetProperty("tool_choice").GetString(), "提供工具时使用 tool_choice=auto");
        checks.Equal(1, root.GetProperty("tools").GetArrayLength(), "请求体携带工具 schema");

        // 非流式路径必须 stream=false，避免服务端误判
        var nonStreamHandler = new ScriptedHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}",
                Encoding.UTF8, "application/json"),
        });
        var nonStreamClient = new OpenAiCompatibleModelClient("https://gateway.example.com/openai/v1", "sk-test",
            defaultModelId: "sse-fixture-model", httpClient: new HttpClient(nonStreamHandler));
        var plain = await nonStreamClient.GenerateAsync(new PureModelRequest(
            new[] { new PureAgentMessage(PureAgentRole.User, "hi") }, Array.Empty<string>()));
        checks.Equal("ok", plain.Text, "非流式路径解析 choices[0].message.content");

        using var plainDoc = System.Text.Json.JsonDocument.Parse(nonStreamHandler.LastRequestBody);
        checks.Check(!plainDoc.RootElement.GetProperty("stream").GetBoolean(), "非流式请求体 stream=false");
    }

    private static async Task MissingDoneFallbackAsync(Checks checks)
    {
        // 网关直接关流、不发 [DONE]：只要有有效增量就不能当成失败
        var handler = new ScriptedHttpMessageHandler((_, _) =>
            ScriptedHttpMessageHandler.SseResponse(Sse.Utf8(
                "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"只有一段\"}}]}\n\n")));
        var client = new OpenAiCompatibleModelClient("https://api.example.com/v1", "sk-test",
            defaultModelId: "sse-fixture-model", httpClient: new HttpClient(handler));

        var completion = await client.StreamAsync(new PureModelRequest(
            new[] { new PureAgentMessage(PureAgentRole.User, "hi") }, Array.Empty<string>()));
        checks.Check(!completion.SawDone, "未收到 [DONE] 时 SawDone=false");
        checks.Equal("只有一段", completion.ToResponse().Text, "缺 [DONE] 但有增量时仍返回聚合文本");

        // 完全空流：必须明确失败，而不是静默返回空回复
        var emptyHandler = new ScriptedHttpMessageHandler((_, _) =>
            ScriptedHttpMessageHandler.SseResponse(Sse.Utf8("")));
        var emptyClient = new OpenAiCompatibleModelClient("https://api.example.com/v1", "sk-test",
            defaultModelId: "sse-fixture-model", httpClient: new HttpClient(emptyHandler));
        var emptyError = await checks.CapturesAsync<ModelClientException>(() => emptyClient.StreamAsync(
            new PureModelRequest(new[] { new PureAgentMessage(PureAgentRole.User, "hi") }, Array.Empty<string>())));
        checks.Check(emptyError is not null, "空流抛出结构化异常而不是伪造空回复");
        checks.Equal(ModelClientErrorKind.Transport, emptyError!.Kind, "空流归类为传输层失败");
    }

    private static async Task Base64ishMultibyteSplitAsync(Checks checks)
    {
        // 中文内容被切成 1 字节一片：有状态 UTF-8 解码必须保证不丢字
        var text = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"中文增量测试\"}}]}\n\n" + Sse.Done;
        var handler = new ScriptedHttpMessageHandler((_, _) =>
            ScriptedHttpMessageHandler.SseResponse(Sse.Split(text, 1)));
        var client = new OpenAiCompatibleModelClient("https://api.example.com/v1", "sk-test",
            defaultModelId: "sse-fixture-model", httpClient: new HttpClient(handler));

        var deltas = new List<string>();
        var completion = await client.StreamAsync(
            new PureModelRequest(new[] { new PureAgentMessage(PureAgentRole.User, "hi") }, Array.Empty<string>()),
            new ModelStreamCallbacks { OnTextDelta = deltas.Add });

        checks.Equal(1, deltas.Count, "逐字节切分下增量回调只触发一次");
        checks.Equal("中文增量测试", deltas[0], "逐字节切分下中文内容完整");
        checks.Equal("中文增量测试", completion.Primary!.Text, "聚合文本与原文一致");
    }
}
