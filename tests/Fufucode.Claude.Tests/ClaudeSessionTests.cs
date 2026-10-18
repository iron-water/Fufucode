using Fufucode.App.Abstractions;
using Fufucode.Claude;

namespace Fufucode.Claude.Tests;

/// <summary>
/// 通过与假 CLI 子进程的真实 stdio 往返，验证 init 握手、打字机文本流、
/// 工具调用往返（批准/拒绝）、中断与 session_id 恢复。
/// </summary>
internal static class ClaudeSessionTests
{
    public static async Task RunAsync(Checks checks, string root)
    {
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var fake = Path.Combine(root, "tests/Fufucode.FakeClaude/bin/Debug/net10.0/Fufucode.FakeClaude.dll");
        checks.Check(File.Exists(fake), "假 CLI 夹具已构建");
        var scratch = Path.Combine(Path.GetTempPath(), "fufucode-claude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            await HandshakeAndTextAsync(checks, dotnet, fake, scratch);
            await ToolApprovalAsync(checks, dotnet, fake, scratch);
            await ToolDenialAsync(checks, dotnet, fake, scratch);
            await ResumeAsync(checks, dotnet, fake, scratch);
            await ProcessExitAsync(checks, dotnet, fake, scratch);
        }
        finally { try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } }
    }

    private static ClaudeStreamOptions Options(string dotnet, string fake, string workingDirectory,
        string? resumeId = null, string mode = "text") => new(dotnet,
        new[] { fake, "--fake-mode", mode }.Concat(resumeId is null ? Array.Empty<string>() : ["--resume", resumeId]).ToArray(),
        workingDirectory, StartupTimeout: TimeSpan.FromSeconds(30), ShutdownTimeout: TimeSpan.FromSeconds(3));

    private static async Task<List<SessionEvent>> CollectAsync(ClaudeChatSessionAdapter session, Func<Task> send)
    {
        var events = new List<SessionEvent>();
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Event += (_, ev) =>
        {
            lock (events)
            {
                events.Add(ev);
                if (ev is SessionEvent.TurnSettled) settled.TrySetResult();
            }
        };
        await send();
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return events;
    }

    private static async Task HandshakeAndTextAsync(Checks checks, string dotnet, string fake, string scratch)
    {
        await using var session = await ClaudeChatSessionAdapter.ConnectAsync(Options(dotnet, fake, scratch));
        checks.Equal(SessionConnection.Connected, session.Connection, "init 握手后连接为 Connected");
        checks.Equal("claude", session.BackendId, "后端标识为 claude");
        checks.Equal(SessionActivity.Idle, session.Activity, "握手后空闲");
        checks.Check(session.SessionId is null, "实测：init 帧在首条用户消息前不出现，不提前伪造 session_id");

        var events = await CollectAsync(session, () => session.SendAsync("ping"));
        checks.Check(session.SessionId is { Length: > 0 }, "首轮后 init 帧暴露 session_id");
        checks.Equal("fake/claude-model[1m]", session.CurrentModel, "含 [1m] 后缀的模型按通用字符串显示");
        var started = events.OfType<SessionEvent.MessageStarted>().ToArray();
        var finished = events.OfType<SessionEvent.MessageFinished>().ToArray();
        checks.Equal(1, started.Length, "一轮产生一条助手消息开始事件");
        checks.Equal(MessageRole.Assistant, started[0].Role, "助手消息角色正确");
        checks.Equal(1, finished.Length, "一轮产生一条助手消息结束事件");
        checks.Equal("echo:ping", ((TextBlock)finished[0].FinalMessage.Blocks[0]).Text, "文本输出映射为消息文本块");
        checks.Check(events.OfType<SessionEvent.TurnSettled>().Any(e => e.Outcome == SessionOutcome.Completed),
            "result 帧映射为 Completed 终态");
        checks.Check(events.OfType<SessionEvent.ActivityChanged>().Any(e => e.Activity == SessionActivity.Running),
            "回合开始暴露 Running 活动态");
        checks.Check(events.OfType<SessionEvent.ActivityChanged>().Any(e => e.Activity == SessionActivity.Idle),
            "回合结束回到 Idle 活动态");
        checks.Equal(SessionActivity.Idle, session.Activity, "会话最终空闲");
    }

    private static async Task ToolApprovalAsync(Checks checks, string dotnet, string fake, string scratch)
    {
        await using var session = await ClaudeChatSessionAdapter.ConnectAsync(Options(dotnet, fake, scratch, mode: "tool"));
        var events = new List<SessionEvent>();
        var permission = new TaskCompletionSource<ClaudePermissionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Event += (_, ev) =>
        {
            lock (events)
            {
                events.Add(ev);
                if (ev is SessionEvent.TurnSettled) settled.TrySetResult();
            }
        };
        session.PermissionRequested += request => permission.TrySetResult(request);
        await session.SendAsync("write the file");
        var request = await permission.Task.WaitAsync(TimeSpan.FromSeconds(30));
        checks.Equal("Write", request.ToolName, "工具调用映射为统一工具名");
        checks.Check(session.PendingPermissionRequests.Count == 1, "等待审批的请求被记录");

        var toolFinished = events.OfType<SessionEvent.BlockFinished>()
            .Select(e => e.FinalBlock).OfType<ToolCallBlock>().First();
        checks.Equal("call_fake_tool", toolFinished.ToolCallId, "工具调用块保留 tool_use_id");
        checks.Equal("fake-out.txt", System.Text.Json.JsonDocument.Parse(toolFinished.InputJson)
            .RootElement.GetProperty("file_path").GetString(), "工具调用块保留原始参数");

        await session.RespondToPermissionAsync(request, ClaudePermissionDecision.Allow);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        checks.Equal(0, session.PendingPermissionRequests.Count, "审批应答后请求出队");
        var finalTool = events.OfType<SessionEvent.BlockFinished>()
            .Select(e => e.FinalBlock).OfType<ToolCallBlock>().Last();
        checks.Equal(ToolCallStatus.Completed, finalTool.Status, "批准路径工具状态为 Completed");
        checks.Check(!finalTool.IsErrorOutput, "批准路径不标记错误输出");
        checks.Equal("accept (user)", finalTool.StatusText, "批准路径保留 permission_decision");
        checks.Check(events.OfType<SessionEvent.ToolCallStatusChanged>()
            .Any(e => e.Status == ToolCallStatus.Completed), "批准路径发出工具状态变更事件");

        // 负例：不能因为收到审批就凭空产生成功结果；必须有真实 tool_result 才置为完成。
        checks.Check(events.OfType<SessionEvent.MessageFinished>()
            .Any(e => e.FinalMessage.Blocks.OfType<TextBlock>().Any(t => t.Text == "OK")),
            "批准后回灌结果触发后续助手文本");
    }

    private static async Task ToolDenialAsync(Checks checks, string dotnet, string fake, string scratch)
    {
        await using var session = await ClaudeChatSessionAdapter.ConnectAsync(Options(dotnet, fake, scratch, mode: "tool"));
        var events = new List<SessionEvent>();
        var permission = new TaskCompletionSource<ClaudePermissionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Event += (_, ev) =>
        {
            lock (events)
            {
                events.Add(ev);
                if (ev is SessionEvent.TurnSettled) settled.TrySetResult();
            }
        };
        session.PermissionRequested += request => permission.TrySetResult(request);
        await session.SendAsync("write the file");
        var request = await permission.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await session.RespondToPermissionAsync(request, ClaudePermissionDecision.Deny, "Denied by Fufucode");
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var finalTool = events.OfType<SessionEvent.BlockFinished>()
            .Select(e => e.FinalBlock).OfType<ToolCallBlock>().Last();
        checks.Equal(ToolCallStatus.Failed, finalTool.Status, "拒绝路径工具状态为 Failed");
        checks.Check(finalTool.IsErrorOutput, "拒绝路径标记错误输出，不静默吞掉");
        checks.Equal("Denied by Fufucode", finalTool.Output, "拒绝理由进入工具输出");
        checks.Equal("reject (host)", finalTool.StatusText, "拒绝路径保留 reject 决策");
        checks.Check(events.OfType<SessionEvent.ToolCallStatusChanged>()
            .Any(e => e.Status == ToolCallStatus.Failed), "拒绝路径发出失败状态事件");
        checks.Check(!events.OfType<SessionEvent.MessageFinished>()
            .Any(e => e.FinalMessage.Blocks.OfType<TextBlock>().Any(t => t.Text == "OK")),
            "拒绝路径不产生批准路径的后续文本");
    }

    private static async Task ResumeAsync(Checks checks, string dotnet, string fake, string scratch)
    {
        string firstId;
        await using (var first = await ClaudeChatSessionAdapter.ConnectAsync(Options(dotnet, fake, scratch)))
        {
            await CollectAsync(first, () => first.SendAsync("first"));
            firstId = first.SessionId!;
            checks.Check(firstId is { Length: > 0 }, "首轮会话产生 session_id");
        }
        await using var resumed = await ClaudeChatSessionAdapter.ConnectAsync(
            Options(dotnet, fake, scratch, resumeId: firstId));
        var events = await CollectAsync(resumed, () => resumed.SendAsync("second"));
        checks.Equal(firstId, resumed.SessionId, "--resume 恢复同一 session_id");
        checks.Equal("echo:second", ((TextBlock)events.OfType<SessionEvent.MessageFinished>()
            .First().FinalMessage.Blocks[0]).Text, "恢复会话可继续对话");
    }

    private static async Task ProcessExitAsync(Checks checks, string dotnet, string fake, string scratch)
    {
        var client = ClaudeStreamClient.Start(new ClaudeStreamOptions(dotnet,
            new[] { fake, "--fake-mode", "text" }, scratch, ShutdownTimeout: TimeSpan.FromSeconds(2)));
        await client.SendInitializeAsync();
        await client.StopAsync();
        var exited = await WaitForAsync(() => client.ProcessExit is not null, TimeSpan.FromSeconds(10));
        checks.Check(exited, "进程退出被观测并记录");
        checks.Equal(0, client.ProcessExit!.ExitCode, "正常收尾退出码为 0");
        await client.DisposeAsync();
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }
}
