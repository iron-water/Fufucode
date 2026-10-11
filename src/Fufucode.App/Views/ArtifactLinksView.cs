using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

public sealed class ArtifactLinksView : UserControl
{
    private AssistantMessageViewModel? _message;
    public ArtifactLinksView()
    {
        DataContextChanged += (_, _) =>
        {
            if (_message is not null) _message.PropertyChanged -= Changed;
            _message = DataContext as AssistantMessageViewModel;
            if (_message is not null) _message.PropertyChanged += Changed;
            Render();
        };
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(AssistantMessageViewModel.ArtifactSummary)) Render(); }
    private void Render()
    {
        var rows = new StackPanel { Spacing = 4 };
        foreach (var path in (_message?.ArtifactSummary ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var button = new Button { Content = path, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, FontSize = 12 };
            button.Classes.Add("subtle"); ToolTip.SetTip(button, path);
            button.Click += (_, _) =>
            {
                try { if (TopLevel.GetTopLevel(this) is MainWindow window) window.PreviewArtifact(path); }
                catch (Exception error) { if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel vm) vm.Timeline.AddNotification("产物预览失败：" + error.Message); }
            };
            rows.Children.Add(button);
        }
        Content = rows;
    }
}
