using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Fufucode.Fufu.Bot;
using Fufucode.Fufu.Settings;

namespace Fufucode.Fufu.Integration;

/// <summary>会话目录行模型（顶层类型：编译绑定无法解析嵌套类型）。</summary>
public sealed record SessionRow(string Marker, string Watched, string Title, string State, string Id);

/// <summary>FUFU ↔ Fufucode 桥接面板：会话目录、Agent 事件流、审批应答、受控提交/切换、关注与任务日志。</summary>
public sealed partial class BridgeWindow : Window
{
    private readonly FufuPipeServer _server;
    private readonly ProactiveEngine _engine;
    private readonly FufuSettingsStore _settings;
    private readonly TaskLog _taskLog;
    private readonly ObservableCollection<SessionRow> _sessions = new();
    private readonly ObservableCollection<string> _events = new();
    private readonly ObservableCollection<string> _logLines = new();
    private string? _pendingApprovalId;

    public BridgeWindow(FufuPipeServer server, ProactiveEngine engine,
        FufuSettingsStore settings, TaskLog taskLog)
    {
        _server = server;
        _engine = engine;
        _settings = settings;
        _taskLog = taskLog;
        InitializeComponent();
        SessionList.ItemsSource = _sessions;
        Events.ItemsSource = _events;
        LogItems.ItemsSource = _logLines;

        _server.SnapshotReceived += OnSnapshot;
        _server.AgentEventReceived += OnAgentEvent;
        _server.ApprovalReceived += OnApproval;
        _server.ApprovalClosed += OnApprovalClosed;
        _server.ConnectionChanged += OnConnection;
        _engine.WatchChanged += OnWatchChanged;
        _settings.Changed += _ => Dispatcher.UIThread.Post(RefreshMeta);

        RefreshButton.Click += (_, _) => { RefreshSessions(); RefreshLog(); };
        SubmitButton.Click += async (_, _) => await SubmitAsync();
        SelectButton.Click += async (_, _) => await SelectAsync();
        SubmitBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await SubmitAsync(); };
        ApproveButton.Click += async (_, _) => await RespondAsync(true);
        DenyButton.Click += async (_, _) => await RespondAsync(false);
        WatchButton.Click += (_, _) =>
        {
            if (SelectedId() is { } id) _engine.WatchSession(id);
        };
        WatchAllButton.Click += (_, _) => _engine.WatchAll();
        ProactiveCheck.IsCheckedChanged += (_, _) =>
        {
            var on = ProactiveCheck.IsChecked == true;
            if (on != _settings.Current.ProactiveEnabled)
            {
                _settings.Update(v => v with { ProactiveEnabled = on });
                _taskLog.Append("user", "proactive", "", on ? "启用主动行为" : "停止主动行为");
            }
        };

