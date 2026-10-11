using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
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

public partial class TerminalDockView : UserControl
{
    private readonly ImeEnterGuard _imeGuard = new();
    private TerminalDockViewModel? _dock;
    private TerminalTabViewModel? _tab;
    private bool _preeditHooked;
    private bool _followOutput = true;
    private bool _restoringScroll;
    private long _scrollGeneration;
    private bool _confirmingClose;
    internal Func<TerminalTabViewModel, Task<bool>>? ConfirmCloseForTesting { get; set; }

    private async void CloseTerminal_Click(object? sender, RoutedEventArgs e) => await CloseSelectedAsync();
    internal async Task CloseSelectedAsync()
    {
        if (_confirmingClose || _dock?.SelectedTab is not { } tab) return;
        var dock = _dock;
        _confirmingClose = true;
        try
        {
            if (tab.IsRunning && !await ConfirmCloseAsync(tab)) return;
            dock.CloseTerminal(tab);
        }
        finally { _confirmingClose = false; }
    }

    private Task<bool> ConfirmCloseAsync(TerminalTabViewModel tab)
    {
        if (ConfirmCloseForTesting is { } test) return test(tab);
        if (TopLevel.GetTopLevel(this) is not Window owner) return Task.FromResult(false);
        var cancel = new Button { Content = "取消", MinWidth = 80 };
        var close = new Button { Content = "结束并关闭", MinWidth = 100 };
        var dialog = new Window { Title = "关闭终端", Width = 400, Height = 190, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children =
            {
                new TextBlock { Text = $"关闭 {tab.Title} 将结束该终端及其运行进程。", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, close } }
            } } };
        cancel.Click += (_, _) => dialog.Close(false);
        close.Click += (_, _) => dialog.Close(true);
        dialog.Opened += (_, _) => cancel.Focus();
        return dialog.ShowDialog<bool>(owner);
    }

    public TerminalDockView()
    {
        InitializeComponent();
        TerminalInput.AddHandler(InputElement.KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        TerminalInput.TemplateApplied += (_, _) => HookPreedit();
        Loaded += (_, _) => { HookPreedit(); ResizeTerminal(); };
        SizeChanged += (_, _) => ResizeTerminal();
        OutputScroller.SizeChanged += (_, _) => ResizeTerminal();
        DataContextChanged += (_, _) => AttachDock();
        AttachedToVisualTree += (_, _) => AttachDock();
        DetachedFromVisualTree += (_, _) => DetachDock();
        OutputScroller.ScrollChanged += (_, _) =>
        {
            if (!_restoringScroll)
            {
                _followOutput = OutputScroller.Extent.Height - OutputScroller.Viewport.Height - OutputScroller.Offset.Y < 36;
                if (_tab is not null) { _tab.FollowOutput = _followOutput; _tab.ScrollOffsetY = OutputScroller.Offset.Y; }
            }
            TerminalSurface.InvalidateVisual();
        };
    }
    private void AttachDock()
    {
        DetachDock();
        _dock = DataContext as TerminalDockViewModel;
        if (_dock is null) return;
        _dock.PropertyChanged += OnDockChanged;
        AttachTab();
    }
    private void DetachDock()
    {
        _scrollGeneration++;
        if (_tab is not null && !_restoringScroll)
        {
            _tab.ScrollOffsetY = OutputScroller.Offset.Y;
            _tab.FollowOutput = _followOutput;
        }
        if (_dock is not null) _dock.PropertyChanged -= OnDockChanged;
        if (_tab is not null) _tab.Buffer.Changed -= OnOutputChanged;
        _dock = null; _tab = null;
    }
    private void OnDockChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalDockViewModel.SelectedTab)) AttachTab();
        if (e.PropertyName is nameof(TerminalDockViewModel.SelectedTab) or nameof(TerminalDockViewModel.IsVisible)) Dispatcher.UIThread.Post(ResizeTerminal, DispatcherPriority.Loaded);
    }
    private void AttachTab()
    {
        if (_tab is not null)
        {
            if (!_restoringScroll)
            {
                _tab.ScrollOffsetY = OutputScroller.Offset.Y;
                _tab.FollowOutput = _followOutput;
            }
            _tab.Buffer.Changed -= OnOutputChanged;
        }
        _tab = _dock?.SelectedTab;
        if (_tab is not null) _tab.Buffer.Changed += OnOutputChanged;
        _followOutput = _tab?.FollowOutput ?? true;
        _restoringScroll = true;
        var targetTab = _tab;
        var generation = ++_scrollGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _scrollGeneration || !ReferenceEquals(targetTab, _tab)) return;
            ResizeTerminal();
            if (targetTab?.FollowOutput == true) OutputScroller.ScrollToEnd();
            else OutputScroller.Offset = new Vector(0, targetTab?.ScrollOffsetY ?? 0);
            _restoringScroll = false;
        }, DispatcherPriority.Loaded);
    }
    private void OnOutputChanged()
    {
        var targetTab = _tab;
        var generation = _scrollGeneration;
        if (targetTab?.FollowOutput != true) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation == _scrollGeneration && ReferenceEquals(targetTab, _tab) && targetTab.FollowOutput) OutputScroller.ScrollToEnd();
        }, DispatcherPriority.Loaded);
    }
    private void ResizeTerminal()
    {
        if (_dock?.IsVisible != true || Bounds.Width < 100 || Bounds.Height < 100) return;
        _dock.SelectedTab?.Resize((int)Math.Max(10, OutputScroller.Viewport.Width / TerminalScreenControl.CellWidth), (int)Math.Max(3, OutputScroller.Viewport.Height / TerminalScreenControl.CellHeight));
    }
    private void HookPreedit()
    {
        if (_preeditHooked) return;
        var presenter = TerminalInput.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        if (presenter is null) return;
        _preeditHooked = true;
        _imeGuard.OnPreeditChanged(presenter.PreeditText);
        presenter.PropertyChanged += (_, e) => { if (e.Property == TextPresenter.PreeditTextProperty) _imeGuard.OnPreeditChanged(e.NewValue as string); };
    }
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (!_imeGuard.ShouldSwallowEnter()) _dock?.SelectedTab?.SendInput();
        }
        else if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control) && string.IsNullOrEmpty(TerminalInput.SelectedText))
        {
            _dock?.SelectedTab?.Interrupt(); e.Handled = true;
        }
    }
}
