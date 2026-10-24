using Avalonia;
using System;
using System.IO;
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
using Fufucode.App.Features.Plugins;

namespace Fufucode.App.Views;

public partial class ChatPage
{
    private readonly ComboBox _executionMode = new() { MinWidth = 112, MinHeight = 28, FontSize = 11 };
    private readonly string[] _modes = ["manual", "auto", "full", "plan"];
    private bool _updatingComposer;
    private Button? _executionModeButton;
    private MenuFlyout? _executionModeMenu;
    private bool _previewDraft;
    private void InitializeComposerTools()
    {
        DragDrop.SetAllowDrop(ComposerCard, true);
        ComposerCard.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        { e.DragEffects = e.DataTransfer.TryGetFiles() is not null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; });
        ComposerCard.AddHandler(DragDrop.DropEvent, async (_, e) =>
        { e.Handled = true; await AttachFilesAsync(e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>() ?? []); });
        // 粘贴：剪贴板里有文件或图片时作为附件加入；否则保持默认文本粘贴。
        InputBox.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key != Key.V || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            if (await TryPasteAttachmentsAsync()) e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
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
                    // 权限升级对话框：警示标题 + 能力清单卡 + 风险说明 + 右下按钮。
                    // 必须经 SkinDialog 展示，否则代码构建的 Window 没有主题资源（纯黑底）。
                    var confirm = new Button { Content = "确认" };
                    confirm.Classes.Add("danger");
                    var cancel = new Button { Content = "取消", IsCancel = true };

