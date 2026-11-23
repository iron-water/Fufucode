using Avalonia.Controls;
using Avalonia.Input;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

public partial class UsageDetailsView : UserControl
{
    internal void SetCloseAllowed(bool allowed)
    { ClosePageButton.IsEnabled = allowed; Avalonia.Automation.AutomationProperties.SetHelpText(ClosePageButton, allowed ? "关闭用量页" : "请先取消固定页签"); }
    private UsageDetailsViewModel? _observed;

    public UsageDetailsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Observe(DataContext as UsageDetailsViewModel);
        DetachedFromVisualTree += (_, _) => Observe(null);
        AttachedToVisualTree += (_, _) => Observe(DataContext as UsageDetailsViewModel);
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || DataContext is not UsageDetailsViewModel vm) return;
            vm.CloseCommand.Execute(null);
            args.Handled = true;
        };
    }

    private void Observe(UsageDetailsViewModel? vm)
    {
        if (_observed is { } old)
        {
            old.PropertyChanged -= UsageChanged;
            old.Counters.CollectionChanged -= CountersChanged;
        }
        _observed = vm;
        if (vm is not null)
        {
            vm.PropertyChanged += UsageChanged;
            vm.Counters.CollectionChanged += CountersChanged;
        }
        UpdateSkeleton();
    }

    private void UsageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(UsageDetailsViewModel.IsLoading)) UpdateSkeleton();
    }

    private void CountersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateSkeleton();

    /// <summary>首次读取、列表仍为空时显示计数行形状的骨架；已有数据刷新时保留旧值，只靠进度条。</summary>
    private void UpdateSkeleton()
    {
        var show = _observed is { IsLoading: true, Counters.Count: 0 };
        if (show && UsageSkeletonHost.Child is null) UsageSkeletonHost.Child = CounterSkeleton();
        UsageSkeletonHost.IsVisible = show;
        if (show && UsageSkeletonHost.Child is SkeletonSurface surface) surface.Restart();
    }

    private static SkeletonSurface CounterSkeleton()
    {
        var rows = new StackPanel { Spacing = 24, Margin = new Avalonia.Thickness(0, 12) };
        foreach (var fraction in new[] { .5, .35, .45, .3 })
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
            row.Children.Add(SkeletonSurface.Line(fraction));
            var value = new StackPanel { Spacing = 6 };
            value.Children.Add(SkeletonSurface.Bone(72, 14));
            value.Children.Add(SkeletonSurface.Bone(48, 10));
            Grid.SetColumn(value, 1); row.Children.Add(value);
            rows.Children.Add(row);
        }
        return new SkeletonSurface(rows);
    }
}