using System.Text;
using System.Text.Json;
using Fufucode.App.Abstractions;
using Fufucode.Plugins.RemoteControl;
using Fufucode.Core.RemoteControl;

namespace Fufucode.Mobile.LanHarness;

/// <summary>
/// 手机端真机/模拟器验收用的桌面端宿主。
///
/// 作用：把**真实**的 <c>RemoteControlHost</c>（与产品桌面端同一个类）跑起来，
/// 接入一个脚本化会话，使 Android 客户端可以对着真实局域网协议完成配对、收流、
/// 审批与停止。它不替代产品桌面端，也不修改任何共享外壳代码。
///
/// 用法：<c>dotnet run --project tests/Fufucode.Mobile.LanHarness -- --port 8080</c>
/// 命令（stdin，每行一条）：script | tool-done | stop-sim | status | pin | quit
/// 输出：每行一个 JSON，便于驱动脚本解析。
/// </summary>
internal static class Program
{
    private static readonly object Gate = new();
    private static readonly List<string> Activity = new();

    private static async Task<int> Main(string[] args)
    {
        var port = 0;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--port" && int.TryParse(args[i + 1], out var parsed)) port = parsed;
        }

        var session = new ScriptedSession();
        using var host = new RemoteControlHost();
        host.Attach(session, "AND-1 移动端联调会话");

        RemoteControlStatus status;
        try
        {
            status = await host.StartAsync(port);
        }
        catch (Exception error)
        {
            Emit(new { type = "error", message = error.Message });
            return 1;
        }

        Emit(new
        {
            type = "ready",
            address = status.Address,
            port = status.Port,
            pairingUrl = status.PairingUrl,
            pin = status.Pin,
            tokenLifetime = status.TokenLifetime.ToString(),
        });

        Console.Out.Flush();

        var running = true;
        while (running)
        {
            var line = await Console.In.ReadLineAsync();
            if (line is null) break;
            var command = line.Trim();
            switch (command)
            {
                case "script":
                    session.RunScriptedTurn();
                    Emit(new { type = "scripted", note = "用户消息 + 流式助手 + 待审批工具已投递" });
                    break;
                case "approval":
                    {
                        // 通过**真实**审批闸门登记一次待审批：与产品桌面端同一条代码路径。
                        var request = host.RequestApproval(
                            "call-and1-0001",
                            "edit",
                            "{\"path\":\"src/Views/List.axaml\",\"revision\":1}",
                            "修改 src/Views/List.axaml（计划 14 行）");
                        Emit(new
                        {
                            type = "approvalRequested",
                            approvalId = request?.ApprovalId,
                            toolName = request?.ToolName,
                        });
                        break;
                    }
                case "await-approval":
                    {
                        var pending = host.Adapter?.Approvals.Pending ?? Array.Empty<RemoteApprovalRequest>();
                        if (pending.Count == 0)
                        {
                            Emit(new { type = "awaitApproval", result = "none" });
                            break;
                        }

                        var approvalId = pending[0].ApprovalId;
                        var resolution = await host.WaitForApprovalAsync(approvalId)!.WaitAsync(
                            TimeSpan.FromSeconds(30));
                        Emit(new
                        {
                            type = "awaitApproval",
                            approvalId,
                            decision = resolution.Decision.ToString(),
                            accepted = resolution.Accepted,
                        });
                        break;
                    }
                case "tool-done":
                    session.CompleteTool();
                    Emit(new { type = "toolDone" });
                    break;
                case "stop-sim":
                    session.SimulateStop();
                    Emit(new { type = "stopped" });
                    break;
                case "status":
                    Emit(new
                    {
                        type = "status",
                        service = host.Status.State.ToString(),
                        connections = host.Status.ConnectionCount,
                        pin = host.Status.Pin,
                        sent = session.SentMessages,
                        stopCount = session.StopCount,
                        approvalsPending = host.Adapter?.Approvals.Pending.Count ?? 0,
                        activityLog = Snapshot(),
                    });
                    break;
                case "pin":
                    Emit(new { type = "pin", pin = host.Status.Pin, url = host.Status.PairingUrl });
                    break;
                case "quit":
                    running = false;
                    break;
                default:
                    Emit(new { type = "unknown", command });
                    break;
            }

            Console.Out.Flush();
        }

