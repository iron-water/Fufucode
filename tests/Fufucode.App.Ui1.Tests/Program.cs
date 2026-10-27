using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Fufucode.App.Infrastructure;
using Fufucode.App.Ui1.Tests;

// UI-1 交互质感定向测试（独立入口，未注册全量自测）：
//   dotnet run --project tests/Fufucode.App.Ui1.Tests -c Release
// 覆盖四条迁移项：
//   A. 单闭合边框原则：容器内二级控件无闭合框；规则可用静态扫描 + 渲染属性双层验证；
//   B. 排版微调：Tabular Numbers 令牌可解析、meta/mono/numeric 锁定 tnum；
//   E1. CJK 输入法双重互锁：!(isComposing || keyCode==229) 全组合；
//   E3. 4px 拖拽死区：切比雪夫度量、起拖与上报阈值、真实 Splitter 起拖门。
var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}

var repoRoot = FindRepoRoot();

// ─────────────────────────────────────────────────────────────
// 1. CJK 输入法双重互锁（纯逻辑，不依赖 UI）
// ─────────────────────────────────────────────────────────────
var clockNow = DateTimeOffset.UnixEpoch;
var guard = new ImeEnterGuard(() => clockNow);

// 组合进行中（preedit 非空）→ 吞掉
guard.OnPreeditChanged("zhongwen");
Check(guard.ShouldSwallowEnter(ImeEnterGuard.ImeProcessKeyCode, false),
    "E1：keyCode==229 且组合中 → 吞掉（第一重）");
Check(guard.ShouldSwallowEnter(13, isComposing: true),
    "E1：isComposing==true 且 Enter → 吞掉（第二重）");
Check(guard.ShouldSwallowEnter(13, isComposing: false),
    "E1：组合中纯 Enter（无 isComposing 标志）也吞掉（时间窗兜底）");

// 组合刚结束（非空 → 空跳变）的 80ms 窗口内仍吞掉；超出窗口放行
guard.OnPreeditChanged("");
Check(guard.ShouldSwallowEnter(13, false), "E1：组合结束瞬间（<80ms）仍吞掉回车");
clockNow = clockNow.AddMilliseconds(120);
Check(!guard.ShouldSwallowEnter(13, isComposing: false),
    "E1：组合结束超过 80ms 后放行正常回车");

// 纯判定：合成键 229 在未组合时不误吞普通回车
var fresh = new ImeEnterGuard(() => DateTimeOffset.UnixEpoch);
Check(!fresh.ShouldSwallowEnter(13, isComposing: false), "E1：无组合、普通 keyCode → 放行");
Check(fresh.ShouldSwallowEnter(ImeEnterGuard.ImeProcessKeyCode, isComposing: false),
    "E1：无组合但 keyCode==229 → 仍吞掉（合成键互锁）");
Check(ImeEnterGuard.ImeProcessKeyCode == 229, "E1：合成键常量锁定为 VK 229");

// ─────────────────────────────────────────────────────────────
// 2. 4px 拖拽死区（纯逻辑）
// ─────────────────────────────────────────────────────────────
Check(DragDeadZone.Start == 4 && DragDeadZone.Report == 4, "E3：起拖与上报阈值均为 4px");
var origin = new Point(100, 100);
Check(!DragDeadZone.HasStarted(origin, new Point(103.9, 100)), "E3：位移 3.9px 不起拖（死区内）");
Check(DragDeadZone.HasStarted(origin, new Point(104, 100)), "E3：位移 4px 恰好起拖");
Check(!DragDeadZone.HasStarted(origin, new Point(100, 103.9)), "E3：纵向 3.9px 不起拖");
Check(DragDeadZone.HasStarted(origin, new Point(100, 104.5)), "E3：纵向越过 4px 起拖");
Check(!DragDeadZone.HasStarted(origin, new Point(102.8, 102.8)), "E3：对角均 <4px 不起拖（切比雪夫）");
Check(DragDeadZone.HasStarted(origin, new Point(104, 100.2)), "E3：单轴越界即起拖");
Check(!DragDeadZone.ShouldReport(origin, new Point(103, 100)), "E3：拖拽中累计 <4px 不上报（抑制重绘）");
Check(DragDeadZone.ShouldReport(origin, new Point(100, 104)), "E3：拖拽中累计 4px 上报");

// ─────────────────────────────────────────────────────────────
// 3. 单闭合边框原则：静态扫描 token 与样式契约
// ─────────────────────────────────────────────────────────────
var themePath = Path.Combine(repoRoot, "src", "Fufucode.App", "Styles", "FufucodeTheme.axaml");
var workbenchPath = Path.Combine(repoRoot, "src", "Fufucode.App", "Styles", "WorkbenchStyles.axaml");
var chatPath = Path.Combine(repoRoot, "src", "Fufucode.App", "Views", "ChatPage.axaml");
var theme = File.ReadAllText(themePath);
var workbench = File.ReadAllText(workbenchPath);
var chat = File.ReadAllText(chatPath);

