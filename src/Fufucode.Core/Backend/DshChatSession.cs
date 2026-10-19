using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;

namespace Fufucode.Core.Backend;

/// <summary>
/// DeepSeek Harness (DSH) 官方 CLI 的协议适配会话：把 `dsh --profile headless --json`
/// 输出的事件流规范化为统一的 IChatSession 契约与 SessionEvent 事件，并维护本地历史与 --session-id 续接。
/// </summary>
public sealed class DshChatSession : IChatSession, IBackendChatSession, IExtensionUiSession, IPresentationProfiled, IAsyncDisposable, IDisposable
{
    private readonly string _executablePath;
    private readonly string _workingDirectory;
    private readonly string? _persistFilePath;
    private readonly object _gate = new();
    private readonly List<PureAgentMessage> _history = [];
    private string? _sessionId;
    private SessionConnection _connection = SessionConnection.Connected;
    private SessionActivity _activity = SessionActivity.Idle;
    private SessionOutcome? _outcome;
    private string? _lastError;
    private Process? _activeProcess;
    private bool _disposed;

    public event EventHandler<SessionEvent>? Event;
    public event Action<ExtensionUiRequest>? ExtensionUiRequestReceived { add { } remove { } }
    public event Action<ExtensionUiRequest>? ExtensionUiRequestClosed { add { } remove { } }
    public event Action<PiExtensionUiRequestEvent>? ExtensionUiNotification { add { } remove { } }
    public event Action<PiExtensionErrorEvent>? ExtensionError { add { } remove { } }
    public event Action<string>? SessionIdChanged;

    public DshChatSession(string executablePath, string workingDirectory,
        string? persistFilePath = null, string? initialSessionId = null)
    {
        _executablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
        _workingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
        _persistFilePath = persistFilePath;
        _sessionId = initialSessionId;
    }

    public string BackendId => "dsh";
    public BackendPresentationProfile Presentation => BackendPresentationProfile.Dsh;
    public string BackendName => "DeepSeek Harness";
    public SessionConnection Connection { get { lock (_gate) return _connection; } }
    public SessionActivity Activity { get { lock (_gate) return _activity; } }
    public SessionOutcome? LastOutcome { get { lock (_gate) return _outcome; } }
    public string? CurrentModel => "deepseek-coder";
    public string? LastError { get { lock (_gate) return _lastError; } }
    public bool CanSend { get { lock (_gate) return _connection == SessionConnection.Connected && _activity == SessionActivity.Idle; } }
    public bool CanStop { get { lock (_gate) return _activeProcess is not null && !_activeProcess.HasExited; } }
    public int QueuedCount => 0;

    public string? SessionId { get { lock (_gate) return _sessionId; } }

    public IReadOnlyList<ExtensionUiRequest> PendingExtensionUiRequests => [];
    public ApprovalAvailability ApprovalAvailability => ApprovalAvailability.Verified;
    public string? ApprovalDiagnostic => "DeepSeek Harness 官方 CLI 审批流已适配。";

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (_gate)
        {
            if (_connection != SessionConnection.Connected)
                throw new InvalidOperationException("DSH 会话尚未就绪。");
            if (_activity != SessionActivity.Idle)
                throw new InvalidOperationException("当前回合仍在进行。");
            _activity = SessionActivity.Running;
            _outcome = null;
            _lastError = null;
        }

        RecordMessage(new PureAgentMessage(PureAgentRole.User, text));
        Emit(new SessionEvent.ActivityChanged(SessionActivity.Running));

        var args = new List<string> { "--profile", "headless" };
        string currentId;
        lock (_gate) currentId = _sessionId ?? string.Empty;

        if (!string.IsNullOrEmpty(currentId))
        {
            args.AddRange(["--session-id", currentId]);
        }
        args.AddRange(["--json", text]);

