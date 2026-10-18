using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Fufucode.App.LiquidGlass.Tests;

/// <summary>
/// 与 App.axaml 相同的样式链（FluentTheme + FufucodeTheme + WorkbenchStyles），
/// 供 LiquidGlass 租约在真实无头 Window 上验证资源解析、adapter 包覆与降级渲染。
/// </summary>
internal sealed class TestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Fufucode.App/"))
        {
            Source = new Uri("avares://Fufucode.App/Styles/FufucodeTheme.axaml"),
        });
        Styles.Add(new StyleInclude(new Uri("avares://Fufucode.App/"))
        {
            Source = new Uri("avares://Fufucode.App/Styles/WorkbenchStyles.axaml"),
        });
    }
}

/// <summary>真实控件探针：chrome 宿主（chromeBar/dockHost/statusStrip/toast/overlayPanel）+ 常规控件。</summary>
internal sealed class Probe
{
    private Probe(Window window, Border surface)
    {
        Window = window;
        Surface = surface;
    }

    public Window Window { get; }
    public Border Surface { get; }
    public TextBlock Title { get; private init; } = null!;
    public Button DefaultButton { get; private init; } = null!;
    public Button AccentButton { get; private init; } = null!;
    public TextBox TextBox { get; private init; } = null!;
    public ListBox ListBox { get; private init; } = null!;
    public Border Card { get; private init; } = null!;
    public Border SettingsRow { get; private init; } = null!;
    public Border OverlayPanel { get; private init; } = null!;
    public Border Toast { get; private init; } = null!;
    public Border ChromeBar { get; private init; } = null!;
    public Border DockHost { get; private init; } = null!;
    public Border StatusStrip { get; private init; } = null!;
    public Border PlainPanel { get; private init; } = null!;

    public object? Resolved(string key) =>
        Window.TryFindResource(key, Window.ActualThemeVariant, out var value) ? value : null;

    public Color? ResolvedColor(string key) => Resolved(key) switch
    {
        ISolidColorBrush brush => brush.Color,
        Color color => color,
        _ => null,
    };

    public CornerRadius ResolvedCorner(string key) => Resolved(key) is CornerRadius radius ? radius : default;

    public static Probe Create(double width, double height)
    {
        var title = new TextBlock { Text = "Fufucode LiquidGlass 工装", Classes = { "subtitle" } };
        var defaultButton = new Button { Content = "默认按钮", Width = 150, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var accentButton = new Button { Content = "主按钮", Classes = { "accent" }, Width = 150, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var textBox = new TextBox { Text = "皮肤输入 123", Width = 250 };

        var listBox = new ListBox { Width = 250, Height = 120 };
        foreach (var name in new[] { "会话 Alpha", "会话 Beta", "会话 Gamma" })
            listBox.Items.Add(new ListBoxItem { Content = name });
        listBox.SelectedIndex = 1;

        var card = new Border
        {
            Classes = { "card" },
            Child = new TextBlock { Text = "设置卡片", Classes = { "settingsRowTitle" } },
        };
        var settingsRow = new Border
        {
            Classes = { "settingsRow" },
            Child = new TextBlock { Text = "设置行", Classes = { "settingsRowTitle" } },
        };
        var overlayPanel = new Border
        {
            Classes = { "overlayPanel" },
            MinHeight = 48,
            Child = new TextBlock { Text = "浮层面板", Margin = new Thickness(12, 8) },
        };
        var toast = new Border
        {
            Classes = { "toast" },
            Child = new TextBlock { Text = "Toast 通知", Margin = new Thickness(14, 8) },
        };

        // 与生产 MainWindow.axaml 同名的 chrome 宿主：adapter 按 Name/Classes 识别。
        var chromeBar = new Border
        {
            Name = "CustomCaption",
            Classes = { "chromeBar" },
            Height = 40,
            Child = new TextBlock { Text = "标题栏", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(12, 0) },
        };
        var dockHost = new Border
        {
            Name = "SidebarBorder",
            Classes = { "dockHost", "dockLeft" },
            Width = 160,
            Child = new TextBlock { Text = "停靠宿主", Margin = new Thickness(10, 8) },
        };
        var statusStrip = new Border
        {
            Name = "CustomStatusBar",
            Classes = { "chromeBar", "statusStrip" },
            Height = 24,
            Child = new TextBlock { Text = "状态栏", FontSize = 11, Margin = new Thickness(10, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
        };
        // 普通面板：不应被 adapter 识别（反例）。
        var plainPanel = new Border
        {
            Classes = { "card" },
            MinHeight = 32,
            Child = new TextBlock { Text = "普通内容面板", Margin = new Thickness(10, 6) },
        };

        var contentColumn = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(16),
            Children = { title, defaultButton, accentButton, textBox, listBox, card, settingsRow, overlayPanel, toast, plainPanel },
        };
        Grid.SetColumn(contentColumn, 1);

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("160,*"), RowDefinitions = new RowDefinitions("40,*,24") };
        Grid.SetColumnSpan(chromeBar, 2);
        layout.Children.Add(chromeBar);
        Grid.SetRow(dockHost, 1);
        layout.Children.Add(dockHost);
        Grid.SetRow(contentColumn, 1);
        layout.Children.Add(contentColumn);
        Grid.SetRow(statusStrip, 2);
        Grid.SetColumnSpan(statusStrip, 2);
        layout.Children.Add(statusStrip);

        var surface = new Border { Child = layout };
        surface[!Border.BackgroundProperty] = new DynamicResourceExtension("AppWindowBackground");
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = surface,
        };

        return new Probe(window, surface)
        {
            Title = title,
            DefaultButton = defaultButton,
            AccentButton = accentButton,
            TextBox = textBox,
            ListBox = listBox,
            Card = card,
            SettingsRow = settingsRow,
            OverlayPanel = overlayPanel,
            Toast = toast,
            ChromeBar = chromeBar,
            DockHost = dockHost,
            StatusStrip = statusStrip,
            PlainPanel = plainPanel,
        };
    }
}

internal static class Harness
{
    public static void PumpTicks(int rounds = 8)
    {
        for (var index = 0; index < rounds; index++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }
    }

    /// <summary>真实渲染帧写出（需要 Skia 无头渲染；UseHeadlessDrawing=false）。</summary>
    public static bool Screenshot(Window window, string path)
    {
        PumpTicks(4);
        var frame = window.CaptureRenderedFrame();
        if (frame is null) return false;
        frame.Save(path, PngBitmapEncoderOptions.Default);
        return true;
    }
}
