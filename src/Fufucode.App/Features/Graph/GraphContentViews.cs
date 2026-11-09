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
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.Classes.Add(style);
        block.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(style == "caption" ? "AppTextSecondary" : "AppTextPrimary"));
        return block;
    }
    internal static TextBlock Value(string path, string style = "body")
    { var block = Text("", style); Bind(block, TextBlock.TextProperty, path); return block; }
    internal static StackPanel Stack(params Control[] children)
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
    internal static WrapPanel Actions(params Control[] controls)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in controls) { control.Margin = new Thickness(0, 0, 6, 6); panel.Children.Add(control); }
        return panel;
    }
    internal static Button Button(string title, Action action, string? id = null, bool edit = false)
    {
        var button = new Button { Content = title, MinHeight = 30 };
        button.Classes.Add("subtle");
        button.Click += (_, _) => action();
        AutomationProperties.SetName(button, title);
        if (id != null) AutomationProperties.SetAutomationId(button, id);
        if (edit) Bind(button, InputElement.IsEnabledProperty, nameof(GraphWorkspaceViewModel.CanEdit));
        return button;
    }
    internal static Button AsyncButton(string title, Func<Task<bool>> action, string id, bool edit = true)
    {
        var button = Button(title, () => { }, id, edit);
        button.Click += async (_, _) => await action();
        return button;
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
        return Stack(Text(title, "caption"), input);
    }
    internal static Control Error(GraphWorkspaceViewModel vm)
    {
        var text = Value(nameof(vm.Error));
        text.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppCriticalForeground"));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        row.Children.Add(text);
        var dismiss = Button("关闭", vm.DismissError);
        Grid.SetColumn(dismiss, 1); row.Children.Add(dismiss);
        Bind(row, Visual.IsVisibleProperty, nameof(vm.Error), converter: HasText);
        return row;
    }
    internal static Control Version(GraphWorkspaceViewModel vm)
    {
        var label = Value(nameof(vm.VersionLabel), "caption");
        Bind(label, Visual.IsVisibleProperty, nameof(vm.IsHistorical));
        return label;
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
        if (!string.IsNullOrWhiteSpace(entry.Reference))
        {
            var reference = new TextBox { Text = entry.Reference, IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
            reference.Classes.Add("dialogInput"); AutomationProperties.SetName(reference, "引用");
            elements.Children.Add(reference);
        }
        if (GraphEntryPresentation.IsStructured(entry.Text) || summary != entry.Text)
            elements.Children.Add(RawRecord(entry.Text));
        elements.Children.Add(Divider()); elements.Margin = new Thickness(0, 6);
        return elements;
    }
    internal static Control Busy()
    {
        var bar = new ProgressBar { IsIndeterminate = true, Height = 2, Margin = new Thickness(0, 2) };
        Bind(bar, Visual.IsVisibleProperty, "IsBusy");
        return bar;
    }
    internal static Control Divider()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 4) };
        line.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppBorderBrush")); return line;
    }
    internal static Button ModuleButton(GraphWorkspaceViewModel vm, GraphModule module, bool mini = false)
    {
        var text = Stack(Text(module.Name, "bodyStrong"), Text(GraphLabels.ModuleSummary(module.Summary), "caption"));
        text.Spacing = 3;
        var button = Button("", () => vm.SelectModule(module.Id));
        button.Content = text; button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(button, module.Name);
        AutomationProperties.SetAutomationId(button, "GraphModule-" + module.Id);
        button.DoubleTapped += (_, e) => { vm.DrillInto(module.Id); e.Handled = true; };
        return button;
    }
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
            ItemTemplate = new FuncDataTemplate<GraphEdge>((edge, _) => edge == null ? null : GraphUi.Text(
                Name(edge.Relation.SourceId) + " → " + Name(edge.Relation.TargetId) + Environment.NewLine + edge.Label, "caption"))
        };
        string Name(string id) => vm.Snapshot.Modules.FirstOrDefault(m => m.Id == id)?.Name ?? "尚未建立";
        GraphUi.Bind(relationList, ItemsControl.ItemsSourceProperty, "Scene.Edges");
        var list = GraphUi.Scroll(GraphUi.Stack(moduleList, GraphUi.Divider(), relationList));
        list.Margin = new Thickness(16);
        GraphUi.Bind(list, IsVisibleProperty, "IsListView"); diagram.Children.Add(list);
        var empty = GraphUi.Text("暂无模块");
        empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center;
        GraphUi.Bind(empty, IsVisibleProperty, "Modules.Count", converter: new FuncValueConverter<int, bool>(count => count == 0));
        empty.IsHitTestVisible = false; diagram.Children.Add(empty);
        var toolbar = new GraphToolbar(vm, _canvas);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(toolbar); Grid.SetRow(diagram, 1); root.Children.Add(diagram);
        Content = root;
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
        var controls = GraphUi.Actions(GraphUi.Button("适应", _canvas.Fit),
            GraphUi.Button("−", () => _canvas.Zoom(1 / 1.2)), GraphUi.Button("+", () => _canvas.Zoom(1.2)),
            GraphUi.Button("在主图打开", () => vm.RevealPanel(GraphWorkspaceViewModel.PageId), "GraphMiniOpenMain"));
        controls.Margin = new Thickness(8, 4);
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(_canvas); Grid.SetRow(controls, 1); root.Children.Add(controls); Content = root;
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
            ItemTemplate = new FuncDataTemplate<GraphRevision>((revision, _) =>
            {
                if (revision == null) return null;
                var title = GraphEntryPresentation.RevisionSummary(revision.Summary);
                var button = GraphUi.Button($"v{revision.Version}  {title}\n{GraphLabels.Time(revision.CreatedUtc)}", () => vm.ShowRevision(revision.Version));
                return title == revision.Summary ? button : GraphUi.Stack(button, GraphUi.RawRecord(revision.Summary));
            })
        };
        GraphUi.Bind(revisions, ItemsControl.ItemsSourceProperty, "Revisions");
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
        var content = GraphUi.Stack(GraphUi.Error(vm), GraphUi.Value("VersionLabel", "bodyStrong"),
            GraphUi.Actions(GraphUi.Button("上一版", () => vm.MoveHistory(-1)),
                GraphUi.Button("下一版", () => vm.MoveHistory(1)), GraphUi.Button("现在", () => vm.ShowRevision(null))),
            _slider, revisions, GraphUi.Divider(), entries, sources);
        content.Margin = new Thickness(12); Content = GraphUi.Scroll(content); UpdateSlider();
    }
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
