using System.Text.Json.Nodes;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Plugins;
using Fufucode.Core.RemoteControl;

namespace Fufucode.Plugins.FufuBridge;

/// <summary>
/// FUFU 桥接宿主：作为命名管道客户端连到 FUFU 的 \\.\pipe\fufu.fufucode.v1，
/// 上报会话目录快照与 Agent 事件，转发审批，并把 FUFU 的受控提交/切换/应答回派给宿主回调。
/// FUFU 未运行时按退避重连；连接与断开都不影响 Fufucode 自身功能。
/// </summary>
public sealed class FufuBridgeHost : IFufuBridge
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private IChatSession? _session;
    private string _title = "当前会话";
    private Func<RemoteSessionCatalog>? _catalog;
    private Func<string, CancellationToken, Task<RemoteCommandResult>>? _switchSession;
    private Func<string, string, CancellationToken, Task<RemoteCommandResult>>? _submitTask;
    private FufuPipeClient? _client;
    private bool _disposed;
    private readonly string? _logPath;

    public bool IsConnected { get; private set; }

    public FufuBridgeHost(string? logPath = null)
    {
        _logPath = logPath;
        _ = Task.Run(ConnectLoopAsync);
    }

    private void Log(string message)
    {
        if (_logPath is null) return;
        try { File.AppendAllText(_logPath, $"[{DateTime.UtcNow:o}] {message}\n"); } catch { }
    }

    public void Attach(IChatSession session, string sessionTitle)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (_session is not null) _session.Event -= OnSessionEvent;
            if (_session is IExtensionUiSession oldUi)
            {
                oldUi.ExtensionUiRequestReceived -= OnApprovalRequested;
                oldUi.ExtensionUiRequestClosed -= OnApprovalClosed;
            }
            _session = session;
            _title = sessionTitle;
            _session.Event += OnSessionEvent;
            if (session is IExtensionUiSession ui)
            {
                ui.ExtensionUiRequestReceived += OnApprovalRequested;
                ui.ExtensionUiRequestClosed += OnApprovalClosed;
            }
        }
        _ = SendSnapshotAsync();
    }

    public void ConfigureSessionDirectory(
        Func<RemoteSessionCatalog> catalog,
        Func<string, CancellationToken, Task<RemoteCommandResult>> switchSession,
        Func<string, string, CancellationToken, Task<RemoteCommandResult>> submitTask)
    {
        lock (_gate)
        {
            _catalog = catalog;
            _switchSession = switchSession;
            _submitTask = submitTask;
        }
    }

    public void LoadHistory(IReadOnlyList<ChatMessage> messages) => _ = SendSnapshotAsync();

    // ── 出站：快照 / 事件 / 审批 ────────────────────────────────

    private async Task SendSnapshotAsync()
    {
        RemoteSessionCatalog? catalog;
        IChatSession? session;
        string title;
        lock (_gate) { catalog = _catalog?.Invoke(); session = _session; title = _title; }

        var entries = new JsonArray();
        if (catalog is { } cat)
        {
            foreach (var info in cat.Sessions)
            {
                entries.Add(new JsonObject
                {
                    ["id"] = info.SessionId, ["title"] = info.Title, ["project"] = info.ProjectName,
                    ["activity"] = session is not null && info.IsCurrent ? session.Activity.ToString() : nameof(SessionActivity.Idle),
                    ["connection"] = session is not null && info.IsCurrent ? session.Connection.ToString() : nameof(SessionConnection.Stopped),
                    ["model"] = session is not null && info.IsCurrent ? session.CurrentModel : null,
                    ["canSend"] = session is not null && info.IsCurrent && session.CanSend,
                    ["canStop"] = session is not null && info.IsCurrent && session.CanStop,
                    ["queued"] = session is not null && info.IsCurrent ? session.QueuedCount : 0,
                    ["isCurrent"] = info.IsCurrent,
                });
            }
        }
        else if (session is not null)
        {
            entries.Add(new JsonObject
            {
                ["id"] = "current", ["title"] = title, ["project"] = "",
                ["activity"] = session.Activity.ToString(),
                ["connection"] = session.Connection.ToString(),
                ["model"] = session.CurrentModel,
                ["canSend"] = session.CanSend, ["canStop"] = session.CanStop,
                ["queued"] = session.QueuedCount, ["isCurrent"] = true,
            });
        }
        await SendAsync(new JsonObject { ["kind"] = FufuBridgeProtocol.KindSnapshot, ["sessions"] = entries })
            .ConfigureAwait(false);
    }

    private void OnSessionEvent(object? sender, SessionEvent e)
    {
        (string type, string summary) = e switch
        {
            SessionEvent.ActivityChanged a => ("activity", a.Activity.ToString()),
            SessionEvent.ConnectionChanged c => ("connection", c.Connection.ToString()),
            SessionEvent.MessageStarted m => ("message", $"[{m.Role}]"),
            SessionEvent.MessageFinished m => ("messageEnd", m.FinalMessage.Role.ToString()),
            SessionEvent.TurnSettled t => ("settled", t.Summary ?? t.Outcome.ToString()),
            SessionEvent.SessionError err => ("error", err.Message),
            SessionEvent.QueueChanged q => ("queued", q.QueuedCount.ToString()),
            SessionEvent.SubagentUpdated s => ("subagent", $"{s.Agent}:{s.Kind}:{s.Detail}"),
            _ => ("", ""),
        };
        if (type.Length == 0) return;
        _ = SendAsync(new JsonObject
        {
            ["kind"] = FufuBridgeProtocol.KindEvent,
            ["sessionId"] = CurrentSessionId(), ["type"] = type, ["summary"] = summary,
        });
        if (e is SessionEvent.ActivityChanged or SessionEvent.ConnectionChanged)
            _ = SendSnapshotAsync();
    }

    private void OnApprovalRequested(ExtensionUiRequest request)
    {
        var message = request.Payload.TryGetProperty("message", out var m) ? m.GetString() : request.Method;
        _ = SendAsync(new JsonObject
        {
            ["kind"] = FufuBridgeProtocol.KindApproval,
            ["requestId"] = request.Id, ["sessionId"] = CurrentSessionId(),
            ["tool"] = request.Payload.TryGetProperty("toolName", out var t) ? t.GetString() : request.Method,
            ["message"] = message,
        });
    }

    private void OnApprovalClosed(ExtensionUiRequest request) =>
        _ = SendAsync(new JsonObject
        {
            ["kind"] = FufuBridgeProtocol.KindApprovalClosed, ["requestId"] = request.Id,
        });

    private string CurrentSessionId()
    {
        lock (_gate) return _catalog?.Invoke().CurrentSessionId ?? "current";
    }

    // ── 连接循环 ────────────────────────────────────────────────

    private async Task ConnectLoopAsync()
    {
        var delay = 500;
        Log("connect loop started");
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                var client = new FufuPipeClient();
                await client.ConnectAsync(1500, _shutdown.Token).ConfigureAwait(false);
                lock (_gate) _client = client;
                IsConnected = true;
                delay = 500;
                Log("connected to FUFU pipe");

                var hello = new JsonObject
                {
                    ["kind"] = FufuBridgeProtocol.KindHello,
                    ["pluginVersion"] = FufuBridgeProtocol.ProtocolVersion,
                    ["fufucodeVersion"] = System.Diagnostics.FileVersionInfo
                        .GetVersionInfo(Environment.ProcessPath!).FileVersion,
                    ["pid"] = Environment.ProcessId,
                };
                await SendAsync(hello).ConfigureAwait(false);
                await SendSnapshotAsync().ConfigureAwait(false);
                await ReadLoopAsync(client).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log($"connect/loop failed: {ex.GetBaseException().Message}"); /* 退避重连 */ }

            IsConnected = false;
            lock (_gate) { _client?.Dispose(); _client = null; }
            try { await Task.Delay(delay, _shutdown.Token).ConfigureAwait(false); } catch { break; }
            delay = Math.Min(delay * 2, 8000);
        }
    }

    private async Task ReadLoopAsync(FufuPipeClient client)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            var line = await client.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);
            if (line is null) { Log("read loop ended: pipe closed"); return; } // FUFU 端断开
            var message = FufuBridgeProtocol.ReadLine(line);
            if (message is null) continue;
            try { await DispatchAsync(message).ConfigureAwait(false); }
            catch (Exception ex)
            {
                await SendAsync(new JsonObject
                {
                    ["kind"] = FufuBridgeProtocol.KindError,
                    ["code"] = "dispatch", ["message"] = ex.Message,
                }).ConfigureAwait(false);
            }
        }
    }

    private async Task DispatchAsync(JsonObject message)
    {
        Func<string, CancellationToken, Task<RemoteCommandResult>>? switchSession;
        Func<string, string, CancellationToken, Task<RemoteCommandResult>>? submitTask;
        IChatSession? session;
        lock (_gate) { switchSession = _switchSession; submitTask = _submitTask; session = _session; }

        switch (FufuBridgeProtocol.Kind(message))
        {
            case FufuBridgeProtocol.KindSubmit:
            {
                var sessionId = message["sessionId"]?.GetValue<string>();
                var text = message["text"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(text))
                { await SendErrorAsync("submit.invalid", "submit 缺 sessionId/text"); return; }
                if (submitTask is null) { await SendErrorAsync("submit.unsupported", "宿主未提供提交回调"); return; }
                var result = await submitTask(sessionId, text, _shutdown.Token).ConfigureAwait(false);
                if (!result.Accepted) await SendErrorAsync("submit.rejected", result.Message ?? "提交被拒绝");
                return;
            }
            case FufuBridgeProtocol.KindSelect:
            {
                var sessionId = message["sessionId"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(sessionId)) { await SendErrorAsync("select.invalid", "select 缺 sessionId"); return; }
                if (switchSession is null) { await SendErrorAsync("select.unsupported", "宿主未提供切换回调"); return; }
                var result = await switchSession(sessionId, _shutdown.Token).ConfigureAwait(false);
                if (!result.Accepted) await SendErrorAsync("select.rejected", result.Message ?? "切换被拒绝");
                return;
            }
            case FufuBridgeProtocol.KindApprove:
            {
                var requestId = message["requestId"]?.GetValue<string>();
                var approved = message["approved"]?.GetValue<bool>() == true;
                if (session is IExtensionUiSession ui && !string.IsNullOrWhiteSpace(requestId))
                {
                    var pending = ui.PendingExtensionUiRequests.FirstOrDefault(r => r.Id == requestId);
                    if (pending is not null)
                        await ui.RespondToExtensionUiAsync(pending,
                            PiExtensionUiResponse.Confirm(approved), _shutdown.Token).ConfigureAwait(false);
                }
                return;
            }
            case FufuBridgeProtocol.KindPing:
                await SendAsync(new JsonObject { ["kind"] = FufuBridgeProtocol.KindPong }).ConfigureAwait(false);
                return;
            case FufuBridgeProtocol.KindHelloAck:
                if (message["accepted"]?.GetValue<bool>() == false)
                    throw new InvalidOperationException(message["error"]?.GetValue<string>() ?? "协议被拒绝");
                return;
        }
    }

    private Task SendErrorAsync(string code, string detail) =>
        SendAsync(new JsonObject
        {
            ["kind"] = FufuBridgeProtocol.KindError, ["code"] = code, ["message"] = detail,
        });

    private async Task SendAsync(JsonObject message)
    {
        FufuPipeClient? client;
        lock (_gate) client = _client;
        if (client is null) return;
        await _writer.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        try { await client.WriteLineAsync(FufuBridgeProtocol.Write(message), _shutdown.Token).ConfigureAwait(false); }
        catch (Exception ex) { Log($"send failed: {ex.GetBaseException().Message}"); /* 断连由读循环感知并重连 */ }
        finally { _writer.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        lock (_gate)
        {
            if (_session is not null) _session.Event -= OnSessionEvent;
            if (_session is IExtensionUiSession ui)
            {
                ui.ExtensionUiRequestReceived -= OnApprovalRequested;
                ui.ExtensionUiRequestClosed -= OnApprovalClosed;
            }
            _session = null;
            _client?.Dispose(); _client = null;
        }
        _shutdown.Dispose();
    }
}
