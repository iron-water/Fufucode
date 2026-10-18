using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Fufucode.App.Aero.Tests;

/// <summary>
/// 真实状态画廊：对 Button / TextBox / dockTab / ListBoxItem / 窗口按钮 逐状态（normal·hover·pressed·focus·disabled）
/// 构建真实控件实例，挂树后强制伪类，供亮/暗帧拍摄与断言取真实模板属性。
/// </summary>
internal static class StateGallery
{
    internal sealed record Cells(
        Window Window,
        List<(Control Control, string State)> Forced,
        Dictionary<(string Kind, string State), Control> Map);

    public static Cells Build(ThemeVariant variant, double width = 1100, double height = 800)
    {
        var states = new[] { "normal", "hover", "pressed", "focus", "disabled" };
        var forced = new List<(Control, string)>();
        var map = new Dictionary<(string, string), Control>();

        var root = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*"), ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16, RowSpacing = 10 };
        var heading = new TextBlock { Text = $"Aero 状态画廊 · {variant.Key}", Classes = { "subtitle" } };
        Grid.SetColumnSpan(heading, 2);
        root.Children.Add(heading);
        var leftColumn = new StackPanel { Spacing = 10 };
        var rightColumn = new StackPanel { Spacing = 10 };
        Grid.SetRow(leftColumn, 1); Grid.SetRow(rightColumn, 1);
        Grid.SetColumn(rightColumn, 1);
        root.Children.Add(leftColumn);
        root.Children.Add(rightColumn);

        // 行 1：默认按钮 / 主按钮 / 图标按钮
        leftColumn.Children.Add(Row("按钮", states, (state, index) =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var kind in new[] { "button", "accent", "icon" })
            {
                var button = new Button { Content = kind == "icon" ? "▣" : $"{kind} {state}", Width = kind == "icon" ? 34 : 124, Height = 28 };
                if (kind == "accent") button.Classes.Add("accent");
                if (kind == "icon")
                {
                    button.Classes.Add("iconButton");
                    button.Padding = new Thickness(0);
                }

                forced.Add((button, state));
                map[(kind, state)] = button;
                panel.Children.Add(button);
            }

            return panel;
        }));

        // 行 2：TextBox / ComboBox
        leftColumn.Children.Add(Row("输入", states, (state, _) =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var textBox = new TextBox { Text = $"输入 {state}", Width = 200, Height = 28 };
            var comboBox = new ComboBox { Width = 160, Height = 28 };
            comboBox.Items.Add($"选项 {state}");
            comboBox.SelectedIndex = 0;
            forced.Add((textBox, state));
            forced.Add((comboBox, state));
            map[("input", state)] = textBox;
            map[("combo", state)] = comboBox;
            panel.Children.Add(textBox);
            panel.Children.Add(comboBox);
            return panel;
        }));

        // 行 3：dockTab（工作台页签）
        leftColumn.Children.Add(Row("页签", states, (state, _) =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var tab = new Button { Content = $"页签 {state}", Width = 104, Height = 28, Padding = new Thickness(10, 3) };
            tab.Classes.Add("subtle");
            tab.Classes.Add("dockTab");
            AutomationProperties.SetName(tab, $"选择 {state}");
            forced.Add((tab, state));
            map[("docktab", state)] = tab;
            panel.Children.Add(tab);
            return panel;
        }));

        // 行 4：ListBoxItem（选中态单独加 selected）
        rightColumn.Children.Add(Row("列表", states, (state, _) =>
        {
            var listBox = new ListBox { Width = 220, Height = 28 };
            var item = new ListBoxItem { Content = $"行 {state}" };
            listBox.Items.Add(item);
            if (state != "disabled") forced.Add((item, state));
            else item.IsEnabled = false;
            map[("list", state)] = item;
            return listBox;
        }));

        // 行 5：窗口按钮（真 Aero ControlTheme）
        rightColumn.Children.Add(Row("窗口按钮", states, (state, _) =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var kind in new[] { "min", "max", "close" })
            {
                var button = new Button { };
                button.Classes.Add("aeroCaptionButton");
                button.Classes.Add(kind);
                AutomationProperties.SetName(button, kind switch { "min" => "最小化", "max" => "最大化", _ => "关闭" });
                forced.Add((button, state));
                map[(kind, state)] = button;
                panel.Children.Add(button);
            }

            return panel;
        }));

        // 行 6：设置行 / 浮层 / 菜单项
        rightColumn.Children.Add(Row("行/浮层", states, (state, _) =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var row = new Border
            {
                Classes = { "settingsRow" },
                Width = 150,
                MinHeight = 32,
                Child = new TextBlock { Text = $"设置行 {state}", Classes = { "settingsRowTitle" }, Margin = new Thickness(12, 6) },
            };
            if (state == "disabled") row.Classes.Add("disabled");
            var overlay = new Border
            {
                Classes = { "overlayPanel" },
                Width = 120,
                MinHeight = 32,
                Child = new TextBlock { Text = $"浮层 {state}", Margin = new Thickness(12, 6) },
            };
            var menuItem = new MenuItem { Header = "菜单", Width = 84 };
            forced.Add((menuItem, state));
            map[("row", state)] = row;
            map[("overlay", state)] = overlay;
            map[("menu", state)] = menuItem;
            panel.Children.Add(row);
            panel.Children.Add(overlay);
            panel.Children.Add(menuItem);
            return panel;
        }));

        var surface = new Border { Child = root };
        surface[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppWindowBackground");
        var window = new Window { Width = width, Height = height, Content = surface, RequestedThemeVariant = variant };
        return new Cells(window, forced, map);
    }

    private static Control Row(string label, IReadOnlyList<string> states, Func<string, int, Control> factory)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*") };
        grid.Children.Add(new TextBlock { Text = label, Classes = { "sectionCaption" }, VerticalAlignment = VerticalAlignment.Center });
        var column = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 4) };
        for (var index = 0; index < states.Count; index++)
        {
            var state = states[index];
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            line.Children.Add(new TextBlock { Text = state, Width = 64, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(factory(state, index));
            column.Children.Add(line);
        }

        Grid.SetColumn(column, 1);
        grid.Children.Add(column);
        return grid;
    }
}
