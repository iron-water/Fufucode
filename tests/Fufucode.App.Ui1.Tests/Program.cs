using System;
using System.Linq;
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
// 覆盖 IME 组合输入、拖拽死区、Splitter 和真实控件的资源解析。
// 颜色、样式类和 XAML 排版由设计演进，不再作为源码字符串快照。
var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}

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

// ─────────────────────────────────────────────────────────────
// 2. 4px 拖拽死区（纯逻辑）
// ─────────────────────────────────────────────────────────────
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

// 5b. 令牌在真实窗口可解析
var tnumResolved = window.TryFindResource("AppTabularNumericFeatures", window.ActualThemeVariant, out var tnumValue);
Check(tnumResolved && tnumValue is FontFeatureCollection,
    "B：窗口层可解析 AppTabularNumericFeatures 为 FontFeatureCollection");
var dividerResolved = window.TryFindResource("AppDividerSubtle", window.ActualThemeVariant, out var dividerValue);
Check(dividerResolved && dividerValue is ISolidColorBrush,
    "A：窗口层可解析 AppDividerSubtle 为画刷");

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
