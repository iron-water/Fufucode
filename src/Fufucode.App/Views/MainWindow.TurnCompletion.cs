using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.Core;
using Fufucode.Core.Plugins;

namespace Fufucode.App;

public partial class MainWindow
{
    private string? _activeScheduledId;
    private async void OnCustomizedTurnSettled(MainViewModel vm)
    {
        if (_closed || !ReferenceEquals(_vm, vm)) return;
        if (_activeScheduledId is { } id)
        {
            _activeScheduledId = null;
            try { _schedules?.Update(id, t => t with { State = vm.Session.LastOutcome?.ToString().ToLowerInvariant() ?? "unknown" }); }
            catch (Exception error) { vm.Timeline.AddNotification("计划任务终态保存失败：" + error.Message); }
        }
        if (_customization is null || !_customization.Current.AutoReadReplies || vm.Session.LastOutcome != SessionOutcome.Completed) return;
        // 门禁：服务只由已安装且已启用的 service.speech-synthesis 插件提供，未安装时静默跳过。
        if (PluginPackageManager.ActiveSpeechService is not { } speech) return;
        try { await speech.SpeakAsync(Views.MessageActionsView.MessageText(vm.Timeline.Entries.OfType<AssistantMessageViewModel>().LastOrDefault())); }
        catch (Exception error) { vm.Timeline.AddNotification("朗读失败：" + error.Message); }
    }

    /// <summary>
    /// 首轮对话结束后的自动取名：
    /// 用户亲手改过名字则永不覆盖；只尝试一次；子代理不可用或失败时退回机械截取首条 prompt。
    /// </summary>
    private async Task NameSessionAfterFirstTurnAsync(MainViewModel vm)
    {
        if (_host is null || _closed || !ReferenceEquals(_vm, vm)) return;
        var metadata = _host.Manager.Metadata;
        var id = _host.Current.Session.Id;
        var presentation = SessionPresentation.Load(metadata, id);
        if (presentation.NameUserSet || presentation.NameAutoAttempted) return;

        var first = SessionTitles.FirstUserText(_host.CurrentTranscript());
        if (string.IsNullOrWhiteSpace(first)) return;

        // 立刻记账：即使用户在取名期间改名，也不会被稍后的结果覆盖。
        SessionPresentation.Save(metadata, id, presentation with { NameAutoAttempted = true });
        var mode = _customization?.Current.SessionNaming ?? "subagent";

        string? name = null;
        if (mode == "subagent" && _host.Chat is Fufucode.Core.Backend.NativeCSharpChatSession { Subagents: { } subagents })
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var reply = await subagents.RunUtilityAsync(SessionTitles.NamingPrompt(first), cts.Token);
                name = SessionTitles.SanitizeGenerated(reply);
            }
            catch (OperationCanceledException) { name = null; }
            catch (Exception) { name = null; }
        }

        name ??= SessionTitles.Mechanical(first);
        // 取名期间用户已改名/归档/切换：放弃自动结果。
        var latest = SessionPresentation.Load(metadata, id);
        if (latest.NameUserSet || latest.Archived || _host.Current.Session.Id != id || _closed) return;

        try
        {
            await _host.RenameCurrentAsync(name);
            SessionPresentation.Save(metadata, id, SessionPresentation.Load(metadata, id) with { NameAutoAttempted = true });
            RefreshSessionList();
        }
        catch (Exception error)
        {
            // 取名只是显示优化，失败不打扰用户；下一次回合不会再重试。
            _ = error;
        }
    }

}
