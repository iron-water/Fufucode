using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Fufucode.App.Features.Library;

/// <summary>
/// 资料库页面视图（自包含，未接共享外壳）。
/// 视图自己提供 ILibraryFilePicker（基于 TopLevel.StorageProvider）并处理引用复制，
/// 宿主只需把它注册为工作台页面（RegisterOptional）并注入 LibraryViewModel。
///
/// 生命周期约定：
/// - 临时离开视觉树（工作台停靠换栏会重挂同一控件）**不**释放视图模型，刷新/检索/引用仍可用；
/// - 只有宿主调用 <see cref="Detach"/> 才取消订阅并释放视图模型；
/// - 同一实例重复 <see cref="Attach"/> 幂等，不会重复订阅或重复初始化；
/// - 已释放的视图模型不能重新挂载，宿主重建页面时应创建新实例。
/// </summary>
public partial class LibraryPageView : UserControl, ILibraryFilePicker
{
    private LibraryViewModel? _viewModel;
    private bool _narrowLayout;

    public LibraryPageView() => InitializeComponent();

    /// <summary>
    /// 窄容器（例如默认宽度的右侧停靠栏）下把“列表 + 详情”和页头改为上下堆叠。
    /// 固定 340 列表列在 320 宽停靠栏里会把详情列压成 0 宽，导致标签/引用/删除按钮不可达。
    /// </summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyNarrowLayout(Bounds.Width);
    }

    /// <summary>按当前宽度切换两列/堆叠布局；同一宽度状态内幂等，不重复改布局。</summary>
    private void ApplyNarrowLayout(double width)
    {
        // 页头 + 列表 340 + 间距 12 + 左右留白 32：低于此宽度两列布局必然挤压详情列。
        const double twoColumnMinimum = 720;
        var narrow = width > 0 && width < twoColumnMinimum;
        if (narrow == _narrowLayout) return;
        _narrowLayout = narrow;

        if (narrow)
        {
            HeaderGrid.ColumnDefinitions = new ColumnDefinitions("*");
            HeaderGrid.RowDefinitions = new RowDefinitions("Auto,Auto");
            HeaderGrid.RowSpacing = 8;
            Grid.SetRow(HeaderActions, 1);
            EntriesGrid.ColumnDefinitions = new ColumnDefinitions("*");
            // 列表行只保留可滚动最小高度：详情行按内容排布，避免窄停靠栏里纵向溢出把按钮推出可视区。
            EntriesGrid.RowDefinitions = new RowDefinitions("120,*");
            EntriesGrid.RowSpacing = 8;
            Grid.SetColumn(EntryDetail, 0);
            Grid.SetRow(EntryDetail, 1);
            // 长路径与提示语在 320 宽里换行数行，会把操作按钮推出可视区；窄模式改为单行截断。
            DetailSource.TextWrapping = TextWrapping.NoWrap;
            DetailSource.TextTrimming = TextTrimming.CharacterEllipsis;
            DetailNote.TextWrapping = TextWrapping.NoWrap;
            DetailNote.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        else
        {
            HeaderGrid.ColumnDefinitions = new ColumnDefinitions("*,Auto");
            // 清空行定义以还原 XAML 的隐式单行（Star）；显式 Auto 会让列表不再撑满可用高度。
            HeaderGrid.RowDefinitions.Clear();
            HeaderGrid.RowSpacing = 0;
            Grid.SetRow(HeaderActions, 0);
            EntriesGrid.ColumnDefinitions = new ColumnDefinitions("340,*");
            EntriesGrid.RowDefinitions.Clear();
            EntriesGrid.RowSpacing = 0;
            Grid.SetColumn(EntryDetail, 1);
            Grid.SetRow(EntryDetail, 0);
            DetailSource.TextWrapping = TextWrapping.Wrap;
            DetailSource.TextTrimming = TextTrimming.None;
            DetailNote.TextWrapping = TextWrapping.Wrap;
            DetailNote.TextTrimming = TextTrimming.None;
        }
    }

    public LibraryViewModel? ViewModel => _viewModel;

    /// <summary>当前是否已绑定视图模型。</summary>
    public bool IsAttached => _viewModel is not null;

    /// <summary>绑定视图模型：注入选择器、订阅引用复制、启动加载与后台索引。</summary>
    public void Attach(LibraryViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (ReferenceEquals(_viewModel, viewModel))
        {
            // 幂等：同一实例重复挂载不重复订阅、不重复初始化。
            DataContext = viewModel;
            return;
        }
        if (viewModel.IsDisposed)
            throw new ObjectDisposedException(nameof(LibraryViewModel), "视图模型已释放；重建页面时请创建新的 LibraryViewModel。");
        if (_viewModel is not null) ReleaseViewModel();
        _viewModel = viewModel;
        viewModel.Picker = this;
        viewModel.CitationCopyRequested -= OnCitationCopyRequested;
        viewModel.CitationCopyRequested += OnCitationCopyRequested;
        DataContext = viewModel;
        _ = viewModel.InitializeAsync();
    }

    /// <summary>最终释放：取消订阅并释放视图模型。临时离开视觉树不会走到这里。</summary>
    public void Detach() => ReleaseViewModel();

    private void ReleaseViewModel()
    {
        var viewModel = _viewModel;
        if (viewModel is null) return;
        _viewModel = null;
        viewModel.CitationCopyRequested -= OnCitationCopyRequested;
        if (ReferenceEquals(DataContext, viewModel)) DataContext = null;
        viewModel.Dispose();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_viewModel is null && DataContext is LibraryViewModel viewModel) Attach(viewModel);
    }

    // 工作台停靠换栏会把页面临时移出视觉树再放回；这不等于所有者销毁，
    // 因此这里刻意不释放视图模型（释放只发生在宿主调用 Detach 时）。
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) =>
        base.OnDetachedFromVisualTree(e);

    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return Array.Empty<string>();
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入到资料库",
            AllowMultiple = true,
        });
        return files.Select(file => file.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private async void OnCitationCopyRequested(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
        }
        catch (Exception)
        {
            // 剪贴板不可用不影响引用文本生成。
        }
    }

    private void TagChip_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        if (sender is Button { Tag: string tag }) _viewModel.SelectedTag = tag;
    }

    private void RemoveTag_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        if (sender is Button { Tag: string tag }) _viewModel.RemoveTagCommand.Execute(tag);
    }
}
