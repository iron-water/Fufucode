using System.Text.Json;
using Fufucode.App.Abstractions;
using Fufucode.Core.Backend;

namespace Fufucode.Core;

/// <summary>
/// FUMO 原生内核（纯 C# 实现）的会话协调器：不启动外部进程，
/// 把 <see cref="NativeCSharpChatSession"/> 的事件映射为与 Pi 通道同形的
/// <see cref="SessionSnapshot"/>，使 UI 与持久化层无需区分后端。
///
/// 只实现真正具备的能力：没有审批桥、没有扩展 UI 对话框、没有分支/分叉命令。
/// 对这些能力如实返回不可用，不伪造成功响应。
/// </summary>
public sealed class NativeSessionCoordinator : ISessionCoordinator
{
    private readonly NativeCSharpChatSession _session;
    private readonly object _gate = new();
    /// <summary>工具卡片保留上限：只保留最近的已完成卡片，更早的会淘汰；未完成的卡片始终保留。</summary>
    private const int MaxRetainedToolCards = 500;
    private readonly List<PiMessage> _messages = [];
    /// <summary>工具卡片存储：仅存放已完成卡片，超过上限的头部队列被淘汰（Result 置空后释放）。</summary>
    private readonly List<PiToolCard> _tools = [];
    /// <summary>尚未完成的工具卡片（按插入顺序）；完成后转入 <see cref="_tools"/>。</summary>
    private readonly List<PiToolCard> _pendingTools = [];
    private readonly List<string> _queuedTexts = [];
    private readonly List<string> _recoverableQueue = [];
    // 不可变快照缓存：Publish 只重建发生变化的集合数组，其余复用同一列表实例。
    private IReadOnlyList<PiMessage> _messagesSnapshot = Array.Empty<PiMessage>();
    private PiToolCard[] _toolsSnapshot = [];
    private bool _toolsDirty;
    private string[] _queuedSnapshot = [];
    private string[] _recoverableSnapshot = [];
    private ConnectionState _connection = ConnectionState.Stopped;
    private ActivityState _activity = ActivityState.Idle;
    private TaskResult _result = TaskResult.Unknown;
    private string? _error;
    private bool _idleMaintenance;
    private bool _disposed;

    /// <summary>端点设置来源。查询模型时实时读取，这样保存新端点后无需重启会话。</summary>
    private readonly AppSettingsStore? _settings;
    private readonly ModelCatalogClient? _modelCatalog;
    private readonly string? _endpointApiKey;

    public NativeSessionCoordinator(NativeCSharpChatSession session, BackendId backend = BackendId.NativeCSharp,
        AppSettingsStore? settings = null, ModelCatalogClient? modelCatalog = null, string? endpointApiKey = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Backend = backend;
        _settings = settings;
        _modelCatalog = modelCatalog ?? new ModelCatalogClient();
        _endpointApiKey = endpointApiKey;
        _session.Event += OnSessionEvent;
    }

    public BackendId Backend { get; }
    public BackendCapabilities Capabilities => BackendCapabilities.For(Backend);
    public SessionSnapshot Snapshot { get { lock (_gate) return MakeSnapshot(); } }
    public event Action<SessionSnapshot>? SnapshotChanged;
    // 审批弹窗事件直接转发会话的扩展 UI 通道（远程控制等订阅方也能看到）。
    public event Action<ExtensionUiRequest>? ExtensionUiRequestReceived
    {
        add => _session.ExtensionUiRequestReceived += value;
        remove => _session.ExtensionUiRequestReceived -= value;
    }
    public event Action<ExtensionUiRequest>? ExtensionUiRequestClosed
    {
        add => _session.ExtensionUiRequestClosed += value;
        remove => _session.ExtensionUiRequestClosed -= value;
    }
    public event Action<PiExtensionErrorEvent>? ExtensionError { add { } remove { } }
    // FUMO 不使用外部审批桥：权限由执行模式（plan/auto/full）在工具调度器内守卫。
    public ApprovalAvailability ApprovalAvailability => ApprovalAvailability.Unavailable;
    public string? ApprovalDiagnostic => "FUMO 不使用外部审批桥；工具权限由执行模式与 Plan 只读守卫控制。";
    public bool IsInIdleMaintenance { get { lock (_gate) return _idleMaintenance; } }
    public bool CanSendPrompt { get { lock (_gate) return !_idleMaintenance; } }
    public RpcProcessExit? LastProcessExit => null;

