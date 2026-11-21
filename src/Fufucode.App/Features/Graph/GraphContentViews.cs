using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Fufucode.App.Abstractions.Graph;

namespace Fufucode.App.Features.Graph;

/// <summary>Dock content only. The existing workbench owns headers, tabs and panel placement.</summary>
public abstract class GraphContentView : UserControl, IDisposable
{
    protected GraphWorkspaceViewModel Workspace { get; }
    private bool _subscribed, _disposed;
    protected GraphContentView(GraphWorkspaceViewModel vm)
    {
        Workspace = vm ?? throw new ArgumentNullException(nameof(vm));
        DataContext = vm;
        Bind(BackgroundProperty, new DynamicResourceExtension("AppMicaPageBackground"));
        AttachedToVisualTree += (_, _) =>
        {
            if (_disposed || _subscribed) return;
            _subscribed = true;
            Workspace.PropertyChanged += WorkspaceChanged;
            OnWorkspaceChanged(null);
        };
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }
    private void WorkspaceChanged(object? sender, PropertyChangedEventArgs e) => OnWorkspaceChanged(e.PropertyName);
    protected virtual void OnWorkspaceChanged(string? name) { }
    private void Unsubscribe()
    {
        if (!_subscribed) return;
        Workspace.PropertyChanged -= WorkspaceChanged; _subscribed = false;
    }
    public virtual void Dispose() { if (_disposed) return; _disposed = true; Unsubscribe(); }
}

