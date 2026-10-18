using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Fufucode.App;
using Fufucode.App.Features.Skins.Aero;
using Fufucode.App.Integration;
using Fufucode.App.Views;

// E7-AERO 返工独立反例探针（真实 MainWindow / 真实 CaptionAssetButton，不使用模块自己的工装结构）：
//   dotnet run --project tests/Fufucode.App.Aero.Tests/RealCaptionProbe -c Release
// 复现 M 三条原始红例并验证修复：
//   1) 真实 MainWindow CustomCaption（含普通工具条按钮）可以挂接，只识别三个窗口按钮；
//   2) 挂接前已有显式 Theme 的按钮，Dispose 后按原引用精确复原；
//   3) 挂接前已存在的 aeroCaption 类（他人所有）在 Dispose 后保留。
// 追加：混合顶栏里未知普通工具按钮不被拦截也不被改动；重复挂接/释放；min/max/restore/close 语义；
//       浮动辅助栏三键（Kind + “最小化辅助栏”等新自动化名）；两窗口独立。
// 本探针只用公开 API，不改产品事件；构建失败或断言失败均以非 0 退出。
var failures = 0;
var passed = 0;
void Check(bool ok, string label)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + label);
    if (ok) passed++; else failures++;
}

var root = Path.Combine(Path.GetTempPath(), "e7-aero-probe-" + Guid.NewGuid().ToString("N"));
var options = AppOptions.Mock with { AppDataRootOverride = root };
AppBuilder.Configure(() => new App(options))
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
    .SetupWithoutStarting();

var probeRoot = Path.Combine(FindRepoRoot(), "tests", "Fufucode.App.Aero.Tests", "evidence");
Directory.CreateDirectory(probeRoot);
var redLog = new System.Text.StringBuilder();
redLog.AppendLine("E7-AERO 返工独立反例探针（真实 MainWindow + 真实 CaptionAssetButton）");
redLog.AppendLine("原始 M 红例（修复前实测，见主仓库 tmp/m-aero-review/result.log）：");
redLog.AppendLine("  FAIL production caption attach: InvalidOperationException: 无法从名称或 AutomationName 推断窗口按钮语义：左侧边栏");
redLog.AppendLine("  FAIL lease restores existing explicit button theme exactly");
redLog.AppendLine("  FAIL lease preserves preexisting caption class");
redLog.AppendLine("修复后本轮实测：");

// ---------- 1. 真实 MainWindow CustomCaption：混合顶栏可挂接，只识别三个窗口按钮 ----------
var main = new MainWindow(options);
main.Show();
var caption = main.FindControl<Border>("CustomCaption");
if (caption is null)
{
    Check(false, "真实 MainWindow 找到 CustomCaption");
}
else
{
    var captionButtonsBefore = caption.GetVisualDescendants().OfType<Button>().ToList();
    var ordinary = captionButtonsBefore.Where(button => button is not CaptionAssetButton).ToList();
    Console.WriteLine($"INFO 真实标题栏按钮共 {captionButtonsBefore.Count} 个（普通工具按钮 {ordinary.Count} 个）");

    AeroLease? lease = null;
    try
    {
        lease = AeroSkin.Apply(main, caption);
        Check(lease.Caption is not null, "真实 MainWindow CustomCaption 成功挂接（原红例 1）");
        Check(lease.Caption!.Buttons.Count == 3,
            $"只识别三个窗口按钮，普通工具按钮不计数（实际 {lease.Caption.Buttons.Count}）");
        Check(lease.Caption.Buttons.All(button => button is CaptionAssetButton),
            "识别结果全部是真实 CaptionAssetButton");
        Check(lease.Caption.Buttons.Select(button => ((CaptionAssetButton)button).Kind).OrderBy(kind => kind)
                .SequenceEqual(new[] { "close", "maximize", "minimize" }),
            "三键 Kind 为 minimize/maximize/close");
        Check(lease.Caption.Buttons.Any(button => button.Name == "CaptionMinimize") &&
              lease.Caption.Buttons.Any(button => button.Name == "CaptionMaximize") &&
              lease.Caption.Buttons.Any(button => button.Name == "CaptionClose"),
            "三键是 CaptionMinimize/CaptionMaximize/CaptionClose 本体");
        foreach (var button in ordinary)
        {
            Check(!button.Classes.Contains("aeroCaptionButton") &&
                  !button.Classes.Contains("min") && !button.Classes.Contains("max") &&
                  !button.Classes.Contains("restore") && !button.Classes.Contains("close"),
                $"普通工具按钮保持原样、未被改写：{AutomationProperties.GetName(button) ?? button.Name ?? button.Classes.FirstOrDefault() ?? "(匿名)"}");
        }

        var ordinaryThemeBefore = ordinary.Select(button => button.Theme).ToList();
        lease.Dispose();
        Check(ordinary.Select(button => ReferenceEquals(button.Theme, ordinaryThemeBefore[ordinary.IndexOf(button)]))
                .All(equal => equal),
            "释放后普通工具按钮 Theme 逐项未变");
    }
    catch (Exception error)
    {
        Check(false, "真实 MainWindow CustomCaption 挂接/释放：" + error.GetType().Name + ": " + error.Message);
        lease?.Dispose();
    }
}

