using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

public partial class CommandPaletteView : UserControl
{
    private readonly ImeEnterGuard _imeGuard = new();
    private CommandPaletteViewModel? _vm;
    private TextPresenter? _presenter;
    private bool _composing;

    public CommandPaletteView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPaletteKeyDown, RoutingStrategies.Tunnel);
        ActionsList.DoubleTapped += (_, _) => _vm?.ExecuteSelectedCommand.Execute(null);
        DataContextChanged += (_, _) => AttachViewModel();
        SearchBox.TemplateApplied += (_, _) => HookPreedit();
        Loaded += (_, _) => { AttachViewModel(); HookPreedit(); if (_vm?.IsOpen == true) FocusSearch(); };
    }

    private void AttachViewModel()
    {
        if (ReferenceEquals(_vm, DataContext)) return;
        if (_vm is not null) _vm.Opened -= FocusSearch;
        _vm = DataContext as CommandPaletteViewModel;
        if (_vm is not null) _vm.Opened += FocusSearch;
    }

    public void FocusSearch() => Dispatcher.UIThread.Post(() =>
    {
        SearchBox.Focus();
        if (_vm?.SelectedItem is { } item) ActionsList.ScrollIntoView(item);
    });

    private void HookPreedit()
    {
        var presenter = SearchBox.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        if (ReferenceEquals(presenter, _presenter)) return;
        if (_presenter is not null) _presenter.PropertyChanged -= OnPreeditChanged;
        _presenter = presenter;
        if (_presenter is null) return;
        _presenter.PropertyChanged += OnPreeditChanged;
        UpdatePreedit(_presenter.PreeditText);
    }

    private void OnPreeditChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == TextPresenter.PreeditTextProperty) UpdatePreedit(args.NewValue as string);
    }

    private void UpdatePreedit(string? text)
    {
        _composing = !string.IsNullOrEmpty(text);
        _imeGuard.OnPreeditChanged(text);
    }

    private void OnPaletteKeyDown(object? sender, KeyEventArgs args)
    {
        if (_vm is null || args.KeyModifiers != KeyModifiers.None) return;
        if (args.Key == Key.Enter && _imeGuard.ShouldSwallowEnter()) { args.Handled = true; return; }
        if (_composing) return;
        switch (args.Key)
        {
            case Key.Up: _vm.MoveSelection(-1); break;
            case Key.Down: _vm.MoveSelection(1); break;
            case Key.Enter: _vm.ExecuteSelectedCommand.Execute(null); break;
            case Key.Escape: _vm.CloseCommand.Execute(null); break;
            default: return;
        }
        args.Handled = true;
        if (_vm.SelectedItem is { } item) ActionsList.ScrollIntoView(item);
    }
}
