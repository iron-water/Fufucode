// 无头工装：组装真实 Avalonia 控件 → 强制各状态 → 断言模板实际取到的画刷/尺寸 → 输出渲染帧。
// 用法：dotnet run -c Release -- <输出目录>
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AeroCaptionHarness;

internal static class Program
{
    private static int _fail;
    private static int _pass;

    private static void Check(bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
    }

    public static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "frames");
        Directory.CreateDirectory(outDir);
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        // 演示树：亮/暗各一个 ThemeVariantScope；caption 结构与生产 MainWindow.axaml 的 CustomCaption 同形
        var lightPanel = new StackPanel { Spacing = 10 };
        var darkPanel = new StackPanel { Spacing = 10 };
        Fill(lightPanel); Fill(darkPanel);
        var root = new StackPanel();
        root.Children.Add(new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Light, Child = new Border { Background = new SolidColorBrush(Color.Parse("#2E78B8")), Padding = new Thickness(16), Child = lightPanel } });
        root.Children.Add(new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Dark, Child = new Border { Background = new SolidColorBrush(Color.Parse("#0B1626")), Padding = new Thickness(16), Child = darkPanel } });
        var window = new Window { Width = 900, Height = 398, Content = root, FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), FontSize = 12 };
        window.Show();
        Pump();
        ForceStates(lightPanel); ForceStates(darkPanel);
        Pump();

        Verify(lightPanel, "Light");
        Verify(darkPanel, "Dark");
        VerifyRealPointer();
        VerifyThemeHotSwap();

        using (var frame = window.CaptureRenderedFrame())
            frame?.Save(Path.Combine(outDir, "avalonia-aero-caption-900x398.png"), PngBitmapEncoderOptions.Default);
        Console.WriteLine($"frame: {Path.Combine(outDir, "avalonia-aero-caption-900x398.png")} {window.Bounds.Width}x{window.Bounds.Height}");
        Console.WriteLine($"RESULT pass={_pass} fail={_fail}");
        window.Close();
        return _fail == 0 ? 0 : 1;
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    private static readonly string[] States = ["normal", "hover", "pressed", "focus", "disabled"];

    private static Button CaptionButton(string kind, string name, string state = "normal")
    {
        var b = new Button
        {
            Theme = (ControlTheme)Application.Current!.FindResource("AeroCaptionButtonTheme")!,
            Tag = state,
        };
        b.Classes.Add("aeroCaptionButton");
        b.Classes.Add(kind);
        AutomationProperties.SetName(b, name);
        if (state == "disabled") b.IsEnabled = false;
        return b;
    }

    private static Border Caption(string title, bool inactive)
    {
        var caption = new Border();
        caption.Classes.Add("aeroCaption");
        if (inactive) caption.Classes.Add("inactive");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var text = new TextBlock { Text = title, Margin = new Thickness(12, 0) };
        text.Classes.Add("aeroCaptionTitle");
        grid.Children.Add(text);
        var drag = new Border { Background = Brushes.Transparent, Name = "DragRegion" };  // 拖拽区：生产在此处理 BeginMoveDrag
        Grid.SetColumn(drag, 1); grid.Children.Add(drag);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 6, 0) };
        buttons.Children.Add(CaptionButton("min", "最小化"));
        buttons.Children.Add(CaptionButton("max", "最大化"));
        buttons.Children.Add(CaptionButton("close", "关闭"));
        Grid.SetColumn(buttons, 2); grid.Children.Add(buttons);
        var highlight = new Border(); highlight.Classes.Add("aeroCaptionTopHighlight");
        Grid.SetColumnSpan(highlight, 3); grid.Children.Add(highlight);
        caption.Child = grid;
        caption.Width = 860;
        return caption;
    }

    private static void Fill(StackPanel host)
    {
        host.Children.Add(Caption("Fufucode · 修复登录回调竞态（active）", inactive: false));
        host.Children.Add(Caption("Fufucode · 修复登录回调竞态（inactive）", inactive: true));
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        foreach (var state in States)
        {
            var cell = new StackPanel { Spacing = 4 };
            cell.Children.Add(new TextBlock { Text = state, Foreground = Brushes.White, FontSize = 11 });
            var plate = new Border(); plate.Classes.Add("aeroCaption"); plate.Height = 32;
            var group = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            group.Children.Add(CaptionButton("min", "最小化", state));
            group.Children.Add(CaptionButton(state == "pressed" ? "restore" : "max", "最大化", state));
            group.Children.Add(CaptionButton("close", "关闭", state));
            plate.Child = group;
            cell.Children.Add(plate);
            strip.Children.Add(cell);
        }
        var single = new StackPanel { Spacing = 4 };
        single.Children.Add(new TextBlock { Text = "dialog single", Foreground = Brushes.White, FontSize = 11 });
        var singlePlate = new Border(); singlePlate.Classes.Add("aeroCaption"); singlePlate.Height = 32;
        var sb = CaptionButton("close", "关闭"); sb.Classes.Add("single");
        singlePlate.Child = sb; single.Children.Add(singlePlate);
        strip.Children.Add(single);
        host.Children.Add(strip);
    }

    // 状态强制：写入真实伪类（与指针/键盘驱动的是同一套选择器）。必须在挂入视觉树之后执行：
    // 首轮工装在挂树前设置 :pressed，实测挂树后只剩 :pointerover（红例，见记录 §4），所以改为挂树后强制。
    private static void ForceStates(StackPanel host)
    {
        var strip = host.Children.OfType<StackPanel>().Last();
        foreach (var b in strip.GetLogicalDescendantsOfType<Button>())
        {
            var pc = (IPseudoClasses)b.Classes;
            switch (b.Tag as string)
            {
                case "hover": pc.Set(":pointerover", true); break;
                case "pressed": pc.Set(":pointerover", true); pc.Set(":pressed", true); break;
                case "focus": pc.Set(":focus", true); pc.Set(":focus-visible", true); break;
            }
        }
    }

    private static IEnumerable<T> GetLogicalDescendantsOfType<T>(this Control root) where T : Control
        => root.GetVisualDescendants().OfType<T>();

    private static IBrush? Res(Control scope, string key) => scope.FindResource(scope.ActualThemeVariant, key) as IBrush;

    private static Border Plate(Button b) => b.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PART_Plate");

    private static void Verify(StackPanel host, string theme)
    {
        var captions = host.Children.OfType<Border>().ToList();
        var active = captions[0]; var inactive = captions[1];
        Check(active.ActualThemeVariant.ToString() == theme, $"{theme}: ThemeVariantScope resolves {theme}");
        Check(ReferenceEquals(active.Background, Res(active, "AeroCaptionBackground")) && active.Background is LinearGradientBrush g && g.GradientStops.Count == 4
              && g.GradientStops[1].Offset < g.GradientStops[2].Offset + 0.05 && Math.Abs(g.GradientStops[2].Offset - 0.5) < 1e-6,
            $"{theme}: active caption uses 4-stop gradient with hard split at 50%");
        Check(ReferenceEquals(inactive.Background, Res(inactive, "AeroCaptionBackgroundInactive")), $"{theme}: .inactive caption switches background");
        var activeTitle = active.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("aeroCaptionTitle"));
        var inactiveTitle = inactive.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("aeroCaptionTitle"));
        Check(activeTitle.Effect is DropShadowEffect && inactiveTitle.Effect is null, $"{theme}: title glow only when active");
        Check(!Equals(activeTitle.Foreground, inactiveTitle.Foreground), $"{theme}: inactive title foreground differs");

        var buttons = active.GetVisualDescendants().OfType<Button>().ToList();
        Check(buttons.Count == 3 && buttons.All(b => Math.Abs(b.Bounds.Width - 46) < 0.01 && Math.Abs(b.Bounds.Height - 32) < 0.01),
            $"{theme}: every caption button hit area is 46x32 (same as production CaptionAssetButton)");
        Check(buttons.All(b => Math.Abs(Plate(b).Bounds.Height - 21) < 0.01 && Plate(b).Bounds.Y == 0), $"{theme}: visual plate is 21px hung at top");
        Check(buttons.All(b => b.InputHitTest(new Point(23, 28)) is Visual hit && (hit == b || hit.GetVisualAncestors().Contains(b))), $"{theme}: area below plate (y=28) still hit-tests to the button");
        Check(buttons.Select(AutomationProperties.GetName).SequenceEqual(["最小化", "最大化", "关闭"]), $"{theme}: automation names preserved");
        Check(ReferenceEquals(Plate(buttons[2]).Background, Res(active, "AeroCaptionClose")), $"{theme}: close plate uses red gradient");

        var inactiveButtons = inactive.GetVisualDescendants().OfType<Button>().ToList();
        Check(inactiveButtons.All(b => ReferenceEquals(Plate(b).Background, Res(inactive, "AeroCaptionPlateInactive"))), $"{theme}: inactive caption fades all three plates");

        var strip = host.Children.OfType<StackPanel>().Last();
        var all = strip.GetVisualDescendants().OfType<Button>().ToList();
        Button Pick(string state, string kind) => all.First(b => (string?)b.Tag == state && b.Classes.Contains(kind) && !b.Classes.Contains("single"));
        Check(ReferenceEquals(Plate(Pick("hover", "min")).Background, Res(strip, "AeroCaptionPlateHover")), $"{theme}: :pointerover → PlateHover");
        Check(ReferenceEquals(Plate(Pick("pressed", "min")).Background, Res(strip, "AeroCaptionPlatePressed")), $"{theme}: :pressed overrides hover → PlatePressed");
        Check(ReferenceEquals(Plate(Pick("hover", "close")).Background, Res(strip, "AeroCaptionCloseHover")), $"{theme}: close:pointerover → CloseHover");
        Check(ReferenceEquals(Plate(Pick("pressed", "close")).Background, Res(strip, "AeroCaptionClosePressed")), $"{theme}: close:pressed → ClosePressed");
        var focusRing = Pick("focus", "min").GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PART_Focus");
        var normalRing = Pick("normal", "min").GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PART_Focus");
        Check(focusRing.Opacity == 1 && normalRing.Opacity == 0, $"{theme}: :focus-visible shows ring, normal hides it");
        var disabledGlyph = Pick("disabled", "min").GetVisualDescendants().OfType<Panel>().Single(x => x.Name == "PART_GlyphHost");
        Check(Math.Abs(disabledGlyph.Opacity - 0.45) < 1e-6, $"{theme}: :disabled dims glyph to 0.45");
        var restoreGlyph = Pick("pressed", "restore").GetVisualDescendants().OfType<ShapePath>().Single(x => x.Name == "PART_Glyph");
        Check(restoreGlyph.Data is not null, $"{theme}: restore class supplies glyph geometry");
    }

    // 真实无头指针输入（不经伪类强制的独立证据）：移到关闭键 → hover；按下 → pressed；抬起 → 一次 Click；移开 → 复原。
    private static void VerifyRealPointer()
    {
        var caption = Caption("pointer", inactive: false);
        var w = new Window { Width = 900, Height = 60, Content = caption };
        w.Show(); Pump();
        var close = caption.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("close"));
        var center = close.TranslatePoint(new Point(23, 10), w)!.Value;
        var normal = Plate(close).Background;
        w.MouseMove(center); Pump();
        Check(close.IsPointerOver && ReferenceEquals(Plate(close).Background, Res(close, "AeroCaptionCloseHover")), "real pointer: move onto close → CloseHover");
        var clicks = 0; close.Click += (_, _) => clicks++;
        w.MouseDown(center, MouseButton.Left); Pump();
        Check(close.IsPressed && ReferenceEquals(Plate(close).Background, Res(close, "AeroCaptionClosePressed")), "real pointer: press close → ClosePressed");
        w.MouseUp(center, MouseButton.Left); Pump();
        Check(clicks == 1, "real pointer: release raises exactly one Click (button semantics unchanged)");
        var below = close.TranslatePoint(new Point(23, 28), w)!.Value;
        w.MouseDown(below, MouseButton.Left); Pump(); w.MouseUp(below, MouseButton.Left); Pump();
        Check(clicks == 2, "real pointer: click at y=28 (below 21px plate) still activates button");
        w.MouseMove(new Point(10, 10)); Pump();
        Check(!close.IsPointerOver && ReferenceEquals(Plate(close).Background, normal), "real pointer: leave → back to normal plate");
        w.Close();
    }

    // DynamicResource 热切换：同一控件实例改主题后画刷必须换成另一套（不是 StaticResource 旧别名）
    private static void VerifyThemeHotSwap()
    {
        var scope = new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Light };
        var caption = Caption("hot swap", inactive: false);
        scope.Child = caption;
        var w = new Window { Width = 900, Height = 60, Content = scope };
        w.Show(); Pump();
        var light = caption.Background;
        var lightPlate = Plate(caption.GetVisualDescendants().OfType<Button>().First()).Background;
        scope.RequestedThemeVariant = ThemeVariant.Dark; Pump();
        Check(!ReferenceEquals(light, caption.Background) && ReferenceEquals(caption.Background, Res(caption, "AeroCaptionBackground")),
            "hot swap: caption background re-resolves to Dark brush on same instance");
        Check(!ReferenceEquals(lightPlate, Plate(caption.GetVisualDescendants().OfType<Button>().First()).Background), "hot swap: template plate re-resolves");
        caption.Classes.Add("inactive"); Pump();
        Check(ReferenceEquals(caption.Background, Res(caption, "AeroCaptionBackgroundInactive")), "hot swap: toggling .inactive at runtime applies");
        caption.Classes.Remove("inactive"); caption.Classes.Add("glass"); Pump();
        Check(ReferenceEquals(caption.Background, Res(caption, "AeroCaptionGlass")), "hot swap: .glass applies translucent caption brush");
        w.Close();
    }
}
