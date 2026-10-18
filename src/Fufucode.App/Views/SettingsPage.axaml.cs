using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

public partial class SettingsPage : UserControl
{
    private readonly Dictionary<Grid, RowLayout> _rowLayouts = new();
    private readonly Dictionary<string, Vector> _categoryOffsets = new();
    private SettingsPageViewModel? _viewModel;
    private string? _displayedCategory;
    private bool _restoringScroll;
    private int _scrollGeneration;
    private bool? _compactNavigation;
    private bool _syncingNav;

    private sealed record Position(Control Control, int Column, int Row, int ColumnSpan, int RowSpan,
        Thickness Margin, HorizontalAlignment Alignment);
    private sealed record RowLayout(ColumnDefinitions Columns, RowDefinitions Rows, IReadOnlyList<Position> Positions);

    public SettingsPage()
    {
        InitializeComponent();
        EndpointKeyBox.TextChanged += (_, _) => _viewModel?.SetEndpointKeyDraftPresence(!string.IsNullOrEmpty(EndpointKeyBox.Text));
        // 侧栏选中项不双向绑定：搜索/升降级会重建条目实例，双向绑定会把选中写回成 null。
        SettingsCategoryList.SelectionChanged += OnNavSelectionChanged;
        CompactSettingsCategories.SelectionChanged += OnNavSelectionChanged;
        Loaded += (_, _) =>
        {
            AttachViewModel();
            UpdateResponsiveLayout();
            // 首帧渲染后才同步选中——Loaded 时 Items 可能尚未 realize，SelectedItem 写入会被吞。
            Dispatcher.UIThread.Post(SyncNavSelection, DispatcherPriority.Loaded);
        };
        DataContextChanged += (_, _) => AttachViewModel();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        SettingsScroller.ScrollChanged += (_, _) =>
        {
            if (!_restoringScroll && _displayedCategory is { } id) _categoryOffsets[id] = SettingsScroller.Offset;
        };
    }

