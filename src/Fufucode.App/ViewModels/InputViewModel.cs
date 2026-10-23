using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Abstractions;
using Fufucode.App.Integration;

namespace Fufucode.App.ViewModels;

/// <summary>排队条中的一条排队文本及其投递方式徽章（「插入」=steer /「排队」=followUp）。</summary>
public sealed record QueuedItemViewModel(string Text, string BadgeText, bool IsLocallyOwned = false)
{
    /// <summary>steer 投递的项（徽章高亮）。</summary>
    public bool IsSteer => BadgeText == "插入";

    /// <summary>只有仍由界面持有的项才能改写/删除；已进入后端队列的项界面无权单独操作。</summary>
    public bool CanEdit => IsLocallyOwned;

    public bool CanDelete => IsLocallyOwned;

    public string BlockedReason => IsLocallyOwned
        ? string.Empty
        : "该条已在后端的待发队列里，界面无法单独改写或删除。要改内容请按「停止」（整列清空并退回输入框可再编辑）；" +
          "单条操作需要 Core 提供队列单条接口。";
}

/// <summary>
/// 输入区：草稿、发送/停止、停止后队列文本的待恢复草稿（计划 §6.5：不覆盖已有草稿）。
/// 输入法组合状态由 View 通过 <see cref="SetComposing"/> 回写（见 ImeEnterGuard）。
/// </summary>
public sealed partial class InputViewModel : ViewModelBase
{
    private readonly IChatSession _session;

    private string _draftText = string.Empty;
    private string _pendingRestoreText = string.Empty;
    private bool _isTurnActive;
    // 本地记录 steer 过的文本（同文本按计数）；项离开队列即清理。
    private readonly Dictionary<string, int> _steeredCounts = new(StringComparer.Ordinal);

    public InputViewModel(IChatSession session)
    {
        _session = session;
        if (session is IExecutionModeSession modes)
        {
            ExecutionMode = modes.ExecutionMode;
            modes.ExecutionModeChanged += mode => Dispatcher.UIThread.Post(() => { ExecutionMode = mode; OnPropertyChanged(nameof(ExecutionMode)); });
        }
        _session.Event += OnSessionEvent;
        // 派生属性（HasQueue/QueueSummary/溢出提示）随集合内容变化自动通知，
        // 不要求每个改动点都记得手动 raise，否则队列条会停留在折叠态。
        QueuedTexts.CollectionChanged += (_, _) => RaiseQueueCounts();
        // H2：附着时若回合已在进行，发送实为排队，按钮文案要立即正确。
        IsTurnActive = session.Activity is SessionActivity.Running
            or SessionActivity.Compacting or SessionActivity.Retrying;
    }

    /// <summary>草稿文本（双向绑定输入框）。</summary>
    public string DraftText
    {
        get => _draftText;
        set
        {
            if (SetProperty(ref _draftText, value))
            {
                OnPropertyChanged(nameof(HasText));
                SendCommand.NotifyCanExecuteChanged();
                SteerCommand.NotifyCanExecuteChanged();
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReferenceContext))]
    private string _referenceContext = string.Empty;
    public bool HasReferenceContext => !string.IsNullOrWhiteSpace(ReferenceContext);
    public string IdentityContext { get; set; } = "";
    public ObservableCollection<AttachedFile> Attachments { get; } = new();
    public bool HasAttachments => Attachments.Count > 0;
    public bool SupportsImages => _session is IChatAttachments { CanSendImages: true };
    public string ExecutionMode { get; private set; } = "manual";
    /// <summary>当前会话是否暴露执行模式控件；不支持的后端不显示死下拉。</summary>
    public bool ShowsExecutionMode => _session is IExecutionModeSession { SupportsExecutionModes: true };

