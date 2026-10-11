using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.ViewModels;

/// <summary>状态栏：连接/活动/模型/队列/累计用量/最近任务终态（计划 §7.1：状态分离展示）。</summary>
public sealed partial class StatusBarViewModel : ViewModelBase
{
    private readonly IChatSession _session;

    public StatusBarViewModel(IChatSession session)
    {
        _session = session;
        _session.Event += OnSessionEvent;
        Refresh();
    }

    [ObservableProperty] private string _connectionText = string.Empty;
    [ObservableProperty] private string _activityText = string.Empty;
    [ObservableProperty] private string _modelText = string.Empty;
    [ObservableProperty] private string _queueText = string.Empty;
    [ObservableProperty] private string _usageText = string.Empty;
    [ObservableProperty] private string _usageTooltip = string.Empty;
    [ObservableProperty] private string _outcomeText = string.Empty;
    [ObservableProperty] private string _approvalText = string.Empty;
    [ObservableProperty] private string _extensionStatusText = string.Empty;
    [ObservableProperty] private string _approvalTooltip = string.Empty;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private StatusTone _connectionTone;
    [ObservableProperty] private StatusTone _outcomeTone;
    [ObservableProperty] private StatusTone _approvalTone;

    // XAML 类选择器用的布尔化投影（CompiledBinding 不能比较枚举）。
    public bool ConnectionIsSuccess => ConnectionTone == StatusTone.Success;
    public bool ConnectionIsCaution => ConnectionTone == StatusTone.Caution;
    public bool ConnectionIsCritical => ConnectionTone == StatusTone.Critical;
    public bool OutcomeIsSuccess => OutcomeTone == StatusTone.Success;
    public bool OutcomeIsCritical => OutcomeTone == StatusTone.Critical;
    public bool OutcomeIsNeutral => HasOutcome && !OutcomeIsSuccess && !OutcomeIsCritical;
    public bool ApprovalIsVerified => ApprovalTone == StatusTone.Success;
    public bool ApprovalIsUnavailable => ApprovalTone == StatusTone.Critical;
    public bool IsApprovalVisible => !string.IsNullOrEmpty(ApprovalText);
    public bool HasQueue => !string.IsNullOrEmpty(QueueText);
    public bool HasUsage => !string.IsNullOrEmpty(UsageText);
    public bool HasOutcome => !string.IsNullOrEmpty(OutcomeText);
    public bool HasExtensionStatus => !string.IsNullOrEmpty(ExtensionStatusText);

    private readonly Dictionary<string, string> _extensionStatuses = new(StringComparer.Ordinal);

    /// <summary>扩展 setStatus 通知（免答类）：按键覆盖展示，空文本撤下。</summary>
    public void SetExtensionStatus(string key, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            _extensionStatuses.Remove(key);
        }
        else
        {
            _extensionStatuses[key] = text;
        }