internal static class GraphUi
{
    internal static readonly FuncValueConverter<bool, bool> Not = new(value => !value);
    internal static readonly FuncValueConverter<string?, bool> HasText = new(value => !string.IsNullOrWhiteSpace(value));
    internal sealed class SelectionIdConverter(bool root = false) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value ?? (root ? "" : null);
        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value == null ? BindingOperations.DoNothing : root && value is "" ? null : value;
    }
    internal static void Bind(AvaloniaObject control, AvaloniaProperty property, string path,
        BindingMode mode = BindingMode.OneWay, object? source = null, IValueConverter? converter = null)
    {
        if (path.StartsWith('!')) { path = path[1..]; converter = Not; }
        var binding = new Binding(path) { Mode = mode, Converter = converter };
        if (source != null) binding.Source = source;
        control.Bind(property, binding);
    }
    internal static TextBlock Text(string text = "", string style = "body")
    {
        // Colour comes from the type-ramp class or inheritance, never a local value, so state styles
        // (disabled, danger, accent, current crumb) can apply.
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.Classes.Add(style);
        return block;
    }
    internal static TextBlock Value(string path, string style = "body")
    { var block = Text("", style); Bind(block, TextBlock.TextProperty, path); return block; }
    internal static TextBlock SingleLine(string text, string style)
    {
        var block = Text(text, style);
        block.TextWrapping = TextWrapping.NoWrap;
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        return block;
    }
    internal static StackPanel Stack(params Control[] children)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
    /// <summary>A titled block inside <see cref="Sections"/>; spacing comes from the shared style.</summary>
    internal static StackPanel Section(Control header, params Control[] children)
    {
        var panel = new StackPanel();
        panel.Classes.Add("graphSection");
        panel.Children.Add(header);
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
    internal static StackPanel Section(string title, params Control[] children) => Section(Text(title, "bodyStrong"), children);
    internal static StackPanel Sections(params Control[] sections)
    {
        var panel = new StackPanel();
        panel.Classes.Add("graphSections");
        foreach (var section in sections) panel.Children.Add(section);
        return panel;
    }
    /// <summary>A header row with trailing commands, e.g. a section title and its add button.</summary>
    internal static Grid HeaderRow(Control title, params Control[] trailing)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        title.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(title);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var control in trailing) actions.Children.Add(control);
        Grid.SetColumn(actions, 1); row.Children.Add(actions);
        return row;
    }
    internal static WrapPanel Actions(params Control[] controls)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in controls) { control.Margin = new Thickness(0, 0, 8, 8); panel.Children.Add(control); }
        return panel;
    }
    internal static PathIcon Icon(string name, double size = 16)
    {
        var icon = new PathIcon { Width = size, Height = size };
        icon.Bind(PathIcon.DataProperty, new DynamicResourceExtension("Icon." + name));
        return icon;
    }
    internal static Button Button(string title, Action action, string? id = null, bool edit = false)
    {
        var button = new Button { Content = title, MinHeight = 32 };
        button.Classes.Add("subtle");
        button.Click += (_, _) => action();
        AutomationProperties.SetName(button, title);
        if (id != null) AutomationProperties.SetAutomationId(button, id);
        if (edit) Bind(button, InputElement.IsEnabledProperty, nameof(GraphWorkspaceViewModel.CanEdit));
        return button;
    }
    /// <summary>The single primary action of a section uses the accent button.</summary>
    internal static Button Primary(string title, Action action, string? id = null, bool edit = false)
    {
        var button = Button(title, action, id, edit);
        button.Classes.Remove("subtle"); button.Classes.Add("accent");
        return button;
    }
    internal static Button AsyncButton(string title, Func<Task<bool>> action, string id, bool edit = true)
    {
        var button = Button(title, () => { }, id, edit);
        button.Click += async (_, _) => await action();
        return button;
    }
    /// <summary>Command-bar button: icon, optional visible label, tooltip with the full name.</summary>
    internal static Button Command(string icon, string? label, Action action, string id, string? name = null, string? enabledPath = null,
        object? source = null)
    {
        var button = Button(label ?? name ?? "", action, id);
        button.Classes.Add("graphCommand");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(Icon(icon));
        if (label != null) { var text = Text(label); text.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(text); }
        button.Content = row;
        ToolTip.SetTip(button, name ?? label);
        if (enabledPath != null) Bind(button, InputElement.IsEnabledProperty, enabledPath, source: source);
        return button;
    }
    internal static Border Separator()
    {
        var line = new Border(); line.Classes.Add("graphCommandSeparator"); return line;
    }
    internal static TextBox Input(string path, string id, bool multiline = false)
    {
        var box = new TextBox { AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 76 : 32, HorizontalAlignment = HorizontalAlignment.Stretch };
        box.Classes.Add("dialogInput");
        AutomationProperties.SetAutomationId(box, id);
        Bind(box, TextBox.TextProperty, path, BindingMode.TwoWay);
        return box;
    }
    internal static Control Field(string title, Control input)
    {
        AutomationProperties.SetName(input, title);
        var field = Stack(Text(title, "caption"), input);
        field.Spacing = 4;
        return field;
    }
    /// <summary>InfoBar for the workspace's last failed operation.</summary>
    internal static Control Error(GraphWorkspaceViewModel vm)
    {
        var icon = Icon("ErrorCircle");
        icon.Classes.Add("graphStatusIcon");
        icon.VerticalAlignment = VerticalAlignment.Center;
        var text = Value(nameof(vm.Error));
        text.VerticalAlignment = VerticalAlignment.Center;
        var dismiss = Command("Dismiss", null, vm.DismissError, "GraphDismissError", "关闭");
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        row.Children.Add(icon);
        Grid.SetColumn(text, 1); row.Children.Add(text);
        Grid.SetColumn(dismiss, 2); row.Children.Add(dismiss);
        var bar = new Border { Child = row };
        bar.Classes.Add("graphInfoBar");
        AutomationProperties.SetAutomationId(bar, "GraphError");
        Bind(bar, Visual.IsVisibleProperty, nameof(vm.Error), converter: HasText);
        return bar;
    }
    /// <summary>Read-only marker for historical snapshots, with the way back to the present.</summary>
    internal static Control Version(GraphWorkspaceViewModel vm)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var icon = Icon("History", 12); icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);
        var label = Value(nameof(vm.VersionLabel), "caption"); label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        var chip = new Border { Child = row, HorizontalAlignment = HorizontalAlignment.Left };
        chip.Classes.Add("chip");
        var present = Button("回到现在", () => vm.ShowRevision(null), "GraphReturnPresent");
        var version = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        version.Children.Add(chip); version.Children.Add(present);
        Bind(version, Visual.IsVisibleProperty, nameof(vm.IsHistorical));
        return version;
    }
    internal static Control Scroll(Control child) => new ScrollViewer
    { Content = child, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    internal static ComboBox ModuleCombo(string selectedIdPath, bool parent = false)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32 };
        Bind(combo, ItemsControl.ItemsSourceProperty, parent ? "ParentOptions" : "ModuleOptions");
        combo.SelectedValueBinding = new Binding(nameof(GraphModuleOption.Id));
        Bind(combo, SelectingItemsControl.SelectedValueProperty, selectedIdPath, BindingMode.TwoWay,
            converter: new SelectionIdConverter(parent));
        return combo;
    }
    internal static ComboBox KindCombo(string path)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedValueBinding = new Binding(nameof(GraphKindOption.Id)) };
        Bind(combo, ItemsControl.ItemsSourceProperty, "AvailableRelationKinds");
        Bind(combo, SelectingItemsControl.SelectedValueProperty, path, BindingMode.TwoWay,
            converter: new SelectionIdConverter());
        return combo;
    }
    internal static Expander RawRecord(string text, string? reference = null)
    {
        var raw = new Expander { Header = "原始记录", HorizontalAlignment = HorizontalAlignment.Stretch };
        raw.PropertyChanged += (_, e) =>
        {
            if (e.Property != Expander.IsExpandedProperty || !raw.IsExpanded || raw.Content != null) return;
            var value = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                MaxHeight = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
            value.Classes.Add("dialogInput"); AutomationProperties.SetName(value, "原始记录");
            raw.Content = string.IsNullOrWhiteSpace(reference) ? value : Stack(value, Text(reference, "caption"));
        };
        return raw;
    }
    internal static Control Entry(GraphEntry entry)
    {
        var summary = GraphEntryPresentation.Summary(entry);
        var elements = Stack(Text(GraphLabels.Actor(entry.Actor) + " · " + GraphLabels.EntryKind(entry.Kind) + " · " +
            GraphLabels.Time(entry.CreatedUtc), "caption"), Text(summary));
        elements.Spacing = 4;
        if (!string.IsNullOrWhiteSpace(entry.Reference))
        {
            var reference = new TextBox { Text = entry.Reference, IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
            reference.Classes.Add("dialogInput"); AutomationProperties.SetName(reference, "引用");
            elements.Children.Add(reference);
        }
        if (GraphEntryPresentation.IsStructured(entry.Text) || summary != entry.Text)
            elements.Children.Add(RawRecord(entry.Text));
        elements.Children.Add(Divider()); elements.Margin = new Thickness(0, 4);
        return elements;
    }
    internal static Control Busy()
    {
        var bar = new ProgressBar { IsIndeterminate = true, Height = 2, MinHeight = 2 };
        Bind(bar, Visual.IsVisibleProperty, "IsBusy");
        return bar;
    }
    internal static Control Divider()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 4) };
        line.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppDividerSubtle")); return line;
    }
    internal static Button ModuleButton(GraphWorkspaceViewModel vm, GraphModule module, bool mini = false)
    {
        var text = Stack(SingleLine(module.Name, "bodyStrong"), SingleLine(GraphLabels.ModuleSummary(module.Summary), "caption"));
        text.Spacing = 2;
        var button = Button("", () => vm.SelectModule(module.Id));
        button.Content = text; button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Padding = new Thickness(12, 8);
        AutomationProperties.SetName(button, module.Name);
        AutomationProperties.SetAutomationId(button, "GraphModule-" + module.Id);
        button.DoubleTapped += (_, e) => { vm.DrillInto(module.Id); e.Handled = true; };
        return button;
    }
}

