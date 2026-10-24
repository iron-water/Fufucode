using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Fufucode.App.ViewModels;
using Fufucode.App.Features.FilePreview;

namespace Fufucode.App.Views;

public partial class WorkspacePanelView : UserControl
{
    public event Action? PreviewRequested;

    /// <summary>
    /// 把当前渲染好的 Mermaid 图表保存为 PNG。
    /// 用 2 倍像素渲染，避免在高 DPI 屏幕上导出图发虚。
    /// </summary>
    private async void OnDownloadDiagram(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspacePanelViewModel model) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        try
        {
            var suggested = Path.GetFileNameWithoutExtension(model.PreviewFileName) + ".png";
            var target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存图表",
                SuggestedFileName = suggested,
                DefaultExtension = "png",
                FileTypeChoices = [new FilePickerFileType("PNG 图片") { Patterns = ["*.png"] }],
            });
            if (target is null) return;

            var diagram = this.FindControl<ContentControl>("PreviewHost")?.Content is FilePreviewHost host
                ? host.Content as RichMarkdownView : null;
            var size = diagram?.Bounds.Size ?? default;
            if (diagram is null || size.Width < 1 || size.Height < 1) return;

            const double scale = 2;
            var pixelSize = new PixelSize(
                Math.Max(1, (int)Math.Ceiling(size.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(size.Height * scale)));
            using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scale, 96 * scale));
            bitmap.Render(diagram);
            await using var stream = await target.OpenWriteAsync();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        }
        catch (Exception error)
        {
            model.PreviewFileDetail = "导出失败：" + error.Message;
        }
    }

    internal (Control Files, Control Changes) TakeDockPages()
    {
        var root = (Grid)Content!;
        root.Children.Remove(FilesContent);
        root.Children.Remove(ChangesContent);
        FilesContent.IsVisible = ChangesContent.IsVisible = true;
        return (FilesContent, ChangesContent);
    }

    internal void SetDockContext(WorkspacePanelViewModel vm)
    {
        DataContext = vm;
        FilesContent.DataContext = ChangesContent.DataContext = vm;
    }
    public WorkspacePanelView()
    {
        InitializeComponent();
        // Files 选中只更新列表，点击“打开预览”或再次点击打开统一本地预览宿主。
        FilesList.SelectionChanged += OnFilesSelectionChanged;
        // 语法着色按深浅色选调色板：跟随实际主题，切换后重新着色当前 diff。
        ActualThemeVariantChanged += (_, _) => PushTheme();
        DataContextChanged += (_, _) => PushTheme();
        // 行宽交给 VM：模板里对祖先 ScrollViewer 的绑定在停靠宿主下不可靠。
        DiffScroll.SizeChanged += (_, _) =>
        {
            if (DataContext is WorkspacePanelViewModel vm && DiffScroll.Viewport.Width > 0)
                vm.DiffRowWidth = DiffScroll.Viewport.Width;
        };
        DataContextChanged += (_, _) => HookWordWrap();
        // 变更页被 TakeDockPages 摘到停靠区后就不再是本控件的子级，
        // 因此响应用的是真正挂在树里的 ChangesContent，而不是 this.Bounds。
        ChangesContent.SizeChanged += (_, _) => ApplyChangeLayout(ChangesContent.Bounds.Width);
        // diff 行实测宽度随内容变化；用视口宽度兜底，增删底色才能铺满整行。
        DiffScroll.SizeChanged += (_, _) =>
        {
            var width = DiffScroll.Viewport.Width;
            if (width > 0) DiffRowsHost.MinWidth = width;
        };
    }

    private WorkspacePanelViewModel? _wordWrapSource;

    /// <summary>换行开关决定要不要横向滚动条；跟随 DataContext 重新挂接，避免重复订阅。</summary>
    private void HookWordWrap()
    {
        if (_wordWrapSource is not null) _wordWrapSource.PropertyChanged -= OnWordWrapChanged;
        _wordWrapSource = DataContext as WorkspacePanelViewModel;
        if (_wordWrapSource is not null)
        {
            _wordWrapSource.PropertyChanged += OnWordWrapChanged;
            ApplyWordWrap(_wordWrapSource.IsWordWrapEnabled);
        }
    }

    private void OnWordWrapChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(WorkspacePanelViewModel.IsWordWrapEnabled) &&
            sender is WorkspacePanelViewModel vm)
            ApplyWordWrap(vm.IsWordWrapEnabled);
    }

    private void ApplyWordWrap(bool wrap) =>
        DiffScroll.HorizontalScrollBarVisibility = wrap
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;

    private void PushTheme()
    {
        if (DataContext is not WorkspacePanelViewModel vm) return;
        vm.SetDarkTheme(ActualThemeVariant != Avalonia.Styling.ThemeVariant.Light);
    }

    private void SelectTab_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is WorkspacePanelViewModel vm && sender is Button { DataContext: WorkspacePanelTabViewModel tab })
            vm.SelectTabCommand.Execute(tab);
    }

    private void CloseTab_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is WorkspacePanelViewModel vm && sender is Button { DataContext: WorkspacePanelTabViewModel tab })
            vm.CloseTabCommand.Execute(tab);
    }

    private void BrowseFile_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not WorkspacePanelViewModel vm ||
            sender is not Button { DataContext: WorkspacePanelFileItemViewModel file })
        {
            return;
        }

        // 目录走原有逐层浏览；文件在选中命令之后立刻把预览目标推给内置浏览器。
        // 「再次点击已选中文件」= 打开预览；双击的第二下走的正是这条路径，
        // 因此不另挂 DoubleTapped 手势——同一语义只保留一个可断言入口。
        var reopen = IsSamePath(vm.SelectedFile?.FullPath, file.FullPath);
        if (file.IsDirectory)
        {
            _ = vm.ToggleDirectoryAsync(file);
            return;
        }
        vm.BrowseFileCommand.Execute(file);
        if (reopen)
        {
            vm.OpenFilePreview(file.FullPath);
            PreviewRequested?.Invoke();
        }
    }

    /// <summary>
    /// 选中文件（键盘或列表自身选中）→ 把路径推给内置浏览器，进入文件预览态，但<b>不</b>抢走 Files tab：
    /// 每次点击都跳到 Preview 会让目录连续浏览不可用（guideline §3.4 要求 tab 身份稳定）。
    /// </summary>
    private void OnFilesSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        // 选择只更新列表状态；预览由“打开预览”或再次点击触发。
    }

    private static bool IsSamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void PreviewSelectedFile_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is WorkspacePanelViewModel { SelectedFile: { IsDirectory: false } file } vm)
        {
            vm.OpenFilePreview(file.FullPath);
            PreviewRequested?.Invoke();
        }
    }

    private async void OpenPreviewInSystem_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not WorkspacePanelViewModel vm || string.IsNullOrWhiteSpace(vm.PreviewFilePath)) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher &&
                await TopLevel.GetTopLevel(this)!.StorageProvider.TryGetFileFromPathAsync(vm.PreviewFilePath) is { } item)
                await launcher.LaunchFileAsync(item);
        }
        catch (Exception error) { vm.PreviewFileDetail = "系统打开失败：" + error.Message; }
    }

    /// <summary>
    /// 变更页的响应式分栏：够宽时列表与 diff 并排，窄栏仍是列表 → diff 的单栏切换。
    /// 断点取 620：低于它时两栏各自都不足 300，代码会被挤到频繁横向滚动。
    /// </summary>
    private void ApplyChangeLayout(double width)
    {
        const double wideBreakpoint = 620;
        const double listWidth = 224;
        var wide = width >= wideBreakpoint;
        if (DataContext is WorkspacePanelViewModel model && !model.WordWrapUserOverride)
            model.IsWordWrapEnabled = !wide;
        ChangeBody.ColumnDefinitions[0].Width = wide ? new GridLength(listWidth) : new GridLength(1, GridUnitType.Star);
        ChangeBody.ColumnDefinitions[1].Width = new GridLength(wide ? 1 : 0);
        ChangeBody.ColumnDefinitions[2].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        // 窄栏里 diff 复用第 0 列：列表与 diff 靠可见性互斥，同一列宽度才够。
        Grid.SetColumn(ChangeDiffPane, wide ? 2 : 0);
        Grid.SetColumnSpan(ChangeDiffPane, wide ? 1 : 3);
        if (DataContext is WorkspacePanelViewModel vm) vm.IsChangePaneWide = wide;
    }

    private void SelectChange_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is WorkspacePanelViewModel vm &&
            sender is Button { DataContext: WorkspacePanelChangedFileViewModel file })
            vm.SelectedChange = file;
    }

    /// <summary>返回文件列表：清掉选中，diff 区随之让位。</summary>
    private void CloseDiff_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is WorkspacePanelViewModel vm) vm.SelectedChange = null;
    }

    private void ToggleGroup_Click(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: WorkspaceChangeGroupViewModel group })
            group.IsExpanded = !group.IsExpanded;
    }

    private void ExpandGap_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is WorkspacePanelViewModel { DiffModel: { } model } vm &&
            sender is Button { DataContext: DiffRowViewModel row })
            vm.ExpandGap(model, row);
    }

    /// <summary>diff 的更多操作：复制全文、复制路径、在新预览里打开、切换自动换行。</summary>
    private void ChangeDiffMenu_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not WorkspacePanelViewModel vm) return;
        var menu = new ContextMenu();
        menu.Items.Add(Item(vm.WordWrapText, vm.ToggleWordWrap));
        menu.Items.Add(Item("复制差异文本", () => _ = vm.CopyDiffTextAsync()));
        menu.Items.Add(Item("复制文件路径", () => _ = vm.CopyChangePathCommand.ExecuteAsync(null)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("打开当前文件", () => PreviewSelectedChange_Click(sender, args)));
        menu.Open(sender as Control ?? this);

        static MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }
    }

    private void PreviewSelectedChange_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is WorkspacePanelViewModel { SelectedChange: { } file } vm)
        {
            vm.OpenFilePreview(file.FullPath);
            vm.OpenFilesCommand.Execute(null);
            PreviewRequested?.Invoke();
        }
    }

}