    public bool CanChangeExecutionMode => _session is IExecutionModeSession { SupportsExecutionModes: true } && !IsTurnActive;
    public async Task SetExecutionModeAsync(string mode)
    {
        if (_session is not IExecutionModeSession session) throw new InvalidOperationException("此会话不支持权限模式");
        await session.SetExecutionModeAsync(mode);
        ExecutionMode = mode; OnPropertyChanged(nameof(ExecutionMode));
    }
    public void AddAttachment(AttachedFile file)
    {
        if (Attachments.Any(a => string.Equals(a.Path, file.Path, StringComparison.OrdinalIgnoreCase))) return;
        Attachments.Add(file); OnPropertyChanged(nameof(HasAttachments)); OnPropertyChanged(nameof(HasText)); SendCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand] private void RemoveAttachment(AttachedFile? file)
    { if (file is not null) Attachments.Remove(file); OnPropertyChanged(nameof(HasAttachments)); OnPropertyChanged(nameof(HasText)); SendCommand.NotifyCanExecuteChanged(); }

    public string ComposePrompt() => (string.IsNullOrWhiteSpace(DraftText) && HasAttachments ? "请查看附加文件。" : DraftText)
        + (HasAttachments ? "\n\n<attached_files>\n" + string.Join("\n", Attachments.Select(a => a.Context)) + "\n</attached_files>" : "")
        + (HasReferenceContext ? "\n\n<reference_context>\n" + ReferenceContext + "\n</reference_context>" : "")
        + (string.IsNullOrEmpty(IdentityContext) || IsExtensionCommand(DraftText) ? "" : "\n\n<display_preferences>\n" + IdentityContext + "\n</display_preferences>");

    [RelayCommand] private void ClearReference() => ReferenceContext = string.Empty;

    /// <summary>输入法组合期守卫在视图层实现（见 ImeEnterGuard）：组合确认的回车在 KeyDown 中被吞掉。</summary>
    public bool HasText => !string.IsNullOrWhiteSpace(DraftText) || HasAttachments;

    public bool CanSend => _session.CanSend && HasText && (!IsTurnActive || !Attachments.Any(a => a.Image is not null));

    public bool CanStop => _session.CanStop;

    /// <summary>存在待恢复草稿（停止时被清空的队列文本，且当时草稿非空未能直接恢复）。</summary>
    public bool HasPendingRestore => !string.IsNullOrEmpty(_pendingRestoreText);

    /// <summary>当前排队文本的有序快照（来自 QueueChanged 事件）。</summary>
    public ObservableCollection<string> QueuedTexts { get; } = new();

    public bool HasQueue => QueuedTexts.Count > 0;

    public string QueueSummary => HasQueue ? $"已排队 {QueuedTexts.Count} 条 · 当前回合结束后依次发送" : string.Empty;

    /// <summary>排队条里至多展示前 3 条预览（含投递方式徽章）。</summary>
    public ObservableCollection<QueuedItemViewModel> QueuedPreview { get; } = new();

    public string QueuedOverflowText => QueuedTexts.Count > 3 ? $"…还有 {QueuedTexts.Count - 3} 条" : string.Empty;

    public bool HasQueuedOverflow => QueuedTexts.Count > 3;

