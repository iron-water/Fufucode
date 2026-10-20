using System;
using System.Threading;

namespace Fufucode.Core.Diagnostics.Perf;

/// <summary>
/// 宿主与性能监控插件之间的跨程序集契约。全部放在 Core：插件与 App 都以引用方式共享同一份类型身份，
/// 插件不需要引用 Fufucode.App，也不需要 InternalsVisibleTo。
/// </summary>
public interface IPerfPageHost : IPerfSettingsHost, IPerfPageActions
{
}

/// <summary>插件提供的设置页内容控件（Avalonia Control）。</summary>
public interface IPerfSettingsHost
{
    object SettingsView { get; }
    string? DataDirectory { get; }
    /// <summary>导出诊断包并返回结果路径（或可读的失败原因）。</summary>
    string Export();
}

/// <summary>宿主要求插件执行的用户动作。</summary>
public enum PerfUiRequest
{
    Export,
    OpenDataDirectory,
    Clear,
}

/// <summary>插件可执行的动作。</summary>
public interface IPerfPageActions
{
    string Execute(PerfUiRequest request);
}

/// <summary>插件提供的停靠页内容（Avalonia 控件）。</summary>
public interface IPerfPageViewSource
{
    object PageContent { get; }
}

/// <summary>插件对外暴露的异常摘要（命令面板入口使用）。</summary>
public interface IPerfAnomalySource
{
    int Count { get; }
    string Latest { get; }
}

/// <summary>性能监控插件包的标记接口。</summary>
public interface IPerfPluginPackage
{
}

/// <summary>
/// 宿主提供给插件的界面能力：创建页面提供者、注册设置对象。
/// 宿主实现本接口并通过 <see cref="PluginHostServices"/> 交给插件。
/// </summary>
public interface IPerfHostBridge
{
    /// <summary>宿主创建的页面提供者（宿主侧 PluginPageView 实例）；宿主不可用时为 null。</summary>
    object? CreatePageView();

    /// <summary>插件把设置对象交给宿主（设置页内容、命令面板动作）。</summary>
    void RegisterSettings(object? settings);
}

/// <summary>
/// 宿主服务对象：插件在 <c>InitializeAsync</c> 中通过
/// <c>PluginPackageContext.GetService&lt;IPerfHostServices&gt;()</c> 取得。
/// </summary>
public interface IPerfHostServices
{
    IPerfHostBridge Bridge { get; }
}

/// <summary>进程内唯一实例（同一程序集身份，插件与宿主共享）。</summary>
public static class PluginHostServices
{
    private static IPerfHostServices? s_current;

    public static IPerfHostServices? Current => Volatile.Read(ref s_current);

    public static void Set(IPerfHostServices? services) => Volatile.Write(ref s_current, services);
}

/// <summary>宿主桥的静态入口：插件可在没有服务对象时退回到这里（测试/自测路径）。</summary>
public static class PerfHostAccess
{
    private static IPerfHostBridge? s_bridge;

    public static IPerfHostBridge? Bridge => Volatile.Read(ref s_bridge);

    public static void Set(IPerfHostBridge? bridge) => Volatile.Write(ref s_bridge, bridge);
}
