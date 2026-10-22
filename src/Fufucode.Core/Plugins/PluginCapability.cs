using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;

namespace Fufucode.Core.Plugins;

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
    /// 自动开发工作台：DAG 目标规划与多步骤推进（automation.auto-dev）。
    /// </summary>
    AutoDevelopment,

    /// <summary>NVX 硬件隔离执行沙箱（sandbox.nvx）。</summary>
    SandboxExecution,
}

/// <summary>
/// 能力 → 插件映射（唯一真源）。未列入表中的能力视为没有任何插件提供。
/// </summary>
public static class PluginCapabilityRegistry
{
    /// <summary>
    /// 能力提示语里的短名称：与插件目录的展示名分开，
    /// 使“未安装：请在插件市场中安装 X 扩展”这类固定文案不被目录命名改动破坏。
    /// </summary>
    private static readonly IReadOnlyDictionary<PluginCapability, string> ShortNames =
        new Dictionary<PluginCapability, string>
        {
            [PluginCapability.BrowserControl] = "Browser Use",
            [PluginCapability.ComputerControl] = "Computer Use",
            [PluginCapability.ReverseLab] = "Seep 逆向工程工作台",
            [PluginCapability.AutoDevelopment] = "自动开发工作台",
            [PluginCapability.SandboxExecution] = "NVX 执行沙箱",
        };

    /// <summary>能力在提示语中的短名称；未登记时回退插件 ID。</summary>
    public static string ShortName(PluginCapability capability) =>
        ShortNames.TryGetValue(capability, out var name) ? name : ProviderId(capability) ?? capability.ToString();

    public static readonly IReadOnlyDictionary<PluginCapability, string> ProviderByCapability =
        new Dictionary<PluginCapability, string>
        {
            [PluginCapability.BrowserControl] = "agent.browser-use",
            [PluginCapability.ComputerControl] = "agent.computer-use",
            [PluginCapability.ReverseLab] = "tool.seep-reverse-lab",
            [PluginCapability.AutoDevelopment] = "automation.auto-dev",
            [PluginCapability.SandboxExecution] = "sandbox.nvx",
        };

    /// <summary>提供该能力的插件 ID；没有对应插件时返回 null（不抛异常）。</summary>
    public static string? ProviderId(PluginCapability capability) =>
        ProviderByCapability.TryGetValue(capability, out var id) ? id : null;

    /// <summary>该能力对应的插件定义；未登记时返回 null。</summary>
    public static PluginDefinition? Provider(PluginCapability capability) =>
        ProviderId(capability) is { } id ? PluginMarketplaceCatalog.Find(id) : null;

    /// <summary>该插件提供的能力集合（同一插件可提供多项能力）。</summary>
    public static IReadOnlyList<PluginCapability> CapabilitiesOf(string pluginId) =>
        ProviderByCapability.Where(pair => string.Equals(pair.Value, pluginId, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .ToArray();
}

/// <summary>
/// 能力门禁结果：既记录是否可用，也保留可读原因，便于设置页与工具调度器
/// 显示真实状态，而不是渲染假开关。全部字段来自 <see cref="PluginStateStore"/> 的实时快照。
/// </summary>
public sealed record PluginCapabilityStatus(
    PluginCapability Capability,
    string? PluginId,
    bool IsInstalled,
    bool IsEnabled,
    string Reason)
{
    /// <summary>能力当前是否真的可用（已安装且已启用）。</summary>
    public bool IsAvailable => IsInstalled && IsEnabled;
}

/// <summary>
/// 插件能力网关：把插件市场的装卸/启停状态翻译成能力可用性。
/// 不缓存状态，每次查询都读 <see cref="PluginStateStore"/> 的当前值，
/// 因此安装、启用、停用、卸载都会立即反映到设置页显示与工具注册。
/// </summary>
public sealed class PluginCapabilityGateway
{
    private readonly PluginStateStore _store;

    public PluginCapabilityGateway(PluginStateStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>当前状态存储；调用方只读使用，不绕过网关写状态。</summary>
    public PluginStateStore Store => _store;

    /// <summary>宿主是否出现状态变化；卸载/安装/启停都会触发。</summary>
    public event Action? Changed
    {
        add => _store.Changed += value;
        remove => _store.Changed -= value;
    }

    public PluginCapabilityStatus Status(PluginCapability capability)
    {
        var providerId = PluginCapabilityRegistry.ProviderId(capability);
        if (providerId is null)
            return new PluginCapabilityStatus(capability, null, false, false, "没有插件提供该能力。");

        var state = _store.GetState(providerId);
        var name = PluginCapabilityRegistry.ShortName(capability);
        var reason = !state.IsInstalled
            ? $"未安装：请在插件市场中安装 {name} 扩展"
            : !state.IsEnabled
                ? $"{name} 已安装但已停用，请在插件市场中启用。"
                : $"{name} 插件已启用。";
        return new PluginCapabilityStatus(capability, providerId, state.IsInstalled, state.IsEnabled, reason);
    }

    public bool IsAvailable(PluginCapability capability) => Status(capability).IsAvailable;

    /// <summary>能力注册表快照：每个能力一行，供工具调度器与设置页复用。</summary>
    public IReadOnlyList<PluginCapabilityStatus> AllStatuses() =>
        Enum.GetValues<PluginCapability>().Select(Status).ToArray();
}
