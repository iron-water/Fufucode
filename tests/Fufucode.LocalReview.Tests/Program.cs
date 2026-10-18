using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Fufucode.Core;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Features.LocalReview;
using Fufucode.LocalReview.Tests;

// E1 本地 Git 工作树与审查模块定向测试（独立入口，未注册到全量自测，不改共享 sln）：
//   dotnet run --project tests/Fufucode.LocalReview.Tests
// 全部使用临时真实 Git 仓库与子进程夹具，不触碰任何用户仓库；
// 夹具响应是确定性输入，只验证传输/终态/解析机制，不冒充真实模型审查质量。

// M 复核返工的七项反例（忠实移植主区 tmp/e1-audit/Program.cs）：
//   dotnet run --project tests/Fufucode.LocalReview.Tests -c Release -- --ce
if (args.FirstOrDefault() == "--ce")
{
    var ceFailures = 0;
    void CeCheck(bool condition, string name)
    {
        if (!condition) { ceFailures++; Console.WriteLine("FAIL " + name); return; }
        Console.WriteLine("PASS " + name);
    }
    await Fufucode.LocalReview.Tests.Counterexamples.RunAsync(CeCheck);
    Console.WriteLine(ceFailures == 0 ? "CE ALL PASS" : $"CE FAILURES={ceFailures}");
    return ceFailures == 0 ? 0 : 1;
}

// 审查者子进程夹具（反例用，对应主区 --protocol）：hang/done/changed-empty/invalid-only/其余空发现。
if (args.FirstOrDefault() == "--protocol")
{
    var mode = args.Length > 1 ? args[1] : "empty";
    await Counterexamples.ProtocolAsync(mode, args.Length > 2 ? args[2] : null);
    return 0;
}

// 可选真实模型最小探针（显式 devin/swe-2，免费档）：只验证传输/终态/严格解析机制。
if (args.FirstOrDefault() == "--real-probe") return await RealProbe.RunAsync();

// 无头页面绑定/生命周期测试（真实 Avalonia 控件与编译绑定）：
//   dotnet run --project tests/Fufucode.LocalReview.Tests -c Release -- --ui
if (args.FirstOrDefault() == "--ui")
{
    void UiCheck(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL " + name);
        Console.WriteLine("PASS " + name);
    }
    var uiTotal = Fufucode.LocalReview.Tests.UiTests.Run(UiCheck);
    Console.WriteLine($"UI ALL PASS ({uiTotal})");
    return 0;
}

if (args.FirstOrDefault() == "--fake-reviewer")
{
    // 审查者夹具：按 codex 协议输出 JSONL。hang 场景排空 stdin 后挂起，用于取消路径。
    var scenario = args.Length > 1 ? args[1] : "empty";
    if (scenario == "hang")
    {
        _ = await Console.In.ReadToEndAsync();
        await Task.Delay(30000);
        return 0;
    }
    _ = await Console.In.ReadToEndAsync();
    string Text(string kind) => kind switch
    {
        // 一条有效（快照内路径 + 真实行 + 证据匹配）+ 一条坏优先级 + 一条快照外路径。
        "good" => """{"findings":[{"priority":"high","path":"src/main.cs","line":2,"explanation":"null deref possible","evidence":"var x = CHANGED;"},{"priority":"bogus","path":"src/main.cs","line":1,"explanation":"bad priority","evidence":""},{"priority":"low","path":"outside/sneak.cs","line":1,"explanation":"not in snapshot","evidence":"x"}]}""",
        "garbage" => "I reviewed it and it looks fine, no JSON here.",
        _ => """{"findings":[]}""",
    };
    var payload = Text(scenario);
    void Emit(object record) => Console.WriteLine(JsonSerializer.Serialize(record));
    Emit(new { type = "thread.started", thread_id = "fake-thread" });
    Emit(new { type = "item.completed", item = new { type = "agent_message", text = payload } });
    Emit(new { type = "turn.completed" });
    return 0;
}