                    var heading = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            BindIcon(new PathIcon { Width = 20, Height = 20 }, "Icon.Warning", "AppWarningForeground"),
                            new TextBlock { Text = "要开启完整访问权限吗？", FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                        },
                    };

                    var intro = new TextBlock
                    {
                        Text = "Fufucode 将能够在未经你确认的情况下，在这台计算机上的任何位置运行命令、使用互联网，以及创建和编辑文件。这包括但不限于：",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        FontSize = 13,
                        LineHeight = 20,
                    };
                    intro.Classes.Add("secondary");

                    var details = new StackPanel();
                    details.Children.Add(DetailRow("Icon.Folder", "文件和文件夹", "读取、创建、修改或删除计算机上任意位置的文件"));
                    details.Children.Add(Divider());
                    details.Children.Add(DetailRow("Icon.Terminal", "终端命令", "运行命令、安装软件和更改系统设置"));
                    details.Children.Add(Divider());
                    details.Children.Add(DetailRow("Icon.Globe", "互联网和已连接的应用", "访问网站、发送数据并使用已启用的插件"));
                    // 对话框内嵌卡：显式底/描边、不加浮起阴影（设计规范：普通卡无阴影）。
                    var detailsCard = new Border { Child = details, Padding = new Thickness(0), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
                    detailsCard.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppCardBackground"));
                    detailsCard.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppCardStroke"));

                    var risk = new TextBlock
                    {
                        Text = "这会带来敏感数据丢失或泄露、提示注入等风险。你可以随时将其关闭。",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        FontSize = 12,
                    };
                    risk.Classes.Add("secondary");

                    var actions = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, confirm },
                    };

                    var dialog = new Window
                    {
                        Title = "完全访问：工具无需人工确认",
                        Width = 560,
                        SizeToContent = SizeToContent.Height,
                        CanResize = false,
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Content = new StackPanel
                        {
                            Margin = new Thickness(24, 20, 24, 20),
                            Spacing = 16,
                            Children = { heading, intro, detailsCard, risk, actions },
                        },
                    };
                    cancel.Click += (_, _) => dialog.Close(false);
                    confirm.Click += (_, _) => dialog.Close(true);
                    if (!await SkinDialog.ShowAsync<bool>(dialog, owner)) return;
                }
                await vm.Input.SetExecutionModeAsync(mode);
            }
            catch (Exception error) { ReportActionError("权限模式切换失败：" + error.Message); }
            finally { RefreshComposerTools(); }
        };
        // 权限模式：底部只留一个小图标，点开才是选项列表；文字进 tooltip。
        var modeButton = WorkbenchUi.IconButton("ShieldCheckmark", "权限模式", 30);
        var modeMenu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight };
        modeButton.Flyout = modeMenu;
        _executionModeMenu = modeMenu;
        _executionModeButton = modeButton;
        ComposerModes.Children.Insert(0, modeButton);
        // 输入区底部图标统一用上方浮层提示（离开 0.3s 消失）。
        MainViewModel? Vm() => DataContext as MainViewModel;
        HoverTip.Attach(ComposerMoreButton, () => "附加功能");
        HoverTip.Attach(AttachContextButton, () => "添加文件、图片、PDF 或链接");
        HoverTip.Attach(SessionProjectChip, () =>
            Vm()?.Sidebar.ProjectName is { Length: > 0 } project ? "项目：" + project : "更改此会话的项目");
        HoverTip.Attach(ModelButton, () => "模型：" + (Vm()?.Settings.SelectedModel.Label ?? "选择模型"));
        HoverTip.Attach(ThinkingButton, () => "思考：" + (Vm()?.Settings.SelectedThinking?.Label ?? "默认"));
        HoverTip.Attach(modeButton, () => "权限：" + (_executionMode.SelectedItem?.ToString() ?? "—"));
        HoverTip.Attach(SendButton, () => "发送");
        HoverTip.Attach(StopButton, () => "停止当前回合");
        var menu = new MenuFlyout();
        var preview = new MenuItem { Header = "Markdown 草稿预览" };
        preview.Click += (_, _) => { _previewDraft = !_previewDraft; RefreshComposerTools(); };
        var commands = new MenuItem { Header = "命令与技能" };
        commands.Click += (_, _) => CommandFlyoutButton.Flyout?.ShowAt(ComposerMoreButton);
        var hide = new MenuItem { Header = "隐藏输入区" }; hide.Click += (_, _) => ToggleComposer();
        menu.Items.Add(preview); menu.Items.Add(commands); menu.Items.Add(hide);
        ComposerMoreButton.Flyout = menu;
        RefreshComposerPluginMenu();
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
    private void BuildExecutionModeMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        var labels = _executionMode.ItemsSource as System.Collections.IEnumerable;
        if (labels is null) return;
        var index = 0;
        foreach (var label in labels)
        {
            var captured = index++;
            var text = label?.ToString() ?? string.Empty;
            var item = new MenuItem
            {
                Header = (captured == _executionMode.SelectedIndex ? "✓  " : "    ") + text,
                IsEnabled = _executionMode.IsEnabled,
            };
            item.Click += (_, _) => _executionMode.SelectedIndex = captured;
            menu.Items.Add(item);
        }
    }

    /// <summary>思考等级菜单：预构建可选档位（不支持的置灰）。</summary>
    private void BuildThinkingMenu(MenuFlyout menu, MainViewModel vm)
    {
        menu.Items.Clear();
        foreach (var choice in vm.Settings.ThinkingChoices)
        {
            var captured = choice;
            var current = Equals(vm.Settings.SelectedThinking, captured);
            var item = new MenuItem
            {
                Header = (current ? "✓  " : "    ") + captured.Label,
                IsEnabled = captured.IsSupported,
            };
            item.Click += (_, _) => { vm.Settings.SelectedThinking = captured; RefreshComposerTools(); };
            menu.Items.Add(item);
        }
    }

    /// <summary>剪贴板附件：文件列表、常见图片格式（字节流）。成功时返回 true。</summary>
    private async System.Threading.Tasks.Task<bool> TryPasteAttachmentsAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return false;
        try
        {
            var transfer = await clipboard.TryGetDataAsync();
            if (transfer is null) return false;
            if (await transfer.TryGetFilesAsync() is { Length: > 0 } files)
            {
                var paths = files.Select(item => item.TryGetLocalPath()).OfType<string>().ToArray();
                if (paths.Length > 0) { await AttachFilesAsync(paths); return true; }
            }
            // 图片：剪贴板里通常是 PNG 字节流；落成临时文件后按普通附件处理。
            foreach (var (name, extension) in new[] { ("PNG", ".png"), ("image/png", ".png"), ("image/jpeg", ".jpg"), ("image/bmp", ".bmp") })
            {
                var bytes = await transfer.TryGetValueAsync<byte[]>(DataFormat.CreateBytesPlatformFormat(name));
                if (bytes is { Length: > 0 })
                {
                    var path = Path.Combine(Path.GetTempPath(), "fufucode-paste-" + Guid.NewGuid().ToString("N") + extension);
                    await File.WriteAllBytesAsync(path, bytes);
                    await AttachFilesAsync(new[] { path });
                    return true;
                }
            }
        }
        catch (Exception error) { ReportActionError("粘贴附件失败：" + error.Message); }
        return false;
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

    /// <summary>每次打开模型选择器都从空搜索开始，避免上次的关键字把新提供商的模型全部过滤掉。</summary>
    private void ModelPicker_Opened(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Settings.ModelPickerFilter = string.Empty;
    }

    /// <summary>搜索框显式同步到 VM；不依赖控件的默认绑定触发时机。</summary>
    private void ModelPickerSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is TextBox box)
            vm.Settings.ModelPickerFilter = box.Text ?? string.Empty;
    }

    internal PluginComposerHost? ComposerPluginHost { get; set; }

    internal void RefreshComposerPluginMenu()
    {
        if (ComposerMoreButton.Flyout is not MenuFlyout menu || DataContext is not MainViewModel vm) return;
        while (menu.Items.Count > 3) menu.Items.RemoveAt(3);
        ComposerPluginHost?.Populate(menu, vm);
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
            // 后端不支持执行模式（例如 FUMO 原生内核）时隐藏入口，不留死按钮。
            if (_executionModeButton is not null)
            {
                _executionModeButton.IsEnabled = vm.Input.CanChangeExecutionMode;
                _executionModeButton.IsVisible = vm.Input.ShowsExecutionMode;
            }
            ThinkingButton.IsEnabled = vm.Settings.CanChangeModel;
            if (_executionModeMenu is not null) BuildExecutionModeMenu(_executionModeMenu);
            if (ThinkingButton.Flyout is MenuFlyout thinkingMenu) BuildThinkingMenu(thinkingMenu, vm);
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

    /// <summary>给图标绑动态资源：Data 取 Icon.*，Foreground 取 App* 画刷。</summary>
    private static T BindIcon<T>(T icon, string dataKey, string brushKey) where T : PathIcon
    {
        icon.Bind(PathIcon.DataProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(dataKey));
        icon.Bind(PathIcon.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(brushKey));
        return icon;
    }

    /// <summary>权限说明卡里的一行：accent 图标块 + 标题 + 描述。</summary>
    private static Control DetailRow(string iconKey, string title, string description)
    {
        var tile = new Border
        {
            Width = 34, Height = 34, CornerRadius = new Avalonia.CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Top,
        };
        tile.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppAccentSubtle"));
        tile.Child = BindIcon(new PathIcon
        {
            Width = 16, Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        }, iconKey, "AppAccentText");

        var caption = new TextBlock { Text = title, FontSize = 13, FontWeight = Avalonia.Media.FontWeight.SemiBold };
        var detail = new TextBlock { Text = description, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 2, 0, 0) };
        detail.Classes.Add("secondary");
        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(12, 0, 0, 0) };
        texts.Children.Add(caption); texts.Children.Add(detail);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(tile);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        return new Border { Padding = new Avalonia.Thickness(14, 10), Child = grid };
    }

    /// <summary>卡内行之间的 1px 发丝分隔线。</summary>
    private static Border Divider()
    {
        var rule = new Border { Height = 1, Margin = new Avalonia.Thickness(14, 0, 14, 0) };
        rule.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppDividerSubtle"));
        return rule;
    }
}
