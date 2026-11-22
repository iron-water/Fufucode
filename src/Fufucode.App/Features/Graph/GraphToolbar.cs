using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;

namespace Fufucode.App.Features.Graph;

/// <summary>A Graph command, shown as a ribbon button and, when its group collapses, as a menu item.
/// New AutoDev capabilities add commands or whole groups to <see cref="GraphToolbar"/>.</summary>
internal sealed record GraphCommand(string Id, string Icon, string Label, Action Execute, string? EnabledPath = null);

public enum GraphCommandBarPlacement { Top, Bottom }

/// <summary>Where the Graph command bar sits and whether it is hidden. One instance is shared by every
/// project's Graph page; the host persists it.</summary>
public sealed class GraphCommandBarSettings : GraphObservable
{
    private GraphCommandBarPlacement _placement;
    private bool _hidden;
    public GraphCommandBarPlacement Placement { get => _placement; set => Set(ref _placement, value); }
    public bool IsHidden { get => _hidden; set => Set(ref _hidden, value); }
    public string Serialize() => (Placement == GraphCommandBarPlacement.Bottom ? "bottom" : "top") + (IsHidden ? "-hidden" : "");
    public static GraphCommandBarSettings Parse(string? value) => new()
    {
        Placement = value?.StartsWith("bottom", StringComparison.Ordinal) == true ? GraphCommandBarPlacement.Bottom : GraphCommandBarPlacement.Top,
        IsHidden = value?.EndsWith("-hidden", StringComparison.Ordinal) == true
    };
}

/// <summary>Office-style ribbon for the Graph page: captioned groups in one bar. When the bar narrows,
/// the lowest-priority groups collapse into a single dropdown button each.</summary>
internal sealed class GraphToolbar : Border
{
    protected override Type StyleKeyOverride => typeof(Border);
    private sealed record Group(Border Root, Control Expanded, Button Collapsed, TextBlock Caption, int Priority);
    private readonly List<Group> _groups = new();
    private readonly Border _location;
    private readonly Button _options;
    private readonly GraphViewportState _viewport;
    private readonly TextBlock _zoom;
    private bool _arranging;