        ExtensionStatusText = _extensionStatuses.Count == 0
            ? string.Empty
            : string.Join("  ", _extensionStatuses.Select(pair => pair.Value));
        OnPropertyChanged(nameof(HasExtensionStatus));
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
            // P2-18：助手 message_end 无 usage 时也要覆盖（null=未知，不留旧值、也不归零）；
            // 用户角色的 message_end 不携带用量语义，不触碰。
            case SessionEvent.MessageFinished finished when finished.FinalMessage.Role == MessageRole.Assistant:
                _latestUsage = finished.CumulativeUsage;
                Refresh();
                break;
            case SessionEvent.TurnSettled:
            case SessionEvent.ActivityChanged:
            case SessionEvent.ConnectionChanged:
            case SessionEvent.QueueChanged:
            case SessionEvent.SessionError:
                Refresh();
                break;
        }
    }

    private UsageInfo? _latestUsage;
    // set_model 成功后 adapter 的 CurrentModel 不随会话事件刷新（仅 attach 时读 get_state）；
    // 设置页应用成功后经此覆盖，下一次 Refresh 也优先使用。
    private string? _modelOverride;

    /// <summary>设置页 live set_model 成功后同步页头模型 chip（仅 App 侧覆盖，不改会话对象）。</summary>
    internal void SetModelOverride(string? model)
    {
        _modelOverride = model;
        Refresh();
    }

    private void Refresh()
    {
        ConnectionText = _session.Connection switch
        {
            SessionConnection.Stopped => "已停止",
            SessionConnection.Starting => "连接中…",
            SessionConnection.Connected => "已连接",
            SessionConnection.Faulted => "连接故障",
            SessionConnection.Stopping => "正在断开…",
            _ => _session.Connection.ToString(),
        };

        ActivityText = _session.Activity switch
        {
            SessionActivity.Idle => "空闲",
            SessionActivity.Running => "运行中",
            SessionActivity.Compacting => "压缩中",
            SessionActivity.Retrying => "重试中",
            SessionActivity.Cancelling => "停止中…",
            _ => _session.Activity.ToString(),
        };

        ConnectionTone = StatusColors.ForConnection(_session.Connection);

        IsRunning = _session.Activity is SessionActivity.Running or SessionActivity.Cancelling;
        var model = _modelOverride ?? _session.CurrentModel;
        ModelText = string.IsNullOrEmpty(model) ? "未选择模型" : model!;
        QueueText = _session.QueuedCount > 0 ? $"队列：{_session.QueuedCount}" : string.Empty;

        // P0-3：严格避免假零，未知值如实标注为未知
        if (_latestUsage is null || _latestUsage.IsUnknown)
        {
            UsageText = string.Empty;
            UsageTooltip = string.Empty;
        }
        else
        {
            var inStr = _latestUsage.InputTokens.HasValue ? _latestUsage.InputTokens.Value.ToString() : "未知";
            var outStr = _latestUsage.OutputTokens.HasValue ? _latestUsage.OutputTokens.Value.ToString() : "未知";
            UsageText = $"会话累计：输入 {inStr} / 输出 {outStr} tokens";

            var cacheRead = _latestUsage.CacheReadTokens.HasValue ? _latestUsage.CacheReadTokens.Value.ToString() : "未知";
            var cacheWrite = _latestUsage.CacheWriteTokens.HasValue ? _latestUsage.CacheWriteTokens.Value.ToString() : "未知";
            var costStr = _latestUsage.TotalCost.HasValue ? $" · 费用 {_latestUsage.TotalCost.Value:F4}" : string.Empty;
            UsageTooltip = $"用量详情（覆盖式累计口径）：\n• 输入 Tokens：{inStr}\n• 输出 Tokens：{outStr}\n• 缓存命中：{cacheRead}\n• 缓存写入：{cacheWrite}{costStr}\n\n注：未提供或已压缩字段显示为“未知”，不作为零消耗假定。";
        }

        OutcomeText = _session.LastOutcome switch
        {
            SessionOutcome.Completed => "上次任务：完成",
            SessionOutcome.Failed => "上次任务：失败",
            SessionOutcome.Interrupted => "上次任务：已中断",
            SessionOutcome.Unknown => "上次任务：未知",
            null => string.Empty,
            _ => string.Empty,
        };
        OutcomeTone = StatusColors.ForOutcome(_session.LastOutcome);

        // 审批桥接状态：只在 Verified 时显示"已验证"；
        // 不可用时透出诊断，解释 CanSend=false 的原因。mock 会话无此接口，不显示。
        if (_session is Fufucode.Core.IExtensionUiSession extensionUi)
        {
            switch (extensionUi.ApprovalAvailability)
            {
                case Fufucode.Core.ApprovalAvailability.Verified:
                    ApprovalText = "审批：已验证";
                    ApprovalTone = StatusTone.Success;
                    ApprovalTooltip = string.Empty;
                    break;
                case Fufucode.Core.ApprovalAvailability.Unavailable:
                    ApprovalText = "审批不可用";
                    ApprovalTone = StatusTone.Critical;
                    ApprovalTooltip = extensionUi.ApprovalDiagnostic ?? "原因未知";
                    break;
                default:
                    ApprovalText = "审批：未验证";
                    ApprovalTone = StatusTone.Neutral;
                    ApprovalTooltip = string.Empty;
                    break;
            }
        }
        else
        {
            ApprovalText = string.Empty;
            ApprovalTone = StatusTone.Neutral;
            ApprovalTooltip = string.Empty;
        }

        // 布尔化投影随 Refresh 统一刷新。
        OnPropertyChanged(nameof(ConnectionIsSuccess));
        OnPropertyChanged(nameof(ConnectionIsCaution));
        OnPropertyChanged(nameof(ConnectionIsCritical));
        OnPropertyChanged(nameof(OutcomeIsSuccess));
        OnPropertyChanged(nameof(OutcomeIsCritical));
        OnPropertyChanged(nameof(OutcomeIsNeutral));
        OnPropertyChanged(nameof(ApprovalIsVerified));
        OnPropertyChanged(nameof(ApprovalIsUnavailable));
        OnPropertyChanged(nameof(IsApprovalVisible));
        OnPropertyChanged(nameof(HasQueue));
        OnPropertyChanged(nameof(HasUsage));
        OnPropertyChanged(nameof(HasOutcome));
        OnPropertyChanged(nameof(HasExtensionStatus));
    }
}
