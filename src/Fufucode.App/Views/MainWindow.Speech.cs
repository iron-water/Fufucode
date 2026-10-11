using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Fufucode.App;

public partial class MainWindow
{
    private async Task ShowSpeechSettingsAsync()
    {
        var status = new TextBlock { Text = _speech.Status };
        var rate = new NumericUpDown { Minimum = -10, Maximum = 10, Increment = 1, Value = _speech.Rate };
        var volume = new Slider { Minimum = 0, Maximum = 100, Value = _speech.Volume, TickFrequency = 10, IsSnapToTickEnabled = true };
        rate.ValueChanged += (_, _) => _speech.Rate = (int)(rate.Value ?? 0);
        volume.PropertyChanged += (_, args) => { if (args.Property == Slider.ValueProperty) _speech.Volume = (int)volume.Value; };
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        foreach (var (id, title) in new[] { ("speech.start", "开始"), ("speech.pause", "暂停"), ("speech.resume", "继续"), ("speech.stop", "停止") })
            actions.Children.Add(new Button { Content = title, Command = _actions.GetCommand(id) });
        void Changed() => Dispatcher.UIThread.Post(() => status.Text = _speech.Status);
        _speech.Changed += Changed;
        try
        {
            await ShowPanelAsync("朗读设置", new StackPanel { Spacing = 12, Children = {
                status, new TextBlock { Text = "语速（下次朗读生效）" }, rate,
                new TextBlock { Text = "音量（下次朗读生效）" }, volume, actions } });
        }
        finally { _speech.Changed -= Changed; }
    }
}
