using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Fufucode.App.Aero.Tests;

/// <summary>与 App.axaml 相同的样式链（FluentTheme + FufucodeTheme + WorkbenchStyles）的无头宿主。</summary>
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

/// <summary>
/// 真实控件探针：结构与生产同形（CustomCaption 栅格、CaptionAssetButton 等价 Button、工作台控件）。
/// 标题栏用普通 Button + AutomationName/Click 复刻 CaptionAssetButton 的语义面（类型本身属 Views，测试只用可复用语义）。
/// </summary>
internal sealed class Probe
{
    private Probe(Window window, Border surface)
    {
        Window = window;
        Surface = surface;
    }

    public Window Window { get; }
    public Border Surface { get; }
    public Border Caption { get; private init; } = null!;
    public TextBlock CaptionTitle { get; private init; } = null!;
    public Button CaptionMin { get; private init; } = null!;
    public Button CaptionMax { get; private init; } = null!;
    public Button CaptionClose { get; private init; } = null!;
    public Button DefaultButton { get; private init; } = null!;
    public Button AccentButton { get; private init; } = null!;
    public Button DisabledButton { get; private init; } = null!;
    public Button IconButton { get; private init; } = null!;
    public TextBox TextBox { get; private init; } = null!;
    public ComboBox ComboBox { get; private init; } = null!;
    public ListBox ListBox { get; private init; } = null!;
    public TabControl Tabs { get; private init; } = null!;
    public Border ContentPaper { get; private init; } = null!;
    public Border SettingsRow { get; private init; } = null!;
    public Border OverlayPanel { get; private init; } = null!;
    public Border Toast { get; private init; } = null!;
    public Border DockSurface { get; private init; } = null!;
    public Border DockIndicator { get; private init; } = null!;
    public Button DockTabSelected { get; private init; } = null!;
    public Button DockTabNormal { get; private init; } = null!;
    public ContextMenu ContextMenu { get; private init; } = null!;
    public Border DialogCaption { get; private init; } = null!;
    public Border DialogBody { get; private init; } = null!;
    public Border DialogFooter { get; private init; } = null!;
    public int CaptionClicks { get; set; }

    public object? Resolved(string key) =>
        Window.TryFindResource(key, Window.ActualThemeVariant, out var value) ? value : null;

    public Color? ResolvedColor(string key) => Resolved(key) is ISolidColorBrush brush ? brush.Color : null;

    public double? ResolvedDouble(string key) => Resolved(key) is double value ? value : null;

    public CornerRadius? ResolvedCorner(string key) => Resolved(key) is CornerRadius radius ? radius : null;

    public ContentPresenter? SelectedListPresenter() =>
        ListBox.GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(item => item.IsSelected)?
            .GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault();

