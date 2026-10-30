using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Fufucode.App.Views;

public partial class SettingsPage
{
    private readonly TranslateTransform _navigationMarkerOffset = new();
    private IDisposable? _navigationMarkerAnimation;
    private double _navigationMarkerTarget = double.NaN;
    private object? _navigationMarkerItem;

    private void InitializeNavigationMarker()
    {
        SettingsNavigationMarker.RenderTransform = _navigationMarkerOffset;
        // Layout changes include realization, search results, scrolling and responsive navigation.
        SettingsNavigationTrack.LayoutUpdated += (_, _) => UpdateNavigationMarker();
        SettingsCategoryList.SelectionChanged += (_, _) => UpdateNavigationMarker();
        DetachedFromVisualTree += (_, _) => HideNavigationMarker();
        PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty && !IsVisible) HideNavigationMarker();
        };
    }

    private void HideNavigationMarker()
    {
        _navigationMarkerAnimation?.Dispose();
        _navigationMarkerAnimation = null;
        _navigationMarkerTarget = double.NaN;
        _navigationMarkerItem = null;
        SettingsNavigationMarker.IsVisible = false;
    }

    private void UpdateNavigationMarker()
    {
        if (!IsEffectivelyVisible || !SettingsCategoryList.IsEffectivelyVisible ||
            TopLevel.GetTopLevel(this) is null || SettingsCategoryList.SelectedItem is not { } selected)
        {
            HideNavigationMarker();
            return;
        }
        var row = SettingsCategoryList.GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(item => item.IsSelected && ReferenceEquals(item.DataContext, selected));
        var position = row?.TranslatePoint(default, SettingsNavigationTrack);
        if (row is null || position is null || row.Bounds.Height <= 0)
        {
            HideNavigationMarker();
            return;
        }
        var target = position.Value.Y + (row.Bounds.Height - SettingsNavigationMarker.Height) / 2;
        // Offscreen selected rows must not leave a stale indicator in the viewport.
        if (target < 0 || target + SettingsNavigationMarker.Height > SettingsNavigationTrack.Bounds.Height)
        {
            HideNavigationMarker();
            return;
        }
        _navigationMarkerOffset.X = position.Value.X + 4;
        var selectionChanged = !ReferenceEquals(_navigationMarkerItem, selected);
        if (!selectionChanged && Math.Abs(target - _navigationMarkerTarget) < 0.1) return;
        var animate = SettingsNavigationMarker.IsVisible && selectionChanged &&
            !Motion.Reduced && !Integration.MemoryProfile.DisableVisualEffects;
        _navigationMarkerAnimation?.Dispose();
        _navigationMarkerAnimation = null;
        _navigationMarkerItem = selected;
        _navigationMarkerTarget = target;
        SettingsNavigationMarker.IsVisible = true;
        if (!animate) _navigationMarkerOffset.Y = target;
        else _navigationMarkerAnimation = Motion.AnimateValue(_navigationMarkerOffset.Y, target,
            Motion.StandardDuration, value => _navigationMarkerOffset.Y = value);
    }
}
