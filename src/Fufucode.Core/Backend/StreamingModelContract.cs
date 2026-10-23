using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Backend;

/// <summary>流式增量文本块回调：delta 为本次新增文本（可能为空串）。</summary>
public delegate void TextDeltaHandler(string delta);

/// <summary>
/// 流式工具调用分片回调：同一 <paramref name="index"/> 的多次回调需要按顺序拼接 <paramref name="argumentsFragment"/>。
/// <paramref name="id"/> 与 <paramref name="name"/> 仅在分片首次携带时非空。
/// </summary>
public delegate void ToolCallChunkHandler(int index, string? id, string? name, string argumentsFragment);

/// <summary>
/// 流式消费回调集合。默认全部为 null，表示调用方只关心最终聚合结果。
/// </summary>
public sealed class ModelStreamCallbacks
{
    /// <summary>增量文本回调（对应 OpenAI delta.content）。</summary>
    public TextDeltaHandler? OnTextDelta { get; init; }

    /// <summary>增量思考/推理回调（对应 DeepSeek delta.reasoning_content 或 delta.thought）。</summary>
    public TextDeltaHandler? OnThinkingDelta { get; init; }

    /// <summary>
    /// 流式工具调用分片回调（对应 OpenAI delta.tool_calls）。
    /// 参数按任务约定顺序为：id、name、argsFragment。
    /// </summary>
    public Action<string, string, string>? OnToolCallChunk { get; init; }

    /// <summary>带分片索引的增强回调；与 <see cref="OnToolCallChunk"/> 可同时使用。</summary>
    public ToolCallChunkHandler? OnToolCallChunkIndexed { get; init; }

    /// <summary>用量帧回调（网关在流中返回 usage 时触发）。</summary>
    public Action<PureUsage>? OnUsage { get; init; }

    /// <summary>流结束（收到 data: [DONE]）回调，用于 UI 收尾。</summary>
    public Action? OnCompleted { get; init; }

    /// <summary>
    /// 已输出的流因瞬时故障被丢弃、即将从头重试时触发。
    /// UI 应关闭当前半成品消息并从新消息继续，避免把重试增量拼到旧文本后面。
    /// </summary>
    public Action? OnRestart { get; init; }

    /// <summary>空回调集合。</summary>
    public static ModelStreamCallbacks None { get; } = new();
}

/// <summary>
/// 支持真实 Server-Sent Events 流式响应的模型客户端契约。
/// 实现方必须：增量解析 data 行、按 index 拼装工具调用分片、在 data: [DONE] 处结束、
/// 并在非 2xx 时抛出 <see cref="ModelClientException"/>。
/// </summary>
public interface IStreamingModelClient
{
    /// <summary>
    /// 以 stream=true 发起请求并增量消费 SSE；返回值是完整聚合后的响应（文本与已拼装的工具调用）。
    /// </summary>
    Task<PureModelResponse> GenerateStreamingAsync(
        PureModelRequest request,
        ModelStreamCallbacks? callbacks = null,
        CancellationToken cancellationToken = default);
}

/// <summary>拼装完成的流式工具调用（与 <see cref="PureToolCall"/> 对应的分片聚合结果）。</summary>
public sealed record StreamedToolCall(int Index, string Id, string Name, string ArgumentsJson);

/// <summary>
/// 单个 choice 的流式增量聚合结果：文本、工具调用分片与结束原因。
/// </summary>
public sealed class StreamedChoice
{
    public StreamedChoice(int index) => Index = index;

    /// <summary>choice 在响应中的下标。</summary>
    public int Index { get; }

    /// <summary>聚合后的完整文本（各 delta.content 顺序拼接）。</summary>
    public string Text => _text.ToString();

    /// <summary>结束原因（如 stop / tool_calls），未收到时为 null。</summary>
    public string? FinishReason { get; private set; }

    private readonly System.Text.StringBuilder _text = new();
    private readonly System.Text.StringBuilder _thinking = new();
    private readonly Dictionary<int, ToolCallBuilder> _toolCalls = new();

    /// <summary>聚合后的完整思考/推理文本。</summary>
    public string Thinking => _thinking.ToString();

    /// <summary>已收到的增量思考块数量。</summary>
    public int ThinkingDeltaCount { get; private set; }

    /// <summary>已收到的增量文本块数量（用于断言真实分片而非单块回放）。</summary>
    public int TextDeltaCount { get; private set; }