    private async void OnExportDiagnostics(object? sender, RoutedEventArgs args)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window)
            await window.ExecuteWorkbenchActionAsync("diagnostics.export");
    }

    private void AttachViewModel()
    {
        if (ReferenceEquals(_viewModel, DataContext)) return;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.DraftsDiscarded -= OnDraftsDiscarded;
            _viewModel.NavigationRebuilt -= OnNavigationRebuilt;
        }
        _viewModel = DataContext as SettingsPageViewModel;
        _categoryOffsets.Clear();
        _displayedCategory = _viewModel?.SelectedCategory?.Id;
        _scrollGeneration++;
        _restoringScroll = false;
        EndpointKeyBox.Text = string.Empty;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.DraftsDiscarded += OnDraftsDiscarded;
            _viewModel.NavigationRebuilt += OnNavigationRebuilt;
        }
        SyncNavSelection();
    }

    private void OnDraftsDiscarded(object? sender, EventArgs args) => EndpointKeyBox.Text = string.Empty;
    private void OnNavigationRebuilt(object? sender, EventArgs args) => SyncNavSelection();

    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_syncingNav || _viewModel is null) return;
        if (sender is ListBox { SelectedItem: SettingsCategoryViewModel category })
            _viewModel.SelectCategory(category.Id);
    }

    /// <summary>把 VM 的选中分类同步到侧栏列表；条目实例会随搜索/升降级重建，因此按 Id 查找。</summary>
    private void SyncNavSelection()
    {
        if (_viewModel is null) return;
        _syncingNav = true;
        try
        {
            var id = _viewModel.SelectedCategory?.Id;
            // 选中「其他」桶里的小功能（不在一级列表）时，回退高亮到「其他」占位项。
            var selected = FindNavItem(SettingsCategoryList, id);
            if (id is not null && selected is null)
                selected = FindNavItem(SettingsCategoryList, SettingsPageViewModel.OtherHubId);
            SettingsCategoryList.SelectedItem = selected;

            var compactSelected = FindNavItem(CompactSettingsCategories, id);
            if (id is not null && compactSelected is null)
                compactSelected = FindNavItem(CompactSettingsCategories, SettingsPageViewModel.OtherHubId);
            CompactSettingsCategories.SelectedItem = compactSelected;
        }
        finally
        {
            _syncingNav = false;
        }
    }

    private static SettingsCategoryViewModel? FindNavItem(ListBox list, string? id) =>
        id is null ? null : list.ItemsSource?.OfType<SettingsCategoryViewModel>().FirstOrDefault(item => item.Id == id);

    /// <summary>内容切换动画（Win11 设置：新页淡入+轻微上滑 ~150ms）。
    /// Opacity 走 DoubleTransition，位移走 TransformOperationsTransition（RenderTransform
    /// 在样式里初始化为 translate(0,0)，这里先压起始偏移再回 0 触发补间）。</summary>
    private CancellationTokenSource? _contentAnimCts;

    private void AnimateContentIn()
    {
        _contentAnimCts?.Cancel();
        _contentAnimCts = new CancellationTokenSource();
        var host = SettingsContentHost;
        host.Opacity = 0;
        host.RenderTransform = TransformOperations.Parse("translate(0px,12px)");
        Dispatcher.UIThread.Post(() =>
        {
            if (_contentAnimCts.IsCancellationRequested) return;
            host.Opacity = 1;
            host.RenderTransform = TransformOperations.Parse("translate(0px,0px)");
        }, DispatcherPriority.Render);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        // 进入/退出子页时内容整页更换：滚动位置必须回到顶部，
        // 否则会停在上一页的偏移处，看起来像一片空白。
        if (args.PropertyName == nameof(SettingsPageViewModel.ActivePageId))
        {
            Dispatcher.UIThread.Post(() =>
            {
                SettingsScroller.Offset = default;
                AnimateContentIn();
            }, DispatcherPriority.Loaded);
            return;
        }

        if (args.PropertyName != nameof(SettingsPageViewModel.SelectedCategory)) return;
        SyncNavSelection();
        if (!_restoringScroll && _displayedCategory is { } previous) _categoryOffsets[previous] = SettingsScroller.Offset;
        _displayedCategory = _viewModel?.SelectedCategory?.Id;
        var id = _displayedCategory;
        var generation = ++_scrollGeneration;
        _restoringScroll = true;
        AnimateContentIn();
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _scrollGeneration) return;
            SettingsScroller.Offset = id is not null && _categoryOffsets.TryGetValue(id, out var offset) ? offset : default;
            _restoringScroll = false;
            UpdateResponsiveLayout();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>三个通知渠道共用一份已持久化偏好。</summary>
    internal void AttachNotificationDelivery(NotificationDeliveryViewModel delivery)
    {
        WorkbenchSections.AttachNotificationDelivery(delivery);
        WorkbenchSections.PageRequested += id => (DataContext as SettingsPageViewModel)?.OpenPage(id);
    }

    /// <summary>浏览器设置真源（BRW-1）：设置页只做投影，真源由组合根创建并落盘。</summary>
    internal void AttachBrowserSettings(BrowserSettingsViewModel browserSettings)
        => WorkbenchSections.AttachBrowserSettings(browserSettings);

    internal void UpdateResponsiveLayout()
    {
        if (Bounds.Width <= 0) return;
        var compact = Bounds.Width < 720;
        if (_compactNavigation != compact)
        {
            _compactNavigation = compact;
            SettingsCategoryList.IsVisible = !compact;
            SettingsSearchBox.IsVisible = !compact;
            CompactSettingsCategories.IsVisible = compact;
            SettingsBody.ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "300,*");
            SettingsBody.RowDefinitions = new RowDefinitions(compact ? "Auto,*" : "*");
            Grid.SetColumn(SettingsContentArea, compact ? 0 : 1);
            Grid.SetRow(SettingsContentArea, compact ? 1 : 0);
        }

        var controlBudget = Bounds.Width - 32 - (compact ? 0 : 196) - 48;
        var narrowRows = controlBudget < 640;
        foreach (var grid in this.GetVisualDescendants().OfType<Grid>().Where(x => x.Classes.Contains("settingsControlRow") || x.Classes.Contains("settingsPathRow")))
        {
            if (!_rowLayouts.TryGetValue(grid, out var original))
            {
                original = new RowLayout(grid.ColumnDefinitions, grid.RowDefinitions, grid.Children.Select(child => new Position(
                    child, Grid.GetColumn(child), Grid.GetRow(child), Grid.GetColumnSpan(child), Grid.GetRowSpan(child), child.Margin, child.HorizontalAlignment)).ToArray());
                _rowLayouts.Add(grid, original);
            }
            if (!narrowRows)
            {
                grid.ColumnDefinitions = original.Columns;
                grid.RowDefinitions = original.Rows;
                foreach (var position in original.Positions) RestorePosition(position);
                continue;
            }
            var isPath = grid.Classes.Contains("settingsPathRow");
            grid.ColumnDefinitions = new ColumnDefinitions(isPath ? "*,Auto" : "28,*");
            grid.RowDefinitions = new RowDefinitions("Auto,Auto");
            foreach (var position in original.Positions)
            {
                if (isPath)
                {
                    Grid.SetRow(position.Control, position.Column == 0 ? 0 : 1);
                    Grid.SetColumn(position.Control, position.Column == 2 ? 1 : 0);
                    Grid.SetColumnSpan(position.Control, position.Column == 0 ? 2 : 1);
                }
                else
                {
                    Grid.SetRow(position.Control, position.Column >= 2 ? 1 : 0);
                    Grid.SetColumn(position.Control, position.Column >= 2 ? 0 : position.Column);
                    Grid.SetColumnSpan(position.Control, position.Column >= 2 ? 2 : 1);
                }
                if (position.Column >= 2) position.Control.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
        }
        AboutActions.Orientation = narrowRows ? Orientation.Vertical : Orientation.Horizontal;
    }

    private static void RestorePosition(Position position)
    {
        Grid.SetColumn(position.Control, position.Column);
        Grid.SetRow(position.Control, position.Row);
        Grid.SetColumnSpan(position.Control, position.ColumnSpan);
        Grid.SetRowSpan(position.Control, position.RowSpan);
        position.Control.Margin = position.Margin;
        position.Control.HorizontalAlignment = position.Alignment;
    }

    /// <summary>保存端点：密钥从密码框读出后即清空，绝不进入 VM 字符串属性。</summary>
    /// <summary>「模型与思考」首页的提供商行：与其它行一致，用行级点击而不是内部按钮。</summary>
    internal void OnOpenProvidersPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not SettingsPageViewModel vm) return;
        if (!e.GetCurrentPoint(sender as Control).Properties.IsLeftButtonPressed) return;
        vm.OpenProviderListCommand.Execute(null);
    }

    /// <summary>预设行：点击用该预设建立提供商并进入编辑页。</summary>
    internal void OnPresetPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not SettingsPageViewModel vm) return;
        if (!e.GetCurrentPoint(sender as Control).Properties.IsLeftButtonPressed) return;
        if (sender is not Control { Tag: ProviderPreset preset }) return;
        vm.ChoosePresetCommand.Execute(preset);
    }

    /// <summary>提供商列表行：点击进入该提供商的模型选择页。</summary>
    internal void OnEditProviderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not SettingsPageViewModel vm) return;
        if (!e.GetCurrentPoint(sender as Control).Properties.IsLeftButtonPressed) return;
        if (sender is not Control { Tag: EndpointProviderItem provider }) return;
        vm.EditProviderCommand.Execute(provider);
    }

    internal async void OnSaveEndpointClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsPageViewModel vm)
        {
            return;
        }

        var key = EndpointKeyBox.Text;
        EndpointKeyBox.Text = string.Empty;
        await vm.SaveEndpointAsync(key);
    }
}