    /// <summary>回合活跃（运行/压缩/重试）：此时发送实际进入队列。</summary>
    public bool IsTurnActive
    {
        get => _isTurnActive;
        private set
        {
            if (SetProperty(ref _isTurnActive, value))
            {
                OnPropertyChanged(nameof(SendButtonText));
                OnPropertyChanged(nameof(SendButtonToolTip));
                OnPropertyChanged(nameof(InputHint));
                OnPropertyChanged(nameof(CanSteer));
                OnPropertyChanged(nameof(CanChangeExecutionMode));
                SteerCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>发送按钮文案：回合活跃时发送实为排队。</summary>
    public string SendButtonText => IsTurnActive ? "排队" : "发送";

    /// <summary>发送按钮 Tooltip：回合活跃时说明排队语义。</summary>
    public string SendButtonToolTip => IsTurnActive ? "排队：本回合完全结束后执行" : "发送";

    /// <summary>回合活跃时的输入提示（P2-17：Ctrl+Enter 插入）。</summary>
    public string InputHint => IsTurnActive ? "Enter 排队 · Ctrl+Enter 插入 · Shift+Enter 换行" : string.Empty;

    /// <summary>运行中插入（steer）能力：真实会话为 coordinator 旁路，mock 会话自身实现。</summary>
    public IPromptSteering? Steering { get; set; }

    /// <summary>
    /// 草稿是否按扩展命令形状（以 / 开头且非 /skill:）：提示词模板无法在 App 侧区分，
    /// 保守地把所有非 skill 的 / 都视为可能的扩展命令——扩展命令无论如何立即执行，
    /// steer 不接受，此时插入入口禁用。
    /// </summary>
    public static bool IsExtensionCommand(string text) =>
        text.StartsWith('/') && !text.StartsWith("/skill:", StringComparison.OrdinalIgnoreCase);

    public bool CanSteer => IsTurnActive && HasText
                            && Steering is { CanSteer: true }
                            && !HasAttachments && !IsExtensionCommand(DraftText.TrimStart());

    public string PendingRestoreSummary => HasPendingRestore ? $"恢复已清空的排队文本（{_pendingRestoreText.Split('\n').Length} 行）" : string.Empty;

    /// <summary>发送或停止失败时触发（真实链路：命令拒绝、桥接未就绪、停止未完成等），由组合层转显。</summary>
    public event Action<string>? OperationFailed;

    /// <summary>Local notification run begins before the RPC may emit events; acknowledgements are not completion.</summary>
    public event Func<long>? SubmissionStarting;
    public event Action<long>? SubmissionRejected;
    /// <summary>发送已开始：UI 据此立即显示乐观气泡，不等待后端回执。</summary>
    public event Action<string>? SubmissionStarted;
    /// <summary>发送被拒绝：UI 据此把乐观气泡转为失败态。</summary>
    public event Action? SubmissionFailed;

    /// <summary>
    /// 供消息内交互组件调用：把一段文本作为用户消息发送。
    /// 发送前会暂存用户当前草稿，发送后原样还回，避免吞掉正在写的内容。
    /// </summary>
    public async Task<bool> SubmitExternalAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var backup = DraftText;
        DraftText = text;
        if (!CanSend)
        {
            DraftText = backup;
            return false;
        }

        await SendAsync();
        if (backup.Length > 0 && DraftText.Length == 0) DraftText = backup;
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        // 命令入口（快捷键、脚本、程序调用）可能绕过 CanExecute；空白内容一律不发送。
        if (!CanSend) return;
        if ((HasReferenceContext || HasAttachments) && IsExtensionCommand(DraftText.TrimStart()))
        {
            OperationFailed?.Invoke("带引用时不能执行扩展命令，请先移除引用或改用普通消息。");
            return;
        }
        var notificationRun = SubmissionStarting?.Invoke() ?? 0;
        var files = Attachments.ToArray();
        var draft = DraftText;
        var reference = ReferenceContext;
        var text = ComposePrompt();
        DraftText = string.Empty;
        ReferenceContext = string.Empty;
        Attachments.Clear(); OnPropertyChanged(nameof(HasAttachments));
        SubmissionStarted?.Invoke(text);
        try
        {
            var images = files.Where(f => f.Image is not null).Select(f => f.Image!).ToArray();
            if (images.Length > 0)
            {
                if (_session is not IChatAttachments media) throw new InvalidOperationException("此会话不支持图片内容");
                await media.SendWithImagesAsync(text, images);
            }
            else await _session.SendAsync(text);
        }
        catch (Exception ex)
        {
            SubmissionRejected?.Invoke(notificationRun);
            SubmissionFailed?.Invoke();
            // 发送未被接受：回填草稿（不覆盖用户其间新输入），并显式报告，不静默吞掉。
            ApplyExternalText(draft);
            ReferenceContext = string.IsNullOrEmpty(ReferenceContext) ? reference : ReferenceContext + "\n" + reference;
            foreach (var file in files) AddAttachment(file);

            OperationFailed?.Invoke($"发送失败：{ex.GetBaseException().Message}");
        }
    }

    /// <summary>运行中插入（steer）：草稿处理与 SendAsync 相同（先清空，失败回填并报告）。</summary>
    [RelayCommand(CanExecute = nameof(CanSteer))]
    private async Task SteerAsync()
    {
        var text = ComposePrompt();
        DraftText = string.Empty;
        ReferenceContext = string.Empty;
        try
        {
            await Steering!.SteerAsync(text);
            lock (_steeredCounts)
            {
                _steeredCounts[text] = _steeredCounts.TryGetValue(text, out var count) ? count + 1 : 1;
            }

            RefreshQueueBadges();
        }
        catch (Exception ex)
        {
            ApplyExternalText(text);

            OperationFailed?.Invoke($"插入失败：{ex.GetBaseException().Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        try
        {
            await _session.StopAsync();
        }
        catch (Exception ex)
        {
            // 停止失败意味着队列可能仍会执行（Core 诚实语义），必须显式告知。
            OperationFailed?.Invoke($"停止失败：{ex.GetBaseException().Message}");
        }
    }

    /// <summary>把待恢复草稿接回输入框。</summary>
    [RelayCommand(CanExecute = nameof(HasPendingRestore))]
    private void ApplyPendingRestore()
    {
        if (!HasPendingRestore)
        {
            return;
        }

        DraftText = string.IsNullOrEmpty(DraftText) ? _pendingRestoreText : DraftText + "\n" + _pendingRestoreText;
        _pendingRestoreText = string.Empty;
        OnPropertyChanged(nameof(PendingRestoreSummary));
        ApplyPendingRestoreCommand.NotifyCanExecuteChanged();
    }

    private void OnSessionEvent(object? sender, SessionEvent e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnSessionEvent(sender, e));
            return;
        }

        switch (e)
        {
            case SessionEvent.QueueCleared cleared:
                RestoreQueue(cleared);
                SyncQueue([]);
                break;
            case SessionEvent.QueueChanged queueChanged:
                SyncQueue(queueChanged.QueuedTexts);
                SendCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
                break;
            case SessionEvent.ActivityChanged activityChanged:
                IsTurnActive = activityChanged.Activity is SessionActivity.Running
                    or SessionActivity.Compacting or SessionActivity.Retrying;
                SendCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
                break;
            case SessionEvent.TurnSettled:
                IsTurnActive = _session.Activity is SessionActivity.Running
                    or SessionActivity.Compacting or SessionActivity.Retrying;
                SendCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
                break;
            case SessionEvent.ConnectionChanged:
            case SessionEvent.SessionError:
                // 真实链路下 CanSend/CanStop 依赖连接、活动与审批门控，状态迁移都要刷新命令可用性。
                SendCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
                break;
        }
    }

    private void SyncQueue(System.Collections.Generic.IReadOnlyList<string> texts)
    {
        QueuedTexts.Clear();
        foreach (var text in texts)
        {
            QueuedTexts.Add(text);
        }

        // 清理已离开队列的 steer 记录（按当前队列中同文本的出现次数封顶）。
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var text in texts)
        {
            occurrences[text] = occurrences.TryGetValue(text, out var count) ? count + 1 : 1;
        }

        lock (_steeredCounts)
        {
            foreach (var key in _steeredCounts.Keys.ToArray())
            {
                if (!occurrences.TryGetValue(key, out var available) || available == 0)
                {
                    _steeredCounts.Remove(key);
                }
                else if (_steeredCounts[key] > available)
                {
                    _steeredCounts[key] = available;
                }
            }
        }

        RefreshQueueBadges();
        RaiseQueueCounts();
    }

    private void RaiseQueueCounts()
    {
        OnPropertyChanged(nameof(HasQueue));
        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(QueuedOverflowText));
        OnPropertyChanged(nameof(HasQueuedOverflow));
    }

