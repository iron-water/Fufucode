using System;
using System.Collections.Generic;

namespace Fufucode.Core.Backend;

public sealed record NativeSubagentDescriptor(string Name, string Description, string Mode);

public sealed record NativeSubagentProgress(string JobId, string Agent, string Kind, string? Detail);

/// <summary>原生会话与可选 Subagent 插件之间的最小运行时契约。</summary>
public interface INativeSubagentRuntime
{
    /// <summary>
    /// 插件是否仍处于已安装且启用状态；旧会话每轮读取，
    /// Shutdown 必须先置 false 再取消在途调用。
    /// </summary>
    bool IsActive => true;
    /// <summary>旧插件默认只声明它实际支持的 run/list。</summary>
    PureToolDefinition ToolDefinition => NativeSubagentContract.Definition;
    IPureToolDispatcher Wrap(IPureToolDispatcher inner);
    IReadOnlyList<NativeSubagentDescriptor> Agents { get; }
    string BuildPromptSection();
    /// <summary>
    /// 让子代理执行一次读任务并返回文本结果；会话取名等功能使用。
    /// 未实现或失败时返回 null，调用方必须回退到本地规则，不能假定可用。
    /// </summary>
    System.Threading.Tasks.Task<string?> RunUtilityAsync(string prompt,
        System.Threading.CancellationToken cancellationToken) =>
        System.Threading.Tasks.Task.FromResult<string?>(null);
    Func<PureToolCall, System.Threading.CancellationToken, System.Threading.Tasks.Task<bool>>? ApprovalGate { get; set; }
    Func<IReadOnlyList<PureAgentMessage>>? HistoryProvider { get; set; }
    Func<PermissionMode>? ParentMode { get; set; }
    Action<NativeSubagentProgress>? Progress { get; set; }
}

public interface INativeSubagentFactory
{
    INativeSubagentRuntime Create(
        IPureModelClient modelClient,
        IPureToolDispatcher capabilities,
        string? defaultModelId,
        string? workspaceRoot,
        IReadOnlyList<PureToolDefinition> capabilityDefinitions);
}

/// <summary>由 Subagent 插件在激活时注册；核心未安装插件时保持 null。</summary>
public static class NativeSubagentRegistry
{
    private static readonly object Gate = new();
    private static INativeSubagentFactory? _factory;

    public static INativeSubagentFactory? Factory
    {
        get { lock (Gate) return _factory; }
    }

    public static void Register(INativeSubagentFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (Gate)
        {
            _factory = factory;
        }
    }

    public static void Unregister(INativeSubagentFactory factory)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_factory, factory)) _factory = null;
        }
    }
}

public static class NativeSubagentContract
{
    public const string ToolName = "subagent";

    private const string EvidenceProperties = """
            "jobId": { "type": "string", "description": "交付卡中的任务 ID，不是文件路径" },
            "section": { "type": "string", "enum": ["report", "patch", "commands", "files", "trace"], "description": "默认 report；commands 是工具实录，不是验收结论" },
            "offset": { "type": "integer", "minimum": 0, "description": "续读上页给出的 nextOffset；默认 0" }
        """;

    // A delegated evidence reader has no list/run action, even in a patch worktree.
    public static PureToolDefinition EvidenceReader { get; } = new(ToolName, "只读父代理明确转交的证据；不能派发子代理。",
        $$"""
        { "type": "object", "required": ["action", "jobId"], "properties": {
            "action": { "type": "string", "enum": ["read_evidence"] },
            {{EvidenceProperties}}
        } }
        """);

    /// <summary>
    /// 父会话与嵌套子代理共用的唯一 Schema。
    /// `contextRefs` 显式转交当前父会话的结果，读取证据不启动模型或授予执行权限。
    /// </summary>
    public static PureToolDefinition Definition { get; } = Build(false);
    public static PureToolDefinition WithEvidence { get; } = Build(true);

    private static PureToolDefinition Build(bool handoff) => new(
        ToolName,
        "派发一个隔离上下文的子代理。read 模式只读、快速，适合搜索、定位与审查；" +
        "patch 模式在临时 git worktree 中修改并返回候选补丁（不直接写入父工作区）。" +
        (handoff ? "完成后用 jobId 回读原始证据，或通过 contextRefs 转交下一位子代理；不要复制整段报告。" : "省略 prompt 时列出可用子代理。"),
        $$"""
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["run", "list"{{(handoff ? ", \"read_evidence\"" : "")}}], "description": "默认 run；list 列出代理" },
            {{(handoff ? HandoffProperties : "")}}
            "agent": { "type": "string", "description": "子代理名称；read 默认 explore，patch 默认 general" },
            "prompt": { "type": "string", "description": "任务说明；省略时列出可用子代理" },
            "mode": { "type": "string", "enum": ["read", "patch"], "description": "覆盖代理模式；与代理定义冲突时会被拒绝" },
            "context": { "type": "string", "description": "已知线索：文件路径、符号名、报错原文、已排除的方向。用于减少子代理的发现轮次" },
            "model": { "type": "string", "description": "可选模型 id" },
            "fork": { "type": "boolean", "description": "是否继承父会话最近历史" }
          }
        }
        """);

    private const string HandoffProperties = EvidenceProperties + ",\n" + """
            "contextRefs": { "type": "array", "items": { "type": "string" }, "description": "run 时明确交给本次子代理的 jobId 列表，按需读取，不复制历史" },
            "baseRef": { "type": "string", "description": "仅 patch：转交此候选 jobId，并从它的原始提交开临时 worktree 复测；不自动应用补丁" },
        """;
}

public interface INativeSubagentDispatcher
{
    IPureToolDispatcher Inner { get; }

    /// <summary>插件是否真正安装并启用；未激活的调度器不得改变底层权限链。</summary>
    bool IsActive { get; }
}
