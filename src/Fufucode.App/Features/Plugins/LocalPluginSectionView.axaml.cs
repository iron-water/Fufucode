using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Fufucode.App.Features.Plugins;

/// <summary>
/// 可嵌入现有扩展页的本地插件管理控件（E3-LOCAL）。
///
/// 生命周期约定（与本地审查页一致）：
/// - 工作台停靠换栏会把控件临时移出视觉树再放回，这**不**释放视图模型、不取消进行中的动作；
/// - 只有宿主调用 <see cref="Detach"/> 才释放视图模型；
/// - 同一实例重复 <see cref="Attach"/> 幂等；
/// - 已释放的视图模型不能重新挂载，重建时应创建新实例。
/// </summary>
public partial class LocalPluginSectionView : UserControl
{
    private LocalPluginViewModel? _viewModel;
    private bool _narrowLayout;

    public LocalPluginSectionView() => InitializeComponent();

    /// <summary>窄容器下两列列表改为上下堆叠，保证按钮不被压成零宽或挤出可视区。</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyNarrowLayout(Bounds.Width);
    }

    private void ApplyNarrowLayout(double width)
    {
        const double twoColumnMinimum = 720;
        var narrow = width > 0 && width < twoColumnMinimum;
        if (narrow == _narrowLayout) return;
        _narrowLayout = narrow;
        if (narrow)
        {
            ListsGrid.ColumnDefinitions = new ColumnDefinitions("*");
            ListsGrid.RowDefinitions = new RowDefinitions("140,*");
            ListsGrid.RowSpacing = 8;
            Grid.SetColumn(VersionsPanel, 0);
            Grid.SetRow(VersionsPanel, 1);
            DescriptionBlock.IsVisible = false;
            ImportHintBlock.IsVisible = false;
        }
        else
        {
            ListsGrid.ColumnDefinitions = new ColumnDefinitions("*,*");
            ListsGrid.RowDefinitions.Clear();
            ListsGrid.RowSpacing = 0;
            Grid.SetColumn(VersionsPanel, 1);
            Grid.SetRow(VersionsPanel, 0);
            DescriptionBlock.IsVisible = true;
            ImportHintBlock.IsVisible = true;
        }
    }

    public LocalPluginViewModel? ViewModel => _viewModel;

    public bool IsAttached => _viewModel is not null;

    /// <summary>绑定视图模型；不触发自动导入或应用，所有动作由用户显式发起。</summary>
    public void Attach(LocalPluginViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (ReferenceEquals(_viewModel, viewModel))
        {
            DataContext = viewModel;
            return;
        }
        if (viewModel.IsDisposed)
            throw new ObjectDisposedException(nameof(LocalPluginViewModel),
                "视图模型已释放；重建页面时请创建新的 LocalPluginViewModel。");
        if (_viewModel is not null) ReleaseViewModel();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>最终释放：取消本模块动作并释放视图模型。临时移出视觉树不会走到这里。</summary>
    public void Detach() => ReleaseViewModel();

    private void ReleaseViewModel()
    {
        var viewModel = _viewModel;
        if (viewModel is null) return;
        _viewModel = null;
        if (ReferenceEquals(DataContext, viewModel)) DataContext = null;
        viewModel.Dispose();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // 停靠换栏的临时移出：保留视图模型与进行中的动作。
        base.OnDetachedFromVisualTree(e);
    }
}
