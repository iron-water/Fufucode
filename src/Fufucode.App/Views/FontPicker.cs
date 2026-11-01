using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.Views;

/// <summary>当前字体与搜索词分别保存；每次展开从完整字体列表开始。</summary>
internal sealed class FontPicker : UserControl
{
    private readonly IReadOnlyList<AppFontChoice> _choices;
    private readonly Button _button;
    private readonly TextBlock _label;
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly Popup _popup;
    private readonly ImeEnterGuard _imeGuard = new();
    private TextPresenter? _presenter;
    private AppFontChoice[] _visible;
    private string _value = "system";

    internal event Action<string>? SelectionCommitted;
    internal string SelectedValue => _value;
    internal TextBox SearchBox => _search;
    internal ListBox Results => _list;
    internal bool IsDropDownOpen { get => _popup.IsOpen; set => SetOpen(value); }
    protected override Type StyleKeyOverride => typeof(UserControl);

    internal FontPicker(IReadOnlyList<AppFontChoice> choices, string name)
    {
        _choices = choices;
        _visible = choices.ToArray();
        Width = 220;
        VerticalAlignment = VerticalAlignment.Center;
        _label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var arrow = new PathIcon { Width = 10, Height = 10, Margin = new Thickness(12, 0, 0, 0) };
        arrow.Bind(PathIcon.DataProperty, new DynamicResourceExtension("Icon.ChevronDown"));
        var field = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        field.Children.Add(_label); Grid.SetColumn(arrow, 1); field.Children.Add(arrow);
        _button = new Button
        {
            Content = field, MinHeight = 32, Padding = new Thickness(12, 5),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        _button.Classes.Add("fontPickerButton");
        AutomationProperties.SetName(_button, name);
        _search = new TextBox { Classes = { "searchInput" }, PlaceholderText = "搜索字体", Margin = new Thickness(8, 8, 8, 4), MinHeight = 32 };
        AutomationProperties.SetName(_search, "搜索" + name);
        _list = new ListBox
        {
            ItemsSource = _visible, MinHeight = 32, MaxHeight = 288,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<AppFontChoice>((choice, _) =>
                new TextBlock { Text = choice?.Label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }),
        };
        var body = new DockPanel { Width = 300 };
        DockPanel.SetDock(_search, Dock.Top); body.Children.Add(_search); body.Children.Add(_list);
        var surface = new Border { Child = body, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(0, 0, 0, 4) };
        surface.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MenuSurfaceBrush"));
        surface.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("SurfaceStrokeColorFlyoutBrush"));
        _popup = new Popup { Child = surface, PlacementTarget = _button, Placement = PlacementMode.BottomEdgeAlignedRight, IsLightDismissEnabled = true, ShouldUseOverlayLayer = true };
        var root = new Grid(); root.Children.Add(_button); root.Children.Add(_popup); Content = root;
        _button.Click += (_, _) => SetOpen(!_popup.IsOpen);
        _button.KeyDown += (_, args) =>
        {
            if (args.Key is not (Key.Down or Key.Up) && !(args.Key == Key.F4)) return;
            SetOpen(true);
            args.Handled = true;
        };
        _search.TextChanged += (_, _) => Filter();
        _search.TemplateApplied += (_, _) =>
        {
            if (_presenter is not null) _presenter.PropertyChanged -= OnPreeditChanged;
            _presenter = _search.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
            _imeGuard.OnPreeditChanged(_presenter?.PreeditText);
            if (_presenter is not null) _presenter.PropertyChanged += OnPreeditChanged;
        };
        _search.AddHandler(KeyDownEvent, OnSearchKey, RoutingStrategies.Tunnel);
        _list.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) { Commit(preferSelection: true); args.Handled = true; }
            else if (args.Key == Key.Escape) { SetOpen(false); _button.Focus(); args.Handled = true; }
        };
        _list.AddHandler(PointerReleasedEvent, (_, args) =>
        {
            if (args.InitialPressMouseButton != MouseButton.Left || args.Source is not Visual source) return;
            if (source is ListBoxItem || source.GetVisualAncestors().OfType<ListBoxItem>().Any()) Commit(preferSelection: true);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        _popup.Closed += (_, _) => _button.Classes.Remove("open");
        DetachedFromVisualTree += (_, _) => _popup.IsOpen = false;
        SetValue("system");
    }

    internal void SetValue(string value)
    {
        _value = value;
        _label.Text = _choices.FirstOrDefault(choice => choice.Value.Equals(value, StringComparison.OrdinalIgnoreCase))?.Label ?? value + "（不可用）";
    }

    private void SetOpen(bool open)
    {
        if (!open) { _popup.IsOpen = false; return; }
        _search.Text = "";
        Filter();
        _list.SelectedItem = _visible.FirstOrDefault(choice => choice.Value.Equals(_value, StringComparison.OrdinalIgnoreCase));
        _popup.IsOpen = true;
        _button.Classes.Add("open");
        Dispatcher.UIThread.Post(() => { if (_popup.IsOpen) _search.Focus(); }, DispatcherPriority.Loaded);
    }

    private void Filter()
    {
        var query = _search.Text?.Trim() ?? "";
        _visible = _choices.Where(choice => query.Length == 0 || choice.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            choice.Value.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        _list.ItemsSource = _visible;
        _list.SelectedItem = _visible.FirstOrDefault(choice => choice.Value.Equals(_value, StringComparison.OrdinalIgnoreCase));
    }

    private void OnPreeditChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == TextPresenter.PreeditTextProperty) _imeGuard.OnPreeditChanged(_presenter?.PreeditText);
    }

    private void OnSearchKey(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.ImeProcessed || _imeGuard.ShouldSwallowEnter()) return;
        if (args.Key == Key.Tab) { SetOpen(false); return; }
        if (args.Key is Key.Down or Key.Up)
        {
            _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + (args.Key == Key.Down ? 1 : -1), 0, Math.Max(0, _visible.Length - 1));
            if (_list.SelectedItem is { } selected) _list.ScrollIntoView(selected);
            args.Handled = true;
        }
        else if (args.Key == Key.Enter) { Commit(); args.Handled = true; }
        else if (args.Key == Key.Escape) { SetOpen(false); _button.Focus(); args.Handled = true; }
    }

    private void Commit(bool preferSelection = false)
    {
        var exact = _visible.FirstOrDefault(choice => choice.Label.Equals(_search.Text?.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
            choice.Value.Equals(_search.Text?.Trim(), StringComparison.OrdinalIgnoreCase));
        var selected = _list.SelectedItem as AppFontChoice;
        var choice = (preferSelection ? selected : exact) ?? selected ?? (_visible.Length == 1 ? _visible[0] : null);
        if (choice is null) return;
        SetValue(choice.Value);
        _popup.IsOpen = false;
        _button.Focus();
        SelectionCommitted?.Invoke(choice.Value);
    }
}
