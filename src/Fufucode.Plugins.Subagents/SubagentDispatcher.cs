using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Backend;

/// <summary>`subagent` 工具：read 只读快速探索；patch 在隔离 worktree 中修改并返回补丁。</summary>
public sealed class SubagentDispatcher : IPureToolDispatcher, INativeSubagentDispatcher
{
    public const string ToolName = "subagent";

    /// <summary>
    /// 复用 Core 的唯一 Schema：父会话与嵌套子代理必须声明同一套参数，
    /// 否则模型在两层拿到的 `context`、`action` 能力不一致。
    /// </summary>
    public static PureToolDefinition Definition => NativeSubagentContract.WithEvidence;

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
    public Func<bool>? ActiveProvider { get; init; }
    bool INativeSubagentDispatcher.IsActive => ActiveProvider?.Invoke() ?? IsActive;

    public Task<PureToolResult> DispatchAsync(PureToolCall call, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(call.Name, ToolName, StringComparison.Ordinal))
            return _inner.DispatchAsync(call, cancellationToken);
        // 撤销后连 list 也不能用：旧会话可能仍持有 wrapper并绕过声明检查。
        if (ActiveProvider is { } active && !active())
            return Task.FromResult(Revoked(call));
        return HandleAsync(call, _runner, cancellationToken);
    }

    private static PureToolResult Revoked(PureToolCall call) =>
        new(call.Id, false, "子代理插件已卸载，调用被拒绝。");

    internal static async Task<PureToolResult> HandleAsync(PureToolCall call, SubagentRunner? runner,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, SubagentEvidence>? inherited = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (runner?.Lifetime is { IsActive: false })
            return Revoked(call);
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

        if (root.ValueKind != JsonValueKind.Object) return new(call.Id, false, "subagent 参数必须是对象。");
        var action = String(root, "action");
        if (action is "read_evidence")
        {
            var id = SubagentEvidenceStore.Id(root);
            var item = id is null ? null : inherited?.GetValueOrDefault(id) ?? runner?.Evidence.Find(id);
            return item?.Page(call.Id, root) ?? new(call.Id, false, SubagentEvidenceStore.Unavailable);
        }
        if (runner is null) return new(call.Id, false, "仅授权 read_evidence，不能派发或列出子代理。");
        if (action is "list")
            return Listing(call, runner);
        if (action is not null and not "run")
            return new PureToolResult(call.Id, false, $"未知 action '{action}'；只支持 run/list/read_evidence。");

        var prompt = String(root, "prompt");
        if (string.IsNullOrWhiteSpace(prompt)) return root.TryGetProperty("contextRefs", out _) || root.TryGetProperty("baseRef", out _)
            ? new(call.Id, false, "转交证据时需要任务 prompt。") : Listing(call, runner);

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

        var context = String(root, "context");
        if (!string.IsNullOrWhiteSpace(context))
            prompt = $"{prompt}\n\n## 父代理提供的线索（不是运行时验证）\n{context}";

        try
        {
            var grant = runner.Evidence.Bind(root, inherited);
            if (grant?.Basis is not null && definition.Mode != SubagentMode.Patch) return new(call.Id, false, "baseRef 需要 patch 工作副本；只读审查请用 contextRefs。");
            var result = await runner.RunWithEvidenceAsync(definition.Name, prompt, String(root, "model"),
                cancellationToken, root.TryGetProperty("fork", out var f) && f.ValueKind == JsonValueKind.True, requestedMode, grant).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (runner.Lifetime is { IsActive: false }) return Revoked(call);
            var item = new SubagentEvidence(result, runner.Security);
            var card = item.Card(call.Id, result);
            runner.Evidence.Publish(item);
            return card;
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

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

}
