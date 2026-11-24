using System.Text.Json;
using System.Threading.Channels;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Approvals;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Tui;

/// <summary>Owns one actual FUMO lease. No subprocess or external-backend selection path.</summary>
internal sealed class FumoTuiSession : ITuiSession
{
    private readonly PersistentSession _session;
    private readonly IExtensionUiSession _ui;
    private readonly SharedApprovalGate _approvals;
    private readonly Channel<TuiEvent> _events = Channel.CreateUnbounded<TuiEvent>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly FumoEventProjection _projection;
    private readonly object _lifecycle = new();
    private readonly object _approvalLock = new();
    private readonly HashSet<string> _visibleApprovals = new(StringComparer.Ordinal);
    private Task? _shutdown;
    private bool _closing;

    private FumoTuiSession(PersistentSession session, PluginPackageManager plugins)
    {
        _session = session;
        _ui = session.Chat as IExtensionUiSession
            ?? throw new InvalidOperationException("FUMO 会话没有原生审批入口。");
        _projection = new(_events.Writer);
        History = FumoEventProjection.ReadHistory(session.History);
        _approvals = SharedApprovalRegistry.For(_ui);
        _approvals.Requested += OnApproval;
        _approvals.Decided += OnDecision;
        _ui.ExtensionUiRequestReceived += OnNativeRequest;
        session.Chat.Event += OnSessionEvent;
        foreach (var (id, reason) in plugins.LoadErrors)
            _events.Writer.TryWrite(new TuiEvent.Message("plugin:" + id, "info", $"插件 {id}：{reason}"));
        if (session.Coordinator.Snapshot.LastResult == TaskResult.Interrupted)
            _events.Writer.TryWrite(new TuiEvent.Status("上次运行已中断", false));
    }

    public string Project => _session.Project.Path;
    public string Id => _session.Session.Id;
    public string? Model => _session.Chat.CurrentModel;
    public IReadOnlyList<TuiLine> History { get; }
    public ChannelReader<TuiEvent> Events => _events.Reader;

    public static async Task<FumoTuiSession> OpenAsync(string project, string? dataRoot, string? sessionId,
        bool resumeLatest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (sessionId is not null && resumeLatest)
            throw new ArgumentException("--session 与 --continue 不能同时使用。");
        project = AppDataLayout.NormalizeProjectPath(project);
        var layout = new AppDataLayout(dataRoot);
        FileChangeJournal.Configure(layout.Root);
        // null requests toolchain discovery; explicit empty paths disable it entirely.
        var manager = new PersistentSessionManager("", "", layout, ompExecutablePath: "",
            claudeExecutablePath: "", codexExecutablePath: "", dshExecutablePath: "");
        var selected = SelectSession(manager, project, sessionId, resumeLatest);
        var hadMessages = selected is not null && CheckPersistedHistory(layout, selected);
        var trust = manager.GetProjectTrustStatus(project);
        if (trust.RequiresDecision) throw new ProjectTrustDecisionRequiredException(trust);

        var state = new PluginStateStore(layout);
        var tools = new PluginToolRegistry(new PluginCapabilityGateway(state));
        var plugins = new PluginPackageManager(layout, tools, state);
        PersistentSession? opened = null;
        try
        {
            plugins.InitializeInstalledPlugins(project, seedDefaults: false);
            manager.NativePluginToolProviders = () => [tools];
            manager.NativeSystemPromptProvider = new PluginStateSystemPromptProvider(state);
            ct.ThrowIfCancellationRequested();
            opened = selected is null
                ? await manager.CreateAsync(project, backend: BackendId.NativeCSharp, cancellationToken: ct)
                : await manager.ResumeAsync(selected.Id, ct);
            // PersistentSession owns the chat and lease; the separate native coordinator also owns a model catalogue.
            opened.Disposables.Add(opened.Coordinator);
            opened.Disposables.Add(plugins);
            if (selected is not null && (!File.Exists(opened.Session.FilePath) ||
                (hadMessages && opened.History.Entries.Count == 0)))
                throw new InvalidDataException("FUMO 未能恢复已有历史；未打开替代会话。");
            if (selected is null && !File.Exists(opened.Session.FilePath) && opened.Chat is IExecutionModeSession modes)
            {
                // Core normally first persists on a turn. Even a newly opened empty TUI must be resumable;
                // ask the native serializer to persist its *existing* mode, never elect a permission mode here.
                await modes.SetExecutionModeAsync(modes.ExecutionMode, ct);
                if (!File.Exists(opened.Session.FilePath)) throw new IOException("未能保存新的 FUMO 会话。");
            }
            ct.ThrowIfCancellationRequested();
            return new FumoTuiSession(opened, plugins);
        }
        catch
        {
            if (opened is not null) await opened.DisposeAsync();
            else await plugins.DisposeAsync();
            throw;
        }
    }