    public Border? SelectedTabPipe() =>
        Tabs.GetVisualDescendants().OfType<TabItem>()
            .FirstOrDefault(item => item.IsSelected)?
            .GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "PART_SelectedPipe");

    public static Button CaptionButton(string kind, string automation, Action click)
    {
        var button = new Button
        {
            Width = 46,
            Height = 32,
            Padding = new Thickness(0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Content = new TextBlock { Text = kind, FontSize = 10 },
        };
        button.Classes.Add("captionButton");
        AutomationProperties.SetName(button, automation);
        button.Click += (_, _) => click();
        return button;
    }

    public static Probe Create(double width, double height)
    {
        var probe = (Probe?)null;
        var clicks = 0;

        void Count() => clicks++;

        var captionMin = CaptionButton("min", "最小化", Count);
        captionMin.Name = "CaptionMinimize";
        var captionMax = CaptionButton("max", "最大化", Count);
        captionMax.Name = "CaptionMaximize";
        var captionClose = CaptionButton("close", "关闭", Count);
        captionClose.Name = "CaptionClose";

        var title = new TextBlock { Text = "Fufucode · Aero 模块帧", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(12, 0) };
        title.Classes.Add("aeroCaptionTitle");
        var drag = new Border { Background = Brushes.Transparent };
        var captionButtons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Children = { captionMin, captionMax, captionClose },
        };
        var captionGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,Auto") };
        captionGrid.Children.Add(title);
        Grid.SetColumn(drag, 1);
        captionGrid.Children.Add(drag);
        Grid.SetColumn(captionButtons, 2);
        captionGrid.Children.Add(captionButtons);
        var caption = new Border { Child = captionGrid };
        caption.Classes.Add("chromeBar");
        var topHighlight = new Border();
        topHighlight.Classes.Add("aeroCaptionTopHighlight");
        var captionHost = new Grid();
        captionHost.Children.Add(caption);
        captionHost.Children.Add(topHighlight);

        var defaultButton = new Button { Content = "默认按钮", Width = 150, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var accentButton = new Button { Content = "主按钮", Classes = { "accent" }, Width = 150, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var disabledButton = new Button { Content = "禁用按钮", IsEnabled = false, Width = 150, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var iconButton = new Button { Width = 32, Height = 32, Padding = new Thickness(6), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        iconButton.Classes.Add("iconButton");
        AutomationProperties.SetName(iconButton, "图标按钮");
        iconButton.Content = new PathIcon { Data = Geometry.Parse("M2,2 L14,14") };

        var textBox = new TextBox { Text = "Aero 输入 123", Width = 250, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var comboBox = new ComboBox { Width = 250, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        comboBox.Items.Add("选项一");
        comboBox.Items.Add("选项二");
        comboBox.SelectedIndex = 0;

        var listBox = new ListBox { Width = 250, Height = 132, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        foreach (var name in new[] { "会话 Alpha", "会话 Beta", "会话 Gamma", "会话 Delta" })
            listBox.Items.Add(new ListBoxItem { Content = name });
        listBox.SelectedIndex = 1;

        var tabs = new TabControl { Width = 250, Height = 132, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        tabs.Items.Add(new TabItem { Header = "对话", Content = new TextBlock { Text = "页签内容" } });
        tabs.Items.Add(new TabItem { Header = "变更", Content = new TextBlock { Text = "差异视图" } });
        tabs.SelectedIndex = 0;

        var contentPaper = new Border { Height = 60, Child = new TextBlock { Text = "内容纸（不透明、无渐变）", Margin = new Thickness(10, 6) } };
        contentPaper[!Border.BackgroundProperty] = new DynamicResourceExtension("AppContentBackground");

        var settingsRow = new Border
        {
            Classes = { "settingsRow" },
            Child = new TextBlock { Text = "设置行：Aero 3px 圆角", Classes = { "settingsRowTitle" } },
        };
        var overlayPanel = new Border
        {
            Classes = { "overlayPanel" },
            MinHeight = 56,
            Child = new TextBlock { Text = "浮层面板（直角 + 硬阴影）", Margin = new Thickness(12, 8) },
        };
        var toast = new Border
        {
            Classes = { "toast" },
            Child = new TextBlock { Text = "Toast 通知", Margin = new Thickness(14, 8) },
        };
        var dockSurface = new Border { Height = 48, Child = new TextBlock { Text = "停靠区（chrome 浅渐变）", Margin = new Thickness(10, 6) } };
        dockSurface.Classes.Add("aeroDockSurface");

        // 工作台页签（Button.dockTab）与选中指示条：与 DockRegionView 的真实结构同形
        var dockTabSelected = new Button { Content = "对话", Width = 84, Height = 30, Padding = new Thickness(10, 3) };
        dockTabSelected.Classes.Add("subtle");
        dockTabSelected.Classes.Add("dockTab");
        dockTabSelected.Classes.Add("selected");
        AutomationProperties.SetName(dockTabSelected, "选择对话");
        var dockTabNormal = new Button { Content = "变更", Width = 84, Height = 30, Padding = new Thickness(10, 3) };
        dockTabNormal.Classes.Add("subtle");
        dockTabNormal.Classes.Add("dockTab");
        AutomationProperties.SetName(dockTabNormal, "选择变更");
        var dockIndicator = new Border { Height = 2, Width = 84, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom, IsHitTestVisible = false };
        dockIndicator.Classes.Add("dockIndicator");
        var dockHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto") };
        dockHeader.Children.Add(dockTabSelected);
        Grid.SetColumn(dockTabNormal, 1);
        dockHeader.Children.Add(dockTabNormal);
        var dockHost = new Grid { Height = 34 };
        dockHost.Children.Add(dockHeader);
        dockHost.Children.Add(dockIndicator);

        // 区域菜单（真实 ContextMenu/Items 结构）
        var contextMenu = new ContextMenu();
        contextMenu.Items.Add(new MenuItem { Header = "固定页签" });
        contextMenu.Items.Add(new MenuItem { Header = "浮动整栏" });
        contextMenu.Items.Add(new MenuItem { Header = "禁用项", IsEnabled = false });

        // 对话框三段（Aero 标题条 + 白体 + 灰底栏；生产 ProjectTrustDialog 同构但用类挂接）
        var dialogCaptionText = new TextBlock { Text = "是否信任此项目？" };
        var dialogCaption = new Border { Height = 32, Child = dialogCaptionText };
        dialogCaption.Classes.Add("aeroDialogCaption");
        var dialogBody = new Border { MinHeight = 40, Child = new TextBlock { Text = "对话框正文（不透明）", Margin = new Thickness(12, 8) } };
        dialogBody.Classes.Add("aeroDialogBody");
        var dialogFooter = new Border { Height = 36, Child = new TextBlock { Text = "底部按钮栏", Margin = new Thickness(12, 8) } };
        dialogFooter.Classes.Add("aeroDialogFooter");
        var dialog = new StackPanel { Children = { dialogCaption, dialogBody, dialogFooter } };

        var left = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 0, 12, 0),
            Children =
            {
                new TextBlock { Text = "Aero 控件画廊", Classes = { "subtitle" } },
                new TextBlock { Text = "E7-AERO · 真实 Avalonia 控件", Classes = { "secondary" }, FontSize = 12 },
                defaultButton, accentButton, disabledButton, iconButton, textBox, comboBox, dockHost,
            },
        };
        var middle = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 0, 12, 0),
            Children = { listBox, tabs, dialog },
        };
        var right = new StackPanel
        {
            Spacing = 10,
            Children = { contentPaper, dockSurface, settingsRow, overlayPanel, toast },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetColumnSpan(captionHost, 3);
        grid.Children.Add(captionHost);
        Grid.SetRow(left, 1);
        Grid.SetRow(middle, 1);
        Grid.SetRow(right, 1);
        Grid.SetColumn(left, 0);
        Grid.SetColumn(middle, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(middle);
        grid.Children.Add(right);

        var surface = new Border { Child = grid };
        surface[!Border.BackgroundProperty] = new DynamicResourceExtension("AppWindowBackground");
        var window = new Window { Width = width, Height = height, Content = surface };

        probe = new Probe(window, surface)
        {
            Caption = caption,
            CaptionTitle = title,
            CaptionMin = captionMin,
            CaptionMax = captionMax,
            CaptionClose = captionClose,
            DefaultButton = defaultButton,
            AccentButton = accentButton,
            DisabledButton = disabledButton,
            IconButton = iconButton,
            TextBox = textBox,
            ComboBox = comboBox,
            ListBox = listBox,
            Tabs = tabs,
            ContentPaper = contentPaper,
            SettingsRow = settingsRow,
            OverlayPanel = overlayPanel,
            Toast = toast,
            DockSurface = dockSurface,
            DockIndicator = dockIndicator,
            DockTabSelected = dockTabSelected,
            DockTabNormal = dockTabNormal,
            ContextMenu = contextMenu,
            DialogCaption = dialogCaption,
            DialogBody = dialogBody,
            DialogFooter = dialogFooter,
        };

        _ = clicks;
        return probe;
    }
}

internal static class Harness
{
    /// <summary>
    /// 真实状态画廊：每个控件 × 状态一个实例（伪类在挂树后强制，与真实指针/键盘驱动同一套选择器）。
    /// 输入需先建好控件并挂树，再调用 ForceStates。
    /// </summary>
    public static void ForceStates(IEnumerable<(Control Control, string State)> entries)
    {
        foreach (var (control, state) in entries)
        {
            var pseudo = (IPseudoClasses)control.Classes;
            switch (state)
            {
                case "hover":
                    pseudo.Set(":pointerover", true);
                    break;
                case "pressed":
                    pseudo.Set(":pointerover", true);
                    pseudo.Set(":pressed", true);
                    break;
                case "focus":
                    pseudo.Set(":focus", true);
                    pseudo.Set(":focus-visible", true);
                    break;
                case "disabled":
                    control.IsEnabled = false;
                    break;
            }
        }
    }

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

    /// <summary>真实渲染帧写出（Skia 无头渲染；UseHeadlessDrawing=false）。</summary>
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

    public static string DescribeGradient(IBrush? brush) =>
        brush is LinearGradientBrush gradient
            ? $"stops={gradient.GradientStops.Count} [" + string.Join(", ", gradient.GradientStops.Select(s => $"{s.Offset:F2}:{s.Color}")) + "]"
            : brush?.GetType().Name ?? "(null)";
}