    /// <summary>
    /// 编辑队列项：把文本退回输入框并从界面队列移除。
    /// 仅对界面仍持有的项生效——Core 的 QueueChanged 会随时覆盖镜像，越权移除只会让条目"复活"。
    /// </summary>
    [RelayCommand]
    private void EditQueued(QueuedItemViewModel? item)
    {
        if (item is not { CanEdit: true } || !RemoveLocalItem(item))
        {
            return;
        }

        DraftText = string.IsNullOrEmpty(DraftText) ? item.Text : DraftText + "\n" + item.Text;
        FocusRequested?.Invoke();
    }

    /// <summary>删除队列项：仅界面持有的项可删，理由同 EditQueued。</summary>
    [RelayCommand]
    private void DeleteQueued(QueuedItemViewModel? item)
    {
        if (item is not { CanDelete: true })
        {
            return;
        }

        RemoveLocalItem(item);
    }

    private bool RemoveLocalItem(QueuedItemViewModel item)
    {
        var index = -1;
        for (var position = 0; position < QueuedTexts.Count; position++)
        {
            if (string.Equals(QueuedTexts[position], item.Text, StringComparison.Ordinal))
            {
                index = position;
                break;
            }
        }

        if (index < 0)
        {
            return false;
        }

        QueuedTexts.RemoveAt(index);
        RefreshQueueBadges();
        RaiseQueueCounts();
        return true;
    }

