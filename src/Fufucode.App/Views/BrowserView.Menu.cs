using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Fufucode.App.Views;

public partial class BrowserView
{
    private void More_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor) return;
        _viewModel.IsHistoryOpen = _viewModel.IsDownloadsOpen = _viewModel.IsSuggestionsOpen = _viewModel.IsFeedbackOpen = false;
        anchor.ContextMenu = BuildBrowserMenu();
        anchor.ContextMenu.Open(anchor);
    }

    internal ContextMenu BuildBrowserMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedRight };
        MenuItem Item(string text, string icon, ICommand command)
        {
            var item = new MenuItem { Header = text, Command = command };
            if (this.TryFindResource("Icon." + icon, out var resource) && resource is Geometry geometry)
                item.Icon = new PathIcon { Data = geometry, Width = 16, Height = 16 };
            return item;
        }
        menu.Items.Add(Item("新标签页", "Add", _viewModel.NewTabCommand));
        var file = new MenuItem { Header = "打开文件" };
        if (this.TryFindResource("Icon.Folder", out var folder) && folder is Geometry folderGeometry)
            file.Icon = new PathIcon { Data = folderGeometry, Width = 16, Height = 16 };
        file.Click += OpenFile_Click;
        menu.Items.Add(file);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("复制链接", "Copy", _viewModel.CopyAddressCommand));
        menu.Items.Add(Item("分享链接", "Share", _viewModel.ShareCommand));
        menu.Items.Add(Item(_viewModel.IsCurrentBookmarked ? "取消收藏" : "收藏页面", "Pin", _viewModel.ToggleBookmarkCommand));
        menu.Items.Add(Item("在默认浏览器打开", "Open", _viewModel.OpenExternalCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("页内查找", "Search", _viewModel.ToggleFindCommand));
        var zoom = new MenuItem { Header = $"缩放 · {_viewModel.ZoomPercent}%" };
        zoom.Items.Add(Item("放大", "ZoomIn", _viewModel.ZoomInCommand));
        zoom.Items.Add(Item("缩小", "ZoomOut", _viewModel.ZoomOutCommand));
        zoom.Items.Add(Item("恢复 100%", "ArrowClockwise", _viewModel.ZoomResetCommand));
        menu.Items.Add(zoom);
        menu.Items.Add(Item(_viewModel.FullViewToolTip, "FullScreenMaximize", _viewModel.ToggleFullViewCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("浏览历史", "History", _viewModel.ToggleHistoryCommand));
        menu.Items.Add(Item("下载", "ArrowDownload", _viewModel.ToggleDownloadsCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("开发者工具", "Code", _viewModel.OpenDevToolsCommand));
        return menu;
    }
}
