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
/// Codex 官方 CLI 的协议适配会话：把 `codex exec --json` 输出的事件流
/// 规范化为统一的 IChatSession 契约与 SessionEvent 事件，并维护本地历史与 --resume 续接。
/// </summary>
public sealed class CodexChatSession : IChatSession, IBackendChatSession, IExtensionUiSession, IPresentationProfiled, IAsyncDisposable, IDisposable
{
    private readonly string _executablePath;
    private readonly string _workingDirectory;
    private readonly string? _persistFilePath;
    private readonly string? _model;
    private readonly object _gate = new();
    private readonly List<PureAgentMessage> _history = [];
    private string? _threadId;
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

    public CodexChatSession(string executablePath, string workingDirectory,
        string? persistFilePath = null, string? initialThreadId = null, string? model = null)
    {
        _executablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
        _workingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
        _persistFilePath = persistFilePath;
        _threadId = initialThreadId;
        _model = model;
    }

    public string BackendId => "codex";
    public BackendPresentationProfile Presentation => BackendPresentationProfile.Codex;
    public string BackendName => "Codex";
    public SessionConnection Connection { get { lock (_gate) return _connection; } }
    public SessionActivity Activity { get { lock (_gate) return _activity; } }
    public SessionOutcome? LastOutcome { get { lock (_gate) return _outcome; } }
    public string? CurrentModel => _model ?? "codex";
    public string? LastError { get { lock (_gate) return _lastError; } }
    public bool CanSend { get { lock (_gate) return _connection == SessionConnection.Connected && _activity == SessionActivity.Idle; } }
    public bool CanStop { get { lock (_gate) return _activeProcess is not null && !_activeProcess.HasExited; } }
    public int QueuedCount => 0;

    public string? SessionId { get { lock (_gate) return _threadId; } }

    public IReadOnlyList<ExtensionUiRequest> PendingExtensionUiRequests => [];
    public ApprovalAvailability ApprovalAvailability => ApprovalAvailability.Unavailable;
    public string? ApprovalDiagnostic => "Codex CLI 以 bypass 模式运行；审批桥尚未接入。";

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (_gate)
        {
            if (_connection != SessionConnection.Connected)
                throw new InvalidOperationException("Codex 会话尚未就绪。");
            if (_activity != SessionActivity.Idle)
                throw new InvalidOperationException("当前回合仍在进行。");
            _activity = SessionActivity.Running;
            _outcome = null;
            _lastError = null;
        }

        RecordMessage(new PureAgentMessage(PureAgentRole.User, text));
        Emit(new SessionEvent.ActivityChanged(SessionActivity.Running));

        var args = new List<string>();
        string currentId;
        lock (_gate) currentId = _threadId ?? string.Empty;

        if (string.IsNullOrEmpty(currentId))
        {
            args.AddRange(["exec", "--skip-git-repo-check", "--dangerously-bypass-approvals-and-sandbox", "--json"]);
            if (!string.IsNullOrWhiteSpace(_model)) args.AddRange(["-m", _model]);
            args.Add(text);
        }
        else
        {
            args.AddRange(["exec", "resume", currentId, "--skip-git-repo-check", "--dangerously-bypass-approvals-and-sandbox", "--json"]);
            if (!string.IsNullOrWhiteSpace(_model)) args.AddRange(["-m", _model]);
            args.Add(text);
        }

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
            var stderrTask = Task.Run(
                () => ReadStderrTailAsync(process.StandardError, cancellationToken), cancellationToken);

            var messageId = Guid.NewGuid().ToString("N");
            var assistantEmitted = false;
            var assistantText = new System.Text.StringBuilder();

            using var reader = process.StandardOutput;
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeEl)) continue;
                    var type = typeEl.GetString();

                    switch (type)
                    {
                        case "thread.started":
                            if (root.TryGetProperty("thread_id", out var tidEl) && tidEl.ValueKind == JsonValueKind.String)
                            {
                                var tid = tidEl.GetString()!;
                                lock (_gate) _threadId = tid;
                                SessionIdChanged?.Invoke(tid);
                            }
                            break;

                        case "turn.started":
                            lock (_gate) _activity = SessionActivity.Running;
                            Emit(new SessionEvent.ActivityChanged(SessionActivity.Running));
                            break;

                        case "item.completed":
                            if (root.TryGetProperty("item", out var itemEl) && itemEl.ValueKind == JsonValueKind.Object)
                            {
                                var itemType = itemEl.TryGetProperty("type", out var itEl) ? itEl.GetString() : null;
                                if (itemType == "agent_message" && itemEl.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
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
                            }
                            break;

                        case "turn.completed":
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
                            break;

                        case "error":
                            if (root.TryGetProperty("message", out var errEl) && errEl.ValueKind == JsonValueKind.String)
                            {
                                var errText = errEl.GetString() ?? "Codex error";
                                bool shouldEmitErrorSettled;
                                lock (_gate)
                                {
                                    _lastError = errText;
                                    _outcome = SessionOutcome.Failed;
                                    shouldEmitErrorSettled = !turnSettledEmitted;
                                    turnSettledEmitted = true;
                                }
                                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                                Emit(new SessionEvent.SessionError(SessionErrorKind.CommandRejected, errText, null));
                                Emit(new SessionEvent.ActivityChanged(SessionActivity.Idle));
                                if (shouldEmitErrorSettled) Emit(new SessionEvent.TurnSettled(SessionOutcome.Failed, errText));
                            }
                            break;
                    }
                }
                catch (JsonException) { }
            }

            try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); } catch { }

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
                    _lastError = string.IsNullOrWhiteSpace(stderr) ? $"Codex 退出码: {process.ExitCode}" : stderr;
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
            AtomicJsonFile.Write(_persistFilePath, writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", 2);
                writer.WritePropertyName("messages");
                JsonSerializer.Serialize(writer, _history);
                if (_model is null) writer.WriteNull("modelId");
                else writer.WriteString("modelId", _model);
                if (_threadId is null) writer.WriteNull("sessionId");
                else writer.WriteString("sessionId", _threadId);
                writer.WriteEndObject();
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Failed to persist Codex session: {0}", ex);
        }
    }

    /// <summary>
    /// 读取 stderr 但只保留尾部：持续读取以排空管道，内存上限约 16 KB，
    /// 避免异常子进程输出海量日志时把整个输出留在内存里。
    /// </summary>
    private static async Task<string> ReadStderrTailAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int maxCharacters = 16 * 1024;
        var lines = new Queue<string>();
        var retained = 0;
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                lines.Enqueue(line);
                retained += line.Length + 1;
                while (retained > maxCharacters && lines.Count > 0)
                    retained -= lines.Dequeue().Length + 1;
            }
        }
        catch
        {
            // 子进程被终止或流关闭：保留已经读取的尾部即可。
        }
        return string.Join('\n', lines);
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
