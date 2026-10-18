// M0 手机原型静态契约断言：验证夹具标记、覆盖界面、断线/审批/键盘语义无回归。
// 用法：dotnet run --project tests/Fufucode.M0.Tests；按部署基准定位原型目录。
// 交互验证（点击/输入/截图）属于浏览器层，见 docs/records/M0-delivery.md。

var root = RepoRoot();
var html = File.ReadAllText(Path.Combine(root, "docs", "frontend-prototype", "mobile", "index.html"));
var css = File.ReadAllText(Path.Combine(root, "docs", "frontend-prototype", "mobile", "mobile.css"));
var app = File.ReadAllText(Path.Combine(root, "docs", "frontend-prototype", "mobile", "js", "app.js"));
var fx = File.ReadAllText(Path.Combine(root, "docs", "frontend-prototype", "mobile", "js", "fixtures.js"));
var readme = File.ReadAllText(Path.Combine(root, "docs", "frontend-prototype", "mobile", "README.md"));

// 1. 夹具标识：数据必须显式标原型，界面上存在可见夹具标识元素。
Check.That(fx.Contains("fixture: true"), "fixture-data-marked");
Check.That(html.Contains("data-fixture"), "fixture-elements-marked");
Check.That(readme.Contains("夹具"), "readme-states-fixture");

// 2. 覆盖界面：连接/列表/聊天/审批/附件五个交互面都存在。
Check.That(html.Contains("screen-connect") && html.Contains("pairCode"), "connect-screen");
Check.That(html.Contains("sessionList") && html.Contains("projectList"), "project-session-lists");
Check.That(html.Contains("timeline") && html.Contains("composer"), "chat-timeline-composer");
Check.That(html.Contains("btnApprove") && html.Contains("btnDeny") && html.Contains("btnAlways"), "approval-actions");
Check.That(html.Contains("attachSheet") && html.Contains("chips"), "attachment-sheet-chips");

// 3. 发送/停止：互斥按钮与停止语义。
Check.That(html.Contains("btnSend") && html.Contains("btnStop"), "send-stop-buttons");
Check.That(app.Contains("clear_queue + abort"), "stop-clear-queue-abort");
Check.That(app.Contains("btnSend\").hidden = state.running"), "send-hidden-while-running");

// 4. 连接/重连/错误状态：断线横幅、宿主退出不可重连、代次失效。
Check.That(app.Contains("state.generation += 1"), "reconnect-new-generation");
Check.That(app.Contains("hostExited") && app.Contains("act.hidden = true"), "host-exit-no-reconnect");
Check.That(app.Contains("generation !== state.generation"), "stale-approval-invalidated");
Check.That(html.Contains("bannerText") && html.Contains("bannerAction"), "offline-banner");
Check.That(app.Contains("approvalValid") && app.Contains("callId !== snap.callId"),
    "approval-commit-time-revalidation");
Check.That(app.Contains("invalidateOpenApproval"), "approval-modal-invalidation");

// 5. 返回路径：聊天→列表、列表→连接 的真实返回。
Check.That(html.Contains("data-nav=\"list\"") && html.Contains("data-nav=\"connect\""), "back-paths");

// 6. 窄屏/键盘：安全区、视口高度、visualViewport 兜底。
Check.That(html.Contains("viewport-fit=cover"), "viewport-fit-cover");
Check.That(css.Contains("safe-area-inset-bottom") && css.Contains("100dvh"), "safe-area-and-dvh");
Check.That(app.Contains("visualViewport"), "visual-viewport-fallback");
Check.That(css.Contains("@media (max-width: 340px)"), "extra-narrow-tuning");

// 7. 无真实接线：不得出现 fetch/WebSocket 真实调用（夹具协议）。
foreach (var (src, name) in new[] { (app, "app.js"), (fx, "fixtures.js") })
{
    Check.That(!src.Contains("new WebSocket") && !src.Contains("fetch("),
        "no-real-network-" + name);
}

// 8. HTML 结构健全性：script 引用存在且文件齐全。
foreach (var m in System.Text.RegularExpressions.Regex.Matches(html, "src=\"([^\"]+)\"").Cast<System.Text.RegularExpressions.Match>())
{
    var p = Path.Combine(root, "docs", "frontend-prototype", "mobile", m.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
    Check.That(File.Exists(p), "script-exists-" + m.Groups[1].Value);
}

Console.WriteLine(Check.Failures == 0 ? "ALL PASS" : $"{Check.Failures} FAILED");
return Check.Failures == 0 ? 0 : 1;

// 测试程序集输出在 bin/... 下；原型以仓库根相对路径定位。
static string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "frontend-prototype", "mobile")))
        dir = dir.Parent;
    if (dir is null) throw new Exception("找不到 docs/frontend-prototype/mobile 原型目录");
    return dir.FullName;
}

static class Check
{
    static int _failed;

    public static void That(bool condition, string name)
    {
        if (condition) { Console.WriteLine("PASS " + name); return; }
        _failed++;
        Console.WriteLine("FAIL " + name);
    }

    public static int Failures => _failed;
}