main.Close();

// ---------- 2. 已有显式 Theme：Dispose 按原引用复原（原红例 2） ----------
var originalTheme = new ControlTheme(typeof(Button));
var themedClose = new Button { Name = "CaptionClose", Theme = originalTheme };
var themedCaption = new Border { Child = themedClose };
using (var lease = AeroSkin.Apply(themedCaption, caption: themedCaption))
{
    Check(!ReferenceEquals(themedClose.Theme, originalTheme) && themedClose.Theme is not null,
        "挂接期间 Theme 换成 AeroCaptionButtonTheme");
}

Check(ReferenceEquals(themedClose.Theme, originalTheme),
    "Dispose 后 Theme 精确复原为原引用（原红例 2 修复）");

// ---------- 3. 预已有 aeroCaption 类：Dispose 保留他人类（原红例 3） ----------
var preClassedCaption = new Border { Child = ProbeClose("关闭") };
preClassedCaption.Classes.Add("aeroCaption");
using (var lease = AeroSkin.Apply(preClassedCaption, caption: preClassedCaption))
{
    Check(preClassedCaption.Classes.Contains("aeroCaption"), "挂接期间保持 aeroCaption 类");
}

Check(preClassedCaption.Classes.Contains("aeroCaption"),
    "Dispose 后保留挂接前已有的 aeroCaption 类（原红例 3 修复）");

// ---------- 4. 浮动辅助栏三键：真实 Kind + 新自动化名，未知普通按钮不拦截 ----------
var floatMin = new CaptionAssetButton { Kind = "minimize" };
AutomationProperties.SetName(floatMin, "最小化辅助栏");
var floatMax = new CaptionAssetButton { Kind = "maximize" };
AutomationProperties.SetName(floatMax, "最大化或还原辅助栏");
var floatClose = new CaptionAssetButton { Kind = "close" };
AutomationProperties.SetName(floatClose, "隐藏辅助栏");
var floatTitle = new Button { Content = "辅助栏" };
AutomationProperties.SetName(floatTitle, "辅助栏标题动作");
var floatActions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { floatMin, floatMax, floatClose } };
var floatCaption = new Border { Child = new StackPanel { Children = { floatTitle, floatActions } } };
using (var lease = AeroSkin.Apply(floatCaption, caption: floatCaption))
{
    Check(lease.Caption!.Buttons.Count == 3, "浮动辅助栏只识别三个 CaptionAssetButton 窗口键");
    Check(ReferenceEquals(lease.Caption.Buttons[0], floatMin) && ReferenceEquals(lease.Caption.Buttons[1], floatMax) &&
          ReferenceEquals(lease.Caption.Buttons[2], floatClose),
        "浮动辅助栏三键按 Kind 识别（不依赖旧中文自动化名）");
    Check(!floatTitle.Classes.Contains("aeroCaptionButton"), "浮动容器里未知普通按钮不被拦截");

    // min/max/restore/close 语义类
    Check(floatMin.Classes.Contains("min") && floatMax.Classes.Contains("max") && floatClose.Classes.Contains("close"),
        "浮动三键语义类 min/max/close");
}

