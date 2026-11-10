using System.Threading.Tasks;
using Avalonia.Threading;

namespace Fufucode.App;

public partial class MainWindow
{
    /// <summary>所有真实会话视图复用异步历史加载，不把磁盘/大快照工作带回 UI 线程。</summary>
    private async Task AttachRealSessionLoadedAsync(bool preservePage = false)
    {
        var host = _host!;
        NavigationProgress.IsVisible = true;
        try
        {
            AttachRealSession(preservePage, deferHistory: true);
            var vm = _vm!;
            var snapshot = await Task.Run(host.CurrentTranscriptSnapshot, _bootstrapCts.Token);
            await vm.Timeline.LoadLiveHistoryAsync(snapshot, _bootstrapCts.Token);
            // 用量/上下文面板也必须读同一完整历史，不能因为异步加载而显示空快照。
            await Task.Run(() => _perfWiring?.AttachSession(host.Chat, host.Current.Session.Name ?? host.Current.Session.Id,
                host.Chat.CurrentModel, snapshot.Messages, host.Current.Session.Id));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        finally { NavigationProgress.IsVisible = _navigationPending || _switching; }
    }
}
