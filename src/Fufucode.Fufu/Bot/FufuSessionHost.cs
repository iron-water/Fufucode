using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Fufu.Integration;
using Fufucode.Fufu.Pet;

namespace Fufucode.Fufu.Bot;

/// <summary>
/// FUFU 自己的 FUMO 会话宿主：复用 <see cref="NativeSessionFactory"/>，但数据根/端点/会话文件全部独立。
/// 事件流 → <see cref="PetBehavior"/> 状态映射 + 聊天文本回调；权限门禁走会话内确认（见 <see cref="ApprovalRequested"/>）。
/// </summary>
public sealed class FufuSessionHost : IDisposable
{
    /// <summary>FUFU 会话的工程 id 与会话 id：持久化文件固定在 sessions/fufu/fumo/ 下。</summary>
    public const string ProjectId = "fufu";
    public const string SessionId = "fufu-main";

    private readonly PetBehavior _behavior;
    private readonly AppDataLayout _layout;
    private readonly AppSettingsStore _settings;
    private NativeCSharpChatSession? _session;
    private bool _disposed;

    public FufuSessionHost(string dataRoot, PetBehavior behavior)
    {
        _behavior = behavior;
        _layout = new AppDataLayout(dataRoot);
        _settings = new AppSettingsStore(_layout);
    }

    /// <summary>端点未配置时为 null —— UI 据此提示用户填写 settings.json，不伪造可用。</summary>
    public CompatibleEndpoint? Endpoint => _settings.Current.Endpoint;
    public AppSettingsStore Settings => _settings;

    public string SettingsPath => Path.Combine(_layout.Root, "settings.json");

    public bool CanSend => _session?.CanSend == true;
    public bool CanStop => _session?.CanStop == true;

    /// <summary>会话事件（文本增量/消息完成/终态/错误/队列），统一在调用方线程派发 —— 由 UI 订阅自行封送。</summary>
    public event EventHandler<SessionEvent>? SessionEventReceived;

    /// <summary>工具审批请求：宠物进入 Attention，UI 出示允许/拒绝；应答经 <see cref="RespondToApprovalAsync"/> 回传。</summary>
    public event Action<ExtensionUiRequest>? ApprovalRequested;
    public event Action<ExtensionUiRequest>? ApprovalClosed;

    /// <summary>
    /// 确保会话存在。返回 null 表示端点未配置（此时不创建会话）。
    /// workspace 固定为 &lt;dataRoot&gt;/workspace —— 内置工具的真实工作目录。
    /// 上次会话的历史从 persist 文件恢复（复用 <see cref="PersistentSessionManager.LoadNativeMessages"/>）。
    /// </summary>
    public async Task<NativeCSharpChatSession?> EnsureSessionAsync()
    {
        if (_disposed) return null;
        if (_session is not null) return _session;
        if (Endpoint is null) return null;

        var workspace = Path.Combine(_layout.Root, "workspace");
        Directory.CreateDirectory(workspace);
        var persist = NativeSessionFactory.SessionFilePath(_layout, ProjectId, SessionId);
        var chat = (NativeCSharpChatSession)NativeSessionFactory.Create(_settings, SessionId, workspace,
            persistFilePath: persist, supportsImages: false);
        _session = chat;
        // 宠物默认 auto 档：普通编辑工具自动放行，run_command 仍需用户确认（权限门禁真实生效）。
        await chat.SetExecutionModeAsync("auto").ConfigureAwait(false);

        // 恢复上次会话：历史/模型/思考等级；损坏文件由 LoadNativeMessages 自行隔离。
        if (File.Exists(persist) && PersistentSessionManager.LoadNativeMessages(persist) is { } saved
            && saved.Messages.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(saved.ModelId)) chat.SetModel(saved.ModelId);
            if (!string.IsNullOrWhiteSpace(saved.ThinkingLevel)) chat.SetThinkingLevel(saved.ThinkingLevel);
            chat.RestoreHistory(saved.Messages);
            FufuLog.Write($"FUMO history restored: {saved.Messages.Count} messages");
        }

        chat.ExtensionUiRequestReceived += OnApprovalReceived;
        chat.ExtensionUiRequestClosed += OnApprovalClosed;
        chat.Event += OnSessionEvent;
        FufuLog.Write($"FUMO session ready: model={chat.CurrentModel ?? "unknown"}");
        return _session;
    }

    /// <summary>
    /// 打开 FUFU 聊天窗时读取当前会话的历史正文。
    /// 工具调用与工具结果保留在 FUMO 上下文中，但不伪装成聊天气泡。
    /// </summary>
    public async Task<IReadOnlyList<ChatMessage>> LoadChatHistoryAsync()
    {
        var session = await EnsureSessionAsync().ConfigureAwait(false);
        if (session is null) return Array.Empty<ChatMessage>();

        var result = new List<ChatMessage>();
        var index = 0;
        foreach (var message in session.GetHistoryMessages())
        {
            var role = message.Role switch
            {
                PureAgentRole.User => MessageRole.User,
                PureAgentRole.Assistant => MessageRole.Assistant,
                _ => (MessageRole?)null,
            };
            if (role is null || string.IsNullOrWhiteSpace(message.Content)) continue;

            index++;
            result.Add(new ChatMessage(
                $"history-{index}",
                role.Value,
                role == MessageRole.Assistant ? session.CurrentModel : null,
                [new TextBlock(message.Content)],
                null,
                true));
        }

        return result;
    }

    public async Task SendAsync(string text, CancellationToken ct = default)
    {
        var session = await EnsureSessionAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("端点未配置");
        await session.SendAsync(text, ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_session is not null) await _session.StopAsync(ct).ConfigureAwait(false);
    }

    public Task RespondToApprovalAsync(ExtensionUiRequest request, bool approved)
    {
        if (_session is null) return Task.CompletedTask;
        return _session.RespondToExtensionUiAsync(request, PiExtensionUiResponse.Confirm(approved));
    }

    private void OnApprovalReceived(ExtensionUiRequest request)
    {
        _behavior.Notify(PetState.Attention);
        ApprovalRequested?.Invoke(request);
    }

    private void OnApprovalClosed(ExtensionUiRequest request) => ApprovalClosed?.Invoke(request);

    private void OnSessionEvent(object? sender, SessionEvent e)
    {
        switch (e)
        {
            case SessionEvent.ActivityChanged activity:
                _behavior.Notify(activity.Activity switch
                {
                    SessionActivity.Running or SessionActivity.Compacting or SessionActivity.Cancelling => PetState.Working,
                    SessionActivity.Retrying => PetState.Attention,
                    _ => PetState.Thinking,
                });
                break;
            case SessionEvent.TurnSettled settled:
                _behavior.Notify(settled.Outcome == SessionOutcome.Failed ? PetState.Error : PetState.Success);
                break;
            case SessionEvent.SessionError:
                _behavior.Notify(PetState.Error);
                break;
            case SessionEvent.ThinkingAppended:
            case SessionEvent.ToolCallStatusChanged:
                _behavior.Notify(PetState.Working);
                break;
        }
        SessionEventReceived?.Invoke(this, e);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_session is { } session)
        {
            session.ExtensionUiRequestReceived -= OnApprovalReceived;
            session.ExtensionUiRequestClosed -= OnApprovalClosed;
            session.Event -= OnSessionEvent;
        }
    }
}
