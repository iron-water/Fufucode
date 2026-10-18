using System;
using Fufucode.App.ViewModels;
using Fufucode.Core;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Features.Plugins;

/// <summary>
/// 设置页的插件能力查询接缝：把 <see cref="PluginStateStore"/> 的实时状态
/// 投影成可绑定的布尔值与可读说明。任何调用方都不应自行判断插件是否可用，
/// 统一走这里，保证"未安装"不被渲染成可用。
/// </summary>
public static class PluginAccess
{
    /// <summary>Browser Use 插件 ID。</summary>
    public const string BrowserUsePluginId = "agent.browser-use";

    /// <summary>Computer Use 插件 ID。</summary>
    public const string ComputerUsePluginId = "agent.computer-use";

    /// <summary>Seep 逆向工程工作台插件 ID。</summary>
    public const string ReverseLabPluginId = SeepReverseLabAssets.PluginId;

    /// <summary>手机远程控制服务插件 ID。</summary>
    public const string RemoteControlPluginId = "service.remote-control";

    /// <summary>语音朗读服务插件 ID。</summary>
    public const string SpeechPluginId = "service.speech-synthesis";

    /// <summary>自动开发工作台插件 ID。</summary>
    public const string AutoDevPluginId = "automation.auto-dev";

    /// <summary>用户定时任务插件 ID。</summary>
    public const string CronPluginId = "automation.cron";

    /// <summary>Windows Aero 玻璃皮肤插件 ID。</summary>
    public const string AeroPluginId = "skin.aero";

    /// <summary>未安装时统一使用的提示文案（含插件市场入口指向）。</summary>
    public const string BrowserUseNotInstalledMessage = "未安装：请在插件市场中安装 Browser Use 扩展";

    private static PluginCapabilityGateway? Gateway(WorkbenchCustomizationViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        if (vm.DataRoot is not { Length: > 0 } root) return null;
        return new PluginCapabilityGateway(new PluginStateStore(new AppDataLayout(root)));
    }

    /// <summary>浏览器高级控制（CDP / Browser Use 动作契约）是否真正解锁。</summary>
    public static bool AdvancedControlEnabled(WorkbenchCustomizationViewModel vm) =>
        Gateway(vm)?.IsAvailable(PluginCapability.BrowserControl) ?? false;

    /// <summary>电脑操控工具是否真正解锁。</summary>
    public static bool ComputerControlEnabled(WorkbenchCustomizationViewModel vm) =>
        Gateway(vm)?.IsAvailable(PluginCapability.ComputerControl) ?? false;

    /// <summary>浏览器控制的真实状态说明；未安装时给出安装指引。</summary>
    public static string Status(WorkbenchCustomizationViewModel vm) =>
        Gateway(vm)?.Status(PluginCapability.BrowserControl).Reason ?? BrowserUseNotInstalledMessage;

    /// <summary>电脑操控的真实状态说明。</summary>
    public static string ComputerStatus(WorkbenchCustomizationViewModel vm) =>
        Gateway(vm)?.Status(PluginCapability.ComputerControl).Reason ?? "未安装：请在插件市场中安装 Computer Use 扩展";

    /// <summary>Seep 逆向工程工作台是否真正解锁（已安装且已启用）。</summary>
    public static bool ReverseLabEnabled(WorkbenchCustomizationViewModel vm) =>
        Gateway(vm)?.IsAvailable(PluginCapability.ReverseLab) ?? false;

    /// <summary>Seep 工作台的真实状态说明；未安装时给出插件市场入口指向。</summary>
    public static string ReverseLabStatus(WorkbenchCustomizationViewModel vm) =>
        Gateway(vm)?.Status(PluginCapability.ReverseLab).Reason ?? "未安装：请在插件市场中安装 Seep 逆向工程工作台";

    /// <summary>直接查询指定数据根下的插件是否真正已安装且已启用。</summary>
    public static bool IsPluginActive(string? dataRoot, string pluginId)
    {
        if (string.IsNullOrWhiteSpace(dataRoot) || !System.IO.Directory.Exists(dataRoot))
            return false;
        try
        {
            var store = new PluginStateStore(new AppDataLayout(dataRoot));
            return store.IsActive(pluginId);
        }
        catch
        {
            return false;
        }
    }
}