    /// <summary>已收到的工具调用分片数量。</summary>
    public int ToolCallChunkCount { get; private set; }

    internal void AppendThinking(string delta)
    {
        if (delta.Length == 0) return;
        _thinking.Append(delta);
        ThinkingDeltaCount++;
    }

    internal void AppendText(string delta)
    {
        if (delta.Length == 0) return;
        _text.Append(delta);
        TextDeltaCount++;
    }

    internal void SetFinishReason(string? reason)
    {
        if (!string.IsNullOrEmpty(reason)) FinishReason = reason;
    }

    internal void AppendToolCallChunk(int index, string? id, string? name, string? argumentsFragment)
    {
        ToolCallChunkCount++;
        if (!_toolCalls.TryGetValue(index, out var builder))
        {
            builder = new ToolCallBuilder(index);
            _toolCalls[index] = builder;
        }

        builder.Append(id, name, argumentsFragment ?? "");
    }

    /// <summary>快照聚合结果：订单号按分片 index 升序，参数为各分片顺序拼接。</summary>
    public IReadOnlyList<StreamedToolCall> ToolCalls
    {
        get
        {
            var indexes = new List<int>(_toolCalls.Keys);
            indexes.Sort();
            var result = new List<StreamedToolCall>(indexes.Count);
            foreach (var index in indexes)
            {
                result.Add(_toolCalls[index].Build());
            }
            return result;
        }
    }

    private sealed class ToolCallBuilder(int index)
    {
        private readonly System.Text.StringBuilder _arguments = new();
        private string? _id;
        private string? _name;

        internal void Append(string? id, string? name, string argumentsFragment)
        {
            // 供应商通常在首个分片给出 id/name，后续分片为空；重复出现的同值不覆盖、不同值不静默改写。
            if (!string.IsNullOrEmpty(id) && string.IsNullOrEmpty(_id)) _id = id;
            if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(_name)) _name = name;
            _arguments.Append(argumentsFragment);
        }

        internal StreamedToolCall Build()
        {
            var arguments = _arguments.ToString();
            if (arguments.Length == 0) arguments = "{}";
            return new StreamedToolCall(
                index,
                _id ?? $"call_{index}",
                _name ?? "",
                arguments);
        }
    }
}

/// <summary>
/// 整次流式响应的聚合快照：按 choice 下标区分，供调用方取最终结果。
/// </summary>
public sealed class StreamedCompletion
{
    private readonly Dictionary<int, StreamedChoice> _choices = new();

    internal StreamedChoice Choice(int index)
    {
        if (!_choices.TryGetValue(index, out var choice))
        {
            choice = new StreamedChoice(index);
            _choices[index] = choice;
        }
        return choice;
    }

    /// <summary>已收到的 choice 数量。</summary>
    public int ChoiceCount => _choices.Count;

    /// <summary>已收到的原始 SSE data 事件数量（含非 choice 事件）。</summary>
    public int EventCount { get; internal set; }

    /// <summary>是否已收到 data: [DONE]。</summary>
    public bool SawDone { get; internal set; }

    /// <summary>模型标识（响应体中的 model 字段，存在时）。</summary>
    public string? Model { get; internal set; }

    /// <summary>最近一次收到的 usage 帧（未提供时为 null）。</summary>
    public PureUsage? Usage { get; internal set; }

    /// <summary>主 choice：优先下标 0，其次最小的下标。</summary>
    public StreamedChoice? Primary
    {
        get
        {
            if (_choices.Count == 0) return null;
            if (_choices.TryGetValue(0, out var zero)) return zero;
            var indexes = new List<int>(_choices.Keys);
            indexes.Sort();
            return _choices[indexes[0]];
        }
    }

    /// <summary>聚合为内核使用的模型响应。</summary>
    public PureModelResponse ToResponse()
    {
        var primary = Primary;
        if (primary is null) return new PureModelResponse(null, Array.Empty<PureToolCall>());

        var calls = new List<PureToolCall>(primary.ToolCalls.Count);
        foreach (var call in primary.ToolCalls)
        {
            calls.Add(new PureToolCall(call.Id, call.Name, call.ArgumentsJson));
        }

        var text = primary.Text.Length > 0 ? primary.Text : null;
        return new PureModelResponse(text, calls, primary.FinishReason, Usage);
    }
}
