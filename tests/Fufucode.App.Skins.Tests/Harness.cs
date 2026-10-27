using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Fufucode.App.Skins.Tests;

/// <summary>
/// 与 App.axaml 相同的样式链（FluentTheme + FufucodeTheme + WorkbenchStyles），
/// 供皮肤租约在真实无头 Window 上验证资源解析与控件属性。
/// </summary>
internal sealed class TestApp : Application
{
    public override void Initialize()
    {
        // 与真实 App 一致：启动时确定具体变体（真机取自设置）；工装固定 Light 作为基线。
        RequestedThemeVariant = ThemeVariant.Light;
        // 与真实 App 一致：LiveMarkdown 默认配色 + 本应用的浅色覆盖。
        Resources.MergedDictionaries.Add((IResourceDictionary)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri("avares://LiveMarkdown.Avalonia/Defaults.axaml")));
        Resources.MergedDictionaries.Add((IResourceDictionary)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri("avares://Fufucode.App/Styles/LiveMarkdownTheme.axaml")));
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://LiveMarkdown.Avalonia/")) { Source = new Uri("avares://LiveMarkdown.Avalonia/Styles.axaml") });
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

/// <summary>真实控件探针：皮肤断言全部读取这些控件的运行时属性/资源解析值。</summary>
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
    public Button DisabledButton { get; private init; } = null!;
    public TextBox TextBox { get; private init; } = null!;
    public ComboBox ComboBox { get; private init; } = null!;
    public ListBox ListBox { get; private init; } = null!;
    public TabControl Tabs { get; private init; } = null!;
    public Border Card { get; private init; } = null!;
    public Border SettingsRow { get; private init; } = null!;
    public Border OverlayPanel { get; private init; } = null!;
    public Border Toast { get; private init; } = null!;
    public Border Chip { get; private init; } = null!;
    public Border ChromeBar { get; private init; } = null!;
    public TextBlock ChromeBarText { get; private init; } = null!;
    public PathIcon ChromeBarIcon { get; private init; } = null!;

    public object? Resolved(string key) =>
        Window.TryFindResource(key, Window.ActualThemeVariant, out var value) ? value : null;

    public Color? ResolvedColor(string key) => Resolved(key) switch
    {
        ISolidColorBrush brush => brush.Color,
        Color color => color,
        _ => null,
    };

    public CornerRadius ResolvedCorner(string key) => Resolved(key) is CornerRadius radius ? radius : default;

    /// <summary>选中 ListBoxItem 的模板 ContentPresenter（选择态实际渲染面）。</summary>
    public ContentPresenter? SelectedListPresenter() =>
        ListBox.GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(item => item.IsSelected)?
            .GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault();

    /// <summary>选中 TabItem 的模板指示条（Fluent TabItem 模板部件）。</summary>
    public Border? SelectedTabPipe() =>
        Tabs.GetVisualDescendants().OfType<TabItem>()
            .FirstOrDefault(item => item.IsSelected)?
            .GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "PART_SelectedPipe");

    public static Probe Create(double width, double height)
    {
        var title = new TextBlock { Text = "Fufucode 皮肤工装", Classes = { "subtitle" } };
        var subtitle = new TextBlock { Text = "E7 · 真实 Avalonia 控件", Classes = { "secondary" }, FontSize = 12 };

        var defaultButton = new Button { Content = "默认按钮", Width = 150, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var accentButton = new Button { Content = "主按钮", Classes = { "accent" }, Width = 150, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var disabledButton = new Button { Content = "禁用按钮", IsEnabled = false, Width = 150, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var textBox = new TextBox { Text = "皮肤输入 123", Width = 250 };
        var comboBox = new ComboBox { Width = 250 };
        comboBox.Items.Add("选项一");
        comboBox.Items.Add("选项二");
        comboBox.Items.Add("选项三");
        comboBox.SelectedIndex = 0;

        var listBox = new ListBox { Width = 250, Height = 132 };
        foreach (var name in new[] { "会话 Alpha", "会话 Beta", "会话 Gamma", "会话 Delta" })
            listBox.Items.Add(new ListBoxItem { Content = name });
        listBox.SelectedIndex = 1;

        var tabs = new TabControl { Width = 250, Height = 132 };
        tabs.Items.Add(new TabItem { Header = "对话", Content = new TextBlock { Text = "页签内容" } });
        tabs.Items.Add(new TabItem { Header = "变更", Content = new TextBlock { Text = "差异视图" } });
        tabs.SelectedIndex = 0;

        var card = MakeCard("设置卡片", "卡片 / 浮层 / 行状态");
        var settingsRow = new Border
        {
            Classes = { "settingsRow" },
            Child = new TextBlock { Text = "设置行：标题 + 说明", Classes = { "settingsRowTitle" } },
        };
        var overlayPanel = new Border
        {
            Classes = { "overlayPanel" },
            MinHeight = 56,
            Child = new TextBlock { Text = "浮层面板", Margin = new Thickness(12, 8) },
        };
        var toast = new Border
        {
            Classes = { "toast" },
            Child = new TextBlock { Text = "Toast 通知", Margin = new Thickness(14, 8) },
        };
        var chip = new Border
        {
            Classes = { "chip" },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Child = new TextBlock { Text = "状态标签", FontSize = 12, Margin = new Thickness(8, 2) },
        };

        // 真实外壳的标题栏同款类名（Border.chromeBar）：Luna 的 XP 蓝渐变与白字断言读取这里。
        var chromeBarIcon = new PathIcon
        {
            Data = Geometry.Parse("M2,8 H14"),
            Width = 12,
            Height = 12,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var chromeBarText = new TextBlock
        {
            Text = "Fufucode 标题",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var chromeBar = new Border
        {
            Classes = { "chromeBar" },
            Height = 32,
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8,
                Children = { chromeBarIcon, chromeBarText },
            },
        };

        var left = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 0, 12, 0),
            Children = { title, subtitle, Section("按钮状态"), defaultButton, accentButton, disabledButton, Section("输入 / 选择"), textBox, comboBox },
        };
        var middle = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 0, 12, 0),
            Children = { Section("列表选择态"), listBox, Section("页签状态"), tabs },
        };
        var right = new StackPanel
        {
            Spacing = 10,
            Children = { Section("卡片 / 浮层"), card, settingsRow, overlayPanel, toast, chip, Section("窗口 chrome"), chromeBar },
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            Margin = new Thickness(16),
        };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(middle, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(middle);
        grid.Children.Add(right);

        var surface = new Border { Child = grid };
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
            DisabledButton = disabledButton,
            TextBox = textBox,
            ComboBox = comboBox,
            ListBox = listBox,
            Tabs = tabs,
            Card = card,
            SettingsRow = settingsRow,
            OverlayPanel = overlayPanel,
            Toast = toast,
            Chip = chip,
            ChromeBar = chromeBar,
            ChromeBarText = chromeBarText,
            ChromeBarIcon = chromeBarIcon,
        };
    }

    private static Border MakeCard(string heading, string description) => new()
    {
        Classes = { "card" },
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = heading, Classes = { "settingsRowTitle" } },
                new TextBlock { Text = description, Classes = { "settingsRowDesc" } },
            },
        },
    };

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        Classes = { "sectionCaption" },
    };
}

internal static class Harness
{
    public static bool Pump(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            Thread.Sleep(15);
        }

        Dispatcher.UIThread.RunJobs();
        return condition();
    }

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

    public static Point CenterOf(Control control, Visual relativeTo) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), relativeTo)
        ?? new Point(-1, -1);

    public static void Hover(TopLevel topLevel, Control control) =>
        topLevel.MouseMove(CenterOf(control, topLevel), RawInputModifiers.None);

    public static string Text(Color? color) => color?.ToString() ?? "(null)";
}