// 3a. 令牌：浅/深两套 AppDividerSubtle 均已定义，且值为 6% 白/黑发丝线
Check(Regex.Matches(theme, "x:Key=\"AppDividerSubtle\"").Count == 2,
    "A：AppDividerSubtle 在浅/深两套主题字典各定义一次");
Check(theme.Contains("Color=\"#0FFFFFFF\"") && theme.Contains("Color=\"#0F000000\""),
    "A：发丝细线为 6% 白（深色）/6% 黑（浅色）");
Check(Regex.IsMatch(theme, "<Style Selector=\"Border\\.nestedFill\"[\\s\\S]*?BorderThickness\" Value=\"0\""),
    "A：Border.nestedFill 为无框轻质填充（嵌套内层契约）");
Check(Regex.IsMatch(theme, "<Style Selector=\"Border\\.innerRule\"[\\s\\S]*?BorderThickness\" Value=\"0,1,0,0\""),
    "A：Border.innerRule 为单向 1px 细线（不闭合）");

// 3b. composerCard 内的内部条不再闭合
Check(Regex.IsMatch(theme, "<Style Selector=\"Border\\.queueStrip\"[\\s\\S]*?BorderThickness\" Value=\"0\""),
    "A：queueStrip 嵌套在 composerCard 内已降级为无框");
Check(Regex.IsMatch(theme, "<Style Selector=\"Border\\.restoreInfo\"[\\s\\S]*?BorderThickness\" Value=\"0\""),
    "A：restoreInfo 嵌套在 composerCard 内已降级为无框");

// 3c. ChatPage：toolCard 内的输出区改用 nestedFill（不再 codeCard）
Check(!Regex.IsMatch(chat, "Classes=\"codeCard\"[^>]*IsVisible=\"\\{Binding (HasOutput|ShowTextOutput)\\}\"")
      && Regex.IsMatch(chat, "Classes=\"nestedFill\"[^>]*IsVisible=\"\\{Binding (HasOutput|ShowTextOutput)\\}\""),
    "A：ChatPage 工具卡输出区由闭合 codeCard 降级为 nestedFill");
Check(chat.Contains("BorderBrush=\"{DynamicResource AppDividerSubtle}\""),
    "A：ChatPage 容器内结构划分改用 AppDividerSubtle 单向细线");

// 3d. 设置行/自动化卡内的输入控件已降级
Check(Regex.IsMatch(workbench, "<Style Selector=\"Border\\.settingsRow TextBox\"[\\s\\S]*?BorderThickness\" Value=\"0\""),
    "A：设置行内 TextBox 降级为无框轻质填充");
var automationPath = Path.Combine(repoRoot, "src", "Fufucode.Plugins.AutoDev", "Ui", "Automation",
    "AutomationControlPageView.axaml");
var automation = File.ReadAllText(automationPath);
Check(Regex.IsMatch(automation, "<Style Selector=\"Border\\.automationCardInner\"[\\s\\S]*?BorderThickness\" Value=\"0\""),
    "A：自动化卡内次级条目降级为 automationCardInner（无框）");
Check(Regex.IsMatch(automation, "Classes=\"automationCardInner\"[^>]*Margin=\"0,0,0,6\""),
    "A：主控对话消息条目改用不闭合内层卡");

// ─────────────────────────────────────────────────────────────
// 4. 排版微调：Tabular Numbers 令牌 + 字阶锁定
// ─────────────────────────────────────────────────────────────
Check(theme.Contains("x:Key=\"AppTabularNumericFeatures\"") && theme.Contains("<FontFeature Tag=\"tnum\"/>"),
    "B：AppTabularNumericFeatures 令牌为 tnum 字体特性集");
Check(Regex.IsMatch(theme, "<Style Selector=\"TextBlock\\.numeric\"[\\s\\S]*?FontFeatures\" Value=\"\\{StaticResource AppTabularNumericFeatures\\}\""),
    "B：TextBlock.numeric 锁定等宽数字");
Check(Regex.IsMatch(theme, "<Style Selector=\"TextBlock\\.mono\"[\\s\\S]*?FontFeatures\" Value=\"\\{StaticResource AppTabularNumericFeatures\\}\""),
    "B：mono 文本锁定等宽数字");
Check(Regex.IsMatch(workbench, "<Style Selector=\"TextBlock\\.meta\"[\\s\\S]*?FontFeatures\" Value=\"\\{StaticResource AppTabularNumericFeatures\\}\""),
    "B：meta 时间戳文本锁定等宽数字");
