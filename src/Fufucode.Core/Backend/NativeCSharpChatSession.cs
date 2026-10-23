using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Diagnostics;
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
    private readonly INativeSubagentRuntime? _subagents;
    private readonly string? _workspaceRoot;
    private readonly object _gate = new();
    private readonly object _persistGate = new();
    private long _lastPersistTimestamp;
    /// <summary>长回合中途落盘的最小间隔：崩溃/强杀最多丢失最近几秒的工具结果。</summary>
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(5);

    // 人工确认的超时。60 秒对"看一眼大 diff 再决定"太短，超时即拒绝会让用户
    // 在思考时被静默驳回；放宽到 5 分钟，仍保留超时拒绝的安全语义。
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(5);
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
    private volatile bool _supportsImages;
    public bool CanSendImages => _supportsImages;

    /// <summary>图片能力探测在后台完成后回写；不影响已建立会话的历史。</summary>
    internal void SetSupportsImages(bool value) => _supportsImages = value;
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
                DateTimeOffset.UtcNow, ApprovalTimeout);
            waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingApprovals[id] = waiter;
            _pendingUi.Add(request);
        }

        notify(request);
        using var timeout = new CancellationTokenSource(ApprovalTimeout);
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
    public string? WorkspaceRoot => _workspaceRoot;

    /// <summary>可选的外部轮次预算；默认 null，由模型终态或取消结束。</summary>
    public int? MaxIterations
    {
        get => (_engine as PureCSharpAgentEngine)?.MaxIterations;
        set
        {
            if (_engine is PureCSharpAgentEngine pureEngine)
                pureEngine.MaxIterations = value;
        }
    }

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
        INativeSubagentRuntime? subagents = null,
        string? workspaceRoot = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _supportsImages = supportsImages;
        _skills = skills;
        _mcp = mcp;
        _subagents = subagents;
        _workspaceRoot = workspaceRoot;
        _systemPromptProvider = systemPromptProvider;
        _pluginTools = pluginTools;
        _persistFilePath = persistFilePath;
        SessionId = sessionId ?? Guid.NewGuid().ToString("N");
        _modelId = modelId ?? engine.GetState().ModelId;

        if (engine is PureCSharpAgentEngine pureEngine)
        {
            pureEngine.ApprovalGate = RequestApprovalAsync;
            pureEngine.TakeSteering = TakeSteeringMessage;
            if (_subagents is not null)
            {
                _subagents.ApprovalGate = pureEngine.ApprovalGate;
                _subagents.ParentMode = () => pureEngine.Mode;
                _subagents.HistoryProvider = () => pureEngine.GetState().History ?? [];
                _subagents.Progress = progress => Emit(new SessionEvent.SubagentUpdated(
                    progress.JobId, progress.Agent, progress.Kind, progress.Detail));
            }
        }
    }

    /// <summary>便利构造；仍通过同一 PureCSharpAgentEngine 保存历史和驱动工具循环。</summary>
    public NativeCSharpChatSession(
        IPureModelClient modelClient,
        IPureToolDispatcher? toolDispatcher = null,
        int? maxIterations = null,
        string? sessionId = null,
        string? modelId = null,
        IPluginSystemPromptProvider? systemPromptProvider = null,
        IPluginToolCatalog? pluginTools = null,
        string? persistFilePath = null,
        bool supportsImages = true,
        SkillRegistry? skills = null,
        McpManager? mcp = null,
        INativeSubagentRuntime? subagents = null,
        string? workspaceRoot = null)
        : this(new PureCSharpAgentEngine(toolDispatcher, modelClient, maxIterations), sessionId, modelId,
            systemPromptProvider, pluginTools, persistFilePath, supportsImages, skills, mcp, subagents, workspaceRoot)
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
    public INativeSubagentRuntime? Subagents => _subagents;

    /// <summary>内置工具集合：读取、写入、精确定位编辑、目录列表、正则检索、通配发现、命令执行与任务计划。</summary>
    private static readonly string[] BuiltInToolNames =
        ["read_file", "write_file", "edit_file", "list_dir", "grep_search", "glob_files", "run_command", "update_plan"];

    /// <summary>
    /// 每次请求重新计算可声明工具：内置工具 + 当前门禁放行的插件工具。
    /// 插件停用/卸载后 <see cref="PluginToolRegistry.ActiveToolNames"/> 立即为空，
    /// 下一次请求不再向模型声明这些工具（不留幽灵工具）。
    /// Plan 模式只声明明确只读的内置工具与 update_plan：声明一个必然被拒绝的写工具，
    /// 既浪费输入 Token，也会诱发模型反复尝试无效调用。
    /// </summary>
    private IReadOnlyList<string> BuildToolNames()
    {
        var planOnly = IsPlanMode();
        var merged = new List<string>(BuiltInToolNames.Length);
        foreach (var name in BuiltInToolNames)
        {
            if (planOnly && !ProductionToolDispatcher.IsPlanSafeTool(name)) continue;
            merged.Add(name);
        }

        if (!planOnly)
        {
            if (_skills?.HasModelInvocableSkills == true) merged.Add(SkillRegistry.ToolName);
            if (_mcp?.HasServers == true) merged.Add(McpGatewayDispatcher.ToolName);
            if (_subagents is not null) merged.Add(NativeSubagentContract.ToolName);
        }

        // Plan 模式不声明插件工具：第三方工具无法静态证明只读，调度器必然拒绝，
        // 声明它们只会制造无效调用。
        if (planOnly || _pluginTools is not { } pluginTools) return merged;
        foreach (var name in pluginTools.ActiveToolNames)
        {
            if (!merged.Contains(name, StringComparer.Ordinal)) merged.Add(name);
        }
        return merged;
    }

    /// <summary>当前会话是否处于只读 Plan 模式（非 FUMO 引擎时按 false 处理）。</summary>
    private bool IsPlanMode() =>
        _engine is PureCSharpAgentEngine { Mode: PermissionMode.Plan };

    /// <summary>构建完整的工具动作契约清单（内置 + 技能 + 插件工具的 JSON Schema）。</summary>
    private IReadOnlyList<PureToolDefinition> BuildToolDefinitions()
    {
        var planOnly = IsPlanMode();
        var list = new List<PureToolDefinition>();
        foreach (var definition in ProductionToolDispatcher.GetBuiltinToolDefinitions())
        {
            if (planOnly && !ProductionToolDispatcher.IsPlanSafeTool(definition.Name)) continue;
            list.Add(definition);
        }

        if (!planOnly)
        {
            if (_mcp?.HasServers == true) list.Add(McpGatewayDispatcher.Definition);
            if (_subagents is not null) list.Add(NativeSubagentContract.Definition);
            if (_skills?.HasModelInvocableSkills == true) list.Add(SkillRegistry.Definition);
        }

        if (!planOnly && _pluginTools is { } pt)
        {
            foreach (var reg in pt.ActiveRegistrations)
            {
                list.Add(new PureToolDefinition(reg.Name, reg.Description, reg.ParametersJsonSchema));
            }
        }
        return list;
    }

    /// <summary>
    /// 组装本轮发往模型的消息：包含基础环境规范，并追加插件人格、规划、技能与 MCP 信息。
    /// </summary>
    private IReadOnlyList<PureAgentMessage> BuildTurnMessages(string text, IReadOnlyList<PureImageAttachment>? images = null)
    {
        var basePrompt = BuildBaseSystemPrompt();
        var fragment = _systemPromptProvider?.Current;
        var planSnippet = GetCurrentPlanSnippet();
        var skillsSection = _skills?.BuildPromptSection();
        var systemContent = string.IsNullOrWhiteSpace(fragment) ? basePrompt : $"{basePrompt}\n\n{fragment}";
        // 项目约定（AGENTS.md / CLAUDE.md）是仓库级事实，五个参考后端都会自动带入；
        // 让模型用工具先找一遍只是浪费轮次。
        var projectInstructions = BuildProjectInstructionsSection();

        void AppendSection(string? section)
        {
            if (string.IsNullOrWhiteSpace(section)) return;
            systemContent = string.IsNullOrWhiteSpace(systemContent) ? section : $"{systemContent}\n\n{section}";
        }

        AppendSection(projectInstructions);
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

    private string BuildBaseSystemPrompt()
    {
        var isWindows = OperatingSystem.IsWindows();
        var osName = isWindows ? "Windows" : (OperatingSystem.IsMacOS() ? "macOS" : "Linux");
        var shellName = isWindows ? "Windows cmd.exe" : "sh";

        var sb = new StringBuilder();
        sb.AppendLine("你是运行在 Fufucode 原生内核（FUMO）中的自主编码 Agent。");
        sb.AppendLine($"当前运行环境：{osName}，命令行解释器为 {shellName}。");
        if (!string.IsNullOrWhiteSpace(_workspaceRoot))
            sb.AppendLine($"当前工作区根目录：{_workspaceRoot}");

        sb.AppendLine();
        sb.AppendLine("## 工具使用与工程原则");
        sb.AppendLine("1. 优先使用内置文件工具（read_file、write_file、edit_file、list_dir、grep_search、glob_files）进行文件查阅与编辑，比外部 shell 命令更高效稳定。");
        if (isWindows)
        {
            sb.AppendLine("2. 在 Windows 环境下使用 run_command 时：");
            sb.AppendLine("   - 串联命令必须使用 '&&' 或 '&'，不要使用 Unix shell 分隔符 ';'。");
            sb.AppendLine("   - 使用 Windows 原生命令（如 dir、where.exe、findstr、echo 等），不要直接假设存在 Unix 工具（如 ls、cat、grep、head、tr 等）。");
        }
        sb.AppendLine("3. 目标是实际完成代码开发与落地，不只给出空泛方案。");
        sb.AppendLine("4. 当遇到环境缺少某 SDK/工具链（如未安装对应语言编译器）时，不要陷入无限探测循环；应依据任务书规范自主完成核心架构设计、源代码编写、种子数据准备与必要单元测试，并在最终回复中如实说明环境限制与未验证项。");
        sb.AppendLine("5. 验证命令要克制：同一处改动后不要重复运行同一个耗时检查；先跑与改动直接相关的测试，最后再跑一次全量。");
        sb.AppendLine("6. 修改共享模块后，必须重跑覆盖它的既有测试；不要只验证新增路径，旧行为退化要自己发现。");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 自动加载仓库根的 AGENTS.md / CLAUDE.md。项目约定是仓库级事实，
    /// 每个参考后端都会在系统上下文里直接提供；让模型先花工具轮次去找是纯浪费。
    /// 只读根目录第一个存在的文件，限长 32k 字符，避免把超大文档塞满窗口。
    /// </summary>
    private string? BuildProjectInstructionsSection()
    {
        if (string.IsNullOrWhiteSpace(_workspaceRoot)) return null;
        foreach (var name in new[] { "AGENTS.md", "CLAUDE.md" })
        {
            var path = Path.Combine(_workspaceRoot, name);
            if (!File.Exists(path)) continue;
            try
            {
                var text = File.ReadAllText(path);
                if (text.Length > 32_000)
                {
                    text = text[..24_000] + "\n\n…[项目约定过长，已截断中间部分]…\n\n" +
                           text[^8_000..];
                }
                return $"## 项目约定（自动加载自 {name}）\n{text.Trim()}";
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
        return null;
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
        var queuedAt = Stopwatch.GetTimestamp();
        _activeTurn = Task.Run(() => RunTurnAsync(text, images, cts, queuedAt), CancellationToken.None);
    }

    private async Task RunTurnAsync(string text, IReadOnlyList<PureImageAttachment>? images, CancellationTokenSource cts,
        long? queuedAt = null)
    {
        // 回合根 span：所有模型请求、工具、审批、持久化都挂在它下面（trace id 即回合 id）。
        using var turnSpan = PerfTracer.Step("turn", "model", _modelId);
        turnSpan.Tag("executionMode", ExecutionMode);
        turnSpan.Tag("promptChars", text.Length);
        turnSpan.Tag("images", images?.Count ?? 0);
        turnSpan.Tag("backend", "fumo");
        if (queuedAt is { } start) turnSpan.Tag("queueMs", (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        var recorder = new RoundRecorder(this);
        var outcome = SessionOutcome.Failed;
        string? summary = null;
        try
        {
            Emit(new SessionEvent.ActivityChanged(SessionActivity.Running));
            ReplayMessage(MessageRole.User, text);
            // 会话构建请求时动态读取当前启用插件的人格片段（停用插件后下一次请求立即不再携带）。
            PureModelRequest request;
            using (var buildSpan = PerfTracer.Step("context.build"))
            {
                var messages = BuildTurnMessages(text, images);
                var toolDefinitions = BuildToolDefinitions();
                request = new PureModelRequest(
                    messages,
                    BuildToolNames(),
                    _modelId,
                    toolDefinitions,
                    _thinkingLevel == "off" ? null : _thinkingLevel);
                buildSpan.Tag("messages", messages.Count);
                buildSpan.Tag("toolDefinitions", toolDefinitions.Count);
            }
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
                        OnRestart = recorder.OnStreamRestart,
                    },
                    OnMessageAppended = message =>
                    {
                        recorder.OnMessageAppended(message);
                        // 工具轮结束后立即尝试落盘（内部有 5 秒节流）：
                        // 长回合被强杀时，已完成的工具结果不应整段丢失。
                        if (message.Role == PureAgentRole.Tool ||
                            (message.Role == PureAgentRole.Assistant && message.ToolCalls is { Count: > 0 }))
                            PersistThrottled();
                    },
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
            outcome = result.Status == PureAgentTurnStatus.Failed
                ? SessionOutcome.Failed
                : result.Truncated ? SessionOutcome.Truncated : SessionOutcome.Completed;
            summary = result.Error ?? (outcome switch
            {
                SessionOutcome.Completed => "已完成",
                SessionOutcome.Truncated => "外部任务预算耗尽，结果可能不完整。",
                _ => "模型请求失败。"
            });
            turnSpan.Tag("toolCalls", result.ToolResults.Count);
            if (outcome == SessionOutcome.Failed) turnSpan.Fail(result.Error ?? "model_failed");
            if (outcome == SessionOutcome.Truncated) turnSpan.Tag("truncated", true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            outcome = SessionOutcome.Interrupted;
            summary = "已中断";
            turnSpan.Cancel();
        }
        catch (Exception error)
        {
            summary = error.Message;
            turnSpan.Fail(error.GetType().Name);
        }
        finally
        {
            turnSpan.Tag("outcome", outcome.ToString());
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

    /// <summary>长回合中途的节流落盘；不改变回合终态的完整落盘。</summary>
    private void PersistThrottled()
    {
        if (string.IsNullOrEmpty(_persistFilePath)) return;
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastPersistTimestamp);
        if (last != 0 && Stopwatch.GetElapsedTime(last, now) < PersistInterval) return;
        Interlocked.Exchange(ref _lastPersistTimestamp, now);
        PersistHistory();
    }

    private void PersistHistory()
    {
        if (string.IsNullOrEmpty(_persistFilePath)) return;
        var history = _engine.GetState().History;
        if (history is not { Count: > 0 }) return;

        var modelId = _modelId;
        var thinkingLevel = _thinkingLevel;
        var executionMode = ExecutionMode;
        using var span = PerfTracer.Step("persist.history", "messages", PerfTracer.Invariant(history.Count));
        try
        {
            lock (_persistGate)
            {
                AtomicJsonFile.Write(_persistFilePath, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("version", 2);
                    writer.WritePropertyName("messages");
                    JsonSerializer.Serialize(writer, history);
                    if (modelId is null) writer.WriteNull("modelId");
                    else writer.WriteString("modelId", modelId);
                    writer.WriteString("thinkingLevel", thinkingLevel);
                    writer.WriteString("executionMode", executionMode);
                    writer.WriteEndObject();
                });
                if (span.TraceId != 0) span.Tag("bytes", new System.IO.FileInfo(_persistFilePath).Length);
            }
        }
        catch (Exception ex)
        {
            span.Fail(ex.GetType().Name);
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

        /// <summary>流被瞬时故障丢弃后重启：关闭半成品消息，下一次增量从新消息开始。</summary>
        public void OnStreamRestart()
        {
            if (_messageId is null) return;
            OnTextDelta("\n\n[流式连接中断，正在重试…]\n\n");
            CloseMessage();
        }

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
            tool.StartedTicks ??= Stopwatch.GetTimestamp();
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
            tool.DurationMs = tool.StartedTicks is { } started
                ? (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds
                : null;
            _blocks[tool.ContentIndex] = tool.Block;
            session.Emit(new SessionEvent.ToolCallStatusChanged(
                _messageId!, tool.ContentIndex, tool.Id, tool.Status, result.Content, tool.DurationMs));
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
                tool.DurationMs = tool.StartedTicks is { } started
                    ? (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds
                    : null;
                _blocks[tool.ContentIndex] = tool.Block;
                session.Emit(new SessionEvent.ToolCallStatusChanged(
                    _messageId, tool.ContentIndex, tool.Id, tool.Status, null, tool.DurationMs));
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
                new ChatMessage(_messageId, MessageRole.Assistant, session.CurrentModel, _blocks.ToArray(), _usage, true), _usage));
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
            /// <summary>实际派发时刻（Stopwatch 时间戳）；尚未开始执行时为 null。</summary>
            public long? StartedTicks { get; set; }
            /// <summary>进入终态时计算的耗时；未执行或旧路径没有起点时为 null。</summary>
            public long? DurationMs { get; set; }
            public ToolCallBlock Block => new(Id, Name, Arguments.Length == 0 ? "{}" : Arguments.ToString(),
                Status, null, Output, Status == ToolCallStatus.Failed, DurationMs);
        }
    }
}
