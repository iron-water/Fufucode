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

    /// <summary>
    /// 父会话与嵌套子代理共用的唯一 Schema。
    /// `action` 支持 list（列出可用代理）与 run（默认）；`context` 用于预取已知线索，
    /// 让子代理不必从零开始发现文件。
    /// </summary>
    public static PureToolDefinition Definition { get; } = new(
        ToolName,
        "派发一个隔离上下文的子代理。read 模式只读、快速，适合搜索、定位与审查；" +
        "patch 模式在临时 git worktree 中修改并返回候选补丁（不直接写入父工作区）。" +
        "省略 agent 时按 mode 选择内置代理；省略 prompt 时列出可用代理。",
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["run", "list"], "description": "run 执行任务（默认）；list 列出可用子代理" },
            "agent": { "type": "string", "description": "子代理名称；read 默认 explore，patch 默认 general" },
            "prompt": { "type": "string", "description": "任务说明；省略时列出可用子代理" },
            "mode": { "type": "string", "enum": ["read", "patch"], "description": "覆盖代理模式；与代理定义冲突时会被拒绝" },
            "context": { "type": "string", "description": "已知线索：文件路径、符号名、报错原文、已排除的方向。用于减少子代理的发现轮次" },
            "model": { "type": "string", "description": "可选模型 id" },
            "fork": { "type": "boolean", "description": "是否继承父会话最近历史" }
          }
        }
        """);
}

public interface INativeSubagentDispatcher
{
    IPureToolDispatcher Inner { get; }

    /// <summary>插件是否真正安装并启用；未激活的调度器不得改变底层权限链。</summary>
    bool IsActive { get; }
}
