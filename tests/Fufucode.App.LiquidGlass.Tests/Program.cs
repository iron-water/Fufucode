using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Fufucode.App.Features.Skins;
using Fufucode.App.Features.Skins.LiquidGlass;
using Fufucode.App.LiquidGlass.Tests;

// LiquidGlass 定向测试（独立入口，未注册全量自测）：
//   dotnet run --project tests/Fufucode.App.LiquidGlass.Tests -c Release
// 覆盖：LiquidGlassSkin/Lease 令牌亮暗解析、adapter 识别/包覆/还原/引用计数、
//       SyncFrom 透明级别同步、普通面板反例、headless 实色降级渲染帧、
//       释放只清自身、失败回滚、SkinLease 集成路径（AttachCaption/扫描）。
var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}

var evidenceRoot = Path.Combine(FindRepoRoot(), "tests", "Fufucode.App.LiquidGlass.Tests", "evidence");
Directory.CreateDirectory(evidenceRoot);

try
{
    // ---------- 1. 启动无头 App（Skia 真实渲染） ----------
    AppBuilder.Configure<TestApp>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia()
        .SetupWithoutStarting();

    var probe = Probe.Create(900, 640);
    probe.Window.Show();
    Harness.PumpTicks();

    var baselineDictCount = probe.Window.Resources.MergedDictionaries.Count;
    var baselineStyleCount = probe.Window.Styles.Count;
    var baselineWindowBackground = probe.ResolvedColor("AppWindowBackground");
    var baselineChromeParent = probe.ChromeBar.Parent;
    var baselineDockParent = probe.DockHost.Parent;
    var baselineToastParent = probe.Toast.Parent;

    Check(!LiquidGlassSkin.IsAppliedTo(probe.Window), "基线：LiquidGlass 未应用");
    Check(!LiquidGlassAdapter.IsAttached(probe.ChromeBar), "基线：chromeBar 未包覆");

    // ---------- 2. LiquidGlassSkin.Apply：令牌 + 样式 + adapter 全链路 ----------
    var lease = LiquidGlassSkin.Apply(probe.Window, ThemeVariant.Light);
    Harness.PumpTicks();
    Check(LiquidGlassSkin.IsAppliedTo(probe.Window), "LiquidGlass 应用后 LiquidGlassAccent 探针键可解析");
    Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount + 1,
        "LiquidGlass 应用追加 1 个令牌字典");
    Check(probe.Window.Styles.Count == baselineStyleCount + 1, "LiquidGlass 应用追加 1 个限定样式");
    Check(lease.OwnsVariantScope && probe.Window.RequestedThemeVariant == ThemeVariant.Light, "顶层窗口租约接管 RequestedThemeVariant");

    // ---------- 3. 亮色 iOS 令牌 ----------
    Check(probe.ResolvedColor("AppWindowBackground") == Color.Parse("#FFF2F2F7"), "亮色窗口底 iOS systemGray6 #F2F2F7");
    Check(probe.ResolvedColor("AppSurfaceBackground") == Color.Parse("#FFFFFFFF"), "亮色内容面 iOS 白");
    Check(probe.ResolvedColor("AppTextPrimary") == Color.Parse("#FF000000"), "亮色 label 黑");
    Check(probe.ResolvedColor("LiquidGlassAccent") == Color.Parse("#FF007AFF"), "探针键 iOS systemBlue #007AFF");
    Check(probe.ResolvedColor("LiquidGlassSurfaceColor") == Color.Parse("#A6FFFFFF"), "玻璃表面色半透明 iOS 灰");
    Check(probe.ResolvedCorner("ControlCornerRadius") == new CornerRadius(10), "ControlCornerRadius=10");
    Check(probe.ResolvedCorner("AppCornerCard") == new CornerRadius(14), "AppCornerCard=14");
    Check(probe.ResolvedCorner("AppCornerPill") == new CornerRadius(20), "AppCornerPill=20");
    Check(probe.ResolvedCorner("AppCornerFlyout") == new CornerRadius(14), "AppCornerFlyout=14");
    Check(ColorOf(probe.Resolved("AppTerminalBackground") as IBrush) == Color.Parse("#FF1C1C1E"), "亮色终端底 iOS 深灰");
    // 正文/终端不透明（玻璃只给 chrome 表面）
    foreach (var key in new[] { "AppContentBackground", "AppTerminalBackground", "AppSurfaceBackground" })
        Check(probe.Resolved(key) is ISolidColorBrush { Color.A: 255 }, $"{key} 不透明（正文不用玻璃）");

    // ---------- 4. Adapter 识别与包覆 ----------
    Check(LiquidGlassAdapter.IsGlassHost(probe.ChromeBar), "CustomCaption/chromeBar 命中玻璃宿主");
    Check(LiquidGlassAdapter.IsGlassHost(probe.DockHost), "SidebarBorder/dockHost 命中玻璃宿主");
    Check(LiquidGlassAdapter.IsGlassHost(probe.StatusStrip), "CustomStatusBar/statusStrip 命中玻璃宿主");
    Check(LiquidGlassAdapter.IsGlassHost(probe.Toast), "toast 类命中玻璃宿主");
    Check(LiquidGlassAdapter.IsGlassHost(probe.OverlayPanel), "overlayPanel 类命中玻璃宿主");
    Check(!LiquidGlassAdapter.IsGlassHost(probe.PlainPanel), "普通 card 面板不命中（反例）");
    Check(!LiquidGlassAdapter.IsGlassHost(probe.Card), "card 类不命中");

    Check(LiquidGlassAdapter.IsCaptionHost(probe.ChromeBar), "chromeBar 按标题栏语义");
    Check(!LiquidGlassAdapter.IsCaptionHost(probe.DockHost), "dockHost 非标题栏语义");
    Check(LiquidGlassAdapter.IsCaptionHost(probe.StatusStrip), "statusStrip 同 chromeBar 类按 caption 处理");

    Check(LiquidGlassAdapter.IsAttached(probe.ChromeBar), "Apply 后 chromeBar 已包覆");
    Check(LiquidGlassAdapter.IsAttached(probe.DockHost), "Apply 后 dockHost 已包覆");
    Check(LiquidGlassAdapter.IsAttached(probe.StatusStrip), "Apply 后 statusStrip 已包覆");
    Check(LiquidGlassAdapter.IsAttached(probe.Toast), "Apply 后 toast 已包覆");
    Check(LiquidGlassAdapter.IsAttached(probe.OverlayPanel), "Apply 后 overlayPanel 已包覆");
    Check(!LiquidGlassAdapter.IsAttached(probe.PlainPanel), "普通面板未被包覆");

    Check(probe.ChromeBar.Parent is LiquidGlassSurface, "chromeBar 父级为 LiquidGlassSurface");
    Check(probe.ChromeBar.Classes.Contains("liquidGlass") && probe.ChromeBar.Classes.Contains("liquidGlassCaption")
          && probe.ChromeBar.Classes.Contains("glass"), "chromeBar 带 liquidGlass/liquidGlassCaption/glass 类");
    Check(probe.DockHost.Classes.Contains("liquidGlass") && !probe.DockHost.Classes.Contains("liquidGlassCaption"),
        "dockHost 带 liquidGlass 但无 caption 类");
    var captionSurface = (LiquidGlassSurface)probe.ChromeBar.Parent!;
    var dockSurface = (LiquidGlassSurface)probe.DockHost.Parent!;
    Check(ReferenceEquals(captionSurface.Child, probe.ChromeBar), "Surface.Child 为原宿主 Border");
    Check(captionSurface.BlurRadius >= dockSurface.BlurRadius, "caption 玻璃强度不低于 dock（BlurRadius）");
    Check(!captionSurface.ShadowEnabled && dockSurface.ShadowEnabled, "caption 无投影、dock 有投影（iOS 语义）");

    // ---------- 4b. iOS interactive() 语义：非标题栏玻璃是 InteractiveSurface ----------
    Check(dockSurface is LiquidGlassInteractiveSurface interactiveDock
          && interactiveDock.IsInteractive && interactiveDock.InteractiveHighlightEnabled,
        "dock 玻璃为可交互面（指尖点亮+形变；iOS interactive()）");
    Check(captionSurface is not LiquidGlassInteractiveSurface,
        "caption 保持静态面（按压=窗口拖动，不该有点亮形变）");
    Check(probe.Toast.Parent is LiquidGlassInteractiveSurface,
        "toast 玻璃同样可交互");

    // ---------- 4c. Progressive blur（iOS scroll edge soft fade 语义） ----------
    Check(dockSurface.ProgressiveBlurEnabled && dockSurface.ProgressiveBlurStart == 0.0
          && dockSurface.ProgressiveBlurEnd > 0.0,
        "dock 玻璃启用 progressive blur（顶部向内容渐隐）");
    Check(!captionSurface.ProgressiveBlurEnabled, "caption 无 progressive blur");

    // ---------- 5. SyncFrom：平台模糊 ↔ BackdropOpacity ----------
    var adapter = lease.Adapters.First(a => ReferenceEquals(a.Host, probe.ChromeBar));
    adapter.SyncFrom(probe.Window);
    // headless 下 ActualTransparencyLevel 通常为 None → BackdropOpacity=1.0（无平台模糊时玻璃自持）
    Check(captionSurface.BackdropOpacity is >= 0.8 and <= 1.0,
        $"SyncFrom 后 BackdropOpacity 在 [0.8,1.0]（实际 {captionSurface.BackdropOpacity}）");

    // ---------- 6. 样式层：宿主背景透明，玻璃从 Surface 透出 ----------
    Check(probe.ChromeBar.Background is null || probe.ChromeBar.Background is ISolidColorBrush { Color.A: 0 },
        "chromeBar 背景透明（.liquidGlass 样式）");
    Check(probe.DefaultButton.CornerRadius == new CornerRadius(10), "按钮真实圆角 10");
    Check(probe.AccentButton.BorderThickness == new Thickness(0), "主按钮无描边");
    Check(probe.Card.CornerRadius == new CornerRadius(14), "卡片真实圆角 14");
    Check(probe.SettingsRow.CornerRadius == new CornerRadius(14), "设置行真实圆角 14");

    // ---------- 7. 降级渲染：无 Skia 快照时 LiquidGlassSurface 画实色 + 细描边 ----------
    var fallbackSurface = new LiquidGlassSurface
    {
        Width = 200, Height = 48,
        SurfaceColor = Color.Parse("#B8F7F7F9"),
        CornerRadius = new CornerRadius(10),
        Child = new Border { Background = Brushes.Transparent },
    };
    var fallbackHost = new Border { Width = 220, Height = 60, Child = fallbackSurface, Background = Brushes.White };
    var fallbackWindow = new Window { Width = 240, Height = 80, Content = fallbackHost };
    fallbackWindow.Show();
    Harness.PumpTicks();
    Check(Harness.Screenshot(fallbackWindow, Path.Combine(evidenceRoot, "liquidglass-fallback.png")),
        "无快照场景 LiquidGlassSurface 渲染帧可写（DrawFallback 实色降级）");
    fallbackWindow.Close();

    // ---------- 8. 暗色热切换 ----------
    probe.Window.RequestedThemeVariant = ThemeVariant.Dark;
    Harness.PumpTicks();
    Check(probe.ResolvedColor("AppWindowBackground") == Color.Parse("#FF000000"), "暗色窗口底 iOS 黑");
    Check(probe.ResolvedColor("LiquidGlassAccent") == Color.Parse("#FF0A84FF"), "暗色探针键 iOS 暗蓝 #0A84FF");
    Check(probe.ResolvedColor("LiquidGlassSurfaceColor") == Color.Parse("#7A1C1C22"), "暗色玻璃表面 iOS 深灰");
    Check(Harness.Screenshot(probe.Window, Path.Combine(evidenceRoot, "liquidglass-dark-900x640.png")), "写出 LiquidGlass 暗色帧");
    // 选中行必须可读：AppAccentSubtle 淡底 vs AccentTextFillColorPrimaryBrush 字色不能同色
    // （未注入 accent 的工装环境靠令牌字典的 iOS 淡色兜底保证对比——这是回归点）。
    var lgSelected = probe.ListBox.GetVisualDescendants().OfType<ListBoxItem>()
        .FirstOrDefault(item => item.IsSelected)?
        .GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault();
    Check(lgSelected is not null, "工装 ListBox 有选中项模板呈现器");
    Check(ColorOf(lgSelected!.Background) != ColorOf(lgSelected.Foreground),
        $"暗色选中行底/字不同色（实际 bg={ColorOf(lgSelected.Background)}, fg={ColorOf(lgSelected.Foreground)}）");
    probe.Window.RequestedThemeVariant = ThemeVariant.Light;
    Harness.PumpTicks();

    // ---------- 8b. AdaptiveLuminance：采样玻璃底下内容亮度，写 underDark/underLight 类 ----------
    // 渲染几帧让 snapshot + 250ms DispatcherTimer 采样都跑起来（无 GPU 时 TryGetSnapshot
    // 为 null、不置类——以下只断言"类合法且互斥"以及"释放后清干净"，不假设具体朝向）。
    Harness.PumpTicks(20);
    foreach (var host in new[] { probe.ChromeBar, probe.DockHost, probe.StatusStrip, probe.Toast, probe.OverlayPanel })
        Check(!(host.Classes.Contains("underDark") && host.Classes.Contains("underLight")),
            $"{host.Name ?? host.Classes[0]} underDark/underLight 互斥");
    // 若 snapshot 可用，至少一个宿主应被分类（玻璃底下要么亮要么暗）。
    if (LiquidGlassBackdropProvider.TryGetSnapshot(captionSurface) is not null)
        Check(probe.ChromeBar.Classes.Contains("underDark") || probe.ChromeBar.Classes.Contains("underLight")
              || probe.DockHost.Classes.Contains("underDark") || probe.DockHost.Classes.Contains("underLight"),
            "snapshot 可用时至少一个玻璃宿主被亮度分类");

    // ---------- 9. 引用计数：同一宿主重复 Attach 共享状态 ----------
    var hostProbe = new Border { Name = "NotificationPrompt", Classes = { "toast" }, Child = new TextBlock { Text = "x" } };
    var hostPanel = new StackPanel();
    hostPanel.Children.Add(hostProbe);
    var first = LiquidGlassAdapter.Attach(hostProbe);
    var second = LiquidGlassAdapter.Attach(hostProbe);
    Check(ReferenceEquals(first.Surface, second.Surface), "重复 Attach 共享同一 Surface");
    Check(hostPanel.Children.Count == 1 && hostPanel.Children[0] is LiquidGlassSurface, "重复 Attach 不产生第二个 Surface");
    first.Dispose();
    Check(LiquidGlassAdapter.IsAttached(hostProbe), "一次 Dispose 后引用计数仍持有包覆");
    second.Dispose();
    Check(!LiquidGlassAdapter.IsAttached(hostProbe) && ReferenceEquals(hostPanel.Children[0], hostProbe),
        "计数归零后宿主还原回原位");

    // ---------- 10. 释放：包覆还原 + 字典/样式移除 + 幂等 ----------
    lease.Dispose();
    Harness.PumpTicks();
    Check(!LiquidGlassAdapter.IsAttached(probe.ChromeBar), "释放后 chromeBar 不再包覆");
    Check(ReferenceEquals(probe.ChromeBar.Parent, baselineChromeParent), "释放后 chromeBar 父级还原");
    Check(ReferenceEquals(probe.DockHost.Parent, baselineDockParent), "释放后 dockHost 父级还原");
    Check(ReferenceEquals(probe.Toast.Parent, baselineToastParent), "释放后 toast 父级还原");
    Check(!probe.ChromeBar.Classes.Contains("liquidGlass") && !probe.ChromeBar.Classes.Contains("glass"),
        "释放后玻璃类还原");
    Check(!probe.ChromeBar.Classes.Contains("underDark") && !probe.ChromeBar.Classes.Contains("underLight")
          && !probe.DockHost.Classes.Contains("underDark") && !probe.DockHost.Classes.Contains("underLight"),
        "释放后 underDark/underLight 亮度类一并清除");
    Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount
          && probe.Window.Styles.Count == baselineStyleCount, "释放后字典/样式计数回基线");
    Check(!LiquidGlassSkin.IsAppliedTo(probe.Window), "释放后 LiquidGlassAccent 不可再解析");
    Check(probe.ResolvedColor("AppWindowBackground") == baselineWindowBackground, "释放后窗口底回 Fluent 基线");
    lease.Dispose();
    Check(probe.Window.Resources.MergedDictionaries.Count == baselineDictCount, "重复 Dispose 幂等");

    // ---------- 11. SkinLease 集成路径：liquidglass 键走通用皮肤管线 ----------
    var skinLease = SkinLease.Apply(probe.Window, SkinCatalog.Resolve("liquidglass"), ThemeVariant.Light);
    Harness.PumpTicks();
    Check(skinLease.Skin == SkinId.LiquidGlass && skinLease.HasTokenOverrides && skinLease.HasScopedStyles,
        "SkinLease liquidglass 键挂载令牌与样式");
    Check(LiquidGlassAdapter.IsAttached(probe.ChromeBar), "SkinLease 路径同样扫到并包覆 chromeBar");
    Check(LiquidGlassAdapter.IsAttached(probe.DockHost), "SkinLease 路径同样包覆 dockHost");
    skinLease.Dispose();
    Harness.PumpTicks();
    Check(!LiquidGlassAdapter.IsAttached(probe.ChromeBar) && probe.Window.Resources.MergedDictionaries.Count == baselineDictCount,
        "SkinLease 释放后包覆与字典一并还原");

    // ---------- 12. 失败回滚：坏样式 URI 不留半挂层 ----------
    var beforeFailDict = probe.Window.Resources.MergedDictionaries.Count;
    var beforeFailStyles = probe.Window.Styles.Count;
    var threw = false;
    try
    {
        LiquidGlassLease.ApplyCore(probe.Window, ThemeVariant.Light,
            LiquidGlassSkin.TokenDictionaryUri,
            "avares://Fufucode.App/Styles/Skins/LiquidGlass/DoesNotExist.axaml",
            attachChrome: false);
    }
    catch
    {
        threw = true;
    }
    Harness.PumpTicks();
    Check(threw, "坏样式 URI：应用抛错而非静默成功");
    Check(probe.Window.Resources.MergedDictionaries.Count == beforeFailDict
          && probe.Window.Styles.Count == beforeFailStyles, "失败回滚后无半挂字典/样式");
    Check(!LiquidGlassSkin.IsAppliedTo(probe.Window), "失败回滚后未标记为已应用");
    Check(!LiquidGlassAdapter.IsAttached(probe.ChromeBar), "失败回滚后无半挂包覆");

    // ---------- 13. 只清自身：哨兵资源/样式必须保留 ----------
    var sentinel = new ResourceDictionary { ["LG-Sentinel"] = "keep-me" };
    probe.Window.Resources.MergedDictionaries.Add(sentinel);
    var sentinelStyles = new Styles();
    probe.Window.Styles.Add(sentinelStyles);
    var sentinelLease = LiquidGlassSkin.Apply(probe.Window, ThemeVariant.Light);
    Harness.PumpTicks();
    sentinelLease.Dispose();
    Harness.PumpTicks();
    Check(probe.Window.Resources.MergedDictionaries.Contains(sentinel) && probe.Window.Styles.Contains(sentinelStyles),
        "LiquidGlass 释放未清空他人资源/样式");
    probe.Window.Resources.MergedDictionaries.Remove(sentinel);
    probe.Window.Styles.Remove(sentinelStyles);

    // ---------- 14. 亮色证据帧 ----------
    var shotLease = LiquidGlassSkin.Apply(probe.Window, ThemeVariant.Light);
    Harness.PumpTicks();
    Check(Harness.Screenshot(probe.Window, Path.Combine(evidenceRoot, "liquidglass-light-900x640.png")), "写出 LiquidGlass 亮色帧");
    shotLease.Dispose();
    Harness.PumpTicks();

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

static string FindRepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null &&
           (!File.Exists(Path.Combine(directory.FullName, ".git")) && !Directory.Exists(Path.Combine(directory.FullName, ".git"))
            || !Directory.Exists(Path.Combine(directory.FullName, "src", "Fufucode.App"))))
        directory = directory.Parent;
    return directory?.FullName ?? throw new InvalidOperationException("未找到仓库根目录");
}
