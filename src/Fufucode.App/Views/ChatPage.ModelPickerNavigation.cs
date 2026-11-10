using System;
using System.Collections.Specialized;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

public partial class ChatPage
{
    private SettingsPageViewModel? _modelNavigationSettings;
    private string? _modelNavigationValue;
    private readonly ImeEnterGuard _modelNavigationIme = new();
    private TextPresenter? _modelSearchPresenter;
    private bool _modelNavigationRefreshScheduled;

    private void ModelPickerNavigation_Opened(object? sender, EventArgs e)
    {
        ModelPickerNavigation_Closed(sender, e);
        ModelPicker_Opened(sender, e);
        if (DataContext is not MainViewModel vm || ModelButton.Flyout is not Flyout { Content: Control surface }) return;
        _modelNavigationSettings = vm.Settings;
        vm.Settings.FilteredModelChoices.CollectionChanged += ModelNavigationChoicesChanged;
        surface.AddHandler(KeyDownEvent, ModelPickerNavigation_KeyDown, RoutingStrategies.Tunnel);
        ModelPickerSearchBox.ApplyTemplate();
        _modelSearchPresenter = ModelPickerSearchBox.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        if (_modelSearchPresenter is not null) _modelSearchPresenter.PropertyChanged += ModelSearchPreeditChanged;
        _modelNavigationIme.OnPreeditChanged(_modelSearchPresenter?.PreeditText);
        UpdateModelNavigation();
    }

    private void ModelPickerNavigation_Closed(object? sender, EventArgs e)
    {
        if (_modelNavigationSettings is { } settings)
        {
            settings.FilteredModelChoices.CollectionChanged -= ModelNavigationChoicesChanged;
            foreach (var item in settings.FilteredModelChoices)
            {
                item.IsNavigationActive = false;
                item.IsNavigationSelected = false;
            }
        }
        if (ModelButton.Flyout is Flyout { Content: Control surface })
            surface.RemoveHandler(KeyDownEvent, ModelPickerNavigation_KeyDown);
        if (_modelSearchPresenter is not null) _modelSearchPresenter.PropertyChanged -= ModelSearchPreeditChanged;
        _modelSearchPresenter = null;
        _modelNavigationSettings = null;
        _modelNavigationValue = null;
    }

    private void ModelSearchPreeditChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextPresenter.PreeditTextProperty)
            _modelNavigationIme.OnPreeditChanged(e.NewValue as string);
    }

    private void ModelPickerNavigation_SearchChanged(object? sender, TextChangedEventArgs e)
    {
        _modelNavigationValue = null;
        ModelPickerSearchBox_TextChanged(sender, e);
        UpdateModelNavigation();
    }

    private void ModelNavigationChoicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 刷新/置顶会同步重建整个列表，合并通知后再核对导航目标。
        if (_modelNavigationRefreshScheduled) return;
        _modelNavigationRefreshScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _modelNavigationRefreshScheduled = false;
            UpdateModelNavigation();
        }, DispatcherPriority.Loaded);
    }

    private void UpdateModelNavigation()
    {
        if (_modelNavigationSettings is not { } settings) return;
        if (!settings.FilteredModelChoices.Any(item => item.Value == _modelNavigationValue))
            _modelNavigationValue = null;
        foreach (var item in settings.FilteredModelChoices)
        {
            item.IsNavigationActive = _modelNavigationValue is not null;
            item.IsNavigationSelected = item.Value == _modelNavigationValue;
        }
    }

    private void ModelPickerNavigation_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key is not (Key.Up or Key.Down or Key.Enter) ||
            e.KeyModifiers != KeyModifiers.None || _modelNavigationIme.ShouldSwallowEnter() ||
            _modelNavigationSettings is not { CanChangeModel: true } settings ||
            DataContext is not MainViewModel vm || !ReferenceEquals(vm.Settings, settings)) return;
        var items = settings.FilteredModelChoices;
        if (items.Count == 0) return;
        var index = items.ToList().FindIndex(item => item.Value == _modelNavigationValue);
        if (e.Key == Key.Enter)
        {
            if (index < 0) return;
            if (settings.ChooseModelCommand.CanExecute(items[index]))
            {
                settings.ChooseModelCommand.Execute(items[index]);
                _modelNavigationValue = null;
                UpdateModelNavigation();
            }
        }
        else
        {
            index = index < 0 ? (e.Key == Key.Down ? 0 : items.Count - 1)
                : Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, items.Count - 1);
            _modelNavigationValue = items[index].Value;
            UpdateModelNavigation();
            var selectedValue = _modelNavigationValue;
            Dispatcher.UIThread.Post(() =>
            {
                if (_modelNavigationSettings != settings || _modelNavigationValue != selectedValue) return;
                if (ModelButton.Flyout is Flyout { Content: Control surface })
                    surface.GetVisualDescendants().OfType<ModelChoiceBorder>()
                        .FirstOrDefault(border => border.DataContext is ModelPickerItem item && item.Value == selectedValue)
                        ?.BringIntoView();
            }, DispatcherPriority.Loaded);
        }
        e.Handled = true;
    }
}
