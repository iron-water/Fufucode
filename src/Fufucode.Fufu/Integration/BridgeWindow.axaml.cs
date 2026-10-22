using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Fufucode.Fufu.Integration;

/// <summary>会话目录行模型（顶层类型：编译绑定无法解析嵌套类型）。</summary>
public sealed record SessionRow(string Marker, string Title, string State, string Id);

/// <summary>FUFU ↔ Fufucode 桥接面板：会话目录、Agent 事件流、审批应答、受控提交/切换。</summary>
public sealed partial class BridgeWindow : Window
{
    private readonly FufuPipeServer _server;
    private readonly ObservableCollection<SessionRow> _sessions = new();
    private readonly ObservableCollection<string> _events = new();
    private string? _pendingApprovalId;

    public BridgeWindow(FufuPipeServer server)
    {
        _server = server;
        InitializeComponent();
        SessionList.ItemsSource = _sessions;
        Events.ItemsSource = _events;

        _server.SnapshotReceived += OnSnapshot;
        _server.AgentEventReceived += OnAgentEvent;
        _server.ApprovalReceived += OnApproval;
        _server.ApprovalClosed += OnApprovalClosed;
        _server.ConnectionChanged += OnConnection;

        RefreshButton.Click += (_, _) => RefreshSessions();
        SubmitButton.Click += async (_, _) => await SubmitAsync();
        SelectButton.Click += async (_, _) => await SelectAsync();
        SubmitBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await SubmitAsync(); };
        ApproveButton.Click += async (_, _) => await RespondAsync(true);
        DenyButton.Click += async (_, _) => await RespondAsync(false);

        Dispatcher.UIThread.Post(() => { RefreshSessions(); UpdateStatus(_server.IsConnected); });
    }

    private void OnSnapshot(IReadOnlyList<Fufucode.App.Abstractions.FufuBridgeProtocol.SessionEntry> sessions) =>
        Dispatcher.UIThread.Post(() =>
        {
            _sessions.Clear();
            foreach (var s in sessions)
                _sessions.Add(new(s.IsCurrent ? "●" : " ",
                    $"{s.Title}  ·  {s.Project}",
                    $"{s.Activity}/{s.Connection}{(s.Queued > 0 ? $" +{s.Queued}" : "")}", s.Id));
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
        });

    private void OnApproval(string requestId, string sessionId, string tool, string message) =>
        Dispatcher.UIThread.Post(() =>
        {
            _pendingApprovalId = requestId;
            ApprovalText.Text = $"{tool}\n{message}";
            ApprovalBar.IsVisible = true;
        });

    private void OnApprovalClosed(string requestId) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_pendingApprovalId == requestId) { _pendingApprovalId = null; ApprovalBar.IsVisible = false; }
        });

    private void OnConnection(bool connected) =>
        Dispatcher.UIThread.Post(() => UpdateStatus(connected));

    private void UpdateStatus(bool connected)
    {
        StatusText.Text = connected ? "已连接 Fufucode" : "未连接 Fufucode（等待 Bridge 插件）";
        StatusText.Foreground = connected ? Avalonia.Media.Brushes.LightGreen : Avalonia.Media.Brushes.LightPink;
    }

    private void RefreshSessions() => OnSnapshot(_server.Sessions);

    private string? SelectedId() => SessionList.SelectedItem is SessionRow row ? row.Id : _server.CurrentSessionId;

    private async Task SubmitAsync()
    {
        var text = SubmitBox.Text?.Trim();
        var sessionId = SelectedId();
        if (string.IsNullOrEmpty(text) || sessionId is null) return;
        SubmitBox.Text = "";
        try { await _server.SubmitAsync(sessionId, text); _events.Add($"[submit→{sessionId}] {text}"); }
        catch (Exception ex) { _events.Add($"[error] 提交失败：{ex.Message}"); }
    }

    private async Task SelectAsync()
    {
        if (SelectedId() is { } id)
        {
            try { await _server.SelectAsync(id); _events.Add($"[select→{id}]"); }
            catch (Exception ex) { _events.Add($"[error] 切换失败：{ex.Message}"); }
        }
    }

    private async Task RespondAsync(bool approved)
    {
        if (_pendingApprovalId is { } id)
        {
            _pendingApprovalId = null;
            ApprovalBar.IsVisible = false;
            try { await _server.ApproveAsync(id, approved); }
            catch { /* 断连后审批由 Fufucode 本地 UI 兜底 */ }
        }
    }
}
