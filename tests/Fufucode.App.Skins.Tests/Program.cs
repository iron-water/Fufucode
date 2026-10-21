using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Fufucode.App.Features.Skins;
using Fufucode.Core.Plugins;
using Fufucode.App.Skins.Tests;

// E7 皮肤引擎定向测试（独立入口，未注册全量自测）：
//   dotnet run --project tests/Fufucode.App.Skins.Tests -c Release
// 覆盖：目录解析/未知回退、真实控件属性、亮暗热切换、指针状态、连续切换不积累、
//       双窗口互不污染、释放只清自身、Control 作用域隔离、渲染帧截图（1100×800/800×640；
//       缩放帧仅工装近似，真实系统 DPI 另行验证）。
var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}

var evidenceRoot = Path.Combine(FindRepoRoot(), "tests", "Fufucode.App.Skins.Tests", "evidence");
Directory.CreateDirectory(evidenceRoot);

try
{
    // ---------- 1. 目录解析与解释性回退（无 UI、不写任何持久文件） ----------
// 默认包只提供 Fluent；其余皮肤由插件市场安装。测试显式登记这些皮肤贡献，
// 再验证插件可用后的目录、解析与换肤链路。
Check(SkinCatalog.All.Select(s => s.StorageKey).SequenceEqual(new[] { "fluent" }),
    "默认包内置皮肤只有 Fluent（其余皮肤不再随包提供）");
Check(!SkinCatalog.TryParse("metro", out _), "未安装的 Metro 皮肤键回退 Fluent（市场插件未安装）");
foreach (var id in new[] { SkinId.Metro, SkinId.Material3, SkinId.Aero, SkinId.Luna, SkinId.LiquidGlass })
{
    var d = SkinCatalog.Get(id);
    PluginSkinRegistry.Register("test." + d.StorageKey,
        new PluginSkinContribution(d.StorageKey, d.DisplayName, d.Summary, d.Description, d.TokenDictionaryUri, d.ScopedStylesUri));
}
Check(SkinCatalog.All.Count == 6, "安装皮肤插件后目录恢复六套皮肤");
    Check(SkinCatalog.TryParse("metro", out var metroId) && metroId == SkinId.Metro, "metro 键解析为 Metro");
    Check(SkinCatalog.TryParse("Material3", out var m3Id) && m3Id == SkinId.Material3, "Material3 键（大小写不敏感）解析为 Material3");
    Check(SkinCatalog.TryParse("m3", out var m3Alias) && m3Alias == SkinId.Material3, "m3 别名解析为 Material3");
    Check(SkinCatalog.TryParse("material-3", out var m3Alias2) && m3Alias2 == SkinId.Material3, "material-3 别名解析为 Material3");
    Check(SkinCatalog.TryParse(" fluent ", out var fluentId) && fluentId == SkinId.Fluent, "fluent 键（含空白）解析为 Fluent");
    var unknown = SkinCatalog.Resolve("future-uninstalled-skin");
    Check(unknown.Skin == SkinId.Fluent && unknown.FellBack, "尚未安装的未知键回退 Fluent");
    Check(unknown.RequestedKey == "future-uninstalled-skin" && unknown.Reason.Contains("回退"), "未知键回退保留原始请求并说明原因（不覆写输入）");
    Check(SkinCatalog.TryParse("aero", out var aeroId) && aeroId == SkinId.Aero, "已接线 Aero 键解析为 Aero，不再当作未知键");
    Check(SkinCatalog.TryParse("luna", out var lunaId) && lunaId == SkinId.Luna, "luna 键解析为 Luna");
    Check(SkinCatalog.TryParse("XP", out var xpAlias) && xpAlias == SkinId.Luna, "xp 别名（大小写不敏感）解析为 Luna");
    Check(SkinCatalog.TryParse("luna-blue", out var lunaAlias) && lunaAlias == SkinId.Luna, "luna-blue 别名解析为 Luna");
    var blank = SkinCatalog.Resolve(null);
    Check(blank.Skin == SkinId.Fluent && blank.FellBack, "空皮肤键回退 Fluent");
    Check(SkinCatalog.All.Select(s => s.StorageKey).SequenceEqual(new[] { "fluent", "metro", "material3", "aero", "luna", "liquidglass" })
          && SkinCatalog.Get(SkinId.Fluent).StorageKey == "fluent", "目录含六套已接入皮肤且默认键为 fluent");
    Check(SkinCatalog.All.Count == 6 && SkinCatalog.All.Select(s => s.Id).Distinct().Count() == 6,
        "六套皮肤 ID 唯一且目录完整（Fluent/Metro/Material3/Aero/Luna/LiquidGlass）");
    Check(SkinCatalog.TryParse("liquidglass", out var lgId) && lgId == SkinId.LiquidGlass, "liquidglass 键解析为 LiquidGlass");
    Check(SkinCatalog.TryParse("glass", out var glassAlias) && glassAlias == SkinId.LiquidGlass, "glass 别名解析为 LiquidGlass");
    Check(SkinCatalog.TryParse("ios", out var iosAlias) && iosAlias == SkinId.LiquidGlass, "ios 别名解析为 LiquidGlass");
    Check(SkinCatalog.Get(SkinId.LiquidGlass).FollowsSystemAccent, "LiquidGlass 跟随系统强调色（不锁死 iOS 蓝）");
    Check(SkinCatalog.Get(SkinId.Fluent).TokenDictionaryUri is null && SkinCatalog.Get(SkinId.Fluent).ScopedStylesUri is null,
        "Fluent 不携带覆盖资源（恢复=移除覆盖层）");
    foreach (var definition in SkinCatalog.All.Where(s => s.Id != SkinId.Fluent))
        Check(definition.TokenDictionaryUri is not null && definition.ScopedStylesUri is not null && definition.StorageKey.Length > 0,
            $"{definition.DisplayName} 定义携带独立令牌字典与限定样式 URI");
    Check(SkinCatalog.Get(SkinId.Luna).TokenDictionaryUri == "avares://Fufucode.App/Styles/Skins/Luna.axaml"
          && SkinCatalog.Get(SkinId.Luna).ScopedStylesUri == "avares://Fufucode.App/Styles/Skins/LunaStyles.axaml",
        "Luna 指向真实 Luna.axaml / LunaStyles.axaml（不复用 Fluent 资产）");

    // ---------- 1b. 皮肤文案收敛：短标签内联，长说明只进 ToolTip / 帮助按钮 ----------
    Check(SkinPresentation.All.Count == SkinCatalog.All.Count, "呈现文案覆盖全部皮肤");
    foreach (var copy in SkinPresentation.All)
    {
        Check(!string.IsNullOrWhiteSpace(copy.Summary) && copy.Summary.Length <= SkinPresentation.InlineSummaryBudget,
            $"{copy.Label} 内联短标签在 {SkinPresentation.InlineSummaryBudget} 字预算内（{copy.Summary.Length} 字）");
        Check(!string.IsNullOrWhiteSpace(copy.HelpText) && copy.HelpText.Length > copy.Summary.Length,
            $"{copy.Label} 长说明只在帮助文本中（{copy.HelpText.Length} 字，长于短标签）");
        Check(!copy.Summary.Contains(copy.HelpText, StringComparison.Ordinal),
            $"{copy.Label} 内联标签不包含长说明（不堆砌文字）");
    }

    // ---------- 2. 启动无头 App（Skia 真实渲染，可截帧） ----------
    AppBuilder.Configure<TestApp>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia()
        .SetupWithoutStarting();

    // ---------- 2b. 折叠小帮助按钮：长说明只在 ToolTip / 自动化帮助文本 ----------
    foreach (var copy in SkinPresentation.All)
    {
        var help = SkinPresentation.CreateHelpButton(copy.Id);
        Check(help.Classes.Contains("settingsHelp") && help.Classes.Contains("skinHelp"),
            $"{copy.Label} 帮助按钮带 settingsHelp/skinHelp 类");
        Check(help.Content is string marker && marker.Length <= 2,
            $"{copy.Label} 帮助按钮自身只有短标记（{help.Content}）");
        Check(ToolTip.GetTip(help) as string == copy.HelpText, $"{copy.Label} 长说明只挂在 ToolTip 上");
        Check(AutomationProperties.GetHelpText(help) == copy.HelpText, $"{copy.Label} 长说明同时供屏幕阅读器读取");
    }

    var probe = Probe.Create(1100, 800);
    probe.Window.Show();
    Harness.PumpTicks();

    var baselineDictCount = probe.Window.Resources.MergedDictionaries.Count;
    var baselineStyleCount = probe.Window.Styles.Count;
    var baselineWindow = probe.ResolvedColor("AppWindowBackground");
    var baselineAccent = probe.ResolvedColor("AppAccent");
    var baselineCardRadius = probe.Card.CornerRadius;
    var baselineButton = ColorOf(probe.DefaultButton.Background);
    var baselineChromeBackground = ColorOf(probe.ChromeBar.Background);
    var baselineChromeText = ColorOf(probe.ChromeBarText.Foreground);

    Check(baselineWindow == Color.Parse("#FFF3F3F3"), $"Fluent 基线窗口底色为 #FFF3F3F3（实际 {Harness.Text(baselineWindow)}）");
    Check(baselineCardRadius == new CornerRadius(8), $"Fluent 基线卡片圆角为 OverlayCornerRadius=8（实际 {baselineCardRadius}）");
    Check(baselineButton is not null, "Fluent 基线默认按钮已有实色背景");
    Check(baselineChromeBackground is not null && baselineChromeText is not null, "Fluent 基线标题栏已有底色与文字色");

    // ---------- 3. Metro 亮色：真实控件属性 ----------
    var metro = SkinLease.Apply(probe.Window, SkinCatalog.Resolve("metro"), ThemeVariant.Light);
    Harness.PumpTicks();
    Check(metro.IsActive && metro.HasTokenOverrides && metro.HasScopedStyles, "Metro 租约挂载令牌字典与限定样式");
    Check(metro.OwnsVariantScope && metro.VariantApplied, "顶层窗口租约接管并应用请求的变体");
    Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount + 1
          && probe.Window.Styles.Count == baselineStyleCount + 1, "Metro 应用只追加 1 个字典 + 1 个样式");

    Check(probe.ResolvedColor("AppWindowBackground") == Color.Parse("#FFFFFFFF"), "Metro 亮色窗口底解析为 #FFFFFF");
    Check(probe.ResolvedColor("AppAccent") == Color.Parse("#FF2D89EF"), "Metro 亮色强调色解析为 #2D89EF");
    Check(probe.ResolvedCorner("ControlCornerRadius") == new CornerRadius(0), "Metro ControlCornerRadius 解析为 0");
    Check(probe.Resolved("AppFontUi") is FontFamily, "Metro AppFontUi 解析为 FontFamily");
    Check(probe.Resolved("AppFocusThickness") is double focus && Math.Abs(focus - 2) < 0.001, "Metro AppFocusThickness=2");
    Check(probe.Title.Foreground is ISolidColorBrush inherited && inherited.Color == Color.Parse("#FF1A1A1A"),
        "Metro 主文字色经窗口 Foreground 继承到真实 TextBlock");

    Check(ColorOf(probe.DefaultButton.Background) == Color.Parse("#FFFFFFFF"), "Metro 默认按钮背景跟随 ButtonBackground 令牌");
    Check(ColorOf(probe.DefaultButton.Background) != baselineButton, "Metro 默认按钮与 Fluent 基线实际不同");
    Check(ColorOf(probe.AccentButton.Background) == Color.Parse("#FF2D89EF"), "Metro 主按钮背景为强调色 #2D89EF");
    Check(ColorOf(probe.AccentButton.Foreground) == Color.Parse("#FFFFFFFF"), "Metro 主按钮前景为白");
    var disabledPresenter = probe.DisabledButton.GetVisualDescendants()
        .OfType<Avalonia.Controls.Presenters.ContentPresenter>().FirstOrDefault();
    Check(ColorOf(disabledPresenter?.Foreground) == Color.Parse("#FF9A9A9A"),
        $"Metro 禁用按钮模板呈现层前景为禁用令牌 #9A9A9A（实际 {Harness.Text(ColorOf(disabledPresenter?.Foreground))}）");

    Check(probe.TextBox.CornerRadius == new CornerRadius(0) && probe.TextBox.BorderThickness == new Thickness(2),
        "Metro TextBox 直角 + 2px 描边（真实属性）");
    Check(ColorOf(probe.TextBox.Background) == Color.Parse("#FFFFFFFF"), "Metro TextBox 背景为平面实色");
    Check(ColorOf(probe.ComboBox.BorderBrush) == Color.Parse("#FFD6D6D6"), "Metro ComboBox 描边为结构线色");

    Check(probe.Card.CornerRadius == new CornerRadius(0), "Metro 卡片直角（限定样式覆盖应用级 8）");
    Check(probe.SettingsRow.CornerRadius == new CornerRadius(0), "Metro 设置行直角");
    Check(probe.OverlayPanel.CornerRadius == new CornerRadius(0), "Metro 浮层直角（无阴影样板）");
    Check(probe.Toast.CornerRadius == new CornerRadius(0), "Metro Toast 直角");

    var metroSelected = probe.SelectedListPresenter();
    Check(metroSelected is not null && ColorOf(metroSelected.Background) == Color.Parse("#FF2D89EF"),
        "Metro 列表选中项为整块强调色填充");
    Check(metroSelected is not null && ColorOf(metroSelected.Foreground) == Color.Parse("#FFFFFFFF"),
        "Metro 列表选中项前景为选中前景色");
    var metroPipe = probe.SelectedTabPipe();
    Check(metroPipe is not null && ColorOf(metroPipe.Background) == Color.Parse("#FF2D89EF") && metroPipe.CornerRadius == new CornerRadius(0),
        "Metro 页签指示条为直角强调色");

    // ---------- 4. 亮/暗热切换（不重新应用租约） ----------
    probe.Window.RequestedThemeVariant = ThemeVariant.Dark;
    Harness.PumpTicks();
    Check(probe.ResolvedColor("AppWindowBackground") == Color.Parse("#FF1A1A1A"), "Metro 暗色窗口底热切换为 #1A1A1A");
    Check(probe.ResolvedColor("AppAccent") == Color.Parse("#FF4DB2FF"), "Metro 暗色强调色热切换为 #4DB2FF");
    Check(ColorOf(probe.AccentButton.Background) == Color.Parse("#FF4DB2FF"), "Metro 暗色主按钮真实属性热切换");
    Check(probe.Card.CornerRadius == new CornerRadius(0), "亮暗切换不影响 Metro 形状");
    probe.Window.RequestedThemeVariant = ThemeVariant.Light;
    Harness.PumpTicks();
    Check(probe.ResolvedColor("AppWindowBackground") == Color.Parse("#FFFFFFFF"), "切回亮色恢复 #FFFFFF");

    // ---------- 5. 指针悬停状态（真实无头指针输入） ----------
    Harness.Hover(probe.Window, probe.AccentButton);
    Harness.PumpTicks();
    // The shared shell now paints interaction states on the actual template presenter.
    // Keep the exact expected color and real hover input; inspect the layer that is drawn.
    var metroButtonPresenter = probe.AccentButton.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Single(p=>p.Name=="PART_ContentPresenter");
    Check(probe.AccentButton.IsPointerOver && ColorOf(metroButtonPresenter.Background) == Color.Parse("#FF1E6FC4"), "Metro 主按钮 :pointerover 实际绘制层换为次级强调色");
    Harness.Hover(probe.Window, probe.Title);
    Harness.PumpTicks();
    Check(!probe.AccentButton.IsPointerOver && ColorOf(metroButtonPresenter.Background) == Color.Parse("#FF2D89EF"), "离开后主按钮实际绘制层恢复默认强调色");
    probe.DefaultButton.Focus();
    Harness.PumpTicks();
    Check(probe.DefaultButton.IsFocused, "真实焦点可落到按钮（焦点环为皮肤限定样式）");

    // ---------- 6. 截图：Metro 亮/暗（1100×800） ----------
    Check(Harness.Screenshot(probe.Window, Path.Combine(evidenceRoot, "metro-light-1100x800.png")), "写出 Metro 亮色帧");
    probe.Window.RequestedThemeVariant = ThemeVariant.Dark;
    Harness.PumpTicks();
    Check(Harness.Screenshot(probe.Window, Path.Combine(evidenceRoot, "metro-dark-1100x800.png")), "写出 Metro 暗色帧");
    probe.Window.RequestedThemeVariant = ThemeVariant.Light;
    Harness.PumpTicks();

    // ---------- 7. 释放：资源/样式/属性回到 Fluent 基线 ----------
    metro.Dispose();
    Harness.PumpTicks();
    Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount
          && probe.Window.Styles.Count == baselineStyleCount, "Metro 释放后字典/样式计数回到基线");
    Check(probe.ResolvedColor("AppWindowBackground") == baselineWindow, "Metro 释放后窗口底回到 Fluent 基线");
    Check(probe.ResolvedColor("AppAccent") == baselineAccent, "Metro 释放后强调色回到 Fluent 基线");
    Check(probe.Card.CornerRadius == baselineCardRadius, "Metro 释放后卡片圆角回到基线 8");
    Check(ColorOf(probe.DefaultButton.Background) == baselineButton, "Metro 释放后按钮背景回到基线");
    Check(ColorOf(probe.AccentButton.Background) != Color.Parse("#FF2D89EF"), "Metro 释放后主按钮不再残留强调色");
    metro.Dispose();
    Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount, "重复 Dispose 幂等");

    // ---------- 8. Material 3 亮色 ----------
    var m3 = SkinLease.Apply(probe.Window, SkinCatalog.Resolve("material3"), ThemeVariant.Light);
    Harness.PumpTicks();
    Check(probe.ResolvedColor("AppWindowBackground") == Color.Parse("#FFFEF7FF"), "M3 亮色窗口底 #FEF7FF");
    Check(probe.ResolvedColor("AppAccent") == Color.Parse("#FF6750A4"), "M3 强调色 #6750A4");
    Check(probe.Card.CornerRadius == new CornerRadius(16), "M3 卡片圆角 16");
    Check(probe.SettingsRow.CornerRadius == new CornerRadius(16), "M3 设置行圆角 16");
    Check(probe.OverlayPanel.CornerRadius == new CornerRadius(28), "M3 浮层圆角 28");
    Check(probe.Toast.CornerRadius == new CornerRadius(20), "M3 Toast 圆角 20");
    Check(probe.DefaultButton.CornerRadius == new CornerRadius(20), "M3 按钮胶囊圆角 20（真实属性）");
    Check(ColorOf(probe.DefaultButton.Background) == Color.Parse("#FFE8DEF8"), "M3 按钮为 tonal 容器色");
    Check(probe.TextBox.CornerRadius == new CornerRadius(4), "M3 输入圆角 4");
    var m3Selected = probe.SelectedListPresenter();
    Check(m3Selected is not null && ColorOf(m3Selected.Background) == Color.Parse("#FFEADDFF")
          && ColorOf(m3Selected.Foreground) == Color.Parse("#FF21005D"), "M3 列表选中为 secondary-container + 强调文字");
    var m3Pipe = probe.SelectedTabPipe();
    Check(m3Pipe is not null && m3Pipe.CornerRadius == new CornerRadius(2) && ColorOf(m3Pipe.Background) == Color.Parse("#FF6750A4"),
        "M3 页签指示条为 2 圆角强调色");
    probe.DefaultButton.Focus();
    Harness.PumpTicks();
    probe.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
    Harness.PumpTicks();
    Check(probe.AccentButton.IsFocused, "M3 帧拍摄前 Tab 键盘导航把焦点移到主按钮（键盘可见焦点路径）");
    Check(Harness.Screenshot(probe.Window, Path.Combine(evidenceRoot, "material3-light-1100x800.png")), "写出 M3 亮色帧");

    probe.Window.RequestedThemeVariant = ThemeVariant.Dark;
    Harness.PumpTicks();
    Check(probe.ResolvedColor("AppWindowBackground") == Color.Parse("#FF141218"), "M3 暗色窗口底 #141218");
    Check(probe.ResolvedColor("AppAccent") == Color.Parse("#FFD0BCFF"), "M3 暗色强调色 #D0BCFF");
    Check(ColorOf(probe.DefaultButton.Background) == Color.Parse("#FF4F378B"), "M3 暗色按钮真实属性跟随");
    Check(Harness.Screenshot(probe.Window, Path.Combine(evidenceRoot, "material3-dark-1100x800.png")), "写出 M3 暗色帧");
    probe.Window.RequestedThemeVariant = ThemeVariant.Light;
    Harness.PumpTicks();
    m3.Dispose();
    Harness.PumpTicks();
    Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount
          && probe.Window.Styles.Count == baselineStyleCount, "M3 释放后计数回到基线");
    Check(probe.Card.CornerRadius == baselineCardRadius && probe.DefaultButton.CornerRadius == new CornerRadius(4),
        "M3 释放后卡片/按钮圆角回到 Fluent 基线");

    // ---------- 8b. Luna（Windows XP 经典圆润 + 高对比）：真实控件属性 + 亮暗 + 释放 ----------
    var luna = SkinLease.Apply(probe.Window, SkinCatalog.Resolve("luna"), ThemeVariant.Light);
    Harness.PumpTicks();
    Check(luna.IsActive && luna.HasTokenOverrides && luna.HasScopedStyles, "Luna 租约挂载令牌字典与限定样式");
    Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount + 1
          && probe.Window.Styles.Count == baselineStyleCount + 1, "Luna 应用只追加 1 个字典 + 1 个样式");
    Check(probe.ResolvedColor("AppWindowBackground") == Color.Parse("#FFECE9D8"), "Luna 亮色窗口底为 XP 面底色 #ECE9D8");
    Check(probe.ResolvedColor("AppAccent") == Color.Parse("#FF316AC5"), "Luna 亮色强调色为 XP 选择蓝 #316AC5");
    Check(probe.ResolvedCorner("ControlCornerRadius") == new CornerRadius(3)
          && probe.ResolvedCorner("AppCornerPill") == new CornerRadius(3)
          && probe.ResolvedCorner("AppCornerInput") == new CornerRadius(2), "Luna 圆角 3/3/2（圆润但不胶囊、不直角）");
    Check(probe.Resolved("AppFontUi") is FontFamily lunaFont && lunaFont.ToString().Contains("Tahoma", StringComparison.OrdinalIgnoreCase),
        "Luna 字族回退 XP Tahoma");
    Check(probe.Resolved("AppFontWeightStrong") is FontWeight lunaWeight && lunaWeight == FontWeight.Bold,
        "Luna 强调字重为 Bold（XP 高对比）");
    Check(probe.ResolvedColor("AppFocusBrush") == Color.Parse("#FF316AC5"), "Luna 焦点环用 XP 选择蓝");
    var lunaButton = probe.DefaultButton.Background as LinearGradientBrush;
    Check(lunaButton is not null && lunaButton.GradientStops.Count == 4,
        $"Luna 默认按钮为四段实色玻璃渐变（实际 {DescribeBrush(probe.DefaultButton.Background)}）");
    var lunaAccentButton = probe.AccentButton.Background as LinearGradientBrush;
    Check(lunaAccentButton is not null && lunaAccentButton.GradientStops.Count == 4 && probe.AccentButton.CornerRadius == new CornerRadius(3),
        "Luna 主按钮为 XP 默认按钮（四段渐变 + 圆角 3）");
    var lunaChrome = probe.ChromeBar.Background as LinearGradientBrush;
    Check(lunaChrome is not null && lunaChrome.GradientStops.Count == 4, "Luna 标题栏为 XP 蓝四段渐变（仅 chrome 层）");
    Check(ColorOf(probe.ChromeBarText.Foreground) == Color.Parse("#FFFFFFFF")
          && ColorOf(probe.ChromeBarIcon.Foreground) == Color.Parse("#FFFFFFFF"), "Luna 标题栏文字与图标转白（高对比）");
    Check(ColorOf(probe.ChromeBar.BorderBrush) == Color.Parse("#FF003C74"), "Luna 标题栏底线为 XP 深蓝 #003C74");
    Check(probe.TextBox.CornerRadius == new CornerRadius(2) && probe.TextBox.BorderThickness == new Thickness(1)
          && ColorOf(probe.TextBox.BorderBrush) == Color.Parse("#FF7F9DB9"), "Luna 输入为 2px 圆角 + 1px XP 蓝灰描边");
    var lunaSelected = probe.SelectedListPresenter();
    Check(lunaSelected is not null && ColorOf(lunaSelected.Background) == Color.Parse("#FF316AC5")
          && ColorOf(lunaSelected.Foreground) == Color.Parse("#FFFFFFFF"), "Luna 列表选中为整块 XP 选择蓝 + 白字");
    var lunaPipe = probe.SelectedTabPipe();
    Check(lunaPipe is not null && ColorOf(lunaPipe.Background) == Color.Parse("#FF316AC5"), "Luna 页签指示条为 XP 选择蓝");
    Check(probe.Card.CornerRadius == new CornerRadius(3) && probe.OverlayPanel.CornerRadius == new CornerRadius(3),
        "Luna 卡片/浮层圆角 3");
    Check(Harness.Screenshot(probe.Window, Path.Combine(evidenceRoot, "luna-light-1100x800.png")), "写出 Luna 亮色帧");

    probe.Window.RequestedThemeVariant = ThemeVariant.Dark;
    Harness.PumpTicks();
    Check(probe.ResolvedColor("AppWindowBackground") == Color.Parse("#FF23262B"), "Luna 暗色窗口底 #23262B（皮肤自定高对比暗色）");
    Check(probe.ResolvedColor("AppAccent") == Color.Parse("#FF6FA8DC"), "Luna 暗色强调色 #6FA8DC");
    Check(probe.DefaultButton.Background is LinearGradientBrush, "Luna 暗色按钮仍为渐变（非 Fluent 平面）");
    Check(Harness.Screenshot(probe.Window, Path.Combine(evidenceRoot, "luna-dark-1100x800.png")), "写出 Luna 暗色帧");
    probe.Window.RequestedThemeVariant = ThemeVariant.Light;
    Harness.PumpTicks();

    luna.Dispose();
    Harness.PumpTicks();
    Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount && probe.Window.Styles.Count == baselineStyleCount,
        "Luna 释放后字典/样式计数回到基线");
    Check(probe.ResolvedColor("AppWindowBackground") == baselineWindow && probe.Card.CornerRadius == baselineCardRadius,
        "Luna 释放后窗口底/卡片圆角回到 Fluent 基线");
    Check(probe.ChromeBar.Background is not LinearGradientBrush && ColorOf(probe.ChromeBar.Background) == baselineChromeBackground,
        "Luna 释放后标题栏不再残留 XP 蓝渐变");
    Check(ColorOf(probe.ChromeBarText.Foreground) == baselineChromeText, "Luna 释放后标题栏文字回到 Fluent 基线");

    // ---------- 8c. Aero（Vista）：独立字典 + 标题栏字典 + 释放彻底 ----------
    var aero = SkinLease.Apply(probe.Window, SkinCatalog.Resolve("aero"), ThemeVariant.Light);
    Harness.PumpTicks();
    Check(aero.IsActive && aero.HasTokenOverrides && aero.HasScopedStyles, "Aero 租约挂载令牌字典与限定样式");
    Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount + 2
          && probe.Window.Styles.Count == baselineStyleCount + 1, "Aero 应用追加令牌字典 + 标题栏字典 + 1 个样式");
    Check(probe.ResolvedColor("AppWindowBackground") == Color.Parse("#FFEEF3F8"), "Aero 亮色窗口底 #EEF3F8");
    Check(probe.ResolvedColor("AppAccent") == Color.Parse("#FF2F7ED8"), "Aero 亮色强调色 #2F7ED8");
    Check(probe.ResolvedCorner("ControlCornerRadius") == new CornerRadius(3) && probe.Card.CornerRadius == new CornerRadius(3),
        "Aero 控件/卡片圆角 3");
    var aeroButton = probe.DefaultButton.Background as LinearGradientBrush;
    Check(aeroButton is not null && aeroButton.GradientStops.Count == 4, "Aero 默认按钮为四段实色玻璃渐变");
    Check(ColorOf(probe.ChromeBar.Background) == Color.Parse("#FFDDEBF9"),
        "Aero 未挂标题栏适配时 chromeBar 保持实色（连体水晶按钮由 AeroCaption 接管）");
    aero.Dispose();
    Harness.PumpTicks();
    Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount && probe.Window.Styles.Count == baselineStyleCount,
        "Aero 释放后字典/样式计数回到基线（标题栏字典一并移除）");
    Check(probe.ResolvedColor("AppWindowBackground") == baselineWindow, "Aero 释放后窗口底回到 Fluent 基线");

    // ---------- 8c-lg. LiquidGlass（iOS 26）：令牌 + chrome 玻璃包覆 + 释放还原 ----------
    var lgChromeParent = probe.ChromeBar.Parent;
    var lg = SkinLease.Apply(probe.Window, SkinCatalog.Resolve("liquidglass"), ThemeVariant.Light);
    Harness.PumpTicks();
    Check(lg.IsActive && lg.HasTokenOverrides && lg.HasScopedStyles, "LiquidGlass 租约挂载令牌字典与限定样式");
    Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount + 1
          && probe.Window.Styles.Count == baselineStyleCount + 1, "LiquidGlass 应用只追加 1 个字典 + 1 个样式（无第二字典）");
    Check(probe.ResolvedColor("AppWindowBackground") == Color.Parse("#FFF2F2F7"), "LiquidGlass 亮色窗口底为 iOS systemGray6 #F2F2F7");
    Check(probe.ResolvedColor("AppAccent") is not null, "LiquidGlass 强调色按 CustomAccentKeys 注入链解析（跟随系统）");
    Check(probe.ResolvedColor("LiquidGlassAccent") == Color.Parse("#FF007AFF"), "LiquidGlass 私有探针键为 iOS systemBlue #007AFF");
    Check(probe.ResolvedCorner("ControlCornerRadius") == new CornerRadius(10)
          && probe.ResolvedCorner("AppCornerCard") == new CornerRadius(14)
          && probe.ResolvedCorner("AppCornerPill") == new CornerRadius(20), "LiquidGlass iOS 连续圆角 10/14/20");
    Check(probe.Card.CornerRadius == new CornerRadius(14) && probe.SettingsRow.CornerRadius == new CornerRadius(14),
        "LiquidGlass 卡片/设置行真实圆角 14");
    Check(probe.DefaultButton.CornerRadius == new CornerRadius(10), "LiquidGlass 按钮圆角 10（真实属性）");
    Check(ColorOf(probe.DefaultButton.Background) == Color.Parse("#0A000000"), "LiquidGlass 默认按钮为 iOS 浅灰 tinted 填充");
    Check(ColorOf(probe.AccentButton.Background) is not null && probe.AccentButton.BorderThickness == new Thickness(0),
        "LiquidGlass 主按钮无描边（iOS 实心强调胶囊）");
    // adapter 包覆：工装 chromeBar 命中 chromeBar 类 → 被包进 LiquidGlassSurface
    Check(probe.ChromeBar.Classes.Contains("liquidGlass") && probe.ChromeBar.Classes.Contains("liquidGlassCaption"),
        "LiquidGlass chromeBar 被 adapter 加 liquidGlass/liquidGlassCaption 类");
    Check(probe.ChromeBar.Parent is Fufucode.App.Features.Skins.LiquidGlass.LiquidGlassSurface,
        "LiquidGlass chromeBar 实际被包进 LiquidGlassSurface 渲染宿主");
    Check(probe.ChromeBar.Parent != lgChromeParent || probe.ChromeBar.Parent is Fufucode.App.Features.Skins.LiquidGlass.LiquidGlassSurface,
        "LiquidGlass 包覆改变宿主父级为 Surface");
    Check(probe.ChromeBar.Background is null || probe.ChromeBar.Background is ISolidColorBrush { Color.A: 0 },
        "LiquidGlass chromeBar 背景透明（玻璃从 LiquidGlassSurface 自绘透出）");
    var lgSelected = probe.SelectedListPresenter();
    Check(lgSelected is not null && ColorOf(lgSelected.Background) == Color.Parse("#1F007AFF"),
        "LiquidGlass 列表选中为 iOS tinted 浅强调底（非整块实蓝）");
    Check(lgSelected is not null && ColorOf(lgSelected.Foreground) == Color.Parse("#FF007AFF"),
        "LiquidGlass 列表选中前景为 iOS 强调色文字");
    Check(Harness.Screenshot(probe.Window, Path.Combine(evidenceRoot, "liquidglass-light-1100x800.png")), "写出 LiquidGlass 亮色帧");
    probe.Window.RequestedThemeVariant = ThemeVariant.Dark;
    Harness.PumpTicks();
    Check(probe.ResolvedColor("AppWindowBackground") == Color.Parse("#FF000000")
          && probe.ResolvedColor("LiquidGlassAccent") == Color.Parse("#FF0A84FF"), "LiquidGlass 暗色切到 iOS 黑底 + 暗蓝");
    Check(Harness.Screenshot(probe.Window, Path.Combine(evidenceRoot, "liquidglass-dark-1100x800.png")), "写出 LiquidGlass 暗色帧");
    probe.Window.RequestedThemeVariant = ThemeVariant.Light;
    Harness.PumpTicks();
    lg.Dispose();
    Harness.PumpTicks();
    Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount
          && probe.Window.Styles.Count == baselineStyleCount, "LiquidGlass 释放后字典/样式计数回到基线");
    Check(!probe.ChromeBar.Classes.Contains("liquidGlass") && !probe.ChromeBar.Classes.Contains("liquidGlassCaption")
          && !probe.ChromeBar.Classes.Contains("glass"), "LiquidGlass 释放后 chromeBar 玻璃类全部还原");
    Check(!(probe.ChromeBar.Parent is Fufucode.App.Features.Skins.LiquidGlass.LiquidGlassSurface)
          && ReferenceEquals(probe.ChromeBar.Parent, lgChromeParent), "LiquidGlass 释放后 chromeBar 父级还原为原容器");
    Check(probe.ResolvedColor("AppWindowBackground") == baselineWindow, "LiquidGlass 释放后窗口底回到 Fluent 基线");
    lg.Dispose();
    Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount, "LiquidGlass 重复 Dispose 幂等");

    // ---------- 8d. Fluent 视觉残余剔除：非 Fluent 皮肤不得复用 Fluent 双色高度描边 ----------
    foreach (var skinKey in new[] { "metro", "material3", "aero", "luna", "liquidglass" })
    {
        var definition = SkinCatalog.Get(SkinCatalog.Resolve(skinKey).Skin);
        var dictionary = LoadSkinDictionary(definition.TokenDictionaryUri!);
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var elevation = DictionaryValue(dictionary, "ControlElevationBorderBrush", variant);
            Check(elevation is ISolidColorBrush,
                $"{skinKey} {variant.Key} 高度描边为单色（剔除 Fluent 双色渐变残余，实际 {DescribeBrush(elevation as IBrush)}）");
        }
    }
    var metroTokenKeys = SkinDictionaryKeys("metro", ThemeVariant.Light);
    var lunaTokenKeys = SkinDictionaryKeys("luna", ThemeVariant.Light);
    Check(metroTokenKeys.Count > 150, $"工装读取到 Metro 令牌键集（{metroTokenKeys.Count} 键）");
    Check(metroTokenKeys.IsSubsetOf(lunaTokenKeys),
        $"Luna 覆盖 Metro 全部令牌键（缺 {metroTokenKeys.Count(key => !lunaTokenKeys.Contains(key))} 个）");

    // ---------- 8e. 五套非 Fluent 皮肤形状/笔刷/渐变矩阵 ----------
    var shapeMatrix = new (string Key, int ControlRadius, int CardRadius, int PillRadius, bool ButtonGradient, int GradientStops)[]
    {
        ("metro", 0, 0, 0, false, 0),
        ("material3", 8, 16, 20, false, 0),
        ("aero", 3, 3, 10, true, 4),
        ("luna", 3, 3, 3, true, 4),
        ("liquidglass", 10, 14, 20, false, 0),
    };
    foreach (var (key, controlRadius, cardRadius, pillRadius, buttonGradient, gradientStops) in shapeMatrix)
    {
        var lease = probe.Window.ApplySkin(key, ThemeVariant.Light);
        Harness.PumpTicks();
        Check(probe.ResolvedCorner("ControlCornerRadius") == new CornerRadius(controlRadius), $"{key} ControlCornerRadius={controlRadius}");
        Check(probe.ResolvedCorner("AppCornerCard") == new CornerRadius(cardRadius), $"{key} AppCornerCard={cardRadius}");
        Check(probe.ResolvedCorner("AppCornerPill") == new CornerRadius(pillRadius), $"{key} AppCornerPill={pillRadius}");
        var buttonBrush = probe.DefaultButton.Background;
        if (buttonGradient)
            Check(buttonBrush is LinearGradientBrush gradient && gradient.GradientStops.Count == gradientStops,
                $"{key} 按钮为 {gradientStops} 段渐变（实际 {DescribeBrush(buttonBrush)}）");
        else
            Check(buttonBrush is not LinearGradientBrush, $"{key} 按钮为平面实色（无渐变）");
        lease.Dispose();
        Harness.PumpTicks();
    }
    Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount && probe.Window.Styles.Count == baselineStyleCount,
        "形状矩阵跑完后资源/样式计数回到基线");

    // ---------- 9. 双窗口独立（Metro 暗 vs M3 亮）与 800×640 截图 ----------
    var probe2 = Probe.Create(800, 640);
    probe2.Window.Show();
    Harness.PumpTicks();
    var metroDark = SkinLease.Apply(probe.Window, SkinCatalog.Resolve("metro"), ThemeVariant.Dark);
    var m3Light = SkinLease.Apply(probe2.Window, SkinCatalog.Resolve("material3"), ThemeVariant.Light);
    Harness.PumpTicks();
    Check(probe.ResolvedColor("AppWindowBackground") == Color.Parse("#FF1A1A1A"), "窗口1 Metro 暗色保持自身底色");
    Check(probe2.ResolvedColor("AppWindowBackground") == Color.Parse("#FFFEF7FF"), "窗口2 M3 亮色保持自身底色");
    Check(probe.Card.CornerRadius == new CornerRadius(0) && probe2.Card.CornerRadius == new CornerRadius(16),
        "两窗口形状互不污染（0 vs 16）");
    Check(ColorOf(probe.DefaultButton.Background) == Color.Parse("#FF262626")
          && ColorOf(probe2.DefaultButton.Background) == Color.Parse("#FFE8DEF8"), "两窗口按钮令牌互不污染");
    Check(Harness.Screenshot(probe2.Window, Path.Combine(evidenceRoot, "material3-light-800x640.png")), "写出 800×640 M3 帧");
    m3Light.Dispose();
    Harness.PumpTicks();
    Check(probe.ResolvedColor("AppWindowBackground") == Color.Parse("#FF1A1A1A")
          && probe.Card.CornerRadius == new CornerRadius(0), "窗口2 释放不影响窗口1 的 Metro 租约");
    Check(probe2.ResolvedColor("AppWindowBackground") == Color.Parse("#FFF3F3F3"), "窗口2 释放后自身回 Fluent 基线");
    metroDark.Dispose();
    Harness.PumpTicks();

    // ---------- 10. 连续切换 3 轮（Fluent→Metro→M3→Aero→Luna→Fluent）不积累 ----------
    for (var round = 1; round <= 3; round++)
    {
        var churnMetro = SkinLease.Apply(probe.Window, SkinCatalog.Resolve("metro"), ThemeVariant.Light);
        Harness.PumpTicks();
        Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount + 1
              && probe.Window.Styles.Count == baselineStyleCount + 1, $"第{round}轮 Metro 只追加自身覆盖");
        churnMetro.Dispose();
        var churnM3 = SkinLease.Apply(probe.Window, SkinCatalog.Resolve("m3"), ThemeVariant.Dark);
        Harness.PumpTicks();
        Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount + 1
              && probe.Window.Styles.Count == baselineStyleCount + 1, $"第{round}轮 M3 只追加自身覆盖");
        churnM3.Dispose();
        var churnAero = SkinLease.Apply(probe.Window, SkinCatalog.Resolve("aero"), ThemeVariant.Light);
        Harness.PumpTicks();
        Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount + 2
              && probe.Window.Styles.Count == baselineStyleCount + 1, $"第{round}轮 Aero 只追加令牌+标题栏字典与限定样式");
        churnAero.Dispose();
        var churnLuna = SkinLease.Apply(probe.Window, SkinCatalog.Resolve("luna"), ThemeVariant.Dark);
        Harness.PumpTicks();
        Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount + 1
              && probe.Window.Styles.Count == baselineStyleCount + 1, $"第{round}轮 Luna 只追加自身覆盖");
        churnLuna.Dispose();
        var churnFluent = probe.Window.ApplySkin(SkinId.Fluent, ThemeVariant.Light);
        Harness.PumpTicks();
        Check(!churnFluent.HasTokenOverrides && probe.Window.Resources.MergedDictionaries.Count == baselineDictCount,
            $"第{round}轮 Fluent 恢复不追加覆盖");
        Check(probe.ResolvedColor("AppWindowBackground") == baselineWindow && probe.Card.CornerRadius == baselineCardRadius,
            $"第{round}轮结束时回到 Fluent 基线");
        churnFluent.Dispose();
    }
    Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount
          && probe.Window.Styles.Count == baselineStyleCount, "连续切换后总计数与基线一致（无积累）");

    // ---------- 11. 只清自身：哨兵资源/样式必须保留 ----------
    var sentinel = new ResourceDictionary { ["E7-Sentinel"] = "keep-me" };
    probe.Window.Resources.MergedDictionaries.Add(sentinel);
    var sentinelStyles = new Styles();
    probe.Window.Styles.Add(sentinelStyles);
    var withSentinelDictCount = probe.Window.Resources.MergedDictionaries.Count;
    var withSentinelStyleCount = probe.Window.Styles.Count;
    var sentinelLease = SkinLease.Apply(probe.Window, SkinCatalog.Resolve("metro"), ThemeVariant.Light);
    Harness.PumpTicks();
    sentinelLease.Dispose();
    Harness.PumpTicks();
    Check(probe.Window.Resources.MergedDictionaries.Contains(sentinel) && probe.Window.Styles.Contains(sentinelStyles),
        "皮肤释放未清空他人的资源/样式");
    Check(probe.Window.Resources.MergedDictionaries.Count == withSentinelDictCount
          && probe.Window.Styles.Count == withSentinelStyleCount, "哨兵存在时计数也回到哨兵基线");

    // ---------- 12. 未知键应用：解释性回退且不动目标 ----------
    var unknownLease = probe.Window.ApplySkin("future-uninstalled-skin", ThemeVariant.Light);
    Harness.PumpTicks();
    Check(unknownLease.Resolution.FellBack && unknownLease.Skin == SkinId.Fluent && !unknownLease.HasTokenOverrides,
        "未知键应用回退 Fluent 且不追加覆盖");
    Check(probe.Window.Resources.MergedDictionaries.Count == withSentinelDictCount
          && probe.Window.Styles.Count == withSentinelStyleCount, "未知键回退不改动目标资源/样式");
    unknownLease.Dispose();

    // ---------- 13. Control 作用域：子树换肤，窗口其他区域不受影响 ----------
    var scopedPanel = new StackPanel { Spacing = 6 };
    var scopedButton = new Button { Content = "子树按钮", Width = 150 };
    scopedPanel.Children.Add(scopedButton);
    var outsideButton = new Button { Content = "窗口按钮", Width = 150 };
    var host = (Grid)probe2.Surface.Child!; // 复用窗口2 已释放的布局（追加子项做对照）
    host.Children.Add(scopedPanel);
    host.Children.Add(outsideButton);
    Harness.PumpTicks();
    var scopedLease = scopedPanel.ApplySkin(SkinId.Material3, ThemeVariant.Light);
    Harness.PumpTicks();
    Check(!scopedLease.OwnsVariantScope && scopedLease.VariantApplied, "Control 目标不接管变体作用域但变体一致");
    Check(ColorOf(scopedButton.Background) == Color.Parse("#FFE8DEF8"), "Control 子树按钮应用 M3 tonal 色");
    Check(ColorOf(outsideButton.Background) == baselineButton, "同一窗口子树外按钮保持 Fluent 基线");
    Check(probe2.Window.Resources.MergedDictionaries.Count == 0, "Control 作用域不向窗口追加资源");
    Check(ReferenceEquals(scopedPanel.Parent, host) && Equals(scopedButton.Content, "子树按钮"),
        "换肤不改动控件树与内容（业务 VM/挂载不被销毁）");
    scopedLease.Dispose();
    Harness.PumpTicks();
    Check(ColorOf(scopedButton.Background) == baselineButton, "Control 释放后子树按钮回基线");
    Check(scopedButton.Background is not null && outsideButton.Background is not null, "释放后两按钮均可读背景");

    // ---------- 14. Control 目标变体不一致时如实报告（不伪装主题） ----------
    var mismatchPanel = new StackPanel();
    mismatchPanel.Children.Add(new Button { Content = "变体探针" });
    host.Children.Add(mismatchPanel);
    Harness.PumpTicks();
    var mismatch = mismatchPanel.ApplySkin(SkinId.Metro, ThemeVariant.Dark);
    Check(!mismatch.OwnsVariantScope && !mismatch.VariantApplied && mismatch.VariantNote.Contains("不一致"),
        "Control 目标请求变体与宿主不一致时如实报告且不伪造主题作用域");
    mismatch.Dispose();
    Harness.PumpTicks();

    // ---------- 15. 语义令牌齐备 + 亮暗对比（两皮肤 × 明暗） ----------
    string[] requiredTokens =
    [
        "AppTextPrimary", "AppTextSecondary", "AppTextDim", "AppBorderBrush", "AppHover", "AppPressed",
        "AppFocusBrush", "AppListSelected", "AppSelectionForeground", "AppDisabledForeground",
        "AppErrorBackground", "AppErrorBorder", "AppSuccessForeground", "AppSuccessBackground",
        "AppCautionForeground", "AppCautionBackground", "AppCriticalForeground",
        "AppTerminalBackground", "AppTerminalForeground",
        "AppDiffAddedBackground", "AppDiffAddedForeground", "AppDiffRemovedBackground", "AppDiffRemovedForeground",
        "ControlCornerRadius", "OverlayCornerRadius", "AppCornerCard", "AppCornerInput", "AppCornerPill", "AppCornerFlyout",
        "AppFontUi", "AppFontDisplay", "AppFontMono", "AppFontSizeBase", "AppShadowFlyout", "AppShadowToast",
        "ControlElevationBorderBrush", "ControlStrokeColorDefaultBrush",
        "AppChromeBackground", "AppTitleBarBackground", "AppDockHeaderBackground",
        "ButtonBackground", "ButtonBorderBrush", "AccentButtonBackground", "ListBoxItemBackgroundSelected",
    ];
    foreach (var skinKey in new[] { "metro", "material3", "aero", "luna", "liquidglass" })
    {
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var sweep = probe.Window.ApplySkin(skinKey, variant);
            Harness.PumpTicks();
            var missing = requiredTokens.Where(token => probe.Resolved(token) is null).ToArray();
            Check(missing.Length == 0,
                $"{skinKey} {variant.Key} 语义令牌齐备（缺 {missing.Length} 个：{string.Join(",", missing)}）");
            var primary = ColorOf(probe.Resolved("AppTextPrimary") as IBrush);
            var secondary = ColorOf(probe.Resolved("AppTextSecondary") as IBrush);
            var background = ColorOf(probe.Resolved("AppWindowBackground") as IBrush);
            Check(primary is not null && background is not null && Contrast(primary.Value, background.Value) >= 4.5,
                $"{skinKey} {variant.Key} 主文字对窗口底对比度 ≥ 4.5（实际 {DescribeContrast(primary, background)}）");
            Check(secondary is not null && background is not null && Contrast(secondary.Value, background.Value) >= 3.0,
                $"{skinKey} {variant.Key} 次文字对窗口底对比度 ≥ 3.0（实际 {DescribeContrast(secondary, background)}）");
            sweep.Dispose();
            Harness.PumpTicks();
        }
    }

    // ---------- 16. 125% 工装缩放帧（真实系统 DPI 未验，仅说明） ----------
    var scaleProbe = Probe.Create(800, 640);
    scaleProbe.Window.Show();
    Harness.PumpTicks();
    var scaleLease = scaleProbe.Window.ApplySkin(SkinId.Metro, ThemeVariant.Dark);
    scaleProbe.Window.SetRenderScaling(1.25);
    Harness.PumpTicks();
    Check(Math.Abs(scaleProbe.Window.RenderScaling - 1.25) < 0.001, "工装缩放 125% 生效（仅工装近似）");
    Check(Harness.Screenshot(scaleProbe.Window, Path.Combine(evidenceRoot, "metro-dark-800x640-scale125.png")), "写出 125% 工装帧");
    scaleLease.Dispose();
    scaleProbe.Window.Close();

    probe2.Window.Close();
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

