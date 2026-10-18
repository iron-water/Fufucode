using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Backend;

/// <summary>
/// 纯 C# 原生 Agent 执行循环（Agent Loop 状态机）：
/// 对标 Claude Code / ClawSharp 的多轮工具迭代架构。
/// 循环执行：[请求模型] -> [调度工具] -> [回填结果入上下文] -> [再次请求模型]，直至终态或最大轮次。
/// </summary>
public sealed class NativeAgentLoop
{
    private readonly IPureModelClient _modelClient;
    private readonly IPureToolDispatcher? _toolDispatcher;
    private readonly int _maxIterations;
    private readonly bool _streamingEnabled;

    /// <summary>通过增量回调观察模型的文本与工具分片（默认关闭，仅走一次性请求）。</summary>
    public bool StreamingEnabled => _streamingEnabled;

    /// <summary>已完成的一次模型请求次数（含无工具调用的终态轮）。</summary>
    public int CompletedModelCalls { get; private set; }

    public NativeAgentLoop(
        IPureModelClient modelClient,
        IPureToolDispatcher? toolDispatcher = null,
        int maxIterations = 10,
        bool enableStreaming = true)
    {
        _modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        _toolDispatcher = toolDispatcher;
        _maxIterations = maxIterations > 0 ? maxIterations : 10;
        // 只有客户端真正声明支持流式时才启用流式路径，避免对旧的 IPureModelClient 实现在运行时失败。
        _streamingEnabled = enableStreaming && modelClient is IStreamingModelClient;
    }

