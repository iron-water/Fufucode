using System;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

public partial class ChatPage
{
    private readonly ComboBox _executionMode = new() { MinWidth = 112, MinHeight = 28, FontSize = 11 };
    private readonly string[] _modes = ["manual", "auto", "full", "plan"];
    private bool _updatingComposer;
    private bool _previewDraft;
    private void InitializeComposerTools()
    {
        DragDrop.SetAllowDrop(ComposerCard, true);
        ComposerCard.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        { e.DragEffects = e.DataTransfer.TryGetFiles() is not null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; });
        ComposerCard.AddHandler(DragDrop.DropEvent, async (_, e) =>
        { e.Handled = true; await AttachFilesAsync(e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>() ?? []); });
        _executionMode.ItemsSource = new[] { "人工确认", "自动审批", "完全访问", "规划模式" };
        ToolTip.SetTip(_executionMode, "自动审批允许内置文件编辑，Shell/未知工具仍需确认；规划模式只允许内置读取工具。不是系统沙箱。");
        _executionMode.SelectionChanged += async (_, _) =>
        {
            if (_updatingComposer || _executionMode.SelectedIndex < 0 || DataContext is not MainViewModel vm) return;
            var mode = _modes[_executionMode.SelectedIndex];
            if (mode == vm.Input.ExecutionMode) return;
            _executionMode.IsEnabled = false;
            try
            {
                if (mode == "full" && TopLevel.GetTopLevel(this) is MainWindow owner)
                {
                    var confirm = new Button { Content = "允许本会话所有工具" };
                    var cancel = new Button { Content = "取消", IsCancel = true };
                    var dialog = new Window { Title = "完全访问：工具无需人工确认", Width = 430, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                    dialog.Content = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 16, Children = { new TextBlock { Text = "包括文件写入、Shell 和第三方扩展工具。", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { cancel, confirm } } } };
                    cancel.Click += (_, _) => dialog.Close(false); confirm.Click += (_, _) => dialog.Close(true);
                    if (!await dialog.ShowDialog<bool>(owner)) return;
                }
                await vm.Input.SetExecutionModeAsync(mode);
            }
            catch (Exception error) { ReportActionError("权限模式切换失败：" + error.Message); }
            finally { RefreshComposerTools(); }
        };
        ComposerModes.Children.Add(_executionMode);
        var menu = new MenuFlyout();
        var preview = new MenuItem { Header = "Markdown 草稿预览" };
        preview.Click += (_, _) => { _previewDraft = !_previewDraft; RefreshComposerTools(); };
        var commands = new MenuItem { Header = "命令与技能" };
        commands.Click += (_, _) => CommandFlyoutButton.Flyout?.ShowAt(ComposerMoreButton);
        var hide = new MenuItem { Header = "隐藏输入区" }; hide.Click += (_, _) => ToggleComposer();
        menu.Items.Add(preview); menu.Items.Add(commands); menu.Items.Add(hide); ComposerMoreButton.Flyout = menu;
        DraftPreview.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(DraftPreview).Properties.IsLeftButtonPressed) { _previewDraft = false; RefreshComposerTools(); Input.Focus(); }
        };
        var attachments = new MenuFlyout();
        foreach (var (title, kind) in new[] { ("文件", "file"), ("图片", "image"), ("PDF", "pdf"), ("链接", "link") })
        {
            var choice = kind;
            var item = new MenuItem { Header = title };
            item.Click += async (_, _) =>
            {
                try { await PickAttachmentAsync(choice); }
                catch (Exception error) { ReportActionError("添加附件失败：" + error.Message); }
            };
            attachments.Items.Add(item);
        }
        AttachContextButton.Flyout = attachments;
    }
    private async System.Threading.Tasks.Task PickAttachmentAsync(string kind)
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow owner) return;
        if (kind == "link")
        {
            var url = new TextBox { PlaceholderText = "https://", MinWidth = 280 };
            var add = new Button { Content = "添加链接" };
            var cancel = new Button { Content = "取消", IsCancel = true };
            var dialog = new Window { Title = "添加链接", Width = 440, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            dialog.Content = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 12, Children = { url, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, add } } } };
            cancel.Click += (_, _) => dialog.Close();
            add.Click += (_, _) =>
            {
                if (!Uri.TryCreate(url.Text, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")) { ReportActionError("请填写 HTTP/HTTPS 链接"); return; }
                AddContext(address.AbsoluteUri, "链接（未抓取网页内容）"); dialog.Close();
            };
            await dialog.ShowDialog(owner); return;
        }
        var filter = kind switch
        {
            "image" => new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif"] },
            "pdf" => new FilePickerFileType("PDF") { Patterns = ["*.pdf"] },
            _ => FilePickerFileTypes.All
        };
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "添加" + filter.Name, AllowMultiple = true, FileTypeFilter = [filter] });
        await AttachFilesAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }
    /// <summary>模型选择器条目：选中后关闭浮层。</summary>
    private void ModelPickerItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        (sender as Button)?.Flyout?.Hide();
    }

    private void ComposerChanged(object? sender, PropertyChangedEventArgs e) => RefreshComposerTools();
    private void TimelineChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RefreshRuler();
    private void RefreshComposerTools()
    {
        if (DataContext is not MainViewModel vm) return;
        _updatingComposer = true;
        try
        {
            _executionMode.SelectedIndex = Array.IndexOf(_modes, vm.Input.ExecutionMode);
            _executionMode.IsEnabled = vm.Input.CanChangeExecutionMode;
            // 后端不支持执行模式（例如 FUMO 原生内核）时整个控件隐藏，不留一个点不动的死下拉。
            _executionMode.IsVisible = vm.Input.ShowsExecutionMode;
        }
        finally { _updatingComposer = false; }
        DraftPreview.IsVisible = _previewDraft;
        Input.IsVisible = !_previewDraft;
        MarkdownTextBlock.SetMarkdown(DraftPreviewText, vm.Input.DraftText);
        AttachedFiles.Children.Clear();
        foreach (var file in vm.Input.Attachments)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Avalonia.Thickness(0, 0, 8, 4) };
            var name = new TextBlock { Text = file.Name, MaxWidth = 170, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
            ToolTip.SetTip(name, file.Path);
            var remove = WorkbenchUi.IconButton("DismissCircle", "移除 " + file.Name);
            remove.Command = vm.Input.RemoveAttachmentCommand; remove.CommandParameter = file;
            row.Children.Add(name); row.Children.Add(remove); AttachedFiles.Children.Add(row);
        }
    }
}
