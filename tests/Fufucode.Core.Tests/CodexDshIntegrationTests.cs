using System.Text.Json;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Backend;

internal static class CodexDshIntegrationTests
{
    public static async Task RunAsync(string root)
    {
        BackendPluginLoader.RegisterSessionFactory(new Fufucode.Plugins.Codex.CodexSessionFactory());
        BackendPluginLoader.RegisterSessionFactory(new Fufucode.Plugins.Dsh.DshSessionFactory());
        var scratch = Environment.GetEnvironmentVariable("FUFUCODE_TEST_ROOT") ?? Path.Combine(root, "tmp");
        foreach (var backend in new[] { BackendId.Codex, BackendId.Dsh })
            await VerifyBackendAsync(Path.Combine(scratch, "cli-protocol-" + Guid.NewGuid().ToString("N")), backend);
    }

    private static async Task VerifyBackendAsync(string root, BackendId backend)
    {
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "fake.cmd");
            var arguments = Path.Combine(root, "arguments.txt");
            var id = backend.ToString().ToLowerInvariant() + "-fixture";
            string[] events = backend == BackendId.Codex
                ? ["""{"type":"thread.started","thread_id":"codex-fixture"}""",
                   """{"type":"turn.started"}""",
                   """{"type":"item.completed","item":{"type":"agent_message","text":"answer"}}""",
                   """{"type":"turn.completed"}"""]
                : ["""{"type":"session","sessionId":"dsh-fixture"}""",
                   """{"type":"status","phase":"turn_start"}""",
                   """{"type":"text","text":"answer"}""",
                   """{"type":"final","text":"answer"}""",
                   """{"type":"status","phase":"turn_end"}"""];
            await File.WriteAllLinesAsync(executable,
                ["@echo off", $"echo %* > \"{arguments}\"", "echo npm fixture noise",
                 .. events.Select(value => "echo " + value), "exit /b 0"]);

            var manager = new PersistentSessionManager("", "", new AppDataLayout(Path.Combine(root, "data")),
                codexExecutablePath: executable, dshExecutablePath: executable);
            string sessionId;
            await using (var session = await manager.CreateAsync(root, backend: backend))
            {
                sessionId = session.Session.Id;
                Check(session.Coordinator.ApprovalAvailability == ApprovalAvailability.Unavailable,
                    backend + " 如实报告审批桥未接入");
                var state = (await session.Coordinator.QueryStateAsync()).Data!.Value;
                Check(state.GetProperty("model").GetProperty("provider").GetString() ==
                      (backend == BackendId.Codex ? "openai" : "deepseek"),
                    backend + " 状态查询保留后端模型归属");
                var models = (await session.Coordinator.QueryAvailableModelsAsync()).Data!.Value.GetProperty("models");
                Check(models.ValueKind == JsonValueKind.Array && models.GetArrayLength() > 0 &&
                      models.EnumerateArray().All(model => model.GetProperty("id").ValueKind == JsonValueKind.String &&
                                                          model.GetProperty("name").ValueKind == JsonValueKind.String),
                    backend + " 模型目录满足调用端的 JSON 契约");
                using (session.Coordinator.BeginIdleMaintenance())
                {
                    Check(!session.Coordinator.CanSendPrompt, backend + " 维护期间不可发送");
                    try
                    {
                        await session.Coordinator.SendPromptAsync("hello");
                        throw new Exception("维护期间接受了请求");
                    }
                    catch (InvalidOperationException) { }
                }
                var message = await SendAsync(session);
                Check(message.Blocks.Single() is TextBlock { Text: "answer" }, backend + " JSONL 输出规范化且 final 不重复");
                Check(session.Session.BackendSessionId == id &&
                      session.Coordinator.Snapshot.LastResult == TaskResult.Completed,
                    backend + " 会话身份和完成状态持久化");
                var firstArguments = await File.ReadAllTextAsync(arguments);
                Check(backend == BackendId.Codex ? firstArguments.Contains("\"exec\"") &&
                      !firstArguments.Contains("\"resume\"") : firstArguments.Contains("\"headless\"") &&
                      !firstArguments.Contains("\"--session-id\""), backend + " 首轮参数正确");
            }

            await using (var restored = await manager.ResumeAsync(sessionId))
            {
                Check(restored.History.CurrentBranchMessages.Count == 2, backend + " 重开后恢复用户和助手历史");
                await SendAsync(restored);
                var resumedArguments = await File.ReadAllTextAsync(arguments);
                Check(resumedArguments.Contains(id) && resumedArguments.Contains(
                    backend == BackendId.Codex ? "\"resume\"" : "\"--session-id\""),
                    backend + " 次轮携带原后端会话 ID");
            }

            await File.WriteAllLinesAsync(executable, ["@echo off", "echo fixture failure 1>&2", "exit /b 17"]);
            await using var failed = await manager.CreateAsync(root, backend: backend);
            var terminal = new TaskCompletionSource<SessionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            failed.Chat.Event += (_, value) =>
            {
                if (value is SessionEvent.TurnSettled settled) terminal.TrySetResult(settled.Outcome);
            };
            await failed.Coordinator.SendPromptAsync("hello");
            Check(await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10)) == SessionOutcome.Failed &&
                  failed.Coordinator.Snapshot.LastError?.Contains("fixture failure") == true,
                backend + " 非零退出保留 stderr 和失败状态");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<ChatMessage> SendAsync(PersistentSession session)
    {
        var finished = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEvent(object? sender, SessionEvent value)
        {
            if (value is SessionEvent.MessageFinished message) finished.TrySetResult(message.FinalMessage);
        }
        session.Chat.Event += OnEvent;
        try
        {
            await session.Coordinator.SendPromptAsync("hello");
            var message = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // MessageFinished 先于历史写入；等当前输出泵排空后再释放进程。
            var until = DateTime.UtcNow.AddSeconds(10);
            while (session.Chat.CanStop && DateTime.UtcNow < until) await Task.Delay(10);
            return message;
        }
        finally
        {
            session.Chat.Event -= OnEvent;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
}