        host.Stop();
        Emit(new { type = "bye" });
        return 0;
    }

    private static void Emit(object payload)
    {
        Console.WriteLine(JsonSerializer.Serialize(payload));
    }

    private static string[] Snapshot()
    {
        lock (Gate) return Activity.ToArray();
    }

    private static void Log(string entry)
    {
        lock (Gate)
        {
            Activity.Add(entry);
            if (Activity.Count > 200) Activity.RemoveAt(0);
        }
    }

    /// <summary>
    /// 脚本化会话：实现真实的 <see cref="IChatSession"/>，按远控投影所需的
    /// 事件序列（消息开始、块开始、文本/思考增量、工具参数与状态、结束）投递。
    /// </summary>
    private sealed class ScriptedSession : IChatSession
    {
        private readonly object _gate = new();
        private SessionConnection _connection = SessionConnection.Connected;
        private SessionActivity _activity = SessionActivity.Idle;
        private int _messageSeq;

        public SessionConnection Connection { get { lock (_gate) return _connection; } }
        public SessionActivity Activity { get { lock (_gate) return _activity; } }
        public SessionOutcome? LastOutcome { get; private set; }
        public string? CurrentModel => "gpt-6.1-sol";
        public bool CanSend => Connection == SessionConnection.Connected && Activity != SessionActivity.Cancelling;
        public bool CanStop => Activity == SessionActivity.Running;
        public int QueuedCount { get; private set; }

        public List<string> SentMessages { get; } = new();
        public int StopCount { get; private set; }

        public event EventHandler<SessionEvent>? Event;

        public Task SendAsync(string text, CancellationToken cancellationToken = default)
        {
            lock (_gate) SentMessages.Add(text);
            Log("send:" + text);
            Raise(new SessionEvent.MessageStarted(NextId(), MessageRole.User, null, 1));
            var userId = "u-" + SentMessages.Count;
            Raise(new SessionEvent.MessageStarted(userId, MessageRole.User, null, 1));
            Raise(new SessionEvent.BlockStarted(userId, 0, MessageBlockKind.Text, null, null));
            Raise(new SessionEvent.TextAppended(userId, 0, text));
            Raise(new SessionEvent.BlockFinished(userId, 0, new TextBlock(text)));
            Raise(new SessionEvent.MessageFinished(userId,
                new ChatMessage(userId, MessageRole.User, null, new MessageBlock[] { new TextBlock(text) }, null, true),
                null));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                StopCount++;
                _activity = SessionActivity.Idle;
            }

            Log("stop");
            Raise(new SessionEvent.ActivityChanged(SessionActivity.Idle));
            Raise(new SessionEvent.TurnSettled(SessionOutcome.Interrupted, "已按手机端要求停止"));
            return Task.CompletedTask;
        }

        /// <summary>投递一轮脚本化助手回复：思考 → 文本流 → 工具调用（等待审批）。</summary>
        public void RunScriptedTurn()
        {
            var id = NextId();
            lock (_gate) _activity = SessionActivity.Running;
            Raise(new SessionEvent.ActivityChanged(SessionActivity.Running));

            Raise(new SessionEvent.MessageStarted(id, MessageRole.Assistant, CurrentModel, 3));
            Raise(new SessionEvent.BlockStarted(id, 0, MessageBlockKind.Thinking, null, null));
            Raise(new SessionEvent.ThinkingAppended(id, 0, "先确认当前工作区的结构。"));
            Raise(new SessionEvent.BlockFinished(id, 0, new ThinkingBlock("先确认当前工作区的结构。")));

            Raise(new SessionEvent.BlockStarted(id, 1, MessageBlockKind.Text, null, null));
            foreach (var chunk in new[] { "正在读取", "工作区文件", "并准备修改方案。" })
                Raise(new SessionEvent.TextAppended(id, 1, chunk));
            Raise(new SessionEvent.BlockFinished(id, 1, new TextBlock("正在读取工作区文件并准备修改方案。")));

            const string toolCallId = "call-and1-0001";
            Raise(new SessionEvent.BlockStarted(id, 2, MessageBlockKind.ToolCall, toolCallId, "edit"));
            Raise(new SessionEvent.ToolCallAppended(id, 2, toolCallId, "{\"path\":\"src/View"));
            Raise(new SessionEvent.ToolCallAppended(id, 2, toolCallId, "s/List.axaml\",\"revision\":1}"));
            Raise(new SessionEvent.ToolCallStatusChanged(id, 2, toolCallId, ToolCallStatus.Running, "等待手机端审批"));
            Log("tool:awaiting-approval");
        }

        /// <summary>把待审批工具推进到完成态（模拟电脑端在获批后继续执行）。</summary>
        public void CompleteTool()
        {
            const string toolCallId = "call-and1-0001";
            var id = $"assistant-{_messageSeq}";
            Raise(new SessionEvent.ToolCallStatusChanged(id, 2, toolCallId, ToolCallStatus.Completed, "已修改 1 个文件"));
            Raise(new SessionEvent.BlockFinished(id, 2, new ToolCallBlock(
                toolCallId, "edit", "{\"path\":\"src/Views/List.axaml\",\"revision\":1}",
                ToolCallStatus.Completed, "已修改 1 个文件", "Applied patch (14 lines).", false)));
            lock (_gate) _activity = SessionActivity.Idle;
            Raise(new SessionEvent.ActivityChanged(SessionActivity.Idle));
            Raise(new SessionEvent.TurnSettled(SessionOutcome.Completed, null));
            Log("tool:completed");
        }

        public void SimulateStop()
        {
            lock (_gate) _activity = SessionActivity.Idle;
            Raise(new SessionEvent.ActivityChanged(SessionActivity.Idle));
            Raise(new SessionEvent.TurnSettled(SessionOutcome.Interrupted, "模拟停止"));
        }

        private string NextId() => "assistant-" + Interlocked.Increment(ref _messageSeq);

        private void Raise(SessionEvent e) => Event?.Invoke(this, e);
    }
}
