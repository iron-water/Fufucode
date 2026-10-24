using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using Fufucode.Core.Plugins;

namespace Fufucode.App;

public partial class MainWindow
{
    /// <summary>
    /// 当前可用的朗读服务：只认插件激活时登记的实现，未安装/未启用时是零副作用替身。
    /// 宿主不再自己反射加载插件程序集——那样等于绕开插件状态机，卸载后仍可用。
    /// </summary>
    private static ISpeechService Speech => PluginPackageManager.ActiveSpeechService ?? NullSpeechService.Instance;

    private bool SpeechInstalled => PluginPackageManager.ActiveSpeechService is not null;

    /// <summary>自测接缝：插件登记变化后手动触发一次投影，验证门禁真的生效。</summary>
    internal void SyncSpeechForTesting() => SyncSpeechService();

    /// <summary>
    /// 插件安装/启停后重新投影到对话页与动作目录：朗读按钮、菜单项与快捷键随真实状态出现或消失。
    /// </summary>
    private void SyncSpeechService()
    {
        // 对话页用 null 表示“没有朗读能力”：消息操作条据此隐藏喇叭按钮。
        // 传零副作用替身会让按钮继续显示，正是要避免的假可用。
        _chatPage.SpeechService = PluginPackageManager.ActiveSpeechService;
        _chatPage.RefreshSpeechAvailability();
        _actions.RefreshCapabilities();
        if (!_closed && _customization is not null) BuildActionSurfaces(_customization.Current);
    }

    /// <summary>
    /// 朗读设置：一个对话框只做一件事——调语速/音量、控制播放。
    /// 未安装时如实说明并给出插件市场入口，不渲染任何假装可用的控件。
    /// </summary>
    private async Task ShowSpeechSettingsAsync()
    {
        using var subscription = new SpeechSubscription();
        var dialog = BuildSpeechSettingsDialog(subscription);
        await SkinDialog.ShowAsync(dialog, this);
    }

