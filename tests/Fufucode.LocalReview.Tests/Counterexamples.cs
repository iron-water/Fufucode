using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Fufucode.App.Abstractions.Automation;
using Fufucode.Core;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Features.LocalReview;
using Fufucode.Plugins.AutoDev;
using Fufucode.Plugins.AutoDev.Engine.Harnesses;

namespace Fufucode.LocalReview.Tests;

/// <summary>
/// M 独立复核（docs/records/M-E1-review-2026-10-12.md，decision=rework，reviewedHead=2b695cc）
/// 的七项反例，忠实移植自主区 <c>tmp/e1-audit/Program.cs</c> + <c>tmp/m-e1-counterexamples-final.log</c>：
/// 只调用模块公开 API、真实临时 Git 仓库与 Codex 协议夹具子进程，不用模型、不改主区。
/// 断言保持 M 的原语义，不放宽：
///   CE1 超 2 MiB 的多字节响应必须被分类为不完整/无效，不能抛异常；
///   CE2 数字字符串优先级与空证据必须被拒绝；
///   CE3 载荷总预算必须按 UTF-8 字节成立（多字节内容也不能越界）；
///   CE4 审查期间源变动 + 空发现仍必须标过期，不能提前当成“无发现的完成审查”；
///   CE5 全部发现项无效是无效响应，不是完成审查；
///   CE6 内容被省略后不能报告完整干净审查；
///   CE7 终态未知（取消/Unknown/Interrupted）的运行必须保留 scratch 与身份供对账。
/// </summary>
internal static class Counterexamples
{
    /// <summary>
    /// Codex 协议夹具入口（对应主区 Program.cs 的 --protocol 分支）。
    /// file 的含义按模式区分：changed-empty 要在响应前改写的被审查文件；hang 要落地的启动标记。
    /// </summary>
    public static async Task ProtocolAsync(string mode, string? file)
    {
        _ = await Console.In.ReadToEndAsync();
        if (mode == "unknown")
        {
            Console.WriteLine("{\"type\":\"thread.started\",\"thread_id\":\"audit-fixture\"}");
            return; // No terminal event: process exit is not completion proof.
        }
        if (mode == "hang")
        {
            // Marker proves the child process actually started, so the caller can cancel a run that
            // really is in flight (cancelling before dispatch would prove nothing about retention).
            if (file is not null) File.WriteAllText(file, "started\n", new UTF8Encoding(false));
            await Task.Delay(30000);
            return;
        }
        if (mode == "changed-empty" && file is not null)
            File.WriteAllText(file, "changed during review\n", new UTF8Encoding(false));
        var response = mode switch
        {
            "invalid-only" => """{"findings":[{"priority":"impossible","path":"a.cs","line":1,"explanation":"bad","evidence":"code"}]}""",
            "done" => """{"findings":[{"priority":"high","path":"a.cs","line":1,"explanation":"reviewed","evidence":"original modified"}]}""",
            _ => """{"findings":[]}""",
        };
        Console.WriteLine("""{"type":"thread.started","thread_id":"audit-fixture"}""");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            type = "item.completed",
            item = new { type = "agent_message", text = response },
        }));
        Console.WriteLine("""{"type":"turn.completed"}""");
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        BatchHarnessAdapters.Register();
        var root = Path.Combine(Path.GetTempPath(),
            "fufucode-e1-counterexamples-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Console.WriteLine("CE evidence root: " + root);

        var snapshot = new FileDiffSnapshot(root, new string('a', 40), DateTimeOffset.UtcNow, root,
            [new GitDiffSnapshotEntry("a.cs", true, true, false, null, Encoding.UTF8.GetBytes("code\n"),
                new FileDiff("a.cs", null, false, false, [], null), null)],
            GitDiffSnapshotStatus.Captured, null);

        // CE1: 750000 个中文字（UTF-8 > 2 MiB，但字符数 < 2 MiB）→ 分类，不抛异常。
        var huge = "{\"findings\":[],\"padding\":\"" + new string('中', 750_000) + "\"}";
        try
        {
            var parsed = LocalReviewFindingsParser.Parse(huge, snapshot);
            check(parsed.Truncated && parsed.Findings.Count == 0,
                "CE1 超字节上限的多字节响应被分类为不完整，而不是抛异常");
        }
        catch (Exception error)
        {
            check(false, "CE1 超字节上限的多字节响应抛了 " + error.GetType().Name);
        }

        // CE2: 未定义数字优先级 / 空证据 → 都不得算有效发现。
        var numeric = LocalReviewFindingsParser.Parse(
            """{"findings":[{"priority":"99","path":"a.cs","line":1,"explanation":"bad","evidence":"code"}]}""",
            snapshot);
        check(numeric.Findings.Count == 0, "CE2a 未定义的数字优先级被拒绝");
        var emptyEvidence = LocalReviewFindingsParser.Parse(
            """{"findings":[{"priority":"high","path":"a.cs","line":1,"explanation":"bad","evidence":""}]}""",
            snapshot);
        check(emptyEvidence.Findings.Count == 0, "CE2b 空证据的发现项被拒绝（必须有可核对证据）");

        // CE3: 20 份每份 10000 个中文字 → UTF-8 总预算必须成立。
        var unicodeEntries = Enumerable.Range(0, 20).Select(i => new GitDiffSnapshotEntry(
            "unicode" + i + ".cs", true, true, false, null,
            Encoding.UTF8.GetBytes(new string('中', 10_000)),
            new FileDiff("unicode" + i + ".cs", null, false, false, [], null), null)).ToArray();
        var unicodeSnapshot = snapshot with { Entries = unicodeEntries };
        var unicodeScope = new LocalReviewScopeResolution(LocalReviewScopeKind.Workspace,
            LocalReviewScopeStatus.Resolved, root, root, snapshot.HeadCommit, null, null,
            LocalReviewSnapshotCapability.FullSnapshot, [], null);
        var payloadBytes = Encoding.UTF8.GetByteCount(LocalReviewPayload.Build(unicodeScope, unicodeSnapshot));
        check(payloadBytes <= LocalReviewPayload.TotalLimitBytes,
            "CE3 载荷总预算按 UTF-8 字节成立（多字节内容也不例外，实际 " + payloadBytes + " 字节）");
        var oversizedMetadata = unicodeSnapshot with { RepositoryRoot = new string('中', 200_000) };
        var metadataPayload = LocalReviewPayload.BuildResult(unicodeScope, oversizedMetadata);
        check(metadataPayload.WithinBudget && metadataPayload.UnreviewedPaths.Count == 20,
            "CE3b 元数据本身超限时仍守住字节预算并标全部未送审");

        // CE4/CE5/CE6: 真实临时仓库 + 真实协议夹具子进程。
        var repository = Path.Combine(root, "repo");
        Directory.CreateDirectory(repository);
        await GitAsync(repository, "init", "-q");
        await GitAsync(repository, "config", "user.name", "Fixture");
        await GitAsync(repository, "config", "user.email", "fixture@example.invalid");
        await GitAsync(repository, "config", "core.autocrlf", "false");
        var file = Path.Combine(repository, "a.cs");
        File.WriteAllText(file, "original\n", new UTF8Encoding(false));
        await GitAsync(repository, "add", "a.cs");
        await GitAsync(repository, "commit", "-q", "-m", "fixture");

        foreach (var mode in new[] { "changed-empty", "invalid-only", "omitted" })
        {
            File.WriteAllText(file, mode == "omitted" ? new string('x', 75_000) : "code\n",
                new UTF8Encoding(false));
            var scope = await LocalReviewScopeResolver.ResolveAsync(repository,
                LocalReviewScopeKind.Workspace);
            ExecutionBaseLibrary.Register(new AutoDevExecutionOwner(_ => FixtureRuntime(mode, file)));
            var executor = new LocalReviewHarnessExecutor(
                Path.Combine(root, mode), "fixture", "devin/swe-2");
            var result = await executor.ReviewAsync(scope);
            var unreviewed = UnreviewedOf(result);
            Console.WriteLine($"  [CE] {mode} status={result.Status} complete={result.ReviewComplete} " +
                $"stale={result.IsStale} findings={result.Findings.Count} " +
                $"rejected={result.Rejected.Count} unreviewed={unreviewed.Count}");
            if (mode == "changed-empty")
                check(result.IsStale && result.Status != LocalReviewRunStatus.NoFindings,
                    "CE4 审查期间源变动且响应为空：仍标过期，不提前当无发现完成");
            else if (mode == "invalid-only")
                check(!result.ReviewComplete && result.Status == LocalReviewRunStatus.InvalidResponse,
                    "CE5 全部发现项无效：无效响应，不是完成审查");
            else
                check(!result.ReviewComplete, "CE6 每文件超限导致内容未送审：不能算完整干净审查");
        }

        var totalRepository = Path.Combine(root, "total-budget-repo");
        Directory.CreateDirectory(totalRepository);
        await GitAsync(totalRepository, "init", "-q");
        await GitAsync(totalRepository, "config", "user.name", "Fixture");
        await GitAsync(totalRepository, "config", "user.email", "fixture@example.invalid");
        File.WriteAllText(Path.Combine(totalRepository, "seed.txt"), "seed\n");
        await GitAsync(totalRepository, "add", "seed.txt");
        await GitAsync(totalRepository, "commit", "-q", "-m", "fixture");
        for (var i = 0; i < 20; i++)
            File.WriteAllText(Path.Combine(totalRepository, $"unicode{i}.cs"), new string('中', 10_000));
        var totalScope = await LocalReviewScopeResolver.ResolveAsync(totalRepository, LocalReviewScopeKind.Workspace);
        ExecutionBaseLibrary.Register(new AutoDevExecutionOwner(_ => FixtureRuntime("empty", file)));
        var totalExecutor = new LocalReviewHarnessExecutor(
            Path.Combine(root, "total"), "fixture", "devin/swe-2");
        var totalResult = await totalExecutor.ReviewAsync(totalScope);
        check(!totalResult.ReviewComplete && UnreviewedOf(totalResult).Count > 0,
            "CE6b 总预算省略部分多字节文件后不能报告完整干净审查");
        check(LocalReviewExport.ToMarkdown(totalResult).Contains("不代表全范围干净") &&
            LocalReviewExport.ToDocument(totalResult).Unreviewed.Count > 0,
            "CE6c 导出保留未送审清单并明确不代表全范围干净");

        // CE7: 未知终态必须保留 scratch 目录与身份；明确终态才释放。
        {
            File.WriteAllText(file, "code\n", new UTF8Encoding(false));
            var unknownRoot = Path.Combine(root, "unknown");
            var scratchParent = Path.Combine(unknownRoot, "scratch");
            var scope = await LocalReviewScopeResolver.ResolveAsync(repository, LocalReviewScopeKind.Workspace);
            // 夹具的第三个参数是"子进程启动标记文件"，不是被审查文件：hang 模式靠它证明真的启动了。
            var marker = Path.Combine(unknownRoot, "child-started.txt");
            ExecutionBaseLibrary.Register(new AutoDevExecutionOwner(_ => FixtureRuntime("hang", marker)));
            var executor = new LocalReviewHarnessExecutor(
                unknownRoot, "fixture", "devin/swe-2");
            // 必须等真实投递发生（子进程启动并落地标记）之后再取消：在派发前取消属于
            // "什么都没发出去"，没有需要了对账的未知运行，也就证明不了保留行为。
            using var cts = new CancellationTokenSource();
            var running = executor.ReviewAsync(scope, cts.Token);
            var dispatched = WaitUntil(() => File.Exists(marker), TimeSpan.FromSeconds(30));
            check(dispatched, "CE7a1 夹具子进程确已启动，取消才有意义");
            cts.Cancel();
            var cancelled = await running;
            Console.WriteLine($"  [CE] cancelled status={cancelled.Status} retained={RetainedOf(cancelled) ?? "-"}");
            check(cancelled.Status == LocalReviewRunStatus.Cancelled, "CE7a 取消运行分类为 Cancelled");
            check(!string.IsNullOrWhiteSpace(cancelled.RunId), "CE7a2 取消发生在真实投递之后（有 runId）");
            check(Directory.Exists(scratchParent) && Directory.GetDirectories(scratchParent).Length >= 1,
                "CE7b 终态未知的运行保留 scratch 目录（未在 finally 中删除）");
            check(!string.IsNullOrWhiteSpace(cancelled.RunId) &&
                Directory.Exists(Path.Combine(scratchParent, cancelled.RunId!)),
                "CE7c 保留的 scratch 与运行身份（runId）一致，可供对账");
            // 后端按 runId 哈希建证据目录，所以这里核对目录确有内容，而不是猜路径形状。
            var evidenceRoot = Path.Combine(unknownRoot, "evidence");
            check(Directory.Exists(evidenceRoot) && Directory.GetFileSystemEntries(evidenceRoot).Length >= 1,
                "CE7d 运行证据目录保留");
            var retained = RetainedOf(cancelled);
            check(retained is not null && Directory.Exists(retained),
                "CE7e 结果显式暴露保留的 scratch 路径");

            var doneRoot = Path.Combine(root, "completed");
            ExecutionBaseLibrary.Register(new AutoDevExecutionOwner(_ => FixtureRuntime("done", file)));
            var doneExecutor = new LocalReviewHarnessExecutor(
                doneRoot, "fixture", "devin/swe-2");
            // 必须仍有工作区改动（夹具证据是 "original"），否则范围是空范围，走不到完成审查。
            File.WriteAllText(file, "original modified\n", new UTF8Encoding(false));
            var doneScope = await LocalReviewScopeResolver.ResolveAsync(repository,
                LocalReviewScopeKind.Workspace);
            var done = await doneExecutor.ReviewAsync(doneScope);
            Console.WriteLine($"  [CE] completed status={done.Status} scratchLeft=" +
                (Directory.Exists(Path.Combine(doneRoot, "scratch"))
                    ? string.Join(",", Directory.GetDirectories(Path.Combine(doneRoot, "scratch")))
                    : "none"));
            check(done.Status == LocalReviewRunStatus.Completed,
                "CE7f0 明确完成场景确实是 Completed（否则下面的释放断言没有意义）");
            check(RetainedOf(done) is null && ScratchEntries(Path.Combine(doneRoot, "scratch")).Length == 0,
                "CE7f 明确完成的运行释放自己的 scratch（不累积垃圾目录）");

            var missingTerminalRoot = Path.Combine(root, "missing-terminal");
            ExecutionBaseLibrary.Register(new AutoDevExecutionOwner(_ => FixtureRuntime("unknown", file)));
            var unknownExecutor = new LocalReviewHarnessExecutor(
                missingTerminalRoot, "fixture", "devin/swe-2");
            var unknown = await unknownExecutor.ReviewAsync(doneScope);
            check(!unknown.ReviewComplete && RetainedOf(unknown) is { } retainedPath &&
                Directory.Exists(retainedPath), "CE7g 缺少终态的真实子进程退出后仍保留 scratch");
        }
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private static string[] ScratchEntries(string scratchParent) =>
        Directory.Exists(scratchParent) ? Directory.GetDirectories(scratchParent) : [];

    // 反例必须能在修复前的代码上编译运行（先红后绿），所以新增字段用反射读取：
    // 修复前属性不存在，等价于"没有保留 scratch / 没有未送审清单"，正是红状态的语义。
    private static string? RetainedOf(LocalReviewRunResult result) =>
        (typeof(LocalReviewRunResult).GetProperty("RetainedScratch")
            ?.GetValue(result) as string);

    private static IReadOnlyList<string> UnreviewedOf(LocalReviewRunResult result) =>
        (typeof(LocalReviewRunResult).GetProperty("UnreviewedPaths")
            ?.GetValue(result) as IReadOnlyList<string>) ?? [];

    /// <summary>
    /// 夹具子进程 = 本测试可执行文件自身（忠实移植主区做法：dotnet 宿主时补 assembly 路径）。
    /// </summary>
    private static HarnessRuntime FixtureRuntime(string mode, string watchFile)
    {
        var exe = Environment.ProcessPath!;
        var prefix = new List<string>();
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            prefix.Add(typeof(Counterexamples).Assembly.Location);
        prefix.AddRange(["--protocol", mode, watchFile]);
        return new HarnessRuntime("fixture", "codex", exe, prefix.ToArray(), "fixture-v1");
    }

    private static async Task GitAsync(string directory, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new Exception($"git {string.Join(' ', args)} failed: {await error}");
        _ = await output;
    }
}
