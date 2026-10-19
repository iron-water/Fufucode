using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Plugins;

namespace Fufucode.Core.Backend;

/// <summary>将原生引擎的流式文本、工具及轮次边界映射到既有桌面会话事件。</summary>
public sealed class NativeCSharpChatSession : IChatSession, IBackendChatSession, IExecutionModeSession,
    IChatAttachments, IPromptHandledSession, IExtensionUiSession, IPresentationProfiled, IAsyncDisposable, IDisposable
{
    private readonly IPureCSharpAgentEngine _engine;
    private readonly IPluginSystemPromptProvider? _systemPromptProvider;
    private readonly IPluginToolCatalog? _pluginTools;
    private readonly SkillRegistry? _skills;
    private readonly McpManager? _mcp;
    private readonly SubagentRunner? _subagents;
    private readonly object _gate = new();
    private readonly object _persistGate = new();
    private readonly List<(string Text, IReadOnlyList<PureImageAttachment>? Images)> _queue = [];
    private readonly List<string> _steerQueue = [];
    private string? _modelId;
    private CancellationTokenSource? _activeCts;
    private Task _activeTurn = Task.CompletedTask;
    private SessionActivity _activity = SessionActivity.Idle;
    private SessionOutcome? _lastOutcome;
    private bool _stopRequested;
    private bool _disposed;

    private readonly string? _persistFilePath;

    public bool SupportsExecutionModes => true;
    public string ExecutionMode { get; private set; } = "manual";
    public event Action<string>? ExecutionModeChanged;
    private readonly bool _supportsImages;
    public bool CanSendImages => _supportsImages;
    public event Action? PromptHandled;

    // ── 会话内工具审批（复用扩展 UI 请求通道，无外部审批桥） ──
    private readonly Dictionary<string, TaskCompletionSource<bool>> _pendingApprovals = new(StringComparer.Ordinal);
    private readonly List<ExtensionUiRequest> _pendingUi = [];

    public IReadOnlyList<ExtensionUiRequest> PendingExtensionUiRequests
    {
        get { lock (_gate) return _pendingUi.ToArray(); }
    }

    public ApprovalAvailability ApprovalAvailability => ApprovalAvailability.Unavailable;
    public string? ApprovalDiagnostic => "FUMO 使用会话内确认（无外部审批桥）；非交互环境自动放行。";
    public event Action<ExtensionUiRequest>? ExtensionUiRequestReceived;
    public event Action<ExtensionUiRequest>? ExtensionUiRequestClosed;
    public event Action<PiExtensionUiRequestEvent>? ExtensionUiNotification { add { } remove { } }
    public event Action<PiExtensionErrorEvent>? ExtensionError { add { } remove { } }

    public Task RespondToExtensionUiAsync(ExtensionUiRequest request, PiExtensionUiResponse response,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<bool>? waiter;
        lock (_gate) _pendingApprovals.TryGetValue(request.Id, out waiter);
        if (waiter is null) throw new InvalidOperationException("审批请求已失效。");

        var approved = response.Kind switch
        {
            PiExtensionUiResponseKind.Confirmation => response.Confirmed == true,
            PiExtensionUiResponseKind.Value => true,
            _ => false
        };
        waiter.TrySetResult(approved);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 工具执行前审批：manual 询问所有变更工具；auto 仅询问 Shell；
    /// full 直接放行；非交互（无订阅者）时放行以保持 CLI 可用。
    /// </summary>
    private async Task<bool> RequestApprovalAsync(PureToolCall call, CancellationToken cancellationToken)
    {
        var mode = ExecutionMode;
        if (mode == "full") return true;
        if (mode == "auto" && !string.Equals(call.Name, "run_command", StringComparison.Ordinal)) return true;

        Action<ExtensionUiRequest>? notify;
        ExtensionUiRequest request;
        TaskCompletionSource<bool> waiter;
        lock (_gate)
        {
            notify = ExtensionUiRequestReceived;
            if (notify is null) return true;

            var id = Guid.NewGuid().ToString("N");
            var payload = JsonSerializer.SerializeToElement(new
            {
                title = "工具执行确认",
                message = $"是否允许执行工具 {call.Name}？\n{call.ArgumentsJson}",
                toolName = call.Name,
                arguments = call.ArgumentsJson
            });
            request = new ExtensionUiRequest(Guid.NewGuid(), id, "confirm", payload,
                DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60));
            waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingApprovals[id] = waiter;
            _pendingUi.Add(request);
        }

        notify(request);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await waiter.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false; // 超时或回合取消 = 拒绝
        }
        finally
        {
            lock (_gate)
            {
                _pendingApprovals.Remove(request.Id);
                _pendingUi.Remove(request);
            }
            ExtensionUiRequestClosed?.Invoke(request);
        }
    }

    public Task SetExecutionModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        if (mode is not ("manual" or "auto" or "full" or "plan"))
            throw new ArgumentException($"未知执行模式：'{mode}'。", nameof(mode));

        lock (_gate)
        {
            ExecutionMode = mode;
        }

        var permMode = mode switch
        {
            "plan" => PermissionMode.Plan,
            "auto" => PermissionMode.AcceptEdits,
            "full" => PermissionMode.BypassPermissions,
            _ => PermissionMode.Default,
        };

        if (_engine is PureCSharpAgentEngine pureEngine)
        {
            pureEngine.Mode = permMode;
        }

        ExecutionModeChanged?.Invoke(mode);
        return Task.CompletedTask;
    }

    public Task SendWithImagesAsync(string text, IReadOnlyList<ChatImage> images, CancellationToken cancellationToken = default)
    {
        var attachments = images?.Select(i => new PureImageAttachment(i.Data, i.MimeType)).ToArray();
        return SendCoreAsync(text, attachments, cancellationToken);
    }

    public void SetModel(string modelId)
    {
        lock (_gate)
        {
            _modelId = modelId;
        }
    }

    private volatile string _thinkingLevel = "off";

    /// <summary>设置推理强度（off/low/medium/high/max）；下一轮请求生效。</summary>
    public void SetThinkingLevel(string level)
    {
        _thinkingLevel = string.IsNullOrWhiteSpace(level) ? "off" : level;
    }

    public string ThinkingLevel => _thinkingLevel;

    /// <summary>恢复会话时标记“上次运行被中断”，供 UI 提示。</summary>
    public void MarkInterrupted()
    {
        lock (_gate) _lastOutcome = SessionOutcome.Interrupted;
    }

    public NativeCSharpChatSession(
        IPureCSharpAgentEngine engine,
        string? sessionId = null,
        string? modelId = null,
        IPluginSystemPromptProvider? systemPromptProvider = null,
        IPluginToolCatalog? pluginTools = null,
        string? persistFilePath = null,
        bool supportsImages = true,
        SkillRegistry? skills = null,
        McpManager? mcp = null,
        SubagentRunner? subagents = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _supportsImages = supportsImages;
        _skills = skills;
        _mcp = mcp;
        _subagents = subagents;
        _systemPromptProvider = systemPromptProvider;
        _pluginTools = pluginTools;
        _persistFilePath = persistFilePath;
        SessionId = sessionId ?? Guid.NewGuid().ToString("N");
        _modelId = modelId ?? engine.GetState().ModelId;

        if (engine is PureCSharpAgentEngine pureEngine)
        {
            pureEngine.ApprovalGate = RequestApprovalAsync;
            pureEngine.TakeSteering = TakeSteeringMessage;
            if (_subagents is not null) _subagents.ApprovalGate = pureEngine.ApprovalGate;
        }
    }

    /// <summary>便利构造；仍通过同一 PureCSharpAgentEngine 保存历史和驱动工具循环。</summary>
    public NativeCSharpChatSession(
        IPureModelClient modelClient,
        IPureToolDispatcher? toolDispatcher = null,
        int maxIterations = 10,
        string? sessionId = null,
        string? modelId = null,
        IPluginSystemPromptProvider? systemPromptProvider = null,
        IPluginToolCatalog? pluginTools = null,
        string? persistFilePath = null,
        bool supportsImages = true,
        SkillRegistry? skills = null,
        McpManager? mcp = null,
        SubagentRunner? subagents = null)
        : this(new PureCSharpAgentEngine(toolDispatcher, modelClient, maxIterations), sessionId, modelId,
            systemPromptProvider, pluginTools, persistFilePath, supportsImages, skills, mcp, subagents)
    {
        ArgumentNullException.ThrowIfNull(modelClient);
    }

    /// <summary>当前会话使用的插件人格提供方；未接线时为 null（不注入任何插件人格）。</summary>
    public IPluginSystemPromptProvider? SystemPromptProvider => _systemPromptProvider;

    /// <summary>插件工具注册表；未接线时为 null（只声明内置文件/目录/命令工具）。</summary>
    public IPluginToolCatalog? PluginTools => _pluginTools;

    /// <summary>技能注册表；未接线时为 null（不声明 skill 工具）。</summary>
    public SkillRegistry? Skills => _skills;

    /// <summary>MCP 服务器管理器；未配置服务器时为 null 或不含服务器。</summary>
    public McpManager? Mcp => _mcp;

    /// <summary>子代理运行器；未接线时为 null（不声明 subagent 工具）。</summary>
    public SubagentRunner? Subagents => _subagents;

    /// <summary>内置工具集合：读取、写入、精确定位编辑、目录列表、正则检索、通配发现、命令执行与任务计划。</summary>
    private static readonly string[] BuiltInToolNames =
        ["read_file", "write_file", "edit_file", "list_dir", "grep_search", "glob_files", "run_command", "update_plan"];

    /// <summary>
    /// 每次请求重新计算可声明工具：内置工具 + 当前门禁放行的插件工具。
    /// 插件停用/卸载后 <see cref="PluginToolRegistry.ActiveToolNames"/> 立即为空，
    /// 下一次请求不再向模型声明这些工具（不留幽灵工具）。
    /// </summary>
    private IReadOnlyList<string> BuildToolNames()
    {
        var merged = new List<string>(BuiltInToolNames);
        if (_skills?.HasModelInvocableSkills == true) merged.Add(SkillRegistry.ToolName);
        if (_mcp?.HasServers == true) merged.Add(McpGatewayDispatcher.ToolName);
        if (_subagents is not null) merged.Add(SubagentDispatcher.ToolName);

        if (_pluginTools is not { } pluginTools) return merged;
        foreach (var name in pluginTools.ActiveToolNames)
        {
            if (!merged.Contains(name, StringComparer.Ordinal)) merged.Add(name);
        }
        return merged;
    }

    /// <summary>构建完整的工具动作契约清单（内置 + 技能 + 插件工具的 JSON Schema）。</summary>
    private IReadOnlyList<PureToolDefinition> BuildToolDefinitions()
    {
        var list = new List<PureToolDefinition>();
        list.AddRange(ProductionToolDispatcher.GetBuiltinToolDefinitions());
        if (_mcp?.HasServers == true) list.Add(McpGatewayDispatcher.Definition);
        if (_subagents is not null) list.Add(SubagentDispatcher.Definition);
        if (_skills?.HasModelInvocableSkills == true)
        {
            list.Add(new PureToolDefinition(SkillRegistry.ToolName,
                "加载指定技能的完整操作说明（SKILL.md）。技能通常描述如何用 CLI/API 完成某类任务。",
                """
                {
                    "type": "object",
                    "properties": {
                        "name": { "type": "string", "description": "技能名称；省略时列出可用技能" }
                    }
                }
                """));
        }
        if (_pluginTools is { } pt)
        {
            foreach (var reg in pt.ActiveRegistrations)
            {
                list.Add(new PureToolDefinition(reg.Name, reg.Description, reg.ParametersJsonSchema));
            }
        }
        return list;
    }

    /// <summary>
    /// 组装本轮发往模型的消息：启用插件提供人格时，在最前面追加一条 System 消息；
    /// 没有启用插件提供人格时保持原有单条 User 消息，不注入空 System。支持携带多模态图像附件与计划提示。
    /// </summary>
    private IReadOnlyList<PureAgentMessage> BuildTurnMessages(string text, IReadOnlyList<PureImageAttachment>? images = null)
    {
        var fragment = _systemPromptProvider?.Current;
        var planSnippet = GetCurrentPlanSnippet();
        var skillsSection = _skills?.BuildPromptSection();
        var systemContent = fragment;

        void AppendSection(string? section)
        {
            if (string.IsNullOrWhiteSpace(section)) return;
            systemContent = string.IsNullOrWhiteSpace(systemContent) ? section : $"{systemContent}\n\n{section}";
        }

        AppendSection(planSnippet);
        AppendSection(skillsSection);
        AppendSection(_mcp?.BuildPromptSection());
        AppendSection(_subagents?.BuildPromptSection());

        // 用户显式 /skill:name：把技能正文注入本轮用户消息。
        var effectiveText = _skills?.ExpandUserInvocation(text) ?? text;
        var userMsg = new PureAgentMessage(PureAgentRole.User, effectiveText, Images: images);
        if (string.IsNullOrWhiteSpace(systemContent))
            return [userMsg];
        return
        [
            new PureAgentMessage(PureAgentRole.System, systemContent),
            userMsg,
        ];
    }

    private string? GetCurrentPlanSnippet() => (_engine as PureCSharpAgentEngine)?.CurrentPlanSnippet;

    public bool IsStreaming => _engine is IObservablePureCSharpAgentEngine { StreamingEnabled: true };
    public string SessionId { get; }
    public string BackendId => "fumo";
    public string BackendName => "FUMO";
    public BackendPresentationProfile Presentation => BackendPresentationProfile.Fumo;
    public SessionConnection Connection { get { lock (_gate) return _disposed ? SessionConnection.Stopped : SessionConnection.Connected; } }
    public SessionActivity Activity { get { lock (_gate) return _activity; } }
    public SessionOutcome? LastOutcome { get { lock (_gate) return _lastOutcome; } }
    public string? CurrentModel => _modelId;
    public bool CanSend { get { lock (_gate) return !_disposed && _activity == SessionActivity.Idle; } }
    public bool CanStop { get { lock (_gate) return _activity is SessionActivity.Running or SessionActivity.Cancelling || _queue.Count > 0; } }
    public int QueuedCount { get { lock (_gate) return _queue.Count; } }
    public event EventHandler<SessionEvent>? Event;

    /// <summary>
    /// 接受后返回，运行终态通过 TurnSettled 通知。运行中再次发送会进入队列，
    /// 当前回合结束后自动执行（与 Pi/OMP 的 FollowUp 语义一致）。
    /// </summary>
    public Task SendAsync(string text, CancellationToken cancellationToken = default) =>
        SendCoreAsync(text, null, cancellationToken);

    /// <summary>运行中插队纠偏：在下一轮模型请求之前注入一条用户消息。空闲时退化为普通发送。</summary>
    public Task SteerAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        bool running;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            running = _activity is SessionActivity.Running or SessionActivity.Compacting or SessionActivity.Retrying;
            if (running) _steerQueue.Add(text);
        }
        if (!running) return SendCoreAsync(text, null, cancellationToken);

        Emit(new SessionEvent.QueueChanged(QueuedTexts().Length, QueuedTexts()));
        return Task.CompletedTask;
    }

    internal string? TakeSteeringMessage()
    {
        lock (_gate)
        {
            if (_steerQueue.Count == 0) return null;
            var text = _steerQueue[0];
            _steerQueue.RemoveAt(0);
            return text;
        }
    }

    private string[] QueuedTexts()
    {
        lock (_gate) return _queue.Select(entry => entry.Text).ToArray();
    }

    private Task SendCoreAsync(string text, IReadOnlyList<PureImageAttachment>? images, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        bool queued;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activity == SessionActivity.Cancelling)
                throw new InvalidOperationException("正在停止当前回合，暂不接受新消息。");
            queued = _activity != SessionActivity.Idle;
            if (queued)
            {
                _queue.Add((text, images));
            }
            else
            {
                StartTurnLocked(text, images, cancellationToken);
            }
        }

        if (queued) Emit(new SessionEvent.QueueChanged(QueuedTexts().Length, QueuedTexts()));
        return Task.CompletedTask;
    }

    private void StartTurnLocked(string text, IReadOnlyList<PureImageAttachment>? images,
        CancellationToken cancellationToken)
    {
        _stopRequested = false;
        _activity = SessionActivity.Running;
        _activeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cts = _activeCts;
        _activeTurn = Task.Run(() => RunTurnAsync(text, images, cts), CancellationToken.None);
    }

    private async Task RunTurnAsync(string text, IReadOnlyList<PureImageAttachment>? images, CancellationTokenSource cts)
    {
        var recorder = new RoundRecorder(this);
        var outcome = SessionOutcome.Failed;
        string? summary = null;
        try
        {
            Emit(new SessionEvent.ActivityChanged(SessionActivity.Running));
            ReplayMessage(MessageRole.User, text);
            // 会话构建请求时动态读取当前启用插件的人格片段（停用插件后下一次请求立即不再携带）。
            var request = new PureModelRequest(
                BuildTurnMessages(text, images),
                BuildToolNames(),
                _modelId,
                BuildToolDefinitions(),
                _thinkingLevel == "off" ? null : _thinkingLevel);
            PureAgentTurnResult result;
            if (_engine is IObservablePureCSharpAgentEngine observable)
            {
                result = await observable.RunObservedTurnAsync(request, new NativeAgentCallbacks
                {
                    Stream = new ModelStreamCallbacks
                    {
                        OnTextDelta = recorder.OnTextDelta,
                        OnThinkingDelta = recorder.OnThinkingDelta,
                        OnUsage = recorder.OnUsage,
                        OnToolCallChunkIndexed = recorder.OnToolCallChunk,
                    },
                    OnMessageAppended = recorder.OnMessageAppended,
                    OnToolStarting = recorder.OnToolStarting,
                    OnToolCompleted = recorder.OnToolCompleted,
                }, cts.Token).ConfigureAwait(false);
            }
            else
            {
                result = await _engine.RunTurnAsync(request, cts.Token).ConfigureAwait(false);
                if (result.Usage is { } usage) recorder.OnUsage(usage);
                // 旧实现没有中间通知能力，按真实聚合结果生成完整块，避免发往不存在消息的状态事件。
                recorder.OnMessageAppended(new PureAgentMessage(
                    PureAgentRole.Assistant, result.Response.Text ?? "", ToolCalls: result.Response.ToolCalls));
                foreach (var toolResult in result.ToolResults)
                    recorder.OnLegacyToolCompleted(toolResult);
            }
            outcome = result.Status == PureAgentTurnStatus.Failed ? SessionOutcome.Failed : SessionOutcome.Completed;
            summary = result.Error ?? (outcome == SessionOutcome.Completed ? "已完成" : "模型请求失败。");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            outcome = SessionOutcome.Interrupted;
            summary = "已中断";
        }
        catch (Exception error)
        {
            summary = error.Message;
        }
        finally
        {
            recorder.Finish(outcome);
            PersistHistory();

            (string Text, IReadOnlyList<PureImageAttachment>? Images)? next = null;
            lock (_gate)
            {
                _activity = SessionActivity.Idle;
                _lastOutcome = outcome;
                _activeCts = null;
                cts.Dispose();
                if (!_stopRequested && !_disposed && _queue.Count > 0)
                {
                    next = _queue[0];
                    _queue.RemoveAt(0);
                }
            }

            // 事件在锁外派发：订阅者可以安全地再次调用会话方法，不会被持锁回调卡住。
            if (outcome == SessionOutcome.Failed)
                Emit(new SessionEvent.SessionError(SessionErrorKind.ModelFailed, summary ?? "模型请求失败。", null));
            Emit(new SessionEvent.ActivityChanged(SessionActivity.Idle));
            Emit(new SessionEvent.TurnSettled(outcome, summary));
            PromptHandled?.Invoke();

            if (next is { } pending)
            {
                lock (_gate)
                {
                    if (!_disposed && !_stopRequested)
                    {
                        StartTurnLocked(pending.Text, pending.Images, CancellationToken.None);
                    }
                    else
                    {
                        next = null;
                    }
                }
                Emit(new SessionEvent.QueueChanged(QueuedTexts().Length, QueuedTexts()));
            }
        }
    }

    private void PersistHistory()
    {
        if (string.IsNullOrEmpty(_persistFilePath)) return;
        var history = _engine.GetState().History;
        if (history is not { Count: > 0 }) return;

        try
        {
            lock (_persistGate)
            {
                var dir = Path.GetDirectoryName(_persistFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(new
                    {
                        version = 2,
                        messages = history,
                        modelId = _modelId,
                        thinkingLevel = _thinkingLevel,
                        executionMode = ExecutionMode
                    },
                    new JsonSerializerOptions { WriteIndented = true });
                var temp = _persistFilePath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
                File.WriteAllText(temp, json);
                File.Move(temp, _persistFilePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Failed to persist FUMO session: {0}", ex);
        }
    }

    /// <summary>
    /// 从持久化历史恢复引擎上下文。桌面时间线由 <see cref="PersistentSessionManager"/> 生成的
    /// SessionHistory 回放，这里不再发出重复事件（恢复发生在 UI 订阅之前）。
    /// </summary>
    public void RestoreHistory(IReadOnlyList<PureAgentMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _engine.RestoreHistory(messages);
    }

    /// <summary>当前引擎中的完整历史快照（用于会话条目查询与分叉）。</summary>
    public IReadOnlyList<PureAgentMessage> GetHistoryMessages() =>
        _engine.GetState().History ?? Array.Empty<PureAgentMessage>();

    private void ReplayMessage(MessageRole role, string text)
    {
        var id = Guid.NewGuid().ToString("N");
        Emit(new SessionEvent.MessageStarted(id, role, role == MessageRole.Assistant ? CurrentModel : null, 1));
        Emit(new SessionEvent.BlockStarted(id, 0, MessageBlockKind.Text, null, null));
        Emit(new SessionEvent.TextAppended(id, 0, text));
        Emit(new SessionEvent.BlockFinished(id, 0, new TextBlock(text)));
        Emit(new SessionEvent.MessageFinished(id, new ChatMessage(id, role, null, [new TextBlock(text)], null, true), null));
    }

    // 订阅者异常不应被模型客户端当作运输错误，更不能打断工具或阻止其他订阅者收到终态。
    // 派发前复制订阅者列表，避免在回调期间持有会话锁。
    private void Emit(SessionEvent value)
    {
        EventHandler<SessionEvent>? handlers;
        lock (_gate) handlers = Event;
        if (handlers is null) return;

        foreach (EventHandler<SessionEvent> handler in handlers.GetInvocationList())
        {
            try { handler(this, value); }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Native session subscriber: {0}", error); }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task turn;
        CancellationTokenSource? cts;
        string[] cleared;
        lock (_gate)
        {
            if (_disposed) return;
            turn = _activeTurn;
            _stopRequested = true;
            cleared = _queue.Select(entry => entry.Text).ToArray();
            _queue.Clear();
            _steerQueue.Clear();
            cts = _activeCts;
            if (cts is not null && _activity != SessionActivity.Cancelling)
                _activity = SessionActivity.Cancelling;
        }

        if (cleared.Length > 0)
        {
            Emit(new SessionEvent.QueueCleared(cleared));
            Emit(new SessionEvent.QueueChanged(0, []));
        }
        if (cts is not null)
        {
            Emit(new SessionEvent.ActivityChanged(SessionActivity.Cancelling));
            cts.Cancel();
        }

        await turn.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _activeCts?.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task turn;
        lock (_gate) turn = _activeTurn;
        await turn.ConfigureAwait(false);
        if (_mcp is not null)
            await _mcp.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>每个模型轮次独立建消息，工具调用通过轮次内索引关联，执行结果通过真实调用 ID 关联。</summary>
    private sealed class RoundRecorder(NativeCSharpChatSession session)
    {
        private string? _messageId;
        private readonly List<MessageBlock> _blocks = new();
        private int? _textIndex;
        private readonly StringBuilder _text = new();
        private int? _thinkingIndex;
        private readonly StringBuilder _thinking = new();
        private readonly Dictionary<int, ToolState> _toolsByIndex = new();
        private readonly Dictionary<string, ToolState> _toolsById = new(StringComparer.Ordinal);
        private UsageInfo? _usage;
        private bool _assistantAppended;

        public void OnUsage(PureUsage usage) => _usage = new UsageInfo
        {
            InputTokens = (int?)usage.InputTokens,
            OutputTokens = (int?)usage.OutputTokens,
            CacheReadTokens = (int?)usage.CacheReadTokens,
            CacheWriteTokens = (int?)usage.CacheWriteTokens,
        };

        public void OnThinkingDelta(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            EnsureMessage();
            if (_thinkingIndex is null)
            {
                _thinkingIndex = _blocks.Count;
                _blocks.Add(new ThinkingBlock(""));
                session.Emit(new SessionEvent.BlockStarted(_messageId!, _thinkingIndex.Value, MessageBlockKind.Thinking, null, null));
            }
            _thinking.Append(delta);
            _blocks[_thinkingIndex.Value] = new ThinkingBlock(_thinking.ToString());
            session.Emit(new SessionEvent.ThinkingAppended(_messageId!, _thinkingIndex.Value, delta));
        }

        public void OnTextDelta(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            EnsureMessage();
            if (_textIndex is null)
            {
                _textIndex = _blocks.Count;
                _blocks.Add(new TextBlock(""));
                session.Emit(new SessionEvent.BlockStarted(_messageId!, _textIndex.Value, MessageBlockKind.Text, null, null));
            }
            _text.Append(delta);
            _blocks[_textIndex.Value] = new TextBlock(_text.ToString());
            session.Emit(new SessionEvent.TextAppended(_messageId!, _textIndex.Value, delta));
        }

        public void OnToolCallChunk(int index, string? id, string? name, string argumentsFragment)
        {
            EnsureMessage();
            if (!_toolsByIndex.TryGetValue(index, out var tool))
            {
                tool = new ToolState(_blocks.Count, id ?? $"call_{index}", name ?? "");
                _toolsByIndex.Add(index, tool);
                _blocks.Add(tool.Block);
                session.Emit(new SessionEvent.BlockStarted(
                    _messageId!, tool.ContentIndex, MessageBlockKind.ToolCall, tool.Id, tool.Name));
            }
            if (!string.IsNullOrEmpty(id) && !tool.HasExplicitId)
            {
                tool.Id = id;
                tool.HasExplicitId = true;
            }
            if (!string.IsNullOrEmpty(name) && tool.Name.Length == 0) tool.Name = name;
            tool.Arguments.Append(argumentsFragment);
            _blocks[tool.ContentIndex] = tool.Block;
            session.Emit(new SessionEvent.ToolCallAppended(_messageId!, tool.ContentIndex, tool.Id, argumentsFragment));
        }

        public void OnMessageAppended(PureAgentMessage message)
        {
            if (message.Role != PureAgentRole.Assistant) return;
            EnsureMessage();
            // 非流式旧客户端/轮次上限提示未必产生 delta：仅在缺失时补一条真实聚合文本。
            if (_textIndex is null && message.Content.Length > 0) OnTextDelta(message.Content);
            if (message.ToolCalls is { } calls)
            {
                var streamedTools = _toolsByIndex.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
                for (var i = 0; i < calls.Count; i++)
                {
                    var call = calls[i];
                    ToolState tool;
                    if (i < streamedTools.Length)
                    {
                        tool = streamedTools[i];
                    }
                    else
                    {
                        var index = _toolsByIndex.Count == 0 ? 0 : _toolsByIndex.Keys.Max() + 1;
                        OnToolCallChunk(index, call.Id, call.Name, call.ArgumentsJson);
                        tool = _toolsByIndex[index];
                    }
                    tool.Id = call.Id;
                    tool.Name = call.Name;
                    tool.Arguments.Clear().Append(call.ArgumentsJson);
                    _toolsById[call.Id] = tool;
                    _blocks[tool.ContentIndex] = tool.Block;
                }
            }
            _assistantAppended = true;
            // 工具轮等所有真实工具结果到齐后关闭，最终文本轮立即关闭。
            if (_toolsByIndex.Count == 0) CloseMessage();
        }

        public void OnToolStarting(PureToolCall call)
        {
            if (!_toolsById.TryGetValue(call.Id, out var tool)) return;
            tool.Status = ToolCallStatus.Running;
            _blocks[tool.ContentIndex] = tool.Block;
            session.Emit(new SessionEvent.ToolCallStatusChanged(
                _messageId!, tool.ContentIndex, tool.Id, ToolCallStatus.Running, null));
        }

        public void OnToolCompleted(PureToolCall call, PureToolResult result)
        {
            if (_toolsById.TryGetValue(call.Id, out var tool)) CompleteTool(tool, result);
        }

        public void OnLegacyToolCompleted(PureToolResult result)
        {
            if (_toolsById.TryGetValue(result.Id, out var tool)) CompleteTool(tool, result);
        }

        private void CompleteTool(ToolState tool, PureToolResult result)
        {
            tool.Status = result.Success ? ToolCallStatus.Completed : ToolCallStatus.Failed;
            tool.Output = result.Content;
            _blocks[tool.ContentIndex] = tool.Block;
            session.Emit(new SessionEvent.ToolCallStatusChanged(
                _messageId!, tool.ContentIndex, tool.Id, tool.Status, result.Content));
            if (_toolsByIndex.Values.All(t => t.Status is ToolCallStatus.Completed or ToolCallStatus.Failed))
                CloseMessage();
        }

        public void Finish(SessionOutcome outcome)
        {
            if (_messageId is null) return;
            foreach (var tool in _toolsByIndex.Values)
            {
                if (tool.Status is not (ToolCallStatus.Pending or ToolCallStatus.Running)) continue;
                tool.Status = outcome == SessionOutcome.Interrupted ? ToolCallStatus.Cancelled : ToolCallStatus.Failed;
                _blocks[tool.ContentIndex] = tool.Block;
                session.Emit(new SessionEvent.ToolCallStatusChanged(
                    _messageId, tool.ContentIndex, tool.Id, tool.Status, null));
            }
            CloseMessage();
        }

        private void EnsureMessage()
        {
            if (_messageId is not null && _assistantAppended) CloseMessage();
            if (_messageId is not null) return;
            _messageId = Guid.NewGuid().ToString("N");
            session.Emit(new SessionEvent.MessageStarted(_messageId, MessageRole.Assistant, session.CurrentModel, 0));
        }

        private void CloseMessage()
        {
            if (_messageId is null) return;
            for (var index = 0; index < _blocks.Count; index++)
                session.Emit(new SessionEvent.BlockFinished(_messageId, index, _blocks[index]));
            session.Emit(new SessionEvent.MessageFinished(_messageId,
                new ChatMessage(_messageId, MessageRole.Assistant, session.CurrentModel, _blocks.ToArray(), _usage, true), null));
            _messageId = null;
            _usage = null;
            _blocks.Clear();
            _toolsByIndex.Clear();
            _toolsById.Clear();
            _text.Clear();
            _textIndex = null;
            _thinking.Clear();
            _thinkingIndex = null;
            _assistantAppended = false;
        }

        private sealed class ToolState(int contentIndex, string id, string name)
        {
            public int ContentIndex { get; } = contentIndex;
            public string Id { get; set; } = id;
            public bool HasExplicitId { get; set; }
            public string Name { get; set; } = name;
            public StringBuilder Arguments { get; } = new();
            public ToolCallStatus Status { get; set; } = ToolCallStatus.Pending;
            public string? Output { get; set; }
            public ToolCallBlock Block => new(Id, Name, Arguments.Length == 0 ? "{}" : Arguments.ToString(),
                Status, null, Output, Status == ToolCallStatus.Failed);
        }
    }
}
