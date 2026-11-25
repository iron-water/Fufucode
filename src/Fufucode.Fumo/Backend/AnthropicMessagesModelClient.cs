using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fufucode.Core.Backend;

/// <summary>
/// Anthropic Messages API（POST /v1/messages）原生客户端，纯 HTTP，无 SDK 依赖。
/// - 思考：Claude 4.6 及以后用自适应思考 + output_config.effort；更早的模型用 budget_tokens；
///   展示设为 summarized，FUMO 思考区才有内容（新模型默认 omitted 只给空块）。
/// - 回传：助手内容块（含带签名的 thinking/redacted_thinking）原样存进 ProviderState，下一轮逐字节回放；
///   签名因对话前缀变化失效被拒时，先请求服务端丢弃失效块，网关不支持再剥离思考后重发。
/// - 不发送 temperature（新模型一律 400）；顶层自动提示缓存；直连 api.anthropic.com 时开启工具参数流式。
/// - 网关拒绝可选字段（缓存、effort、思考形式、max_tokens 上限）时去掉或调整后重发一次，不让整轮失败。
/// </summary>
public sealed class AnthropicMessagesModelClient : SseModelClientBase
{
    public const string ApiVersion = "2023-06-01";
    private const string BindingBeta = "thinking-binding-controls-2026-08-01";
    internal const string StateKind = "anthropic-messages";

    private enum ThinkingMode { Auto, Adaptive, Budget, Off }

    private ThinkingMode _thinkingOverride = ThinkingMode.Auto;
    private int? _maxTokensCap;
    private bool _noCacheControl;
    private bool _noEagerStreaming;
    private bool _noEffort;
    private bool _noDisplay;
    private bool _effortDowngraded;
    private bool _dropInvalidThinking;
    private bool _stripReplayedThinking;
    private bool _bindingBetaUnsupported;

    public AnthropicMessagesModelClient(string baseUrl, string? apiKey = null, string defaultModelId = "",
        HttpClient? httpClient = null, TimeSpan? requestTimeout = null)
        : base(baseUrl, apiKey, defaultModelId, httpClient, requestTimeout) { }

    protected override string ProtocolId => ModelApi.AnthropicMessages;

