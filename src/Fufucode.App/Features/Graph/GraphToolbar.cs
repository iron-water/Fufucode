using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace Fufucode.App.Features.Graph;

/// <summary>Compact graph commands in the existing workbench visual language.</summary>
internal sealed class GraphToolbar : Border
{
    protected override Type StyleKeyOverride => typeof(Border);
    private readonly List<TextBlock> _labels = new();
    private readonly List<Control> _wideOnly = new(), _compactOnly = new();
    internal GraphToolbar(GraphWorkspaceViewModel vm, GraphCanvas canvas)
    {
        Classes.Add("graphToolbar");
        AutomationProperties.SetAutomationId(this, "GraphToolbar");
        var context = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        var title = GraphUi.Value("Snapshot.Title", "bodyStrong");
        title.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
        title.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        context.Children.Add(title);
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var depth = GraphUi.Value("Depth", "caption");
        depth.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("Depth") { StringFormat = "深度 {0}" });
        status.Children.Add(depth); status.Children.Add(GraphUi.Value("VersionLabel", "caption"));
        Grid.SetColumn(status, 1); context.Children.Add(status);

        var moreViews = Command("More", null, () => { }, "GraphMoreViews", "更多视图");
        var views = new MenuFlyout();
        var topItem = Item("顶层", vm.GoTop, "GraphTopMenu");
        _compactOnly.Add(topItem); views.Items.Add(topItem);
        views.Items.Add(Item("展开整图", vm.ExpandAll, "GraphExpandAll"));
        views.Items.Add(Item("最底层", vm.GoBottom, "GraphBottom"));
        views.Items.Add(Item("图 / 列表", () => vm.IsListView = !vm.IsListView, "GraphToggleList"));
        moreViews.Flyout = views;
        var selected = Command("Branch", "展开", vm.ExpandSelected, "GraphExpandSelected");
        GraphUi.Bind(selected, IsEnabledProperty, "HasSelectedModule");
        var top = Command("ArrowUpload", "顶层", vm.GoTop, "GraphTop");
        _wideOnly.Add(top);
        var navigation = Group("视图", top, Command("ChevronUp", "上一层", vm.GoUp, "GraphUp"), selected, moreViews);
        var zoom = Group("缩放", Command("ZoomOut", null, () => canvas.Zoom(1 / 1.2), "GraphZoomOut", "缩小"),
            Command("FullScreenMaximize", "适应", canvas.Fit, "GraphFit"),
            Command("ZoomIn", null, () => canvas.Zoom(1.2), "GraphZoomIn", "放大"));
        var properties = Command("Info", "属性", () => vm.RevealPanel(GraphWorkspaceViewModel.PropertiesPageId), "GraphShowProperties");
        GraphUi.Bind(properties, IsEnabledProperty, "HasSelectedModule");
        var chat = Command("Chat", "会话", () => vm.OpenChat(), "GraphSelectedChat");
        GraphUi.Bind(chat, IsEnabledProperty, "HasSelectedModule");
        var add = Command("Add", null, () => vm.BeginModule(), "GraphToolbarNewModule", "新建模块");
        GraphUi.Bind(add, IsEnabledProperty, "CanEdit");
        _wideOnly.Add(chat); _wideOnly.Add(add);
        var moduleMore = Command("More", null, () => { }, "GraphModuleMore", "模块操作");
        var moduleMenu = new MenuFlyout();
        var openChat = Item("打开会话", () => vm.OpenChat(), "GraphSelectedChatMenu");
        GraphUi.Bind(openChat, IsEnabledProperty, "HasSelectedModule");
        var newModule = Item("新建模块", () => vm.BeginModule(), "GraphNewModuleMenu");
        GraphUi.Bind(newModule, IsEnabledProperty, "CanEdit");
        moduleMenu.Items.Add(openChat); moduleMenu.Items.Add(newModule);
        moduleMore.Flyout = moduleMenu; _compactOnly.Add(moduleMore);
        var modules = Group("模块", properties, chat, add, moduleMore);
        var groups = new WrapPanel { Orientation = Orientation.Horizontal };
        groups.Children.Add(navigation); groups.Children.Add(zoom); groups.Children.Add(modules);
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(context); body.Children.Add(groups); body.Children.Add(GraphUi.Error(vm));
        Child = body;
        SizeChanged += (_, _) =>
        {
            var compact = Bounds.Width < 720;
            foreach (var label in _labels) label.IsVisible = !compact;
            foreach (var control in _wideOnly) control.IsVisible = !compact;
            foreach (var control in _compactOnly) control.IsVisible = compact;
        };
    }

    private static Control Group(string title, params Control[] commands)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var command in commands) buttons.Children.Add(command);
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(GraphUi.Text(title, "caption")); body.Children.Add(buttons);
        var border = new Border { Child = body };
        border.Classes.Add("graphToolbarGroup");
        return border;
    }
    private static MenuItem Item(string title, Action action, string id)
    {
        var item = new MenuItem { Header = title };
        AutomationProperties.SetAutomationId(item, id);
        item.Click += (_, _) => action();
        return item;
    }
    private Button Command(string icon, string? title, Action action, string id, string? name = null)
    {
        var button = GraphUi.Button(title ?? name ?? "", action, id);
        button.Classes.Add("graphToolCommand");
        var glyph = new PathIcon { Width = 16, Height = 16 };
        glyph.Bind(PathIcon.DataProperty, new DynamicResourceExtension("Icon." + icon));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(glyph);
        if (title != null) { var label = GraphUi.Text(title); _labels.Add(label); row.Children.Add(label); }
        button.Content = row;
        ToolTip.SetTip(button, name ?? title);
        return button;
    }
}
