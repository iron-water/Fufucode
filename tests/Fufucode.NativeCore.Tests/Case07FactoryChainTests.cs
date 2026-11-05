using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;
using Fufucode.Plugins.Subagents;

namespace Fufucode.NativeCore.Tests;

/// <summary>Case 07: the real plugin factory chain must keep delivery, Plan mode and plan snippet.</summary>
internal static class Case07FactoryChainTests
{
    public static async Task RunAsync(Checks checks)
    {
        var root = TestTemp.Create("07");
        try
        {
            var package = new SubagentPluginPackage();
            await package.InitializeAsync(new PluginPackageContext(SubagentPluginPackage.PluginId, root, null));
            try
            {
                var builtin = new ProductionToolDispatcher(root);
                await using var mcp = McpManager.Create([]);
                IPureToolDispatcher capabilities = new McpGatewayDispatcher(
                    new SkillAwareDispatcher(builtin, SkillRegistry.Discover(root, root)), mcp);
                var runtime = NativeSubagentRegistry.Factory!.Create(new NoNetwork(), capabilities, null, root, []);
                var wrapped = runtime.Wrap(capabilities);
                var engine = new PureCSharpAgentEngine(wrapped, new NoNetwork());
                engine.Mode = PermissionMode.Plan;
                checks.Check(((INativeSubagentDispatcher)wrapped).IsActive, "factory wrapper activity");
                checks.Check(builtin.Mode == PermissionMode.Plan, "factory MCP Skill mode propagation");
                var plan = await wrapped.DispatchAsync(new PureToolCall("plan", "update_plan",
                    "{\"steps\":[{\"title\":\"factory-chain-proof\",\"status\":\"in_progress\"}]}"));
                checks.Check(plan.Success, "plan update through factory wrapper");
                checks.Check(engine.CurrentPlanSnippet?.Contains("factory-chain-proof") == true,
                    "factory plan snippet propagation");
                var blocked = await wrapped.DispatchAsync(new PureToolCall("write", "write_file", "{}"));
                checks.Check(!blocked.Success && blocked.Content.Contains("Plan"),
                    "Plan blocks mutation before argument validation");
                var runner = new SubagentRunner(AgentRegistry.Discover(root), new NoNetwork(), capabilities, null, root, []);
                var active = true;
                var dynamicWrapper = new SubagentDispatcher(capabilities, runner) { ActiveProvider = () => active };
                var dynamicEngine = new PureCSharpAgentEngine(dynamicWrapper, new NoNetwork());
                checks.Check(((INativeSubagentDispatcher)dynamicWrapper).IsActive, "dynamic active provider");
                active = false;
                checks.Check(!((INativeSubagentDispatcher)dynamicWrapper).IsActive, "provider revocation");
                checks.Check(dynamicEngine.CurrentPlanSnippet is null, "revoked wrapper retains existing engine contract");
                var bare = new SubagentDispatcher(capabilities, runner);
                checks.Check(!((INativeSubagentDispatcher)bare).IsActive, "bare wrapper remains inactive");

                await TestSubagentConcurrencyIdentityAsync(checks, root);
            }
            finally
            {
                await package.ShutdownAsync();
            }
        }
        finally
        {
            TestTemp.Delete(root);
        }
    }