static string DescribeBrush(IBrush? brush) => brush switch
{
    ISolidColorBrush solid => solid.Color.ToString(),
    LinearGradientBrush gradient => $"渐变({gradient.GradientStops.Count} 段)",
    null => "(null)",
    _ => brush.GetType().Name,
};

/// <summary>独立加载一套皮肤的令牌字典（不挂到任何窗口，验证资产可单独解析）。</summary>
static ResourceDictionary LoadSkinDictionary(string uri) =>
    AvaloniaXamlLoader.Load(new Uri(uri)) as ResourceDictionary
    ?? throw new InvalidOperationException($"皮肤资源不是 ResourceDictionary：{uri}");

static object? DictionaryValue(ResourceDictionary dictionary, string key, ThemeVariant variant) =>
    dictionary.TryGetResource(key, variant, out var value) ? value : null;

/// <summary>读取某皮肤某一变体下的全部令牌键名（用于跨皮肤漏键核对）。</summary>
static System.Collections.Generic.HashSet<string> SkinDictionaryKeys(string skinKey, ThemeVariant variant)
{
    var definition = SkinCatalog.Get(SkinCatalog.Resolve(skinKey).Skin);
    var dictionary = LoadSkinDictionary(definition.TokenDictionaryUri!);
    var keys = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
    if (dictionary.ThemeDictionaries.TryGetValue(variant, out var provider) && provider is ResourceDictionary themed)
        foreach (var key in ((System.Collections.Generic.IDictionary<object, object>)themed).Keys)
            if (key is string text)
                keys.Add(text);
    return keys;
}

static string DescribeContrast(Color? foreground, Color? background) =>
    foreground is { } fg && background is { } bg ? Contrast(fg, bg).ToString("F2") : "n/a";

/// <summary>WCAG 相对亮度对比度（皮肤令牌均为不透明色，无需 alpha 合成）。</summary>
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
