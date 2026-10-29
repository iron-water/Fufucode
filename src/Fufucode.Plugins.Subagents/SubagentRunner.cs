using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Diagnostics;

namespace Fufucode.Core.Backend;

/// <summary>子代理进度事件（用于父会话状态提示）。</summary>
public sealed record SubagentProgress(string JobId, string Agent, string Kind, string? Detail);

/// <summary>子代理工具调用轨迹（patch 模式用于证明改了什么、验证是否执行）。</summary>
public sealed record SubagentToolTrace(string Tool, string ArgumentsJson, bool Success, string? Summary);

/// <summary>一次子代理执行结果。</summary>
public sealed record SubagentResult(
    bool Success,
    string AgentName,
    SubagentMode Mode,
    string Content,
    int ToolCalls,
    string? Error,
    string? JobId = null,
    string? StructuredOutput = null,
    string? Patch = null,
    string? PatchPath = null,
    IReadOnlyList<string>? ChangedFiles = null,
    string? BaseHead = null,
    IReadOnlyList<SubagentToolTrace>? ToolTrace = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    long? DurationMs = null,
    bool Truncated = false);

/// <summary>
/// 进程内子代理运行器：
/// - read：只读、隔离上下文的快速探索；轮次/超时只由代理定义决定；
/// - patch：临时 git worktree 内修改并返回 unified diff，不宣称交付。
/// </summary>
public sealed class SubagentRunner
{
    private static readonly HashSet<string> ReadOnlyBuiltins = new(StringComparer.Ordinal)
    {
        "read_file", "list_dir", "grep_search", "glob_files"
    };

    private static readonly HashSet<string> PatchBuiltins = new(StringComparer.Ordinal)
    {
        "read_file", "write_file", "edit_file", "list_dir", "grep_search", "glob_files", "run_command"
    };
    // worktree 创建会争用 .git/worktrees 元数据；只串行创建，运行本身仍可并行。
    private static readonly SemaphoreSlim WorktreeGate = new(1, 1);

    private readonly AgentRegistry _agents;
    private readonly IPureModelClient _modelClient;
    private readonly IPureToolDispatcher _capabilities;
    private readonly IReadOnlyList<PureToolDefinition> _capabilityDefinitions;
    private readonly string? _defaultModelId;
    private readonly string? _workspaceRoot;
    private readonly int _depth;
    private readonly int _maxDepth;
    public Func<IReadOnlyList<PureAgentMessage>>? HistoryProvider { get; set; }
    private readonly Dictionary<string, PureToolDefinition> _definitions;

