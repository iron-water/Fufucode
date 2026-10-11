using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

public partial class ChatPage
{
    internal event Action<string>? SideQuestionRequested;

    public void AddContext(string text, string source)
    {
        if (DataContext is not MainViewModel vm || string.IsNullOrWhiteSpace(text)) return;
        vm.Input.ReferenceContext = (vm.Input.HasReferenceContext ? vm.Input.ReferenceContext + "\n\n" : "") + source + "\n" + text;
        OnInputFocusRequested();
    }

    public void EditMessage(string text)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.Input.ApplyExternalText(text);
        OnInputFocusRequested();
    }

    public void AskAbout(string text, string source) => SideQuestionRequested?.Invoke(source + "\n" + text);
    public void ReportActionError(string text) => (DataContext as MainViewModel)?.Timeline.AddNotification(text);

    internal void ScrollToMessage(string id)
    {
        if (DataContext is not MainViewModel vm) return;
        var entry = vm.Timeline.Entries.FirstOrDefault(entry => entry.Id == id);
        if (entry is not null) TimelineItems.ScrollIntoView(entry);
    }

    private void SelectionReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        var block = (e.Source as Avalonia.Visual)?.GetVisualAncestors().Prepend(e.Source as Avalonia.Visual).OfType<SelectableTextBlock>().FirstOrDefault();
        if (block is null || string.IsNullOrWhiteSpace(block.SelectedText)) return;
        ShowSelectionActions(block,block.SelectedText);
    }

    internal void ShowSelectionActions(Control anchor, string text)
    {
        var menu = new MenuFlyout();
        Add("复制", async () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard,text);
        });
        Add("添加到上下文", () => { AddContext(text,"选中片段"); return Task.CompletedTask; });
        Add("在侧边栏提问", () => { AskAbout(text,"选中片段"); return Task.CompletedTask; });
        Add("更多详情", () => { AskAbout(text,"请结合原文解释"); return Task.CompletedTask; });
        Add("添加到当前任务草稿", () => { EditMessage("任务补充：\n" + text); return Task.CompletedTask; });
        menu.ShowAt(anchor);
        void Add(string title, Func<Task> action)
        {
            var item = new MenuItem { Header = title };
            item.Click += async (_, _) => { try { await action(); } catch (Exception error) { ReportActionError(error.Message); } };
            menu.Items.Add(item);
        }
    }

    private async void AttachContext_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "添加文件", AllowMultiple = true });
            await AttachFilesAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>());
        }
        catch (Exception error) { ReportActionError("添加上下文失败：" + error.Message); }
    }

    internal async Task AttachFilesAsync(System.Collections.Generic.IEnumerable<string> paths)
    {
        if (DataContext is not MainViewModel vm) return;
        foreach (var path in paths)
        {
            try
            {
                var file = await AttachedFile.LoadAsync(path);
                if (file.Image is not null && !vm.Input.SupportsImages) throw new InvalidOperationException("当前会话或模型未声明图片输入能力，请选择支持图片的模型");
                vm.Input.AddAttachment(file);
            }
            catch (Exception error) { ReportActionError("添加文件失败：" + error.Message); }
        }
        OnInputFocusRequested();
    }

    private void FindConversation_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var query = new TextBox { PlaceholderText = "查找本会话", Width = 230 };
        var previous = WorkbenchUi.IconButton("ChevronUp","上一个匹配");
        var next = WorkbenchUi.IconButton("ChevronDown","下一个匹配");
        var count = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { query,previous,next,count } };
        TimelineEntryViewModel[] matches = [];
        var position = -1;
        query.TextChanged += (_, _) =>
        {
            matches = DataContext is MainViewModel vm && !string.IsNullOrWhiteSpace(query.Text)
                ? vm.Timeline.Entries.Where(entry => MessageActionsView.MessageText(entry).Contains(query.Text,StringComparison.OrdinalIgnoreCase)).ToArray() : [];
            position = -1; count.Text = matches.Length + " 项";
            previous.IsEnabled = next.IsEnabled = matches.Length > 0;
        };
        next.Click += (_, _) => Jump(1); previous.Click += (_, _) => Jump(-1);
        var flyout = new Flyout { Content = row };
        flyout.ShowAt((Control)sender!);
        query.Focus();
        void Jump(int direction)
        {
            if (matches.Length == 0) return;
            position = (position + direction + matches.Length) % matches.Length;
            ScrollToMessage(matches[position].Id); count.Text = (position + 1) + " / " + matches.Length;
        }
    }
}
