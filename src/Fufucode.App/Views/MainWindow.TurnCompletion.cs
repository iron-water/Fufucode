using System;
using System.Linq;
using Fufucode.App.Abstractions;
using Fufucode.App.Features.Plugins;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.Core;
using Fufucode.Core.Plugins;

namespace Fufucode.App;

public partial class MainWindow
{
    private ISpeechService _speech = NullSpeechService.Instance;
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
        // 门禁：仅在 service.speech-synthesis 插件已安装且启用时才初始化与执行朗读
        if (!PluginAccess.IsPluginActive(_customization.DataRoot, PluginAccess.SpeechPluginId)) return;
        EnsureSpeechPluginLoaded();
        try { await _speech.SpeakAsync(Views.MessageActionsView.MessageText(vm.Timeline.Entries.OfType<AssistantMessageViewModel>().LastOrDefault())); }
        catch (Exception error) { vm.Timeline.AddNotification("朗读失败：" + error.Message); }
    }

    private void EnsureSpeechPluginLoaded()
    {
        if (_speech is not NullSpeechService) return;
        var root = _customization?.DataRoot ?? Program.SelfTestDataRoot ?? _options.CreateAppDataLayout().Root;
        var layout = new AppDataLayout(root);
        var dir = layout.PluginDirectory(PluginAccess.SpeechPluginId);
        var dll = System.IO.Path.Combine(dir, "Fufucode.Plugins.Speech.dll");
        if (System.IO.File.Exists(dll))
        {
            try
            {
                var asm = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
                var type = asm.GetType("Fufucode.Plugins.Speech.ReplySpeechService");
                if (type is not null && Activator.CreateInstance(type) is ISpeechService service)
                {
                    _speech = service;
                    // 把服务交给对话页，消息工具栏据此显示"朗读"按钮。
                    _chatPage.SpeechService = service;
                }
            }
            catch { }
        }
    }
}
