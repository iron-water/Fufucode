using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.VisualTree;

namespace Fufucode.App.Views;

/// <summary>
/// Windows 设置风格的唯一布局构件来源：分组标题 + 行卡。
/// 一行 = 可选图标 + 标题/说明 + 右侧控件，或一个进入箭头（点击跳到二级页）。
/// 每项是一张独立圆角行卡，组只负责纵向节奏（8px），不包大卡。
/// 新设置页必须经这里构建行，禁止在页面里手写 settingsRow 结构。
/// </summary>
internal static class SettingsLayout
{
    private sealed class SearchTermsOwner { }

    /// <summary>组与组之间的固定上间隔（8px 网格的整数倍）。</summary>
    public const double SectionTopGap = 24;

    /// <summary>供设置页搜索使用的行语义词。由标准行构件统一写入，页面不重复维护过滤条件。</summary>
    public static readonly AttachedProperty<string?> SearchTermsProperty =
        AvaloniaProperty.RegisterAttached<SearchTermsOwner, Control, string?>("SearchTerms");

    public static void SetSearchTerms(Control control, string? value) => control.SetValue(SearchTermsProperty, value);
    public static string? GetSearchTerms(Control control) => control.GetValue(SearchTermsProperty);

    /// <summary>分组标题（卡片外面）。</summary>
    public static TextBlock GroupHeader(string text, bool first = false)
    {
        var block = new TextBlock { Text = text };
        block.Classes.Add("settingsGroupHeader");
        if (first) block.Classes.Add("first");
        return block;
    }

    /// <summary>
    /// 可折叠的分组标题：标题 + 右侧计数 + 展开箭头。
    /// 对应 Windows 设置里长列表分组（如"多显示器"）的收起/展开。
    /// </summary>
    public static Control CollapsibleGroupHeader(string text, Control body, int count, bool first = false)
    {
        var chevron = new PathIcon { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        TryGeometry("Icon.ChevronUp", chevron);
        var countText = new TextBlock { Text = $"{count} 项", VerticalAlignment = VerticalAlignment.Center };
        countText.Classes.Add("meta");

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        // 标题在按钮内部：显式清掉 settingsGroupHeader 自带的外部组间隔，
        // 组间隔只由按钮自身的 Margin 提供，避免 32 + 32 叠加。
        var title = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0) };
        title.Classes.Add("settingsGroupHeader");
        content.Children.Add(title);
        Grid.SetColumn(countText, 1); content.Children.Add(countText);
        Grid.SetColumn(chevron, 2); content.Children.Add(chevron);

        var header = new Button
        {
            Padding = new Thickness(0), Margin = new Thickness(0, first ? 0 : 32, 0, 10), MinHeight = 24,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = null, BorderThickness = new Thickness(0), Content = content,
        };
        header.Classes.Add("settingsGroupToggle");
        AutomationProperties.SetName(header, "折叠或展开分组 " + text);
        AutomationProperties.SetAutomationId(header, "settings-collapsible-" + text);
        AutomationProperties.SetAutomationId(body, "settings-collapsible-body-" + text);

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

    private static void TryGeometry(string key, PathIcon icon)
    {
        icon.Bind(PathIcon.DataProperty, new DynamicResourceExtension(key));
    }

    /// <summary>
    /// 一组设置项。在默认/Win11 模式下为无边框分组容器；
    /// 在 Apple Inset Grouped / LiquidGlass 下作为一体化连续圆角卡片渲染（组内发丝分割线）。
    /// </summary>
    public static Control Group(params Control[] rows)
    {
        var inner = new StackPanel();
        inner.Classes.Add("settingsGroupList");
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            if (row is Border border)
            {
                if (index == 0) border.Classes.Add("first");
                if (index == rows.Length - 1) border.Classes.Add("last");
                else border.Classes.Add("hasDivider");
            }
            inner.Children.Add(row);
        }
        var group = new Border { Child = inner };
        group.Classes.Add("settingsGroup");
        return group;
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
            if (e.Source is ToggleSwitch or Button) return;
            if (e.Source is Visual visual && visual.GetVisualAncestors().Any(a => a is ToggleSwitch or Button)) return;
            toggle.IsChecked = toggle.IsChecked != true;
        };
        return row;
    }

    /// <summary>可点击行：右侧显示进入箭头，点击执行 open。</summary>
    public static Border ChevronRow(string title, string? description, Action open, string? iconKey = null)
    {
        var chevron = new PathIcon { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        TryGeometry("Icon.ChevronRight", chevron);
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
            // Win11 设置：描述直接以次级文字渲染在标题下方——唯一的提示方式。
            var desc = new TextBlock { Text = description };
            desc.Classes.Add("settingsRowDesc");
            desc.TextTrimming = TextTrimming.None;
            text.Children.Add(desc);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 14 };
        if (iconKey is not null)
        {
            var icon = new PathIcon { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
            TryGeometry("Icon." + iconKey, icon);
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
        SetSearchTerms(row, string.Join(" ", new[] { title, description }
            .Where(value => !string.IsNullOrWhiteSpace(value))));
        row.Classes.Add("settingsRow");
        // Win11 行高：带说明 64，仅标题 52。compact 只给无说明行。
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