    public SubagentRunner(
        AgentRegistry agents,
        IPureModelClient modelClient,
        IPureToolDispatcher capabilities,
        string? defaultModelId,
        string? workspaceRoot = null,
        IReadOnlyList<PureToolDefinition>? capabilityDefinitions = null,
        int depth = 0,
        int maxDepth = 1,
        Func<IReadOnlyList<PureAgentMessage>>? historyProvider = null,
        string? jobId = null)
    {
        _agents = agents;
        _modelClient = modelClient;
        _capabilities = capabilities;
        _capabilityDefinitions = capabilityDefinitions ?? [];
        _defaultModelId = defaultModelId;
        _workspaceRoot = workspaceRoot;
        _depth = depth;
        _maxDepth = maxDepth;
        HistoryProvider = historyProvider;

        var definitions = ProductionToolDispatcher.GetBuiltinToolDefinitions()
            .Concat(_capabilityDefinitions)
            .Concat(depth < maxDepth ? new[] { SubagentDispatcher.Definition } : Array.Empty<PureToolDefinition>())
            .GroupBy(definition => definition.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        _definitions = definitions;
        JobId = jobId ?? Guid.NewGuid().ToString("N")[..12];
    }

    public string JobId { get; }

    /// <summary>插件激活代次；非 null 时执行进入 lease，撤销后拒绝并取消在途调用。</summary>
    internal SubagentGeneration? Lifetime { get; init; }

    public Func<PureToolCall, CancellationToken, Task<bool>>? ApprovalGate { get; set; }
    public Action<SubagentProgress>? Progress { get; set; }

    /// <summary>父会话权限模式；Plan 下禁止 patch 子代理，保证计划阶段不产生写入。</summary>
    public Func<PermissionMode>? ParentMode { get; set; }

    public IReadOnlyList<AgentDefinition> Agents => _agents.All;
    public string BuildPromptSection() => _agents.BuildPromptSection();

    private void Report(string agent, string kind, string? detail = null) =>
        Progress?.Invoke(new SubagentProgress(JobId, agent, kind, detail));

    private SubagentRunner CreateChildRunner()
    {
        var child = new SubagentRunner(
            _agents, _modelClient, _capabilities, _defaultModelId, _workspaceRoot, _capabilityDefinitions,
            _depth + 1, _maxDepth, HistoryProvider, JobId)
        {
            Lifetime = Lifetime,
            ApprovalGate = ApprovalGate,
            ParentMode = ParentMode,
            Progress = Progress
        };
        return child;
    }

    public async Task<SubagentResult> RunAsync(string agentName, string prompt, string? modelOverride,
        CancellationToken cancellationToken, bool? fork = null, SubagentMode? mode = null)
    {
        if (Lifetime is not { } generation)
            return await RunObservedAsync(agentName, prompt, modelOverride, cancellationToken, fork, mode)
                .ConfigureAwait(false);
        // 旧会话在 Shutdown 后仍可能调用：撤销后拒绝新执行，执行中的调用由代次 token 取消。
        if (!generation.TryEnter(out var generationToken))
            return Failure(agentName, mode ?? SubagentMode.Read, "子代理插件已卸载，调用被拒绝。");
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generationToken);
            return await RunObservedAsync(agentName, prompt, modelOverride, linked.Token, fork, mode)
                .ConfigureAwait(false);
        }
        finally
        {
            generation.Exit();
        }
    }

    private async Task<SubagentResult> RunObservedAsync(string agentName, string prompt, string? modelOverride,
        CancellationToken cancellationToken, bool? fork, SubagentMode? mode)
    {
        // 子代理在独立上下文中运行：开启新的根 span，内部模型/工具调用挂在它下面。
        using var span = PerfTracer.Step("subagent", "agent", agentName);
        span.Tag("mode", mode?.ToString() ?? "default");
        span.Tag("fork", fork == true);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await RunDispatchAsync(agentName, prompt, modelOverride, cancellationToken, fork, mode)
                .ConfigureAwait(false);
            span.Tag("ok", result.Success);
            span.Tag("toolCalls", result.ToolCalls);
            if (!result.Success) span.Fail(result.Error ?? "failed");
            return result with { DurationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds };
        }
        catch (OperationCanceledException) { span.Cancel(); throw; }
        catch (Exception error) { span.Fail(error.GetType().Name); throw; }
    }

    private async Task<SubagentResult> RunDispatchAsync(string agentName, string prompt, string? modelOverride,
        CancellationToken cancellationToken, bool? fork, SubagentMode? mode)
    {
        if (!_agents.TryGet(agentName, out var agent))
            return Failure(agentName, SubagentMode.Read,
                $"未知子代理 '{agentName}'。可用：{string.Join(", ", _agents.All.Select(a => a.Name))}。");

        if (string.IsNullOrWhiteSpace(prompt))
            return Failure(agent.Name, agent.Mode, "子代理任务 prompt 不能为空。");

        var effectiveMode = mode ?? agent.Mode;
        Report(agent.Name, "started", prompt.Length > 120 ? prompt[..120] + "…" : prompt);

        if (effectiveMode == SubagentMode.Patch)
        {
            if (ParentMode?.Invoke() == PermissionMode.Plan)
                return Failure(agent.Name, SubagentMode.Patch, "Plan 模式只允许 read 子代理，patch 已被拒绝。");
            return await RunPatchAsync(agent, prompt, modelOverride, fork, cancellationToken)
                .ConfigureAwait(false);
        }

        return await RunCoreAsync(agent, prompt, modelOverride, fork, _capabilities,
            SubagentMode.Read, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SubagentResult> RunCoreAsync(AgentDefinition agent, string prompt, string? modelOverride,
        bool? fork, IPureToolDispatcher baseDispatcher, SubagentMode mode,
        CancellationToken cancellationToken)
    {
        // read 模式强制只读内置工具；能力工具只有显式声明才可用。
        // patch 模式只允许内置修改工具，能力工具与嵌套一律不开放（隔离边界）。
        var builtins = mode == SubagentMode.Patch ? PatchBuiltins : ReadOnlyBuiltins;
        var capabilityNames = mode == SubagentMode.Read
            ? _capabilityDefinitions.Select(definition => definition.Name).ToHashSet(StringComparer.Ordinal)
            : [];
        // 默认子代理不嵌套：只有代理定义显式声明 subagent，且未到深度上限时才开放。
        // patch 模式始终禁止嵌套，避免绕过 worktree 隔离边界。
        var canNest = mode == SubagentMode.Read && _depth < _maxDepth &&
                      agent.Tools.Contains(SubagentDispatcher.ToolName, StringComparer.Ordinal);

        var allowedTools = new List<string>(agent.Tools);

        var allowed = allowedTools
            .Where(name => builtins.Contains(name) || capabilityNames.Contains(name) ||
                           (canNest && string.Equals(name, SubagentDispatcher.ToolName, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (allowed.Length == 0)
            return Failure(agent.Name, mode, $"子代理 '{agent.Name}' 没有可用的工具。");

        IPureToolDispatcher chain = baseDispatcher;
        if (allowed.Contains(SubagentDispatcher.ToolName, StringComparer.Ordinal))
            chain = new NestedSubagentDispatcher(chain, this.CreateChildRunner());
        // 同一次子代理运行内重复读取同一文件/同一检索是最常见的浪费：
        // 缓存命中直接复用结果，不重复落盘、不重复递归扫描。跨运行不共享，避免读到过期内容。
        chain = new RunScopedReadCache(chain);
        var childDispatcher = new AllowedToolDispatcher(chain, allowed.ToHashSet(StringComparer.Ordinal));

        var definitions = allowed.Select(name => _definitions[name]).ToArray();
        var systemPrompt = BuildSystemPrompt(agent, mode);
        var messages = new List<PureAgentMessage>
        {
            new(PureAgentRole.System, systemPrompt)
        };
        if (fork ?? agent.Fork)
            messages.AddRange(BuildForkHistory());
        messages.Add(new PureAgentMessage(PureAgentRole.User, prompt));

        // 未声明 maxTurns 时不设置轮次预算；模式层不截断子代理。
        var engine = new PureCSharpAgentEngine(childDispatcher, _modelClient, agent.MaxTurns)
        {
            ApprovalGate = ApprovalGate
        };
        var request = new PureModelRequest(messages, allowed,
            modelOverride ?? agent.ModelId ?? _defaultModelId, definitions,
            string.IsNullOrWhiteSpace(agent.Effort) ? null : agent.Effort);

        var trace = new List<SubagentToolTrace>();
        var timeoutSpan = agent.TimeoutSeconds is { } seconds
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 3600))
            : (TimeSpan?)null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutSpan is { } span) timeout.CancelAfter(span);
        try
        {
            var result = await engine.RunObservedTurnAsync(request, new NativeAgentCallbacks
            {
                OnMessageAppended = message =>
                {
                    if (message.Role == PureAgentRole.Assistant && !string.IsNullOrWhiteSpace(message.Content))
                        Report(agent.Name, "assistant", Truncate(message.Content, 400));
                },
                OnToolStarting = call => Report(agent.Name, "tool_start", call.Name),
                OnToolCompleted = (call, toolResult) =>
                {
                    trace.Add(new SubagentToolTrace(call.Name, call.ArgumentsJson, toolResult.Success,
                        Truncate(toolResult.Content ?? string.Empty, 200)));
                    Report(agent.Name, "tool_end", call.Name + (toolResult.Success ? "" : " (失败)"));
                }
            }, timeout.Token).ConfigureAwait(false);

            if (result.Status == PureAgentTurnStatus.Failed)
                return Failure(agent.Name, mode, result.Error ?? "子代理执行失败。",
                    result.Usage?.InputTokens, result.Usage?.OutputTokens);

            var content = result.Response.Text ?? string.Empty;
            // 轮次耗尽不是成功交付：父会话必须知道结论可能不完整，而不是把半成品当定论。
            // 以引擎的显式标记为准；文本匹配只作为旧客户端的兜底。
            var truncated = result.Truncated ||
                            content.Contains("已达到最大轮次限制", StringComparison.Ordinal);
            string? structured = null;
            if (!string.IsNullOrWhiteSpace(agent.OutputSchema))
            {
                var (json, error) = ExtractAndValidate(content, agent.OutputSchema);
                if (error is not null)
                {
                    Report(agent.Name, "failed", error);
                    return new SubagentResult(false, agent.Name, mode, content, result.ToolResults.Count,
                        error, JobId, null, null, null, null, null, trace,
                        result.Usage?.InputTokens, result.Usage?.OutputTokens);
                }
                structured = json;
                content = json!;
            }

            if (content.Length == 0)
                content = result.ToolResults.Count > 0
                    ? "（子代理执行了工具但没有返回文本。）"
                    : "（子代理没有返回内容。）";

            Report(agent.Name, truncated ? "truncated" : "completed", null);
            return new SubagentResult(true, agent.Name, mode, content, result.ToolResults.Count, null, JobId,
                structured, null, null, null, null, trace,
                result.Usage?.InputTokens, result.Usage?.OutputTokens, Truncated: truncated);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutSpan is not null)
        {
            Report(agent.Name, "failed", "timeout");
            return Failure(agent.Name, mode,
                $"子代理执行超时（{timeoutSpan.Value.TotalSeconds:0} 秒）。");
        }
    }

    private async Task<SubagentResult> RunPatchAsync(AgentDefinition agent, string prompt, string? modelOverride,
        bool? fork, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_workspaceRoot))
            return Failure(agent.Name, SubagentMode.Patch, "patch 模式需要工作区根目录。");

        var (path, error) = await CreateWorktreeAsync(_workspaceRoot!, cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            Report(agent.Name, "failed", error);
            return Failure(agent.Name, SubagentMode.Patch, error ?? "创建 worktree 失败。");
        }

        try
        {
            var (dirty, _) = await RunGitAsync(_workspaceRoot!, ["status", "--porcelain"],
                cancellationToken).ConfigureAwait(false);
            var dispatcher = new ProductionToolDispatcher(path);
            var result = await RunCoreAsync(agent, prompt, modelOverride, fork, dispatcher,
                SubagentMode.Patch, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(dirty))
                result = result with
                {
                    Content = result.Content +
                              "\n\n[注意：父工作区存在未提交改动；补丁基于 HEAD，应用前请先核对基线]"
                };

            var (baseHead, _) = await RunGitAsync(path, ["rev-parse", "HEAD"], cancellationToken)
                .ConfigureAwait(false);
            await RunGitAsync(path, ["add", "-A"], cancellationToken).ConfigureAwait(false);
            var (diff, diffError) = await RunGitAsync(path, ["diff", "--cached", "--no-color"],
                cancellationToken).ConfigureAwait(false);
            var (changedText, _) = await RunGitAsync(path, ["diff", "--cached", "--name-only"],
                cancellationToken).ConfigureAwait(false);

            if (diffError is not null)
            {
                return result with
                {
                    Success = false,
                    Error = "读取补丁失败：" + diffError,
                    Mode = SubagentMode.Patch
                };
            }

            var changedFiles = changedText.Split('\n', StringSplitOptions.RemoveEmptyEntries |
                                                        StringSplitOptions.TrimEntries);
            if (string.IsNullOrWhiteSpace(diff))
            {
                return result with
                {
                    Content = result.Content + "\n\n[patch 子代理未产生任何改动]",
                    Mode = SubagentMode.Patch,
                    ChangedFiles = [],
                    BaseHead = baseHead
                };
            }

            // 补丁始终落一份临时文件，避免父模型上下文截断导致补丁不可应用；
            // 小补丁同时内联返回，便于直接审阅。
            var patchPath = Path.Combine(Path.GetTempPath(),
                "fufucode-subagent-patch-" + JobId + ".patch");
            await File.WriteAllTextAsync(patchPath, diff, CancellationToken.None).ConfigureAwait(false);
            var patch = diff.Length <= 32 * 1024 ? diff : null;
            var note = $"\n\n[patch 候选补丁：{changedFiles.Length} 个文件，基于 {Truncate(baseHead, 12)}；未交付]" +
                       $"\n补丁文件：{patchPath}";
            return result with
            {
                Content = result.Content + note,
                Mode = SubagentMode.Patch,
                Patch = patch,
                PatchPath = patchPath,
                ChangedFiles = changedFiles,
                BaseHead = baseHead
            };
        }
        finally
        {
            await RemoveWorktreeAsync(_workspaceRoot!, path, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private string BuildSystemPrompt(AgentDefinition agent, SubagentMode mode)
    {
        var builder = new StringBuilder(agent.SystemPrompt);
        if (!string.IsNullOrWhiteSpace(_workspaceRoot))
            builder.Append("\n\n工作区根目录：").Append(_workspaceRoot);
        if (mode == SubagentMode.Patch)
            builder.Append("\n你只应做最小必要修改；最终说明改动与验证结果，改动会以补丁形式交回父会话，不会直接交付。");
        else
            builder.Append("\n当前是 read 模式：只允许只读工具；写入、编辑与命令执行不可用，不要尝试。")
                .Append("\n先用 grep/glob 定位，再只读必要片段；证据足够就立即给出可用结论，不要为了完整性遍历无关文件或重复确认。")
                .Append("\n结论必须区分“已确认（有文件:行号或命令输出证据）”与“推测”，不要编造未读到的代码。")
                .Append("\n不要用“再查一点”换取边际很小的完整性；标注哪些结论尚未完整验证。");
        if (!string.IsNullOrWhiteSpace(agent.OutputSchema))
        {
            builder.Append("\n\n最终答复必须是符合以下 JSON Schema 的纯 JSON（不要代码块、不要额外说明）：\n")
                .Append(agent.OutputSchema);
        }
        return builder.ToString();
    }

    private IReadOnlyList<PureAgentMessage> BuildForkHistory()
    {
        if (HistoryProvider?.Invoke() is not { Count: > 0 } history) return [];
        // 父调用尚在执行时不能继承其 assistant/tool 部分组，也不能伪造取消结果。
        var groups = NativeHistoryGroups.Enumerate(history)
            .Where(group => group.Complete && history[group.Start].Role != PureAgentRole.System).ToArray();
        long total = 0;
        var count = 0;
        var first = groups.Length;
        for (var i = groups.Length - 1; i >= 0; i--)
        {
            var group = groups[i];
            if (total + group.Characters > 100_000 || count + group.End - group.Start > 30) break;
            total += group.Characters;
            count += group.End - group.Start;
            first = i;
        }
        var kept = new List<PureAgentMessage>();
        for (var i = first; i < groups.Length; i++)
            for (var j = groups[i].Start; j < groups[i].End; j++) kept.Add(history[j]);
        return kept;
    }

    private static (string? Json, string? Error) ExtractAndValidate(string content, string schemaText)
    {
        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            if (text.EndsWith("```", StringComparison.Ordinal)) text = text[..^3];
            text = text.Trim();
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return (null, "结构化输出缺少 JSON 对象。");
        var json = text[start..(end + 1)];
        try
        {
            using var document = JsonDocument.Parse(json);
            using var schema = JsonDocument.Parse(schemaText);
            if (schema.RootElement.ValueKind == JsonValueKind.Object &&
                schema.RootElement.TryGetProperty("required", out var required) &&
                required.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in required.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var name = item.GetString()!;
                    if (!document.RootElement.TryGetProperty(name, out _))
                        return (null, $"结构化输出缺少必需字段 '{name}'。");
                }
            }
            return (document.RootElement.GetRawText(), null);
        }
        catch (JsonException error)
        {
            return (null, "结构化输出不是合法 JSON：" + error.Message);
        }
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "…";

    private SubagentResult Failure(string agent, SubagentMode mode, string error,
        long? input = null, long? output = null)
    {
        Report(agent, "failed", error);
        return new SubagentResult(false, agent, mode, string.Empty, 0, error, JobId,
            InputTokens: input, OutputTokens: output);
    }

    private sealed class AllowedToolDispatcher(IPureToolDispatcher inner, HashSet<string> allowed)
        : IPureToolDispatcher
    {
        public Task<PureToolResult> DispatchAsync(PureToolCall call, CancellationToken cancellationToken = default) =>
            allowed.Contains(call.Name)
                ? inner.DispatchAsync(call, cancellationToken)
                : Task.FromResult(new PureToolResult(call.Id, false,
                    $"<tool_use_error>子代理未获授权使用工具 '{call.Name}'。</tool_use_error>"));
    }

    /// <summary>
    /// 单次子代理运行内的只读结果缓存。只缓存确定性只读工具的成功结果；
    /// 失败结果不缓存（可能是临时占用/超时），写工具永不缓存。
    /// </summary>
    private sealed class RunScopedReadCache(IPureToolDispatcher inner) : IPureToolDispatcher
    {
        private static readonly HashSet<string> Cacheable = new(StringComparer.Ordinal)
        {
            "read_file", "list_dir", "grep_search", "glob_files"
        };

        private readonly Dictionary<string, PureToolResult> _cache = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public async Task<PureToolResult> DispatchAsync(PureToolCall call,
            CancellationToken cancellationToken = default)
        {
            if (!Cacheable.Contains(call.Name))
                return await inner.DispatchAsync(call, cancellationToken).ConfigureAwait(false);

            var key = call.Name + "\n" + call.ArgumentsJson;
            lock (_gate)
            {
                if (_cache.TryGetValue(key, out var cached))
                    return cached with { Id = call.Id };
            }

            var result = await inner.DispatchAsync(call, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                lock (_gate) _cache[key] = result;
            }
            return result;
        }
    }

    private sealed class NestedSubagentDispatcher(IPureToolDispatcher inner, SubagentRunner child)
        : IPureToolDispatcher
    {
        public Task<PureToolResult> DispatchAsync(PureToolCall call, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(call.Name, SubagentDispatcher.ToolName, StringComparison.Ordinal))
                return inner.DispatchAsync(call, cancellationToken);
            return SubagentDispatcher.HandleForegroundAsync(call, child, cancellationToken);
        }
    }

    // ── git worktree helpers ──

    private static async Task<(string? Path, string? Error)> CreateWorktreeAsync(string workspace,
        CancellationToken cancellationToken)
    {
        var (root, rootError) = await RunGitAsync(workspace, ["rev-parse", "--show-toplevel"], cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(root))
            return (null, "worktree 隔离要求工作区是 git 仓库。" + (rootError ?? string.Empty));

        var path = Path.Combine(Path.GetTempPath(), "fufucode-subagent-wt-" + Guid.NewGuid().ToString("N")[..8]);
        await WorktreeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (_, error) = await RunGitAsync(workspace,
                ["worktree", "add", "--detach", path, "HEAD"], cancellationToken).ConfigureAwait(false);
            return error is null ? (path, null) : (null, "创建 worktree 失败：" + error);
        }
        finally
        {
            WorktreeGate.Release();
        }
    }

    private static async Task RemoveWorktreeAsync(string workspace, string path, CancellationToken cancellationToken)
    {
        await RunGitAsync(workspace, ["worktree", "remove", "--force", path], cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(string Output, string? Error)> RunGitAsync(string workingDirectory,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(workingDirectory);
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);

            process = new Process { StartInfo = psi };
            process.Start();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            // 必须同时读 stdout/stderr：只读一路会让另一路填满管道后互相等待。
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (stdoutTask.Result.Trim(), process.ExitCode == 0 ? null : stderrTask.Result.Trim());
        }
        catch (Exception error)
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
            return (string.Empty, error.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }
}