    public override string Endpoint =>
        BaseUrl.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) ? BaseUrl :
        BaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? BaseUrl + "/messages" :
        BaseUrl + "/v1/messages";

    private bool IsFirstParty =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) &&
        (uri.Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".anthropic.com", StringComparison.OrdinalIgnoreCase));

    protected override void ApplyHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", ApiKey);
            // 第三方 Anthropic 兼容网关多数按 Bearer 鉴权；官方端点只用 x-api-key。
            if (!IsFirstParty) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey);
        }
        if (_dropInvalidThinking) request.Headers.TryAddWithoutValidation("anthropic-beta", BindingBeta);
    }

    // ───────────────────────── 请求体 ─────────────────────────

    /// <summary>Claude 4.6 起的模型用自适应思考；更早的 Claude 4.x / 3.7 用 budget_tokens；3.5 及以前不支持思考。</summary>
    internal static string ThinkingStyle(string model)
    {
        var id = model.ToLowerInvariant();
        string[] adaptive = ["opus-4-6", "sonnet-4-6", "opus-4-7", "opus-4-8", "opus-5", "sonnet-5", "haiku-5", "fable", "mythos"];
        if (adaptive.Any(id.Contains)) return "adaptive";
        if (id.Contains("claude-3-5") || id.Contains("claude-3-opus") || id.Contains("claude-3-haiku") ||
            id.Contains("claude-3-sonnet") || id.Contains("claude-instant") || id.Contains("claude-2")) return "none";
        return "budget";
    }

    private static string? Effort(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        null or "" or "off" or "none" => null,
        "minimal" => "low",
        var value => value,
    };

    protected override JsonElement BuildBody(PureModelRequest request, string model, bool streaming)
    {
        var maxTokens = Math.Min(_maxTokensCap ?? int.MaxValue, streaming ? 64000 : 16000);
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
        };

        var system = string.Join("\n\n", request.Messages
            .Where(message => message.Role == PureAgentRole.System && !string.IsNullOrWhiteSpace(message.Content))
            .Select(message => message.Content.Trim()));
        if (system.Length > 0) body["system"] = system;
        body["messages"] = BuildMessages(request.Messages);

        var tools = ResolveTools(request);
        if (tools.Count > 0)
        {
            var array = new JsonArray();
            foreach (var tool in tools)
            {
                var node = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = JsonNode.Parse(ToolSchema(tool).GetRawText()),
                };
                // 大参数（整文件内容）边生成边下发；代理与旧部署可能不认该字段，所以只对官方端点开启。
                if (streaming && IsFirstParty && !_noEagerStreaming) node["eager_input_streaming"] = true;
                array.Add(node);
            }
            body["tools"] = array;
        }

        ApplyThinking(body, model, Effort(request.ReasoningEffort), maxTokens);
        if (!_noCacheControl) body["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        if (streaming) body["stream"] = true;
        return JsonSerializer.SerializeToElement(body);
    }

    private void ApplyThinking(JsonObject body, string model, string? effort, int maxTokens)
    {
        var style = _thinkingOverride switch
        {
            ThinkingMode.Adaptive => "adaptive",
            ThinkingMode.Budget => "budget",
            ThinkingMode.Off => "none",
            _ => ThinkingStyle(model),
        };
        if (style == "none" || effort is null) return;
        if (style == "adaptive")
        {
            var thinking = new JsonObject { ["type"] = "adaptive" };
            if (!_noDisplay) thinking["display"] = "summarized";
            if (_dropInvalidThinking)
                thinking["block_binding"] = new JsonObject { ["prefix_mismatch_behavior"] = "drop_block" };
            body["thinking"] = thinking;
            if (!_noEffort)
            {
                var value = _effortDowngraded && effort is "xhigh" or "max" ? "high" : effort;
                body["output_config"] = new JsonObject { ["effort"] = value };
            }
            return;
        }
        var budget = effort switch { "low" => 4096, "medium" => 10000, "high" => 24000, _ => 32000 };
        budget = Math.Max(1024, Math.Min(budget, maxTokens - 2048));
        body["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget };
    }

    private JsonArray BuildMessages(IReadOnlyList<PureAgentMessage> messages)
    {
        var turns = new List<(string Role, JsonArray Content)>();
        void Append(string role, JsonArray content)
        {
            if (content.Count == 0) return;
            // 连续同角色合并：并行工具的多个 tool_result 必须在同一条 user 消息里。
            if (turns.Count > 0 && turns[^1].Role == role)
            {
                foreach (var block in content.ToArray())
                {
                    content.Remove(block);
                    turns[^1].Content.Add(block);
                }
                return;
            }
            turns.Add((role, content));
        }

        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case PureAgentRole.System:
                    continue;
                case PureAgentRole.Tool:
                    Append("user", new JsonArray(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = message.ToolCallId ?? "call_0",
                        ["content"] = string.IsNullOrEmpty(message.Content) ? "(无输出)" : message.Content,
                    }));
                    break;
                case PureAgentRole.Assistant:
                    Append("assistant", AssistantContent(message));
                    break;
                default:
                    Append("user", UserContent(message));
                    break;
            }
        }

        // Messages API 要求从 user 开始。
        if (turns.Count > 0 && turns[0].Role != "user")
            turns.Insert(0, ("user", new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "(继续)" })));

        var result = new JsonArray();
        foreach (var (role, content) in turns)
            result.Add(new JsonObject { ["role"] = role, ["content"] = content });
        return result;
    }

    private static JsonArray UserContent(PureAgentMessage message)
    {
        var content = new JsonArray();
        var missing = 0;
        foreach (var image in message.Images ?? Array.Empty<PureImageAttachment>())
        {
            var data = SessionImageStore.Resolve(image);
            if (string.IsNullOrEmpty(data)) { missing++; continue; }
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.MimeType, ["data"] = data },
            });
        }
        var text = message.Content ?? "";
        if (missing > 0) text += (text.Length > 0 ? "\n" : "") + $"[有 {missing} 张历史附件的本地文件已不可读，未随本次请求发送]";
        if (!string.IsNullOrWhiteSpace(text)) content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        return content;
    }

    /// <summary>
    /// 助手内容：保存的原始块与当前消息一致（正文、工具调用未被改写）时逐字节回放，
    /// 签名思考块才有效；不一致或已剥离思考时按正文与工具调用重建。
    /// </summary>
    private JsonArray AssistantContent(PureAgentMessage message)
    {
        var saved = ReadState(message.ProviderState);
        if (saved is not null && MatchesMessage(saved, message))
        {
            var replay = new JsonArray();
            foreach (var block in saved)
            {
                if (block is null) continue;
                var type = block["type"]?.GetValue<string>();
                if (_stripReplayedThinking && type is "thinking" or "redacted_thinking") continue;
                replay.Add(block.DeepClone());
            }
            return replay;
        }

        var content = new JsonArray();
        if (!string.IsNullOrWhiteSpace(message.Content))
            content.Add(new JsonObject { ["type"] = "text", ["text"] = message.Content });
        foreach (var call in message.ToolCalls ?? Array.Empty<PureToolCall>())
        {
            content.Add(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = call.Id,
                ["name"] = call.Name,
                ["input"] = ParseInput(call.ArgumentsJson),
            });
        }
        return content;
    }

    private static JsonNode ParseInput(string arguments)
    {
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            return node is JsonObject ? node : new JsonObject { ["value"] = node };
        }
        catch (JsonException) { return new JsonObject(); }
    }

    internal static JsonArray? ReadState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return null;
        try
        {
            var root = JsonNode.Parse(state) as JsonObject;
            return root?["api"]?.GetValue<string>() == StateKind ? root["content"] as JsonArray : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return null; }
    }

    private static bool MatchesMessage(JsonArray saved, PureAgentMessage message)
    {
        var text = new StringBuilder();
        var ids = new List<string>();
        foreach (var block in saved)
        {
            switch (block?["type"]?.GetValue<string>())
            {
                case "text": text.Append(block["text"]?.GetValue<string>()); break;
                case "tool_use": ids.Add(block["id"]?.GetValue<string>() ?? ""); break;
            }
        }
        var expectedIds = (message.ToolCalls ?? Array.Empty<PureToolCall>()).Select(call => call.Id);
        return string.Equals(text.ToString().Trim(), (message.Content ?? "").Trim(), StringComparison.Ordinal) &&
               ids.SequenceEqual(expectedIds, StringComparer.Ordinal);
    }

    // ───────────────────────── 拒绝后的降级 ─────────────────────────

    private static readonly Regex MaxTokensLimit = new(@"(?:>\s*|maximum[^0-9]{0,40})(\d{3,7})", RegexOptions.IgnoreCase);

    protected override bool TryRecover(ModelClientException error)
    {
        var text = (error.ErrorBody ?? "") + " " + error.Message;
        bool Mentions(string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);

        if (Mentions("signature") && Mentions("thinking"))
        {
            // 对话前缀变了（系统提示/工具集重建），旧思考块失效：先让服务端丢弃失效块，网关不认 beta 再整段剥离。
            if (!_dropInvalidThinking && !_bindingBetaUnsupported && !_stripReplayedThinking)
            {
                _dropInvalidThinking = true;
                return true;
            }
            if (_stripReplayedThinking) return false;
            _dropInvalidThinking = false;
            _stripReplayedThinking = true;
            return true;
        }
        if (_dropInvalidThinking && (Mentions("anthropic-beta") || Mentions("block_binding") || Mentions(BindingBeta)))
        {
            _dropInvalidThinking = false;
            _bindingBetaUnsupported = true;
            _stripReplayedThinking = true;
            return true;
        }
        if (Mentions("max_tokens"))
        {
            var match = MaxTokensLimit.Matches(text).Select(m => int.Parse(m.Groups[1].Value)).Where(v => v >= 1024).ToArray();
            var current = _maxTokensCap ?? 64000;
            var next = match.Length > 0 ? match.Min() : current / 2;
            if (next >= current || next < 1024) return false;
            _maxTokensCap = next;
            return true;
        }
        if (Mentions("cache_control") && !_noCacheControl) { _noCacheControl = true; return true; }
        if (Mentions("eager_input_streaming") && !_noEagerStreaming) { _noEagerStreaming = true; return true; }
        if (Mentions("display") && !_noDisplay) { _noDisplay = true; return true; }
        if ((Mentions("effort") || Mentions("output_config")) && !_noEffort)
        {
            if (!_effortDowngraded && (Mentions("xhigh") || Mentions("max"))) { _effortDowngraded = true; return true; }
            _noEffort = true;
            return true;
        }
        if (Mentions("budget_tokens") && _thinkingOverride != ThinkingMode.Adaptive) { _thinkingOverride = ThinkingMode.Adaptive; return true; }
        if (Mentions("adaptive") && _thinkingOverride != ThinkingMode.Budget) { _thinkingOverride = ThinkingMode.Budget; return true; }
        if (Mentions("thinking") && _thinkingOverride != ThinkingMode.Off) { _thinkingOverride = ThinkingMode.Off; return true; }
        return false;
    }

    // ───────────────────────── 响应 ─────────────────────────

    protected override PureModelResponse ParseResponse(JsonElement root, int statusCode, string rawBody)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new ModelClientException(ModelClientErrorKind.Unknown, "Anthropic 响应缺少 content 数组。",
                statusCode, errorBody: Bound(rawBody));
        var parser = new AnthropicStreamParser();
        parser.ApplyFinished(root);
        return parser.Completion.ToResponse() with { ProviderState = parser.ProviderState };
    }

    protected override IModelStreamParser CreateStreamParser() => new AnthropicStreamParser();

    internal static string MapStopReason(string? reason) => reason switch
    {
        "tool_use" => "tool_calls",
        "max_tokens" => "length",
        "refusal" => "refusal",
        null => "stop",
        _ => "stop",
    };

    internal static ModelClientException StreamError(JsonElement error)
    {
        var type = Str(error, "type");
        int? status = type switch
        {
            "overloaded_error" => 529,
            "rate_limit_error" => 429,
            "api_error" => 500,
            "authentication_error" => 401,
            "permission_error" => 403,
            "invalid_request_error" => 400,
            "request_too_large" => 413,
            _ => null,
        };
        return ModelClientErrorMapper.FromStreamError(error, status);
    }

    private sealed class AnthropicStreamParser : IModelStreamParser
    {
        private sealed class Block
        {
            public required string Type;
            public required JsonObject Node;
            public readonly StringBuilder Text = new();
            public readonly StringBuilder Thinking = new();
            public readonly StringBuilder Signature = new();
            public readonly StringBuilder Json = new();
            public int ToolIndex = -1;
        }

        private readonly SortedDictionary<int, Block> _blocks = new();
        private readonly StreamedChoice _choice;
        private int _nextTool;
        private long? _input, _cacheRead, _cacheWrite, _output;
        private string? _stopReason;

        public AnthropicStreamParser() => _choice = Completion.Choice(0);

        public StreamedCompletion Completion { get; } = new();

        public string? ProviderState
        {
            get
            {
                // 只有含思考块时才需要逐字节回放；纯文本/工具调用由消息本身即可重建。
                var content = new JsonArray(_blocks.Values.Select(block => (JsonNode?)Finish(block)).ToArray());
                if (!content.Any(node => node?["type"]?.GetValue<string>() is "thinking" or "redacted_thinking")) return null;
                return new JsonObject { ["api"] = StateKind, ["content"] = content }.ToJsonString();
            }
        }

        public bool Apply(ServerSentEvent sse, ModelStreamCallbacks? callbacks)
        {
            using var document = JsonDocument.Parse(sse.Data);
            var root = document.RootElement;
            switch (Str(root, "type") ?? sse.EventName)
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var message))
                    {
                        Completion.Model = Str(message, "model");
                        if (message.TryGetProperty("usage", out var startUsage)) ReadUsage(startUsage);
                    }
                    break;
                case "content_block_start":
                    Start((int)(Long(root, "index") ?? _blocks.Count), root.GetProperty("content_block"), callbacks);
                    break;
                case "content_block_delta":
                    Delta((int)(Long(root, "index") ?? 0), root.GetProperty("delta"), callbacks);
                    break;
                case "message_delta":
                    if (root.TryGetProperty("delta", out var delta) && Str(delta, "stop_reason") is { } reason) _stopReason = reason;
                    if (root.TryGetProperty("usage", out var usage)) ReadUsage(usage);
                    break;
                case "message_stop":
                    Complete(callbacks);
                    return true;
                case "error":
                    throw StreamError(root.TryGetProperty("error", out var error) ? error : root);
            }
            return false;
        }

        /// <summary>一次性响应：把完整 content 当作已结束的流折叠进来。</summary>
        public void ApplyFinished(JsonElement message)
        {
            Completion.Model = Str(message, "model");
            if (message.TryGetProperty("usage", out var usage)) ReadUsage(usage);
            var index = 0;
            foreach (var block in message.GetProperty("content").EnumerateArray())
            {
                var type = Str(block, "type") ?? "";
                var node = (JsonObject)JsonNode.Parse(block.GetRawText())!;
                var entry = new Block { Type = type, Node = node };
                switch (type)
                {
                    case "text":
                        _choice.AppendText(Str(block, "text") ?? "");
                        break;
                    case "thinking":
                        _choice.AppendThinking(Str(block, "thinking") ?? "");
                        break;
                    case "tool_use":
                        entry.ToolIndex = _nextTool++;
                        var input = block.TryGetProperty("input", out var inputElement) ? inputElement.GetRawText() : "{}";
                        _choice.AppendToolCallChunk(entry.ToolIndex, Str(block, "id"), Str(block, "name"), input);
                        break;
                }
                _blocks[index++] = entry;
            }
            _stopReason = Str(message, "stop_reason");
            Complete(null);
        }

        private void Start(int index, JsonElement block, ModelStreamCallbacks? callbacks)
        {
            var type = Str(block, "type") ?? "";
            var entry = new Block { Type = type, Node = (JsonObject)JsonNode.Parse(block.GetRawText())! };
            _blocks[index] = entry;
            switch (type)
            {
                case "text" when Str(block, "text") is { Length: > 0 } text:
                    entry.Text.Append(text);
                    _choice.AppendText(text);
                    callbacks?.OnTextDelta?.Invoke(text);
                    break;
                case "thinking" when Str(block, "thinking") is { Length: > 0 } thinking:
                    entry.Thinking.Append(thinking);
                    _choice.AppendThinking(thinking);
                    callbacks?.OnThinkingDelta?.Invoke(thinking);
                    break;
                case "tool_use":
                    entry.ToolIndex = _nextTool++;
                    var id = Str(block, "id");
                    var name = Str(block, "name");
                    _choice.AppendToolCallChunk(entry.ToolIndex, id, name, "");
                    callbacks?.OnToolCallChunk?.Invoke(id ?? "", name ?? "", "");
                    callbacks?.OnToolCallChunkIndexed?.Invoke(entry.ToolIndex, id, name, "");
                    break;
            }
        }

        private void Delta(int index, JsonElement delta, ModelStreamCallbacks? callbacks)
        {
            if (!_blocks.TryGetValue(index, out var entry)) return;
            switch (Str(delta, "type"))
            {
                case "text_delta" when Str(delta, "text") is { Length: > 0 } text:
                    entry.Text.Append(text);
                    _choice.AppendText(text);
                    callbacks?.OnTextDelta?.Invoke(text);
                    break;
                case "thinking_delta" when Str(delta, "thinking") is { Length: > 0 } thinking:
                    entry.Thinking.Append(thinking);
                    _choice.AppendThinking(thinking);
                    callbacks?.OnThinkingDelta?.Invoke(thinking);
                    break;
                case "signature_delta":
                    entry.Signature.Append(Str(delta, "signature"));
                    break;
                case "input_json_delta" when Str(delta, "partial_json") is { Length: > 0 } fragment:
                    entry.Json.Append(fragment);
                    if (entry.ToolIndex < 0) break;
                    _choice.AppendToolCallChunk(entry.ToolIndex, null, null, fragment);
                    callbacks?.OnToolCallChunk?.Invoke("", "", fragment);
                    callbacks?.OnToolCallChunkIndexed?.Invoke(entry.ToolIndex, null, null, fragment);
                    break;
            }
        }

        private static JsonObject Finish(Block block)
        {
            var node = (JsonObject)block.Node.DeepClone();
            switch (block.Type)
            {
                case "text":
                    if (block.Text.Length > 0) node["text"] = block.Text.ToString();
                    break;
                case "thinking":
                    if (block.Thinking.Length > 0) node["thinking"] = block.Thinking.ToString();
                    if (block.Signature.Length > 0) node["signature"] = block.Signature.ToString();
                    break;
                case "tool_use":
                    if (block.Json.Length > 0)
                    {
                        try { node["input"] = JsonNode.Parse(block.Json.ToString()); }
                        catch (JsonException) { node["input"] = new JsonObject(); }
                    }
                    break;
            }
            return node;
        }

        private void Complete(ModelStreamCallbacks? callbacks)
        {
            var finish = MapStopReason(_stopReason);
            if (_stopReason == "refusal" && _choice.TextDeltaCount == 0)
            {
                const string notice = "（模型拒绝了这次请求。）";
                _choice.AppendText(notice);
                callbacks?.OnTextDelta?.Invoke(notice);
            }
            if (finish == "length")
            {
                foreach (var call in _choice.ToolCalls)
                {
                    try { using var _ = JsonDocument.Parse(call.ArgumentsJson); }
                    catch (JsonException)
                    {
                        throw new ModelClientException(ModelClientErrorKind.StreamError,
                            "模型输出达到 max_tokens 上限，工具参数被截断，本次工具调用未执行。", errorCode: "max_tokens");
                    }
                }
            }
            _choice.SetFinishReason(finish);
            if (_input is not null || _output is not null)
            {
                var input = (_input ?? 0) + (_cacheRead ?? 0) + (_cacheWrite ?? 0);
                Completion.Usage = new PureUsage(input, _output, input + (_output ?? 0), _cacheRead, _cacheWrite);
            }
        }

        private void ReadUsage(JsonElement usage)
        {
            _input = Long(usage, "input_tokens") ?? _input;
            _cacheRead = Long(usage, "cache_read_input_tokens") ?? _cacheRead;
            _cacheWrite = Long(usage, "cache_creation_input_tokens") ?? _cacheWrite;
            _output = Long(usage, "output_tokens") ?? _output;
        }
    }
}
