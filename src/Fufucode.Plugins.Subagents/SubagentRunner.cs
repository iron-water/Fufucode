using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
    long? DurationMs = null);

/// <summary>
/// 进程内子代理运行器：
/// - read：只读、低轮次、短超时的快速探索；
/// - patch：临时 git worktree 内修改并返回 unified diff，不宣称交付。
/// </summary>
public sealed class SubagentRunner
{
    private SubagentTier _tier = SubagentTier.Faster;
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
    public SubagentTier Tier => _tier;
    public void SetTier(SubagentTier tier) => _tier = tier;
    public Func<PureToolCall, CancellationToken, Task<bool>>? ApprovalGate { get; set; }
    public Action<SubagentProgress>? Progress { get; set; }

    /// <summary>父会话权限模式；Plan 下禁止 patch 子代理，保证计划阶段不产生写入。</summary>
    public Func<PermissionMode>? ParentMode { get; set; }

    public IReadOnlyList<AgentDefinition> Agents => _agents.All;
    public string BuildPromptSection() => _agents.BuildPromptSection();

    private void Report(string agent, string kind, string? detail = null) =>
        Progress?.Invoke(new SubagentProgress(JobId, agent, kind, detail));

    private SubagentRunner CreateChildRunner() => new(
        _agents, _modelClient, _capabilities, _defaultModelId, _workspaceRoot, _capabilityDefinitions,
        _depth + 1, _maxDepth, HistoryProvider, JobId)
    {
        ApprovalGate = ApprovalGate,
        ParentMode = ParentMode,
        Progress = Progress
    };

    public async Task<SubagentResult> RunAsync(string agentName, string prompt, string? modelOverride,
        CancellationToken cancellationToken, bool? fork = null, SubagentMode? mode = null)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await RunDispatchAsync(agentName, prompt, modelOverride, cancellationToken, fork, mode)
            .ConfigureAwait(false);
        return result with { DurationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds };
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
        var canNest = _tier == SubagentTier.Stronger && _depth < _maxDepth;
        var allowed = agent.Tools
            .Where(name => builtins.Contains(name) || capabilityNames.Contains(name) ||
                           (canNest && string.Equals(name, SubagentDispatcher.ToolName, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (allowed.Length == 0)
            return Failure(agent.Name, mode, $"子代理 '{agent.Name}' 没有可用的工具。");

        IPureToolDispatcher chain = baseDispatcher;
        if (allowed.Contains(SubagentDispatcher.ToolName, StringComparer.Ordinal))
            chain = new NestedSubagentDispatcher(chain, this.CreateChildRunner());
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

        // 轮次只由代理定义决定；模式层不再截断。
        var maxTurns = Math.Max(1, agent.MaxTurns);
        var engine = new PureCSharpAgentEngine(childDispatcher, _modelClient, maxTurns)
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

            Report(agent.Name, "completed", null);
            return new SubagentResult(true, agent.Name, mode, content, result.ToolResults.Count, null, JobId,
                structured, null, null, null, null, trace,
                result.Usage?.InputTokens, result.Usage?.OutputTokens);
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
                .Append("\n请尽可能高效地并行收集证据，并给出可定位的结论。");
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
        // 取最近一段历史；不要以孤立的 tool 结果开头，总字符预算 100k。
        var start = Math.Max(0, history.Count - 30);
        while (start < history.Count && history[start].Role == PureAgentRole.Tool) start++;
        var kept = new List<PureAgentMessage>();
        var total = 0;
        for (var i = start; i < history.Count; i++)
        {
            var message = history[i];
            var size = (message.Content?.Length ?? 0) + (message.ToolCalls?.Sum(call => call.ArgumentsJson.Length) ?? 0);
            if (total + size > 100_000) break;
            total += size;
            kept.Add(message);
        }
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

            using var process = new Process { StartInfo = psi };
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (stdout.Trim(), process.ExitCode == 0 ? null : stderr.Trim());
        }
        catch (Exception error)
        {
            return (string.Empty, error.Message);
        }
    }
}