    /// <summary>朗读设置的内容与生命周期：展示与自测截图共用这一份构造，不各写一套。</summary>
    internal Window BuildSpeechSettingsDialog(SpeechSubscription subscription)
    {
        // 高度跟内容走：未安装只有一段说明，安装后是三组设置加控制条。
        // 固定高度会让两种状态各空一半或截断，这正是不用固定尺寸的理由。
        var dialog = AppDialog.Create("语音朗读", 560);
        dialog.MinHeight = 320;
        dialog.MaxHeight = 720;
        dialog.CanResize = true;

        // 组间距由 GroupHeader 自带的 32px 提供；这里再加 StackPanel 间距会叠成 48px，
        // 对话框里三组就撑出一屏多余空白。
        var body = new StackPanel();
        // 状态是行尾的只读值，不能再套行标题样式——那会让它和左侧标题抢同一层级。
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

        if (!SpeechInstalled)
        {
            body.Children.Add(SettingsLayout.GroupHeader("未安装", first: true));
            body.Children.Add(SettingsLayout.Group(
                SettingsLayout.ChevronRow("语音朗读服务",
                    "朗读需要通过系统语音引擎发声，因此作为独立插件分发。安装并启用后，这里会变成完整的朗读控制面板。",
                    () => _ = OpenSpeechMarketplaceAsync(), "Speaker")));
            body.Children.Add(SettingsLayout.Note(
                "未安装时朗读动作不出现在菜单与消息操作条里，也不会启动任何 PowerShell 进程。"));
        }
        else
        {
            var service = Speech;
            var rate = new NumericUpDown
            {
                Minimum = -10, Maximum = 10, Increment = 1, FormatString = "0",
                Value = Math.Clamp(service.Rate, -10, 10),
                Width = 120, VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(rate, "语速");
            rate.ValueChanged += (_, _) => service.Rate = (int)(rate.Value ?? 0);

            var volumeText = new TextBlock
            {
                Text = Math.Clamp(service.Volume, 0, 100).ToString(),
                Classes = { "meta", "tabularNumeric" },
                VerticalAlignment = VerticalAlignment.Center, MinWidth = 32,
                TextAlignment = TextAlignment.Right,
            };
            var volume = new Slider
            {
                Minimum = 0, Maximum = 100, Value = Math.Clamp(service.Volume, 0, 100),
                TickFrequency = 10, IsSnapToTickEnabled = true, Width = 200,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(volume, "音量");
            volume.PropertyChanged += (_, args) =>
            {
                if (args.Property != Slider.ValueProperty) return;
                service.Volume = (int)volume.Value;
                volumeText.Text = service.Volume.ToString();
            };
            var volumeRow = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { volume, volumeText },
            };

            var autoRead = new ToggleSwitch
            {
                IsChecked = _customization.Current.AutoReadReplies,
                OnContent = "", OffContent = "",
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(autoRead, "回复结束后自动朗读");
            autoRead.IsCheckedChanged += (_, _) =>
                _customization.Change(state => state with { AutoReadReplies = autoRead.IsChecked == true });

            body.Children.Add(SettingsLayout.GroupHeader("播放", first: true));
            body.Children.Add(SettingsLayout.Group(
                SettingsLayout.Row("状态", "由服务实时上报；切换朗读后立即更新。", status, "Info"),
                SettingsLayout.ToggleRow("回复结束后自动朗读",
                    "只在语音插件已启用时生效；随时朗读单条消息仍用消息操作条的喇叭按钮。", autoRead, "Speaker")));

            body.Children.Add(SettingsLayout.GroupHeader("声音"));
            body.Children.Add(SettingsLayout.Group(
                SettingsLayout.Row("语速", "范围 -10 到 10，0 为正常语速。对下一次朗读生效。", rate, "Speedometer"),
                SettingsLayout.Row("音量", "0 到 100，对下一次朗读生效。", volumeRow, "Speaker")));

            body.Children.Add(SettingsLayout.GroupHeader("播放控制"));
            body.Children.Add(AppDialog.Card(BuildSpeechControls()));

            void SyncStatus()
            {
                var text = service.Status;
                status.Text = text;
            }
            SyncStatus();
            subscription.Changed = () => Dispatcher.UIThread.Post(SyncStatus);
        }

        var close = new Button { Content = "关闭", IsCancel = true, MinWidth = 88 };
        close.Click += (_, _) => dialog.Close();
        var footerActions = new System.Collections.Generic.List<Control>();
        if (SpeechInstalled) footerActions.Add(BuildSpeechMarketplaceButton());
        footerActions.Add(close);

        // 中间行用 * 而不是 Auto：窗口被 MaxHeight 卡住时，只有弹性行会让内容滚动，
        // 页脚始终留在可见区，而不是被内容顶出窗口下沿。
        var root = new Grid { RowDefinitions = new("Auto,*,Auto") };
        root.Children.Add(AppDialog.Header("Icon.Speaker", "语音朗读", SpeechInstalled
            ? "由 service.speech-synthesis 插件提供 · 设置对下一次朗读生效"
            : "由 service.speech-synthesis 插件提供 · 当前未安装"));
        var content = AppDialog.Body(new ScrollViewer { Content = body, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        Grid.SetRow(content, 1); root.Children.Add(content);
        var footer = AppDialog.Footer([.. footerActions]);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        dialog.Content = root;
        return dialog;
    }

    /// <summary>播放控制条：用共享动作命令，按钮的可用性与菜单/快捷键完全一致。</summary>
    private Control BuildSpeechControls()
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        foreach (var (id, icon, title) in new[]
        {
            ("speech.start", "Speaker", "开始朗读"),
            ("speech.pause", "Pause", "暂停"),
            ("speech.resume", "Play", "继续"),
            ("speech.stop", "RecordStop", "停止"),
        })
        {
            var button = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        SpeechIcon(icon),
                        new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
                Command = _actions.GetCommand(id),
                Padding = new Thickness(14, 6),
            };
            if (id == "speech.start") button.Classes.Add("accent");
            AutomationProperties.SetName(button, title);
            AutomationProperties.SetAutomationId(button, "speech-control-" + id);
            row.Children.Add(button);
        }
        return row;
    }

    private Button BuildSpeechMarketplaceButton()
    {
        var button = new Button
        {
            Content = "插件市场",
            Padding = new Thickness(16, 6),
            MinWidth = 88,
        };
        AutomationProperties.SetName(button, "打开插件市场中的语音朗读服务");
        button.Click += (_, _) => _ = OpenSpeechMarketplaceAsync();
        return button;
    }

    private static Control SpeechIcon(string icon)
    {
        var path = new PathIcon { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        path.Bind(PathIcon.DataProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Icon." + icon));
        return path;
    }

    /// <summary>插件市场 → 后台服务分组：用户从那里安装/卸载语音插件。</summary>
    private async Task OpenSpeechMarketplaceAsync()
    {
        if (_vm is null) return;
        await NavigateWorkspaceAsync(AppPage.Settings);
        _vm.Settings.SelectCategory("marketplace");
        _vm.Settings.OpenPage("marketplace.service");
    }

    /// <summary>对话框存活期间订阅服务状态；关闭时解除，避免遗留闭包持有窗口。</summary>
    internal sealed class SpeechSubscription : IDisposable
    {
        private Action? _handler;

        /// <summary>登记状态回调；同一对话框只用一个处理器，重复赋值会替换旧处理器。</summary>
        public Action? Changed
        {
            set
            {
                if (_handler is not null) Speech.Changed -= _handler;
                _handler = value;
                if (_handler is not null) Speech.Changed += _handler;
            }
        }

        public void Dispose()
        {
            if (_handler is null) return;
            Speech.Changed -= _handler;
            _handler = null;
        }
    }
}
