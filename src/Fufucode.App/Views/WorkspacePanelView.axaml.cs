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

            var diagram = this.FindControl<RichMarkdownView>("DiagramPreview");
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
            model.DiagramError = "导出失败：" + error.Message;
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
        // D6：Files 选中/双击 → E1 内置浏览器预览。走 E 侧公共入口 TryPreviewFile，
        // 它按 x:Name=PreviewBrowser 契约定位宿主，右栏收起/未初始化时安全返回 false。
        FilesList.SelectionChanged += OnFilesSelectionChanged;
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
        vm.BrowseFileCommand.Execute(file);
        if (file.IsDirectory) return;
        if (!ArmPreview(file)) return;
        if (reopen)
        {
            vm.OpenFilePreview(file.FullPath);
            PreviewBrowser.PreviewFile(file.FullPath);
            PreviewRequested?.Invoke();
        }
    }

    /// <summary>
    /// 选中文件（键盘或列表自身选中）→ 把路径推给内置浏览器，进入文件预览态，但<b>不</b>抢走 Files tab：
    /// 每次点击都跳到 Preview 会让目录连续浏览不可用（guideline §3.4 要求 tab 身份稳定）。
    /// </summary>
    private void OnFilesSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        ArmPreview(SelectedFileItem());
    }

    private static bool IsSamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void PreviewSelectedFile_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is WorkspacePanelViewModel { SelectedFile: { IsDirectory: false } file } vm && ArmPreview(file))
        {
            vm.OpenFilePreview(file.FullPath);
            PreviewBrowser.PreviewFile(file.FullPath);
            PreviewRequested?.Invoke();
        }
    }

    private void PreviewSelectedChange_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is WorkspacePanelViewModel { SelectedChange: { } file } vm)
        {
            var item = new WorkspacePanelFileItemViewModel(System.IO.Path.GetFileName(file.FullPath), file.Path, file.FullPath, false, false);
            if (ArmPreview(item))
            {
                vm.OpenFilePreview(file.FullPath);
                PreviewBrowser.PreviewFile(file.FullPath);
                vm.OpenFilesCommand.Execute(null);
            PreviewRequested?.Invoke();
            }
        }
    }

    private WorkspacePanelFileItemViewModel? SelectedFileItem() =>
        (FilesList.SelectedItem ?? (DataContext as WorkspacePanelViewModel)?.SelectedFile)
            as WorkspacePanelFileItemViewModel;

    private bool ArmPreview(WorkspacePanelFileItemViewModel? file) =>
        file is { IsDirectory: false } && (BrowserView.TryPreviewFile(this, file.FullPath) || BrowserView.TryPreviewFile(FilesContent, file.FullPath));
}
