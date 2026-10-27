using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


using Fufucode.Core.Diagnostics;

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

    /// <summary>
    /// 模型声明的上下文窗口（tokens）。由宿主按模型目录注入；返回 null 表示未知。
    /// 这是压缩阈值的真实依据，不用固定字符数猜测模型能力。
    /// </summary>
    public Func<string?, CancellationToken, Task<long?>>? ContextLengthResolver { get; set; }

    /// <summary>上下文使用率达到该比例时触发压缩（默认 0.85，与主流 Harness 的自动压缩区间一致）。</summary>
    public double CompactionThreshold { get; set; } = 0.85;

    /// <summary>最近一次模型请求实际报告的 prompt tokens；用于下一次请求前的压缩判断。</summary>
    private long _lastPromptTokens;

    /// <summary>压缩后到下一次真实 usage 返回前，不再重复压缩。</summary>
    private bool _compactionDeferred;

    private long? _resolvedContextTokens;
    private string? _resolvedContextModel;
    private bool _contextResolved;

    /// <summary>无法获知模型窗口时的物理内存保护，不是产品级上下文限制。</summary>
    private const long PhysicalHistoryChars = 2_000_000;

    public int? MaxIterations
    {
        get => _loop.MaxIterations;
        set => _loop.MaxIterations = value;
    }

    public PureCSharpAgentEngine(
        IPureToolDispatcher? toolDispatcher = null,
        IPureModelClient? modelClient = null,
        int? maxIterations = null)
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
            // 长回合可能在工具执行途中被强杀：恢复时必须补齐缺失的 tool_result、
            // 丢弃没有对应 tool_call 的孤立结果，否则下一次模型请求会被协议拒绝。
            RepairToolCallPairing(_history);
        }
    }

    /// <summary>
    /// 修复恢复历史中的工具调用配对：为缺失结果的调用补合成结果，并移除孤立工具结果。
    /// 只处理协议结构，不改写已有内容；合法历史保持原样。
    /// </summary>
    public static void RepairToolCallPairing(List<PureAgentMessage> history)
    {
        var knownCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in history)
        {
            if (message.Role != PureAgentRole.Assistant || message.ToolCalls is not { Count: > 0 } calls)
                continue;
            foreach (var call in calls) knownCallIds.Add(call.Id);
        }

        // 移除找不到对应 tool_call 的孤立工具结果。
        history.RemoveAll(message => message.Role == PureAgentRole.Tool &&
                                     message.ToolCallId is { Length: > 0 } id &&
                                     !knownCallIds.Contains(id));

        for (var i = 0; i < history.Count; i++)
        {
            var message = history[i];
            if (message.Role != PureAgentRole.Assistant || message.ToolCalls is not { Count: > 0 } calls)
                continue;

            var answered = new HashSet<string>(StringComparer.Ordinal);
            var insertAt = i + 1;
            while (insertAt < history.Count && history[insertAt].Role == PureAgentRole.Tool)
            {
                if (history[insertAt].ToolCallId is { Length: > 0 } id) answered.Add(id);
                insertAt++;
            }

            var missing = calls.Where(call => !answered.Contains(call.Id)).ToArray();
            if (missing.Length == 0) continue;
            for (var m = 0; m < missing.Length; m++)
            {
                history.Insert(insertAt + m, new PureAgentMessage(
                    PureAgentRole.Tool,
                    "<tool_use_error>canceled before completion</tool_use_error>",
                    ToolCallId: missing[m].Id));
            }
            i = insertAt + missing.Length - 1;
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
            INativeSubagentDispatcher { IsActive: true } subagent => ResolveProductionDispatcher(subagent.Inner),
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
        using var turnSpan = PerfTracer.Step("engine.turn", "model", request.ModelId);
        turnSpan.Tag("historyMessages", request.Messages.Count);
        turnSpan.Tag("toolSchema", request.ToolDefinitions?.Count ?? request.ToolNames.Count);
        var waitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        turnSpan.Tag("gateWaitMs", (long)System.Diagnostics.Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds);

        // 回合直接推进实时历史：回合闸门保证同一时刻只有一个写者，读取方（GetState）在锁内取副本。
        // 不复制整段历史，也不再在结束时合并副本。
        try
        {
            lock (_gate)
            {
                if (_isRunning) throw new InvalidOperationException("已有回合在运行。");
                _isRunning = true;
                // 切换模型后，旧模型的 prompt token 数不能拿来判断新模型的窗口占用。
                if (request.ModelId is { Length: > 0 } requested && requested != _modelId)
                    _lastPromptTokens = 0;
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
            }

            using (var compactSpan = PerfTracer.Step("context.compact"))
            {
                compactSpan.Tag("historyMessages", _history.Count);
                await PrepareRequestAsync(request.ModelId ?? _modelId, cancellationToken).ConfigureAwait(false);
                compactSpan.Tag("historyAfter", _history.Count);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var effectiveModel = request.ModelId ?? _modelId;
            return await _loop.ExecuteLoopAsync(
                _history,
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
                reasoningEffort: request.ReasoningEffort,
                prepareRequest: ct => PrepareRequestAsync(effectiveModel, ct),
                onModelUsage: OnModelUsage,
                recoverFromContextOverflow: ct => RecoverFromContextOverflowAsync(effectiveModel, ct))
                .ConfigureAwait(false);
        }
        finally
        {
            _isRunning = false;
            _turnGate.Release();
        }
    }

    /// <summary>
    /// 请求前准备：按“模型真实窗口 × 实际 prompt tokens”判断是否需要压缩。
    /// 窗口未知时才退回到物理字符保护；不再用 400k 字符猜测所有模型。
    /// </summary>
    private async Task PrepareRequestAsync(string? modelId, CancellationToken cancellationToken)
    {
        if (_compactionDeferred) return;

        var limit = await ResolveContextTokensAsync(modelId, cancellationToken).ConfigureAwait(false);
        if (limit is > 0)
        {
            // 真实窗口来自模型目录解析；只上报已知值，未知时保持无标签。
            using var windowSpan = PerfTracer.Step("context.window", "tokens", PerfTracer.Invariant(limit.Value));
        }
        var chars = TotalChars(_history);
        bool needed;
        if (limit is > 0)
        {
            // 已知窗口：优先用上一次真实 usage。恢复的历史没有 usage 时，
            // 用 chars/2 的保守估算（中英混排下不会低估太多），宁可提前压缩也不让请求超限。
            var estimate = _lastPromptTokens > 0 ? _lastPromptTokens : chars / 2;
            needed = estimate >= limit.Value * CompactionThreshold;
        }
        else
        {
            needed = chars >= PhysicalHistoryChars;
        }

        if (!needed) return;
        if (await CompactIfNeededAsync(_history, modelId, cancellationToken, force: true)
                .ConfigureAwait(false))
            _compactionDeferred = true;
    }

    private void OnModelUsage(PureUsage usage)
    {
        _lastPromptTokens = usage.InputTokens
                            ?? (usage.TotalTokens is { } total && usage.OutputTokens is { } output
                                ? total - output
                                : 0);
        _compactionDeferred = false;
    }

    private async Task<long?> ResolveContextTokensAsync(string? modelId, CancellationToken cancellationToken)
    {
        if (ContextLengthResolver is null) return null;
        if (_contextResolved && string.Equals(_resolvedContextModel, modelId, StringComparison.Ordinal))
            return _resolvedContextTokens;

        try
        {
            _resolvedContextTokens = await ContextLengthResolver(modelId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 目录查询失败不应影响主回合；退回物理保护。
            _resolvedContextTokens = null;
        }
        _resolvedContextModel = modelId;
        _contextResolved = true;
        return _resolvedContextTokens;
    }

    private async Task<bool> RecoverFromContextOverflowAsync(string? modelId, CancellationToken cancellationToken)
    {
        var before = _history.Count;
        try
        {
            await CompactIfNeededAsync(_history, modelId, cancellationToken,
                force: true, aggressive: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
        _compactionDeferred = true;
        return _history.Count < before;
    }

    private static long TotalChars(List<PureAgentMessage> history)
    {
        long total = 0;
        foreach (var message in history) total += SizeOf(message);
        return total;
    }

    private async Task<bool> CompactIfNeededAsync(List<PureAgentMessage> history, string? modelId,
        CancellationToken cancellationToken, bool force = false, bool aggressive = false)
    {
        var limit = _contextResolved ? _resolvedContextTokens : null;
        // 压缩窗口同样按真实窗口缩放：未知时才使用固定的保守窗口。
        var keepRecent = aggressive ? 4 : 8;
        var keepRecentChars = limit is { } context
            ? (long)(context * (aggressive ? 0.08 : 0.15) * 3)
            : (aggressive ? 30_000 : 120_000);
        var transcriptBudget = limit is { } contextBudget
            ? Math.Clamp((long)(contextBudget * 0.25 * 3), 30_000, 240_000)
            : 60_000;

        var total = TotalChars(history);
        if (history.Count <= keepRecent + 1) return false;
        if (!force && total <= PhysicalHistoryChars) return false;

        var system = history.Count > 0 && history[0].Role == PureAgentRole.System ? history[0] : null;
        var start = system is null ? 0 : 1;

        // 用户指令是最容易在压缩中丢失、又最昂贵的信息：目标与硬约束一旦丢失，
        // 后续所有工作都会偏离。它们在预算内逐字保留，不交给摘要模型改写。
        var pinned = FindPinnedInstructions(history, start);

        var firstKept = Math.Max(start + 1, history.Count - keepRecent);
        // 不要从孤立的 tool 结果开始：它们对应的 assistant tool_call 已被折叠。
        while (firstKept < history.Count && history[firstKept].Role == PureAgentRole.Tool) firstKept++;
        // 最近窗口同时受字符预算约束：8 条超大工具结果同样会撑爆上下文。
        var recentChars = 0;
        while (firstKept < history.Count - 1)
        {
            recentChars += SizeOf(history[firstKept]);
            if (recentChars <= keepRecentChars || history.Count - firstKept <= 2) break;
            firstKept++;
            while (firstKept < history.Count && history[firstKept].Role == PureAgentRole.Tool) firstKept++;
        }
        if (firstKept >= history.Count) return false;

        var omitted = firstKept - start;
        if (omitted <= 0) return false;

        var transcript = new StringBuilder();
        var filesTouched = new List<string>();
        var commands = new List<string>();
        var exitCodes = new List<string>();
        var failedTools = 0;
        for (var i = start; i < firstKept && transcript.Length < transcriptBudget; i++)
        {
            var message = history[i];
            var text = message.Content ?? string.Empty;
            if (text.Length > 6000) text = text[..6000] + "…";
            transcript.Append(message.Role).Append(": ").AppendLine(text);
            if (message.ToolCalls is { Count: > 0 } calls)
            {
                foreach (var call in calls)
                {
                    CollectTouchedFile(call, filesTouched);
                    CollectCommand(call, commands);
                    transcript.Append("  [tool_call] ").Append(call.Name).Append(' ')
                        .AppendLine(call.ArgumentsJson.Length > 800
                            ? call.ArgumentsJson[..800] + "…"
                            : call.ArgumentsJson);
                }
            }
            if (message.Role == PureAgentRole.Tool && message.Content is { Length: > 0 } toolText)
            {
                if (toolText.Contains("tool_use_error", StringComparison.Ordinal)) failedTools++;
                if (TryReadExitCode(toolText) is { } code) exitCodes.Add(code);
            }
        }

        var fileHint = filesTouched.Count > 0
            ? $"涉及文件（按出现顺序，最多 20 个）：{string.Join(", ", filesTouched.Take(20))}"
            : "涉及文件：未从工具调用中识别";

        // 确定性状态台账：这些事实不依赖摘要模型的可靠性，压缩后必须原样存在。
        var ledger = BuildStateLedger(filesTouched, commands, exitCodes, failedTools);

        string summary;
        try
        {
            // 压缩摘要是一次隐藏的模型调用：单独计时，避免它拉高“回合变慢”却无从定位。
            using var summarizeSpan = PerfTracer.Step("model.summarize", "omittedMessages", PerfTracer.Invariant(omitted));
            var response = await _modelClient.GenerateAsync(new PureModelRequest(
                new[]
                {
                    new PureAgentMessage(PureAgentRole.System,
                        "你是编码 Agent 的上下文压缩器。把以下对话压缩为可继续工作的交接摘要。" +
                        "必须按以下标题输出，信息不足时写“无”，不要编造：\n" +
                        "## 1. 用户目标与硬约束\n逐条列出，用户明确给出的路径/命名/格式/禁止事项保留原文。\n" +
                        "## 2. 已完成工作\n已创建/修改/删除的文件，以及每个文件的关键改动点。\n" +
                        "## 3. 关键决策与理由\n选择了什么方案，放弃了什么，为什么。\n" +
                        "## 4. 错误与修复\n遇到的报错、失败命令、被拒绝的工具调用，以及是否已修复。\n" +
                        "## 5. 验证状态\n实际运行过的测试/命令及结果；未验证项必须明确标注。\n" +
                        "## 6. 待办与下一步\n按优先级列出未完成项与下一步动作。\n" +
                        "## 7. 关键事实\n继续工作所需的符号名、行号、接口签名、数据形状；只写已经确认过的事实。\n" +
                        "只输出摘要，不要评论，不要使用代码块包裹整个答复。"),
                    new PureAgentMessage(PureAgentRole.User,
                        $"{fileHint}\n{ledger}\n\n--- 对话记录 ---\n{transcript}")
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

        // 计划是结构化待办，且不在对话文本里——摘要模型看不到它。
        // 压缩后必须原样保留，否则模型会忘记剩余步骤并重复已完成的工作。
        var planSnapshot = CurrentPlanSnippet;

        // 历史是实时列表：压缩结果一次性换入，GetState 不会读到清空到一半的状态。
        lock (_gate)
        {
            history.Clear();
            if (system is not null) history.Add(system);
            history.Add(note);
            // 台账用 Assistant 角色：它是内核自己记录的状态，不是用户新指令，
            // 避免模型把台账内容误当成用户要求。
            history.Add(new PureAgentMessage(PureAgentRole.Assistant, ledger));
            foreach (var instruction in pinned)
            {
                if (!kept.Contains(instruction)) history.Add(instruction);
            }
            if (!string.IsNullOrWhiteSpace(planSnapshot))
                history.Add(new PureAgentMessage(PureAgentRole.Assistant,
                    $"[压缩时保留的计划快照]\n{planSnapshot}"));
            history.AddRange(kept);
        }
        return true;
    }

    private static int SizeOf(PureAgentMessage message)
    {
        var size = message.Content?.Length ?? 0;
        if (message.ToolCalls is { Count: > 0 } calls)
            size += calls.Sum(call => call.ArgumentsJson.Length);
        return size;
    }

    /// <summary>
    /// 收集需要逐字保留的用户指令。
    ///
    /// 只钉住"第一条足够长的用户消息"是不够的：用户常在后续回合追加硬约束
    /// （"不要动 main 分支"、"只用标准库"），这些恰恰是压缩后最容易丢失的信息。
    /// 这里按顺序保留所有用户消息，总量受字符预算约束，不做内容判断——
    /// 判断"哪句重要"需要语义理解，而代码只能保证不丢。
    /// </summary>
    private static List<PureAgentMessage> FindPinnedInstructions(List<PureAgentMessage> history, int start)
    {
        const int budgetChars = 8_000;
        var pinned = new List<PureAgentMessage>();
        var used = 0;
        for (var i = start; i < history.Count; i++)
        {
            var message = history[i];
            if (message.Role != PureAgentRole.User) continue;
            if (string.IsNullOrWhiteSpace(message.Content)) continue;
            var content = message.Content;
            // 跳过内核自己注入的痕迹，否则第二次压缩会把台账/提示当成用户指令。
            if (content.StartsWith("[上下文摘要", StringComparison.Ordinal)) continue;
            if (content.StartsWith("[压缩时保留的状态台账]", StringComparison.Ordinal)) continue;
            if (content.StartsWith("[预算提示]", StringComparison.Ordinal)) continue;
            if (content.StartsWith("[压缩时保留的计划快照]", StringComparison.Ordinal)) continue;

            if (used + content.Length > budgetChars && pinned.Count > 0) break;
            pinned.Add(message);
            used += content.Length;
        }
        return pinned;
    }

    /// <summary>
    /// 构造确定性状态台账：已改文件、执行过的命令与退出码、失败工具次数。
    /// 摘要模型可能漏项，而这些是继续工作必需的事实，必须由代码保证保留。
    /// </summary>
    private static string BuildStateLedger(List<string> files, List<string> commands,
        List<string> exitCodes, int failedTools)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[压缩时保留的状态台账]");
        builder.Append("已改文件：").AppendLine(files.Count == 0
            ? "无（或未识别）"
            : string.Join(", ", files.Take(20)));

        if (commands.Count > 0)
        {
            builder.AppendLine("执行过的命令（最近 8 条）：");
            var startIndex = Math.Max(0, commands.Count - 8);
            for (var i = startIndex; i < commands.Count; i++)
            {
                var code = i < exitCodes.Count ? exitCodes[i] : "未知";
                var command = commands[i].Length > 160 ? commands[i][..160] + "…" : commands[i];
                builder.Append("- `").Append(command).Append("` → ExitCode ").AppendLine(code);
            }
        }

        builder.Append("失败工具结果：").Append(failedTools).AppendLine(" 次");
        return builder.ToString().TrimEnd();
    }

    private static void CollectCommand(PureToolCall call, List<string> commands)
    {
        if (!string.Equals(call.Name, "run_command", StringComparison.Ordinal)) return;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(call.ArgumentsJson);
            if (document.RootElement.TryGetProperty("command", out var command) &&
                command.GetString() is { Length: > 0 } value)
                commands.Add(value.Replace('\n', ' '));
        }
        catch
        {
            // 参数非法时跳过，不影响压缩。
        }
    }

    /// <summary>从 run_command 的结果首行读取退出码（格式固定为 `ExitCode: N`）。</summary>
    private static string? TryReadExitCode(string content)
    {
        const string prefix = "ExitCode:";
        if (!content.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var newline = content.IndexOf('\n');
        var line = newline < 0 ? content : content[..newline];
        var value = line[prefix.Length..].Trim();
        return value.Length == 0 ? null : value;
    }

    private static void CollectTouchedFile(PureToolCall call, List<string> files)
    {
        if (call.Name is not ("read_file" or "write_file" or "edit_file")) return;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(call.ArgumentsJson);
            if (!document.RootElement.TryGetProperty("path", out var path)) return;
            if (path.GetString() is not { Length: > 0 } value) return;
            if (!files.Contains(value, StringComparer.OrdinalIgnoreCase)) files.Add(value);
        }
        catch
        {
            // 参数不是合法 JSON 时只跳过文件名提取，不影响压缩本身。
        }
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