/// <summary>Floating canvas controls: hierarchy depth and zoom, with live read-outs.</summary>
internal sealed class GraphViewControls : Border
{
    protected override Type StyleKeyOverride => typeof(Border);
    private readonly GraphViewportState _viewport;
    private readonly TextBlock _zoom;
    internal GraphViewControls(GraphWorkspaceViewModel vm, GraphCanvas canvas, bool mini)
    {
        Classes.Add("graphFloat");
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        AutomationProperties.SetAutomationId(this, mini ? "GraphMiniViewControls" : "GraphViewControls");
        _viewport = mini ? vm.MiniMapViewport : vm.MainViewport;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        if (!mini)
        {
            row.Children.Add(GraphUi.Command("ChevronUp", null, vm.GoUp, "GraphUp", "上一层", nameof(vm.CanGoUp)));
            var depth = GraphUi.Value(nameof(vm.Depth), "graphReadout");
            depth.Bind(TextBlock.TextProperty, new Binding(nameof(vm.Depth)) { StringFormat = "深度 {0}" });
            AutomationProperties.SetAutomationId(depth, "GraphDepth");
            row.Children.Add(depth);
            row.Children.Add(GraphUi.Command("ChevronDown", null, vm.GoDeeper, "GraphDeeper", "下一层", nameof(vm.CanGoDeeper)));
            row.Children.Add(GraphUi.Separator());
        }
        row.Children.Add(GraphUi.Command("ZoomOut", null, () => canvas.Zoom(1 / 1.2), mini ? "GraphMiniZoomOut" : "GraphZoomOut", "缩小"));
        _zoom = GraphUi.Text("", "graphReadout");
        _zoom.IsVisible = !mini;
        AutomationProperties.SetAutomationId(_zoom, "GraphZoomLevel");
        row.Children.Add(_zoom);
        row.Children.Add(GraphUi.Command("ZoomIn", null, () => canvas.Zoom(1.2), mini ? "GraphMiniZoomIn" : "GraphZoomIn", "放大"));
        row.Children.Add(GraphUi.Command("FullScreenMaximize", null, canvas.Fit, mini ? "GraphMiniFit" : "GraphFit", "适应"));
        if (mini)
        {
            row.Children.Add(GraphUi.Separator());
            row.Children.Add(GraphUi.Command("Open", null, () => vm.RevealPanel(GraphWorkspaceViewModel.PageId), "GraphMiniOpenMain", "在主图打开"));
        }
        Child = row;
        AttachedToVisualTree += (_, _) => { _viewport.PropertyChanged += ViewportChanged; UpdateZoom(); };
        DetachedFromVisualTree += (_, _) => _viewport.PropertyChanged -= ViewportChanged;
    }
    private void ViewportChanged(object? sender, PropertyChangedEventArgs e) => UpdateZoom();
    private void UpdateZoom() => _zoom.Text = Math.Round(_viewport.Scale * 100).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
}

