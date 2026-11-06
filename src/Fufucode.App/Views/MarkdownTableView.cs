using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.VisualTree;

namespace Fufucode.App.Views;

/// <summary>表格的有限高度视口及右上角放大入口。</summary>
public sealed class MarkdownTableView : ContentControl
{
    internal const double InlineMaxHeight = 360;
    private readonly ScrollViewer _scroll;
    private readonly Button _expand;
    private readonly Grid _layout;
    private TableLightboxOverlay? _preview;

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
        AutomationProperties.SetName(_expand, "放大表格");
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
        if (_preview is { } existing) { existing.Focus(); return; }
        var view = this.FindAncestorOfType<RichMarkdownView>();
        _preview = TableLightboxOverlay.Show(this, Markdown, view?.ImageBasePath);
        if (_preview is not null) _preview.Closed += (_, _) => { _preview = null; _expand.Focus(); };
    }
}
