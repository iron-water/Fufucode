using System;
using System.Collections.Generic;

namespace Fufucode.Core.Backend;

/// <summary>用户手动选择的子代理档位。插件安装后固定默认为 Faster。</summary>
public enum SubagentTier
{
    Faster,
    Stronger,
}

public sealed record NativeSubagentDescriptor(string Name, string Description, string Mode);

public sealed record NativeSubagentProgress(string JobId, string Agent, string Kind, string? Detail);

/// <summary>原生会话与可选 Subagent 插件之间的最小运行时契约。</summary>
public interface INativeSubagentRuntime
{
    SubagentTier Tier { get; }
    void SetTier(SubagentTier tier);
    IPureToolDispatcher Wrap(IPureToolDispatcher inner);
    IReadOnlyList<NativeSubagentDescriptor> Agents { get; }
    string BuildPromptSection();
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

    void SetTier(SubagentTier tier);
}

/// <summary>由 Subagent 插件在激活时注册；核心未安装插件时保持 null。</summary>
public static class NativeSubagentRegistry
{
    private static readonly object Gate = new();
    private static INativeSubagentFactory? _factory;
    private static SubagentTier _tier = SubagentTier.Faster;

    public static SubagentTier Tier
    {
        get { lock (Gate) return _tier; }
        set { lock (Gate) { _tier = value; _factory?.SetTier(value); } }
    }

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
            _factory.SetTier(_tier);
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

    public static PureToolDefinition Definition { get; } = new(
        ToolName,
        "运行已安装的子代理插件；默认档位由用户设置决定。",
        """
        {
          "type":"object",
          "properties":{
            "agent":{"type":"string","description":"子代理名称；省略时使用默认代理"},
            "prompt":{"type":"string","description":"任务说明"},
            "model":{"type":"string","description":"可选模型 id"},
            "fork":{"type":"boolean","description":"是否继承父会话上下文"}
          }
        }
        """);
}

public interface INativeSubagentDispatcher
{
    IPureToolDispatcher Inner { get; }
}
