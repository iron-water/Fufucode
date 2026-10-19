using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace Fufucode.Core.Backend;

/// <summary>纯 C# 模型客户端契约。</summary>
public interface IPureModelClient
{
    Task<PureModelResponse> GenerateAsync(PureModelRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// 纯 C# 原生 Agent 执行引擎实现（NATIVE-CS）：
/// 全程托管运行，零外部 Python/Node.js/RPC 进程依赖。
/// 由 NativeAgentLoop 状态机驱动多轮工具循环、结果回填与上下文维护。
/// 同一时刻只允许一个回合运行；历史通过回合级工作副本合并，读取方不会看到半写状态。
/// </summary>
public sealed class PureCSharpAgentEngine : IObservablePureCSharpAgentEngine
{
    private readonly IPureToolDispatcher? _toolDispatcher;
    public IPureToolDispatcher? ToolDispatcher => _toolDispatcher;

    private volatile PermissionMode _mode = PermissionMode.Default;
    public PermissionMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            ResolveProductionDispatcher()?.Mode = value;
        }
    }

    /// <summary>执行变更类工具前的人工审批门；null 表示不审批。</summary>
    public Func<PureToolCall, CancellationToken, Task<bool>>? ApprovalGate { get; set; }

    /// <summary>每轮模型请求之前读取的插队纠偏消息；null 表示不支持 Steer。</summary>
    public Func<string?>? TakeSteering { get; set; }

    private readonly IPureModelClient _modelClient;
    private readonly NativeAgentLoop _loop;
    private readonly List<PureAgentMessage> _history = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _turnGate = new(1, 1);

    private string? _modelId;
    private volatile bool _isRunning;

    public PureCSharpAgentEngine(
        IPureToolDispatcher? toolDispatcher = null,
        IPureModelClient? modelClient = null,
        int maxIterations = 10)
    {
        _toolDispatcher = toolDispatcher;
        // 生产路径不允许静默回退到“确定性假模型”：没有配置端点时明确失败。
        _modelClient = modelClient ?? new UnconfiguredModelClient();
        _loop = new NativeAgentLoop(_modelClient, _toolDispatcher, maxIterations);
    }

    public PureAgentState GetState()
    {
        lock (_gate)
        {
            return new PureAgentState(_modelId, _history.Count, _isRunning, _history.ToArray());
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _history.Clear();
        }
    }

    public void RestoreHistory(IReadOnlyList<PureAgentMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        lock (_gate)
        {
            if (_isRunning) throw new InvalidOperationException("运行中的回合不能被历史恢复覆盖。");
            _history.Clear();
            _history.AddRange(messages);
        }
    }

    public bool StreamingEnabled => _loop.StreamingEnabled;

    /// <summary>当前计划的结构化提示片段（由工具调度器维护）；没有计划时为 null。</summary>
    public string? CurrentPlanSnippet
    {
        get
        {
            var plan = ResolveProductionDispatcher()?.CurrentPlan;
            if (plan is not { Steps.Count: > 0 }) return null;

            var sb = new StringBuilder();
            sb.AppendLine("# 当前执行计划（Task Plan）");
            for (var i = 0; i < plan.Steps.Count; i++)
            {
                var mark = plan.Steps[i].Status switch
                {
                    "completed" => "[x]",
                    "in_progress" => "[>]",
                    _ => "[ ]"
                };
                sb.AppendLine($"{mark} 步骤 {i + 1}: {plan.Steps[i].Title} ({plan.Steps[i].Status})");
            }
            if (!string.IsNullOrEmpty(plan.Notes))
                sb.AppendLine($"备注: {plan.Notes}");
            return sb.ToString().TrimEnd();
        }
    }

    private ProductionToolDispatcher? ResolveProductionDispatcher() =>
        ResolveProductionDispatcher(_toolDispatcher);

    /// <summary>沿调度链向下找到真正的生产调度器（权限模式与计划状态都在它上面）。</summary>
    private static ProductionToolDispatcher? ResolveProductionDispatcher(IPureToolDispatcher? dispatcher) =>
        dispatcher switch
        {
            ProductionToolDispatcher production => production,
            Plugins.PluginToolRegistry registry => ResolveProductionDispatcher(registry.InnerDispatcher),
            SkillAwareDispatcher skill => ResolveProductionDispatcher(skill.Inner),
            McpGatewayDispatcher mcp => ResolveProductionDispatcher(mcp.Inner),
            SubagentDispatcher subagent => ResolveProductionDispatcher(subagent.Inner),
            NativeToolComposite composite => ResolveProductionDispatcher(composite.Builtin),
            _ => null
        };

    public Task<PureAgentTurnResult> RunTurnAsync(PureModelRequest request, CancellationToken cancellationToken = default)
        => RunTurnCoreAsync(request, null, cancellationToken);

    public Task<PureAgentTurnResult> RunObservedTurnAsync(
        PureModelRequest request,
        NativeAgentCallbacks callbacks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        return RunTurnCoreAsync(request, callbacks, cancellationToken);
    }

