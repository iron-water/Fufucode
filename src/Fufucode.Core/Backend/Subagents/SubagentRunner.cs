using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Backend;

/// <summary>一次子代理执行结果。</summary>
public sealed record SubagentResult(bool Success, string AgentName, string Content, int ToolCalls, string? Error);

/// <summary>
/// 进程内子代理运行器：子代理拥有独立上下文与工具白名单，结束后只把最终文本交回父会话，
/// 避免探索过程污染父上下文。第一版不支持嵌套（子代理工具中不声明 subagent）。
/// </summary>
public sealed class SubagentRunner
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(5);

    private readonly AgentRegistry _agents;
    private readonly IPureModelClient _modelClient;
    private readonly IPureToolDispatcher _dispatcher;
    private readonly string? _defaultModelId;
    private readonly string? _workspaceRoot;
    private readonly Dictionary<string, PureToolDefinition> _builtinDefinitions;

    public SubagentRunner(AgentRegistry agents, IPureModelClient modelClient, IPureToolDispatcher dispatcher,
        string? defaultModelId, string? workspaceRoot = null)
    {
        _agents = agents;
        _modelClient = modelClient;
        _dispatcher = dispatcher;
        _defaultModelId = defaultModelId;
        _workspaceRoot = workspaceRoot;
        _builtinDefinitions = ProductionToolDispatcher.GetBuiltinToolDefinitions()
            .GroupBy(definition => definition.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    /// <summary>父会话的审批门；子代理的变更类工具同样需要用户确认。</summary>
    public Func<PureToolCall, CancellationToken, Task<bool>>? ApprovalGate { get; set; }

    public IReadOnlyList<AgentDefinition> Agents => _agents.All;
    public string BuildPromptSection() => _agents.BuildPromptSection();

    public async Task<SubagentResult> RunAsync(string agentName, string prompt, string? modelOverride,
        CancellationToken cancellationToken)
    {
        if (!_agents.TryGet(agentName, out var agent))
            return new SubagentResult(false, agentName, string.Empty, 0,
                $"未知子代理 '{agentName}'。可用：{string.Join(", ", _agents.All.Select(a => a.Name))}。");
        if (string.IsNullOrWhiteSpace(prompt))
            return new SubagentResult(false, agent.Name, string.Empty, 0, "子代理任务 prompt 不能为空。");

        // 只允许内置工具；禁止递归调用 subagent，避免无界派生。
        var allowed = agent.Tools
            .Where(name => _builtinDefinitions.ContainsKey(name))
            .Where(name => !string.Equals(name, SubagentDispatcher.ToolName, StringComparison.Ordinal))
            .Where(name => !string.Equals(name, "update_plan", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (allowed.Length == 0)
            return new SubagentResult(false, agent.Name, string.Empty, 0,
                $"子代理 '{agent.Name}' 没有可用的内置工具。");

        var definitions = allowed.Select(name => _builtinDefinitions[name]).ToArray();
        var childDispatcher = new AllowedToolDispatcher(_dispatcher,
            allowed.ToHashSet(StringComparer.Ordinal));
        var engine = new PureCSharpAgentEngine(childDispatcher, _modelClient, agent.MaxTurns)
        {
            ApprovalGate = ApprovalGate
        };

        var systemPrompt = string.IsNullOrWhiteSpace(_workspaceRoot)
            ? agent.SystemPrompt
            : agent.SystemPrompt + "\n\n工作区根目录：" + _workspaceRoot;
        var request = new PureModelRequest(
            new[]
            {
                new PureAgentMessage(PureAgentRole.System, systemPrompt),
                new PureAgentMessage(PureAgentRole.User, prompt)
            },
            allowed,
            modelOverride ?? agent.ModelId ?? _defaultModelId,
            definitions);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RunTimeout);
        try
        {
            var result = await engine.RunTurnAsync(request, timeout.Token).ConfigureAwait(false);
            if (result.Status == PureAgentTurnStatus.Failed)
                return new SubagentResult(false, agent.Name, result.Response.Text ?? string.Empty,
                    result.ToolResults.Count, result.Error ?? "子代理执行失败。");

            var content = result.Response.Text ?? string.Empty;
            if (content.Length == 0)
                content = result.ToolResults.Count > 0
                    ? "（子代理执行了工具但没有返回文本。）"
                    : "（子代理没有返回内容。）";
            return new SubagentResult(true, agent.Name, content, result.ToolResults.Count, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SubagentResult(false, agent.Name, string.Empty, 0,
                $"子代理执行超时（{RunTimeout.TotalMinutes:0} 分钟）。");
        }
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
}

/// <summary>`subagent` 工具：派发隔离子任务并只返回最终结论。</summary>
public sealed class SubagentDispatcher(IPureToolDispatcher inner, SubagentRunner runner) : IPureToolDispatcher
{
    public const string ToolName = "subagent";

    public IPureToolDispatcher Inner => inner;

    public static PureToolDefinition Definition { get; } = new(
        ToolName,
        "把一项边界明确的任务交给隔离的子代理执行。子代理拥有独立上下文与受限工具，只返回最终结论。" +
        "省略 prompt 时列出可用子代理。",
        """
        {
            "type": "object",
            "properties": {
                "agent": { "type": "string", "description": "子代理名称，默认 general" },
                "prompt": { "type": "string", "description": "交给子代理的完整任务说明" },
                "model": { "type": "string", "description": "可选：为该子代理指定模型 id" }
            }
        }
        """);

    public Task<PureToolResult> DispatchAsync(PureToolCall call, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(call.Name, ToolName, StringComparison.Ordinal))
            return inner.DispatchAsync(call, cancellationToken);
        return HandleAsync(call, cancellationToken);
    }

    private async Task<PureToolResult> HandleAsync(PureToolCall call, CancellationToken cancellationToken)
    {
        string? agent = null;
        string? prompt = null;
        string? model = null;
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("agent", out var agentElement) &&
                    agentElement.ValueKind == JsonValueKind.String)
                    agent = agentElement.GetString();
                if (root.TryGetProperty("prompt", out var promptElement) &&
                    promptElement.ValueKind == JsonValueKind.String)
                    prompt = promptElement.GetString();
                if (root.TryGetProperty("model", out var modelElement) &&
                    modelElement.ValueKind == JsonValueKind.String)
                    model = modelElement.GetString();
            }
        }
        catch (JsonException error)
        {
            return new PureToolResult(call.Id, false, $"subagent 参数 JSON 无效：{error.Message}");
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            var listing = string.Join("\n", runner.Agents.Select(a => $"- {a.Name}: {a.Description}"));
            return new PureToolResult(call.Id, true, "可用子代理：\n" + listing);
        }

        var selected = string.IsNullOrWhiteSpace(agent) ? "general" : agent!;
        var result = await runner.RunAsync(selected, prompt!, model, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
            return new PureToolResult(call.Id, false,
                $"子代理 {result.AgentName} 失败：{result.Error}");

        var content = new StringBuilder();
        content.Append("[子代理 ").Append(result.AgentName).Append("]");
        content.Append("（工具调用 ").Append(result.ToolCalls).AppendLine(" 次）");
        content.AppendLine(result.Content);
        return new PureToolResult(call.Id, true, Cap(content.ToString().TrimEnd()));
    }

    private static string Cap(string text) =>
        text.Length <= 32 * 1024 ? text : text[..32_768] + "\n... [子代理输出已截断]";
}
