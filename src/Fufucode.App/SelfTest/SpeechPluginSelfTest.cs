using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.ViewModels;
using Fufucode.Core.Plugins;

namespace Fufucode.App.SelfTest;

/// <summary>
/// 语音朗读的插件门禁验收（--speech-only）：
/// 未安装时动作整条消失、消息操作条没有喇叭按钮、对话框只给安装指引；
/// 登记服务后这些入口才出现。门禁判断只看真实注册，不看配置文件或开关。
/// </summary>
internal static class SpeechPluginSelfTest
{
    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool condition, string description)
        {
            if (condition) pass("Speech: " + description);
            else fail("Speech: " + description);
        }

        var previous = PluginPackageManager.ActiveSpeechService;
        try
        {
            PluginPackageManager.ActiveSpeechService = null;
            var window = new MainWindow { Width = 1100, Height = 760 };
            window.Show();
            await Settle();

            var actions = window.ActionsForTesting;
            Check(new[] { "speech.start", "speech.pause", "speech.resume", "speech.stop" }
                    .All(id => actions.GetDisabledReason(id) is not null),
                "未安装时朗读播放动作全部报告不可用");
            // 菜单只渲染可用动作：不可用的朗读动作不应出现在任何菜单里。
            var surfaceLabels = window.GetVisualDescendants().OfType<MenuItem>()
                .Select(item => item.Header?.ToString()).ToArray();
            Check(!surfaceLabels.Contains("开始朗读") && !surfaceLabels.Contains("暂停朗读"),
                "未安装时朗读动作不渲染成灰条目");
            Check(actions.Contains("speech.settings"),
                "朗读设置动作始终可达，作为未安装时的说明与安装入口");

            var chat = window.GetVisualDescendants().OfType<Views.ChatPage>().First();
            Check(chat.SpeechService is null,
                "未安装时对话页没有朗读服务，消息操作条据此隐藏喇叭按钮");

            using (var subscription = new MainWindow.SpeechSubscription())
            {
                var dialog = window.BuildSpeechSettingsDialog(subscription);
                var texts = dialog.GetLogicalDescendants().OfType<TextBlock>()
                    .Select(block => block.Text ?? "").ToArray();
                Check(texts.Any(text => text.Contains("未安装", StringComparison.Ordinal)),
                    "未安装对话框如实说明未安装");
                Check(!dialog.GetLogicalDescendants().OfType<NumericUpDown>().Any(),
                    "未安装对话框不渲染不可用的语速控件");
                Check(!dialog.GetLogicalDescendants().OfType<Slider>().Any(),
                    "未安装对话框不渲染不可用的音量控件");
                dialog.Close();
            }

            // 登记服务后：动作立即可用，菜单出现，对话框切换成控制面板。
            var probe = new SpeechDialogProbe();
            PluginPackageManager.ActiveSpeechService = probe;
            window.SyncSpeechForTesting();
            await Settle();
            Check(actions.GetDisabledReason("speech.settings") is null &&
                  actions.GetDisabledReason("speech.pause") is not null,
                "登记服务后设置可用；暂停仍按真实播放状态判断");
            Check(ReferenceEquals(chat.SpeechService, probe),
                "登记服务后对话页拿到真实服务，显示朗读按钮");
            using (var subscription = new MainWindow.SpeechSubscription())
            {
                var dialog = window.BuildSpeechSettingsDialog(subscription);
                Check(dialog.GetLogicalDescendants().OfType<NumericUpDown>().Any() &&
                      dialog.GetLogicalDescendants().OfType<Slider>().Any() &&
                      dialog.GetLogicalDescendants().OfType<ToggleSwitch>().Any(),
                    "安装态对话框提供语速、音量与自动朗读开关");
                var status = dialog.GetLogicalDescendants().OfType<TextBlock>()
                    .Select(block => block.Text ?? "").ToArray();
                Check(status.Contains(probe.Status), "状态行显示服务上报的真实状态");
                dialog.Close();
            }

            window.Close();
        }
        catch (Exception error)
        {
            fail("Speech: 自测异常 " + error.GetType().Name + " " + error.Message);
        }
        finally
        {
            PluginPackageManager.ActiveSpeechService = previous;
        }
    }

    private static async Task Settle()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