    /// <summary>按 steer 计数给排队预览项标注「插入」/「排队」徽章（同文本按出现顺序匹配）。</summary>
    private void RefreshQueueBadges()
    {
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        lock (_steeredCounts)
        {
            foreach (var (key, count) in _steeredCounts)
            {
                remaining[key] = count;
            }
        }

        QueuedPreview.Clear();
        foreach (var text in QueuedTexts)
        {
            var steered = remaining.TryGetValue(text, out var count) && count > 0;
            if (steered)
            {
                remaining[text] = count - 1;
            }

            if (QueuedPreview.Count < 3)
            {
                // 预览项一律来自 Core 的 QueueChanged 镜像，界面不拥有它们，故 IsLocallyOwned 恒为 false。
                QueuedPreview.Add(new QueuedItemViewModel(text, steered ? "插入" : "排队"));
            }
        }
    }

    private void RestoreQueue(SessionEvent.QueueCleared cleared)
    {
        if (cleared.ClearedTexts.Count == 0)
        {
            return;
        }

        var text = string.Join("\n", cleared.ClearedTexts);

        // 计划 §6.5：保存为待恢复文本，不覆盖已有草稿
        if (string.IsNullOrEmpty(DraftText))
        {
            DraftText = text;
        }
        else
        {
            StashPendingRestore(text);
        }
    }

    /// <summary>
    /// 扩展 set_editor_text 写入输入框（计划 §8.4）：空草稿直接采用；
    /// 已有未发送草稿时不覆盖，存为待恢复文本并返回 false（调用方负责明确提示）。
    /// 调用方须在 UI 线程（接线层已封送）。
    /// </summary>
    public bool ApplyExternalText(string text)
    {
        if (string.IsNullOrEmpty(DraftText))
        {
            DraftText = text;
            return true;
        }

        StashPendingRestore(text);
        return false;
    }

    /// <summary>
    /// 命令/技能调用填入（H3）：草稿为空则直接设为 "/name "，
    /// 非空则把 "/name " 前置到草稿开头（命令只在行首生效，原有草稿保留）。
    /// </summary>
    public void InsertInvocation(string invocation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocation);
        var text = invocation.EndsWith(' ') ? invocation : invocation + " ";
        DraftText = string.IsNullOrEmpty(DraftText) ? text : text + DraftText;
        FocusRequested?.Invoke();
    }

    /// <summary>命令填入后请求输入框聚焦（由 ChatPage 视图执行 Focus）。</summary>
    public event Action? FocusRequested;

    private void StashPendingRestore(string text)
    {
        _pendingRestoreText = string.IsNullOrEmpty(_pendingRestoreText) ? text : _pendingRestoreText + "\n" + text;
        OnPropertyChanged(nameof(HasPendingRestore));
        OnPropertyChanged(nameof(PendingRestoreSummary));
        ApplyPendingRestoreCommand.NotifyCanExecuteChanged();
    }
}