    internal GraphToolbar(GraphWorkspaceViewModel vm, GraphCanvas canvas, GraphCommandBarSettings settings)
    {
        Classes.Add("graphRibbon");
        AutomationProperties.SetAutomationId(this, "GraphToolbar");
        _viewport = vm.MainViewport;
        var groups = new StackPanel { Orientation = Orientation.Horizontal };

        // 位置: where you are in the hierarchy and in history. It never collapses; crumbs trim instead.
        var depth = GraphUi.Text("", "caption");
        depth.Bind(TextBlock.TextProperty, new Binding(nameof(vm.Depth)) { StringFormat = "深度 {0}" });
        depth.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(depth, "GraphDepth");
        var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 0, 0, 0) };
        state.Children.Add(depth);
        state.Children.Add(GraphUi.Version(vm));
        var place = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        place.Children.Add(new GraphBreadcrumb(vm)); place.Children.Add(state);
        _location = GroupFrame("GraphGroupLocation", "位置", place, out _);
        _location.MaxWidth = 360;
        groups.Children.Add(_location);

        // 模块: actions on the selection, as large buttons.
        var module = new[]
        {
            new GraphCommand("GraphShowProperties", "Info", "属性", () => vm.RevealPanel(GraphWorkspaceViewModel.PropertiesPageId), nameof(vm.HasSelectedModule)),
            new GraphCommand("GraphExpandSelected", "Branch", "展开", vm.ExpandSelected, nameof(vm.HasSelectedModule)),
            new GraphCommand("GraphSelectedChat", "Chat", "会话", () => vm.OpenChat(), nameof(vm.HasSelectedModule)),
            new GraphCommand("GraphToolbarNewModule", "Add", "新建模块", () => vm.BeginModule(), nameof(vm.CanEdit)),
        };
        var large = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var command in module) large.Children.Add(Large(command));
        AddGroup(groups, "GraphGroupModule", "模块", "PuzzlePiece", 3, large, module);

        // 层级: move through the module hierarchy.
        var up = new GraphCommand("GraphUp", "ChevronUp", "上一层", vm.GoUp, nameof(vm.CanGoUp));
        var deeper = new GraphCommand("GraphDeeper", "ChevronDown", "下一层", vm.GoDeeper, nameof(vm.CanGoDeeper));
        var all = new GraphCommand("GraphExpandAll", "Branch", "展开整图", vm.ExpandAll);
        var bottom = new GraphCommand("GraphBottom", "ArrowDown", "最底层", vm.GoBottom);
        AddGroup(groups, "GraphGroupHierarchy", "层级", "Branch", 2,
            Rows(Row(Small(up, true), Small(deeper, true)), Row(Small(all, true), Small(bottom, true))), up, deeper, all, bottom);

        // 视图: zoom with a live read-out, fit, and the list alternative.
        var zoomOut = new GraphCommand("GraphZoomOut", "ZoomOut", "缩小", () => canvas.Zoom(1 / 1.2));
        var zoomIn = new GraphCommand("GraphZoomIn", "ZoomIn", "放大", () => canvas.Zoom(1.2));
        var fit = new GraphCommand("GraphFit", "FullScreenMaximize", "适应", canvas.Fit);
        var list = new GraphCommand("GraphToggleList", "AppsList", "列表视图", () => vm.IsListView = !vm.IsListView);
        _zoom = GraphUi.Text("", "graphReadout");
        AutomationProperties.SetAutomationId(_zoom, "GraphZoomLevel");
        var listButton = Small(list, true);
        var listLabel = ((StackPanel)listButton.Content!).Children.OfType<TextBlock>().Single();
        GraphUi.Bind(listLabel, TextBlock.TextProperty, nameof(vm.IsListView), converter: ListLabel);
        var viewGroup = AddGroup(groups, "GraphGroupView", "视图", "ZoomIn", 1,
            Rows(Row(Small(zoomOut, false), _zoom, Small(zoomIn, false)), Row(Small(fit, true), listButton)), zoomOut, zoomIn, fit, list);
        var listItem = ((MenuFlyout)viewGroup.Collapsed.Flyout!).Items.OfType<MenuItem>().Last();
        GraphUi.Bind(listItem, MenuItem.HeaderProperty, nameof(vm.IsListView), converter: ListLabel);

        _options = GraphUi.Command("More", null, () => { }, "GraphCommandBarOptions", "命令栏选项");
        _options.Classes.Remove("graphCommand");
        _options.Classes.Add("graphSmallCommand");
        _options.VerticalAlignment = VerticalAlignment.Top;
        _options.Flyout = OptionsMenu(settings);

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        layout.Children.Add(groups);
        Grid.SetColumn(_options, 1); layout.Children.Add(_options);
        Child = layout;
        SizeChanged += (_, _) => ArrangeGroups();
        AttachedToVisualTree += (_, _) => { _viewport.PropertyChanged += ViewportChanged; UpdateZoom(); ArrangeGroups(); };
        DetachedFromVisualTree += (_, _) => _viewport.PropertyChanged -= ViewportChanged;
    }

    private static readonly FuncValueConverter<bool, string> ListLabel = new(list => list ? "图视图" : "列表视图");

    internal static MenuFlyout OptionsMenu(GraphCommandBarSettings settings)
    {
        var menu = new MenuFlyout();
        MenuItem Placement(string title, GraphCommandBarPlacement placement, string id)
        {
            var item = new MenuItem { Header = title, ToggleType = MenuItemToggleType.Radio, GroupName = "GraphCommandBarPlacement" };
            AutomationProperties.SetAutomationId(item, id);
            item.Click += (_, _) => { settings.Placement = placement; settings.IsHidden = false; };
            return item;
        }
        var top = Placement("位于顶部", GraphCommandBarPlacement.Top, "GraphCommandBarTop");
        var bottom = Placement("位于底部", GraphCommandBarPlacement.Bottom, "GraphCommandBarBottom");
        var hide = new MenuItem { Header = "隐藏命令栏", Icon = GraphUi.Icon("Dismiss"), InputGesture = new KeyGesture(Key.F1, KeyModifiers.Control) };
        AutomationProperties.SetAutomationId(hide, "GraphCommandBarHide");
        hide.Click += (_, _) => settings.IsHidden = true;
        menu.Opening += (_, _) =>
        {
            top.IsChecked = settings.Placement == GraphCommandBarPlacement.Top;
            bottom.IsChecked = settings.Placement == GraphCommandBarPlacement.Bottom;
        };
        menu.Items.Add(top); menu.Items.Add(bottom); menu.Items.Add(new Separator()); menu.Items.Add(hide);
        return menu;
    }

    private static Border GroupFrame(string id, string title, Control content, out TextBlock caption)
    {
        caption = GraphUi.SingleLine(title, "caption");
        caption.Classes.Add("graphRibbonCaption");
        var body = new Grid { RowDefinitions = new RowDefinitions("52,16") };
        body.Children.Add(content);
        Grid.SetRow(caption, 1); body.Children.Add(caption);
        var frame = new Border { Child = body };
        frame.Classes.Add("graphRibbonGroup");
        AutomationProperties.SetAutomationId(frame, id);
        return frame;
    }

    private Group AddGroup(Panel host, string id, string title, string icon, int priority, Control expanded, params GraphCommand[] commands)
    {
        var menu = new MenuFlyout();
        foreach (var command in commands) menu.Items.Add(MenuItemFor(command));
        var collapsed = Large(new GraphCommand(id + "Collapsed", icon, title, () => { }));
        collapsed.Flyout = menu;
        collapsed.IsVisible = false;
        expanded.VerticalAlignment = VerticalAlignment.Center;
        var slot = new Panel();
        slot.Children.Add(expanded); slot.Children.Add(collapsed);
        var frame = GroupFrame(id, title, slot, out var caption);
        host.Children.Add(frame);
        var group = new Group(frame, expanded, collapsed, caption, priority);
        _groups.Add(group);
        return group;
    }

    private static MenuItem MenuItemFor(GraphCommand command)
    {
        var item = new MenuItem { Header = command.Label, Icon = GraphUi.Icon(command.Icon) };
        AutomationProperties.SetAutomationId(item, command.Id + "Menu");
        if (command.EnabledPath != null) GraphUi.Bind(item, IsEnabledProperty, command.EnabledPath);
        item.Click += (_, _) => command.Execute();
        return item;
    }

    /// <summary>Icon above its label, the ribbon's primary button form.</summary>
    private static Button Large(GraphCommand command)
    {
        var button = GraphUi.Button(command.Label, command.Execute, command.Id);
        button.Classes.Add("graphLargeCommand");
        var stack = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        var icon = GraphUi.Icon(command.Icon, 20);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock { Text = command.Label, HorizontalAlignment = HorizontalAlignment.Center });
        button.Content = stack;
        ToolTip.SetTip(button, command.Label);
        if (command.EnabledPath != null) GraphUi.Bind(button, IsEnabledProperty, command.EnabledPath);
        return button;
    }

    private static Button Small(GraphCommand command, bool label)
    {
        var button = GraphUi.Command(command.Icon, label ? command.Label : null, command.Execute, command.Id, command.Label, command.EnabledPath);
        button.Classes.Remove("graphCommand");
        button.Classes.Add("graphSmallCommand");
        button.ClearValue(MinHeightProperty); // the shared factory's 32px floor would break two stacked rows
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        return button;
    }

    private static StackPanel Row(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var control in controls) row.Children.Add(control);
        return row;
    }

    private static StackPanel Rows(params Control[] rows)
    {
        var stack = new StackPanel();
        foreach (var row in rows) stack.Children.Add(row);
        return stack;
    }

    private void ViewportChanged(object? sender, PropertyChangedEventArgs e) => UpdateZoom();
    private void UpdateZoom() => _zoom.Text = Math.Round(_viewport.Scale * 100).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";

    /// <summary>Collapse groups from the lowest priority until the bar fits; the location group trims last.</summary>
    private void ArrangeGroups()
    {
        if (_arranging || Bounds.Width < 1) return;
        _arranging = true;
        try
        {
            var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
            double Width(Control control) { control.InvalidateMeasure(); control.Measure(infinite); return control.DesiredSize.Width; }
            void Collapse(Group group, bool collapsed)
            {
                group.Expanded.IsVisible = !collapsed;
                group.Collapsed.IsVisible = collapsed;
                group.Caption.IsVisible = !collapsed;
            }
            var available = Bounds.Width - Padding.Left - Padding.Right - Width(_options);
            _location.MaxWidth = 360;
            var location = Math.Min(Width(_location), 360);
            foreach (var group in _groups) Collapse(group, false);
            var used = location + _groups.Sum(group => Width(group.Root));
            foreach (var group in _groups.OrderBy(g => g.Priority))
            {
                if (used <= available) break;
                var before = Width(group.Root);
                Collapse(group, true);
                used -= before - Width(group.Root);
            }
            if (used > available) _location.MaxWidth = Math.Max(96, location - (used - available));
        }
        finally { _arranging = false; }
    }
}

