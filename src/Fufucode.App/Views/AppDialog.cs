using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Fufucode.App.Views;

/// <summary>
/// 代码构建对话框的统一外观：对话框底色、标题区与底部操作条。
/// 此前各调用点各自 new Window，未设置背景，深色主题下会出现纯黑窗口，
/// 与设置页/主窗口的视觉语言不一致。
/// </summary>
internal static class AppDialog
{
    /// <summary>创建带应用对话框底色、居中、随内容高度的窗口。</summary>
    public static Window Create(string title, double width)
    {
        var dialog = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        dialog.Bind(Window.BackgroundProperty, new DynamicResourceExtension("AppDialogBackground"));
        return dialog;
    }

    /// <summary>标题区：图标 + 标题 + 一句说明。</summary>
    public static Control Header(string iconResourceKey, string title, string description)
    {
        var glyph = new PathIcon { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        glyph.Bind(PathIcon.DataProperty, new DynamicResourceExtension(iconResourceKey));
        glyph.Bind(PathIcon.ForegroundProperty, new DynamicResourceExtension("AppAccentText"));

        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title, Classes = { "subtitle" } });
        var hint = new TextBlock { Text = description, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
        texts.Children.Add(hint);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(24, 20, 24, 6),
        };
        row.Children.Add(glyph);
        row.Children.Add(texts);
        return row;
    }

    /// <summary>底部操作条：与 ProjectTrustDialog 相同的实色底条与右对齐按钮。</summary>
    public static Border Footer(params Control[] actions)
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var action in actions)
        {
            action.VerticalAlignment = VerticalAlignment.Center;
            buttons.Children.Add(action);
        }

        var footer = new Border
        {
            Padding = new Thickness(20, 12),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = buttons,
        };
        footer.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppDialogFooterBackground"));
        footer.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("AppBorderBrush"));
        return footer;
    }

    /// <summary>表单主体：统一左右边距，内部用设置卡片承载字段。</summary>
    public static Control Body(Control content) =>
        new Border { Margin = new Thickness(24, 4, 24, 16), Child = content };

    /// <summary>字段标题 + 控件 + 说明。</summary>
    public static Control Field(string label, Control control, string? description = null)
    {
        // 对话框输入框统一使用设置行的内嵌底色，避免深色主题下默认黑底与卡片脱节。
        if (control is TextBox textBox && !textBox.Classes.Contains("dialogInput"))
            textBox.Classes.Add("dialogInput");
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock { Text = label, Classes = { "settingsRowTitle" } });
        stack.Children.Add(control);
        if (description is not null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = description, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap,
            });
        }
        return stack;
    }

    /// <summary>设置卡片容器。</summary>
    public static Control Card(Control content) =>
        new Border { Classes = { "settingsPanel" }, Child = content };

    /// <summary>错误提示行（默认隐藏）。</summary>
    public static TextBlock ErrorText()
    {
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, IsVisible = false, Classes = { "caption" },
        };
        error.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppCriticalForeground"));
        return error;
    }
}
