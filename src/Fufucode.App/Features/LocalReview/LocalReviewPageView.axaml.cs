using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Fufucode.App.Features.LocalReview;

/// <summary>
/// 本地审查页面视图（自包含，未接共享外壳）。宿主只需注册为工作台页面并注入 LocalReviewViewModel。
///
/// 生命周期约定（与资料库页一致）：
/// - 工作台停靠换栏会把页面临时移出视觉树再放回，这**不**取消运行中的审查，也不释放视图模型；
/// - 只有宿主调用 <see cref="Detach"/> 才取消本模块运行并释放视图模型；
/// - 同一实例重复 <see cref="Attach"/> 幂等，不重复订阅；
/// - 已释放的视图模型不能重新挂载，重建页面时应创建新实例。
/// </summary>
public partial class LocalReviewPageView : UserControl
{
    private LocalReviewViewModel? _viewModel;
    private bool _narrowLayout;

    public LocalReviewPageView() => InitializeComponent();

    /// <summary>
    /// 窄容器（默认宽度的右侧停靠栏）下把“发现项 + 详情”改为上下堆叠：
    /// 固定 340 列在 320 宽停靠栏里会把详情列压成 0 宽，按钮不可达（P1 已登记同类缺陷 D-P1-1）。
    /// </summary>
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
            ResultsGrid.ColumnDefinitions = new ColumnDefinitions("*");
            ResultsGrid.RowDefinitions = new RowDefinitions("120,*");
            ResultsGrid.RowSpacing = 8;
            Grid.SetColumn(DetailPanel, 0);
            Grid.SetRow(DetailPanel, 1);
            // 输入行/按钮行本身是 WrapPanel，窄宽度自动换行；这里只压缩列表高度，
            // 避免纵向溢出把下方按钮推出可视区。
            WorktreeList.MaxHeight = 48;
            HistoryList.MaxHeight = 60;
            // 320 宽下常驻说明会换行数行，把结果区操作按钮推出可视区（与资料库页同一处置）：
            // 隐藏常驻说明，状态行改为单行截断（完整文本仍在结果区与导出中可见）。
            DescriptionBlock.IsVisible = false;
            StatusBlock.TextWrapping = TextWrapping.NoWrap;
            StatusBlock.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        else
        {
            ResultsGrid.ColumnDefinitions = new ColumnDefinitions("340,*");
            // 清空行定义以还原 XAML 的隐式单行（Star），否则详情面板不再撑满高度。
            ResultsGrid.RowDefinitions.Clear();
            ResultsGrid.RowSpacing = 0;
            Grid.SetColumn(DetailPanel, 1);
            Grid.SetRow(DetailPanel, 0);
            WorktreeList.MaxHeight = 120;
            HistoryList.MaxHeight = 120;
            DescriptionBlock.IsVisible = true;
            StatusBlock.TextWrapping = TextWrapping.Wrap;
            StatusBlock.TextTrimming = TextTrimming.None;
        }
    }

    public LocalReviewViewModel? ViewModel => _viewModel;

    public bool IsAttached => _viewModel is not null;

    /// <summary>绑定视图模型；不触发自动运行，所有 Git/审查动作由用户显式发起。</summary>
    public void Attach(LocalReviewViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (ReferenceEquals(_viewModel, viewModel))
        {
            DataContext = viewModel;
            return;
        }
        if (viewModel.IsDisposed)
            throw new ObjectDisposedException(nameof(LocalReviewViewModel),
                "视图模型已释放；重建页面时请创建新的 LocalReviewViewModel。");
        if (_viewModel is not null) ReleaseViewModel();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>最终释放：取消本模块运行并释放视图模型。临时移出视觉树不会走到这里。</summary>
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
        // 停靠换栏的临时移出：保留视图模型与进行中的运行，不做释放或取消。
        base.OnDetachedFromVisualTree(e);
    }

    private void LoadHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        if (HistoryList?.SelectedItem is string fileName) _viewModel.LoadHistoryCommand.Execute(fileName);
    }
}
