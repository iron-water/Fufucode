using System;
using System.Linq;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;

namespace Fufucode.App;

public partial class MainWindow
{
    private readonly ReplySpeechService _speech = new();
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
        if (!_customization.Current.AutoReadReplies || vm.Session.LastOutcome != SessionOutcome.Completed) return;
        try { await _speech.SpeakAsync(Views.MessageActionsView.MessageText(vm.Timeline.Entries.OfType<AssistantMessageViewModel>().LastOrDefault())); }
        catch (Exception error) { vm.Timeline.AddNotification("朗读失败：" + error.Message); }
    }
}
