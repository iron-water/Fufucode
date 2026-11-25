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

/// <summary>一种协议的 SSE 流解析器：逐帧把事件折叠进 <see cref="StreamedCompletion"/>。</summary>
public interface IModelStreamParser
{
    StreamedCompletion Completion { get; }

    /// <summary>应用一帧；返回 true 表示协议的终止事件已到达。流内错误以 <see cref="ModelClientException"/> 抛出。</summary>
    bool Apply(ServerSentEvent sse, ModelStreamCallbacks? callbacks);

    /// <summary>需要在后续请求里原样回传的供应商状态（签名、加密推理等）；没有时为 null。</summary>
    string? ProviderState { get; }
}

/// <summary>
/// 非 Chat Completions 协议（Anthropic Messages、OpenAI Responses）的共用 HTTP 骨架：
/// 尝试循环、单次超时、重试与退避、流式失败降级为一次性请求、性能埋点。
/// 子类只负责请求体、请求头、响应解析和流解析器；网关拒绝可选特性时通过
/// <see cref="TryRecover"/> 去掉该特性后立即重发，不消耗重试预算。
/// </summary>
public abstract class SseModelClientBase : IPureModelClient, IStreamingModelClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan? _requestTimeout;
    private bool _disposed;

    protected SseModelClientBase(string baseUrl, string? apiKey, string defaultModelId,
        HttpClient? httpClient, TimeSpan? requestTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        BaseUrl = baseUrl.TrimEnd('/');
        ApiKey = apiKey;
        DefaultModelId = defaultModelId;
        _ownsHttpClient = httpClient is null;
        // 不继承 HttpClient 的 100 秒默认超时：长推理请求经常超过它。
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _requestTimeout = requestTimeout is { } timeout && timeout > TimeSpan.Zero ? timeout : null;
    }

    protected string BaseUrl { get; }
    protected string? ApiKey { get; }
    protected string DefaultModelId { get; }

    /// <summary>协议标识（埋点与诊断使用）。</summary>
    protected abstract string ProtocolId { get; }

    /// <summary>已解析的请求地址。</summary>
    public abstract string Endpoint { get; }

    public TimeSpan? RequestTimeout => _requestTimeout;

    /// <summary>额外请求头（例如自定义网关鉴权）。</summary>
    public IDictionary<string, string> AdditionalHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>单个请求的最大尝试次数（含首次）。</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>每次请求允许的特性降级次数上限，防止网关反复拒绝时无限重发。</summary>
    private const int MaxRecoveries = 4;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    protected abstract JsonElement BuildBody(PureModelRequest request, string model, bool streaming);
    protected abstract void ApplyHeaders(HttpRequestMessage request);
    protected abstract PureModelResponse ParseResponse(JsonElement root, int statusCode, string rawBody);
    protected abstract IModelStreamParser CreateStreamParser();

    /// <summary>
    /// 网关以 4xx 拒绝了请求：子类可去掉引起拒绝的可选特性（或剥离失效的回传状态）并返回 true 立即重发。
    /// </summary>
    protected virtual bool TryRecover(ModelClientException error) => false;

    private string ResolveModel(PureModelRequest request)
    {
        var model = string.IsNullOrWhiteSpace(request.ModelId) ? DefaultModelId : request.ModelId;
        if (string.IsNullOrWhiteSpace(model))
            throw new ModelClientException(ModelClientErrorKind.InvalidRequest,
                "未配置模型：请在设置中填写端点模型或选择可用模型。");
        return model;
    }

    private HttpRequestMessage CreateRequest(PureModelRequest request, bool streaming)
    {
        var body = BuildBody(request, ResolveModel(request), streaming);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json"),
        };
        ApplyHeaders(httpRequest);
        if (streaming) httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        foreach (var header in AdditionalHeaders)
            httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return httpRequest;
    }

    public async Task<PureModelResponse> GenerateAsync(PureModelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        using var span = PerfTracer.Step("model.http", "mode", "non-streaming");
        span.Tag("api", ProtocolId);
        var recoveries = 0;
        for (var attempt = 1; ; attempt++)
        {
            using var httpRequest = CreateRequest(request, streaming: false);
            using var timeout = CreateAttemptTimeout(cancellationToken);
            var attemptToken = timeout?.Token ?? cancellationToken;
            try
            {
                using var response = await SendAsync(httpRequest, attemptToken).ConfigureAwait(false);
                span.Tag("status", (long)response.StatusCode);
                var raw = await response.Content.ReadAsStringAsync(attemptToken).ConfigureAwait(false);
                span.Tag("responseChars", (long)raw.Length);
                JsonDocument document;
                try { document = JsonDocument.Parse(raw); }
                catch (JsonException error)
                {
                    throw new ModelClientException(ModelClientErrorKind.Unknown, "模型响应不是合法 JSON。",
                        (int)response.StatusCode, errorBody: Bound(raw), innerException: error);
                }
                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var errorElement) &&
                        errorElement.ValueKind is JsonValueKind.Object or JsonValueKind.String)
                        throw ModelClientErrorMapper.FromStreamError(errorElement);
                    var parsed = ParseResponse(root, (int)response.StatusCode, raw);
                    RecordUsage(span, parsed.Usage);
                    span.Tag("toolCalls", parsed.ToolCalls.Count);
                    return parsed;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                span.Cancel();
                throw;
            }
            catch (OperationCanceledException error) when (timeout?.IsCancellationRequested == true)
            {
                span.Fail("Timeout");
                throw AttemptTimeout(error);
            }
            catch (ModelClientException error) when (error.Kind == ModelClientErrorKind.InvalidRequest &&
                                                     recoveries < MaxRecoveries && TryRecover(error))
            {
                recoveries++;
                span.Tag($"recovered{recoveries}", error.ErrorCode ?? "invalid_request");
                attempt--;
            }
            catch (Exception error) when (attempt < MaxAttempts && ModelRetryPolicy.IsRetryable(error))
            {
                span.Tag($"retry{attempt}", error.GetType().Name);
                await ModelRetryPolicy.DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                span.Fail(error.GetType().Name);
                throw;
            }
        }
    }

    public async Task<PureModelResponse> GenerateStreamingAsync(PureModelRequest request,
        ModelStreamCallbacks? callbacks = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        using var http = PerfTracer.Step("model.http", "mode", "streaming");
        http.Tag("api", ProtocolId);
        var recoveries = 0;
        for (var attempt = 1; ; attempt++)
        {
            using var httpRequest = CreateRequest(request, streaming: true);
            using var timeout = CreateAttemptTimeout(cancellationToken);
            var attemptToken = timeout?.Token ?? cancellationToken;
            var delivered = false;
            var tracked = ModelRetryPolicy.TrackDeliveries(callbacks, () => delivered = true);
            try
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                using var response = await SendAsync(httpRequest, attemptToken).ConfigureAwait(false);
                http.Tag("status", (long)response.StatusCode);
                http.Tag("ttfbMs", (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return await ReadStreamAsync(response, tracked, attemptToken, started).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested &&
                                                           timeout?.IsCancellationRequested == true)
            {
                http.Fail("Timeout");
                throw AttemptTimeout(error);
            }
            catch (OperationCanceledException)
            {
                http.Cancel();
                throw;
            }
            catch (ModelClientException error) when (!delivered && error.Kind == ModelClientErrorKind.InvalidRequest &&
                                                     recoveries < MaxRecoveries && TryRecover(error))
            {
                recoveries++;
                http.Tag($"recovered{recoveries}", error.ErrorCode ?? "invalid_request");
                attempt--;
            }
            // 瞬时故障可以重放：已交付过增量时先通知 UI 丢弃半成品。工具尚未执行，重放没有副作用。
            catch (Exception error) when (attempt < MaxAttempts && ModelRetryPolicy.IsRetryable(error))
            {
                http.Tag($"retry{attempt}", error.GetType().Name);
                if (delivered) callbacks?.OnRestart?.Invoke();
                await ModelRetryPolicy.DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            // SSE 通道持续失败但普通通道可用时，降级为一次性请求并整块回放（真实一次性投递，不伪造分片）。
            catch (Exception error) when (attempt >= MaxAttempts && ModelRetryPolicy.IsRetryable(error))
            {
                http.Tag("fallback", "non-streaming");
                if (delivered) callbacks?.OnRestart?.Invoke();
                return await FallbackToNonStreamingAsync(request, callbacks, error, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                http.Fail(error.GetType().Name);
                throw;
            }
        }
    }

    private async Task<PureModelResponse> FallbackToNonStreamingAsync(PureModelRequest request,
        ModelStreamCallbacks? callbacks, Exception streamFailure, CancellationToken cancellationToken)
    {
        PureModelResponse response;
        try { response = await GenerateAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (ModelClientException) { throw; }
        catch (Exception error)
        {
            throw new ModelClientException(ModelClientErrorKind.Transport,
                $"流式失败后降级请求也失败：{error.Message}（原始流错误：{streamFailure.Message}）", innerException: error);
        }
        if (!string.IsNullOrEmpty(response.ReasoningContent)) callbacks?.OnThinkingDelta?.Invoke(response.ReasoningContent);
        if (!string.IsNullOrEmpty(response.Text)) callbacks?.OnTextDelta?.Invoke(response.Text);
        for (var i = 0; i < response.ToolCalls.Count; i++)
        {
            var call = response.ToolCalls[i];
            callbacks?.OnToolCallChunk?.Invoke(call.Id, call.Name, call.ArgumentsJson);
            callbacks?.OnToolCallChunkIndexed?.Invoke(i, call.Id, call.Name, call.ArgumentsJson);
        }
        if (response.Usage is { } usage) callbacks?.OnUsage?.Invoke(usage);
        callbacks?.OnCompleted?.Invoke();
        return response;
    }

    private async Task<PureModelResponse> ReadStreamAsync(HttpResponseMessage response,
        ModelStreamCallbacks? callbacks, CancellationToken cancellationToken, long requestStarted)
    {
        using var streamSpan = PerfTracer.Step("model.stream");
        var parser = CreateStreamParser();
        var frames = 0;
        long firstContentMs = -1;
        var terminal = false;
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (stream.ConfigureAwait(false))
            {
                await foreach (var sse in ServerSentEvents.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
                {
                    if (sse.IsDone) { terminal = true; break; }
                    if (sse.Data.Length == 0) continue;
                    frames++;
                    parser.Completion.EventCount++;
                    if (parser.Apply(sse, callbacks)) terminal = true;
                    if (firstContentMs < 0 && parser.Completion.Primary is { } primary &&
                        (primary.TextDeltaCount > 0 || primary.ThinkingDeltaCount > 0 || primary.ToolCallChunkCount > 0))
                        firstContentMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds;
                    if (terminal) break;
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
        catch (Exception error)
        {
            streamSpan.Fail(error.GetType().Name);
            throw ModelClientErrorMapper.FromTransport(error);
        }

        streamSpan.Tag("frames", frames);
        if (firstContentMs >= 0) streamSpan.Tag("ttftMs", firstContentMs);
        if (!terminal)
        {
            // 两种协议都有明确的终止事件；缺失说明连接在中途断开，半截结果（尤其工具参数）不可信。
            streamSpan.Fail("Transport");
            throw new ModelClientException(ModelClientErrorKind.Transport,
                frames == 0 ? "流式响应在收到任何数据帧之前结束。" : "流式响应在结束事件到达前中断。");
        }
        parser.Completion.SawDone = true;
        var result = parser.Completion.ToResponse() with { ProviderState = parser.ProviderState };
        if (result.Usage is { } usage)
        {
            RecordUsage(streamSpan, usage);
            callbacks?.OnUsage?.Invoke(usage);
        }
        callbacks?.OnCompleted?.Invoke();
        return result;
    }

    private static void RecordUsage(PerfSpan span, PureUsage? usage)
    {
        if (usage is not { IsEmpty: false }) return;
        if (usage.InputTokens is { } input) span.Tag("promptTokens", input);
        if (usage.OutputTokens is { } output) span.Tag("completionTokens", output);
        if (usage.CacheReadTokens is { } cached) span.Tag("cachedTokens", cached);
        if (usage.CacheWriteTokens is { } written) span.Tag("cacheWriteTokens", written);
    }

    private CancellationTokenSource? CreateAttemptTimeout(CancellationToken cancellationToken)
    {
        if (_requestTimeout is not { } duration) return null;
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(duration);
        return timeout;
    }

    private TimeoutException AttemptTimeout(OperationCanceledException error) =>
        new($"模型请求超过配置的 {_requestTimeout!.Value.TotalSeconds:0.#} 秒超时。", error);

    /// <summary>发送 HTTP 请求。virtual 供测试注入脚本化响应。</summary>
    protected virtual async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelClientException(ModelClientErrorKind.Transport, "模型请求被 HTTP 客户端超时取消。", innerException: error);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw ModelClientErrorMapper.FromTransport(error); }

        if (response.IsSuccessStatusCode) return response;
        try
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw ModelClientErrorMapper.FromStatus(response.StatusCode, errorBody);
        }
        finally { response.Dispose(); }
    }

    protected static string Bound(string body) =>
        body.Length <= ModelClientErrorMapper.MaxErrorBodyLength ? body : body[..ModelClientErrorMapper.MaxErrorBodyLength] + "…";

    /// <summary>工具参数 Schema：与 Chat Completions 客户端共用解析缓存。</summary>
    protected static JsonElement ToolSchema(PureToolDefinition definition) =>
        OpenAiCompatibleModelClient.ParseToolSchema(definition.Name, definition.ParametersJsonSchema);

    /// <summary>本轮声明的工具：按名称去重排序（稳定顺序有利于提示缓存），未提供定义时用内置定义。</summary>
    protected static IReadOnlyList<PureToolDefinition> ResolveTools(PureModelRequest request)
    {
        var provided = new Dictionary<string, PureToolDefinition>(StringComparer.Ordinal);
        foreach (var definition in request.ToolDefinitions ?? Array.Empty<PureToolDefinition>())
            provided.TryAdd(definition.Name, definition);
        var builtins = new Dictionary<string, PureToolDefinition>(StringComparer.Ordinal);
        foreach (var definition in ProductionToolDispatcher.GetBuiltinToolDefinitions())
            builtins.TryAdd(definition.Name, definition);
        var result = new List<PureToolDefinition>();
        foreach (var name in new SortedSet<string>(request.ToolNames, StringComparer.Ordinal))
        {
            if (provided.TryGetValue(name, out var definition) || builtins.TryGetValue(name, out definition))
                result.Add(definition);
            else
                result.Add(new PureToolDefinition(name, $"自定义工具 {name}", """{"type":"object"}"""));
        }
        return result;
    }

    /// <summary>非空字符串（流事件里的可选字段）。</summary>
    protected static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    protected static long? Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
