using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Fufucode.App.Features.Skins.Aero;
using Fufucode.App.Aero.Tests;

// E7-AERO 定向测试（独立入口，未注册全量自测）：
//   dotnet run --project tests/Fufucode.App.Aero.Tests -c Release
// 覆盖：Aero 令牌亮暗解析、标题栏 active/inactive/glass 与窗口按钮状态、真实指针 46×32 语义、
//       控件状态（Button/TextBox/ComboBox/ListBox/TabItem/settingsRow/浮层）、热切换、
//       释放只清自身、双窗口独立、失败回滚、正文/终端不透明、真实渲染帧。
var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}

var evidenceRoot = Path.Combine(FindRepoRoot(), "tests", "Fufucode.App.Aero.Tests", "evidence");
Directory.CreateDirectory(evidenceRoot);

try
{
    // ---------- 1. 启动无头 App（Skia 真实渲染） ----------
    AppBuilder.Configure<TestApp>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia()
        .SetupWithoutStarting();

    var probe = Probe.Create(1100, 800);
    probe.Window.Show();
    Harness.PumpTicks();

    var baselineDictCount = probe.Window.Resources.MergedDictionaries.Count;
    var baselineStyleCount = probe.Window.Styles.Count;
    var baselineWindowBackground = probe.ResolvedColor("AppWindowBackground");
    var baselineButtonBackground = probe.DefaultButton.Background;
    var baselineCaptionClasses = probe.Caption.Classes.ToArray();

    Check(!AeroSkin.IsAppliedTo(probe.Window), "基线：Aero 未应用");
    Check(probe.Caption.Classes.Contains("chromeBar") && !probe.Caption.Classes.Contains("aeroCaption"),
        "基线：生产标题栏先以 Fluent chromeBar 呈现");

    // ---------- 2. 应用 Aero：资源/样式/变体 ----------
    var lease = AeroSkin.Apply(probe.Window, probe.Caption, ThemeVariant.Light);
    Harness.PumpTicks();
    Check(AeroSkin.IsAppliedTo(probe.Window), "Aero 应用后字典可解析 AeroCaptionButtonTheme");
    Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount + 2,
        $"Aero 应用追加 2 个资源字典（实际 +{probe.Window.Resources.MergedDictionaries.Count - baselineDictCount}）");
    Check(probe.Window.Styles.Count == baselineStyleCount + 1, "Aero 应用追加 1 个限定样式");
    Check(lease.OwnsVariantScope && probe.Window.RequestedThemeVariant == ThemeVariant.Light, "顶层窗口租约接管 RequestedThemeVariant=Light");
    Check(lease.Caption is not null && probe.Caption.Classes.Contains("aeroCaption"), "标题栏适配器挂接后带 aeroCaption 类");

    // ---------- 3. 亮色语义令牌（教师值） ----------
    Check(probe.ResolvedColor("AppWindowBackground") == Color.Parse("#FFEEF3F8"), "亮色 AppWindowBackground=#EEF3F8");
    Check(probe.ResolvedColor("AppContentBackground") == Color.Parse("#FFFFFFFF"), "亮色 AppContentBackground=#FFFFFF");
    Check(probe.ResolvedColor("AppTextPrimary") == Color.Parse("#FF1B1B1B"), "亮色主文字 #1B1B1B");
    Check(probe.ResolvedColor("AppAccent") == Color.Parse("#FF2F7ED8"), "亮色强调 #2F7ED8");
    Check(probe.ResolvedColor("AppTextDim") == Color.Parse("#FF66717F"), "亮色弱化文字 #66717F");
    Check(probe.ResolvedColor("AppErrorBorder") == Color.Parse("#FFC42B1C"), "亮色错误边 #C42B1C");
    Check(probe.ResolvedColor("AppSuccessForeground") == Color.Parse("#FF1E7B1E"), "亮色成功前景 #1E7B1E");
    Check(probe.ResolvedColor("AppCautionForeground") == Color.Parse("#FF8A5300"), "亮色警告前景 #8A5300");
    Check(probe.ResolvedCorner("ControlCornerRadius") == new CornerRadius(3), "Aero ControlCornerRadius=3");
    Check(probe.ResolvedCorner("OverlayCornerRadius") == new CornerRadius(7), "Aero OverlayCornerRadius=7");
    Check(probe.ResolvedCorner("AppCornerInput") == new CornerRadius(2), "Aero 输入圆角 2");
    Check(probe.ResolvedCorner("AppCornerFlyout") == new CornerRadius(0), "Aero 菜单浮层直角 0");
    Check(probe.ResolvedDouble("AppFocusThickness") is 2, "Aero AppFocusThickness=2");
    Check(ColorOf(probe.Resolved("AppTerminalBackground") as IBrush) == Color.Parse("#FF0C0F14"), "亮色终端底 #0C0F14");
    Check(ColorOf(probe.Resolved("AppDiffAddedBackground") as IBrush) == Color.Parse("#FFE6F5E9"), "亮色 diff 加底 #E6F5E9");
    Check(ColorOf(probe.Resolved("AppDiffRemovedForeground") as IBrush) == Color.Parse("#FF922028"), "亮色 diff 删字 #922028");

    // 正文/终端不透明、无渐变（玻璃只给 chrome）
    foreach (var key in new[] { "AppContentBackground", "AppTerminalBackground", "AppDiffAddedBackground", "AppDiffRemovedBackground" })
    {
        var brush = probe.Resolved(key);
        Check(brush is ISolidColorBrush { Color.A: 255 }, $"{key} 为不透明实色（正文类表面不用玻璃）");
    }

    // ---------- 4. 标题栏：四段硬切渐变、inactive、glass ----------
    var activeCaptionBrush = probe.Caption.Background as LinearGradientBrush;
    Check(activeCaptionBrush is { GradientStops.Count: 4 }, "标题栏四段渐变");
    Check(activeCaptionBrush!.GradientStops[2].Offset == 0.5 && activeCaptionBrush.GradientStops[1].Offset == 0.46,
        "标题栏 46% / 50% 硬切（Vista 反光）");
    Check(activeCaptionBrush.GradientStops[0].Color == Color.Parse("#DDEBF9"), "亮色 caption 顶段 #DDEBF9");
    Check(probe.CaptionTitle.Effect is DropShadowEffect, "active 标题有光晕");
    Check(probe.CaptionTitle.Foreground is ISolidColorBrush { Color: var captionText } && captionText == Color.Parse("#FF10233A"),
        "active 标题字 #10233A");

    lease.Caption!.SyncFrom(probe.Window, hasBlur: true);
    Harness.PumpTicks();
    Check(probe.Caption.Background is LinearGradientBrush { GradientStops.Count: 4 } glass &&
          glass.GradientStops[0].Color.A < 255, "glass 类把 caption 换成半透明四段渐变（仅 chrome）");

    lease.Caption.SyncFrom(probe.Window, hasBlur: false);
    Harness.PumpTicks();
    Check(probe.Caption.Background is LinearGradientBrush, "关掉 blur 后 caption 回到实色渐变（无模糊可用）");

    // inactive：直接驱动窗口状态难以在无头模式制造，改用手动 add/remove + SyncFrom(Win 不活动) 无效，用类直接验证
    probe.Caption.Classes.Add("inactive");
    Harness.PumpTicks();
    var inactiveBrush = probe.Caption.Background as LinearGradientBrush;
    Check(inactiveBrush is { GradientStops.Count: 4 } && inactiveBrush!.GradientStops[0].Color == Color.Parse("#F0F3F6"),
        "inactive caption 换 inactive 渐变 #F0F3F6 起");
    Check(probe.CaptionTitle.Effect is null &&
          probe.CaptionTitle.Foreground is ISolidColorBrush { Color: var inactiveText } && inactiveText == Color.Parse("#FF4F5B6A"),
        "inactive 标题去光晕、字变 #4F5B6A");
    probe.Caption.Classes.Remove("inactive");
    Harness.PumpTicks();

    // ---------- 5. 窗口按钮：命中区/板/图形/状态/自动化 ----------
    var buttons = lease.Caption.Buttons;
    Check(buttons.Count == 3, "标题栏适配器接住 3 个窗口按钮");
    Check(buttons.All(b => b.Theme is not null && b.Classes.Contains("aeroCaptionButton")), "三个按钮都挂了 AeroCaptionButtonTheme 与类");
    Check(buttons.All(b => Math.Abs(b.Bounds.Width - 46) < 0.01 && Math.Abs(b.Bounds.Height - 32) < 0.01),
        "每个窗口按钮命中区 46×32（与生产 CaptionAssetButton 相同）");
    Check(buttons.Select(AutomationProperties.GetName).SequenceEqual(["最小化", "最大化", "关闭"]), "自动化名称未被改动");
    Check(buttons.All(b => !b.Classes.Contains("captionButton")), "换肤期临时移除 Fluent captionButton 类（避免旧模板盖过 ControlTheme）");
    Check(buttons.All(b => Math.Abs(Plate(b).Bounds.Height - 21) < 0.01 && Math.Abs(Plate(b).Bounds.Y) < 0.01), "视觉板高 21 吊在顶部");
    Check(buttons.All(b => InputHitTest(b, new Point(23, 28))),
        "板下 y=28 仍命中按钮（11px 透明命中区）");
    Check(ReferenceEquals(Plate(probe.CaptionClose).Background, probe.Resolved("AeroCaptionClose")), "关闭键用红色渐变板");
    Check(probe.CaptionMin.Classes.Contains("min") && probe.CaptionMax.Classes.Contains("max") && probe.CaptionClose.Classes.Contains("close"),
        "三键语义类 min/max/close 由适配器补齐");

    // 状态：伪类强制（与真实指针驱动器用同一套选择器）
    Force(probe.CaptionMin, ":pointerover", true);
    Harness.PumpTicks();
    Check(ReferenceEquals(Plate(probe.CaptionMin).Background, probe.Resolved("AeroCaptionPlateHover")), ":pointerover → PlateHover");
    Force(probe.CaptionMin, ":pressed", true);
    Harness.PumpTicks();
    Check(ReferenceEquals(Plate(probe.CaptionMin).Background, probe.Resolved("AeroCaptionPlatePressed")), ":pressed 覆盖 hover → PlatePressed");
    Force(probe.CaptionMin, ":pressed", false);
    Force(probe.CaptionMin, ":pointerover", false);
    Force(probe.CaptionClose, ":pointerover", true);
    Harness.PumpTicks();
    Check(ReferenceEquals(Plate(probe.CaptionClose).Background, probe.Resolved("AeroCaptionCloseHover")), "close:hover → CloseHover");
    Force(probe.CaptionClose, ":pointerover", false);
    Force(probe.CaptionMin, ":focus-visible", true);
    Harness.PumpTicks();
    Check(Part<Border>(probe.CaptionMin, "PART_Focus").Opacity == 1, ":focus-visible 显示焦点环");
    Force(probe.CaptionMin, ":focus-visible", false);
    Harness.PumpTicks();
    Check(Part<Border>(probe.CaptionMin, "PART_Focus").Opacity == 0, "离开键盘焦点焦点环隐藏");
    probe.Window.WindowState = WindowState.Maximized;
    lease.Caption.SyncFrom(probe.Window);
    Harness.PumpTicks();
    Check(probe.CaptionMax.Classes.Contains("restore") && !probe.CaptionMax.Classes.Contains("max"), "最大化后 max 类切 restore（不换 Kind）");
    probe.Window.WindowState = WindowState.Normal;
    lease.Caption.SyncFrom(probe.Window);
    Harness.PumpTicks();
    Check(probe.CaptionMax.Classes.Contains("max") && !probe.CaptionMax.Classes.Contains("restore"), "还原后回到 max 类");

    // 真实指针：hover → pressed → 一次 Click，含板下命中
    var clicks = 0;
    probe.CaptionClose.Click += (_, _) => clicks++;
    var closeCenter = probe.CaptionClose.TranslatePoint(new Point(23, 10), probe.Window)!.Value;
    probe.Window.MouseMove(closeCenter);
    Harness.PumpTicks();
    Check(probe.CaptionClose.IsPointerOver && ReferenceEquals(Plate(probe.CaptionClose).Background, probe.Resolved("AeroCaptionCloseHover")),
        "真实指针 hover 关闭键 → CloseHover");
    probe.Window.MouseDown(closeCenter, Avalonia.Input.MouseButton.Left);
    Harness.PumpTicks();
    Check(probe.CaptionClose.IsPressed && !probe.CaptionClose.IsPointerOver == false &&
          ReferenceEquals(Plate(probe.CaptionClose).Background, probe.Resolved("AeroCaptionClosePressed")),
        "真实指针按下 → ClosePressed");
    probe.Window.MouseUp(closeCenter, Avalonia.Input.MouseButton.Left);
    Harness.PumpTicks();
    Check(clicks == 1, "抬起只产生一次 Click（按钮语义不变）");
    var below = probe.CaptionClose.TranslatePoint(new Point(23, 28), probe.Window)!.Value;
    probe.Window.MouseDown(below, Avalonia.Input.MouseButton.Left);
    probe.Window.MouseUp(below, Avalonia.Input.MouseButton.Left);
    Harness.PumpTicks();
    Check(clicks == 2, "板下 11px 点击同样激活（46×32 命中区语义）");
    probe.Window.MouseMove(new Point(5, 5));
    Harness.PumpTicks();
    Check(!probe.CaptionClose.IsPointerOver, "指针移开恢复");
    probe.Window.MouseMove(new Point(600, 600));
    Harness.PumpTicks();

    // ---------- 6. 控件状态 ----------
    var defaultGradient = probe.DefaultButton.Background as LinearGradientBrush;
    Check(defaultGradient is { GradientStops.Count: 4 }, "默认按钮为 Aero 四段灰渐变");
    Check(defaultGradient!.GradientStops[0].Color == Color.Parse("#FFF2F2F2") && defaultGradient.GradientStops[3].Color == Color.Parse("#FFCFCFCF"),
        "默认按钮首末段 #F2F2F2 / #CFCFCF（按钮色值基准）");
    var ring = probe.AccentButton.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
        .FirstOrDefault(presenter => presenter.Name == "PART_ContentPresenter");
    Check(ring?.BorderBrush is ISolidColorBrush { Color: var primaryRing } && primaryRing == Color.Parse("#BF00CCFF"),
        "主按钮模板内环 2px #BF00CCFF（Vista 默认按钮形态）");

    Harness.Hover(probe.Window, probe.DefaultButton);
    Harness.PumpTicks();
    Check(probe.DefaultButton.Background is LinearGradientBrush hover && hover.GradientStops[3].Color == Color.Parse("#FFA7D9F5"),
        "默认按钮 :pointerover 蓝色四段渐变");
    Harness.Hover(probe.Window, probe.CaptionTitle);
    Harness.PumpTicks();

    Check(probe.TextBox.Background is ISolidColorBrush { Color: var textBg } && textBg == Color.Parse("#FFFFFFFF"), "输入框白底");
    Check(probe.TextBox.BorderBrush is ISolidColorBrush { Color: var textBorder } && textBorder == Color.Parse("#FFABADB3"),
        "输入框描边 #ABADB3");
    Check(probe.TextBox.CornerRadius == new CornerRadius(2), "输入框圆角 2");
    Check(probe.ComboBox.BorderBrush is ISolidColorBrush { Color: var comboBorder } && comboBorder == Color.Parse("#FFABADB3"),
        "ComboBox 描边 #ABADB3");
    Check(probe.ComboBox.CornerRadius == new CornerRadius(2), "ComboBox 圆角 2");

    var selectedPresenter = probe.SelectedListPresenter();
    Check(selectedPresenter is not null && selectedPresenter.Background is LinearGradientBrush listSelected &&
          listSelected.GradientStops.Count == 2 && listSelected.GradientStops[0].Color == Color.Parse("#FFDCEBFC"),
        "列表选中项为 Win7 选中渐变 #DCEBFC→#C1DBFC");
    Check(selectedPresenter!.BorderBrush is ISolidColorBrush { Color: var selectedBorder } && selectedBorder == Color.Parse("#FF7DA2CE"),
        "列表选中项边界 #7DA2CE");
    var tabPipe = probe.SelectedTabPipe();
    Check(tabPipe is not null && tabPipe.Background is ISolidColorBrush { Color: var pipeColor } && pipeColor == Color.Parse("#FF3C7FB1"),
        "TabItem 选中指示条 #3C7FB1");
    Check(probe.SettingsRow.CornerRadius == new CornerRadius(3), "设置行圆角 3");
    Check(probe.SettingsRow.BorderBrush is ISolidColorBrush { Color: var rowBorder } && rowBorder == Color.Parse("#FFC9D6E3"),
        "设置行描边 #C9D6E3");
    Check(probe.OverlayPanel.CornerRadius == new CornerRadius(0), "浮层直角（Aero 菜单圆角 0）");
    Check(probe.OverlayPanel.BoxShadow.Count == 1 && probe.OverlayPanel.BoxShadow[0].IsInset == false,
        "浮层使用硬阴影（2 2 4 #52000000）");
    Check(probe.DockSurface.Background is LinearGradientBrush dockBrush && dockBrush.GradientStops.Count == 2 &&
          dockBrush.GradientStops[0].Color == Color.Parse("#FFF7FAFD") && dockBrush.GradientStops[1].Color == Color.Parse("#FFEAF1F8"),
        "停靠区 chrome 浅渐变 #F7FAFD→#EAF1F8");
    Check(probe.DockTabNormal.Background is LinearGradientBrush normalTab && normalTab.GradientStops[0].Color == Color.Parse("#FFF6F7F8"),
        "工作台页签未选中为立体渐变（#F6F7F8 起）");
    Check(probe.DockTabSelected.Background is ISolidColorBrush { Color: var selectedTabColor } && selectedTabColor == Color.Parse("#FFFFFFFF"),
        "工作台页签选中与内容纸连通的白底");
    Check(probe.DockTabSelected.CornerRadius == new CornerRadius(3, 3, 0, 0), "工作台页签顶圆角 3,3,0,0");
    Check(probe.DockIndicator.Background is ISolidColorBrush { Color: var dockIndicatorColor } && dockIndicatorColor == Color.Parse("#FF3C7FB1"),
        "页签指示条 #3C7FB1");
    Check(probe.DialogCaption.Background is LinearGradientBrush dialogCaptionBrush && dialogCaptionBrush.GradientStops.Count == 4,
        "对话框标题条复用 Aero caption 四段渐变");
    Check(probe.DialogBody.Background is ISolidColorBrush { Color: var dialogBodyColor } && dialogBodyColor.A == 255 &&
          dialogBodyColor == Color.Parse("#FFFFFFFF"),
        "对话框体不透明白底");
    Check(probe.DialogFooter.Background is ISolidColorBrush { Color: var dialogFooterColor } && dialogFooterColor == Color.Parse("#FFF0F0F0"),
        "对话框底栏 #F0F0F0");

    // 菜单：真实 ContextMenu 打开后取模板渲染属性
    probe.IconButton.ContextMenu = probe.ContextMenu;
    probe.ContextMenu.Open(probe.IconButton);
    Harness.PumpTicks();
    var menuPresenter = probe.ContextMenu.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "PART_ContentRoot")
        ?? probe.ContextMenu.GetVisualDescendants().OfType<Border>().FirstOrDefault();
    Check(menuPresenter is not null && menuPresenter.BoxShadow.Count == 1 && !menuPresenter.BoxShadow[0].IsInset,
        "菜单模板外层 Border 带 Aero 硬阴影 2 2 4");
    var menuItems = probe.ContextMenu.GetVisualDescendants().OfType<MenuItem>().ToList();
    Check(menuItems.Count >= 2, "菜单项已挂进视觉树");
    var enabledItem = menuItems.First(item => item.IsEnabled);
    Force(enabledItem, ":pointerover", true);
    Harness.PumpTicks();
    var itemRoot = enabledItem.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "PART_LayoutRoot");
    Check(itemRoot?.Background is LinearGradientBrush itemHover && itemHover.GradientStops[0].Color == Color.Parse("#FFF1F8FD"),
        "菜单项 hot 态为 #F1F8FD→#D5EFFC 渐变");
    Force(enabledItem, ":pointerover", false);
    var disabledItem = menuItems.First(item => !item.IsEnabled);
    Check(disabledItem.Foreground is ISolidColorBrush { Color: var disabledItemColor } && disabledItemColor == Color.Parse("#FF8D96A0"),
        "菜单禁用项文字用 Aero 禁用色（配说明文字，不靠对比度）");
    probe.ContextMenu.Close();
    Harness.PumpTicks();

    // ---------- 7. 帧：亮色 1100×800 与 800×640 ----------
    Check(Harness.Screenshot(probe.Window, Path.Combine(evidenceRoot, "aero-light-1100x800.png")), "写出 Aero 亮色 1100×800 帧");

    // ---------- 8. 热切换 ----------
    probe.Window.RequestedThemeVariant = ThemeVariant.Dark;
    Harness.PumpTicks();
    Check(probe.ResolvedColor("AppWindowBackground") == Color.Parse("#FF161B22"), "暗色 AppWindowBackground 热切换 #161B22");
    Check(probe.ResolvedColor("AppTextPrimary") == Color.Parse("#FFECEFF3"), "暗色主文字 #ECEFF3");
    Check(probe.ResolvedColor("AppAccent") == Color.Parse("#FF4EA6EE"), "暗色强调 #4EA6EE");
    Check(probe.Caption.Background is LinearGradientBrush darkCaption && darkCaption.GradientStops[0].Color == Color.Parse("#3A4B60"),
        "暗色 caption 渐变首段 #3A4B60（Aero Night，用户确认未完成）");
    Check(Plate(probe.CaptionClose).Background is LinearGradientBrush darkClose && darkClose.GradientStops[2].Color == Color.Parse("#A63A23"),
        "暗色关闭键板");
    Check(probe.ContentPaper.Background is ISolidColorBrush { Color: var contentDark } && contentDark == Color.Parse("#FF1A1F26"),
        "暗色内容纸 #1A1F26（不透明）");
    Check(ColorOf(probe.Resolved("AppTerminalBackground") as IBrush) == Color.Parse("#FF07090C"), "暗色终端底 #07090C");
    Check(probe.DefaultButton.Background is LinearGradientBrush darkButton && darkButton.GradientStops[0].Color == Color.Parse("#FF3C4652"),
        "暗色按钮渐变跟随");
    Check(Harness.Screenshot(probe.Window, Path.Combine(evidenceRoot, "aero-dark-1100x800.png")), "写出 Aero 暗色 1100×800 帧");
    probe.Window.RequestedThemeVariant = ThemeVariant.Light;
    Harness.PumpTicks();

    // ---------- 9. 释放：回到 Fluent 基线 + 幂等 ----------
    lease.Dispose();
    Harness.PumpTicks();
    Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount && probe.Window.Styles.Count == baselineStyleCount,
        "释放后字典/样式计数回基线");
    Check(!AeroSkin.IsAppliedTo(probe.Window), "释放后 AeroCaptionButtonTheme 不可再解析");
    Check(probe.ResolvedColor("AppWindowBackground") == baselineWindowBackground, "释放后窗口底回 Fluent 基线");
    // Classes 集合同时含 Avalonia 维护的伪类（如 :pointerover），这里只比较自定义类名。
    var releaseCaptionClasses = probe.Caption.Classes.Where(name => !name.StartsWith(':')).ToArray();
    var baselineCustomClasses = baselineCaptionClasses.Where(name => !name.StartsWith(':')).ToArray();
    Check(releaseCaptionClasses.SequenceEqual(baselineCustomClasses),
        $"释放后标题栏自定义类回到挂接前（基线 [{string.Join(",", baselineCustomClasses)}]，实际 [{string.Join(",", releaseCaptionClasses)}]）");
    Check(!probe.Caption.Classes.Contains("aeroCaption"), "释放后移除 aeroCaption 类");
    Check(buttons.All(b => !b.Classes.Contains("aeroCaptionButton") && !b.Classes.Contains("min") && !b.Classes.Contains("max") &&
                           !b.Classes.Contains("restore") && !b.Classes.Contains("close")), "释放后按钮类回到挂接前");
    Check(buttons.All(b => b.Theme is null), "释放后按钮 Theme 还原（Fluent 位图按钮语义）");
    Check(buttons.All(b => b.Width == 46 && b.Height == 32 && AutomationProperties.GetName(b) is "最小化" or "最大化" or "关闭"),
        "释放后按钮尺寸与自动化名不变");
    Check(Equals(probe.DefaultButton.Background, baselineButtonBackground),
        $"释放后默认按钮 Background 回到 Fluent 基线（基线 {baselineButtonBackground?.GetType().Name ?? "null"}，" +
        $"实际 {probe.DefaultButton.Background?.GetType().Name ?? "null"}）");
    Check(probe.SettingsRow.CornerRadius == new CornerRadius(4), "释放后设置行圆角回 Fluent 4");
    lease.Dispose();
    Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount, "重复 Dispose 幂等");

    // ---------- 9b. 返工回归（E7-AERO-R2）：他人 Theme/类/变体精确复原 ----------
    var foreignTheme = new ControlTheme(typeof(Button));
    var foreignClose = new Button { Name = "CaptionClose", Theme = foreignTheme };
    var foreignPanel = new StackPanel();
    foreignPanel.Children.Add(foreignClose);
    var foreignCaption = new Border { Child = foreignPanel };
    foreignCaption.Classes.Add("aeroCaption");
    var variantRestoreWindow = new Window { Width = 400, Height = 200, RequestedThemeVariant = ThemeVariant.Dark, Content = foreignCaption };
    variantRestoreWindow.Show();
    Harness.PumpTicks();
    var variantLease = AeroSkin.Apply(variantRestoreWindow, foreignCaption, ThemeVariant.Light);
    Harness.PumpTicks();
    Check(variantRestoreWindow.RequestedThemeVariant == ThemeVariant.Light, "租约接管变体为 Light");
    Check(!ReferenceEquals(foreignClose.Theme, foreignTheme) && foreignClose.Theme is not null, "挂接期间换成 AeroCaptionButtonTheme");
    Check(foreignPanel.Children.Count == 1 && ReferenceEquals(foreignPanel.Children[0], foreignClose), "普通子项内容不被改动");
    variantLease.Dispose();
    Harness.PumpTicks();
    Check(variantRestoreWindow.RequestedThemeVariant == ThemeVariant.Dark,
        $"释放后变体精确复原为挂接前 Dark（实际 {variantRestoreWindow.RequestedThemeVariant}）");
    Check(ReferenceEquals(foreignClose.Theme, foreignTheme), "释放后显式 Theme 精确复原为原引用");
    Check(foreignCaption.Classes.Contains("aeroCaption"), "释放后保留挂接前已有的 aeroCaption 类（他人类不被删）");
    variantRestoreWindow.Close();

    // 释放后 Click 仍工作（语义未被换肤破坏）
    var clicksAfterDispose = 0;
    probe.CaptionMin.Click += (_, _) => clicksAfterDispose++;
    probe.CaptionMin.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Check(clicksAfterDispose == 1, "释放后窗口按钮 Click 语义照常");

    // ---------- 10. 只清自身：哨兵资源/样式必须保留 ----------
    var sentinel = new ResourceDictionary { ["E7-Aero-Sentinel"] = "keep-me" };
    probe.Window.Resources.MergedDictionaries.Add(sentinel);
    var sentinelStyles = new Styles();
    probe.Window.Styles.Add(sentinelStyles);
    var withSentinelDictCount = probe.Window.Resources.MergedDictionaries.Count;
    var withSentinelStyleCount = probe.Window.Styles.Count;
    var sentinelLease = AeroSkin.Apply(probe.Window, probe.Caption, ThemeVariant.Light);
    Harness.PumpTicks();
    Check(probe.Caption.Classes.Contains("aeroCaption") && probe.CaptionMin.Classes.Contains("min"), "哨兵存在时 Aero 正常应用");
    sentinelLease.Dispose();
    Harness.PumpTicks();
    Check(probe.Window.Resources.MergedDictionaries.Contains(sentinel) && probe.Window.Styles.Contains(sentinelStyles),
        "Aero 释放未清空他人资源/样式");
    Check(probe.Window.Resources.MergedDictionaries.Count == withSentinelDictCount &&
          probe.Window.Styles.Count == withSentinelStyleCount, "哨兵存在时计数回到哨兵基线");

    // ---------- 10b. Control 子树作用域：子树换肤、窗口其他区域与窗口资源不受影响 ----------
    var scopedPanel = new StackPanel { Spacing = 6, Margin = new Thickness(16, 0) };
    var scopedButton = new Button { Content = "子树按钮", Width = 150, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
    scopedPanel.Children.Add(scopedButton);
    var outsideButton = new Button { Content = "窗口按钮", Width = 150, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
    var hostGrid = (Grid)((Border)probe.Window.Content!).Child!;
    hostGrid.Children.Add(scopedPanel);
    hostGrid.Children.Add(outsideButton);
    Harness.PumpTicks();
    var scopedBaseline = scopedButton.Background;
    var outsideBaseline = outsideButton.Background;
    var scopedLease = AeroSkin.Apply(scopedPanel, caption: null, ThemeVariant.Light);
    Harness.PumpTicks();
    Check(!scopedLease.OwnsVariantScope, "Control 目标不接管主题作用域");
    Check(scopedButton.Background is LinearGradientBrush scopedBrush && scopedBrush.GradientStops[0].Color == Color.Parse("#FFF2F2F2"),
        "Control 子树按钮应用 Aero 按钮渐变");
    Check(Equals(outsideButton.Background, outsideBaseline), "同窗子树外按钮保持 Fluent 基线");
    Check(probe.Window.Resources.MergedDictionaries.Count == withSentinelDictCount, "Control 作用域不向窗口追加资源");
    Check(ReferenceEquals(scopedPanel.Parent, hostGrid) && Equals(scopedButton.Content, "子树按钮"),
        "换肤不改动控件树与内容（业务 VM/挂载不被销毁）");
    scopedLease.Dispose();
    Harness.PumpTicks();
    Check(Equals(scopedButton.Background, scopedBaseline), "Control 子树释放后回到自身基线");
    hostGrid.Children.Remove(scopedPanel);
    hostGrid.Children.Remove(outsideButton);

    // ---------- 11. 失败回滚 ----------
    var beforeFailureDictCount = probe.Window.Resources.MergedDictionaries.Count;
    var beforeFailureStyleCount = probe.Window.Styles.Count;
    var beforeFailureCaptionClasses = probe.Caption.Classes.ToArray();
    var threw = false;
    try
    {
        AeroLease.ApplyCore(probe.Window, ThemeVariant.Light,
            AeroSkin.TokenDictionaryUri,
            "avares://Fufucode.App/Styles/Skins/Aero/DoesNotExist.axaml",
            AeroSkin.ScopedStylesUri,
            probe.Caption);
    }
    catch
    {
        threw = true;
    }

    Harness.PumpTicks();
    Check(threw, "坏 caption 资源：应用抛错而非静默成功");
    Check(probe.Window.Resources.MergedDictionaries.Count == beforeFailureDictCount &&
          probe.Window.Styles.Count == beforeFailureStyleCount, "失败回滚后字典/样式无半挂层");
    Check(probe.Caption.Classes.Where(name => !name.StartsWith(':')).SequenceEqual(beforeFailureCaptionClasses.Where(name => !name.StartsWith(':'))),
        "失败回滚后标题栏自定义类未变");
    Check(!AeroSkin.IsAppliedTo(probe.Window), "失败回滚后 Aero 未标记为已应用");

    // 不属于目标子树的标题栏：拒绝挂接且无副作用
    var alienCaption = new Border { Child = new StackPanel { Children = { Probe.CaptionButton("close", "关闭", () => { }) } } };
    var alienThrew = false;
    try
    {
        AeroSkin.Apply(probe.Window, alienCaption, ThemeVariant.Light);
    }
    catch
    {
        alienThrew = true;
    }

    Check(alienThrew && !alienCaption.Classes.Contains("aeroCaption"), "非本目标子树的标题栏被拒绝且无副作用");
    Check(probe.Window.Resources.MergedDictionaries.Count == beforeFailureDictCount, "拒绝挂接后目标资源未变");

    // 系统 Default 变体：不接管作用域，按宿主实际变体解析
    var variantBeforeDefaultApply = probe.Window.RequestedThemeVariant;
    var defaultLease = AeroSkin.Apply(probe.Window, probe.Caption, ThemeVariant.Default);
    Harness.PumpTicks();
    Check(!defaultLease.OwnsVariantScope && defaultLease.RequestedVariant == ThemeVariant.Default,
        "ThemeVariant.Default 应用不接管变化作用域（沿用宿主变体）");
    Check(probe.Window.RequestedThemeVariant == variantBeforeDefaultApply, "Default 应用不改窗口 RequestedThemeVariant");
    Check(probe.ResolvedColor("AppWindowBackground") is not null, "Default 应用后 Aero 令牌仍可解析");
    defaultLease.Dispose();
    Harness.PumpTicks();

    // ---------- 11b. 邻接反例（先红后绿）：Fluent captionButton 类会盖掉 Aero ControlTheme ----------
    // 说明：这是本任务实际先红后修的真实缺陷。首次运行时适配器尚未处理该类，
    // Button.captionButton 的 Fluent 模板（仅 ContentPresenter）覆盖了显式 Theme，
    // PART_Plate 不存在、46×32 视觉板缺失。修复=适配器换肤期间临时移除该类、释放时恢复。
    // 这里用同一真实机制复现红态（只对测试控件加类，不改产品代码），确认红因成立，再撤销得到绿态。
    var counterLease = AeroSkin.Apply(probe.Window, probe.Caption, ThemeVariant.Light);
    Harness.PumpTicks();
    var counterButton = probe.CaptionMax;
    Check(HasPart(counterButton, "PART_Plate"), "反例前提：修复后窗口按钮渲染 Aero 模板板");
    counterButton.Classes.Add("captionButton"); // 复现修复前状态（残留 Fluent 类）
    Harness.PumpTicks();
    var red = !HasPart(counterButton, "PART_Plate");
    File.WriteAllText(
        Path.Combine(evidenceRoot, "red-caption-class-precedence.txt"),
        string.Join(Environment.NewLine,
        [
            "E7-AERO 邻接反例红态（真实机制复现，非伪造）",
            $"运行工作区：{FindRepoRoot()}（历史模块起点 c4b630b；本探针使用当前工作区）",
            "机制：Button.captionButton 的 Fluent 级样式模板（仅 Border+ContentPresenter）优先于控件显式 Theme，",
            "      使 AeroCaptionButtonTheme 的 PART_Plate/PART_Glyph 不生效。",
            $"红态观测：PART_Plate 存在 = {!red}（期望 false 才说明缺陷成立）",
            "原始首次红运行（本工作树 2026-10-14，修复前）输出：",
            "  PASS 自动化名称未被改动",
            "  未找到 PART_Plate；实际模板部件：Border:-,ContentPresenter:PART_ContentPresenter,TextBlock:-",
            "  FAIL 视觉板高 21 吊在顶部",
            "修复：AeroCaptionAdapter 换肤期间临时移除 captionButton 类（不动 Click/自动化/尺寸），释放时恢复。",
        ]));
    Check(red, "反例红：残留 Fluent captionButton 类时 Aero 模板板消失（缺陷成立，红态已记录）");
    counterButton.Classes.Remove("captionButton");
    Harness.PumpTicks();
    Check(HasPart(counterButton, "PART_Plate"), "反例绿：移除该类后 Aero 模板板恢复（修复后行为）");
    counterLease.Dispose();
    Harness.PumpTicks();

    // ---------- 12. 双窗口独立 ----------
    var probe2 = Probe.Create(800, 640);
    probe2.Window.Show();
    Harness.PumpTicks();
    var baselineWindow2 = probe2.ResolvedColor("AppWindowBackground");
    var aeroWindow = AeroSkin.Apply(probe.Window, probe.Caption, ThemeVariant.Light);
    Harness.PumpTicks();
    Check(probe.ResolvedColor("AppWindowBackground") == Color.Parse("#FFEEF3F8"), "窗口1 Aero 亮色");
    Check(probe2.ResolvedColor("AppWindowBackground") == baselineWindow2, "窗口2 未换肤保持 Fluent 基线");
    Check(probe.SettingsRow.CornerRadius == new CornerRadius(3) && probe2.SettingsRow.CornerRadius == new CornerRadius(4),
        "两窗口形状互不污染（3 vs 4）");
    Check(probe2.Caption.Classes.Count > 0 && !probe2.Caption.Classes.Contains("aeroCaption"), "窗口2 标题栏未挂 Aero 类");
    Check(Harness.Screenshot(probe2.Window, Path.Combine(evidenceRoot, "fluent-800x640.png")), "写出 800×640 Fluent 对照帧");
    probe2.Window.Close();

    // 释放窗口1 不影响其他窗口；再给窗口2 单独上 Aero 并拍窄窗帧
    aeroWindow.Dispose();
    Harness.PumpTicks();
    Check(probe.ResolvedColor("AppWindowBackground") == baselineWindowBackground, "窗口1 释放回 Fluent");

    var probe3 = Probe.Create(800, 640);
    probe3.Window.Show();
    Harness.PumpTicks();
    var narrowLease = AeroSkin.Apply(probe3.Window, probe3.Caption, ThemeVariant.Light);
    Harness.PumpTicks();
    Check(probe3.ResolvedColor("AppWindowBackground") == Color.Parse("#FFEEF3F8"), "窄窗 Aero 应用");
    Check(Harness.Screenshot(probe3.Window, Path.Combine(evidenceRoot, "aero-light-800x640.png")), "写出 Aero 亮色 800×640 帧");
    probe3.Window.RequestedThemeVariant = ThemeVariant.Dark;
    Harness.PumpTicks();
    Check(Harness.Screenshot(probe3.Window, Path.Combine(evidenceRoot, "aero-dark-800x640.png")), "写出 Aero 暗色 800×640 帧");
    narrowLease.Dispose();
    probe3.Window.Close();

    // ---------- 13. 语义令牌齐备 + 对比度 ----------
    // ---------- 13a. 真实状态画廊帧（亮/暗，逐状态强制伪类） ----------
    foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
    {
        var gallery = StateGallery.Build(variant);
        gallery.Window.Show();
        Harness.PumpTicks();
        var galleryLease = AeroSkin.Apply(gallery.Window, caption: null, variant);
        Harness.PumpTicks();
        if (gallery.Window.FindResource("AeroCaptionButtonTheme") is ControlTheme captionTheme)
        {
            foreach (var state in new[] { "normal", "hover", "pressed", "focus", "disabled" })
                foreach (var kind in new[] { "min", "max", "close" })
                    ((Button)gallery.Map[(kind, state)]).Theme = captionTheme;
            Harness.PumpTicks();
        }

        Harness.ForceStates(gallery.Forced);
        Harness.PumpTicks();
        var variantKey = (variant.Key?.ToString() ?? "default").ToLowerInvariant();
        var file = Path.Combine(evidenceRoot, $"aero-states-{variantKey}.png");
        Check(Harness.Screenshot(gallery.Window, file), $"写出 Aero {variant.Key} 状态画廊帧");
        if (variant == ThemeVariant.Light)
        {
            // 画廊断言：每控件 × 状态取真实模板属性，确认状态选择器生效（不是只看截图）
            var accentHover = (Button)gallery.Map[("accent", "hover")];
            var accentNormal = (Button)gallery.Map[("accent", "normal")];
            Check(!Equals(accentHover.Background, accentNormal.Background), "画廊：主按钮 hover 画刷与 normal 不同");
            var accentPressed = (Button)gallery.Map[("accent", "pressed")];
            Check(accentPressed.Background is LinearGradientBrush pressedGradient &&
                  pressedGradient.GradientStops[3].Color == Color.Parse("#FF68B3DB"),
                "画廊：主按钮 pressed 为深蓝四段渐变末段 #68B3DB");
            var focusRing = (Button)gallery.Map[("button", "focus")];
            Check(focusRing.BorderBrush is ISolidColorBrush { Color: var focusColor } && focusColor == Color.Parse("#FF39A0E6"),
                "画廊：键盘焦点按钮画 Aero 焦点环 #39A0E6");
            var disabled = (Button)gallery.Map[("button", "disabled")];
            Check(disabled.Background is ISolidColorBrush { Color: var disabledBg } && disabledBg == Color.Parse("#FFF4F4F4"),
                "画廊：禁用按钮平底 #F4F4F4");
            var textHover = (TextBox)gallery.Map[("input", "hover")];
            Check(textHover.BorderBrush is ISolidColorBrush { Color: var inputHover } && inputHover == Color.Parse("#FF5794BF"),
                "画廊：输入框 hover 描边 #5794BF");
            var textFocus = (TextBox)gallery.Map[("input", "focus")];
            Check(textFocus.BorderBrush is ISolidColorBrush { Color: var inputFocus } && inputFocus == Color.Parse("#FF3D7BAD"),
                "画廊：输入框 focus 描边 #3D7BAD");
            var tabHover = (Button)gallery.Map[("docktab", "hover")];
            Check(tabHover.Background is LinearGradientBrush tabHoverBrush && tabHoverBrush.GradientStops[0].Color == Color.Parse("#FFEAF6FD"),
                "画廊：页签 hover 蓝四段渐变");
            var tabDisabled = (Button)gallery.Map[("docktab", "disabled")];
            Check(!tabDisabled.IsEffectivelyEnabled, "画廊：页签 disabled 为真实禁用控件");
            var listItem = (ListBoxItem)gallery.Map[("list", "hover")];
            var listPresenter = listItem.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault();
            Check(listPresenter?.Background is LinearGradientBrush listHover && listHover.GradientStops[1].Color == Color.Parse("#FFEBF3FD"),
                "画廊：列表项 hover 为 #FAFBFD→#EBF3FD");
            var closeDisabled = (Button)gallery.Map[("close", "disabled")];
            var glyphHost = closeDisabled.GetVisualDescendants().OfType<Panel>().FirstOrDefault(panel => panel.Name == "PART_GlyphHost");
            Check(glyphHost is not null && Math.Abs(glyphHost.Opacity - 0.45) < 1e-6, "画廊：禁用窗口按钮图形 opacity 0.45");
            var menuDisabled = (MenuItem)gallery.Map[("menu", "disabled")];
            Check(!menuDisabled.IsEnabled && menuDisabled.Foreground is ISolidColorBrush { Color: var menuDisabledColor } &&
                  menuDisabledColor == Color.Parse("#FF8D96A0"), "画廊：禁用菜单项文字用禁用色");
        }

        galleryLease.Dispose();
        gallery.Window.Close();
    }

    string[] requiredTokens =
    [
        "AppWindowBackground", "AppTitleBarBackground", "AppSidebarBackground", "AppSurfaceBackground",
        "AppContentBackground", "AppCardBackground", "AppCardStroke", "AppBorderBrush", "AppTextPrimary",
        "AppTextSecondary", "AppTextDim", "AppDisabledForeground", "AppAccent", "AppAccentForeground",
        "AppAccentText", "AppAccentSubtle", "AppHover", "AppPressed", "AppListHover", "AppListSelected",
        "AppSelectionForeground", "AppFocusBrush", "AppErrorBackground", "AppErrorBorder", "AppErrorForeground",
        "AppSuccessForeground", "AppSuccessBackground", "AppCautionForeground", "AppCautionBackground",
        "AppCriticalForeground", "AppTerminalBackground", "AppTerminalForeground", "AppTerminalAccent",
        "AppDiffAddedBackground", "AppDiffAddedForeground", "AppDiffRemovedBackground", "AppDiffRemovedForeground",
        "AeroButtonBackground", "AeroButtonBackgroundPointerOver", "AeroButtonBackgroundPressed", "AeroButtonBorder",
        "AeroButtonPrimaryRing", "AeroInputBackground", "AeroInputBorder", "AeroInputBorderFocused",
        "AeroTabBackground", "AeroTabBackgroundSelected", "AeroTabIndicator", "AeroRowHoverBackground",
        "AeroRowSelectedBackground", "AeroMenuItemHoverBackground", "AeroCaptionBackground",
        "AeroCaptionBackgroundInactive", "AeroCaptionGlass", "AeroCaptionPlate", "AeroCaptionClose",
        "AeroFrameOuter", "AeroFrameInner", "AeroMenuShadow", "AeroFloatShadow",
        "ControlCornerRadius", "OverlayCornerRadius", "AppCornerCard", "AppCornerInput", "AppCornerPill",
        "AppCornerFlyout", "AppFontUi", "AppFontDisplay", "AppFontMono", "AppFontSizeBase", "AppShadowFlyout",
        "AppShadowToast", "AppFocusThickness",
    ];
    foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
    {
        var sweep = AeroSkin.Apply(probe.Window, probe.Caption, variant);
        Harness.PumpTicks();
        var missing = requiredTokens.Where(token => probe.Resolved(token) is null).ToArray();
        Check(missing.Length == 0, $"Aero {variant.Key} 语义令牌齐备（缺 {missing.Length} 个：{string.Join(",", missing)}）");
        var primary = ColorOf(probe.Resolved("AppTextPrimary") as IBrush);
        var secondary = ColorOf(probe.Resolved("AppTextSecondary") as IBrush);
        var background = ColorOf(probe.Resolved("AppWindowBackground") as IBrush);
        var content = ColorOf(probe.Resolved("AppContentBackground") as IBrush);
        Check(primary is not null && background is not null && Contrast(primary.Value, background.Value) >= 4.5,
            $"Aero {variant.Key} 主文字/窗口底对比 ≥4.5（实际 {DescribeContrast(primary, background)}）");
        Check(primary is not null && content is not null && Contrast(primary.Value, content.Value) >= 4.5,
            $"Aero {variant.Key} 主文字/内容纸对比 ≥4.5（实际 {DescribeContrast(primary, content)}）");
        Check(secondary is not null && background is not null && Contrast(secondary.Value, background.Value) >= 3.0,
            $"Aero {variant.Key} 次文字/窗口底对比 ≥3.0（实际 {DescribeContrast(secondary, background)}）");
        var captionTitleColor = ColorOf(probe.Resolved("AeroCaptionText") as IBrush);
        var captionDeep = (probe.Resolved("AeroCaptionBackground") as LinearGradientBrush)?.GradientStops[2].Color;
        Check(captionTitleColor is not null && captionDeep is not null && Contrast(captionTitleColor.Value, captionDeep.Value) >= 4.5,
            $"Aero {variant.Key} 标题字/渐变最深段对比 ≥4.5（实际 {DescribeContrast(captionTitleColor, captionDeep)}）");
        var errorFg = ColorOf(probe.Resolved("AppErrorForeground") as IBrush);
        var errorBg = ColorOf(probe.Resolved("AppErrorBackground") as IBrush);
        Check(errorFg is not null && errorBg is not null && Contrast(errorFg.Value, errorBg.Value) >= 4.5,
            $"Aero {variant.Key} 错误字/底对比 ≥4.5（实际 {DescribeContrast(errorFg, errorBg)}）");
        sweep.Dispose();
        Harness.PumpTicks();
    }

    Check(probe.Window.Resources.MergedDictionaries.Count == withSentinelDictCount &&
          probe.Window.Styles.Count == withSentinelStyleCount, "连续应用/释放后总计数回到哨兵基线");

    probe.Window.Close();

    Console.WriteLine($"\n全部通过：{passed} 项断言");
    Console.WriteLine($"证据目录：{evidenceRoot}");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    Console.Error.WriteLine(error);
    return 1;
}

static Color? ColorOf(IBrush? brush) => brush is ISolidColorBrush solid ? solid.Color : null;

static string DescribeContrast(Color? foreground, Color? background) =>
    foreground is { } fg && background is { } bg ? Contrast(fg, bg).ToString("F2") : "n/a";

/// <summary>WCAG 相对亮度对比度（正文/标题均为不透明色，无需 alpha 合成）。</summary>
static double Contrast(Color foreground, Color background)
{
    static double Channel(byte value)
    {
        var normalized = value / 255.0;
        return normalized <= 0.03928 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
    }

    static double Luminance(Color color) =>
        0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);

    var first = Luminance(foreground);
    var second = Luminance(background);
    var brighter = Math.Max(first, second);
    var darker = Math.Min(first, second);
    return (brighter + 0.05) / (darker + 0.05);
}