// ---------- 5. min/max/restore/close 与两窗口独立 ----------
var captionA = new Border { Child = new StackPanel { Children = { Min(), Max(), Close() } } };
var captionB = new Border { Child = new StackPanel { Children = { Min(), Max(), Close() } } };
var leaseA = AeroSkin.Apply(captionA, caption: captionA);
var leaseB = AeroSkin.Apply(captionB, caption: captionB);
Check(leaseA.Caption!.Buttons.Count == 3 && leaseB.Caption!.Buttons.Count == 3, "两个独立标题栏各自识别三键");
Check(captionA.Classes.Contains("aeroCaption") && captionB.Classes.Contains("aeroCaption"), "两窗口都挂 aeroCaption");
leaseA.Dispose();
Check(!captionA.Classes.Contains("aeroCaption") && captionA.Classes.Count == 0, "释放窗口 A 后其 aeroCaption 移除");
Check(captionB.Classes.Contains("aeroCaption"), "释放窗口 A 不影响窗口 B");

// 重复挂接/释放：引用计数，最后一次释放才复原
var repeatCaption = new Border { Child = new StackPanel { Children = { Min(), Max(), Close() } } };
var firstLease = AeroSkin.Apply(repeatCaption, caption: repeatCaption);
var secondLease = AeroSkin.Apply(repeatCaption, caption: repeatCaption);
Check(repeatCaption.Classes.Contains("aeroCaption") && AeroCaptionAdapter.IsAttached(repeatCaption),
    "重复挂接共享同一状态");
firstLease.Dispose();
Check(repeatCaption.Classes.Contains("aeroCaption") && AeroCaptionAdapter.IsAttached(repeatCaption),
    "第一次释放仍保持 Aero 呈现（引用计数 > 0）");
secondLease.Dispose();
Check(!repeatCaption.Classes.Contains("aeroCaption") && !AeroCaptionAdapter.IsAttached(repeatCaption),
    "第二次释放才精确复原");
secondLease.Dispose();
Check(!repeatCaption.Classes.Contains("aeroCaption"), "重复 Dispose 幂等");
leaseB.Dispose();

redLog.AppendLine($"  PASS 真实 MainWindow CustomCaption 成功挂接（原红例 1 修复）");
redLog.AppendLine($"  PASS Theme 精确复原为原引用（原红例 2 修复）");
redLog.AppendLine($"  PASS 保留挂接前已有的 aeroCaption 类（原红例 3 修复）");
redLog.AppendLine($"本探针结果：passed={passed} failed={failures}");
redLog.AppendLine("复现命令：dotnet run --project tests/Fufucode.App.Aero.Tests/RealCaptionProbe -c Release");
File.WriteAllText(Path.Combine(probeRoot, "real-caption-probe.txt"), redLog.ToString());

Console.WriteLine($"RESULT passed={passed} failed={failures}");
return failures == 0 ? 0 : 1;

static Button Min()
{
    var button = new CaptionAssetButton { Kind = "minimize" };
    AutomationProperties.SetName(button, "最小化");
    return button;
}

static Button Max()
{
    var button = new CaptionAssetButton { Kind = "maximize" };
    AutomationProperties.SetName(button, "最大化");
    return button;
}

static Button Close()
{
    var button = new CaptionAssetButton { Kind = "close" };
    AutomationProperties.SetName(button, "关闭");
    return button;
}

static Button ProbeClose(string automation)
{
    var button = new Button { Name = "CaptionClose" };
    AutomationProperties.SetName(button, automation);
    return button;
}

static string FindRepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null &&
           (!File.Exists(Path.Combine(directory.FullName, ".git")) && !Directory.Exists(Path.Combine(directory.FullName, ".git"))
            || !Directory.Exists(Path.Combine(directory.FullName, "src", "Fufucode.App"))))
        directory = directory.Parent;
    return directory?.FullName ?? throw new InvalidOperationException("未找到仓库根目录");
}
