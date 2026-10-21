using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Backend;

/// <summary>`subagent` 工具：read 只读快速探索；patch 在隔离 worktree 中修改并返回补丁。</summary>
public sealed class SubagentDispatcher : IPureToolDispatcher, INativeSubagentDispatcher
{
    public const string ToolName = "subagent";

    public static PureToolDefinition Definition { get; } = new(
        ToolName,
        "运行子代理。read 模式只读、快速，适合搜索与审查；patch 模式在临时 worktree 中修改，" +
        "返回 unified diff 候选补丁（不直接交付父工作区）。省略 agent 时按 mode 选择内置代理。",
        """
        {
            "type": "object",
            "properties": {
                "agent": { "type": "string", "description": "子代理名称；read 默认 explore，patch 默认 general" },
                "prompt": { "type": "string", "description": "任务说明；省略时列出可用子代理" },
                "mode": { "type": "string", "enum": ["read", "patch"], "description": "覆盖代理模式" },
                "model": { "type": "string", "description": "可选模型 id" },
                "fork": { "type": "boolean", "description": "是否继承父会话最近历史" }
            }
        }
        """);

    private readonly IPureToolDispatcher _inner;
    private readonly SubagentRunner _runner;

    public SubagentDispatcher(IPureToolDispatcher inner, SubagentRunner runner)
    {
        _inner = inner;
        _runner = runner;
    }

    public IPureToolDispatcher Inner => _inner;

    /// <summary>裸调度器默认不视为已安装；插件运行时覆盖为 true。</summary>
    public bool IsActive { get; init; }

    public Task<PureToolResult> DispatchAsync(PureToolCall call, CancellationToken cancellationToken = default) =>
        string.Equals(call.Name, ToolName, StringComparison.Ordinal)
            ? HandleAsync(call, _runner, cancellationToken)
            : _inner.DispatchAsync(call, cancellationToken);

    internal static Task<PureToolResult> HandleForegroundAsync(PureToolCall call, SubagentRunner runner,
        CancellationToken cancellationToken) => HandleAsync(call, runner, cancellationToken);

    private static async Task<PureToolResult> HandleAsync(PureToolCall call, SubagentRunner runner,
        CancellationToken cancellationToken)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            return new PureToolResult(call.Id, false, "subagent 参数 JSON 无效：" + error.Message);
        }

        var action = String(root, "action");
        if (action is "list")
            return Listing(call, runner);
        if (action is not null and not "run")
            return new PureToolResult(call.Id, false, $"未知 action '{action}'；只支持 run/list。");

        var prompt = String(root, "prompt");
        if (string.IsNullOrWhiteSpace(prompt)) return Listing(call, runner);

        var requestedMode = String(root, "mode")?.ToLowerInvariant() switch
        {
            "patch" => SubagentMode.Patch,
            "read" => SubagentMode.Read,
            _ => (SubagentMode?)null
        };
        var agentName = String(root, "agent");
        if (string.IsNullOrWhiteSpace(agentName))
            agentName = requestedMode == SubagentMode.Patch ? "general" : "explore";

        var definition = runner.Agents.FirstOrDefault(agent =>
            string.Equals(agent.Name, agentName, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
            return new PureToolResult(call.Id, false,
                $"未知子代理 '{agentName}'。可用：{string.Join(", ", runner.Agents.Select(agent => agent.Name))}。");
        if (requestedMode is { } mode && mode != definition.Mode)
            return new PureToolResult(call.Id, false,
                $"子代理 '{definition.Name}' 是 {definition.Mode.ToString().ToLowerInvariant()} 模式；" +
                $"请省略 mode 或使用对应模式的代理。");

        try
        {
            var result = await runner.RunAsync(definition.Name, prompt, String(root, "model"),
                cancellationToken, Bool(root, "fork"), requestedMode).ConfigureAwait(false);
            return ToToolResult(call, result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return new PureToolResult(call.Id, false, "子代理调用失败：" + error.Message);
        }
    }

    private static PureToolResult Listing(PureToolCall call, SubagentRunner runner) =>
        new(call.Id, true, "可用子代理：\n" + string.Join("\n", runner.Agents.Select(agent =>
            $"- {agent.Name} [{agent.Mode.ToString().ToLowerInvariant()}]: {agent.Description}")));

    private static PureToolResult ToToolResult(PureToolCall call, SubagentResult result)
    {
        if (!result.Success)
        {
            var failed = new StringBuilder($"子代理 {result.AgentName} 失败：{result.Error}");
            if (result.Mode == SubagentMode.Patch)
            {
                failed.AppendLine();
                AppendPatch(failed, result);
            }
            return new PureToolResult(call.Id, false, Clamp(failed.ToString()));
        }

        var mode = result.Mode == SubagentMode.Patch ? "patch" : "read";
        var builder = new StringBuilder();
        builder.Append("[子代理 ").Append(result.AgentName).Append(" · ").Append(mode).Append(']')
            .Append("（工具调用 ").Append(result.ToolCalls).Append(" 次");
        if (result.DurationMs is { } duration) builder.Append("，").Append(duration).Append(" ms");
        builder.AppendLine("）");
        builder.AppendLine(result.Content);

        if (result.Mode == SubagentMode.Patch)
            AppendPatch(builder, result);

        return new PureToolResult(call.Id, true, Clamp(builder.ToString()));
    }

    private static void AppendPatch(StringBuilder builder, SubagentResult result)
    {
        if (result.ChangedFiles is { Count: > 0 } files)
            builder.Append("改动文件：").AppendLine(string.Join(", ", files));
        if (result.BaseHead is { Length: > 0 } head)
            builder.Append("基线 HEAD：").AppendLine(head[..Math.Min(head.Length, 12)]);
        if (result.ToolTrace is { Count: > 0 } trace)
        {
            var verifications = trace.Where(item => item.Tool == "run_command").ToArray();
            if (verifications.Length > 0)
            {
                builder.AppendLine("验证命令：");
                foreach (var item in verifications)
                    builder.Append("- ").Append(item.Success ? "[通过] " : "[失败] ")
                        .AppendLine(item.ArgumentsJson);
            }
        }
        if (result.PatchPath is { Length: > 0 } patchPath)
            builder.Append("补丁文件：").AppendLine(patchPath);
        if (result.Patch is { Length: > 0 } patch)
            builder.AppendLine("```diff").AppendLine(patch).AppendLine("```");
        builder.Append("以上为候选补丁，未写入父工作区；由父会话审阅后决定是否应用或升级为线程。");
    }

    private static string Clamp(string text)
    {
        text = text.TrimEnd();
        return text.Length <= 64 * 1024 ? text : text[..65_536] + "\n... [子代理输出已截断]";
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
