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

/// <summary>表格的有限高度视口及右上角全屏入口。</summary>
public sealed class MarkdownTableView : ContentControl
{
    internal const double InlineMaxHeight = 360;
    private readonly ScrollViewer _scroll;
    private readonly Button _expand;
    private readonly Grid _layout;
    private TableLightboxWindow? _preview;

    public MarkdownTableView()
    {
        Classes.Add("markdownTable");
        Table = new ResponsiveTablePanel();
        _scroll = new ScrollViewer
        {
            Name = "MarkdownTableScroll",
            Content = Table,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = InlineMaxHeight,
        };
        var icon = new PathIcon { Width = 14, Height = 14 };
        icon.Bind(PathIcon.DataProperty, new DynamicResourceExtension("Icon.FullScreenMaximize"));
        _expand = new Button
        {
            Name = "ExpandMarkdownTable",
            Classes = { "iconButton", "tableExpand" },
            Content = icon,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        AutomationProperties.SetName(_expand, "全屏查看表格");
        _expand.Click += (_, e) => { ShowFullScreen(); e.Handled = true; };
        _layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        Grid.SetRow(_scroll, 1);
        _layout.Children.Add(_expand);
        _layout.Children.Add(_scroll);
        Content = _layout;
        AttachedToVisualTree += (_, _) => UpdatePresentation();
        DetachedFromVisualTree += (_, _) => _preview?.Close();
    }

    public ResponsiveTablePanel Table { get; }
    internal string Markdown { get; set; } = string.Empty;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_scroll is not null && (change.Property == RichMarkdownView.IsStreamingProperty
            || change.Property == RichMarkdownView.IsTableFullScreenProperty)) UpdatePresentation();
    }

    private void UpdatePresentation()
    {
        var streaming = GetValue(RichMarkdownView.IsStreamingProperty);
        var fullScreen = GetValue(RichMarkdownView.IsTableFullScreenProperty);
        Table.IsStreaming = streaming;
        _scroll.HorizontalScrollBarVisibility = streaming ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _scroll.MaxHeight = fullScreen ? double.PositiveInfinity : InlineMaxHeight;
        _scroll.VerticalScrollBarVisibility = fullScreen ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        _expand.IsVisible = !fullScreen;
        if (!streaming) _scroll.Offset = new Vector(0, _scroll.Offset.Y);
    }

    private void ShowFullScreen()
    {
        if (_preview is { } existing) { existing.Activate(); return; }
        var owner = TopLevel.GetTopLevel(this) as Window;
        var view = this.FindAncestorOfType<RichMarkdownView>();
        _preview = new TableLightboxWindow(Markdown, view?.ImageBasePath);
        if (owner is not null)
        {
            _preview.RequestedThemeVariant = owner.ActualThemeVariant;
            _preview.FontFamily = owner.FontFamily;
            foreach (var resource in owner.Resources) _preview.Resources[resource.Key] = resource.Value;
        }
        _preview.Closed += (_, _) => { _preview = null; _expand.Focus(); };
        if (owner is not null) _preview.Show(owner);
        else _preview.Show();
    }
}

public sealed class TableLightboxWindow : Window
{
    public TableLightboxWindow(string markdown, string? imageBasePath = null)
    {
        Name = "TableLightboxWindow";
        Title = "表格";
        Width = 1100;
        Height = 800;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowState = WindowState.FullScreen;
        Bind(BackgroundProperty, new DynamicResourceExtension("AppWindowBackground"));
        var closeIcon = new PathIcon { Width = 14, Height = 14 };
        closeIcon.Bind(PathIcon.DataProperty, new DynamicResourceExtension("Icon.Dismiss"));
        var close = new Button
        {
            Name = "CloseTableLightbox", Content = closeIcon,
            Classes = { "iconButton", "tableExpand" },
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        AutomationProperties.SetName(close, "关闭表格");
        close.Click += (_, _) => Close();
        var content = new RichMarkdownView
        {
            Name = "FullScreenTable", Markdown = markdown, ImageBasePath = imageBasePath,
            Margin = new Thickness(16, 0, 16, 16),
        };
        content.SetValue(RichMarkdownView.IsTableFullScreenProperty, true);
        var scroll = new ScrollViewer
        {
            Name = "FullScreenTableScroll", Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(close);
        layout.Children.Add(scroll);
        Content = layout;
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }
}