    private async Task<PureAgentTurnResult> RunTurnCoreAsync(
        PureModelRequest request,
        NativeAgentCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        List<PureAgentMessage>? working = null;
        try
        {
            lock (_gate)
            {
                if (_isRunning) throw new InvalidOperationException("已有回合在运行。");
                _isRunning = true;
                _modelId = request.ModelId ?? _modelId;
                foreach (var msg in request.Messages)
                {
                    if (msg.Role == PureAgentRole.System)
                    {
                        var existingIdx = _history.FindIndex(m => m.Role == PureAgentRole.System);
                        if (existingIdx >= 0) _history[existingIdx] = msg;
                        else _history.Insert(0, msg);
                    }
                    else
                    {
                        _history.Add(msg);
                    }
                }

                // 回合在副本上推进；完成后再一次性合并，GetState 不会看到并发写。
                working = new List<PureAgentMessage>(_history);
            }

            await CompactIfNeededAsync(working, request.ModelId ?? _modelId, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            return await _loop.ExecuteLoopAsync(
                working,
                request.ToolNames,
                request.ModelId,
                onMessageAppended: callbacks?.OnMessageAppended,
                streamCallbacks: callbacks?.Stream,
                cancellationToken,
                onToolStarting: callbacks?.OnToolStarting,
                onToolCompleted: callbacks?.OnToolCompleted,
                toolDefinitions: request.ToolDefinitions,
                approvalGate: ApprovalGate,
                takeSteering: TakeSteering,
                reasoningEffort: request.ReasoningEffort).ConfigureAwait(false);
        }
        finally
        {
            if (working is not null)
            {
                lock (_gate)
                {
                    _history.Clear();
                    _history.AddRange(working);
                }
            }

            _isRunning = false;
            _turnGate.Release();
        }
    }

    /// <summary>
    /// 语义上下文压缩：超过字符预算时，对较早回合调用一次模型生成结构化摘要，
    /// 保留 System 与最近消息。摘要调用失败时退回确定性说明，绝不让主回合因此失败。
    /// </summary>
    private async Task CompactIfNeededAsync(List<PureAgentMessage> history, string? modelId,
        CancellationToken cancellationToken)
    {
        const int maxChars = 400_000;
        const int keepRecent = 8;
        const int transcriptBudget = 60_000;

        var total = 0;
        foreach (var message in history)
        {
            total += message.Content?.Length ?? 0;
            if (message.ToolCalls is { Count: > 0 } calls)
                total += calls.Sum(call => call.ArgumentsJson.Length);
        }
        if (total <= maxChars || history.Count <= keepRecent + 1) return;

        var system = history.Count > 0 && history[0].Role == PureAgentRole.System ? history[0] : null;
        var firstKept = Math.Max(1, history.Count - keepRecent);
        // 不要从孤立的 tool 结果开始：它们对应的 assistant tool_call 已被折叠。
        while (firstKept < history.Count && history[firstKept].Role == PureAgentRole.Tool) firstKept++;
        if (firstKept >= history.Count) return;

        var start = system is null ? 0 : 1;
        var omitted = firstKept - start;

        var transcript = new StringBuilder();
        for (var i = start; i < firstKept && transcript.Length < transcriptBudget; i++)
        {
            var message = history[i];
            var text = message.Content ?? string.Empty;
            if (text.Length > 4000) text = text[..4000] + "…";
            transcript.Append(message.Role).Append(": ").AppendLine(text);
            if (message.ToolCalls is { Count: > 0 } calls)
            {
                foreach (var call in calls)
                    transcript.Append("  [tool] ").Append(call.Name).Append(' ')
                        .AppendLine(call.ArgumentsJson.Length > 800
                            ? call.ArgumentsJson[..800] + "…"
                            : call.ArgumentsJson);
            }
        }

        string summary;
        try
        {
            var response = await _modelClient.GenerateAsync(new PureModelRequest(
                new[]
                {
                    new PureAgentMessage(PureAgentRole.System,
                        "你是上下文压缩器。把以下对话压缩为结构化交接摘要，必须包含：" +
                        "1) 用户目标与约束（关键约束保留原文）；2) 已完成工作与涉及文件；" +
                        "3) 关键决策；4) 错误与修复；5) 待办与下一步。只输出摘要，不要评论。"),
                    new PureAgentMessage(PureAgentRole.User, transcript.ToString())
                },
                Array.Empty<string>(), modelId), cancellationToken).ConfigureAwait(false);
            summary = string.IsNullOrWhiteSpace(response.Text)
                ? $"[上下文已压缩] 早先 {omitted} 条消息已省略；如需细节请重新读取相关文件。"
                : $"[上下文摘要：早先 {omitted} 条消息已压缩]\n{response.Text}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            summary = $"[上下文已压缩] 早先 {omitted} 条消息因超出上下文预算被省略；如需细节请重新读取相关文件。";
        }

        var note = new PureAgentMessage(PureAgentRole.Assistant, summary);
        var kept = history.Skip(firstKept).ToList();
        history.Clear();
        if (system is not null) history.Add(system);
        history.Add(note);
        history.AddRange(kept);
    }

    /// <summary>未配置模型端点时的显式失败客户端；不伪造任何模型回复。</summary>
    private sealed class UnconfiguredModelClient : IPureModelClient
    {
        public Task<PureModelResponse> GenerateAsync(PureModelRequest request,
            CancellationToken cancellationToken = default) =>
            throw new ModelClientException(ModelClientErrorKind.InvalidRequest,
                "未配置模型端点：请在设置中填写 Base URL 与模型后再发送消息。");
    }
}
