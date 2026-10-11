using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

public partial class WorkspacePanelView : UserControl
{
    public event Action? PreviewRequested;

    internal (Control Files, Control Changes, BrowserView Preview) TakeDockPages()
    {
        var root = (Grid)Content!;
        root.Children.Remove(FilesContent);
        root.Children.Remove(ChangesContent);
        root.Children.Remove(PreviewBrowser);
        FilesContent.IsVisible = ChangesContent.IsVisible = PreviewBrowser.IsVisible = true;
        return (FilesContent, ChangesContent, PreviewBrowser);
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
        if (reopen) { vm.OpenPreviewCommand.Execute(null); PreviewRequested?.Invoke(); }
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
        if (DataContext is WorkspacePanelViewModel { SelectedFile: { IsDirectory: false } file } && ArmPreview(file))
            PreviewRequested?.Invoke();
    }

    private void PreviewSelectedChange_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is WorkspacePanelViewModel { SelectedChange: { } file } && BrowserView.TryPreviewFile(this, file.FullPath))
            PreviewRequested?.Invoke();
    }

    private WorkspacePanelFileItemViewModel? SelectedFileItem() =>
        (FilesList.SelectedItem ?? (DataContext as WorkspacePanelViewModel)?.SelectedFile)
            as WorkspacePanelFileItemViewModel;

    private bool ArmPreview(WorkspacePanelFileItemViewModel? file) =>
        file is { IsDirectory: false } && BrowserView.TryPreviewFile(this, file.FullPath);
}