    private static SessionRecord? SelectSession(PersistentSessionManager manager, string project,
        string? id, bool resumeLatest)
    {
        if (id is null && !resumeLatest) return null;
        var owner = manager.Metadata.TryGetProject(project);
        if (id is not null)
        {
            var record = manager.Metadata.GetSession(id);
            if (owner?.Id != record.ProjectId) throw new InvalidOperationException("所选会话不属于当前项目。");
            if (record.Backend != BackendId.NativeCSharp) throw new InvalidOperationException("所选会话不是 FUMO 会话。");
            return record;
        }
        // ListSessions is ordered by last_opened_utc. Do not silently start a replacement if this one fails.
        return (owner is null ? null : manager.Metadata.ListSessions(owner.Id)
            .FirstOrDefault(record => record.Backend == BackendId.NativeCSharp))
            ?? throw new InvalidOperationException("当前项目没有可继续的 FUMO 会话。");
    }

    private static bool CheckPersistedHistory(AppDataLayout layout, SessionRecord record)
    {
        // Core's recovery UI may quarantine corrupt files and reopen empty; an explicit terminal resume must
        // not mutate an unreadable transcript or claim that an empty replacement is the previous conversation.
        var path = NativeSessionFactory.SessionFilePath(layout, record.ProjectId, record.Id);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Path.GetFileName(record.Id) != record.Id || record.FilePath is null ||
            !string.Equals(Path.GetFullPath(path), Path.GetFullPath(record.FilePath), comparison))
            throw new InvalidDataException("FUMO 会话历史路径无效。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var parsed = PersistentSessionManager.ParseNativeMessages(stream) ?? throw new InvalidDataException("FUMO 会话历史不可读。");
        return parsed.Messages.Count != 0;
    }

    public Task SendAsync(string text, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            return _session.Chat.SendAsync(text, ct);
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            return _session.Chat.StopAsync(ct);
        }
    }

    public async Task RespondAsync(string id, bool allow, CancellationToken ct)
    {
        Task<SharedApprovalResolution> decision;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            ct.ThrowIfCancellationRequested();
            lock (_approvalLock)
                if (!_visibleApprovals.Contains(id)) throw new InvalidOperationException("审批不存在或已失效。");
            var request = _ui.PendingExtensionUiRequests.FirstOrDefault(r => r.Method == "confirm" &&
                string.Equals($"{r.Generation:N}:{r.Id}", id, StringComparison.Ordinal));
            if (request is null || DateTimeOffset.UtcNow >= request.ReceivedAt + request.Timeout)
                throw new InvalidOperationException("审批不存在或已失效。");
            decision = _approvals.DecideAsync(id, allow, "TUI", ct);
        }
        var result = await decision;
        if (!result.Accepted) throw new InvalidOperationException(result.Message ?? "审批已失效。");
    }

    private void OnApproval(SharedApprovalRequest request)
    {
        // The gate exposes only confirms. Never synthesize values for select/input/unknown requests.
        // Summary can be just a title: show the actual redacted request, not a blind yes/no prompt.
        using var data = JsonDocument.Parse(request.ArgsJson);
        var payload = data.RootElement;
        var text = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
            ? message.GetString() : null;
        lock (_approvalLock)
            if (_visibleApprovals.Add(request.ApprovalId))
                _events.Writer.TryWrite(new TuiEvent.Approval(request.ApprovalId,
                    string.IsNullOrWhiteSpace(text) ? $"{request.ToolName}\n{request.ArgsJson}" : text));
    }

    private void OnDecision(SharedApprovalResolution decision)
    {
        lock (_approvalLock)
            if (_visibleApprovals.Remove(decision.ApprovalId))
                _events.Writer.TryWrite(new TuiEvent.ApprovalClosed(decision.ApprovalId));
    }

    private void OnSessionEvent(object? sender, SessionEvent ev)
    {
        if (ev is SessionEvent.TurnSettled)
        {
            // Native tool cancellation and its approval waiter are independent tasks: Closed can race
            // behind TurnSettled. Invalidate the UI token before publishing the turn's terminal status.
            lock (_approvalLock)
            {
                foreach (var id in _visibleApprovals) _events.Writer.TryWrite(new TuiEvent.ApprovalClosed(id));
                _visibleApprovals.Clear();
            }
        }
        _projection.OnEvent(sender, ev);
    }

    private void OnNativeRequest(ExtensionUiRequest request)
    {
        if (request.Method != "confirm") _ = DeclineUnsupportedAsync(request);
    }

    private async Task DeclineUnsupportedAsync(ExtensionUiRequest request)
    {
        string message;
        try
        {
            await _ui.RespondToExtensionUiAsync(request, PiExtensionUiResponse.Cancel());
            message = $"不支持的交互请求已取消：{request.Method}";
        }
        catch (Exception error) { message = $"无法应答交互请求 {request.Method}：{error.Message}"; }
        _events.Writer.TryWrite(new TuiEvent.Message("request:" + request.Id, "error", message));
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle) return new ValueTask(_shutdown ??= ShutdownAsync());
    }

    private async Task ShutdownAsync()
    {
        _closing = true;
        // Losing the terminal must not remove the native approval listener while tools can still run.
        try { await _session.Chat.StopAsync(CancellationToken.None); }
        finally
        {
            try { await _session.DisposeAsync(); }
            finally
            {
                _session.Chat.Event -= OnSessionEvent;
                _ui.ExtensionUiRequestReceived -= OnNativeRequest;
                _approvals.Requested -= OnApproval;
                _approvals.Decided -= OnDecision;
                _approvals.Dispose();
                _events.Writer.TryComplete();
            }
        }
    }
}