public sealed class GraphPageView : GraphContentView
{
    private readonly GraphCanvas _canvas;
    public GraphPageView(GraphWorkspaceViewModel vm) : base(vm)
    {
        AutomationProperties.SetAutomationId(this, "GraphPage");
        _canvas = new GraphCanvas(vm);
        var diagram = new Grid();
        GraphUi.Bind(_canvas, IsVisibleProperty, "!IsListView");
        diagram.Children.Add(_canvas);
        var moduleList = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<GraphModule>((module, _) => module == null ? null : GraphUi.ModuleButton(vm, module))
        };
        GraphUi.Bind(moduleList, ItemsControl.ItemsSourceProperty, "VisibleModules");
        var relationList = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<GraphEdge>((edge, _) => edge == null ? null : RelationRow(edge))
        };
        string Name(string id) => vm.Snapshot.Modules.FirstOrDefault(m => m.Id == id)?.Name ?? "尚未建立";
        Control RelationRow(GraphEdge edge)
        {
            var row = GraphUi.Stack(GraphUi.SingleLine(Name(edge.Relation.SourceId) + " → " + Name(edge.Relation.TargetId), "body"),
                GraphUi.SingleLine(edge.Label, "caption"));
            row.Spacing = 2; row.Margin = new Thickness(12, 6);
            return row;
        }
        GraphUi.Bind(relationList, ItemsControl.ItemsSourceProperty, "Scene.Edges");
        var list = GraphUi.Scroll(GraphUi.Sections(GraphUi.Section("模块", moduleList), GraphUi.Section("关系", relationList)));
        GraphUi.Bind(list, IsVisibleProperty, "IsListView"); diagram.Children.Add(list);
        var controls = new GraphViewControls(vm, _canvas, false);
        GraphUi.Bind(controls, IsVisibleProperty, "!IsListView");
        diagram.Children.Add(controls);
        diagram.Children.Add(EmptyState(vm));
        var error = GraphUi.Error(vm);
        error.Margin = new Thickness(12); error.VerticalAlignment = VerticalAlignment.Top;
        diagram.Children.Add(error);
        var busy = GraphUi.Busy(); busy.VerticalAlignment = VerticalAlignment.Top;
        diagram.Children.Add(busy);
        var toolbar = new GraphToolbar(vm);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(toolbar); Grid.SetRow(diagram, 1); root.Children.Add(diagram);
        Content = root;
    }
    private static Control EmptyState(GraphWorkspaceViewModel vm)
    {
        var icon = GraphUi.Icon("Branch", 32);
        icon.Bind(PathIcon.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        var title = GraphUi.Text("暂无模块", "bodyStrong");
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        var scan = GraphUi.AsyncButton("扫描项目", () => vm.ScanAsync(), "GraphEmptyScan");
        scan.Classes.Remove("subtle"); scan.Classes.Add("accent");
        actions.Children.Add(scan);
        actions.Children.Add(GraphUi.Button("新建模块", () => vm.BeginModule(), "GraphEmptyNewModule", true));
        var empty = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        empty.Children.Add(icon); empty.Children.Add(title); empty.Children.Add(actions);
        AutomationProperties.SetAutomationId(empty, "GraphEmpty");
        GraphUi.Bind(empty, IsVisibleProperty, "Modules.Count", converter: new FuncValueConverter<int, bool>(count => count == 0));
        return empty;
    }
    public override void Dispose() { _canvas.Dispose(); base.Dispose(); }
}