        Dispatcher.UIThread.Post(() => { RefreshSessions(); RefreshMeta(); RefreshLog(); UpdateStatus(_server.IsConnected); });
    }

    private void OnSnapshot(IReadOnlyList<Fufucode.App.Abstractions.FufuBridgeProtocol.SessionEntry> sessions) =>
        Dispatcher.UIThread.Post(() =>
        {
            var s = _settings.Current;
            _sessions.Clear();
            foreach (var x in sessions)
                _sessions.Add(new(x.IsCurrent ? "●" : " ",
                    !s.WatchAllSessions && s.WatchedSessionId == x.Id ? "★" : "",
                    $"{x.Title}  ·  {x.Project}",
                    $"{x.Activity}/{x.Connection}{(x.Queued > 0 ? $" +{x.Queued}" : "")}", x.Id));
            if (SessionList.SelectedIndex < 0 && _sessions.Count > 0)
            {
                var idx = 0;
                for (var i = 0; i < _sessions.Count; i++)
                    if (_sessions[i].Marker == "●") { idx = i; break; }
                SessionList.SelectedIndex = idx;
            }
        });

    private void OnAgentEvent(string sessionId, string type, string summary) =>
        Dispatcher.UIThread.Post(() =>
        {
            _events.Add($"[{type}] {summary}");
            while (_events.Count > 300) _events.RemoveAt(0);
            EventScroll.ScrollToEnd();
            RefreshLog();
        });

    private void OnApproval(string requestId, string sessionId, string tool, string message) =>
        Dispatcher.UIThread.Post(() =>
        {
            _pendingApprovalId = requestId;
            ApprovalText.Text = $"{tool}\n{message}";
            ApprovalBar.IsVisible = true;
            RefreshLog();
        });

    private void OnApprovalClosed(string requestId) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_pendingApprovalId == requestId) { _pendingApprovalId = null; ApprovalBar.IsVisible = false; }
        });

    private void OnConnection(bool connected) =>
        Dispatcher.UIThread.Post(() => { UpdateStatus(connected); RefreshLog(); });

    private void OnWatchChanged() => Dispatcher.UIThread.Post(() => { RefreshMeta(); RefreshSessions(); });

    private void UpdateStatus(bool connected)
    {
        StatusText.Text = connected ? "已连接 Fufucode" : "未连接 Fufucode（等待 Bridge 插件）";
        StatusText.Foreground = connected ? Avalonia.Media.Brushes.LightGreen : Avalonia.Media.Brushes.LightPink;
    }

    /// <summary>关注描述与主动行为勾选（与设置同步）。</summary>
    private void RefreshMeta()
    {
        WatchText.Text = _engine.WatchDescription();
        ProactiveCheck.IsChecked = _settings.Current.ProactiveEnabled;
    }

    /// <summary>任务日志尾部：最新 8 条，新条目在下。</summary>
    private void RefreshLog()
    {
        _logLines.Clear();
        foreach (var e in _taskLog.ReadTail(8))
            _logLines.Add($"{e.Ts:HH:mm:ss} [{e.Source}] {Format(e)}");
        static string Format(TaskLog.Entry e)
        {
            var session = string.IsNullOrEmpty(e.Session) ? "" : $" {Shorten(e.Session)}";
            return $"{e.Kind}{session} — {Trunc(e.Text)}";
        }
        static string Shorten(string id) => id.Length <= 8 ? id : id[..8] + "…";
        static string Trunc(string t) => t.Length <= 80 ? t : t[..80] + "…";
    }

    private void RefreshSessions() => OnSnapshot(_server.Sessions);

    private string? SelectedId() => SessionList.SelectedItem is SessionRow row ? row.Id : _server.CurrentSessionId;

    private async Task SubmitAsync()
    {
        var text = SubmitBox.Text?.Trim();
        var sessionId = SelectedId();
        if (string.IsNullOrEmpty(text) || sessionId is null) return;
        SubmitBox.Text = "";
        try
        {
            await _server.SubmitAsync(sessionId, text);
            _events.Add($"[submit→{sessionId}] {text}");
            _taskLog.Append("user", "submit", sessionId, text);
        }
        catch (Exception ex) { _events.Add($"[error] 提交失败：{ex.Message}"); }
    }

    private async Task SelectAsync()
    {
        if (SelectedId() is { } id)
        {
            try
            {
                await _server.SelectAsync(id);
                _events.Add($"[select→{id}]");
                _taskLog.Append("user", "select", id, "切换宿主会话");
            }
            catch (Exception ex) { _events.Add($"[error] 切换失败：{ex.Message}"); }
        }
    }

    private async Task RespondAsync(bool approved)
    {
        if (_pendingApprovalId is { } id)
        {
            _pendingApprovalId = null;
            ApprovalBar.IsVisible = false;
            _taskLog.Append("user", "approve", "", $"{id}: {(approved ? "允许" : "拒绝")}");
            try { await _server.ApproveAsync(id, approved); }
            catch { /* 断连后审批由 Fufucode 本地 UI 兜底 */ }
            RefreshLog();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _engine.WatchChanged -= OnWatchChanged;
        base.OnClosed(e);
    }
}
