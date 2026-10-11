using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
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

    private sealed record Position(Control Control, int Column, int Row, int ColumnSpan, int RowSpan,
        Thickness Margin, HorizontalAlignment Alignment);
    private sealed record RowLayout(ColumnDefinitions Columns, RowDefinitions Rows, IReadOnlyList<Position> Positions);

    public SettingsPage()
    {
        InitializeComponent();
        EndpointKeyBox.TextChanged += (_, _) => _viewModel?.SetEndpointKeyDraftPresence(!string.IsNullOrEmpty(EndpointKeyBox.Text));
        Loaded += (_, _) => { AttachViewModel(); UpdateResponsiveLayout(); };
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
        }
    }

    private void OnDraftsDiscarded(object? sender, EventArgs args) => EndpointKeyBox.Text = string.Empty;
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(SettingsPageViewModel.SelectedCategory)) return;
        if (!_restoringScroll && _displayedCategory is { } previous) _categoryOffsets[previous] = SettingsScroller.Offset;
        _displayedCategory = _viewModel?.SelectedCategory?.Id;
        var id = _displayedCategory;
        var generation = ++_scrollGeneration;
        _restoringScroll = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _scrollGeneration) return;
            SettingsScroller.Offset = id is not null && _categoryOffsets.TryGetValue(id, out var offset) ? offset : default;
            _restoringScroll = false;
            UpdateResponsiveLayout();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>把浮窗 VM 交给功能页，使浮窗开关在设置里可见可改。</summary>
    internal void AttachFloating(FloatingPanelViewModel floating)
    {
        WorkbenchSections.AttachFloating(floating);
        WorkbenchSections.PageRequested += id => (DataContext as SettingsPageViewModel)?.OpenPage(id);
    }

    internal void UpdateResponsiveLayout()
    {
        if (Bounds.Width <= 0) return;
        var compact = Bounds.Width < 720;
        if (_compactNavigation != compact)
        {
            _compactNavigation = compact;
            SettingsCategoryList.IsVisible = !compact;
            CompactSettingsCategories.IsVisible = compact;
            SettingsBody.ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "248,*");
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
