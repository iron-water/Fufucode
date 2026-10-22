using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Fufucode.App.Features.Performance;
using Fufucode.App.Infrastructure;
using Fufucode.Core.Diagnostics.Perf;

namespace Fufucode.App;

/// <summary>
/// 性能监控接线：宿主采集器、插件服务交付、停靠页动作、设置页内容与命令面板入口。
/// 插件未安装时“性能”动作给出真实原因，设置页显示安装提示；宿主采集器仍记录每回合聚合。
/// </summary>
public partial class MainWindow
{
    private PerfHostWiring? _perfWiring;
    private string? _perfInitError;

    /// <summary>
    /// 单行诊断（无头预览 / 自测使用）：接线是否创建、插件是否接通、页面是否注册、最近错误。
    /// 只读本窗口实例的接线，不读进程静态字段。
    /// </summary>
    internal string PerfDiagnosticLine =>
        $"wiring={(_perfWiring is null ? "null" : "ok")}; wired={(_perfWiring?.Bridge.WiredUtc is null ? "-" : "yes")}; " +
        $"page={(_perfWiring?.Bridge.Page is null ? "-" : "ok")}; error={_perfWiring?.Bridge.LastError ?? _perfInitError ?? "-"}; " +
        $"registered={_workbenchPages.ContainsKey("perf")}; hint={_vm?.Settings.PerformanceHintText ?? "-"}; " +
        $"loadErrors={string.Join("; ", _pluginPageHost?.LoadErrors.Select(pair => pair.Key + "=" + pair.Value) ?? [])}";

    /// <summary>
    /// 创建宿主采集器与插件桥，并把服务解析器接到页面宿主的插件管理器。
    /// 插件可能已经激活（页面宿主开始前）：这里补一次激活扫描并重新同步页面贡献。
    /// </summary>
    private void InitializePerformance(string dataRoot)
    {
        try
        {
            _perfWiring = PerfHostWiring.Create(dataRoot);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            // 数据目录不可写：性能页面不可用，但不阻止应用启动。
            _perfWiring = null;
            _perfInitError = "性能监控不可用（数据目录无法写入）：" + error.Message;
            System.Diagnostics.Trace.TraceWarning("Perf host collector unavailable: {0}", error.Message);
        }

        Register("perf", "性能监控", "诊断", "", "打开性能分析窗口：概览、启动、全链路 span、时间线与检查器", () =>
        {
            if (!_workbenchPages.ContainsKey("perf")) return Task.CompletedTask;
            OpenDockPage("perf", DockZone.Right);
            FloatDockPage("perf", preferredWidth: 1180, preferredHeight: 760);
            return Task.CompletedTask;
        }, () => _workbenchPages.ContainsKey("perf") ? null : "未安装或未启用性能监控插件");
    }

    /// <summary>插件页面集合变化后刷新设置页“性能”分类的内容与可用性。</summary>
    private void SyncPerformanceSettings()
    {
        if (_vm is null) return;
        var bridge = _perfWiring?.Bridge;
        var view = bridge?.Page?.SettingsView as Control;
        _vm.Settings.PerformanceContent = view;
        _vm.Settings.PerformancePluginAvailable = view is not null;
        _vm.Settings.PerformanceDiagnostic =
            _workbenchPages.ContainsKey("perf") ? null
            : _perfInitError ?? bridge?.LastError
              ?? (bridge?.WiredUtc is null ? "性能监控插件未安装或未启用。" : "性能监控插件已激活，但页面尚未注册。");
    }

    private void DisposePerformance()
    {
        _perfWiring?.Dispose();
        _perfWiring = null;
    }
}
