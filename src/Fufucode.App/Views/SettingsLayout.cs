using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Fufucode.App.Views;

/// <summary>
/// Windows 设置风格的布局构件：分组卡片 + 行式条目。
/// 一行 = 可选图标 + 标题/说明 + 右侧控件，或一个进入箭头（点击跳到二级页）。
/// 一个分组一张卡，卡内用细分隔线分行，避免"每项一张卡"的堆叠感。
/// </summary>
internal static class SettingsLayout
{
    /// <summary>分组标题（卡片外面）。</summary>
    public static TextBlock GroupHeader(string text)
    {
        var block = new TextBlock { Text = text };
        block.Classes.Add("settingsGroupHeader");
        return block;
    }

    /// <summary>
    /// 可折叠的分组标题：标题 + 右侧计数 + 展开箭头。
    /// 对应 Windows 设置里长列表分组（如"多显示器"）的收起/展开。
    /// </summary>
    public static Control CollapsibleGroupHeader(string text, Control body, int count)
    {
        var chevron = new PathIcon { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        TryGeometry("Icon.ChevronUp", chevron);
        var countText = new TextBlock { Text = $"{count} 项", VerticalAlignment = VerticalAlignment.Center };
        countText.Classes.Add("meta");

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        var title = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = 14, FontWeight = FontWeight.SemiBold };
        content.Children.Add(title);
        Grid.SetColumn(countText, 1); content.Children.Add(countText);
        Grid.SetColumn(chevron, 2); content.Children.Add(chevron);

        var header = new Button
        {
            Padding = new Thickness(2, 0), Margin = new Thickness(2, 24, 0, 8), MinHeight = 24,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Transparent, BorderThickness = new Thickness(0), Content = content,
        };
        header.Classes.Add("settingsGroupToggle");
        AutomationProperties.SetName(header, "折叠或展开分组 " + text);

        var expanded = true;
        void Apply()
        {
            body.IsVisible = expanded;
            countText.IsVisible = !expanded;
            TryGeometry(expanded ? "Icon.ChevronUp" : "Icon.ChevronDown", chevron);
        }
        header.Click += (_, _) => { expanded = !expanded; Apply(); };
        Apply();
        return header;
    }

    private static readonly IBrush? Transparent = null;
    private static void TryGeometry(string key, PathIcon icon)
    {
        if (Application.Current?.TryGetResource(key, null, out var value) == true && value is Geometry geometry)
            icon.Data = geometry;
    }

    /// <summary>
    /// 一组设置项。Windows 11 设置里每项是一张独立圆角卡、卡间留缝，
    /// 所以这里只做纵向排列，不再把整组合成一张大卡。
    /// </summary>
    public static Control Group(params Control[] rows)
    {
        var host = new StackPanel();
        host.Classes.Add("settingsGroupList");
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            if (row is Border border && index == rows.Length - 1) border.Classes.Add("last");
            host.Children.Add(row);
        }
        return host;
    }

    /// <summary>普通行：标题 + 说明，右侧放控件。</summary>
    public static Border Row(string title, string? description, Control? control, string? iconKey = null)
        => Build(title, description, control, iconKey, null);

    /// <summary>
    /// 开关行：控件左侧带"开/关"状态字，且点击整行任意处都会切换开关
    /// （Windows 设置的行为：行本身就是开关的热区）。
    /// </summary>
    public static Border ToggleRow(string title, string? description, ToggleSwitch toggle,
        string? iconKey = null, string onText = "开", string offText = "关")
    {
        var state = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 20 };
        state.Classes.Add("settingsRowDesc");
        void Sync()
        {
            var on = toggle.IsChecked == true;
            state.Text = on ? onText : offText;
        }
        toggle.IsCheckedChanged += (_, _) => Sync();
        Sync();
        var host = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(state); host.Children.Add(toggle);

        var row = Build(title, description, host, iconKey, null);
        row.Classes.Add("clickable");
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            // 点在开关自身时不重复切换（控件已处理）。
            if (e.Source is ToggleSwitch) return;
            if (e.Source is Visual visual && visual.GetVisualAncestors().Any(a => a is ToggleSwitch)) return;
            toggle.IsChecked = toggle.IsChecked != true;
        };
        return row;
    }

    /// <summary>可点击行：右侧显示进入箭头，点击执行 open。</summary>
    public static Border ChevronRow(string title, string? description, Action open, string? iconKey = null)
    {
        var chevron = new PathIcon { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        if (Application.Current?.TryGetResource("Icon.ChevronRight", null, out var geometry) == true)
            chevron.Data = geometry as Geometry;
        chevron.Classes.Add("meta");
        var row = Build(title, description, chevron, iconKey, open);
        AutomationProperties.SetName(row, "打开：" + title);
        return row;
    }

    private static Border Build(string title, string? description, Control? control, string? iconKey, Action? open)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var titleBlock = new TextBlock { Text = title };
        titleBlock.Classes.Add("settingsRowTitle");
        text.Children.Add(titleBlock);
        if (!string.IsNullOrEmpty(description))
        {
            var desc = new TextBlock { Text = description };
            desc.Classes.Add("settingsRowDesc");
            ToolTip.SetTip(desc, description);
            text.Children.Add(desc);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 14 };
        if (iconKey is not null)
        {
            var icon = new PathIcon { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
            if (Application.Current?.TryGetResource("Icon." + iconKey, null, out var geometry) == true)
                icon.Data = geometry as Geometry;
            icon.Classes.Add("meta");
            grid.Children.Add(icon);
        }
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        if (control is not null)
        {
            var host = new Border { VerticalAlignment = VerticalAlignment.Center, Child = control };
            Grid.SetColumn(host, 2); grid.Children.Add(host);
        }

        var row = new Border { Child = grid };
        row.Classes.Add("settingsRow");
        if (string.IsNullOrEmpty(description)) row.Classes.Add("compact");
        if (open is not null) row.Classes.Add("clickable");
        if (open is not null)
        {
            row.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
                // 右侧控件（下拉、开关、按钮）自行处理点击，行不再触发跳转。
                if (e.Source is Button or ComboBox or ToggleSwitch or TextBox or Slider or CheckBox
                    || (e.Source is Visual visual && visual.GetVisualAncestors().Any(a => a is Button or ComboBox or ToggleSwitch or TextBox or Slider or CheckBox))) return;
                open();
            };
        }
        return row;
    }

    /// <summary>把换行/说明文字统一成行内说明块（用于多行补充说明）。</summary>
    public static TextBlock Note(string text, bool warning = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 8) };
        block.Classes.Add("meta");
        if (warning) block.Foreground = Application.Current?.TryGetResource("AppCriticalForeground", null, out var brush) == true ? brush as IBrush : null;
        return block;
    }
}
