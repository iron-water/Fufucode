using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.VisualTree;

namespace Fufucode.App.Views;

/// <summary>在所属窗口原页面上覆盖半透明遮罩，关闭时保留正文与滚动位置。</summary>
public sealed class TableLightboxOverlay : Grid
{
    private static readonly AttachedProperty<TableLightboxOverlay?> ActiveProperty =
        AvaloniaProperty.RegisterAttached<TableLightboxOverlay, TopLevel, TableLightboxOverlay?>("ActiveTableLightbox");

    private readonly TopLevel _owner;
    private readonly OverlayLayer _layer;
    private readonly Control? _returnFocus;
    private readonly Button _close;
    private bool _closed;

    private TableLightboxOverlay(TopLevel owner, OverlayLayer layer, string markdown, string? imageBasePath)
    {
        _owner = owner;
        _layer = layer;
        _returnFocus = owner.FocusManager?.GetFocusedElement() as Control;
        Name = "TableLightboxOverlay";
        Focusable = true;
        ZIndex = int.MaxValue;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);

        var scrim = new Border { Name = "TableLightboxScrim", Classes = { "tableLightboxScrim" } };
        scrim.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(scrim).Properties.IsLeftButtonPressed) return;
            Close();
            e.Handled = true;
        };
        Children.Add(scrim);
        var closeIcon = new PathIcon { Width = 14, Height = 14 };
        closeIcon.Bind(PathIcon.DataProperty, new DynamicResourceExtension("Icon.Dismiss"));
        closeIcon.Bind(PathIcon.ForegroundProperty, new DynamicResourceExtension("MsRed"));
        _close = new Button
        {
            Name = "CloseTableLightbox", Content = closeIcon,
            Classes = { "iconButton", "tableExpand" },
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        AutomationProperties.SetName(_close, "关闭表格");
        _close.Click += (_, _) => Close();
        var content = new RichMarkdownView { Name = "FullScreenTable", Markdown = markdown, ImageBasePath = imageBasePath };
        content.SetValue(RichMarkdownView.IsTableFullScreenProperty, true);
        var scroll = new ScrollViewer
        {
            Name = "FullScreenTableScroll", Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(_close);
        layout.Children.Add(scroll);
        Children.Add(new Border { Classes = { "tableLightboxSurface" }, Child = layout });
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble);
        DetachedFromVisualTree += (_, _) => Close();
    }

    internal event EventHandler? Closed;

    internal static bool IsOpen(TopLevel owner) => owner.GetValue(ActiveProperty) is not null;

    internal static TableLightboxOverlay? Show(Control anchor, string markdown, string? imageBasePath)
    {
        if (TopLevel.GetTopLevel(anchor) is not { } owner || OverlayLayer.GetOverlayLayer(anchor) is not { } layer) return null;
        owner.GetValue(ActiveProperty)?.Close();
        var overlay = new TableLightboxOverlay(owner, layer, markdown, imageBasePath);
        owner.SetValue(ActiveProperty, overlay);
        overlay.Resize();
        owner.SizeChanged += overlay.OwnerSizeChanged;
        owner.AddHandler(GotFocusEvent, overlay.OwnerGotFocus);
        layer.Children.Add(overlay);
        overlay._close.Focus();
        return overlay;
    }

    internal void Close()
    {
        if (_closed) return;
        _closed = true;
        _owner.SizeChanged -= OwnerSizeChanged;
        _owner.RemoveHandler(GotFocusEvent, OwnerGotFocus);
        if (ReferenceEquals(_owner.GetValue(ActiveProperty), this)) _owner.ClearValue(ActiveProperty);
        _layer.Children.Remove(this);
        if (_returnFocus?.IsEffectivelyVisible == true) _returnFocus.Focus();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void OwnerSizeChanged(object? sender, SizeChangedEventArgs e) => Resize();

    private void Resize()
    {
        Width = _owner.ClientSize.Width;
        Height = _owner.ClientSize.Height;
    }

    private void OwnerGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (!_closed && e.Source is Visual visual && !ReferenceEquals(visual, this) && !this.IsVisualAncestorOf(visual))
            _close.Focus();
    }
}
