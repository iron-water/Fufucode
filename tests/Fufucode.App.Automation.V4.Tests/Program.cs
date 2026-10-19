using System.Text.Json;
using Fufucode.App.Features.Automation;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Automation.Hosting;
using Fufucode.Plugins.AutoDev.Engine.Harnesses;

BatchHarnessAdapters.Register();

// CONV-2 定向验证：v4 工作台页面视图模型的状态投影、双视角切换与自动委托抽屉。
// 运行：dotnet run --project tests/Fufucode.App.Automation.V4.Tests
// 只使用真实临时 SQLite 与真实文件，不调用任何模型。

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var work = Path.Combine(repoRoot, "tmp", "conv2-v4-tests", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
Directory.CreateDirectory(work);
var passed = 0;
// 夹具子进程模式：写一份真实产物并提交，作为可观察的停写交付，不调用任何模型。
if (args.Length >= 2 && args[0] == "--conv2-protocol")
{
    var prompt = await Console.In.ReadToEndAsync();
    File.WriteAllText("fixture.txt", "CONV2\n");
    await Git(Environment.CurrentDirectory, "add", "fixture.txt");
    await Git(Environment.CurrentDirectory, "commit", "-q", "-m", "conv2 fixture delivery");
    File.AppendAllText(args[1], JsonSerializer.Serialize(new { prompt }) + "\n");
    Console.WriteLine("{\"type\":\"thread.started\",\"thread_id\":\"conv2-fixture-session\"}");
    Console.WriteLine("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"{\\\"taskId\\\":\\\"CONV-2\\\",\\\"status\\\":\\\"module_ready\\\"}\"}}");
    Console.WriteLine("{\"type\":\"turn.completed\"}");
    return 0;
}

void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}

static string Sha(char c) => new(c, 40);
string Command() => Guid.NewGuid().ToString("N");

static async Task<string> Git(string directory, params string[] arguments)
{
    var start = new System.Diagnostics.ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = System.Diagnostics.Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
    var text = await output; var detail = await error;
    if (process.ExitCode != 0) throw new Exception("git " + string.Join(' ', arguments) + " 失败：" + detail);
    return text;
}

try
{
    var db = Path.Combine(work, "v4.db");
    var worktree = Path.Combine(work, "worktree-A");
    Directory.CreateDirectory(worktree);
    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    await Git(worktree, "init", "-q");
    await Git(worktree, "config", "user.name", "Fixture");
    await Git(worktree, "config", "user.email", "fixture@example.invalid");
    File.WriteAllText(Path.Combine(worktree, "a.txt"), "fixture\n");
    await Git(worktree, "add", "a.txt");
    await Git(worktree, "commit", "-q", "-m", "fixture");
    var head = (await Git(worktree, "rev-parse", "HEAD")).Trim();
    var marker = Path.Combine(work, "writer-started.txt");
    var executable = Environment.ProcessPath!;
    var prefix = new List<string>();
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        prefix.Add(typeof(Program).Assembly.Location);
    prefix.AddRange(["--conv2-protocol", marker]);
    var runtime = new HarnessRuntime("conv2-fixture", "codex", executable, prefix.ToArray(), "fixture-v1");
    using (var store = new AutomationStore(db))
    await using (var host = new BackgroundHost(new(db, Path.Combine(work, "runs"), "conv2-host", Path.Combine(work, "endpoint"))))
    {
        var consumer = new ControlIntentConsumer(store);
        var queue = new HostJournal(store);
        store.ApplyControlCommand(new(Command(), store.ReadControlSnapshot().Revision, "human", ControlAction.DefinePackage,
            new WorkPackageSpec("package-A", "自动化纵向包", "完成 v4 纵向包", ["src/owned/**"], ["行为已验证"], "codex", "devin/swe-2", "code", []),
            ExpectedPackageVersion: 0), now - 100_000);
        store.ApplyControlCommand(new(Command(), store.ReadControlSnapshot().Revision, "human", ControlAction.ApprovePackage,
            PackageId: "package-A", ExpectedPackageVersion: 1), now - 99_000);
        store.ApplyControlCommand(new(Command(), store.ReadControlSnapshot().Revision, "human", ControlAction.SetMode,
            Mode: DevelopmentMode.SemiAutomatic), now - 98_000);
        store.CreateThread("thread-A", "project", "codex", "devin/swe-2", worktree, null, now - 97_000);
        store.CreateTask("task-A", "thread-A", "自动化纵向包", head, now - 97_000);
        Check(await host.StartAsync(), "前置：真实后台宿主占用唯一槽位");
        var queued = await consumer.QueueAsync(new(Command(), store.ReadControlSnapshot().Revision, "execute:package-A:1", 1,
            "thread-A", "task-A", runtime), now - 96_000);
        var until = DateTime.UtcNow.AddSeconds(60);
        while (queue.Get(queued.RunId)!.State != HostRequestState.Settled)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("v4 夹具执行未在期限内结束；保留现场，不重发");
            await Task.Delay(50);
        }
        Check(host.Executor.Runs.Get(queued.RunId).Status == HarnessRunStatus.Completed, "前置：真实子进程执行完成并停写");
        store.CreatePart("part-A", "task-A", "v4 页面与投影", now - 80_000);
        store.RecordDelivery("revision-A", "part-A", queued.RunId, head, "{\"card\":\"真实卡\"}", Command(), now - 70_000);
        var reviewItem = store.ReadControlInbox().Items.Single(i => i.Kind == "review" && i.ObjectId == "part-A");
        Check(reviewItem.Actionable, "前置：真实注册交付在停写后进入可审队列");
        store.ApplyControlReview(new ControlReviewCommand(Command(), store.ReadControlSnapshot().Revision, "human", "task-A",
            "part-A", 1, queued.RunId, head, reviewItem.EvidenceToken,
            AutomationDecision.Accept, null, "{\"checks\":[\"dotnet test\"]}"), now - 60_000);
        Check(!store.IsIntegrated("part-A", 1), "前置：真实接受不等于已整合");
        await host.StopAsync();
    }
    // 真实授权 → 真实集成标记：复用 Core 的显式授权路径，不用绕过门禁的直接写库。
    using (var store = new AutomationStore(db))
    {
        var candidate = store.ReadControlIntegrationCandidate("package-A")!;
        Check(candidate.Eligible && !candidate.Authorized && !candidate.Integrated,
            "前置：已接受交付具备集成资格但尚未授权");
        store.ApplyControlIntegration(new(Command(), store.ReadControlSnapshot().Revision, "human",
            ControlIntegrationAction.Authorize, "package-A", candidate.Package.Version, candidate.EvidenceToken), now - 55_000);
        store.MarkIntegrated("part-A", 1, Sha('c'), "integration", now - 50_000);
        Check(store.IsIntegrated("part-A", 1), "前置：显式授权后可记录真实集成标记");
    }

    // 1) 未连接：不伪造状态，明确未知。
    using (var disconnected = new AutomationControlViewModel(Path.Combine(work, "appdata")))
    {
        Check(disconnected.ThreadRows.Count == 0 && disconnected.DeliveryRows.Count == 0, "未连接时投影为空，不伪造线程或成果");
        Check(disconnected.ProjectStateText == "未连接" && disconnected.DecisionCountText == "0", "未连接时项目状态与计数明确为未连接/0");
        Check(disconnected.HostConnectionText.Contains("未连接", StringComparison.Ordinal), "未连接时后台连接状态如实显示未连接");
        Check(disconnected.IsOverviewTab && !disconnected.IsThreadsTab, "总览是默认二级页签");
        Check(disconnected.ThreadView == AutomationThreadViewMode.List, "执行线程默认视角是列表");
        Check(disconnected.Delegation.Concurrency == AutomationV4Projection.DefaultConcurrency, "委托默认并发是 2");
        Check(!disconnected.DrawerOpen, "委托抽屉默认收起");
    }

    // 2) 连接真实库：投影读取真实线程、成果与审核结论。
    var model = new AutomationControlViewModel(Path.Combine(work, "appdata"));
    model.SetProjectContext("project");
    await model.ConnectAsync(db);
    Check(model.Connected, "连接真实 SQLite 成功");
    Check(model.ThreadRows.Count == 1 && model.ThreadRows[0].ThreadId == "thread-A", "状态投影读到真实线程");
    Check(model.ThreadRows[0].HeadSha == head[..12] && model.ThreadRows[0].HeadSha != "未知",
        "线程行显示真实当前 SHA：" + model.ThreadRows[0].HeadSha);
    Check(model.DeliveryRows.Count == 1 && model.DeliveryRows[0].PartId == "part-A", "成果投影读到真实成果部分");
    Check(model.DeliveryRows[0].ReviewConclusion.StartsWith("已接受", StringComparison.Ordinal), "成果显示真实审核结论：" + model.DeliveryRows[0].ReviewConclusion);
    Check(model.DeliveryRows[0].Integrated && model.DeliveryRows[0].Flow.Contains("已整合", StringComparison.Ordinal), "接受/整合分开显示且读到真实整合标记");
    Check(model.RecentDeliveryText.Contains("已整合 1", StringComparison.Ordinal), "最近成果摘要来自真实记录：" + model.RecentDeliveryText);
    Check(model.BackendReady, "有已注册执行身份时后端就绪按真实观测判定");

    // 3) 双视角切换：不变量与状态都真实变化。
    Check(model.IsOverviewTab, "连接后仍在默认总览页签");
    model.SelectTab("threads");
    Check(model.IsThreadsTab && model.IsThreadListVisible && !model.IsThreadGraphVisible, "切到执行线程后默认是列表视角");
    model.SetThreadView("graph");
    Check(model.ThreadView == AutomationThreadViewMode.DependencyGraph && model.IsThreadGraphVisible && !model.IsThreadListVisible,
        "切到依赖图后列表隐藏、图可见");
    Check(model.ThreadViewText == "依赖图视角", "视角文本跟随真实视角");
    Check(model.ThreadRows.Count == 1, "视角切换不改变投影数据");
    model.ToggleThreadView();
    Check(model.ThreadView == AutomationThreadViewMode.List, "再次切换回到列表视角");
    model.SelectTab("deliveries");
    Check(model.IsDeliveriesTab && !model.IsThreadsTab, "成果与验收页签可切换");
    model.SelectTab("activity");
    Check(model.IsActivityTab && !model.IsDeliveriesTab, "活动记录页签可切换");
    model.SelectTab("overview");
    Check(model.IsOverviewTab, "返回总览页签");

    // 4) 自动委托抽屉：默认值、范围与显式预算文本、真实落盘与读回。
    model.OpenDelegationDrawer();
    Check(model.DrawerOpen, "打开委托抽屉");
    Check(model.Delegation.Concurrency == 2, "抽屉默认并发 2");
    Check(model.Delegation.ImplementationModel == "devin/swe-2" && model.Delegation.VerificationModel == "devin/swe-2",
        "抽屉默认实现/验证模型为最低成本模型");
    Check(model.Delegation.ScopeText.Contains("允许范围", StringComparison.Ordinal), "抽屉显示允许范围");
    Check(model.Delegation.BudgetText.Contains("未配置显式上限", StringComparison.Ordinal), "未配置预算时明确标注未知而不是编造余额");
    Check(model.Delegation.PermissionText.Contains("越权", StringComparison.Ordinal), "抽屉区分普通工具审批与开发委托权限");
    model.Delegation.Concurrency = 3;
    model.Delegation.AutoIntegrate = false;
    model.SaveDelegationSettings();
    var settingsFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(db))!, AutomationWorkspaceMarker.FileName.Replace("workspaces", "delegation"));
    Check(File.Exists(settingsFile), "委托设置真实落盘：" + Path.GetFileName(settingsFile));
    using (var json = JsonDocument.Parse(File.ReadAllText(settingsFile)))
        Check(json.RootElement.GetProperty("Concurrency").GetInt32() == 3, "落盘内容与设置一致");
    model.Delegation.Concurrency = 2; model.Delegation.AutoIntegrate = true;
    model.LoadDelegationSettings(db);
    Check(model.Delegation.Concurrency == 3 && !model.Delegation.AutoIntegrate, "委托设置能按项目读回");
    model.CloseDelegationDrawer();
    Check(!model.DrawerOpen, "委托抽屉可收起");

    // 5) 主控对话输入：目标真正直达核心，生成目标版本并登记自动推进委托。
    var revisionBefore = model.Snapshot!.Inbox.Revision;
    model.GoalDraft = "补充：把资料引用接入主聊天";
    await model.SubmitGoalRequestAsync();
    Check(model.GoalDraft.Length == 0, "提交后清空输入框");
    Check(model.GoalVersion == 1 && model.GoalVersionText.Contains("v1", StringComparison.Ordinal),
        "提交目标生成真实目标版本：" + model.GoalVersionText);
    using (var check = new AutomationStore(db))
    {
        var goal = check.CurrentProjectGoal("project");
        Check(goal is not null && goal.Version == 1 && goal.Statement.Contains("资料引用", StringComparison.Ordinal),
            "核心持久化了目标原文与版本");
    }
    Check(model.Snapshot!.Inbox.Revision >= revisionBefore, "提交目标不倒退核心版本");
    model.GoalDraft = "未发送的草稿";
    model.ReturnToOverview();
    Check(model.GoalDraft == "未发送的草稿", "同项目内切换页签保留未发送输入");

    // 6) 新项目默认自动 / 旧项目保留：只读标记，不改变核心模式。
    var appData = Path.Combine(work, "policy-appdata");
    Directory.CreateDirectory(appData);
    var policyMarker = new AutomationWorkspaceMarker(appData);
    Check(policyMarker.IsFirstVisit("fresh-project"), "未记录的工作台视为首次进入");
    Check(policyMarker.TryMark("fresh-project", out var markError) && markError is null, "首次进入标记可落盘");
    Check(!new AutomationWorkspaceMarker(appData).IsFirstVisit("fresh-project"), "标记在重开后仍然生效");
    Check(new AutomationWorkspaceMarker(appData).IsFirstVisit("其他项目"), "标记按项目隔离，不影响其他项目");
    var fresh = AutomationEntryPolicy.ForReady(hasGoal: false, hasBackend: true, firstVisit: true);
    Check(fresh.IsFirstVisit && fresh.ModeText.Contains("自动开发", StringComparison.Ordinal), "新项目默认自动开发");
    Check(fresh.Reason.Contains("不启动模型", StringComparison.Ordinal) && fresh.NextAction.Contains("提交目标", StringComparison.Ordinal),
        "新项目未给目标时显示等待目标且无空转");
    var noBackend = AutomationEntryPolicy.ForReady(hasGoal: true, hasBackend: false, firstVisit: false);
    Check(!noBackend.IsReady && noBackend.ModeText.Contains("需配置执行后端", StringComparison.Ordinal), "后端缺失时显示需要配置而不是自动");
    var legacy = AutomationEntryPolicy.ForReady(hasGoal: true, hasBackend: true, firstVisit: false);
    Check(legacy.ModeText == "按原模式继续" && legacy.Reason.Contains("不被默认变更覆盖", StringComparison.Ordinal),
        "已有人工/暂停项目沿用原模式，不被静默覆盖");
    var coreBefore = model.Snapshot!.Inbox.Mode;
    model.ApplyEntryPolicy(appData);
    Check(model.Snapshot!.Inbox.Mode == coreBefore, "入口策略不修改核心模式");
    Check(model.EntryPolicyText.Length > 0 && model.EntryPolicyMode.Length > 0, "入口策略对用户可读：" + model.EntryPolicyMode);
    Check(model.Snapshot!.Inbox.Mode == DevelopmentMode.Automatic,
        "CONV-3：提交目标后核心处于自动开发模式：" + model.Snapshot!.Inbox.Mode);
    try
    {
        await model.ResumeAutomaticAsync();
    }
    catch (Exception error) { Check(false, "自动模式命令不应在 UI 线程抛出：" + error.Message); }
    Check(model.Snapshot!.Inbox.Mode == DevelopmentMode.Automatic,
        "CONV-3：核心已启用自动策略，自动模式可持久化且保持");

    // 7) 失败：连接不存在的库不改变已有快照语义。
    var failing = new AutomationControlViewModel(Path.Combine(work, "appdata2"));
    failing.SetProjectContext("project");
    await failing.ConnectAsync(Path.Combine(work, "missing.db"));
    Check(!failing.Connected && failing.StatusText.StartsWith("连接失败", StringComparison.Ordinal), "连接失败被如实报告");
    Check(failing.ThreadRows.Count == 0 && failing.ProjectStateText == "未连接", "连接失败后投影保持未连接，不保留伪造数据");
    failing.Dispose();
    model.Dispose();

    Console.WriteLine();
    Console.WriteLine("CONV-2 定向验证通过：" + passed + " 项断言");
    Console.WriteLine("工作目录（供复核）：" + work);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL " + error.Message);
    Console.Error.WriteLine(error);
    Console.Error.WriteLine("工作目录（失败现场）：" + work);
    return 1;
}
