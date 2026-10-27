using Avalonia.Controls;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// 性能页宿主适配器。实际界面在 <see cref="PerfCenterView"/>：
/// 左侧“概览 / 分析”两视图，概览只讲用户指标，分析覆盖启动、span 树、时间线与检查器。
/// </summary>
public sealed class PerfDockPage : IDisposable
{
    private readonly PerfCenterView _view;

    public PerfDockPage(PerfRecorder recorder, string dataRoot, Fufucode.Core.Diagnostics.IPerfDataSource? remote = null)
    {
        DataRoot = dataRoot;
        // 非接收端实例通过 remote 读取真正采集数据；两者都没有时才退回本地空记录器。
        _view = new PerfCenterView(new PerfSource(recorder, remote));
    }

    public Control Content => _view;

    public string DataRoot { get; }

    public void NotifySettingsChanged() => _view.NotifySettingsChanged();

    public void Refresh(bool force) => _view.Refresh(force);

    public void Dispose() => _view.Dispose();
}