    private long _usageInput, _usageOutput, _usageCacheRead, _usageCacheWrite;
    private int _usageMessages;

    public SessionUsageReport GetUsage()
    {
        lock (_gate)
        {
            UsageCounter Counter(long total) => new(total, total, _usageMessages, 0);
            var totals = new UsageTotals(_usageMessages, Counter(_usageInput), Counter(_usageOutput),
                Counter(_usageCacheRead), Counter(_usageCacheWrite), Counter(_usageInput + _usageOutput));
            return new SessionUsageReport(totals, [], 0);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) _connection = ConnectionState.Connected;
        Publish();
        return Task.CompletedTask;
    }

    public async Task SendPromptAsync(string message, PromptDelivery delivery = PromptDelivery.Immediate,
        TimeSpan? acknowledgementTimeout = null, CancellationToken cancellationToken = default,
        IReadOnlyList<PiPromptImage>? images = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        cancellationToken.ThrowIfCancellationRequested();
        bool running;
        lock (_gate)
        {
            if (_idleMaintenance) throw new InvalidOperationException("会话正在安全维护，未发送新请求。");
            running = _activity != ActivityState.Idle;
            if (!running)
            {
                _result = TaskResult.Unknown;
                _error = null;
                _recoverableQueue.Clear();
                _activity = ActivityState.Running;
            }
        }

        // Steer：运行中在下一轮模型请求前注入；否则退化为普通发送（会话自行排队）。
        if (running && delivery == PromptDelivery.Steer)
        {
            await _session.SteerAsync(message, cancellationToken).ConfigureAwait(false);
            Publish();
            return;
        }

        if (images is { Count: > 0 })
        {
            var chatImages = images.Select(i => new ChatImage(i.Data, i.MimeType)).ToArray();
            await _session.SendWithImagesAsync(message, chatImages, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _session.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        Publish();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        bool wasActive;
        lock (_gate)
        {
            wasActive = _activity != ActivityState.Idle;
            if (wasActive) _activity = ActivityState.Cancelling;
        }
        if (wasActive) Publish();

        await _session.StopAsync(cancellationToken).ConfigureAwait(false);

        // 空闲 Stop 不应留下 Cancelling 状态；回合事件未到达时也要恢复到 Idle。
        lock (_gate)
        {
            if (_activity == ActivityState.Cancelling)
            {
                _activity = ActivityState.Idle;
                _result = TaskResult.Interrupted;
            }
        }
        Publish();
    }

    public Task CloseAsync() => DisposeAsync().AsTask();

    public Task<RpcResponse> QueryStateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(StateResponse());

    /// <summary>
    /// 查询端点模型目录。FUMO 直接读取 OpenAI 兼容端点的 /models：
    /// 本机 Magpie、Ollama、LM Studio 等网关更新模型后，下一次查询自动反映。
    /// 端点未配置或不可达时返回失败响应（不伪造空成功）。
    /// </summary>
    public async Task<RpcResponse> QueryAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // 实时读取端点：保存后立即生效，不必重启会话。
            var endpoint = _settings?.Current.Endpoint;
            if (endpoint is null || string.IsNullOrWhiteSpace(endpoint.BaseUrl))
                return new RpcResponse("fumo", "get_available_models", false, null,
                    "未配置模型端点；请在设置中填写 Base URL 后再查询模型。");

            // 优先使用端点上保存的模型清单（用户在设置里勾选的），避免每次查询都打网络；
            // 没有保存清单时才回退到实时拉取。
            var saved = endpoint.Models;
            if (saved is { Count: > 0 })
            {
                var savedPayload = saved.Select(id => new { id, name = id }).ToArray();
                return new RpcResponse("fumo", "get_available_models", true,
                    JsonSerializer.SerializeToElement(new { models = savedPayload }), null);
            }

            var apiKey = _endpointApiKey;
            if (string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(endpoint.ApiKeyEnvironmentVariable))
                apiKey = Environment.GetEnvironmentVariable(endpoint.ApiKeyEnvironmentVariable);
            var models = await _modelCatalog!.ListAsync(endpoint.BaseUrl, apiKey, cancellationToken)
                .ConfigureAwait(false);
            // 只回 id/name：设置页会拼成 provider/id，Magpie 的 id 本身已含 "group/..." 前缀，
            // 再拼 provider 会变成 group/group/...，因此这里不带 provider 字段。
            var payload = models.Select(model => new
            {
                id = model.Id,
                name = model.DisplayName,
                contextLength = model.ContextLength,
                reasoningLevels = model.ReasoningEfforts,
            }).ToArray();
            return new RpcResponse("fumo", "get_available_models", true,
                JsonSerializer.SerializeToElement(new { models = payload }), null);
        }
        catch (Exception error)
        {
            return new RpcResponse("fumo", "get_available_models", false, null,
                "模型目录查询失败：" + error.GetBaseException().Message);
        }
    }