static Border Plate(Button button)
{
    foreach (var border in button.GetVisualDescendants().OfType<Border>())
        if (border.Name == "PART_Plate") return border;
    var names = string.Join(",", button.GetVisualDescendants().Select(control => control.GetType().Name + ":" + (control.Name ?? "-")));
    throw new InvalidOperationException($"未找到 PART_Plate；实际模板部件：{names}");
}

static bool HasPart(Button button, string name) =>
    button.GetVisualDescendants().OfType<Border>().Any(border => border.Name == name);

static bool InputHitTest(Button button, Point point)
{
    var hit = button.InputHitTest(point);
    return hit is Visual visual && (visual == button || visual.GetVisualAncestors().Contains(button));
}

static T Part<T>(Button button, string name) where T : Control
{
    foreach (var control in button.GetVisualDescendants())
        if (control is T typed && typed.Name == name) return typed;
    throw new InvalidOperationException($"未找到模板部件 {name}");
}

static void Force(Control control, string pseudoClass, bool value) =>
    ((Avalonia.Controls.IPseudoClasses)control.Classes).Set(pseudoClass, value);

static string FindRepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    // worktree 的 .git 是文件（gitdir 指针），普通克隆是目录；两者都算仓库根，另要求含 src/Fufucode.App。
    while (directory is not null &&
           (!File.Exists(Path.Combine(directory.FullName, ".git")) && !Directory.Exists(Path.Combine(directory.FullName, ".git"))
            || !Directory.Exists(Path.Combine(directory.FullName, "src", "Fufucode.App"))))
        directory = directory.Parent;
    return directory?.FullName ?? throw new InvalidOperationException("未找到仓库根目录");
}