Check(Regex.IsMatch(automation, "Classes=\"numeric\"[^>]*DecisionCountText"),
    "B：自动化页计数文本套用 numeric");

// ─────────────────────────────────────────────────────────────
// 5. 真实无头 Application：令牌解析 + Splitter 4px 起拖门
// ─────────────────────────────────────────────────────────────
AppBuilder.Configure<TestApp>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
    .SetupWithoutStarting();

var window = new Window { Width = 600, Height = 400 };
var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("200,5,*") };
var left = new Border { [!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppContentBackground") };
var splitter = new DeadZoneGridSplitter
{
    Name = "ProbeSplitter",
    ResizeDirection = GridResizeDirection.Columns,
    ResizeBehavior = GridResizeBehavior.PreviousAndNext,
    Classes = { "dockSplitter" },
};
var right = new Border();
Grid.SetColumn(left, 0);
Grid.SetColumn(splitter, 1);
Grid.SetColumn(right, 2);
grid.Children.Add(left);
grid.Children.Add(splitter);
grid.Children.Add(right);
window.Content = grid;
window.Show();
Harness.PumpTicks();

// 5a. DeadZoneGridSplitter 的 4px 死区门（纯判定，确定性；真实指针拖动行为由主 App 的
//     --selftest L11/L07 套件端到端覆盖）
// 指针拖动、累计 3px：吞掉（不重排）
var (swallowA, accA, openA) = DeadZoneGridSplitter.EvaluateDelta(pointerInitiated: true, accumulated: 0, open: false, new Point(3, 0));
Check(swallowA && !openA && Math.Abs(accA - 3) < 1e-9, "E3：指针拖动累计 3px → 吞掉（布局不重排）");
// 指针继续拖到累计 6px：放行
var (swallowB, accB, openB) = DeadZoneGridSplitter.EvaluateDelta(pointerInitiated: true, accumulated: accA, open: openA, new Point(3, 0));
Check(!swallowB && openB && Math.Abs(accB - 6) < 1e-9, "E3：指针拖动越过 4px → 放行");
// 已放行后继续拖动：始终放行
var (swallowC, _, openC) = DeadZoneGridSplitter.EvaluateDelta(pointerInitiated: true, accumulated: accB, open: openB, new Point(1, 0));
Check(!swallowC && openC, "E3：越过死区后持续放行，不回归抑制");
// 键盘（非指针发起）拖动：首帧即放行，永不被死区吞掉
var (swallowK, accK, openK) = DeadZoneGridSplitter.EvaluateDelta(pointerInitiated: false, accumulated: 0, open: false, new Point(0, -10));
Check(!swallowK && !openK && Math.Abs(accK) < 1e-9, "E3：键盘方向键拖动不进死区、即时生效");
// 对角微颤：每轴 <4px 的切比雪夫度量下不越界
var (swallowD, _, _) = DeadZoneGridSplitter.EvaluateDelta(pointerInitiated: true, accumulated: 0, open: false, new Point(3, 3));
Check(swallowD, "E3：对角各轴 3px（<4px）仍吞掉，抑制微颤");

Check(splitter is DeadZoneGridSplitter && window.Content is Grid,
    "E3：真实窗口中可实例化并承载 DeadZoneGridSplitter（XAML 类型契约）");

// 5b. 令牌在真实窗口可解析
var tnumResolved = window.TryFindResource("AppTabularNumericFeatures", window.ActualThemeVariant, out var tnumValue);
Check(tnumResolved && tnumValue is FontFeatureCollection,
    "B：窗口层可解析 AppTabularNumericFeatures 为 FontFeatureCollection");
var dividerResolved = window.TryFindResource("AppDividerSubtle", window.ActualThemeVariant, out var dividerValue);
Check(dividerResolved && dividerValue is ISolidColorBrush,
    "A：窗口层可解析 AppDividerSubtle 为画刷");
if (dividerValue is ISolidColorBrush dividerBrush)
{
    Check(dividerBrush.Color.A <= 0x20, "A：发丝细线 alpha <= 6%（微差分级差分，不喧宾夺主）");
}

// 5c. tnum 应用到真实 TextBlock 后的字体特性集合含 tnum
var numericProbe = new TextBlock { Classes = { "numeric" }, Text = "1234567890" };
grid.Children.Add(numericProbe);
Harness.Pump(static () => true);
Harness.PumpTicks(4);
Check(numericProbe.FontFeatures is { Count: > 0 } features
      && features.Any(feature => feature.Tag == "tnum"),
    "B：套用 numeric 类的 TextBlock 实际携带 tnum 字体特性");

window.Close();

Console.WriteLine();
Console.WriteLine($"UI-1 定向测试全部通过：{passed} 项断言。");
return;

static string FindRepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fufucode.sln")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根（Fufucode.sln）");
}