/// <summary>Scope path of the current view. Earlier crumbs navigate; the last is the current place.</summary>
internal sealed class GraphBreadcrumb : StackPanel
{
    private readonly GraphWorkspaceViewModel _vm;
    internal GraphBreadcrumb(GraphWorkspaceViewModel vm)
    {
        _vm = vm;
        Orientation = Orientation.Horizontal;
        Spacing = 2;
        VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(this, "GraphBreadcrumb");
        AttachedToVisualTree += (_, _) => { _vm.PropertyChanged += Changed; Rebuild(); };
        DetachedFromVisualTree += (_, _) => _vm.PropertyChanged -= Changed;
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(GraphWorkspaceViewModel.ScopePath)) Rebuild();
    }
    private void Rebuild()
    {
        Children.Clear();
        var path = _vm.ScopePath;
        for (var i = 0; i < path.Count; i++)
        {
            var crumb = path[i];
            if (i > 0)
            {
                var separator = GraphUi.Icon("ChevronRight", 12);
                separator.Classes.Add("graphCrumbSeparator");
                separator.VerticalAlignment = VerticalAlignment.Center;
                Children.Add(separator);
            }
            var text = GraphUi.SingleLine(crumb.Name, "body");
            text.MaxWidth = 200;
            text.VerticalAlignment = VerticalAlignment.Center;
            var id = i == 0 ? null : crumb.Id;
            var button = GraphUi.Button(crumb.Name, () => _vm.NavigateScope(id), i == 0 ? "GraphCrumbRoot" : "GraphCrumb-" + crumb.Id);
            button.Classes.Add("graphCommand"); button.Classes.Add("graphCrumb");
            button.Content = text;
            if (i == path.Count - 1)
            {
                button.Classes.Add("current");
                button.IsHitTestVisible = false;
                button.Focusable = false;
            }
            Children.Add(button);
        }
    }
}
