using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Fufucode.Core.Backend;

/// <summary>
/// OpenAI Chat Completions 流式增量解析器：
/// 消费一条 SSE data 载荷（单条 JSON），提取 delta.content 与 delta.tool_calls 分片，
/// 并把结果累积到 <see cref="StreamedCompletion"/>。
/// </summary>
public sealed class ChatCompletionSseParser
{
    private readonly StreamedCompletion _completion = new();
    private StreamedChoice? _choice;

    /// <summary>累积结果快照。</summary>
    public StreamedCompletion Completion => _completion;

    /// <summary>
    /// 处理一条 SSE 事件。返回 true 表示事件被识别为流式数据帧（含 delta）；
    /// 非 JSON 的数据帧（例如网关心跳 "ping"）返回 false，且不产生错误。
    /// 流内出现标准 error 对象时抛出 <see cref="ModelClientException"/>。
    /// </summary>
    public bool TryApply(ServerSentEvent sse, ModelStreamCallbacks? callbacks)
    {
        ArgumentNullException.ThrowIfNull(sse);
        if (sse.IsDone) return false;

        var data = sse.Data.TrimStart();
        if (data.Length == 0) return false;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            // 非 JSON 心跳/注释帧按忽略处理，不污染流状态。
            return false;
        }

        using (document)
        {
            _completion.EventCount++;
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (root.TryGetProperty("error", out var error) && error.ValueKind is JsonValueKind.Object or JsonValueKind.String)
            {
                throw ModelClientErrorMapper.FromStreamError(error, TryReadInt(root, "status"));
            }

            if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
            {
                _completion.Model = model.GetString();
            }

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                var parsedUsage = ReadUsage(usage);
                if (parsedUsage is not null)
                {
                    _completion.Usage = parsedUsage;
                    callbacks?.OnUsage?.Invoke(parsedUsage);
                }
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            {
                // 部分网关先发一帧只带 id/model 的角色声明，属于合法但无增量的事件。
                return false;
            }

            var applied = false;
            foreach (var choiceElement in choices.EnumerateArray())
            {
                if (choiceElement.ValueKind != JsonValueKind.Object) continue;

                var index = TryReadInt(choiceElement, "index") ?? 0;
                var choice = _completion.Choice(index);
                _choice = choice;
                applied = true;

                if (choiceElement.TryGetProperty("finish_reason", out var finish) &&
                    finish.ValueKind == JsonValueKind.String)
                {
                    choice.SetFinishReason(finish.GetString());
                }

                if (!choiceElement.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // 多 choice 响应里只把主 choice 的增量派发给 UI，避免不同候选交错显示。
                var effectiveCallbacks = index == 0 ? callbacks : null;
                ApplyThinkingDelta(delta, choice, effectiveCallbacks);
                ApplyTextDelta(delta, choice, effectiveCallbacks);
                ApplyToolCallDeltas(delta, choice, effectiveCallbacks);
            }

            return applied;
        }
    }

    private static PureUsage? ReadUsage(JsonElement usage)
    {
        var input = TryReadLong(usage, "prompt_tokens") ?? TryReadLong(usage, "input_tokens");
        var output = TryReadLong(usage, "completion_tokens") ?? TryReadLong(usage, "output_tokens");
        var total = TryReadLong(usage, "total_tokens");
        long? cacheRead = null;
        long? cacheWrite = null;

        if (usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
            cacheRead = TryReadLong(details, "cached_tokens");
        cacheRead ??= TryReadLong(usage, "cache_read_input_tokens");
        cacheWrite = TryReadLong(usage, "cache_creation_input_tokens");

        var result = new PureUsage(input, output, total, cacheRead, cacheWrite);
        return result.IsEmpty ? null : result;
    }

    private static long? TryReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var parsed) ? parsed : null;

    private static void ApplyThinkingDelta(JsonElement delta, StreamedChoice choice, ModelStreamCallbacks? callbacks)
    {
        string? thinking = null;
        if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
        {
            thinking = rc.GetString();
        }
        else if (delta.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.String)
        {
            thinking = th.GetString();
        }
        else if (delta.TryGetProperty("reasoning", out var rs) && rs.ValueKind == JsonValueKind.String)
        {
            thinking = rs.GetString();
        }

        if (string.IsNullOrEmpty(thinking)) return;

        choice.AppendThinking(thinking);
        callbacks?.OnThinkingDelta?.Invoke(thinking);
    }

    private static void ApplyTextDelta(JsonElement delta, StreamedChoice choice, ModelStreamCallbacks? callbacks)
    {
        if (!delta.TryGetProperty("content", out var content)) return;

        // 某些实现用 content 数组承载多模态块；只取文本块，避免整体解析失败。
        string? text;
        if (content.ValueKind == JsonValueKind.String)
        {
            text = content.GetString();
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            System.Text.StringBuilder? builder = null;
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object &&
                    part.TryGetProperty("text", out var partText) &&
                    partText.ValueKind == JsonValueKind.String)
                {
                    builder ??= new System.Text.StringBuilder();
                    builder.Append(partText.GetString());
                }
            }
            text = builder?.ToString();
        }
        else
        {
            return;
        }

        if (string.IsNullOrEmpty(text)) return;

        choice.AppendText(text);
        callbacks?.OnTextDelta?.Invoke(text);
    }

    private static void ApplyToolCallDeltas(JsonElement delta, StreamedChoice choice, ModelStreamCallbacks? callbacks)
    {
        if (!delta.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var fallbackIndex = 0;
        foreach (var chunk in toolCalls.EnumerateArray())
        {
            if (chunk.ValueKind != JsonValueKind.Object) continue;

            var index = TryReadInt(chunk, "index") ?? fallbackIndex;
            fallbackIndex = index + 1;

            string? id = null;
            if (chunk.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
            {
                id = idElement.GetString();
            }

            string? name = null;
            string arguments = "";
            if (chunk.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
            {
                if (function.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
                {
                    name = nameElement.GetString();
                }

                if (function.TryGetProperty("arguments", out var argsElement) &&
                    argsElement.ValueKind == JsonValueKind.String)
                {
                    arguments = argsElement.GetString() ?? "";
                }
            }

            choice.AppendToolCallChunk(index, id, name, arguments);

            // 任务约定的回调签名：id、name、argsFragment（其余分片为空串）。
            callbacks?.OnToolCallChunk?.Invoke(id ?? "", name ?? "", arguments);
            callbacks?.OnToolCallChunkIndexed?.Invoke(index, id, name, arguments);
        }
    }

    private static int? TryReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>当前主 choice 的结束原因（用于日志/断言）。</summary>
    public string? FinishReason => _choice?.FinishReason;

    /// <summary>已消费的 JSON 事件帧数量。</summary>
    public int EventCount => _completion.EventCount;
}
