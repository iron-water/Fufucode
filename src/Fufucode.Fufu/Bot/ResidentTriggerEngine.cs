using Avalonia.Threading;
using Fufucode.Fufu.Integration;

namespace Fufucode.Fufu.Bot;

/// <summary>
/// Resident Core 触发引擎。它判断“是否值得观察”，并创建 queued 观察任务；不调用 FUMO、不执行工具。
/// </summary>
public sealed class ResidentTriggerEngine : IDisposable
{
    private readonly FufuPipeServer _server;
    private readonly ResidentTriggerStore _triggers;
    private readonly ResidentTaskStore _tasks;
    private readonly ResidentCore _resident;
    private readonly TaskLog _log;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public ResidentTriggerEngine(FufuPipeServer server, ResidentTriggerStore triggers,
        ResidentTaskStore tasks, ResidentCore resident, TaskLog log)
    {
        _server = server;
        _triggers = triggers;
        _tasks = tasks;
        _resident = resident;
        _log = log;
        _server.AgentEventReceived += OnAgentEvent;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => CheckIntervals();
        _timer.Start();
    }

    private void OnAgentEvent(string sessionId, string type, string summary)
    {
        foreach (var trigger in _triggers.Triggers.Where(x => x.Enabled && x.Kind == ResidentTriggerKind.BridgeEvent &&
            string.Equals(x.EventType, type, StringComparison.OrdinalIgnoreCase)))
        {
            var now = DateTimeOffset.UtcNow;
            if (trigger.LastFiredAt is { } last && now - last < TimeSpan.FromSeconds(trigger.CooldownSeconds)) continue;
            Fire(trigger, sessionId, $"桥接事件 {type}: {summary}");
        }
    }

    private void CheckIntervals()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var trigger in _triggers.Triggers.Where(x => x.Enabled && x.Kind == ResidentTriggerKind.Interval &&
            x.NextDueAt is { } due && due <= now))
        {
            Fire(trigger, "", $"定时观察：{trigger.Name}");
        }
    }

    private void Fire(ResidentTrigger trigger, string subject, string reason)
    {
        var updated = _triggers.Update(trigger.Id, x => x with
        {
            LastFiredAt = DateTimeOffset.UtcNow,
            NextDueAt = x.Kind == ResidentTriggerKind.Interval && x.IntervalSeconds > 0
                ? DateTimeOffset.UtcNow.AddSeconds(x.IntervalSeconds) : x.NextDueAt,
        });
        if (updated is null) return;
        if (_tasks.Tasks.Any(x => x.Source == "resident-trigger" && x.Subject == subject &&
            x.State is ResidentTaskState.Queued or ResidentTaskState.Running or ResidentTaskState.Awaiting)) return;

        var task = _tasks.Create(trigger.Name, "resident-trigger", subject, reason);
        var text = $"触发器 {trigger.Name} 创建观察任务：{reason}";
        _resident.Observe("resident-trigger", "trigger.fired", subject, text, task.Id);
        _log.Append("resident-trigger", "trigger.fired", subject, text);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _server.AgentEventReceived -= OnAgentEvent;
    }
}
