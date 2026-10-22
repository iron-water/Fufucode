using Avalonia.Threading;
using Fufucode.App.Abstractions;
using Fufucode.Fufu.Integration;
using Fufucode.Fufu.Settings;

namespace Fufucode.Fufu.Bot;

/// <summary>
/// FUFU-05 主动行为引擎：订阅桥接事件 → 规则表 → 气泡通知 + 桌宠状态 + 任务日志。
/// 授权模型：<see cref="FufuSettings.ProactiveEnabled"/> 为总开关（随时可停）；
/// <see cref="FufuSettings.WatchAllSessions"/>/<see cref="FufuSettings.WatchedSessionId"/>
/// 决定哪些会话驱动通知——非关注会话只入日志。
/// 简单自主调度：关注会话 Running 超时 → 看门狗提醒一次（每个工作段至多一次）。
/// 所有回调可能在管道线程触发，通知/状态一律 Dispatcher.UIThread.Post。
/// </summary>
public sealed class ProactiveEngine : IDisposable
{
    private static readonly TimeSpan WatchdogDelay = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan NoticeThrottle = TimeSpan.FromSeconds(8);

    private readonly FufuPipeServer _server;
    private readonly FufuSettingsStore _settings;
    private readonly TaskLog _log;
    private readonly DispatcherTimer _watchdog;
    private readonly HashSet<string> _seenApprovals = new();
    private DateTimeOffset _runningSince;
    private bool _watchdogFired;
    private DateTimeOffset _lastNotice;

    /// <summary>通知气泡文案（UI 线程投递）；null 文本不发出。</summary>
    public event Action<string>? Noticed;

    /// <summary>事件解析出的桌宠状态建议（UI 线程投递）；由宿主决定是否应用。</summary>
    public event Action<Pet.PetState>? StateSuggested;

    /// <summary>关注会话变化（刷新面板标记）。</summary>
    public event Action? WatchChanged;

    public ProactiveEngine(FufuPipeServer server, FufuSettingsStore settings, TaskLog log)
    {
        _server = server;
        _settings = settings;
        _log = log;
        _server.SnapshotReceived += OnSnapshot;
        _server.AgentEventReceived += OnAgentEvent;
        _server.ApprovalReceived += OnApproval;
        _server.ConnectionChanged += OnConnection;
        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _watchdog.Tick += (_, _) => WatchdogTick();
        _watchdog.Start();
    }

    private bool Watched(string sessionId)
    {
        var s = _settings.Current;
        return s.WatchAllSessions || string.IsNullOrEmpty(s.WatchedSessionId)
            || string.Equals(s.WatchedSessionId, sessionId, StringComparison.Ordinal);
    }

    /// <summary>当前关注上下文描述（面板显示用）。</summary>
    public string WatchDescription()
    {
        var s = _settings.Current;
        if (s.WatchAllSessions || string.IsNullOrEmpty(s.WatchedSessionId)) return "全部会话";
        var hit = _server.Sessions.FirstOrDefault(x => x.Id == s.WatchedSessionId);
        return hit is { } e ? $"{e.Title} · {e.Project}" : s.WatchedSessionId!;
    }

    /// <summary>桥接面板「关注」：改为只看一个会话。</summary>
    public void WatchSession(string sessionId)
    {
        _settings.Update(v => v with { WatchAllSessions = false, WatchedSessionId = sessionId });
        _log.Append("user", "watch", sessionId, "关注会话");
        WatchChanged?.Invoke();
    }

    /// <summary>桥接面板「关注全部」。</summary>
    public void WatchAll()
    {
        _settings.Update(v => v with { WatchAllSessions = true });
        _log.Append("user", "watch", "", "关注全部会话");
        WatchChanged?.Invoke();
    }

    private void OnSnapshot(IReadOnlyList<FufuBridgeProtocol.SessionEntry> sessions)
    {
        // 关注对象失效（会话目录里没了）→ 回落到全部，避免永久失聪。
        var s = _settings.Current;
        if (!s.WatchAllSessions && s.WatchedSessionId is { } id
            && sessions.Count > 0 && sessions.All(x => x.Id != id))
        {
            _settings.Update(v => v with { WatchAllSessions = true, WatchedSessionId = null });
            _log.Append("proactive", "watch", id, "关注会话已消失，回落到全部");
            WatchChanged?.Invoke();
        }
    }

    private void OnAgentEvent(string sessionId, string type, string summary)
    {
        _log.Append("bridge", type, sessionId, summary);

        // 看门狗计时：关注会话进入 Running 起算；任何非 Running 活动重置。
        var isRunning = type == "activity" &&
            (summary == nameof(SessionActivity.Running) || summary == nameof(SessionActivity.Compacting));
        if (Watched(sessionId))
        {
            if (isRunning && !_watchdogFired && _runningSince == default)
                _runningSince = DateTimeOffset.UtcNow;
            else if (!isRunning && type == "activity")
            { _runningSince = default; _watchdogFired = false; }
        }

        if (!_settings.Current.ProactiveEnabled || !Watched(sessionId)) return;

        switch (type)
        {
            case "settled":
            {
                var ok = summary is "已完成" or "Completed";
                Notice(ok ? "任务完成 ✓" : $"任务结束：{summary}");
                Suggest(ok ? Pet.PetState.Success : Pet.PetState.Error);
                break;
            }
            case "error":
                Notice($"出错了：{summary}");
                Suggest(Pet.PetState.Error);
                break;
            case "activity" when summary == nameof(SessionActivity.Retrying):
                Notice("任务重试中…");
                Suggest(Pet.PetState.Attention);
                break;
        }
    }

    private void OnApproval(string requestId, string sessionId, string tool, string message)
    {
        _log.Append("bridge", "approval", sessionId, $"{tool}: {message}");
        if (!_settings.Current.ProactiveEnabled || !Watched(sessionId)) return;
        if (!_seenApprovals.Add(requestId)) return; // 同一审批不重复提醒
        Notice($"需要审批：{tool}");
        Suggest(Pet.PetState.Attention);
    }

    private void OnConnection(bool connected)
    {
        _log.Append("bridge", "connection", "", connected ? "connected" : "disconnected");
        if (connected) { _seenApprovals.Clear(); _runningSince = default; _watchdogFired = false; }
        else if (_settings.Current.ProactiveEnabled) Notice("Fufucode 已断开");
    }

    /// <summary>简单自主调度：关注会话长任务看门狗，到点提醒一次。</summary>
    private void WatchdogTick()
    {
        if (!_settings.Current.ProactiveEnabled) return;
        if (_runningSince == default || _watchdogFired) return;
        if (DateTimeOffset.UtcNow - _runningSince < WatchdogDelay) return;
        _watchdogFired = true;
        Notice("任务还在运行，我帮你盯着");
        _log.Append("proactive", "watchdog", _settings.Current.WatchedSessionId ?? "", "长任务看门狗提醒");
    }

    private void Notice(string text)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastNotice < NoticeThrottle) return; // 防抖：密集事件不刷屏
        _lastNotice = now;
        _log.Append("fufu", "notice", "", text);
        Dispatcher.UIThread.Post(() => Noticed?.Invoke(text));
    }

    private void Suggest(Pet.PetState state) =>
        Dispatcher.UIThread.Post(() => StateSuggested?.Invoke(state));

    public void Dispose()
    {
        _watchdog.Stop();
        _server.SnapshotReceived -= OnSnapshot;
        _server.AgentEventReceived -= OnAgentEvent;
        _server.ApprovalReceived -= OnApproval;
        _server.ConnectionChanged -= OnConnection;
    }
}