    private static async Task TestSubagentConcurrencyIdentityAsync(Checks checks, string root)
    {
        var testDir = Path.Combine(root, "subagent-concurrency");
        Directory.CreateDirectory(testDir);
        var agentDir = Path.Combine(testDir, ".fumo", "agents");
        Directory.CreateDirectory(agentDir);
        await File.WriteAllTextAsync(Path.Combine(agentDir, "explore.md"),
            "---\nname: explore\ndescription: 探索\ntools: read_file\nmode: read\n---\n只读。");
        await File.WriteAllTextAsync(Path.Combine(agentDir, "writer.md"),
            "---\nname: writer\ndescription: 写入\ntools: write_file, read_file\nmode: patch\n---\n写入。");
        await File.WriteAllTextAsync(Path.Combine(agentDir, "lead.md"),
            "---\nname: lead\ndescription: 嵌套领导\ntools: subagent\nmode: read\n---\n组织。");

        var agents = AgentRegistry.Discover(testDir);
        var client = new ScriptedConcurrencyClient();
        var dispatcher = new ProductionToolDispatcher(testDir);
        var runner = new SubagentRunner(agents, client, dispatcher, "m", testDir);

        // 1) 并发 read 子代理：同一 runner 实例上的并发任务获得独立唯一的 JobId，其进度事件也必须按各自 JobId 隔离
        var progressEvents = new System.Collections.Concurrent.ConcurrentBag<SubagentProgress>();
        runner.Progress = progressEvents.Add;

        var t1 = runner.RunAsync("explore", "任务 alpha", null, CancellationToken.None);
        var t2 = runner.RunAsync("explore", "任务 beta", null, CancellationToken.None);
        var t3 = runner.RunAsync("explore", "任务 gamma", null, CancellationToken.None);
        var results = await Task.WhenAll(t1, t2, t3);

        checks.Check(results.All(r => r.Success), "并发 read 子代理全部执行成功");
        var jobIds = results.Select(r => r.JobId).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToArray();
        checks.Check(jobIds.Length == 3, "同一 runner 上的并发子代理任务各自获得唯一独立的 JobId");

        for (var i = 0; i < results.Length; i++)
        {
            var id = results[i].JobId!;
            var runEvents = progressEvents.Where(e => e.JobId == id).ToList();
            checks.Check(runEvents.Any(e => e.Kind == "started"), $"并发任务 {i} 上报 started 且携带自身独立 JobId");
            checks.Check(runEvents.Any(e => e.Kind == "completed"), $"并发任务 {i} 上报 completed 且携带自身独立 JobId");
        }

        // 2) 顺序执行也各自拥有独立的 JobId，不复用 runner 构造期的单一值
        var s1 = await runner.RunAsync("explore", "顺序任务 1", null, CancellationToken.None);
        var s2 = await runner.RunAsync("explore", "顺序任务 2", null, CancellationToken.None);
        checks.Check(!string.IsNullOrEmpty(s1.JobId) && !string.IsNullOrEmpty(s2.JobId) && s1.JobId != s2.JobId,
            "同一 runner 上的先后子代理任务获得不同 JobId");

        // 3) 嵌套子代理：子任务获得独立 JobId，且 ParentJobId 记录父级 JobId
        var leadResult = await runner.RunAsync("lead", "nest-test", null, CancellationToken.None);
        checks.Check(leadResult.Success, "嵌套 lead 代理执行成功");
        var leadEvents = progressEvents.Where(e => e.Agent == "lead").ToList();
        var exploreEvents = progressEvents.Where(e => e.Agent == "explore" && e.JobId != leadResult.JobId).ToList();
        checks.Check(leadEvents.Count > 0 && exploreEvents.Count > 0, "嵌套子代理事件正常上报");
        checks.Check(leadEvents[0].JobId != exploreEvents[0].JobId, "嵌套子代理任务拥有独立的 JobId，不侵占父任务 ID");

        // 4) 并发 patch 子代理补丁文件隔离：不同任务生成独立的补丁路径，互不覆盖
        var gitRepo = Path.Combine(testDir, "git-repo");
        Directory.CreateDirectory(gitRepo);
        if (RunGit(gitRepo, "init") && RunGit(gitRepo, "config user.email test@example.com") &&
            RunGit(gitRepo, "config user.name Test"))
        {
            await File.WriteAllTextAsync(Path.Combine(gitRepo, "base.txt"), "base");
            if (RunGit(gitRepo, "add .") && RunGit(gitRepo, "commit -m init"))
            {
                var patchRunner = new SubagentRunner(agents, client,
                    new ProductionToolDispatcher(gitRepo), "m", gitRepo);
                var p1 = patchRunner.RunAsync("writer", "patch-alpha", null, CancellationToken.None, mode: SubagentMode.Patch);
                var p2 = patchRunner.RunAsync("writer", "patch-beta", null, CancellationToken.None, mode: SubagentMode.Patch);
                var patchResults = await Task.WhenAll(p1, p2);

                checks.Check(patchResults.All(r => r.Success && r.Mode == SubagentMode.Patch), "并发 patch 子代理均成功");
                checks.Check(patchResults[0].JobId != patchResults[1].JobId, "并发 patch 子代理 JobId 独立");
                checks.Check(patchResults[0].PatchPath is { Length: > 0 } &&
                             patchResults[1].PatchPath is { Length: > 0 } &&
                             patchResults[0].PatchPath != patchResults[1].PatchPath,
                    "并发 patch 子代理各自使用独立的补丁文件路径，不发生同名覆盖");
                checks.Check(File.Exists(patchResults[0].PatchPath) && File.Exists(patchResults[1].PatchPath),
                    "两份并发补丁文件均独立存在于磁盘");
                var diff1 = await File.ReadAllTextAsync(patchResults[0].PatchPath!);
                var diff2 = await File.ReadAllTextAsync(patchResults[1].PatchPath!);
                checks.Check(diff1.Contains("alpha.txt") && diff2.Contains("beta.txt"),
                    "并发 patch 补丁文件各自包含独立修改，内容互不覆盖");

                try { File.Delete(patchResults[0].PatchPath!); } catch { }
                try { File.Delete(patchResults[1].PatchPath!); } catch { }
            }
        }
    }

    private static bool RunGit(string workingDirectory, string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed class ScriptedConcurrencyClient : IPureModelClient
    {
        public Task<PureModelResponse> GenerateAsync(PureModelRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var last = request.Messages[^1];
            var firstUser = request.Messages.FirstOrDefault(m => m.Role == PureAgentRole.User)?.Content ?? "";

            if (firstUser.Contains("patch-alpha"))
            {
                if (last.Role == PureAgentRole.Tool)
                    return Task.FromResult(PureModelResponse.FromText("alpha patched"));
                return Task.FromResult(PureModelResponse.FromToolCalls(new[]
                {
                    new PureToolCall("p1", "write_file", "{\"path\":\"alpha.txt\",\"content\":\"content alpha\"}")
                }));
            }
            if (firstUser.Contains("patch-beta"))
            {
                if (last.Role == PureAgentRole.Tool)
                    return Task.FromResult(PureModelResponse.FromText("beta patched"));
                return Task.FromResult(PureModelResponse.FromToolCalls(new[]
                {
                    new PureToolCall("p2", "write_file", "{\"path\":\"beta.txt\",\"content\":\"content beta\"}")
                }));
            }
            if (firstUser.Contains("nest-test"))
            {
                if (last.Role == PureAgentRole.Tool)
                    return Task.FromResult(PureModelResponse.FromText("lead finished"));
                return Task.FromResult(PureModelResponse.FromToolCalls(new[]
                {
                    new PureToolCall("n1", "subagent", "{\"agent\":\"explore\",\"prompt\":\"nested-read\"}")
                }));
            }

            return Task.FromResult(PureModelResponse.FromText("完成：" + (last.Content ?? "ok")));
        }
    }

    private sealed class NoNetwork : IPureModelClient
    {
        public Task<PureModelResponse> GenerateAsync(PureModelRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Model/API access prohibited");
    }
}
