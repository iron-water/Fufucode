using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Diagnostics;

namespace Fufucode.Core.Backend;

/// <summary>
/// 纯 C# 原生模型客户端：直接通过原生 HttpClient 调用兼容 OpenAI / DeepSeek / Claude / Local LLM 规范的端点，
/// 无任何外部 Node/Python 管道依赖。
/// 同时支持一次性（stream=false）与真实 Server-Sent Events 流式（stream=true）响应，
/// 流式路径增量派发 delta.content 与 delta.tool_calls 分片，并在 data: [DONE] 处收束。
/// </summary>
public class OpenAiCompatibleModelClient : IPureModelClient, IStreamingModelClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly string _defaultModelId;

    /// <summary>流式路径使用的额外请求头（例如自定义网关鉴权）。</summary>
    public IDictionary<string, string> AdditionalHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public OpenAiCompatibleModelClient(
        string baseUrl,
        string? apiKey = null,
        string defaultModelId = "",
        HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        // 不设内置默认模型：模型一律由端点配置给出，避免悄悄回退到一个过时的名字。
        _defaultModelId = defaultModelId;
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>已解析的 Chat Completions 端点。</summary>
    public string Endpoint => _baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
        ? _baseUrl
        : $"{_baseUrl}/chat/completions";

    /// <summary>
    /// 一次性请求：等待完整响应体后解析 choices[0].message。
    /// </summary>
    public async Task<PureModelResponse> GenerateAsync(PureModelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        using var httpRequest = CreateRequest(request, streaming: false);
        using var span = PerfTracer.Step("model.http", "mode", "non-streaming");
        HttpResponseMessage? response = null;
        try
        {
            response = await SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            span.Tag("status", (long)response.StatusCode);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            span.Tag("responseChars", (long)responseJson.Length);
            return ParseNonStreaming(response, responseJson, span);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            span.Cancel();
            throw;
        }
        catch (Exception error)
        {
            span.Fail(error.GetType().Name);
            throw;
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static PureModelResponse ParseNonStreaming(HttpResponseMessage response, string responseJson, PerfSpan span)
    {
        ThrowIfStreamError(responseJson);

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            throw new ModelClientException(
                ModelClientErrorKind.Unknown,
                "模型响应缺少 choices 数组。",
                (int)response.StatusCode,
                errorBody: Bound(responseJson));
        }

        var firstChoice = choices[0];
        if (!firstChoice.TryGetProperty("message", out var messageElem) || messageElem.ValueKind != JsonValueKind.Object)
        {
            throw new ModelClientException(
                ModelClientErrorKind.Unknown,
                "模型响应缺少 choices[0].message。",
                (int)response.StatusCode,
                errorBody: Bound(responseJson));
        }

        string? text = null;
        if (messageElem.TryGetProperty("content", out var contentElem) && contentElem.ValueKind == JsonValueKind.String)
        {
            text = contentElem.GetString();
        }

        var toolCalls = new List<PureToolCall>();
        if (messageElem.TryGetProperty("tool_calls", out var toolCallsElem) && toolCallsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in toolCallsElem.EnumerateArray())
            {
                var id = tc.TryGetProperty("id", out var idElem) ? idElem.GetString() : null;
                var name = "";
                var args = "{}";
                if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                {
                    if (fn.TryGetProperty("name", out var nameElem) && nameElem.ValueKind == JsonValueKind.String)
                    {
                        name = nameElem.GetString() ?? "";
                    }
                    if (fn.TryGetProperty("arguments", out var argsElem) && argsElem.ValueKind == JsonValueKind.String)
                    {
                        args = argsElem.GetString() ?? "{}";
                    }
                }
                toolCalls.Add(new PureToolCall(id ?? Guid.NewGuid().ToString("N"), name, args));
            }
        }

        span.Tag("toolCalls", toolCalls.Count);
        span.Tag("textChars", text?.Length ?? 0);
        if (root.TryGetProperty("usage", out var usageRoot) && usageRoot.ValueKind == JsonValueKind.Object)
            RecordUsage(span, ChatCompletionSseParser.ReadUsage(usageRoot));
        return new PureModelResponse(text, toolCalls);
    }

    private static void RecordUsage(PerfSpan span, PureUsage? usage)
    {
        if (usage is not { IsEmpty: false }) return;
        if (usage.InputTokens is { } input) span.Tag("promptTokens", input);
        if (usage.OutputTokens is { } output) span.Tag("completionTokens", output);
        if (usage.TotalTokens is { } total) span.Tag("totalTokens", total);
        if (usage.CacheReadTokens is { } cached) span.Tag("cachedTokens", cached);
        if (usage.CacheWriteTokens is { } cacheWrite) span.Tag("cacheWriteTokens", cacheWrite);
    }

    /// <summary>
    /// 流式请求：以 stream=true 发起，增量消费 SSE，回调逐分片派发，
    /// 返回聚合后的完整响应；data: [DONE] 或流结束即收束。
    /// </summary>
    public async Task<PureModelResponse> GenerateStreamingAsync(
        PureModelRequest request,
        ModelStreamCallbacks? callbacks = null,
        CancellationToken cancellationToken = default)
    {
        var completion = await StreamAsync(request, callbacks, cancellationToken).ConfigureAwait(false);
        return completion.ToResponse();
    }

    /// <summary>
    /// 流式请求的完整形态：返回带 choice/事件计数/是否见到 [DONE] 的聚合快照，
    /// 便于调用方做落后分片诊断与断言。
    /// </summary>
    public async Task<StreamedCompletion> StreamAsync(
        PureModelRequest request,
        ModelStreamCallbacks? callbacks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        using var httpRequest = CreateRequest(request, streaming: true);
        using var http = PerfTracer.Step("model.http", "mode", "streaming");
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            http.Cancel();
            throw;
        }
        catch (Exception error)
        {
            http.Fail(error.GetType().Name);
            throw;
        }
        using (response)
        {
            http.Tag("status", (long)response.StatusCode);
            return await ReadStreamAsync(response, callbacks, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>读取 SSE 流：逐帧解析，并在启用埋点时统计帧数、TTFT 与帧间隔。</summary>
    private async Task<StreamedCompletion> ReadStreamAsync(HttpResponseMessage response,
        ModelStreamCallbacks? callbacks, CancellationToken cancellationToken)
    {
        using var streamSpan = PerfTracer.Step("model.stream");

        var parser = new ChatCompletionSseParser();
        // 帧间隔统计：最大间隔定位“流式卡顿”，p95 反映整体抖动。只在启用时维护，未启用时零开销。
        var gapWatch = PerfTracer.Enabled ? System.Diagnostics.Stopwatch.StartNew() : null;
        var frameCount = 0;
        long maxGapMs = 0;
        long firstDataMs = -1;
        var gaps = gapWatch is null ? null : new BoundedGapHistogram();

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using (stream.ConfigureAwait(false))
            {
                await foreach (var sse in ServerSentEvents.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
                {
                    if (sse.IsDone)
                    {
                        parser.Completion.SawDone = true;
                        break;
                    }

                    if (gapWatch is not null)
                    {
                        var now = gapWatch.ElapsedMilliseconds;
                        if (firstDataMs < 0) firstDataMs = now;
                        else
                        {
                            var gap = now - gaps!.LastMs;
                            if (gap > maxGapMs) maxGapMs = gap;
                            gaps.Add(gap);
                        }
                        gaps!.LastMs = now;
                        frameCount++;
                    }

                    parser.TryApply(sse, callbacks);
                }
            }
        }
        catch (ModelClientException error)
        {
            streamSpan.Fail(error.GetType().Name);
            throw;
        }
        catch (OperationCanceledException)
        {
            streamSpan.Cancel();
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            streamSpan.Fail(ex.GetType().Name);
            throw ModelClientErrorMapper.FromTransport(ex);
        }

        if (gapWatch is not null)
        {
            streamSpan.Tag("frames", frameCount);
            streamSpan.Tag("ttftMs", firstDataMs < 0 ? 0 : firstDataMs);
            streamSpan.Tag("maxGapMs", maxGapMs);
            if (gaps is { Count: > 0 }) streamSpan.Tag("p95GapMs", gaps.Percentile(95));
        }

        if (!parser.Completion.SawDone)
        {
            // 没有 [DONE] 但有有效增量：部分网关直接关流；只要已解析出内容就按正常结束处理。
            if (parser.Completion.EventCount == 0)
            {
                streamSpan.Fail("Transport");
                throw new ModelClientException(
                    ModelClientErrorKind.Transport,
                    "流式响应在收到任何数据帧之前结束。");
            }

            // 例外：工具参数必须完整。缺 [DONE] 且没有 finish_reason 时，tool_calls 很可能被截断，
            // 半截 JSON 写入历史会让后续回合持续失败。
            var primary = parser.Completion.Primary;
            if (primary is { ToolCalls.Count: > 0 } && primary.FinishReason is null)
            {
                foreach (var call in primary.ToolCalls)
                {
                    try
                    {
                        using var _ = JsonDocument.Parse(call.ArgumentsJson);
                    }
                    catch (JsonException)
                    {
                        streamSpan.Fail("Transport");
                        throw new ModelClientException(ModelClientErrorKind.Transport,
                            "流式响应在工具参数传输完成前中断，本次工具调用未执行。");
                    }
                }
            }
        }

        if (parser.Completion.Usage is not null) RecordUsage(streamSpan, parser.Completion.Usage);
        callbacks?.OnCompleted?.Invoke();
        return parser.Completion;
    }

    private HttpRequestMessage CreateRequest(PureModelRequest request, bool streaming)
    {
        var model = string.IsNullOrWhiteSpace(request.ModelId) ? _defaultModelId : request.ModelId;
        if (string.IsNullOrWhiteSpace(model))
            throw new ModelClientException(ModelClientErrorKind.InvalidRequest,
                "未配置模型：请在设置中填写端点模型或选择可用模型。");
        var requestBody = BuildRequestBody(request, model, streaming);

        var json = JsonSerializer.Serialize(requestBody);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        if (streaming)
        {
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }

        foreach (var header in AdditionalHeaders)
        {
            httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return httpRequest;
    }

    /// <summary>发送 HTTP 请求。virtual 供本机/离线环境注入脚本化响应（测试与诊断探针）。</summary>
    protected virtual async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ModelClientErrorMapper.FromTransport(ex);
        }

        if (response.IsSuccessStatusCode) return response;

        try
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw ModelClientErrorMapper.FromStatus(response.StatusCode, errorBody);
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>把非流式的错误响应体（含 error 对象）映射为结构化异常。</summary>
    private static void ThrowIfStreamError(string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson)) return;

        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("error", out var error) &&
                error.ValueKind is JsonValueKind.Object or JsonValueKind.String)
            {
                throw ModelClientErrorMapper.FromStreamError(error);
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应体交由后续解析报错，这里不吞掉原始内容。
        }
    }

    private static string Bound(string body) =>
        body.Length <= ModelClientErrorMapper.MaxErrorBodyLength
            ? body
            : body[..ModelClientErrorMapper.MaxErrorBodyLength] + "…";

    public static Dictionary<string, object> BuildRequestBody(PureModelRequest request, string model, bool streaming)
    {
        var messagesList = new List<object>();
        foreach (var msg in request.Messages)
        {
            var roleStr = msg.Role switch
            {
                PureAgentRole.System => "system",
                PureAgentRole.Assistant => "assistant",
                PureAgentRole.Tool => "tool",
                _ => "user",
            };

            var text = msg.Content ?? string.Empty;

            if (msg.Role == PureAgentRole.Tool)
            {
                messagesList.Add(new
                {
                    role = "tool",
                    content = text,
                    tool_call_id = msg.ToolCallId ?? "call_0"
                });
            }
            else if (msg.Role == PureAgentRole.Assistant && msg.ToolCalls is { Count: > 0 } calls)
            {
                var tcList = new List<object>();
                foreach (var c in calls)
                {
                    tcList.Add(new
                    {
                        id = c.Id,
                        type = "function",
                        function = new
                        {
                            name = c.Name,
                            arguments = c.ArgumentsJson
                        }
                    });
                }
                messagesList.Add(new
                {
                    role = "assistant",
                    content = text,
                    tool_calls = tcList
                });
            }
            else if (msg.Images is { Count: > 0 } images && msg.Role == PureAgentRole.User)
            {
                var parts = new List<object>();
                if (text.Length > 0) parts.Add(new { type = "text", text });
                foreach (var img in images)
                {
                    parts.Add(new
                    {
                        type = "image_url",
                        image_url = new { url = $"data:{img.MimeType};base64,{img.Base64Data}" }
                    });
                }
                messagesList.Add(new
                {
                    role = "user",
                    content = parts
                });
            }
            else
            {
                messagesList.Add(new
                {
                    role = roleStr,
                    content = text
                });
            }
        }

        // 同名定义只保留第一条，避免插件与内置工具重名时 ToDictionary 直接抛异常。
        var definitionsMap = (request.ToolDefinitions ?? Array.Empty<PureToolDefinition>())
            .GroupBy(d => d.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var builtinMap = ProductionToolDispatcher.GetBuiltinToolDefinitions()
            .GroupBy(d => d.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var toolsList = new List<object>();
        foreach (var tool in request.ToolNames.Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal))
        {
            toolsList.Add(new
            {
                type = "function",
                function = ResolveFunctionTool(tool, definitionsMap, builtinMap)
            });
        }

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messagesList,
            ["temperature"] = 0.2,
            ["stream"] = streaming
        };

        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort) &&
            !string.Equals(request.ReasoningEffort, "off", StringComparison.OrdinalIgnoreCase))
        {
            // OpenAI 兼容网关的通用推理强度字段；不支持的网关通常会忽略未知字段。
            requestBody["reasoning_effort"] = request.ReasoningEffort;
        }

        if (toolsList.Count > 0)
        {
            requestBody["tools"] = toolsList;
            requestBody["tool_choice"] = "auto";
        }

        return requestBody;
    }

    private static object ResolveFunctionTool(
        string toolName,
        IReadOnlyDictionary<string, PureToolDefinition> definitions,
        IReadOnlyDictionary<string, PureToolDefinition> builtins)
    {
        if (definitions.TryGetValue(toolName, out var def) || builtins.TryGetValue(toolName, out def))
        {
            object parameters;
            try
            {
                using var document = JsonDocument.Parse(def.ParametersJsonSchema);
                parameters = document.RootElement.Clone();
            }
            catch
            {
                parameters = new { type = "object" };
            }

            return new
            {
                name = def.Name,
                description = def.Description,
                parameters = parameters
            };
        }

        return new
        {
            name = toolName,
            description = $"自定义工具 {toolName}",
            parameters = new { type = "object" }
        };
    }
}