    public Task<RpcResponse> SetModelAsync(string provider, string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        _session.SetModel(modelId);
        Publish();
        return Task.FromResult(new RpcResponse("fumo", "set_model", true,
            JsonSerializer.SerializeToElement(new { model = new { provider, id = modelId } }), null));
    }

    private static readonly string[] ThinkingLevels = ["off", "low", "medium", "high", "max"];

    public Task<RpcResponse> SetThinkingLevelAsync(string level, CancellationToken cancellationToken = default)
    {
        var normalized = string.IsNullOrWhiteSpace(level) ? "off" : level.ToLowerInvariant();
        if (!ThinkingLevels.Contains(normalized, StringComparer.Ordinal))
            return Task.FromResult(new RpcResponse("fumo", "set_thinking_level", false, null,
                $"未知思考等级 '{level}'；支持：{string.Join(" / ", ThinkingLevels)}。"));

        _session.SetThinkingLevel(normalized);
        Publish();
        return Task.FromResult(new RpcResponse("fumo", "set_thinking_level", true,
            JsonSerializer.SerializeToElement(new { thinkingLevel = normalized }), null));
    }

    public Task<ThinkingLevelAvailability> QueryThinkingLevelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ThinkingLevelAvailability(ThinkingLevels, _session.ThinkingLevel, _session.CurrentModel));

    public Task<RpcResponse> QueryCommandsAsync(CancellationToken cancellationToken = default)
    {
        var commands = ProductionToolDispatcher.GetBuiltinToolDefinitions()
            .Select(t => new { name = t.Name, description = t.Description, source = "native" })
            .ToList();

        if (_session.PluginTools is { } plugins)
        {
            commands.AddRange(plugins.ActiveRegistrations
                .Select(r => new { name = r.Name, description = r.Description, source = "plugin" }));
        }
        if (_session.Mcp is { HasServers: true })
        {
            commands.Add(new
            {
                name = McpGatewayDispatcher.ToolName,
                description = "MCP 服务器：list_servers / list_tools / call",
                source = "mcp"
            });
        }
        if (_session.Subagents is { } subagents)
        {
            commands.AddRange(subagents.Agents.Select(agent => new
            {
                name = "subagent:" + agent.Name,
                description = "[" + agent.Mode + "] " + agent.Description,
                source = "subagent"
            }));
        }
        if (_session.Skills is { } skills)
        {
            commands.AddRange(skills.All
                .Select(skill => new { name = "skill:" + skill.Name, description = skill.Description, source = "skill" }));
        }

        return Task.FromResult(new RpcResponse("fumo", "get_commands", true,
            JsonSerializer.SerializeToElement(new { commands }), null));
    }

    /// <summary>
    /// 会话条目查询：以引擎完整历史为唯一来源（包含恢复的历史），ID 为稳定的位置式 entry_N。
    /// since 指定游标时只返回其后的条目，供增量刷新使用。
    /// </summary>
    public Task<RpcResponse> QueryEntriesAsync(string? since = null, CancellationToken cancellationToken = default)
    {
        var history = _session.GetHistoryMessages();
        var start = 0;
        if (!string.IsNullOrEmpty(since) && since.StartsWith("entry_", StringComparison.Ordinal) &&
            int.TryParse(since.AsSpan("entry_".Length), out var cursor) && cursor >= 0)
        {
            start = cursor + 1;
        }

        var entryList = new List<object>();
        string? lastId = start > 0 ? $"entry_{start - 1}" : null;
        for (var i = start; i < history.Count; i++)
        {
            var id = $"entry_{i}";
            var message = history[i];
            var role = message.Role switch
            {
                PureAgentRole.User => "user",
                PureAgentRole.Assistant => "assistant",
                PureAgentRole.Tool => "toolResult",
                _ => "system",
            };
            // 角色映射与 Pi 线格式一致：Tool→toolResult，System→system；内容形状保持不变。

            var content = new List<object>();
            if (!string.IsNullOrEmpty(message.Content)) content.Add(new { type = "text", text = message.Content });
            if (message.ToolCalls is { Count: > 0 } calls)
            {
                foreach (var call in calls)
                {
                    JsonElement? arguments = null;
                    try
                    {
                        using var document = JsonDocument.Parse(call.ArgumentsJson);
                        arguments = JsonSerializer.SerializeToElement(document.RootElement);
                    }
                    catch (JsonException) { }
                    content.Add(new { type = "toolCall", id = call.Id, name = call.Name, arguments });
                }
            }

            entryList.Add(new { id, parentId = lastId, type = "message", message = new { role, content } });
            lastId = id;
        }

        return Task.FromResult(new RpcResponse("fumo", "get_entries", true,
            JsonSerializer.SerializeToElement(new { entries = entryList, leafId = lastId }), null));
    }

    public Task<RpcResponse> SetSessionNameAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RpcResponse("fumo", "set_session_name", true, null, null));

    /// <summary>
    /// FUMO 的分叉由 <c>PersistentSessionManager</c> 在桌面层完成（复制历史到新会话文件并重开），
    /// 协调器本身不持文件所有权，因此这里只报告该路径，不伪造已在本进程内完成。
    /// </summary>
    public Task<RpcResponse> ForkAsync(string entryId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RpcResponse("fumo", "fork", false, null,
            "FUMO 分叉由桌面会话层处理；请使用会话菜单中的“分叉”。"));

    public Task SetExecutionModeAsync(string mode, CancellationToken cancellationToken = default) =>
        _session.SetExecutionModeAsync(mode, cancellationToken);

    public Task RespondToExtensionUiAsync(ExtensionUiRequest request, PiExtensionUiResponse response,
        CancellationToken cancellationToken = default) =>
        _session.RespondToExtensionUiAsync(request, response, cancellationToken);

    public void RestoreHistory(SessionHistory history, bool previousRunInterrupted)
    {
        lock (_gate)
        {
            // 重建而不复制：同一 PiMessage 实例可被多条历史条目复用。
            _messages.Clear();
            _messages.AddRange(history.CurrentBranchMessages);
            _messagesSnapshot = _messages.ToArray();
            _tools.Clear();
            _pendingTools.Clear();
            _toolsDirty = true;
            _usageInput = _usageOutput = _usageCacheRead = _usageCacheWrite = 0;
            _usageMessages = 0;
            foreach (var message in _messages)
            {
                if (message.Usage is not { } usage) continue;
                _usageMessages++;
                _usageInput += usage.Input ?? 0;
                _usageOutput += usage.Output ?? 0;
                _usageCacheRead += usage.CacheRead ?? 0;
                _usageCacheWrite += usage.CacheWrite ?? 0;
            }
            if (previousRunInterrupted) _result = TaskResult.Interrupted;
        }

        Publish();
    }

    public IDisposable BeginIdleMaintenance()
    {
        lock (_gate)
        {
            if (_idleMaintenance || _activity != ActivityState.Idle || _queuedTexts.Count > 0)
                throw new InvalidOperationException("会话未完全空闲（运行中或仍有排队消息），不能安全重启。");
            _idleMaintenance = true;
        }

        Publish();
        return new Lease(this);
    }

    private sealed class Lease(NativeSessionCoordinator owner) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate) owner._idleMaintenance = false;
            owner.Publish();
        }
    }

    private void OnSessionEvent(object? sender, SessionEvent e)
    {
        lock (_gate)
        {
            switch (e)
            {
                case SessionEvent.ActivityChanged changed:
                    _activity = changed.Activity switch
                    {
                        SessionActivity.Running => ActivityState.Running,
                        SessionActivity.Compacting => ActivityState.Compacting,
                        SessionActivity.Retrying => ActivityState.Retrying,
                        SessionActivity.Cancelling => ActivityState.Cancelling,
                        _ => ActivityState.Idle,
                    };
                    break;
                case SessionEvent.TurnSettled settled:
                    _activity = ActivityState.Idle;
                    _result = settled.Outcome switch
                    {
                        SessionOutcome.Completed => TaskResult.Completed,
                        SessionOutcome.Failed => TaskResult.Failed,
                        SessionOutcome.Interrupted => TaskResult.Interrupted,
                        SessionOutcome.Truncated => TaskResult.Unknown,
                        _ => TaskResult.Unknown,
                    };
                    _error = settled.Summary;
                    break;
                case SessionEvent.QueueChanged changed:
                    _queuedTexts.Clear();
                    _queuedTexts.AddRange(changed.QueuedTexts);
                    _queuedSnapshot = _queuedTexts.ToArray();
                    break;
                case SessionEvent.QueueCleared cleared:
                    _recoverableQueue.AddRange(cleared.ClearedTexts);
                    _recoverableSnapshot = _recoverableQueue.ToArray();
                    _queuedTexts.Clear();
                    _queuedSnapshot = [];
                    break;
                case SessionEvent.MessageFinished finished:
                    _messages.Add(ToWire(finished.FinalMessage));
                    _messagesSnapshot = _messages.ToArray();
                    if (finished.FinalMessage.CumulativeUsage is { } usage)
                    {
                        _usageMessages++;
                        _usageInput += usage.InputTokens ?? 0;
                        _usageOutput += usage.OutputTokens ?? 0;
                        _usageCacheRead += usage.CacheReadTokens ?? 0;
                        _usageCacheWrite += usage.CacheWriteTokens ?? 0;
                    }
                    foreach (var block in finished.FinalMessage.Blocks.OfType<ToolCallBlock>())
                        UpsertToolCard(block);
                    break;
            }
        }

        Publish();
    }

    /// <summary>
    /// 新增或更新一张工具卡片：未完成卡片进入 pending 列表（不淘汰），
    /// 完成卡片进入有界保留列表，超限时淘汰最旧的并清空其结果以释放输出字符串。
    /// </summary>
    private void UpsertToolCard(ToolCallBlock block)
    {
        var isFinal = block.Status is ToolCallStatus.Completed or ToolCallStatus.Failed;
        var card = new PiToolCard(block.ToolCallId, block.ToolName, null, ToJson(block.Output), isFinal,
            block.IsErrorOutput);
        var pendingIndex = _pendingTools.FindIndex(t => t.ToolCallId == block.ToolCallId);
        var retained = _tools.FindIndex(t => t.ToolCallId == block.ToolCallId);
        if (!isFinal)
        {
            if (pendingIndex >= 0) _pendingTools[pendingIndex] = card;
            else if (retained < 0) _pendingTools.Add(card);
            // 已进入保留列表的卡片不回退为 pending：其终态（结果是 pending 输出，不代表未完成）不会回退。
            else return;
            _toolsDirty = true;
            return;
        }

        if (pendingIndex >= 0) _pendingTools.RemoveAt(pendingIndex);
        if (retained >= 0)
        {
            _tools[retained] = card;
        }
        else
        {
            _tools.Add(card);
            TrimRetainedToolCards();
        }
        _toolsDirty = true;
    }

    /// <summary>
    /// 已完成的工具卡片只保留最近 N 张。被淘汰者在存储与上一次已发布快照中都替换为无结果的副本，
    /// 从而释放其输出字符串。
    /// </summary>
    private void TrimRetainedToolCards()
    {
        var excess = _tools.Count - MaxRetainedToolCards;
        if (excess <= 0) return;
        for (var i = 0; i < excess; i++)
        {
            var stale = _tools[i];
            if (stale.DisplayResult is null) continue;
            var released = stale with { DisplayResult = null };
            _tools[i] = released;
            var snapshotIndex = Array.IndexOf(_toolsSnapshot, stale);
            if (snapshotIndex >= 0) _toolsSnapshot[snapshotIndex] = released;
        }
        _tools.RemoveRange(0, excess);
    }

    /// <summary>
    /// 返回不可变工具列表：已完成的保留卡片在前，未完成的 pending 卡片追加在末尾。
    /// 数组惰性重建，使一次事件内的多次卡片更新只产生一个快照数组。
    /// </summary>
    private IReadOnlyList<PiToolCard> CurrentTools()
    {
        if (_toolsDirty)
        {
            _toolsSnapshot = _tools.ToArray();
            _toolsDirty = false;
        }
        return _pendingTools.Count == 0 ? _toolsSnapshot : [.. _toolsSnapshot, .. _pendingTools];
    }

    private static PiMessage ToWire(ChatMessage message) => new(
        message.Role == MessageRole.User ? "user" : "assistant",
        message.Blocks.Select(block => block switch
        {
            TextBlock text => (PiContentBlock)new PiTextBlock(text.Text, default),
            ThinkingBlock thinking => new PiThinkingBlock(thinking.Text, default),
            ToolCallBlock tool => new PiToolCallBlock(tool.ToolCallId, tool.ToolName, null, default),
            _ => new PiOtherBlock("unknown", default),
        }).ToArray(),
        null, null, null, default);

    private static JsonElement? ToJson(string? text) =>
        text is null ? null : JsonSerializer.SerializeToElement(text);

    private RpcResponse StateResponse()
    {
        lock (_gate)
        {
            return new RpcResponse("fumo", "get_state", true, JsonSerializer.SerializeToElement(new
            {
                sessionId = _session.SessionId,
                sessionFile = string.Empty,
                model = new { provider = "fumo", id = _session.CurrentModel ?? string.Empty },
                idle = _activity == ActivityState.Idle,
            }), null);
        }
    }

    /// <summary>构造快照；调用方必须持有 <see cref="_gate"/>。复用未变化集合的不可变列表实例。</summary>
    private SessionSnapshot MakeSnapshot() => new(
        null, _connection, _activity, _result, _messagesSnapshot, CurrentTools(),
        null, _queuedSnapshot, _recoverableSnapshot, _session.PendingExtensionUiRequests,
        _error, SessionFaultKind.None, ApprovalAvailability, ApprovalDiagnostic);

    private void Publish()
    {
        SessionSnapshot snapshot;
        Action<SessionSnapshot>? handlers;
        lock (_gate)
        {
            snapshot = MakeSnapshot();
            handlers = SnapshotChanged;
        }
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action<SessionSnapshot>)handler)(snapshot); }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine($"FUMO snapshot observer failed: {error}"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _session.Event -= OnSessionEvent;
        await _session.DisposeAsync().ConfigureAwait(false);
        lock (_gate) _connection = ConnectionState.Stopped;
        Publish();
    }
}
