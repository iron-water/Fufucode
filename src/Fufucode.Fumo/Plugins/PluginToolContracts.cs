using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;

namespace Fufucode.Core.Plugins;

// 插件工具契约属于 FUMO 内核：内核按这些接口声明、路由与守卫插件工具；
// 能力注册表、门禁与插件市场在 Core 里实现它们。

/// <summary>
/// 插件能力标识。能力与插件不是一一对应的实现细节：调用方按能力查询，
/// 由 <see cref="PluginCapabilityRegistry"/> / <see cref="PluginCapabilityGateway"/>
/// 解析到当前真正安装并启用的插件。
/// </summary>
public enum PluginCapability
{
    /// <summary>浏览器控制：CDP 接口与 Browser Use 动作契约（agent.browser-use）。</summary>
    BrowserControl,

    /// <summary>电脑操控：Win32 屏幕截图与输入模拟（agent.computer-use）。</summary>
    ComputerControl,

    /// <summary>
    /// 逆向工程工作台：Seep Reverse Lab 的 23 个 Radare2 / JADX / Apktool / Frida
    /// 工具与合规转译中继（tool.seep-reverse-lab）。
    /// </summary>
    ReverseLab,

    /// <summary>
    /// 保留旧工具契约的标识和枚举序号。内建 Graph 由应用组合根注册并校验项目权限。
    /// </summary>
    AutoDevelopment,

    /// <summary>NVX 硬件隔离执行沙箱（sandbox.nvx）。</summary>
    SandboxExecution,

    /// <summary>用户级记忆：保存、删除记忆条目并注入会话（tool.memory）。</summary>
    Memory,
}

/// <summary>
/// 插件工具在工具调度表里的注册项。动作契约（名称/说明/参数 JSON Schema）
/// 由插件自身给出，核心调度器只按当前可用性决定是否暴露。
/// </summary>
public sealed record PluginToolRegistration(
    string Name,
    string Description,
    string ParametersJsonSchema,
    PluginCapability Capability);

/// <summary>
/// 插件工具处理器：只负责执行已经通过门禁的调用。
/// 实现方不能自行绕过 <see cref="PluginToolRegistry"/> 的门禁。
/// </summary>
public interface IPluginToolHandler
{
    /// <summary>该处理器提供的全部工具动作契约。</summary>
    IReadOnlyList<PluginToolRegistration> Registrations { get; }

    /// <summary>执行一次已注册的工具调用。</summary>
    Task<PureToolResult> ExecuteAsync(PureToolCall call, CancellationToken cancellationToken = default);

    /// <summary>
    /// 该处理器是否认领某个工具名。默认按已注册契约精确匹配。
    ///
    /// 用于动态工具集（例如 MCP 子进程尚未启动、工具清单还没取到）：
    /// 此时工具确实不该出现在调度表中，但调用被拒时调用方仍需看到真实原因
    /// （未安装 / 已停用 / 子进程未运行），而不是泛化的“未注册”。
    /// </summary>
    bool OwnsToolName(string toolName) =>
        Registrations.Any(r => string.Equals(r.Name, toolName, StringComparison.Ordinal));

    /// <summary>
    /// 认领该工具名但当前不可调度时，给出处理器自己的可读原因；返回 null 表示没有额外说明。
    /// 仅在 <see cref="OwnsToolName"/> 为 true 时被调用。
    /// </summary>
    string? ExplainUnavailable(string toolName) => null;

    /// <summary>该处理器服务的插件能力；未声明时无法给出能力级不可用原因。</summary>
    PluginCapability? Capability => null;
}

/// <summary>原生会话声明插件工具所需的最小只读目录（实现方可聚合多个注册表）。</summary>
public interface IPluginToolCatalog
{
    IReadOnlyList<string> ActiveToolNames { get; }
    IReadOnlyList<PluginToolRegistration> ActiveRegistrations { get; }
}

/// <summary>把清单外调用委托给内层调度器的包装层；内核沿它向下找到生产调度器（权限模式与计划在那里）。</summary>
public interface IDelegatingToolDispatcher
{
    IPureToolDispatcher? InnerDispatcher { get; }
}

/// <summary>Tools owned by the application composition root, with domain authority enforced by their service.</summary>
public interface IApplicationToolCatalog
{
    IReadOnlyList<string> ApplicationToolNames { get; }
}
