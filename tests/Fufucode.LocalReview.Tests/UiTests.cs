using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Fufucode.App.Features.LocalReview;
using Fufucode.Core;
using Fufucode.Core.Features.LocalReview;

namespace Fufucode.LocalReview.Tests;

/// <summary>
/// E1 本地审查页面的无头定向测试（在允许目录内，由 Program.cs 的 --ui 分支调用）：
/// 真实无头 Window 加载 LocalReviewPageView（真实编译绑定），验证工作树/范围/结果/过期/过滤/
/// 跳转/导出/历史绑定，以及工作区切换、临时移出视觉树、最终释放与取消语义。
/// 线程纪律：Avalonia 初始化后主线程即 UI 线程，因此不在主线程 await 依赖 dispatcher 续体的任务
/// （会死锁）；所有异步动作都通过命令 + Pump 驱动，需要同步完成的离线工作用 Task.Run 脱离 UI 线程。
/// 执行入口用可控假实现，只验证接线与生命周期，不模拟审查质量。
/// </summary>
internal static class UiTests
{
    public static int Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-localreview-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            return RunCore(root, check);
        }
        finally
        {
            Console.WriteLine("UI evidence root: " + root);
            if (Environment.GetEnvironmentVariable("E1_KEEP_TMP") is null)
            {
                try { Directory.Delete(root, recursive: true); }
                catch (Exception error) { Console.WriteLine("临时目录保留（清理失败不影响结果）：" + error.GetType().Name); }
            }
        }
    }

    private static int RunCore(string root, Action<bool, string> check)
    {
        // ---------- 真实临时 Git 仓库（在 Avalonia 初始化前建立，避免 UI 线程等待） ----------
        var repo = PrepareRepository(root, "repo 目录");
        var head = Git(repo, "rev-parse", "HEAD").Trim();
        var other = PrepareRepository(root, "other repo");
        var dataRoot = Path.Combine(root, "module-data");

        var builder = AppBuilder.Configure<SmokeApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
        builder.SetupWithoutStarting();

        var host = new RecordingHost();
        var executor = new ControllableExecutor();
        using var viewModel = new LocalReviewViewModel(dataRoot, executor, host);
        var view = new LocalReviewPageView();
        view.Attach(viewModel);
        var window = new Window { Width = 1100, Height = 800, Content = view };
        window.Show();

        var passed = 0;
        void Check(bool condition, string name) { check(condition, name); passed++; }

        Check(Pump(() => view.IsAttached && ReferenceEquals(view.ViewModel, viewModel)),
            "UI 无头视图加载页面（真实编译绑定）");
        Check(Pump(() => viewModel.StatusText.Contains("尚未选择项目")), "UI 初始状态行绑定生效");

        // ---------- 工作树列表 + 显式创建（真实 Git） ----------
        viewModel.SetProject(repo);
        Check(Pump(() => viewModel.StatusText.Contains("项目已选择")), "UI 宿主注入项目路径后状态更新");
        viewModel.RefreshWorktreesCommand.Execute(null);
        Check(Pump(() => viewModel.Worktrees.Count >= 1), "UI 工作树列表渲染（只读真实 Git）");
        Check(viewModel.Worktrees.Any(w => w.Path.Contains("repo 目录", StringComparison.Ordinal)),
            "UI 空格/中文工作树路径可显示");
        Check(Pump(() => !viewModel.IsBusy), "UI 列表读取后回收忙态");

        var createdTarget = Path.Combine(root, "created wt 目录");
        viewModel.CreateDestination = createdTarget;
        viewModel.CreateWorktreeCommand.Execute(null);
        Check(Pump(() => Directory.Exists(createdTarget) && viewModel.Worktrees.Count >= 2),
            "UI 显式创建工作树可用且列表刷新");
        Check(viewModel.StatusText.Contains("未被自动信任") && viewModel.StatusText.Contains("会话未切换"),
            "UI 创建后提示不自动信任、不切换会话");
        Check(File.ReadAllText(Path.Combine(repo, "src", "main.cs")).Contains("CHANGED"),
            "UI 创建后原工作区脏改动仍在（未被还原/搬动）");
        var createdFile = Path.Combine(createdTarget, "src", "main.cs");
        Check(File.Exists(createdFile) && File.ReadAllText(createdFile).Contains("obj.Value") &&
            !File.ReadAllText(createdFile).Contains("CHANGED"),
            "UI 新工作树内容是已提交 HEAD 版本：脏改动没有被复制过去");

        viewModel.SelectedWorktree = viewModel.Worktrees.First(w =>
            string.Equals(Path.GetFullPath(w.Path), Path.GetFullPath(repo), StringComparison.OrdinalIgnoreCase));
        viewModel.OpenSelectedWorktreeCommand.Execute(null);
        Check(Pump(() => host.Opened.Count == 1), "UI 打开经宿主请求接口（本模块不自行开目录）");

        // ---------- 范围解析 ----------
        viewModel.SelectedScope = viewModel.ScopeChoices[0];
        viewModel.ResolveScopeCommand.Execute(null);
        Check(Pump(() => viewModel.ScopeSummary.Contains("文件") && viewModel.ScopeSummary.Contains("1")),
            "UI 范围解析摘要绑定（工作区差异，1 个文件）");
        Check(Pump(() => viewModel.StatusText.Contains("可用既有快照精确冻结")), "UI 工作区范围声明可冻结");

        // 坏基准：显式报错，不静默审整个工作区。
        viewModel.SelectedScope = viewModel.ScopeChoices.First(c => c.Kind == LocalReviewScopeKind.Commit);
        viewModel.Revision = "no-such-ref";
        viewModel.ResolveScopeCommand.Execute(null);
        Check(Pump(() => viewModel.StatusText.Contains("基准/提交无效")), "UI 坏提交基准：显式拒绝并说明");
        var callsBeforeBadReview = executor.CallCount;
        executor.ThrowOnCall = true;
        viewModel.ReviewCommand.Execute(null);
        // 无效范围会触发内部重解析（真实 git 子进程，耗时不定）。等到连续静置 400ms 再判定，
        // 否则可能在忙态尚未建立时空过（假通过）。
        var settleDeadline = DateTime.UtcNow.AddSeconds(20);
        var quietSince = DateTime.UtcNow;
        while (DateTime.UtcNow < settleDeadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (viewModel.IsBusy) quietSince = DateTime.UtcNow;
            else if (DateTime.UtcNow - quietSince >= TimeSpan.FromMilliseconds(400)) break;
            Thread.Sleep(10);
        }
        Check(!viewModel.IsBusy, "UI 无效范围处理结束后回到空闲");
        Check(executor.CallCount == callsBeforeBadReview,
            "UI 范围无效时不投递审查（不静默退回工作区全量）");
        executor.ThrowOnCall = false;

        // ---------- 暂存范围：共享 API 已接线，解析保持真实类型且不自动投递 ----------
        Offline(() => Git(repo, "add", "src/main.cs"));
        viewModel.SelectedScope = viewModel.ScopeChoices.First(c => c.Kind == LocalReviewScopeKind.Staged);
        viewModel.ResolveScopeCommand.Execute(null);
        Check(Pump(() => !viewModel.IsBusy && viewModel.ScopeSummary.Contains("暂存差异")),
            "UI 暂存范围解析保持真实类型");
        Check(executor.CallCount == 0, "UI 只解析暂存范围不自动投递审查");
        Check(Pump(() => viewModel.ScopeSummary.Contains("暂存差异")), "UI 暂存范围摘要如实标注类型");
        executor.ThrowOnCall = false;
        viewModel.SelectedScope = viewModel.ScopeChoices[0];

        // ---------- 审查：忙态、不阻塞 UI、结果绑定 ----------
        viewModel.ReviewCommand.Execute(null);
        Check(Pump(() => viewModel.IsBusy && viewModel.CanCancel), "UI 审查进行中：忙态与可取消绑定生效");
        var pumpRounds = 0;
        for (var i = 0; i < 30; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); pumpRounds++; }
        Check(pumpRounds == 30 && viewModel.IsBusy, "UI 审查期间可持续泵动（耗时 IO 未阻塞 UI）");
        executor.Release();
        Check(Pump(() => !viewModel.IsBusy), "UI 审查结束回到空闲");
        Check(Pump(() => viewModel.HasResult && viewModel.Findings.Count == 1),
            "UI 结果渲染有效发现项（真实快照定位）");
        Check(viewModel.Rejected.Count == 1 && viewModel.Rejected[0].Reason == "path-outside-snapshot",
            "UI 拒绝项单独展示，不静默丢弃");
        Check(executor.CallCount == 1 && executor.LastScope?.Kind == LocalReviewScopeKind.Workspace,
            "UI 执行入口只收到已解析范围一次");
        Check(executor.LastSnapshotHead == head, "UI 结果绑定固定快照 HEAD");

        var findingsList = view.GetLogicalDescendants().OfType<ListBox>()
            .First(list => AutomationProperties.GetName(list) == "发现项列表");
        Check(Pump(() => findingsList.ItemCount == 1), "UI 发现项列表控件反映视图模型");
        Check(view.GetLogicalDescendants().OfType<TextBlock>()
            .Any(block => block.Text is { Length: > 0 } text &&
                text.Contains("src/main.cs", StringComparison.Ordinal)), "UI 详情区渲染真实定位 path:line");

        // ---------- 过滤 ----------
        viewModel.PriorityFilter = "High";
        Check(Pump(() => viewModel.Findings.Count == 1), "UI 优先级过滤保留匹配项");
        viewModel.PriorityFilter = "Low";
        Check(Pump(() => viewModel.Findings.Count == 0), "UI 优先级过滤排除不匹配项");
        viewModel.PriorityFilter = "全部";
        Check(Pump(() => viewModel.Findings.Count == 1), "UI 清除过滤后恢复");

        // ---------- 跳转 / 复制 / 导出 ----------
        viewModel.SelectedFinding = viewModel.Findings[0];
        viewModel.JumpCommand.Execute(null);
        Check(Pump(() => host.Jumps.Count == 1 && host.Jumps[0].Line == 2 &&
            host.Jumps[0].Path.Replace('\\', '/').EndsWith("src/main.cs", StringComparison.Ordinal)),
            "UI 跳转请求带绝对路径与快照行号");
        viewModel.CopyFindingsCommand.Execute(null);
        Check(Pump(() => host.Copied.Count == 1 && host.Copied[0].Contains("本地代码审查结果")),
            "UI 复制经宿主剪贴板接口");
        var exportTarget = Path.Combine(root, "导出结果 目录", "review.md");
        host.PickResult = exportTarget;
        viewModel.ExportCommand.Execute("md");
        Check(Pump(() => File.Exists(exportTarget) && viewModel.ExportedPath.Length > 0),
            "UI 导出写入宿主选定路径（仅临时目录）");
        var exported = File.ReadAllText(exportTarget);
        Check(exported.Contains("本地代码审查结果") && exported.Contains("快照"),
            "UI 导出内容含范围与快照声明");
        Check(!File.Exists(Path.Combine(dataRoot, "desktop.db")), "UI 模块数据根没有 desktop.db：未写用户数据库");

        // ---------- 历史 ----------
        viewModel.ReloadHistoryCommand.Execute(null);
        Check(Pump(() => viewModel.History.Count >= 1), "UI 历史列表从模块目录读回");
        var historyName = viewModel.History[0];
        viewModel.LoadHistoryCommand.Execute(historyName);
        Check(Pump(() => viewModel.ScopeSummary.Contains("历史文件") && viewModel.Findings.Count == 1),
            "UI 加载历史读回同一结果（只读，不重新审查）");
        Check(executor.CallCount == 1, "UI 加载历史不会再次投递审查");
        viewModel.SelectedFinding = viewModel.Findings[0];
        var beforeHistoryJump = host.Jumps.Count;
        viewModel.JumpCommand.Execute(null);
        Check(Pump(() => viewModel.StatusText.Contains("历史") && viewModel.StatusText.Contains("定位")) && host.Jumps.Count == beforeHistoryJump,
            "UI 加载历史不会借用上一次活结果的快照执行定位");
        viewModel.CopyFindingsCommand.Execute(null);
        Check(Pump(() => host.Copied.Count == 2 && host.Copied[1].Contains("possible null deref")),
            "UI 历史结果仍能复制其保存的证据");
        viewModel.PriorityFilter = "High";

        // ---------- 过期：仓库变化后的结果显示过期警告 ----------
        Offline(() => File.AppendAllText(Path.Combine(repo, "src", "main.cs"), "// raced\n", new UTF8Encoding(false)));
        executor.NextStale = true;
        viewModel.ReviewCommand.Execute(null);
        Check(Pump(() => viewModel.IsBusy), "UI 过期场景审查启动");
        executor.Release();
        Check(Pump(() => !viewModel.IsBusy && viewModel.StaleWarning.Contains("快照已过期")),
            "UI 变更后结果显示过期警告（不当作当前精确定位）");
        Check(Pump(() => viewModel.Findings.Count == 1 && viewModel.Findings[0].Stale),
            "UI 过期状态下每条发现项自标 Stale");
        viewModel.SelectedFinding = viewModel.Findings[0];
        host.Jumps.Clear();
        viewModel.JumpCommand.Execute(null);
        Check(Pump(() => viewModel.StatusText.Contains("已阻止")) && host.Jumps.Count == 0,
            "UI 过期发现不得把旧行号传给当前文件定位");
        executor.NextStale = false;

        // ---------- 取消：只取消本模块运行，结果未知需对账 ----------
        executor.Block();
        var callsBeforeCancel = executor.CallCount;
        viewModel.ReviewCommand.Execute(null);
        Check(Pump(() => viewModel.IsBusy && executor.Entered == callsBeforeCancel + 1),
            "UI 取消场景审查已真正在途（执行入口已进入）");
        viewModel.CancelCommand.Execute(null);
        Check(Pump(() => !viewModel.IsBusy), "UI 取消后回到空闲");
        Check(Pump(() => executor.CancelObserved), "UI 取消令牌传到执行入口（本模块自己的运行）");
        Check(executor.CallCount == callsBeforeCancel + 1, "UI 取消不会自动重发");
        Check(viewModel.StatusText.Contains("未知") && viewModel.StatusText.Contains("对账"),
            "UI 取消结果如实说明：状态未知且需对账");
        Check(!viewModel.CanCancel, "UI 取消后回收可取消标志");

        // ---------- 工作区切换不串上下文 ----------
        executor.Block();
        var enteredBeforeSwitch = executor.Entered;
        viewModel.ReviewCommand.Execute(null);
        Check(Pump(() => viewModel.IsBusy && executor.Entered == enteredBeforeSwitch + 1), "UI 切换前审查已真正在途");
        var callsBeforeSwitch = executor.CallCount;
        viewModel.SetProject(other);
        Check(Pump(() => !viewModel.HasResult && viewModel.Findings.Count == 0 &&
            viewModel.Rejected.Count == 0 && viewModel.ScopeSummary.Length == 0 && !viewModel.IsBusy),
            "UI 切换项目后清空上一项目结果并回到空闲（不串上下文）");
        Check(Pump(() => executor.CancelObserved), "UI 切换项目取消了上一项目的在途运行");
        Check(executor.CallCount == callsBeforeSwitch, "UI 切换后没有额外投递");
        Check(Pump(() => viewModel.ScopeChoices.Count == 4), "UI 切换后集合仍完整（未被重复订阅破坏）");

        // ---------- 临时移出视觉树不取消运行、不销毁视图模型 ----------
        viewModel.SetProject(repo);
        executor.Block();
        // Block() 清空观察标记：本场景要求"移出重挂不触发取消"，必须从干净状态判定。
        var callBeforeRemoval = executor.CallCount;
        viewModel.ReviewCommand.Execute(null);
        Check(Pump(() => viewModel.IsBusy && executor.CallCount == callBeforeRemoval + 1), "UI 移出前审查已真正在途");
        window.Content = null;
        PumpTicks(6);
        Check(!viewModel.IsDisposed, "UI 临时移出视觉树不销毁视图模型");
        Check(viewModel.IsBusy, "UI 临时移出视觉树不误取消运行中的审查");
        Check(view.IsAttached && ReferenceEquals(view.ViewModel, viewModel), "UI 移出后视图仍持有同一视图模型");
        window.Content = view;
        PumpTicks(6);
        Check(!executor.CancelObserved, "UI 移出视觉树期间没有触发取消");
        executor.Release();
        Check(Pump(() => !viewModel.IsBusy && viewModel.HasResult),
            "UI 移出重挂后运行正常完成并渲染结果（未被误取消）");
        Check(executor.CallCount == callBeforeRemoval + 1, "UI 移出重挂期间没有额外投递");
        Check(!executor.CancelObserved, "UI 移出重挂全程没有触发取消");

        // ---------- 窄容器（默认右侧停靠栏宽度）下操作按钮必须可达 ----------
        window.Width = 320;
        Check(Pump(() => view.Bounds.Width > 0 && view.Bounds.Width <= 340), "UI 窗口收窄到停靠栏宽度");
        Check(Pump(() => ActionButtonsReachable(view)), "UI 窄宽度下审查/解析/导出等按钮仍可达（自动换行）");
        window.Width = 1100;
        Check(Pump(() => view.Bounds.Width > 700), "UI 恢复宽布局");
        Check(Pump(() => ActionButtonsReachable(view)), "UI 恢复宽度后按钮仍可达");

        // ---------- 重复 Attach 幂等 + 最终 Detach 停止本模块运行 ----------
        view.Attach(viewModel);
        view.Attach(viewModel);
        Check(view.IsAttached, "UI 重复 Attach 保持挂载且无重复初始化");
        executor.Block();
        var callsBeforeDetach = executor.CallCount;
        var historyFilesOnDisk = Directory.GetFiles(Path.Combine(dataRoot, "history"), "*.json").Length;
        viewModel.ReviewCommand.Execute(null);
        Check(Pump(() => viewModel.IsBusy && executor.Entered == callsBeforeDetach + 1), "UI Detach 前审查已真正在途");
        var callsWithRunInFlight = executor.CallCount;
        view.Detach();
        Check(Pump(() => !viewModel.IsBusy), "UI Detach 停止本模块运行并回收忙态");
        Check(!view.IsAttached && view.ViewModel is null, "UI 最终 Detach 清除挂载状态");
        Check(viewModel.IsDisposed, "UI 最终 Detach 释放视图模型");
        Check(Pump(() => executor.CancelObserved), "UI Detach 把取消传到执行入口");
        Check(executor.CallCount == callsWithRunInFlight, "UI Detach 不会补投新运行");

        // 已释放的视图模型：所有命令只是空转，不投递、不改状态、不新增文件。
        var statusAfterDispose = viewModel.StatusText;
        var historyCountAfterDispose = viewModel.History.Count;
        var exportedAfterDispose = viewModel.ExportedPath;
        viewModel.ReviewCommand.Execute(null);
        viewModel.ResolveScopeCommand.Execute(null);
        viewModel.RefreshWorktreesCommand.Execute(null);
        viewModel.CreateWorktreeCommand.Execute(null);
        viewModel.CancelCommand.Execute(null);
        viewModel.JumpCommand.Execute(null);
        viewModel.CopyFindingsCommand.Execute(null);
        viewModel.ExportCommand.Execute("md");
        viewModel.LoadHistoryCommand.Execute(historyName);
        viewModel.ReloadHistoryCommand.Execute(null);
        PumpTicks(6);
        Check(executor.CallCount == callsWithRunInFlight, "UI 释放后不向执行入口投递任何新请求");
        Check(!viewModel.IsBusy && !viewModel.CanCancel, "UI 释放后不再进入忙态/可取消态");
        Check(viewModel.StatusText == statusAfterDispose &&
            viewModel.History.Count == historyCountAfterDispose &&
            viewModel.ExportedPath == exportedAfterDispose,
            "UI 释放后所有命令空转：不改状态、不写历史、不导出");
        Check(Directory.GetFiles(Path.Combine(dataRoot, "history"), "*.json").Length == historyFilesOnDisk,
            "UI 释放后历史目录不再新增文件");

        window.Close();
        return passed;
    }

    private static string PrepareRepository(string root, string name)
    {
        var repo = Path.Combine(root, name);
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q", ".");
        Git(repo, "config", "user.email", "t@example.invalid");
        Git(repo, "config", "user.name", "e1-ui-test");
        Git(repo, "config", "core.autocrlf", "false");
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        File.WriteAllText(Path.Combine(repo, "src", "main.cs"),
            "class A {\n  var x = obj.Value;\n}\n", new UTF8Encoding(false));
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "seed");
        File.WriteAllText(Path.Combine(repo, "src", "main.cs"),
            "class A {\n  var x = CHANGED;\n}\n", new UTF8Encoding(false));
        return repo;
    }

    private static string Git(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception($"git {string.Join(' ', args)} failed: {error}");
        return output;
    }

    /// <summary>离线执行必须同步完成的工作：脱离 UI 线程运行，避免 UI 线程等待 dispatcher 续体死锁。</summary>
    private static void Offline(Action work) => Task.Run(work).GetAwaiter().GetResult();

    private static bool ActionButtonsReachable(LocalReviewPageView view)
    {
        string[] labels = ["刷新工作树", "创建工作树", "打开所选", "解析范围", "开始审查",
            "跳转所选", "复制 Markdown", "导出 Markdown", "导出 JSON", "刷新历史", "加载所选历史"];
        var buttons = view.GetLogicalDescendants().OfType<Button>()
            .Where(button => button.Content is string content && labels.Contains(content, StringComparer.Ordinal))
            .ToList();
        if (buttons.Count != labels.Length)
        {
            Console.WriteLine($"[diag] 按钮数量 {buttons.Count}/{labels.Length}");
            return false;
        }
        return buttons.All(button =>
        {
            if (!button.IsVisible || button.Bounds.Width <= 1 || button.Bounds.Height <= 1)
            {
                Console.WriteLine($"[diag] {button.Content} 不可见或零尺寸 bounds={button.Bounds.Size}");
                return false;
            }
            var origin = button.TranslatePoint(new Point(0, 0), view);
            if (origin is null) return false;
            // 必须完整落在可视区内：只判宽度会漏掉"渲染但溢出被裁"。
            var reachable = origin.Value.X >= -1 && origin.Value.Y >= -1
                && origin.Value.X + button.Bounds.Width <= view.Bounds.Width + 1
                && origin.Value.Y + button.Bounds.Height <= view.Bounds.Height + 1;
            if (!reachable)
                Console.WriteLine($"[diag] {button.Content} 溢出可视区 bounds={button.Bounds.Size} origin={origin} view={view.Bounds.Size}");
            return reachable;
        });
    }

    private static bool Pump(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    private static void PumpTicks(int rounds = 6)
    {
        for (var i = 0; i < rounds; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    }

    /// <summary>可控执行入口：只验证页面接线、生命周期与取消语义，不模拟审查质量。</summary>
    private sealed class ControllableExecutor : ILocalReviewExecutor
    {
        public string Model => "devin/swe-2";
        public int CallCount { get; private set; }
        /// <summary>True only when this executor actually saw the caller's token canceled.</summary>
        public bool CancelObserved { get; private set; }
        /// <summary>Incremented as soon as a call starts, so tests can wait for a run to be in flight.</summary>
        public int Entered { get; private set; }
        public bool NextStale { get; set; }
        public bool Blocked = true;
        public bool ThrowOnCall { get; set; }
        public LocalReviewScopeResolution? LastScope { get; private set; }
        public string? LastSnapshotHead { get; private set; }
        public LocalReviewRunResult? LastResult { get; private set; }

        public void Release() => Blocked = false;
        public void Block() { Blocked = true; CancelObserved = false; }

        public async Task<LocalReviewRunResult> ReviewAsync(LocalReviewScopeResolution scope,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (ThrowOnCall) throw new InvalidOperationException("this scope must never dispatch");
            LastScope = scope;
            Entered = CallCount;
            // 快照捕获在线程池上完成：不占用 UI 线程，也不依赖 dispatcher 续体。
            FileDiffSnapshot? snapshot;
            try
            {
                snapshot = await Task.Run(() => LocalReviewScopeResolver
                    .CaptureFixedSnapshotAsync(scope, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                CancelObserved = true;
                throw;
            }
            LastSnapshotHead = snapshot?.HeadCommit;
            if (NextStale && snapshot is not null)
                await Task.Run(() => File.AppendAllText(
                    Path.Combine(snapshot.RepositoryRoot, "src", "main.cs"), "// extra\n", new UTF8Encoding(false)));
            // 等待期间观察取消令牌：Cancel/切换项目/Detach 必须真正传进执行入口。
            while (Blocked && !cancellationToken.IsCancellationRequested)
                await Task.Delay(10, CancellationToken.None);
            if (cancellationToken.IsCancellationRequested)
            {
                CancelObserved = true;
                throw new OperationCanceledException(cancellationToken);
            }
            var findings = new[] { new LocalReviewFinding(LocalReviewPriority.High, "src/main.cs", 2,
                "possible null deref", "var x = CHANGED;") };
            var rejected = new[] { new LocalReviewParseIssue("path-outside-snapshot", "outside/x.cs", 1, null) };
            var status = NextStale ? LocalReviewRunStatus.StaleAfterReview : LocalReviewRunStatus.Completed;
            var result = new LocalReviewRunResult(scope, snapshot, status, findings, rejected, Model,
                "ui-run-" + CallCount, NextStale ? "repository changed after the snapshot was fixed" : null,
                false, DateTimeOffset.UtcNow);
            LastResult = result;
            return result;
        }
    }

    private sealed class RecordingHost : ILocalReviewHostRequests
    {
        public List<string> Opened { get; } = [];
        public List<(string Path, int Line)> Jumps { get; } = [];
        public List<string> Copied { get; } = [];
        public string? PickResult { get; set; }

        public void OpenWorktree(string worktreePath) => Opened.Add(worktreePath);
        public void JumpToLocation(string absoluteFilePath, int line) => Jumps.Add((absoluteFilePath, line));
        public Task<string?> PickExportPathAsync(string suggestedFileName) => Task.FromResult(PickResult);
        public Task CopyToClipboardAsync(string text) { Copied.Add(text); return Task.CompletedTask; }
    }

    private sealed class SmokeApp : Application
    {
        public override void Initialize()
        {
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
}