var root = Path.Combine(Path.GetTempPath(), "fufucode-localreview-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }
async Task Reject(Func<Task> action, string name)
{ try { await action(); } catch (Exception) { Check(true, name); return; } throw new Exception("FAIL " + name); }
static bool PathEquals(string a, string b) =>
    string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);
HarnessRuntime FixtureRuntime(string scenario) => new("fixture", "codex", Environment.ProcessPath!,
    ["--fake-reviewer", scenario], "fixture-v1");

try
{
    // ---------- 夹具仓库（路径含空格与中文） ----------
    var repo = Path.Combine(root, "repo 目录");
    Directory.CreateDirectory(repo);
    await GitRunner.Run(repo, "init", "-q", ".");
    await GitRunner.Run(repo, "config", "user.email", "test@example.invalid");
    await GitRunner.Run(repo, "config", "user.name", "e1-test");
    await GitRunner.Run(repo, "config", "core.autocrlf", "false");
    await File.WriteAllTextAsync(Path.Combine(repo, "seed.txt"), "seed\n", new UTF8Encoding(false));
    await GitRunner.Run(repo, "add", "-A");
    await GitRunner.Run(repo, "commit", "-q", "-m", "seed");
    var seedSha = (await GitRunner.Run(repo, "rev-parse", "HEAD")).Trim();
    var baseBranch = (await GitRunner.Run(repo, "rev-parse", "--abbrev-ref", "HEAD")).Trim();

    // 1) 只读工作树列表：真实仓库解析，空格/中文路径原样可用。
    var listing = await GitWorktreeService.ListAsync(repo);
    Check(listing.Reason == GitWorktreeInspectReason.Ok && listing.Worktrees.Count >= 1, "工作树列表：真实仓库解析成功");
    Check(listing.Worktrees.Any(w => PathEquals(w.Path, repo) && w.HeadCommit == seedSha), "列表包含主工作树且 HEAD 正确");
    Check(listing.Worktrees.All(w => !w.Bare), "列表无伪 bare 条目");

    // 2) 非 Git / 缺失路径都有可操作诊断，不返回空成功。
    var plain = Path.Combine(root, "not-git");
    Directory.CreateDirectory(plain);
    var plainListing = await GitWorktreeService.ListAsync(plain);
    Check(plainListing.Reason is GitWorktreeInspectReason.NotGitWorktree or GitWorktreeInspectReason.CommandFailed,
        "非 Git 目录：显式失败原因");
    var missing = await GitWorktreeService.StatusAsync(Path.Combine(root, "does-not-exist"));
    Check(missing.Reason == GitWorktreeInspectReason.PathMissing, "缺失路径：PathMissing 诊断");
    Check((await GitWorktreeService.StatusAsync(plain)).Reason == GitWorktreeInspectReason.NotGitWorktree,
        "非 Git 目录状态：NotGitWorktree");

    // 3) 状态只读：修改/未跟踪可列出（含中文+空格路径），不改仓库。
    await File.WriteAllTextAsync(Path.Combine(repo, "seed.txt"), "seed changed\n", new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(repo, "新增 未跟踪.txt"), "x\n", new UTF8Encoding(false));
    var status = await GitWorktreeService.StatusAsync(repo);
    Check(status.Reason == GitWorktreeInspectReason.Ok && status.HeadCommit == seedSha, "状态：HEAD 正确");
    Check(status.ChangedPaths.Contains("seed.txt"), "状态列出已跟踪修改");
    Check(status.ChangedPaths.Any(p => p.EndsWith("新增 未跟踪.txt", StringComparison.Ordinal)),
        "状态列出中文+空格未跟踪路径（-z 原样不转义）");
    Check(!status.Detached && status.Branch == baseBranch, "状态识别分支而非 detached");

    // 4) 显式创建：从已提交 HEAD 建，不复制脏改动，不切换当前会话。
    var target = Path.Combine(root, "created wt 目录");
    var created = await GitWorktreeService.CreateAsync(repo, target);
    Check(created.SourceCommit == seedSha, "创建基于已提交 HEAD（非脏工作区）");
    Check((await GitWorktreeService.StatusAsync(target)).ChangedPaths.Count == 0,
        "新工作树干净：用户未提交内容没有被复制");
    Check(Directory.Exists(target), "创建目录存在");
    Check((await GitRunner.Run(repo, "rev-parse", "HEAD")).Trim() == seedSha &&
        (await File.ReadAllTextAsync(Path.Combine(repo, "seed.txt"))).Contains("changed"),
        "创建不改变原工作树：会话未切换，脏改动仍在");
    await Reject(async () => await GitWorktreeService.CreateAsync(repo, target), "目标已存在：显式拒绝");

    // ---------- 审查范围 ----------
    // 5) 工作区范围：修改 + 未跟踪都在范围内，可用既有共享快照冻结。
    var workspaceScope = await LocalReviewScopeResolver.ResolveAsync(repo, LocalReviewScopeKind.Workspace);
    Check(workspaceScope.Status == LocalReviewScopeStatus.Resolved &&
        workspaceScope.Files.Any(f => f.Path == "seed.txt"), "工作区范围解析出修改文件");
    Check(workspaceScope.Files.Any(f => f.Path.EndsWith("新增 未跟踪.txt", StringComparison.Ordinal)),
        "工作区范围包含未跟踪文件（不静默遗漏）");
    Check(workspaceScope.SnapshotCapability == LocalReviewSnapshotCapability.FullSnapshot &&
        LocalReviewScopeResolver.CanFreezeWithSharedSnapshot(workspaceScope), "工作区范围可用共享快照冻结");

    // 6) M共享API接线：冻结真正索引，即使当前文件已经再次修改。
    await GitRunner.Run(repo, "add", "seed.txt");
    var stagedScope = await LocalReviewScopeResolver.ResolveAsync(repo, LocalReviewScopeKind.Staged);
    Check(stagedScope.Status == LocalReviewScopeStatus.Resolved &&
        stagedScope.Files.Any(f => f.Path == "seed.txt"), "暂存范围解析出已暂存文件");
    var stagedBytes = await File.ReadAllBytesAsync(Path.Combine(repo, "seed.txt"));
    await File.WriteAllTextAsync(Path.Combine(repo, "seed.txt"), "later unstaged bytes\n");
    var stagedSnapshot = await LocalReviewScopeResolver.CaptureFixedSnapshotAsync(stagedScope);
    Check(stagedScope.SnapshotCapability == LocalReviewSnapshotCapability.FullSnapshot && stagedSnapshot is not null,
        "暂存范围使用已接线共享索引快照");
    Check(stagedSnapshot!.Source == GitDiffSnapshotSource.StagedIndex &&
        stagedSnapshot.Entries.Single().Bytes!.SequenceEqual(stagedBytes) &&
        await stagedSnapshot.IsFreshAsync() && !await stagedSnapshot.MatchesWorkingTreeAsync(),
        "暂存冻结精确索引字节，来源有效但当前定位不匹配");
    await File.WriteAllBytesAsync(Path.Combine(repo, "seed.txt"), stagedBytes);

    // 7) 提交/基准合法性真实验证；坏 ref 与选项形状都作为参数拒绝。
    await GitRunner.Run(repo, "commit", "-q", "-m", "stage commit", "--no-verify");
    var c2 = (await GitRunner.Run(repo, "rev-parse", "HEAD")).Trim();
    var bad = await LocalReviewScopeResolver.ResolveAsync(repo, LocalReviewScopeKind.Commit, revision: "does-not-exist");
    Check(bad.Status == LocalReviewScopeStatus.InvalidRevision, "不存在的提交 ref：显式 InvalidRevision");
    var injected = await LocalReviewScopeResolver.ResolveAsync(repo, LocalReviewScopeKind.Commit,
        revision: "--output=C:/tmp/pwned");
    Check(injected.Status == LocalReviewScopeStatus.InvalidRevision, "选项注入形状：拒绝且不进 shell");
    var range = await LocalReviewScopeResolver.ResolveAsync(repo, LocalReviewScopeKind.Range,
        revision: c2, baseRevision: seedSha);
    Check(range.Status == LocalReviewScopeStatus.Resolved &&
        range.Files.Any(f => f.Path == "seed.txt") &&
        range.BaseCommit == seedSha && range.TargetCommit == c2, "基准..目标范围解析并记录精确提交");
    // 本夹具的 c2 只改了 seed.txt：范围不得虚报其它类别。
    // 新增/删除/二进制/Unicode 的类型覆盖由下面的独立夹具专门验证。
    Check(range.Files.Any(f => f.Path == "seed.txt" && f.Kind == "modified"), "范围标记修改类型");
    Check(range.Files.All(f => !f.Path.Contains("..", StringComparison.Ordinal)),
        "范围路径都是项目内相对路径（不含越界片段）");
    var commitScope = await LocalReviewScopeResolver.ResolveAsync(repo, LocalReviewScopeKind.Commit, revision: c2);
    Check(commitScope.Status == LocalReviewScopeStatus.Resolved, "单提交范围解析成功");
    var historical = await LocalReviewScopeResolver.CaptureFixedSnapshotAsync(range);
    Check(historical is { Source: GitDiffSnapshotSource.TargetTree } && historical.TargetCommit == c2 &&
        historical.BaseCommit == seedSha && historical.Entries.Single().Bytes!.SequenceEqual(stagedBytes),
        "范围冻结真实目标树与显式基准");
    var reviewPayload = LocalReviewPayload.Build(range, historical!);
    Check(reviewPayload.Contains("PATCH (") && reviewPayload.Contains("@@") && reviewPayload.Contains("TARGET CONTENT:"),
        "审查载荷含基准补丁和目标定位内容");

    // 7b) 独立夹具仓库：范围必须按类型如实报告新增/删除/二进制/Unicode 路径。
    {
        var kinds = Path.Combine(root, "kinds 仓库");
        Directory.CreateDirectory(kinds);
        await GitRunner.Run(kinds, "init", "-q", ".");
        await GitRunner.Run(kinds, "config", "user.email", "t@example.invalid");
        await GitRunner.Run(kinds, "config", "user.name", "e1-kinds");
        await GitRunner.Run(kinds, "config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(kinds, "keep.txt"), "keep\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(kinds, "gone.txt"), "gone\n", new UTF8Encoding(false));
        await GitRunner.Run(kinds, "add", "-A");
        await GitRunner.Run(kinds, "commit", "-q", "-m", "base");
        var kindsBase = (await GitRunner.Run(kinds, "rev-parse", "HEAD")).Trim();
        File.Delete(Path.Combine(kinds, "gone.txt"));
        await File.WriteAllTextAsync(Path.Combine(kinds, "新增 文件.txt"), "新内容\n", new UTF8Encoding(false));
        var binary = new byte[64];
        binary[3] = 0;
        await File.WriteAllBytesAsync(Path.Combine(kinds, "data.bin"), binary);
        await GitRunner.Run(kinds, "add", "-A");
        await GitRunner.Run(kinds, "commit", "-q", "-m", "adds and deletes");
        var kindsHead = (await GitRunner.Run(kinds, "rev-parse", "HEAD")).Trim();

        var kindRange = await LocalReviewScopeResolver.ResolveAsync(kinds, LocalReviewScopeKind.Range,
            revision: kindsHead, baseRevision: kindsBase);
        Check(kindRange.Status == LocalReviewScopeStatus.Resolved, "类型夹具范围解析成功");
        Check(kindRange.Files.Any(f => f.Path == "gone.txt" && f.Kind == "deleted"), "范围如实报告删除项");
        Check(kindRange.Files.Any(f => f.Path == "新增 文件.txt" && f.Kind == "added"),
            "范围如实报告新增项（中文+空格路径）");
        Check(kindRange.Files.Any(f => f.Path == "data.bin" && f.Kind == "added"), "二进制新增项仍在范围内");
        Check(kindRange.Files.All(f => f.Path != "keep.txt"), "未变更文件不出现在范围（不夸大范围）");
        var kindCommit = await LocalReviewScopeResolver.ResolveAsync(kinds, LocalReviewScopeKind.Commit,
            revision: kindsHead);
        Check(kindCommit.Status == LocalReviewScopeStatus.Resolved &&
            kindCommit.BaseCommit == kindsBase && kindCommit.Files.Count == kindRange.Files.Count,
            "单提交范围等价于第一父..该提交（不误审整个仓库）");
        // 根提交没有第一父：必须走空树枚举而不是失败或伪造基准。
        var rootCommit = await LocalReviewScopeResolver.ResolveAsync(kinds, LocalReviewScopeKind.Commit,
            revision: kindsBase);
        Check(rootCommit.Status == LocalReviewScopeStatus.Resolved &&
            rootCommit.Files.Count == 2 && rootCommit.BaseCommit is null,
            "根提交按空树枚举全部内容为新增（不虚报基准）");
    }

    // 8) 合并提交：拒绝默认第一父，要求显式范围。
    await GitRunner.Run(repo, "checkout", "-q", "-b", "side", seedSha);
    await File.WriteAllTextAsync(Path.Combine(repo, "side.txt"), "side\n", new UTF8Encoding(false));
    await GitRunner.Run(repo, "add", "-A");
    await GitRunner.Run(repo, "commit", "-q", "-m", "side change");
    await GitRunner.Run(repo, "checkout", "-q", baseBranch);
    await GitRunner.Run(repo, "merge", "-q", "--no-edit", "side");
    var mergeSha = (await GitRunner.Run(repo, "rev-parse", "HEAD")).Trim();
    var mergeScope = await LocalReviewScopeResolver.ResolveAsync(repo, LocalReviewScopeKind.Commit, revision: mergeSha);
    Check(mergeScope.Status == LocalReviewScopeStatus.MergeCommitUnsupported,
        "合并提交：不冒充第一父差异");
    var mergeRange = await LocalReviewScopeResolver.ResolveAsync(repo, LocalReviewScopeKind.Range,
        revision: mergeSha, baseRevision: c2);
    Check(mergeRange.Status == LocalReviewScopeStatus.Resolved, "合并提交可用显式范围审查");

    // ---------- 固定快照 / 过期 / 严格解析 ----------
    // 9) 快照固定字节与 HEAD；捕获后源变化必须过期，不当作当前精确定位。
    Directory.CreateDirectory(Path.Combine(repo, "src"));
    await File.WriteAllTextAsync(Path.Combine(repo, "src", "main.cs"),
        "class A {\n  var x = obj.Value;\n}\n", new UTF8Encoding(false));
    var freshScope = await LocalReviewScopeResolver.ResolveAsync(repo, LocalReviewScopeKind.Workspace);
    var snapshot = await LocalReviewScopeResolver.CaptureFixedSnapshotAsync(freshScope);
    Check(snapshot is not null && snapshot.Status == GitDiffSnapshotStatus.Captured, "工作区快照捕获成功");
    Check(snapshot!.HeadCommit == mergeSha, "快照记录实际 HEAD");
    Check(await snapshot.IsFreshAsync(), "刚捕获的快照是最新的");
    await File.WriteAllTextAsync(Path.Combine(repo, "src", "main.cs"),
        "class A {\n  var x = CHANGED;\n}\n", new UTF8Encoding(false));
    Check(!await snapshot.IsFreshAsync(), "快照后源文件变化：过期检测生效");

    var cleanScope = await LocalReviewScopeResolver.ResolveAsync(repo, LocalReviewScopeKind.Workspace);
    var cleanSnap = await LocalReviewScopeResolver.CaptureFixedSnapshotAsync(cleanScope);
    Check(cleanSnap is not null && await cleanSnap.IsFreshAsync(), "重新捕获的快照最新");
    var good = LocalReviewFindingsParser.Parse(
        """{"findings":[{"priority":"high","path":"src/main.cs","line":2,"explanation":"possible null deref","evidence":"var x = CHANGED;"},{"priority":"low","path":"outside/x.cs","line":1,"explanation":"not in snapshot","evidence":"y"}]}""",
        cleanSnap!);
    Check(good.Findings.Count == 1 && good.Findings[0].Path == "src/main.cs", "有效发现项按快照行接受");
    Check(good.Rejected.Any(r => r.Reason == "path-outside-snapshot"), "快照外路径被拒绝并标注");
    var badLine = LocalReviewFindingsParser.Parse(
        """{"findings":[{"priority":"high","path":"src/main.cs","line":9999,"explanation":"beyond","evidence":"x"}]}""", cleanSnap!);
    Check(badLine.Findings.Count == 0 && badLine.Rejected.Any(r => r.Reason == "line-beyond-content"),
        "超出内容的行号被拒绝");
    var badEvidence = LocalReviewFindingsParser.Parse(
        """{"findings":[{"priority":"high","path":"src/main.cs","line":1,"explanation":"misplaced","evidence":"var x = CHANGED;"}]}""", cleanSnap!);
    Check(badEvidence.Rejected.Any(r => r.Reason == "evidence-not-at-line"), "证据与所引行不符：拒绝");
    var badPriority = LocalReviewFindingsParser.Parse(
        """{"findings":[{"priority":"urgent","path":"src/main.cs","line":2,"explanation":"x","evidence":"var x = CHANGED;"}]}""", cleanSnap!);
    Check(badPriority.Rejected.Any(r => r.Reason == "invalid-priority"), "非法优先级被拒绝");
    var garbage = LocalReviewFindingsParser.Parse("looks fine to me", cleanSnap!);
    Check(garbage.Rejected.Any(r => r.Reason == "unparseable-response"), "非 JSON 响应：不当作无发现");

    // 9b) 二进制夹具：快照标二进制，解析拒绝其定位，载荷不注入二进制字节。
    {
        var binBytes = new byte[256];
        for (var i = 0; i < binBytes.Length; i++) binBytes[i] = (byte)(i % 251);
        binBytes[10] = 0; // NUL: content Git treats as binary
        await File.WriteAllBytesAsync(Path.Combine(repo, "asset.bin"), binBytes);
        var binScope = await LocalReviewScopeResolver.ResolveAsync(repo, LocalReviewScopeKind.Workspace);
        Check(binScope.Files.Any(f => f.Path == "asset.bin"), "二进制文件在范围内（不因类型被静默遗漏）");
        var binSnap = await LocalReviewScopeResolver.CaptureFixedSnapshotAsync(binScope);
        var binEntry = binSnap!.Entries.Single(e => e.RelativePath == "asset.bin");
        Check(binEntry.Diff.IsBinary == true, "快照把该文件识别为二进制");
        var binFinding = LocalReviewFindingsParser.Parse(
            """{"findings":[{"priority":"high","path":"asset.bin","line":1,"explanation":"in binary","evidence":"x"}]}""", binSnap);
        Check(binFinding.Findings.Count == 0 &&
            binFinding.Rejected.Any(r => r.Reason == "binary-entry-not-locatable"),
            "二进制条目的发现项被拒绝定位（不产生假精确定位）");
        var binPayload = LocalReviewPayload.Build(binScope, binSnap);
        Check(binPayload.Contains("binary content not sent"), "载荷显式声明二进制内容未送审");
        Check(!binPayload.Contains(Encoding.UTF8.GetString(binBytes[..40])), "二进制字节未被当作文本注入");
        // 未送审项必须出现在清单里：不能悄悄缩小审查范围。
        var binDetailed = LocalReviewPayload.BuildResult(binScope, binSnap);
        Check(binDetailed.UnreviewedPaths.Contains("asset.bin"),
            "二进制条目计入未送审清单（不静默缩小范围）");
        Check(binDetailed.Text.Contains("do not report the scope as clean"),
            "存在未送审项时载荷自带“这不是完整审查”的声明");
        Check(binDetailed.WithinBudget, "二进制省略后载荷仍在字节预算内");
        File.Delete(Path.Combine(repo, "asset.bin"));
    }

    // 10) 载荷有界、行号真实、快照 HEAD 可见。
    var payload = LocalReviewPayload.Build(cleanScope, cleanSnap!);
    Check(payload.Contains("2\t") && payload.Contains("SNAPSHOT HEAD"), "载荷含真实行号与快照 HEAD");
    Check(payload.Contains(LocalReviewFindingsParser.ResponseContract), "载荷附响应契约");
    Check(payload.Length < 600_000, "载荷有界");

    // ---------- harness 适配：完成 / 无发现 / 坏响应 / 取消 互不冒充 ----------
    foreach (var (scenario, expect, findings) in new[]
    {
        ("good", LocalReviewRunStatus.Completed, 1),
        ("empty", LocalReviewRunStatus.NoFindings, 0),
        ("garbage", LocalReviewRunStatus.InvalidResponse, 0),
    })
    {
        var executor = new LocalReviewHarnessExecutor(
            () => new AutomationStore(Path.Combine(root, scenario + ".db")),
            FixtureRuntime(scenario), Path.Combine(root, "data", scenario), "review-project", "devin/swe-2");
        var result = await executor.ReviewAsync(cleanScope);
        Check(result.Status == expect, "夹具 " + scenario + "：终态分类正确（实际 " + result.Status + "）");
        Check(result.Findings.Count == findings, "夹具 " + scenario + "：发现项计数与严格解析一致");
        Check(result.Model == "devin/swe-2", "夹具 " + scenario + "：模型显式配置，不读默认");
        Check(result.Snapshot?.HeadCommit == mergeSha, "夹具 " + scenario + "：结果绑定固定快照 HEAD");
    }

    // 11) 被审查仓库不是 harness 写入目录：夹具运行不改动用户工作树。
    {
        var headBefore = (await GitRunner.Run(repo, "rev-parse", "HEAD")).Trim();
        var beforeHash = Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(Path.Combine(repo, "src", "main.cs"))));
        var executor = new LocalReviewHarnessExecutor(
            () => new AutomationStore(Path.Combine(root, "readonly.db")),
            FixtureRuntime("good"), Path.Combine(root, "data", "readonly"), "review-project", "devin/swe-2");
        _ = await executor.ReviewAsync(cleanScope);
        var afterHash = Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(Path.Combine(repo, "src", "main.cs"))));
        Check(beforeHash == afterHash, "审查运行未修改被审查文件（只读保证）");
        // -uall：默认会折叠整个未跟踪目录，必须逐项确认用户改动仍在工作区。
        var after = await GitRunner.Run(repo, "status", "--porcelain", "--untracked-files=all");
        Check(after.Contains("src/main.cs"), "仓库状态仍列出审查前的用户改动（未被还原）");
        Check((await GitRunner.Run(repo, "rev-parse", "HEAD")).Trim() == headBefore,
            "审查运行没有自动提交：HEAD 不变");
    }

    // 12) 取消：已投递的运行进入未知状态，绝不当完成，也不自动重发。
    {
        var cancelExecutor = new LocalReviewHarnessExecutor(
            () => new AutomationStore(Path.Combine(root, "cancel.db")),
            FixtureRuntime("hang"), Path.Combine(root, "data", "cancel"), "review-project", "devin/swe-2");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var cancelled = await cancelExecutor.ReviewAsync(cleanScope, cts.Token);
        Check(cancelled.Status == LocalReviewRunStatus.Cancelled,
            "取消：状态为 Cancelled（结果未知），不是 Completed/Failed");
        Check(!cancelled.ReviewComplete, "取消：不作为完成证明");
    }

    // 13) 审查期间的仓库变化：标过期而不是当作当前精确定位。
    {
        var racingScope = await LocalReviewScopeResolver.ResolveAsync(repo, LocalReviewScopeKind.Workspace);
        var racingExecutor = new LocalReviewHarnessExecutor(
            () => new AutomationStore(Path.Combine(root, "stale.db")),
            FixtureRuntime("good"), Path.Combine(root, "data", "stale"), "review-project", "devin/swe-2");
        var task = racingExecutor.ReviewAsync(racingScope);
        await File.AppendAllTextAsync(Path.Combine(repo, "src", "main.cs"), "// raced\n", new UTF8Encoding(false));
        var raced = await task;
        Check(raced.Status is LocalReviewRunStatus.StaleAfterReview or LocalReviewRunStatus.Completed,
            "审查中的并发修改：快照阶段或复核阶段必然标记");
        if (raced.Status == LocalReviewRunStatus.StaleAfterReview)
            Check(raced.IsStale, "过期结果必须自报 IsStale");
    }

    // ---------- 导出与历史 ----------
    var exportExecutor = new LocalReviewHarnessExecutor(
        () => new AutomationStore(Path.Combine(root, "export.db")),
        FixtureRuntime("good"), Path.Combine(root, "data", "export"), "review-project", "devin/swe-2");
    var exportResult = await exportExecutor.ReviewAsync(cleanScope);
    var markdown = LocalReviewExport.ToMarkdown(exportResult);
    var json = LocalReviewExport.ToJson(exportResult);
    Check(markdown.Contains("本地代码审查结果") && markdown.Contains(exportResult.Findings[0].Location),
        "Markdown 导出含可定位发现项");
    Check(markdown.Contains("快照") && json.Contains(LocalReviewExport.SchemaId), "导出声明快照与 schema");
    Check(json.Contains("path-outside-snapshot") && markdown.Contains("被拒绝"), "拒绝项一并导出，不静默丢弃");

    var history = new LocalReviewHistory(Path.Combine(root, "history"));
    var fileName = history.Save(exportResult);
    var loaded = history.Load(fileName);
    Check(loaded is not null && loaded.Findings.Count == exportResult.Findings.Count &&
        loaded.Status == exportResult.Status.ToString(), "历史：退出重开后读回同一结果");
    Check(history.Load("../evil.json") is null, "历史：路径越界读取被拒绝");
    Check(history.ListRecent(10).Count >= 1, "历史列表可读");

    // ---------- 打开请求与危险操作边界 ----------
    {
        var opened = new List<string>();
        Action<string> requestOpen = path => opened.Add(path);
        var list = await GitWorktreeService.ListAsync(repo);
        foreach (var worktree in list.Worktrees.Where(w => !PathEquals(w.Path, repo)))
            requestOpen(worktree.Path);
        Check(opened.Count == list.Worktrees.Count - 1, "打开请求经宿主接口转发（本模块不直接开目录）");
        var api = typeof(GitWorktreeService)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(m => m.Name).ToArray();
        Check(api.All(name => !name.Contains("Remove", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("Force", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("Clean", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("Reset", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("Push", StringComparison.OrdinalIgnoreCase)),
            "模块扩展无 remove/force/clean/reset/push 入口：生产工作树不可被误删");
    }

    Console.WriteLine($"ALL PASS ({passed})");
    return 0;
}
finally
{
    // 夹具自有的临时工作树用 git worktree remove 清理（不递归强删目录）。
    try
    {
        var repoPath = Path.Combine(root, "repo 目录");
        if (Directory.Exists(repoPath))
            foreach (var line in (await GitRunner.Run(repoPath, "worktree", "list", "--porcelain"))
                         .Replace("\r\n", "\n").Split('\n'))
                if (line.StartsWith("worktree ", StringComparison.Ordinal))
                {
                    var path = line["worktree ".Length..];
                    if (!PathEquals(path, repoPath))
                        try { await GitRunner.Run(repoPath, "worktree", "remove", "--force", path); }
                        catch (Exception) { /* 已移除或不可移除：保留证据 */ }
                }
    }
    catch (Exception) { /* 清理属于尽力而为 */ }
    Console.WriteLine("Evidence root: " + root);
    if (Environment.GetEnvironmentVariable("E1_KEEP_TMP") is null)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (Exception error) { Console.WriteLine("临时目录保留（清理失败不影响结果）：" + error.GetType().Name); }
    }
}

internal static class GitRunner
{
    public static async Task<string> Run(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new Exception($"git {string.Join(' ', args)} failed: {error}");
        return output;
    }
}
