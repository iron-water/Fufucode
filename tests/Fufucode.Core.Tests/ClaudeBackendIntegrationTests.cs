using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Backend;

internal static class ClaudeBackendIntegrationTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private static async Task Wait(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Claude backend expected state not reached.");
            await Task.Delay(20);
        }
    }

    public static async Task RunAsync(string root)
    {
        // 后端适配已迁出 Core；等价于插件市场安装/启用 backend.claude。
        BackendPluginLoader.RegisterSessionFactory(new Fufucode.Plugins.Claude.ClaudeSessionFactory());
        var scratch = Path.Combine(root, "tmp", "claude-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var layout = new AppDataLayout(Path.Combine(scratch, "data"));

        var fakeClaude = Path.Combine(root, "tests/Fufucode.FakeClaude/bin/Debug/net10.0/Fufucode.FakeClaude.dll");
        Check(File.Exists(fakeClaude), "FakeClaude 夹具存在");

        // 1. 可用性与守卫测试
        AvailabilityAndGuard(scratch, layout, fakeClaude);

        // 2. 真实会话创建与文本往返
        await SessionCreateAndTextAsync(scratch, layout, fakeClaude);

        // 3. 工具统一审批桥测试（批准与拒绝）
        await ToolApprovalAsync(scratch, layout, fakeClaude);
        await ToolDenialAsync(scratch, layout, fakeClaude);

        // 4. 会话恢复测试（--resume）
        await SessionResumeAsync(scratch, layout, fakeClaude);
    }

    private static void AvailabilityAndGuard(string scratch, AppDataLayout layout, string fakeClaude)
    {
        // 缺少可执行文件时：IsBackendAvailable 为 false，CreateAsync 抛 FileNotFoundException 而非 NotSupportedException
        var missingManager = new PersistentSessionManager(
            "", "", layout,
            claudeExecutablePath: Path.Combine(scratch, "non-existent-claude.exe"));

        Check(!missingManager.IsBackendAvailable(BackendId.Claude), "路径不存在时 IsBackendAvailable(Claude) 为 false");

        try
        {
            missingManager.CreateAsync(scratch, backend: BackendId.Claude).GetAwaiter().GetResult();
            throw new Exception("应该抛出 FileNotFoundException");
        }
        catch (FileNotFoundException ex)
        {
            Check(ex.Message.Contains("Claude runtime missing"), "路径不存在时 CreateAsync 抛出 FileNotFoundException 指引安装");
        }
        catch (NotSupportedException)
        {
            throw new Exception("不应抛出 NotSupportedException（接线已完成）");
        }

        // 配置有效路径时：IsBackendAvailable 为 true
        var availableManager = new PersistentSessionManager(
            "", "", layout,
            claudeExecutablePath: fakeClaude);
        Check(availableManager.IsBackendAvailable(BackendId.Claude), "配置有效路径时 IsBackendAvailable(Claude) 为 true");
    }

    private static async Task SessionCreateAndTextAsync(string scratch, AppDataLayout layout, string fakeClaude)
    {
        var manager = new PersistentSessionManager(
            "", "", layout,
            claudeExecutablePath: fakeClaude);

        string sessionId;
        string? backendSessionId;

        await using (var session = await manager.CreateAsync(scratch, "Claude Chat", backend: BackendId.Claude))
        {
            sessionId = session.Session.Id;
            Check(session.Session.Backend == BackendId.Claude, "创建的会话后端为 Claude");
            Check(session.Coordinator.ApprovalAvailability == ApprovalAvailability.Verified, "Claude 审批桥为 Verified 状态");
            Check(session.Coordinator.Capabilities.PersistentHistory, "Claude 声明支持持久历史");
            Check(session.Coordinator.Capabilities.ApprovalBridge, "Claude 声明支持统一审批桥");
            Check(!session.Coordinator.Capabilities.ProjectFork, "Claude 声明不支持会话分叉");

            // 监听 SessionEvent 与扩展 UI 审批
            var turnSettled = false;
            session.Chat.Event += (_, ev) =>
            {
                if (ev is SessionEvent.TurnSettled settled && settled.Outcome == SessionOutcome.Completed)
                    turnSettled = true;
            };

            // 发送消息
            await session.Coordinator.SendPromptAsync("Hello Claude");
            await Wait(() => session.Coordinator.Snapshot.LastResult == TaskResult.Completed && turnSettled);

            Check(session.Coordinator.Snapshot.LastResult == TaskResult.Completed, "首轮对话顺利结算并完成");
            backendSessionId = session.Session.BackendSessionId;
            Check(!string.IsNullOrEmpty(backendSessionId), "init 帧到达后成功绑定 BackendSessionId");

            // 检查历史持久化文件已创建
            var historyFile = Path.Combine(layout.ProjectSessionsDirectory(session.Project.Id), "claude", sessionId + ".json");
            Check(File.Exists(historyFile), "Claude 会话消息历史 JSON 已落地");
        }
    }

    private static async Task SessionResumeAsync(string scratch, AppDataLayout layout, string fakeClaude)
    {
        var manager = new PersistentSessionManager(
            "", "", layout,
            claudeExecutablePath: fakeClaude);

        var project = manager.Metadata.GetOrCreateProject(scratch);
        var sessions = manager.Metadata.ListSessions(project.Id).Where(s => s.Backend == BackendId.Claude).ToArray();
        Check(sessions.Length > 0, "元数据中查到已持久化的 Claude 会话");

        var target = sessions[0];
        Check(!string.IsNullOrEmpty(target.BackendSessionId), "已保存的会话包含后端会话标识供 --resume 续接");

        await using (var resumed = await manager.ResumeAsync(target.Id))
        {
            Check(resumed.Session.Backend == BackendId.Claude, "恢复的会话后端仍为 Claude");
            Check(resumed.Session.Id == target.Id, "恢复的会话 ID 一致");
            Check(resumed.Session.BackendSessionId == target.BackendSessionId, "恢复的后端会话 ID 一致");

            var turnSettled = false;
            resumed.Chat.Event += (_, ev) =>
            {
                if (ev is SessionEvent.TurnSettled settled && settled.Outcome == SessionOutcome.Completed)
                    turnSettled = true;
            };

            await resumed.Coordinator.SendPromptAsync("Continue conversation");
            await Wait(() => resumed.Coordinator.Snapshot.LastResult == TaskResult.Completed && turnSettled);

            Check(resumed.Coordinator.Snapshot.LastResult == TaskResult.Completed, "恢复后的会话可继续发送并正常完成");
        }
    }

    private static async Task ToolApprovalAsync(string scratch, AppDataLayout layout, string fakeClaude)
    {
        Environment.SetEnvironmentVariable("FAKE_CLAUDE_MODE", "tool");
        try
        {
            var manager = new PersistentSessionManager("", "", layout, claudeExecutablePath: fakeClaude);
            await using var session = await manager.CreateAsync(scratch, "Tool Allow", backend: BackendId.Claude);

            ExtensionUiRequest? receivedReq = null;
            session.Coordinator.ExtensionUiRequestReceived += req =>
            {
                receivedReq = req;
                _ = session.Coordinator.RespondToExtensionUiAsync(req, PiExtensionUiResponse.Confirm(true));
            };

            var turnSettled = false;
            session.Chat.Event += (_, ev) =>
            {
                if (ev is SessionEvent.TurnSettled settled && settled.Outcome == SessionOutcome.Completed)
                    turnSettled = true;
            };

            await session.Coordinator.SendPromptAsync("create a file");
            await Wait(() => session.Coordinator.Snapshot.LastResult == TaskResult.Completed && turnSettled);

            Check(receivedReq is not null, "工具调用触发 ExtensionUiRequestReceived 统一审批事件");
            Check(session.Coordinator.Snapshot.LastResult == TaskResult.Completed, "批准后工具成功执行并完成回合");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_CLAUDE_MODE", null);
        }
    }

    private static async Task ToolDenialAsync(string scratch, AppDataLayout layout, string fakeClaude)
    {
        Environment.SetEnvironmentVariable("FAKE_CLAUDE_MODE", "tool");
        try
        {
            var manager = new PersistentSessionManager("", "", layout, claudeExecutablePath: fakeClaude);
            await using var session = await manager.CreateAsync(scratch, "Tool Deny", backend: BackendId.Claude);

            ExtensionUiRequest? receivedReq = null;
            session.Coordinator.ExtensionUiRequestReceived += req =>
            {
                receivedReq = req;
                _ = session.Coordinator.RespondToExtensionUiAsync(req, PiExtensionUiResponse.Confirm(false));
            };

            var turnSettled = false;
            session.Chat.Event += (_, ev) =>
            {
                if (ev is SessionEvent.TurnSettled)
                    turnSettled = true;
            };

            await session.Coordinator.SendPromptAsync("create a file");
            await Wait(() => turnSettled);

            Check(receivedReq is not null, "拒绝路径触发统一审批事件");
            Check(session.Coordinator.Snapshot.LastResult == TaskResult.Completed, "拒绝路径正常结算不抛出未处理异常");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_CLAUDE_MODE", null);
        }
    }
}
