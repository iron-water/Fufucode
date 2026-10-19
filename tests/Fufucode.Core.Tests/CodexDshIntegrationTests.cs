using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Backend;

internal static class CodexDshIntegrationTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private static async Task Wait(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Expected state not reached.");
            await Task.Delay(20);
        }
    }

    public static async Task RunAsync(string root)
    {
        var scratch = Path.Combine(root, "tmp", "codex-dsh-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var layout = new AppDataLayout(Path.Combine(scratch, "data"));

        // 1. BackendId 与枚举映射测试
        EnumAndCapabilitiesTests();

        // 2. 全局默认首选项不回退测试
        DefaultPreferenceNoFallbackTests(layout);

        // 3. 可用性与路径解析测试
        AvailabilityAndGuardTests(scratch, layout);

        // 4. DSH 端到端真实会话测试
        await DshEndToEndTestsAsync(scratch, layout);

        // 5. Codex 端到端真实会话测试
        await CodexEndToEndTestsAsync(scratch, layout);
    }

    private static void EnumAndCapabilitiesTests()
    {
        Check(BackendCapabilities.FromEngineId(EngineId.Codex) == BackendId.Codex,
            "BackendCapabilities.FromEngineId(Codex) 正常映射为 BackendId.Codex（不再返回 null）");
        Check(BackendCapabilities.FromEngineId(EngineId.Dsh) == BackendId.Dsh,
            "BackendCapabilities.FromEngineId(Dsh) 正常映射为 BackendId.Dsh（不再返回 null）");

        Check(BackendCapabilities.StorageId(BackendId.Codex) == "codex", "Codex 持久化 StorageId 为 codex");
        Check(BackendCapabilities.StorageId(BackendId.Dsh) == "dsh", "Dsh 持久化 StorageId 为 dsh");

        Check(BackendCapabilities.Parse("codex") == BackendId.Codex, "Parse(\"codex\") 返回 BackendId.Codex");
        Check(BackendCapabilities.Parse("dsh") == BackendId.Dsh, "Parse(\"dsh\") 返回 BackendId.Dsh");

        var codexCaps = BackendCapabilities.For(BackendId.Codex);
        Check(codexCaps.Name == "Codex" && codexCaps.PersistentHistory && codexCaps.ApprovalBridge && !codexCaps.ProjectFork,
            "Codex 能力声明正确");

        var dshCaps = BackendCapabilities.For(BackendId.Dsh);
        Check(dshCaps.Name == "DSH" && dshCaps.PersistentHistory && dshCaps.ApprovalBridge && !dshCaps.ProjectFork,
            "DSH 能力声明正确");
    }

    private static void DefaultPreferenceNoFallbackTests(AppDataLayout layout)
    {
        var store = new BackendPreferenceStore(layout);

        // 设置 Codex 为全局默认后端
        store.SetDefault(EngineId.Codex);
        var managerCodex = new PersistentSessionManager("", "", layout);
        Check(managerCodex.PreferredBackend == BackendId.Codex,
            "设置页选择 Codex 为默认后端后，PreferredBackend 为 Codex（不再回退 FUMO）");

        // 设置 DSH 为全局默认后端
        store.SetDefault(EngineId.Dsh);
        var managerDsh = new PersistentSessionManager("", "", layout);
        Check(managerDsh.PreferredBackend == BackendId.Dsh,
            "设置页选择 DSH 为默认后端后，PreferredBackend 为 DSH（不再回退 FUMO）");
    }

    private static void AvailabilityAndGuardTests(string scratch, AppDataLayout layout)
    {
        // 自动探测系统安装的可执行文件
        var codexPath = PersistentSessionManager.ResolveCodexExecutable();
        var dshPath = PersistentSessionManager.ResolveDshExecutable();

        Check(codexPath is not null && File.Exists(codexPath), "系统成功探测到 Codex CLI: " + codexPath);
        Check(dshPath is not null && File.Exists(dshPath), "系统成功探测到 DSH CLI: " + dshPath);

        var manager = new PersistentSessionManager("", "", layout);
        Check(manager.IsBackendAvailable(BackendId.Codex), "IsBackendAvailable(Codex) 在本机返回 true");
        Check(manager.IsBackendAvailable(BackendId.Dsh), "IsBackendAvailable(Dsh) 在本机返回 true");

        // 路径缺失守卫测试
        var missingManager = new PersistentSessionManager("", "", layout,
            codexExecutablePath: Path.Combine(scratch, "missing-codex.exe"),
            dshExecutablePath: Path.Combine(scratch, "missing-dsh.cmd"));

        Check(!missingManager.IsBackendAvailable(BackendId.Codex), "路径不存在时 IsBackendAvailable(Codex) 为 false");
        Check(!missingManager.IsBackendAvailable(BackendId.Dsh), "路径不存在时 IsBackendAvailable(Dsh) 为 false");

        try
        {
            missingManager.CreateAsync(scratch, backend: BackendId.Codex).GetAwaiter().GetResult();
            throw new Exception("应该抛出 FileNotFoundException");
        }
        catch (FileNotFoundException ex)
        {
            Check(ex.Message.Contains("Codex runtime missing"), "缺失 Codex 时 CreateAsync 抛出清晰引导的 FileNotFoundException");
        }

        try
        {
            missingManager.CreateAsync(scratch, backend: BackendId.Dsh).GetAwaiter().GetResult();
            throw new Exception("应该抛出 FileNotFoundException");
        }
        catch (FileNotFoundException ex)
        {
            Check(ex.Message.Contains("DSH runtime missing"), "缺失 DSH 时 CreateAsync 抛出清晰引导的 FileNotFoundException");
        }
    }

    private static async Task DshEndToEndTestsAsync(string scratch, AppDataLayout layout)
    {
        var manager = new PersistentSessionManager("", "", layout);
        if (!manager.IsBackendAvailable(BackendId.Dsh))
        {
            Console.WriteLine("SKIP DshEndToEndTests: dsh CLI 未安装");
            return;
        }

        string sessionId;
        string? backendSessionId;

        await using (var session = await manager.CreateAsync(scratch, "DSH Test", backend: BackendId.Dsh))
        {
            sessionId = session.Session.Id;
            Check(session.Session.Backend == BackendId.Dsh, "DSH 会话创建成功，后端标识为 Dsh");
            Check(session.Coordinator.ApprovalAvailability == ApprovalAvailability.Verified, "DSH 协调器审批状态为 Verified");

            var turnSettled = false;
            session.Chat.Event += (_, ev) =>
            {
                if (ev is SessionEvent.TurnSettled)
                    turnSettled = true;
            };

            await session.Coordinator.SendPromptAsync("say hello");
            try
            {
                await Wait(() => turnSettled);
            }
            catch (TimeoutException)
            {
                throw new Exception($"DSH 首轮等待超时。LastResult={session.Coordinator.Snapshot.LastResult}, LastError={session.Coordinator.Snapshot.LastError}, ChatOutcome={session.Chat.LastOutcome}");
            }

            Check(session.Coordinator.Snapshot.LastResult == TaskResult.Completed,
                $"DSH 会话首轮对话成功完成 (Result={session.Coordinator.Snapshot.LastResult}, Error={session.Coordinator.Snapshot.LastError})");
            backendSessionId = session.Session.BackendSessionId;
            Check(!string.IsNullOrEmpty(backendSessionId), "DSH 会话成功获取并绑定 backendSessionId");

            var historyFile = Path.Combine(layout.ProjectSessionsDirectory(session.Project.Id), "dsh", sessionId + ".json");
            Check(File.Exists(historyFile), "DSH 会话历史文件落地成功: " + historyFile);
        }

        // 测试恢复
        await using (var resumed = await manager.ResumeAsync(sessionId))
        {
            Check(resumed.Session.Backend == BackendId.Dsh, "恢复的 DSH 会话后端为 Dsh");
            Check(resumed.Session.BackendSessionId == backendSessionId, "恢复的 DSH 会话后端会话标识一致");

            var turnSettled = false;
            resumed.Chat.Event += (_, ev) =>
            {
                if (ev is SessionEvent.TurnSettled)
                    turnSettled = true;
            };

            await resumed.Coordinator.SendPromptAsync("say hello again");
            try
            {
                await Wait(() => turnSettled);
            }
            catch (TimeoutException)
            {
                throw new Exception($"DSH 第二轮等待超时。LastResult={resumed.Coordinator.Snapshot.LastResult}, LastError={resumed.Coordinator.Snapshot.LastError}, ChatOutcome={resumed.Chat.LastOutcome}");
            }

            Check(resumed.Coordinator.Snapshot.LastResult == TaskResult.Completed,
                $"DSH 恢复会话成功完成第二轮交互 (Result={resumed.Coordinator.Snapshot.LastResult}, Error={resumed.Coordinator.Snapshot.LastError})");
        }
    }

    private static async Task CodexEndToEndTestsAsync(string scratch, AppDataLayout layout)
    {
        var manager = new PersistentSessionManager("", "", layout);
        if (!manager.IsBackendAvailable(BackendId.Codex))
        {
            Console.WriteLine("SKIP CodexEndToEndTests: codex CLI 未安装");
            return;
        }

        string sessionId;
        string? backendSessionId;

        await using (var session = await manager.CreateAsync(scratch, "Codex Test", backend: BackendId.Codex))
        {
            sessionId = session.Session.Id;
            Check(session.Session.Backend == BackendId.Codex, "Codex 会话创建成功，后端标识为 Codex");
            Check(session.Coordinator.ApprovalAvailability == ApprovalAvailability.Verified, "Codex 协调器审批状态为 Verified");

            var turnSettled = false;
            session.Chat.Event += (_, ev) =>
            {
                if (ev is SessionEvent.TurnSettled)
                    turnSettled = true;
            };

            await session.Coordinator.SendPromptAsync("reply with: PONG");
            await Wait(() => session.Coordinator.Snapshot.LastResult != TaskResult.Unknown && turnSettled);

            Check(session.Coordinator.Snapshot.LastResult != TaskResult.Unknown, "Codex 会话首轮对话成功处理并结算");
            backendSessionId = session.Session.BackendSessionId;
            Check(!string.IsNullOrEmpty(backendSessionId), "Codex 会话成功获取并绑定 thread_id 作为 backendSessionId");

            var historyFile = Path.Combine(layout.ProjectSessionsDirectory(session.Project.Id), "codex", sessionId + ".json");
            Check(File.Exists(historyFile), "Codex 会话历史文件落地成功: " + historyFile);
        }

        // 测试恢复
        await using (var resumed = await manager.ResumeAsync(sessionId))
        {
            Check(resumed.Session.Backend == BackendId.Codex, "恢复的 Codex 会话后端为 Codex");
            Check(resumed.Session.BackendSessionId == backendSessionId, "恢复的 Codex 会话后端会话标识一致");

            var turnSettled = false;
            resumed.Chat.Event += (_, ev) =>
            {
                if (ev is SessionEvent.TurnSettled)
                    turnSettled = true;
            };

            await resumed.Coordinator.SendPromptAsync("reply with: PING");
            await Wait(() => resumed.Coordinator.Snapshot.LastResult != TaskResult.Unknown && turnSettled);

            Check(resumed.Coordinator.Snapshot.LastResult != TaskResult.Unknown, "Codex 恢复会话成功完成第二轮交互并结算");
        }
    }
}
