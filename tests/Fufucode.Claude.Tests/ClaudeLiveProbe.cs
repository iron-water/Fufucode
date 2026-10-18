using Fufucode.App.Abstractions;
using Fufucode.Claude;

namespace Fufucode.Claude.Tests;

/// <summary>
/// 可选的真实 CLI 冒烟（不进默认套件）：直接驱动本机安装的官方 claude.exe，
/// 用最低成本模型验证 init 握手、单轮文本往返与真实进程退出。
/// </summary>
internal static class ClaudeLiveProbe
{
    /// <summary>
    /// 真实工具审批往返：让 CLI 请求 Write 权限，宿主批准后确认工具结果回灌且回合正常收束。
    /// </summary>
    public static async Task<int> RunToolAsync(string executable, string model)
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "fufucode-claude-live-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        var options = new ClaudeStreamOptions(executable, ClaudeProtocol.BuildArguments(model),
            workingDirectory, StartupTimeout: TimeSpan.FromSeconds(60), ShutdownTimeout: TimeSpan.FromSeconds(10));
        await using var session = await ClaudeChatSessionAdapter.ConnectAsync(options);
        var statuses = new List<ToolCallBlock>();
        var permissionSeen = new TaskCompletionSource<ClaudePermissionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource<SessionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Event += (_, ev) =>
        {
            switch (ev)
            {
                case SessionEvent.BlockFinished finished when finished.FinalBlock is ToolCallBlock tool:
                    statuses.Add(tool);
                    break;
                case SessionEvent.TurnSettled done: settled.TrySetResult(done.Outcome); break;
                case SessionEvent.SessionError error: Console.WriteLine($"error: {error.Kind} {error.Message}"); break;
            }
        };
        session.PermissionRequested += request => permissionSeen.TrySetResult(request);
        await session.SendAsync("Create a file named live-tool-out.txt in the current directory containing the text OK, using the Write tool.");
        var request = await permissionSeen.Task.WaitAsync(TimeSpan.FromMinutes(3));
        Console.WriteLine($"permission: tool={request.ToolName} input={request.InputJson}");
        await session.RespondToPermissionAsync(request, ClaudePermissionDecision.Allow);
        var outcome = await settled.Task.WaitAsync(TimeSpan.FromMinutes(4));
        var final = statuses.LastOrDefault();
        Console.WriteLine($"outcome: {outcome}");
        Console.WriteLine($"tool status: {final?.Status} error={final?.IsErrorOutput} permission={final?.StatusText}");
        Console.WriteLine($"output: {final?.Output}");
        Console.WriteLine($"file created: {File.Exists(Path.Combine(workingDirectory, "live-tool-out.txt"))}");
        var ok = outcome == SessionOutcome.Completed && final is { Status: ToolCallStatus.Completed, IsErrorOutput: false };
        try { Directory.Delete(workingDirectory, recursive: true); } catch (IOException) { }
        return ok ? 0 : 1;
    }

    public static async Task<int> RunAsync(string executable, string model, string prompt)
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "fufucode-claude-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        Console.WriteLine($"executable: {executable}");
        Console.WriteLine($"model: {model}");
        var options = new ClaudeStreamOptions(executable, ClaudeProtocol.BuildArguments(model),
            workingDirectory, StartupTimeout: TimeSpan.FromSeconds(60), ShutdownTimeout: TimeSpan.FromSeconds(10));
        await using var session = await ClaudeChatSessionAdapter.ConnectAsync(options);
        Console.WriteLine($"handshake: {session.Connection}");
        var texts = new List<string>();
        var settled = new TaskCompletionSource<SessionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Event += (_, ev) =>
        {
            switch (ev)
            {
                case SessionEvent.MessageFinished finished:
                    texts.AddRange(finished.FinalMessage.Blocks.OfType<TextBlock>().Select(t => t.Text));
                    break;
                case SessionEvent.TurnSettled done:
                    settled.TrySetResult(done.Outcome);
                    break;
                case SessionEvent.SessionError error:
                    Console.WriteLine($"error: {error.Kind} {error.Message}");
                    break;
            }
        };
        await session.SendAsync(prompt);
        var outcome = await settled.Task.WaitAsync(TimeSpan.FromMinutes(4));
        Console.WriteLine($"sessionId: {session.SessionId}");
        Console.WriteLine($"model(init): {session.CurrentModel}");
        Console.WriteLine($"outcome: {outcome}");
        Console.WriteLine($"text: {string.Join(" | ", texts)}");
        await session.DisposeAsync();
        Console.WriteLine($"processExit: {(session is null ? "?" : "observed")}");
        try { Directory.Delete(workingDirectory, recursive: true); } catch (IOException) { }
        return texts.Count > 0 && outcome == SessionOutcome.Completed ? 0 : 1;
    }
}
