using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fufucode.Core.Backend;

/// <summary>
/// OpenAI Responses API（POST /v1/responses）原生客户端，纯 HTTP。
/// - 无状态：store=false，历史每轮完整发送；推理项带 encrypted_content 原样回传，工具续轮保留推理上下文。
/// - 函数工具显式 strict=false：Responses 默认严格模式会拒绝未声明 additionalProperties 的既有 Schema。
/// - 推理摘要流入 FUMO 思考区；网关或模型拒绝可选字段（reasoning、include、store、xhigh）时去掉后重发一次。
/// </summary>
public sealed class OpenAiResponsesModelClient : SseModelClientBase
{
    internal const string StateKind = "openai-responses";

    private bool _noReasoning;
    private bool _noSummary;
    private bool _noInclude;
    private bool _noStore;
    private bool _effortDowngraded;
    private bool _stripReasoningItems;

    public OpenAiResponsesModelClient(string baseUrl, string? apiKey = null, string defaultModelId = "",
        HttpClient? httpClient = null, TimeSpan? requestTimeout = null)
        : base(baseUrl, apiKey, defaultModelId, httpClient, requestTimeout) { }

    protected override string ProtocolId => ModelApi.OpenAiResponses;

    public override string Endpoint =>
        BaseUrl.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) ? BaseUrl : BaseUrl + "/responses";

    protected override void ApplyHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
    }

    private string? Effort(string? level)
    {
        var value = level?.Trim().ToLowerInvariant();
        return value switch
        {
            null or "" or "off" => null,
            "max" => _effortDowngraded ? "high" : "xhigh",
            "xhigh" when _effortDowngraded => "high",
            _ => value,
        };
    }

    protected override JsonElement BuildBody(PureModelRequest request, string model, bool streaming)
    {
        var body = new JsonObject { ["model"] = model };
        var instructions = string.Join("\n\n", request.Messages
            .Where(message => message.Role == PureAgentRole.System && !string.IsNullOrWhiteSpace(message.Content))
            .Select(message => message.Content.Trim()));
        if (instructions.Length > 0) body["instructions"] = instructions;
        body["input"] = BuildInput(request.Messages);

        var tools = ResolveTools(request);
        if (tools.Count > 0)
        {
            var array = new JsonArray();
            foreach (var tool in tools)
            {
                array.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(ToolSchema(tool).GetRawText()),
                    ["strict"] = false,
                });
            }
            body["tools"] = array;
        }

        var effort = Effort(request.ReasoningEffort);
        if (effort is not null && !_noReasoning)
        {
            var reasoning = new JsonObject { ["effort"] = effort };
            if (!_noSummary) reasoning["summary"] = "auto";
            body["reasoning"] = reasoning;
        }
        if (!_noStore) body["store"] = false;
        // 无状态续轮需要加密推理内容；非推理模型或不支持的网关拒绝时去掉。
        if (!_noInclude && !_noStore) body["include"] = new JsonArray("reasoning.encrypted_content");
        if (request.Temperature is { } temperature) body["temperature"] = temperature;
        body["stream"] = streaming;
        return JsonSerializer.SerializeToElement(body);
    }

    private JsonArray BuildInput(IReadOnlyList<PureAgentMessage> messages)
    {
        var input = new JsonArray();
        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case PureAgentRole.System:
                    continue;
                case PureAgentRole.Tool:
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = message.ToolCallId ?? "call_0",
                        ["output"] = message.Content ?? "",
                    });
                    break;
                case PureAgentRole.Assistant:
                    // 推理项先于正文与函数调用（与响应中的输出顺序一致）。
                    if (!_stripReasoningItems && ReadState(message.ProviderState) is { } reasoningItems)
                        foreach (var item in reasoningItems) input.Add(item?.DeepClone());
                    if (!string.IsNullOrEmpty(message.Content))
                        input.Add(new JsonObject { ["role"] = "assistant", ["content"] = message.Content });
                    foreach (var call in message.ToolCalls ?? Array.Empty<PureToolCall>())
                    {
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call",
                            ["call_id"] = call.Id,
                            ["name"] = call.Name,
                            ["arguments"] = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson,
                        });
                    }
                    break;
                default:
                    var content = new JsonArray();
                    var missing = 0;
                    var text = message.Content ?? "";
                    foreach (var image in message.Images ?? Array.Empty<PureImageAttachment>())
                    {
                        var data = SessionImageStore.Resolve(image);
                        if (string.IsNullOrEmpty(data)) { missing++; continue; }
                        content.Add(new JsonObject
                        {
                            ["type"] = "input_image",
                            ["image_url"] = $"data:{image.MimeType};base64,{data}",
                        });
                    }
                    if (missing > 0) text += (text.Length > 0 ? "\n" : "") + $"[有 {missing} 张历史附件的本地文件已不可读，未随本次请求发送]";
                    if (text.Length > 0 || content.Count == 0) content.Insert(0, new JsonObject { ["type"] = "input_text", ["text"] = text });
                    input.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                    break;
            }
        }
        return input;
    }

    internal static JsonArray? ReadState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return null;
        try
        {
            var root = JsonNode.Parse(state) as JsonObject;
            return root?["api"]?.GetValue<string>() == StateKind ? root["reasoning"] as JsonArray : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return null; }
    }

    protected override bool TryRecover(ModelClientException error)
    {
        var text = (error.ErrorBody ?? "") + " " + error.Message;
        bool Mentions(string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);

        // 回传的推理项失效（换了组织/模型、内容不可解密）：剥离后重发。
        if ((Mentions("encrypted_content") || Mentions("reasoning item") || Mentions("rs_")) && !_stripReasoningItems &&
            !Mentions("include"))
        {
            _stripReasoningItems = true;
            return true;
        }
        if (Mentions("include") && !_noInclude) { _noInclude = true; return true; }
        if (Mentions("summary") && !_noSummary) { _noSummary = true; return true; }
        if (Mentions("xhigh") && !_effortDowngraded) { _effortDowngraded = true; return true; }
        if (Mentions("reasoning") && !_noReasoning) { _noReasoning = true; return true; }
        if (Mentions("store") && !_noStore) { _noStore = true; return true; }
        return false;
    }

    protected override PureModelResponse ParseResponse(JsonElement root, int statusCode, string rawBody)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            throw new ModelClientException(ModelClientErrorKind.Unknown, "Responses 响应缺少 output 数组。",
                statusCode, errorBody: Bound(rawBody));
        var parser = new ResponsesStreamParser();
        parser.ApplyFinished(root);
        return parser.Completion.ToResponse() with { ProviderState = parser.ProviderState };
    }

    protected override IModelStreamParser CreateStreamParser() => new ResponsesStreamParser();

    internal static ModelClientException ResponseError(JsonElement error)
    {
        var code = Str(error, "code") ?? Str(error, "type");
        int? status = code switch
        {
            "rate_limit_exceeded" => 429,
            "server_error" or "server_is_overloaded" => 500,
            "invalid_prompt" or "invalid_request_error" => 400,
            _ => null,
        };
        return ModelClientErrorMapper.FromStreamError(error, status);
    }

    private sealed class ResponsesStreamParser : IModelStreamParser
    {
        private readonly StreamedChoice _choice;
        private readonly Dictionary<int, int> _toolByOutput = new();
        private readonly HashSet<int> _argumentsStreamed = new();
        private readonly SortedDictionary<int, JsonNode> _reasoning = new();
        private int _nextTool;
        private int _lastSummaryIndex = -1;
        private int _lastSummaryOutput = -1;
        private string? _incomplete;

        public ResponsesStreamParser() => _choice = Completion.Choice(0);

        public StreamedCompletion Completion { get; } = new();

        public string? ProviderState =>
            _reasoning.Count == 0 ? null : new JsonObject
            {
                ["api"] = StateKind,
                ["reasoning"] = new JsonArray(_reasoning.Values.Select(node => (JsonNode?)node.DeepClone()).ToArray()),
            }.ToJsonString();

        public bool Apply(ServerSentEvent sse, ModelStreamCallbacks? callbacks)
        {
            using var document = JsonDocument.Parse(sse.Data);
            var root = document.RootElement;
            var outputIndex = (int)(Long(root, "output_index") ?? -1);
            switch (Str(root, "type") ?? sse.EventName)
            {
                case "response.created":
                case "response.in_progress":
                    if (root.TryGetProperty("response", out var started)) Completion.Model = Str(started, "model");
                    break;
                case "response.output_item.added":
                    if (root.TryGetProperty("item", out var added) && Str(added, "type") == "function_call")
                        StartToolCall(outputIndex, added, callbacks);
                    break;
                case "response.function_call_arguments.delta" when Str(root, "delta") is { Length: > 0 } fragment:
                    if (!_toolByOutput.TryGetValue(outputIndex, out var toolIndex)) break;
                    _argumentsStreamed.Add(outputIndex);
                    _choice.AppendToolCallChunk(toolIndex, null, null, fragment);
                    callbacks?.OnToolCallChunk?.Invoke("", "", fragment);
                    callbacks?.OnToolCallChunkIndexed?.Invoke(toolIndex, null, null, fragment);
                    break;
                case "response.output_text.delta" or "response.refusal.delta" when Str(root, "delta") is { Length: > 0 } text:
                    _choice.AppendText(text);
                    callbacks?.OnTextDelta?.Invoke(text);
                    break;
                case "response.reasoning_summary_text.delta" or "response.reasoning_text.delta"
                    when Str(root, "delta") is { Length: > 0 } thinking:
                    // 多段摘要之间留空行，避免拼成一段。
                    var summaryIndex = (int)(Long(root, "summary_index") ?? 0);
                    if (_choice.ThinkingDeltaCount > 0 && (summaryIndex != _lastSummaryIndex || outputIndex != _lastSummaryOutput))
                    {
                        _choice.AppendThinking("\n\n");
                        callbacks?.OnThinkingDelta?.Invoke("\n\n");
                    }
                    _lastSummaryIndex = summaryIndex;
                    _lastSummaryOutput = outputIndex;
                    _choice.AppendThinking(thinking);
                    callbacks?.OnThinkingDelta?.Invoke(thinking);
                    break;
                case "response.output_item.done":
                    if (root.TryGetProperty("item", out var done)) FinishItem(outputIndex, done, callbacks);
                    break;
                case "response.completed":
                case "response.incomplete":
                    if (root.TryGetProperty("response", out var final)) Complete(final);
                    return true;
                case "response.failed":
                    var failed = root.TryGetProperty("response", out var failure) && failure.TryGetProperty("error", out var failureError)
                        ? failureError : root;
                    throw ResponseError(failed);
                case "error":
                    throw ResponseError(root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? error : root);
            }
            return false;
        }

        public void ApplyFinished(JsonElement response)
        {
            Completion.Model = Str(response, "model");
            var index = 0;
            foreach (var item in response.GetProperty("output").EnumerateArray())
            {
                switch (Str(item, "type"))
                {
                    case "message" when item.TryGetProperty("content", out var parts) && parts.ValueKind == JsonValueKind.Array:
                        foreach (var part in parts.EnumerateArray())
                            if ((Str(part, "text") ?? Str(part, "refusal")) is { Length: > 0 } text) _choice.AppendText(text);
                        break;
                    case "function_call":
                        StartToolCall(index, item, null);
                        break;
                }
                FinishItem(index, item, null);
                index++;
            }
            Complete(response);
        }

        private void StartToolCall(int outputIndex, JsonElement item, ModelStreamCallbacks? callbacks)
        {
            if (_toolByOutput.ContainsKey(outputIndex)) return;
            var toolIndex = _nextTool++;
            _toolByOutput[outputIndex] = toolIndex;
            var id = Str(item, "call_id") ?? Str(item, "id");
            var name = Str(item, "name");
            _choice.AppendToolCallChunk(toolIndex, id, name, "");
            callbacks?.OnToolCallChunk?.Invoke(id ?? "", name ?? "", "");
            callbacks?.OnToolCallChunkIndexed?.Invoke(toolIndex, id, name, "");
        }

        private void FinishItem(int outputIndex, JsonElement item, ModelStreamCallbacks? callbacks)
        {
            switch (Str(item, "type"))
            {
                case "function_call":
                    StartToolCall(outputIndex, item, callbacks);
                    // 没有收到增量（一次性响应或网关整块下发）时用完成项里的完整参数。
                    if (!_argumentsStreamed.Contains(outputIndex) && Str(item, "arguments") is { Length: > 0 } arguments)
                    {
                        var toolIndex = _toolByOutput[outputIndex];
                        _argumentsStreamed.Add(outputIndex);
                        _choice.AppendToolCallChunk(toolIndex, null, null, arguments);
                        callbacks?.OnToolCallChunk?.Invoke("", "", arguments);
                        callbacks?.OnToolCallChunkIndexed?.Invoke(toolIndex, null, null, arguments);
                    }
                    break;
                case "reasoning":
                    // 只有带加密内容的推理项能在 store=false 下回传；否则引用服务端不存在的项会被拒绝。
                    if (Str(item, "encrypted_content") is { Length: > 0 })
                    {
                        var node = JsonNode.Parse(item.GetRawText())!;
                        if (node is JsonObject obj) obj.Remove("status");
                        _reasoning[outputIndex] = node;
                    }
                    if (_choice.ThinkingDeltaCount == 0 && item.TryGetProperty("summary", out var summary) &&
                        summary.ValueKind == JsonValueKind.Array)
                    {
                        var text = string.Join("\n\n", summary.EnumerateArray().Select(part => Str(part, "text")).Where(t => !string.IsNullOrEmpty(t)));
                        if (text.Length > 0)
                        {
                            _choice.AppendThinking(text);
                            callbacks?.OnThinkingDelta?.Invoke(text);
                        }
                    }
                    break;
            }
        }

        private void Complete(JsonElement response)
        {
            if (Str(response, "status") == "incomplete")
                _incomplete = response.TryGetProperty("incomplete_details", out var details) ? Str(details, "reason") ?? "incomplete" : "incomplete";
            if (response.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                long? cached = usage.TryGetProperty("input_tokens_details", out var inputDetails) ? Long(inputDetails, "cached_tokens") : null;
                Completion.Usage = new PureUsage(Long(usage, "input_tokens"), Long(usage, "output_tokens"),
                    Long(usage, "total_tokens"), cached);
            }
            var finish = _incomplete is not null ? "length" : _choice.ToolCalls.Count > 0 ? "tool_calls" : "stop";
            if (finish == "length")
            {
                foreach (var call in _choice.ToolCalls)
                {
                    try { using var _ = JsonDocument.Parse(call.ArgumentsJson); }
                    catch (JsonException)
                    {
                        throw new ModelClientException(ModelClientErrorKind.StreamError,
                            $"模型输出未完成（{_incomplete}），工具参数被截断，本次工具调用未执行。", errorCode: _incomplete);
                    }
                }
            }
            _choice.SetFinishReason(finish);
        }
    }
}