public sealed class GraphMiniMapView : GraphContentView
{
    private readonly GraphCanvas _canvas;
    public GraphMiniMapView(GraphWorkspaceViewModel vm) : base(vm)
    {
        AutomationProperties.SetAutomationId(this, "GraphMiniMap");
        _canvas = new GraphCanvas(vm, true);
        var controls = new GraphViewControls(vm, _canvas, true);
        controls.Margin = new Thickness(8);
        var root = new Grid();
        root.Children.Add(_canvas); root.Children.Add(controls); Content = root;
    }
    public override void Dispose() { _canvas.Dispose(); base.Dispose(); }
}

public sealed class GraphHistoryView : GraphContentView
{
    private readonly Slider _slider = new() { Minimum = 0, TickFrequency = 1, IsSnapToTickEnabled = true };
    private bool _syncing;
    public GraphHistoryView(GraphWorkspaceViewModel vm) : base(vm)
    {
        AutomationProperties.SetAutomationId(this, "GraphHistory");
        AutomationProperties.SetName(_slider, "历史版本");
        _slider.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            var index = (int)Math.Round(_slider.Value);
            vm.ShowRevision(index >= vm.Revisions.Count ? null : vm.Revisions[index].Version);
        };
        var revisions = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<GraphRevision>((revision, _) => revision == null ? null : RevisionRow(vm, revision))
        };
        // Newest first, so "now" leads the list; the slider keeps the chronological order.
        GraphUi.Bind(revisions, ItemsControl.ItemsSourceProperty, "Revisions", converter:
            new FuncValueConverter<IEnumerable<GraphRevision>, IEnumerable<GraphRevision>>(values =>
                values?.OrderByDescending(r => r.Version).ToArray() ?? Array.Empty<GraphRevision>()));
        var entries = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<GraphEntry>((entry, _) => entry == null ? null : GraphUi.Entry(entry))
        };
        GraphUi.Bind(entries, ItemsControl.ItemsSourceProperty, "Entries", converter:
            new FuncValueConverter<IEnumerable<GraphEntry>, IEnumerable<GraphEntry>>(values => values?.Where(e => !GraphEntryPresentation.IsSource(e)).ToArray() ?? Array.Empty<GraphEntry>()));
        var sources = new Expander { Header = "原始记录", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(sources, "GraphHistoryRawSources");
        sources.PropertyChanged += (_, e) =>
        {
            if (e.Property != Expander.IsExpandedProperty || !sources.IsExpanded || sources.Content != null) return;
            var records = new ItemsControl
            {
                ItemTemplate = new FuncDataTemplate<GraphEntry>((entry, _) => entry == null ? null :
                    GraphUi.Stack(GraphUi.Text(entry.Reference ?? "", "caption"), GraphUi.RawRecord(entry.Text)))
            };
            GraphUi.Bind(records, ItemsControl.ItemsSourceProperty, "Entries", converter:
                new FuncValueConverter<IEnumerable<GraphEntry>, IEnumerable<GraphEntry>>(values => values?.Where(GraphEntryPresentation.IsSource).ToArray() ?? Array.Empty<GraphEntry>()));
            sources.Content = records;
        };
        var navigation = GraphUi.HeaderRow(GraphUi.Value("VersionLabel", "bodyStrong"),
            GraphUi.Command("ChevronLeft", null, () => vm.MoveHistory(-1), "GraphHistoryPrevious", "上一版"),
            GraphUi.Command("ChevronRight", null, () => vm.MoveHistory(1), "GraphHistoryNext", "下一版", nameof(vm.IsHistorical)),
            GraphUi.Button("回到现在", () => vm.ShowRevision(null), "GraphHistoryPresent"));
        var present = navigation.Children.OfType<StackPanel>().Single().Children.OfType<Button>().Last();
        GraphUi.Bind(present, IsEnabledProperty, nameof(vm.IsHistorical));
        var content = GraphUi.Sections(GraphUi.Error(vm),
            GraphUi.Section(navigation, _slider, revisions),
            GraphUi.Section("记录", entries, sources));
        Content = GraphUi.Scroll(content); UpdateSlider();
    }
    private static Control RevisionRow(GraphWorkspaceViewModel vm, GraphRevision revision)
    {
        var title = GraphEntryPresentation.RevisionSummary(revision.Summary);
        var version = GraphUi.Text("v" + revision.Version, "caption");
        version.Classes.Add("numeric"); version.MinWidth = 40; version.VerticalAlignment = VerticalAlignment.Center;
        var summary = GraphUi.SingleLine(title, "body"); summary.VerticalAlignment = VerticalAlignment.Center;
        var time = GraphUi.Text(GraphLabels.Time(revision.CreatedUtc), "caption");
        time.Classes.Add("numeric"); time.VerticalAlignment = VerticalAlignment.Center;
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        line.Children.Add(version);
        Grid.SetColumn(summary, 1); line.Children.Add(summary);
        Grid.SetColumn(time, 2); line.Children.Add(time);
        var button = GraphUi.Button(title, () => vm.ShowRevision(IsLatest(vm, revision) ? null : revision.Version),
            "GraphRevision-" + revision.Version);
        button.Content = line; button.Padding = new Thickness(12, 6);
        button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var pill = new Border(); pill.Classes.Add("navPill");
        var cell = new Grid(); cell.Children.Add(button); cell.Children.Add(pill);
        var row = new Border { Child = cell };
        row.Classes.Add("graphRow");
        // The current revision carries the same selection treatment as list items.
        // "Now" is the newest revision; a historical view marks the revision it shows.
        void Update() => row.Classes.Set("graphRowSelected",
            vm.IsHistorical ? vm.Snapshot.Revision == revision.Version : IsLatest(vm, revision));
        void Changed(object? sender, PropertyChangedEventArgs e) => Update();
        row.AttachedToVisualTree += (_, _) => { vm.PropertyChanged += Changed; Update(); };
        row.DetachedFromVisualTree += (_, _) => vm.PropertyChanged -= Changed;
        return title == revision.Summary ? row : GraphUi.Stack(row, GraphUi.RawRecord(revision.Summary));
    }
    private static bool IsLatest(GraphWorkspaceViewModel vm, GraphRevision revision) =>
        vm.Revisions.Count > 0 && revision.Version == vm.Revisions.Max(r => r.Version);
    protected override void OnWorkspaceChanged(string? name) => UpdateSlider();
    private void UpdateSlider()
    {
        _syncing = true;
        try
        {
            _slider.Maximum = Workspace.Revisions.Count;
            var index = Workspace.IsHistorical ? Workspace.Revisions.ToList().FindIndex(r => r.Version == Workspace.Snapshot.Revision)
                : Workspace.Revisions.Count;
            _slider.Value = index < 0 ? 0 : index;
        }
        finally { _syncing = false; }
    }
}
