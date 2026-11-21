using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;

namespace Fufucode.App.Features.Graph;

/// <summary>A Graph command. Higher priority stays visible longest when the bar narrows;
/// secondary commands always live in the overflow menu. New AutoDev capabilities add entries here.</summary>
internal sealed record GraphCommand(string Id, string Icon, string Label, Action Execute,
    string? EnabledPath = null, int Priority = 0);

/// <summary>Single-row page header: scope breadcrumb on the left, a command bar with
/// WinUI-style dynamic overflow on the right.</summary>
internal sealed class GraphToolbar : Border
{
    protected override Type StyleKeyOverride => typeof(Border);
    private sealed record Slot(GraphCommand Command, Button Button, TextBlock Label, MenuItem Overflow);
    private readonly List<Slot> _slots = new();
    private readonly Control _context;
    private readonly Button _more;
    private readonly Separator _overflowSeparator = new();
    private bool _arranging;

    internal GraphToolbar(GraphWorkspaceViewModel vm)
    {
        Classes.Add("graphHeader");
        AutomationProperties.SetAutomationId(this, "GraphToolbar");
        var primary = new[]
        {
            new GraphCommand("GraphShowProperties", "Info", "属性",
                () => vm.RevealPanel(GraphWorkspaceViewModel.PropertiesPageId), nameof(vm.HasSelectedModule), 4),
            new GraphCommand("GraphExpandSelected", "Branch", "展开", vm.ExpandSelected, nameof(vm.HasSelectedModule), 3),
            new GraphCommand("GraphSelectedChat", "Chat", "会话", () => vm.OpenChat(), nameof(vm.HasSelectedModule), 2),
            new GraphCommand("GraphToolbarNewModule", "Add", "新建模块", () => vm.BeginModule(), nameof(vm.CanEdit), 1),
        };
        var secondary = new[]
        {
            new GraphCommand("GraphExpandAll", "Branch", "展开整图", vm.ExpandAll),
            new GraphCommand("GraphBottom", "ArrowDown", "展开到最底层", vm.GoBottom),
            new GraphCommand("GraphToggleList", "AppsList", "列表视图", () => vm.IsListView = !vm.IsListView),
        };

        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var menu = new MenuFlyout();
        foreach (var command in primary)
        {
            var button = GraphUi.Command(command.Icon, command.Label, command.Execute, command.Id, command.Label, command.EnabledPath);
            var label = ((StackPanel)button.Content!).Children.OfType<TextBlock>().Single();
            var item = MenuItemFor(command);
            item.IsVisible = false;
            _slots.Add(new(command, button, label, item));
            commands.Children.Add(button);
            menu.Items.Add(item);
        }
        _overflowSeparator.IsVisible = false;
        menu.Items.Add(_overflowSeparator);
        foreach (var command in secondary)
        {
            var item = MenuItemFor(command);
            if (command.Id == "GraphToggleList")
                GraphUi.Bind(item, MenuItem.HeaderProperty, nameof(vm.IsListView),
                    converter: new FuncValueConverter<bool, string>(list => list ? "图视图" : "列表视图"));
            menu.Items.Add(item);
        }
        _more = GraphUi.Command("More", null, () => { }, "GraphMoreViews", "更多");
        _more.Flyout = menu;
        _more.VerticalAlignment = VerticalAlignment.Center;

        _context = Context(vm);
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 4 };
        layout.Children.Add(_context);
        Grid.SetColumn(commands, 1); layout.Children.Add(commands);
        Grid.SetColumn(_more, 2); layout.Children.Add(_more);
        Child = layout;
        SizeChanged += (_, _) => ArrangeCommands();
        AttachedToVisualTree += (_, _) => ArrangeCommands();
    }

    private static MenuItem MenuItemFor(GraphCommand command)
    {
        var item = new MenuItem { Header = command.Label, Icon = GraphUi.Icon(command.Icon) };
        AutomationProperties.SetAutomationId(item, command.Id + (command.Priority > 0 ? "Menu" : ""));
        if (command.EnabledPath != null) GraphUi.Bind(item, IsEnabledProperty, command.EnabledPath);
        item.Click += (_, _) => command.Execute();
        return item;
    }

    private static Control Context(GraphWorkspaceViewModel vm)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
        row.Children.Add(new GraphBreadcrumb(vm));
        var version = GraphUi.Version(vm);
        version.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(version);
        return row;
    }

    /// <summary>Labels first, then icon-only, then lowest priority into the overflow menu.</summary>
    private void ArrangeCommands()
    {
        if (_arranging || Bounds.Width < 1) return;
        _arranging = true;
        try
        {
            var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
            _context.Measure(infinite);
            var available = Bounds.Width - Padding.Left - Padding.Right - 40 - Math.Min(_context.DesiredSize.Width, 200);
            double Width(Slot slot, bool label)
            {
                slot.Label.IsVisible = label;
                slot.Button.Measure(infinite);
                return slot.Button.DesiredSize.Width + 2;
            }
            var labelled = _slots.Sum(slot => Width(slot, true));
            var showLabels = labelled <= available;
            var used = 0.0;
            foreach (var slot in _slots.OrderByDescending(s => s.Command.Priority))
            {
                var width = Width(slot, showLabels);
                var fits = used + width <= available;
                if (fits) used += width;
                slot.Button.IsVisible = fits;
                slot.Overflow.IsVisible = !fits;
            }
            _overflowSeparator.IsVisible = _slots.Any(slot => slot.Overflow.IsVisible);
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
            text.MaxWidth = 220;
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
