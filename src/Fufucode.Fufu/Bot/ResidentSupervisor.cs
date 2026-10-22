using Fufucode.App.Abstractions;
using Fufucode.Fufu.Integration;

namespace Fufucode.Fufu.Bot;

/// <summary>
/// 观察型 Agent Supervisor：把桥接会话事件映射成常驻任务状态。
/// 第一阶段只观察和记录，不自动提交提示词、不调用工具、不发送外部消息。
/// </summary>
public sealed class ResidentSupervisor : IDisposable
{
    private readonly FufuPipeServer _server;
    private readonly ResidentTaskStore _tasks;
    private readonly ResidentCore _resident;
    private readonly TaskLog _log;
    private bool _disposed;

    public ResidentSupervisor(FufuPipeServer server, ResidentTaskStore tasks, ResidentCore resident, TaskLog log)
    {
        _server = server;
        _tasks = tasks;
        _resident = resident;
        _log = log;
        _server.SnapshotReceived += OnSnapshot;
        _server.AgentEventReceived += OnAgentEvent;
        _server.ApprovalReceived += OnApproval;
        _server.ApprovalClosed += OnApprovalClosed;
        _server.ConnectionChanged += OnConnection;
    }

    public IReadOnlyList<ResidentTask> Tasks => _tasks.Tasks;

    private void OnSnapshot(IReadOnlyList<Fufucode.App.Abstractions.FufuBridgeProtocol.SessionEntry> sessions)
    {
        foreach (var session in sessions)
        {
            if (_tasks.FindBySession(session.Id) is not null) continue;
            if (session.Activity is "Idle" or "Stopped" && session.Queued == 0) continue;
            CreateObservedTask(session.Id, session.Title, session.Project, "Bridge snapshot reports active work");
        }
    }

    private void OnAgentEvent(string sessionId, string type, string summary)
    {
        var task = _tasks.FindBySession(sessionId);
        if (task is null && type is "activity" or "approval" or "settled" or "error")
            task = CreateObservedTask(sessionId, "Observed Agent task", sessionId, summary);
        if (task is null) return;

        var next = type switch
        {
            "activity" when summary is nameof(SessionActivity.Running) or nameof(SessionActivity.Compacting)
                => ResidentTaskState.Running,
            "activity" when summary is nameof(SessionActivity.Cancelling)
                => ResidentTaskState.Cancelled,
            "approval" => ResidentTaskState.Awaiting,
            "settled" when summary is "Completed" or "已完成"
                => ResidentTaskState.Succeeded,
            "settled" => ResidentTaskState.Failed,
            "error" => ResidentTaskState.Failed,
            _ => task.State,
        };
        if (next == task.State && type is not "settled" and not "error") return;
        var updated = _tasks.SetState(task.Id, next, next == ResidentTaskState.Succeeded ? summary : null,
            next == ResidentTaskState.Failed ? summary : null);
        if (updated is not null)
        {
            var text = $"{updated.Title}: {updated.State}";
            _log.Append("resident-supervisor", "task.state", sessionId, text);
            _resident.Observe("resident-supervisor", "task.state", sessionId, text, updated.Id);
        }
    }

    private void OnApproval(string requestId, string sessionId, string tool, string message)
    {
        var task = _tasks.FindBySession(sessionId) ?? CreateObservedTask(sessionId, "Observed Agent task", sessionId, message);
        _tasks.SetState(task.Id, ResidentTaskState.Awaiting, error: $"approval: {tool}");
        _resident.Observe("resident-supervisor", "task.awaiting", sessionId, $"{tool}: {message}", task.Id);
    }

    private void OnApprovalClosed(string requestId)
    {
        _resident.Observe("resident-supervisor", "approval.closed", requestId, "approval request closed", requestId);
    }

    private void OnConnection(bool connected)
    {
        _resident.Observe("resident-supervisor", connected ? "bridge.connected" : "bridge.disconnected", "fufucode", connected ? "Bridge connected" : "Bridge disconnected");
    }

    private ResidentTask CreateObservedTask(string sessionId, string title, string subject, string prompt)
    {
        var task = _tasks.Create(title, "fufucode-bridge", subject, prompt, sessionId);
        _resident.Observe("resident-supervisor", "task.created", sessionId, title, task.Id);
        _log.Append("resident-supervisor", "task.created", sessionId, title);
        return task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _server.SnapshotReceived -= OnSnapshot;
        _server.AgentEventReceived -= OnAgentEvent;
        _server.ApprovalReceived -= OnApproval;
        _server.ApprovalClosed -= OnApprovalClosed;
        _server.ConnectionChanged -= OnConnection;
    }
}