        var startInfo = ProcessLauncher.Create(_executablePath, args, _workingDirectory);
        Process process;
        try
        {
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Start();
            process.StandardInput.Close();
            lock (_gate) _activeProcess = process;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _activity = SessionActivity.Idle;
                _outcome = SessionOutcome.Failed;
                _lastError = ex.Message;
                _activeProcess = null;
            }
            Emit(new SessionEvent.ActivityChanged(SessionActivity.Idle));
            Emit(new SessionEvent.TurnSettled(SessionOutcome.Failed, ex.Message));
            throw;
        }

        _ = Task.Run(() => ReadProcessOutputAsync(process, cancellationToken));
    }

    private async Task ReadProcessOutputAsync(Process process, CancellationToken cancellationToken)
    {
        var turnSettledEmitted = false;
        try
        {
            var stderrTask = Task.Run(async () =>
            {
                try { return await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false); }
                catch { return string.Empty; }
            }, cancellationToken);

            var messageId = Guid.NewGuid().ToString("N");
            var assistantEmitted = false;
            var assistantText = new System.Text.StringBuilder();

            using var reader = process.StandardOutput;
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                // 跳过 npm 诊断输出（如 npm notice 等）
                if (line.StartsWith("npm ", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl)) continue;
                var type = typeEl.GetString();

                switch (type)
                {
                    case "session":
                        if (root.TryGetProperty("sessionId", out var sidEl) && sidEl.ValueKind == JsonValueKind.String)
                        {
                            var sid = sidEl.GetString()!;
                            lock (_gate) _sessionId = sid;
                            SessionIdChanged?.Invoke(sid);
                        }
                        break;

                    case "status":
                        if (root.TryGetProperty("phase", out var phaseEl) && phaseEl.ValueKind == JsonValueKind.String)
                        {
                            var phase = phaseEl.GetString();
                            if (phase == "turn_start")
                            {
                                lock (_gate) _activity = SessionActivity.Running;
                                Emit(new SessionEvent.ActivityChanged(SessionActivity.Running));
                            }
                            else if (phase == "turn_end")
                            {
                                bool shouldEmitSettled;
                                lock (_gate)
                                {
                                    _activity = SessionActivity.Idle;
                                    _outcome = SessionOutcome.Completed;
                                    shouldEmitSettled = !turnSettledEmitted;
                                    turnSettledEmitted = true;
                                }
                                Emit(new SessionEvent.ActivityChanged(SessionActivity.Idle));
                                if (shouldEmitSettled) Emit(new SessionEvent.TurnSettled(SessionOutcome.Completed, null));
                            }
                        }
                        break;

                    case "text":
                        if (root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                        {
                            var text = textEl.GetString() ?? "";
                            if (!assistantEmitted)
                            {
                                assistantEmitted = true;
                                Emit(new SessionEvent.MessageStarted(messageId, MessageRole.Assistant, CurrentModel, 1));
                            }
                            assistantText.Append(text);
                            Emit(new SessionEvent.BlockFinished(messageId, 0, new TextBlock(text)));
                        }
                        break;

                    case "final":
                        if (root.TryGetProperty("text", out var finalTextEl) && finalTextEl.ValueKind == JsonValueKind.String)
                        {
                            var text = finalTextEl.GetString() ?? "";
                            if (!assistantEmitted)
                            {
                                assistantEmitted = true;
                                Emit(new SessionEvent.MessageStarted(messageId, MessageRole.Assistant, CurrentModel, 1));
                                assistantText.Append(text);
                                Emit(new SessionEvent.BlockFinished(messageId, 0, new TextBlock(text)));
                            }
                        }
                        break;
                }
            }
            catch (JsonException) { }
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (assistantEmitted)
        {
            var finalMsg = new ChatMessage(messageId, MessageRole.Assistant, CurrentModel,
                [new TextBlock(assistantText.ToString())], null, true);
            Emit(new SessionEvent.MessageFinished(messageId, finalMsg, null));
            RecordMessage(new PureAgentMessage(PureAgentRole.Assistant, assistantText.ToString()));
        }

        var stderr = await stderrTask.ConfigureAwait(false);

        lock (_gate)
        {
            _activity = SessionActivity.Idle;
            if (process.ExitCode != 0 && _outcome != SessionOutcome.Completed)
            {
                _outcome = SessionOutcome.Failed;
                _lastError = string.IsNullOrWhiteSpace(stderr) ? $"DSH 退出码: {process.ExitCode}" : stderr;
            }
            else
            {
                _outcome ??= SessionOutcome.Completed;
            }
        }

        if (!turnSettledEmitted)
        {
            Emit(new SessionEvent.ActivityChanged(SessionActivity.Idle));
            Emit(new SessionEvent.TurnSettled(_outcome ?? SessionOutcome.Completed, _lastError));
        }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _activity = SessionActivity.Idle;
                _outcome = SessionOutcome.Failed;
                _lastError = ex.Message;
            }
            if (!turnSettledEmitted)
            {
                turnSettledEmitted = true;
                Emit(new SessionEvent.ActivityChanged(SessionActivity.Idle));
                Emit(new SessionEvent.TurnSettled(SessionOutcome.Failed, ex.Message));
            }
        }
        finally
        {
            lock (_gate) _activeProcess = null;
            process.Dispose();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            try
            {
                if (_activeProcess is not null && !_activeProcess.HasExited)
                {
                    _activeProcess.Kill(entireProcessTree: true);
                }
            }
            catch { }
            _activity = SessionActivity.Idle;
            _outcome = SessionOutcome.Interrupted;
        }
        Emit(new SessionEvent.ActivityChanged(SessionActivity.Idle));
        Emit(new SessionEvent.TurnSettled(SessionOutcome.Interrupted, "已中止"));
        return Task.CompletedTask;
    }

    public Task RespondToExtensionUiAsync(ExtensionUiRequest request, PiExtensionUiResponse response,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    private void RecordMessage(PureAgentMessage message)
    {
        lock (_gate)
        {
            _history.Add(message);
            PersistHistory();
        }
    }

    public void RestoreHistory(IReadOnlyList<PureAgentMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        lock (_gate)
        {
            _history.Clear();
            _history.AddRange(messages);
        }
    }

    public IReadOnlyList<PureAgentMessage> GetHistoryMessages()
    {
        lock (_gate) return _history.ToArray();
    }

    private void PersistHistory()
    {
        if (string.IsNullOrEmpty(_persistFilePath)) return;
        try
        {
            var dir = Path.GetDirectoryName(_persistFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(new
            {
                version = 2,
                messages = _history,
                modelId = CurrentModel,
                sessionId = _sessionId
            }, new JsonSerializerOptions { WriteIndented = true });

            var temp = _persistFilePath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllText(temp, json);
            File.Move(temp, _persistFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Failed to persist DSH session: {0}", ex);
        }
    }

    private void Emit(SessionEvent ev) => Event?.Invoke(this, ev);

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            try
            {
                if (_activeProcess is not null && !_activeProcess.HasExited)
                    _activeProcess.Kill(entireProcessTree: true);
            }
            catch { }
            _activeProcess = null;
        }
    }
}
