using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Diagnostics;

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
    private int? _maxIterations;
    private readonly bool _streamingEnabled;

    /// <summary>
    /// 可选的模型调用轮次预算。null 表示由模型终态、取消、超时或外部宿主结束。
    /// 这是测试/无人值守脚本的显式保险丝，不是交互主会话的默认终止条件。
    /// </summary>
    public int? MaxIterations
    {
        get => _maxIterations;
        set => _maxIterations = value is > 0 ? value : null;
    }

    /// <summary>通过增量回调观察模型的文本与工具分片（默认关闭，仅走一次性请求）。</summary>
    public bool StreamingEnabled => _streamingEnabled;

    /// <summary>已完成的一次模型请求次数（含无工具调用的终态轮）。</summary>
    public int CompletedModelCalls { get; private set; }

    public NativeAgentLoop(
        IPureModelClient modelClient,
        IPureToolDispatcher? toolDispatcher = null,
        int? maxIterations = null,
        bool enableStreaming = true)
    {
        _modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        _toolDispatcher = toolDispatcher;
        _maxIterations = maxIterations is > 0 ? maxIterations : null;
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
        string? reasoningEffort = null,
        Func<CancellationToken, Task>? prepareRequest = null,
        Action<PureUsage>? onModelUsage = null,
        Func<CancellationToken, Task<bool>>? recoverFromContextOverflow = null,
        Func<PermissionMode>? currentMode = null)
    {
        ArgumentNullException.ThrowIfNull(conversationHistory);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(toolNames);
        // 固定本回合声明，避免请求在途或审批期间调用方修改集合扩展执行权限。
        toolNames = Array.AsReadOnly(toolNames.ToArray());
        var declaredToolNames = new HashSet<string>(toolNames, StringComparer.Ordinal);
        toolDefinitions = toolDefinitions is null ? null : Array.AsReadOnly(toolDefinitions.ToArray());

        var allToolResults = new List<PureToolResult>();
        PureUsage? lastUsage = null;
        var iterations = 0;

        while (true)
        {
            iterations++;
            cancellationToken.ThrowIfCancellationRequested();
            using var iterationSpan = PerfTracer.Step("agent.iteration", "n", PerfTracer.Invariant(iterations));

            if (_maxIterations is { } budget && iterations > budget)
            {
                var budgetText = $"[外部任务轮次预算已耗尽（{budget} 轮）。当前执行在工具调用后停止，" +
                                 "结果可能未完成；请检查已完成的工具结果后继续。]";
                var budgetResponse = PureModelResponse.FromText(budgetText);
                var budgetMsg = new PureAgentMessage(PureAgentRole.Assistant, budgetText);
                conversationHistory.Add(budgetMsg);
                onMessageAppended?.Invoke(budgetMsg);
                return new PureAgentTurnResult(PureAgentTurnStatus.Completed, budgetResponse, allToolResults,
                    "外部任务轮次预算已耗尽", lastUsage, Truncated: true);
            }

            // 交互主会话不注入人为的预算提示，也不在最后一轮禁用工具。
            // 没有显式预算时只受取消、模型错误、工具超时和模型自身终态控制。
            // Steer：在下一轮模型请求之前注入用户插队消息。
            if (takeSteering?.Invoke() is { Length: > 0 } steering)
            {
                var steerMsg = new PureAgentMessage(PureAgentRole.User, steering);
                conversationHistory.Add(steerMsg);
                onMessageAppended?.Invoke(steerMsg);
            }

            // 每轮请求前给宿主一次准备机会（上下文压缩等）。必须在历史写入之后、
            // 请求序列化之前执行，且不能改变本轮已经追加的用户/工具消息语义。
            if (prepareRequest is not null)
                await prepareRequest(cancellationToken).ConfigureAwait(false);

            // 直接引用实时历史列表发送请求；所有变更都发生在 await 之后、请求序列化之前，
            // 因此模型客户端看到的是一致的前缀（回合闸门保证无并发写者）。
            var request = new PureModelRequest(conversationHistory, toolNames, modelId, toolDefinitions, reasoningEffort);
            PerfContextEstimate.Record(request);
            PureModelResponse response;
            try
            {
                using (var modelSpan = PerfTracer.Step("model.request", "tools", PerfTracer.Invariant(toolNames.Count)))
                {
                    modelSpan.Tag("model", modelId);
                    modelSpan.Tag("streaming", _streamingEnabled);
                    modelSpan.Tag("historyMessages", conversationHistory.Count);
                    modelSpan.PublishProgress();
                    try
                    {
                        response = await RequestModelAsync(request, streamCallbacks, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { modelSpan.Cancel(); throw; }
                    catch (Exception error) { modelSpan.Fail(error.GetType().Name); throw; }
                }
                CompletedModelCalls++;
                lastUsage = response.Usage ?? lastUsage;
                if (response.Usage is { IsEmpty: false } usage) onModelUsage?.Invoke(usage);
                iterationSpan.Tag("toolCalls", response.ToolCalls.Count);
            }
            catch (OperationCanceledException)
            {
                iterationSpan.Cancel();
                throw;
            }
            catch (ModelClientException ex) when (recoverFromContextOverflow is not null &&
                                                  IsContextOverflow(ex))
            {
                // 上下文超限是可恢复错误：压缩一次后重试本轮，而不是把长任务判死。
                iterationSpan.Tag("contextOverflow", true);
                if (await recoverFromContextOverflow(cancellationToken).ConfigureAwait(false))
                    continue;
                return new PureAgentTurnResult(
                    PureAgentTurnStatus.Failed,
                    new PureModelResponse(null, Array.Empty<PureToolCall>()),
                    allToolResults,
                    ex.Message);
            }
            catch (Exception ex)
            {
                return new PureAgentTurnResult(
                    PureAgentTurnStatus.Failed,
                    new PureModelResponse(null, Array.Empty<PureToolCall>()),
                    allToolResults,
                    ex.Message);
            }

            // 1. 没有工具调用才表示模型给出了终态回复。
            // 显式预算在本轮仍允许工具调用；下一次迭代会返回预算耗尽状态，
            // 不把未执行的工具请求伪装成完成。
            if (response.ToolCalls.Count == 0)
            {
                var finalText = response.Text ?? "";
                var finalMsg = new PureAgentMessage(PureAgentRole.Assistant, finalText,
                    Usage: response.Usage) { ReasoningContent = response.ReasoningContent };
                conversationHistory.Add(finalMsg);
                onMessageAppended?.Invoke(finalMsg);
                return new PureAgentTurnResult(PureAgentTurnStatus.Completed,
                    response with { Text = finalText },
                    allToolResults,
                    Usage: lastUsage);
            }

            // 2. 模型请求调用工具：先将带有 ToolCalls 的 Assistant 消息入库
            var assistantToolMsg = new PureAgentMessage(
                PureAgentRole.Assistant,
                response.Text ?? "",
                ToolCalls: response.ToolCalls,
                Usage: response.Usage) { ReasoningContent = response.ReasoningContent };
            conversationHistory.Add(assistantToolMsg);
            onMessageAppended?.Invoke(assistantToolMsg);

            // 3. 执行工具调用：只读安全工具并发并行执行，写操作独占串行执行，结果严格保序回填
            //    索引遍历响应中的调用快照；历史在循环中追加不影响正在遍历的 callList。
            if (_toolDispatcher is null)
            {
                foreach (var call in response.ToolCalls)
                {
                    var failRes = new PureToolResult(call.Id, false, "当前未配置工具调度器，无法执行工具。");
                    allToolResults.Add(failRes);
                    onToolCompleted?.Invoke(call, failRes);
                    var failMsg = new PureAgentMessage(PureAgentRole.Tool, failRes.Content, ToolCallId: call.Id);
                    conversationHistory.Add(failMsg);
                    onMessageAppended?.Invoke(failMsg);
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

                PureToolResult? Denied(PureToolCall call)
                {
                    if (!declaredToolNames.Contains(call.Name))
                        return new PureToolResult(call.Id, false,
                            $"<tool_use_error>本次请求未声明工具 '{call.Name}'。</tool_use_error>");
                    if (currentMode?.Invoke() == PermissionMode.Plan &&
                        !ProductionToolDispatcher.IsPlanSafeTool(call.Name))
                        return new PureToolResult(call.Id, false,
                            $"<tool_use_error>Plan 模式禁止执行工具 '{call.Name}'。</tool_use_error>");
                    return null;
                }

                async Task<PureToolResult> ExecuteOneAsync(PureToolCall call)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Denied(call) is { } denied) return denied;
                    var needsApproval = !ProductionToolDispatcher.IsConcurrencySafe(call.Name) &&
                                        !string.Equals(call.Name, "update_plan", StringComparison.Ordinal);
                    using var toolSpan = PerfTracer.Step("tool", "tool", call.Name);
                    if (_toolDispatcher is Security.SecurityToolDispatcher security)
                        return await security.ExecuteCheckedAsync(call, approvalGate, needsApproval,
                            () => Denied(call), cancellationToken).ConfigureAwait(false);
                    if (approvalGate is not null && needsApproval)
                    {
                        bool approved;
                        using (var approvalSpan = PerfTracer.Step("tool.approval", "tool", call.Name))
                        {
                            approvalSpan.Tag("safe", ProductionToolDispatcher.IsConcurrencySafe(call.Name));
                            approved = await approvalGate(call, cancellationToken).ConfigureAwait(false);
                            approvalSpan.Tag("approved", approved);
                            if (!approved) approvalSpan.Cancel();
                        }
                        if (!approved)
                        {
                            toolSpan.Fail("approval_denied");
                            toolSpan.Tag("ok", false);
                            return new PureToolResult(call.Id, false,
                                "<tool_use_error>用户拒绝了本次工具调用。</tool_use_error>");
                        }
                    }

                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // 审批等待期间模式可能已经切换；所有外层路由都必须重新过门。
                        if (Denied(call) is { } changed) return changed;
                        var result = await _toolDispatcher.DispatchAsync(call, cancellationToken).ConfigureAwait(false);
                        toolSpan.Tag("ok", result.Success);
                        toolSpan.Tag("outputChars", result.Content?.Length ?? 0);
                        if (!result.Success) toolSpan.Fail("tool_failed");
                        return result;
                    }
                    catch (OperationCanceledException) { toolSpan.Cancel(); throw; }
                    catch (Exception ex)
                    {
                        toolSpan.Fail(ex.GetType().Name);
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
                                using var batchSpan = PerfTracer.Step("tool.batch", "count", PerfTracer.Invariant(batch.Count));
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
                    // 同一消息也必须经 onMessageAppended 通知会话：RoundRecorder 借此关闭
                    // 等待中的工具消息，否则取消后会话无法开始新的助手消息。
                    foreach (var pending in callList)
                    {
                        if (completed.Contains(pending.Id)) continue;
                        var cancelled = new PureToolResult(pending.Id, false,
                            "<tool_use_error>canceled before completion</tool_use_error>");
                        allToolResults.Add(cancelled);
                        // 不调用 onToolCompleted：UI 状态由会话终态标记为 Cancelled，
                        // 这里只补上协议所需的 tool_result 配对。
                        var cancelledMsg = new PureAgentMessage(PureAgentRole.Tool, cancelled.Content,
                            ToolCallId: pending.Id);
                        conversationHistory.Add(cancelledMsg);
                        onMessageAppended?.Invoke(cancelledMsg);
                    }
                    throw;
                }
            }

            // 4. 结果已全部按序回填，继续进入下一轮循环，再次请求模型！
        }
    }

    /// <summary>
    /// 判断模型错误是否为“上下文超限”。不同网关的措辞不一致，因此同时看错误码、
    /// 错误体与消息，但必须命中上下文/长度语义，不能把所有 400 都当成可压缩恢复。
    /// </summary>
    public static bool IsContextOverflow(ModelClientException error)
    {
        if (error.Kind is not (ModelClientErrorKind.InvalidRequest or ModelClientErrorKind.Unknown or
            ModelClientErrorKind.StreamError or ModelClientErrorKind.ServerError)) return false;

        var text = string.Join(' ',
            error.ErrorCode ?? "", error.ErrorBody ?? "", error.Message ?? "");
        if (text.Length == 0) return false;

        return text.Contains("context_length", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("context length", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("maximum context", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("context window", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("too many tokens", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("input is too long", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("上下文长度", StringComparison.Ordinal) ||
               text.Contains("上下文超", StringComparison.Ordinal) ||
               text.Contains("超出上下文", StringComparison.Ordinal);
    }

    /// <summary>测试可见的裁剪入口：验证工具层输出不会被循环层二次截断。</summary>
    public static string PruneForTest(string content) => PruneToolResult(content);

    private static string PruneToolResult(string content)
    {
        const int headBudget = 48 * 1024;
        const int tailBudget = 16 * 1024;
        const int maxChars = headBudget + tailBudget;
        if (string.IsNullOrEmpty(content) || content.Length <= maxChars) return content;

        // 按行边界裁剪：固定字符偏移会把一行/一个 token 切成两半，
        // 模型看到的是无法解析的残片。只有在没有换行（超长单行）时才退回硬切。
        var headEnd = content.LastIndexOf('\n', headBudget - 1);
        if (headEnd < headBudget / 2) headEnd = headBudget;

        var tailSearchFrom = content.Length - tailBudget;
        var tailStart = content.IndexOf('\n', tailSearchFrom);
        tailStart = tailStart < 0 ? tailSearchFrom : tailStart + 1;

        if (tailStart <= headEnd) return content[..maxChars];

        var head = content[..headEnd];
        var tail = content[tailStart..];
        var omitted = content.Length - head.Length - tail.Length;
        return $"{head}\n\n... [工具输出过长，已截断折叠 {omitted} 字符以节省上下文；" +
               $"需要完整内容请用 offset/limit 分段读取] ...\n\n{tail}";
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