    public async Task<PureAgentTurnResult> ExecuteLoopAsync(
        List<PureAgentMessage> conversationHistory,
        IReadOnlyList<string> toolNames,
        string? modelId,
        Action<PureAgentMessage>? onMessageAppended = null,
        CancellationToken cancellationToken = default)
        => await ExecuteLoopAsync(
            conversationHistory,
            toolNames,
            modelId,
            onMessageAppended,
            streamCallbacks: null,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 执行多轮 Agent Loop，并把流式增量回调透传给支持流式的模型客户端：
    /// 每轮的 delta.content 与 delta.tool_calls 分片在请求过程中实时派发，
    /// 轮次结束后仍按「工具结果回填 → 再次请求模型」推进，直至终态或有界停止。
    /// </summary>
    public async Task<PureAgentTurnResult> ExecuteLoopAsync(
        List<PureAgentMessage> conversationHistory,
        IReadOnlyList<string> toolNames,
        string? modelId,
        Action<PureAgentMessage>? onMessageAppended,
        ModelStreamCallbacks? streamCallbacks,
        CancellationToken cancellationToken = default,
        Action<PureToolCall>? onToolStarting = null,
        Action<PureToolCall, PureToolResult>? onToolCompleted = null,
        IReadOnlyList<PureToolDefinition>? toolDefinitions = null,
        Func<PureToolCall, CancellationToken, Task<bool>>? approvalGate = null,
        Func<string?>? takeSteering = null,
        string? reasoningEffort = null)
    {
        ArgumentNullException.ThrowIfNull(conversationHistory);
        cancellationToken.ThrowIfCancellationRequested();

        var allToolResults = new List<PureToolResult>();
        PureUsage? lastUsage = null;
        var iterations = 0;

        while (true)
        {
            iterations++;
            cancellationToken.ThrowIfCancellationRequested();

            if (iterations > _maxIterations)
            {
                var timeoutResponse = PureModelResponse.FromText($"[Agent Loop 已达到最大轮次限制（{_maxIterations} 轮）并安全终止。]");
                conversationHistory.Add(new PureAgentMessage(PureAgentRole.Assistant, timeoutResponse.Text!));
                onMessageAppended?.Invoke(conversationHistory[^1]);
                return new PureAgentTurnResult(PureAgentTurnStatus.Completed, timeoutResponse, allToolResults,
                    "已达到最大轮次限制", lastUsage);
            }

            // Steer：在下一轮模型请求之前注入用户插队消息。
            if (takeSteering?.Invoke() is { Length: > 0 } steering)
            {
                var steerMsg = new PureAgentMessage(PureAgentRole.User, steering);
                conversationHistory.Add(steerMsg);
                onMessageAppended?.Invoke(steerMsg);
            }

            var request = new PureModelRequest(conversationHistory, toolNames, modelId, toolDefinitions,
                reasoningEffort);
            PureModelResponse response;
            try
            {
                response = await RequestModelAsync(request, streamCallbacks, cancellationToken).ConfigureAwait(false);
                CompletedModelCalls++;
                lastUsage = response.Usage ?? lastUsage;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new PureAgentTurnResult(
                    PureAgentTurnStatus.Failed,
                    new PureModelResponse(null, Array.Empty<PureToolCall>()),
                    allToolResults,
                    ex.Message);
            }

            // 1. 若无工具调用，说明模型已给出终态回复
            if (response.ToolCalls.Count == 0)
            {
                var finalMsg = new PureAgentMessage(PureAgentRole.Assistant, response.Text ?? "",
                    Usage: response.Usage);
                conversationHistory.Add(finalMsg);
                onMessageAppended?.Invoke(finalMsg);
                return new PureAgentTurnResult(PureAgentTurnStatus.Completed, response, allToolResults,
                    Usage: lastUsage);
            }

            // 2. 模型请求调用工具：先将带有 ToolCalls 的 Assistant 消息入库
            var assistantToolMsg = new PureAgentMessage(
                PureAgentRole.Assistant,
                response.Text ?? "",
                ToolCalls: response.ToolCalls,
                Usage: response.Usage);
            conversationHistory.Add(assistantToolMsg);
            onMessageAppended?.Invoke(assistantToolMsg);

            // 3. 执行工具调用：只读安全工具并发并行执行，写操作独占串行执行，结果严格保序回填
            if (_toolDispatcher is null)
            {
                foreach (var call in response.ToolCalls)
                {
                    var failRes = new PureToolResult(call.Id, false, "当前未配置工具调度器，无法执行工具。");
                    allToolResults.Add(failRes);
                    onToolCompleted?.Invoke(call, failRes);
                    conversationHistory.Add(new PureAgentMessage(PureAgentRole.Tool, failRes.Content, ToolCallId: call.Id));
                    onMessageAppended?.Invoke(conversationHistory[^1]);
                }
            }
            else
            {
                var callList = response.ToolCalls;
                var completed = new HashSet<string>(StringComparer.Ordinal);

                void Record(PureToolCall call, PureToolResult result)
                {
                    completed.Add(call.Id);
                    allToolResults.Add(result);
                    onToolCompleted?.Invoke(call, result);
                    var toolMsg = new PureAgentMessage(PureAgentRole.Tool, PruneToolResult(result.Content),
                        ToolCallId: call.Id);
                    conversationHistory.Add(toolMsg);
                    onMessageAppended?.Invoke(toolMsg);
                }

                async Task<PureToolResult> ExecuteOneAsync(PureToolCall call)
                {
                    var needsApproval = !ProductionToolDispatcher.IsConcurrencySafe(call.Name) &&
                                        !string.Equals(call.Name, "update_plan", StringComparison.Ordinal);
                    if (approvalGate is not null && needsApproval)
                    {
                        var approved = await approvalGate(call, cancellationToken).ConfigureAwait(false);
                        if (!approved)
                            return new PureToolResult(call.Id, false,
                                "<tool_use_error>用户拒绝了本次工具调用。</tool_use_error>");
                    }

                    try
                    {
                        return await _toolDispatcher.DispatchAsync(call, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        return new PureToolResult(call.Id, false,
                            $"<tool_use_error>工具执行异常: {ex.Message}</tool_use_error>");
                    }
                }

                try
                {
                    var i = 0;
                    while (i < callList.Count)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var call = callList[i];
                        if (ProductionToolDispatcher.IsConcurrencySafe(call.Name))
                        {
                            var batch = new List<PureToolCall> { call };
                            while (i + 1 < callList.Count &&
                                   ProductionToolDispatcher.IsConcurrencySafe(callList[i + 1].Name))
                            {
                                i++;
                                batch.Add(callList[i]);
                            }

                            if (batch.Count == 1)
                            {
                                onToolStarting?.Invoke(call);
                                Record(call, await ExecuteOneAsync(call).ConfigureAwait(false));
                            }
                            else
                            {
                                foreach (var b in batch) onToolStarting?.Invoke(b);
                                var results = await Task.WhenAll(batch.Select(ExecuteOneAsync)).ConfigureAwait(false);
                                for (var j = 0; j < batch.Count; j++) Record(batch[j], results[j]);
                            }
                        }
                        else
                        {
                            onToolStarting?.Invoke(call);
                            Record(call, await ExecuteOneAsync(call).ConfigureAwait(false));
                        }

                        i++;
                    }
                }
                catch (OperationCanceledException)
                {
                    // 中断时仍未完成的调用必须补一条合成 tool_result，
                    // 否则 assistant(tool_calls) 后面缺少配对的 tool 消息，下一次请求会被 API 拒绝。
                    foreach (var pending in callList)
                    {
                        if (completed.Contains(pending.Id)) continue;
                        var cancelled = new PureToolResult(pending.Id, false,
                            "<tool_use_error>canceled before completion</tool_use_error>");
                        allToolResults.Add(cancelled);
                        // 不调用 onToolCompleted：UI 状态由会话终态标记为 Cancelled，
                        // 这里只补上协议所需的 tool_result 配对。
                        conversationHistory.Add(new PureAgentMessage(PureAgentRole.Tool, cancelled.Content,
                            ToolCallId: pending.Id));
                    }
                    throw;
                }
            }

            // 4. 结果已全部按序回填，继续进入下一轮循环，再次请求模型！
        }
    }

    private static string PruneToolResult(string content)
    {
        const int maxChars = 12 * 1024;
        if (string.IsNullOrEmpty(content) || content.Length <= maxChars) return content;

        var head = content.Substring(0, 6 * 1024);
        var tail = content.Substring(content.Length - 3 * 1024, 3 * 1024);
        var omitted = content.Length - (9 * 1024);
        return $"{head}\n\n... [工具输出过长，已截断折叠 {omitted} 字符以节省上下文] ...\n\n{tail}";
    }

    /// <summary>
    /// 单次模型请求：客户端支持流式且未显式关闭时走 SSE 流式路径并透传回调，
    /// 否则退回一次性请求（回调静默跳过，不伪造增量）。
    /// </summary>
    private Task<PureModelResponse> RequestModelAsync(
        PureModelRequest request,
        ModelStreamCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        if (_streamingEnabled && _modelClient is IStreamingModelClient streaming)
        {
            return streaming.GenerateStreamingAsync(request, callbacks, cancellationToken);
        }

        if (callbacks is not null)
        {
            // 非流式客户端（不实现 IStreamingModelClient）：没有真实增量可分片。
            // 这里把完整文本一次性派发，使 UI 至少能显示内容——它是「一次性投递」，
            // 不是真实流式增量；工具调用同理按最终结果整条派发，不伪造分片边界。
            return GenerateNonStreamingWithTextDeltaAsync(request, callbacks, cancellationToken);
        }

        return _modelClient.GenerateAsync(request, cancellationToken);
    }

    private async Task<PureModelResponse> GenerateNonStreamingWithTextDeltaAsync(
        PureModelRequest request,
        ModelStreamCallbacks callbacks,
        CancellationToken cancellationToken)
    {
        var response = await _modelClient.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(response.Text))
        {
            callbacks.OnTextDelta?.Invoke(response.Text);
        }
        if (response.ToolCalls.Count > 0)
        {
            for (var i = 0; i < response.ToolCalls.Count; i++)
            {
                var call = response.ToolCalls[i];
                callbacks.OnToolCallChunk?.Invoke(call.Id, call.Name, call.ArgumentsJson);
                callbacks.OnToolCallChunkIndexed?.Invoke(i, call.Id, call.Name, call.ArgumentsJson);
            }
        }
        callbacks.OnCompleted?.Invoke();
        return response;
    }
}
