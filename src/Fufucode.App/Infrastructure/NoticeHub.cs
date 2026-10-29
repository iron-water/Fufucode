using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace Fufucode.App.Infrastructure;

/// <summary>通知的打扰级别。只有 Floating 会打断用户。</summary>
public enum NoticeTier
{
    /// <summary>悬浮：影响生产、需要决定（审批、会话失败、计划任务到期）。</summary>
    Floating,

    /// <summary>边角：状态栏短暂变色/换图标，不打断。</summary>
    Corner,

    /// <summary>安静：只进活动记录，不出现任何浮层。</summary>
    Quiet,
}

public enum NoticeKind { Info, Success, Warning, Error, Progress, Approval }

/// <summary>一条应用级通知。时间线里只保留会话级错误，其余状态都走这里。</summary>
public sealed record Notice(string Text, NoticeTier Tier, NoticeKind Kind, DateTimeOffset At, string? Source = null)
{
    public string TimeText => At.ToString("HH:mm");
    public bool IsError => Kind == NoticeKind.Error;
    public bool IsSuccess => Kind == NoticeKind.Success;
    public bool IsProgress => Kind == NoticeKind.Progress;
    public bool IsInfo => Kind is NoticeKind.Info or NoticeKind.Progress;
}

/// <summary>
/// 通知唯一入口：所有"非对话内容"的反馈都从这里发布，再由各呈现面按级别决定怎么显示。
///
/// 分层规则（用户 2026-10-29 定）：
///  - Floating：审批、会话失败/断连、计划任务到期——必须让人看见并处理；
///  - Corner：大任务状态（会话完成、子代理完成）与操作反馈——状态栏短暂变色/图标；
///  - Quiet：只进活动记录，不打扰。
/// 时间线不再承载这些提示，只记录会话本身的错误。
/// </summary>
public sealed class NoticeHub
{
    public static NoticeHub Shared { get; } = new();

    private readonly object _gate = new();
    private string? _lastText;
    private DateTimeOffset _lastAt;

    /// <summary>活动记录（新→旧）。中心页展示，重启前保留最近 N 条。</summary>
    public ObservableCollection<Notice> History { get; } = new();

    /// <summary>历史最多保留多少条；超出后丢最旧的。</summary>
    public int MaxHistory { get; init; } = 120;

    /// <summary>需要边角提示时触发（UI 线程）。</summary>
    public event Action<Notice>? CornerRequested;

    /// <summary>需要悬浮/系统通知时触发（UI 线程）。会话结果与审批仍走各自的既有通道。</summary>
    public event Action<Notice>? FloatingRequested;

    public void Publish(string text, NoticeTier tier = NoticeTier.Corner, NoticeKind? kind = null, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Publish(text, tier, kind, source));
            return;
        }

        var trimmed = text.Trim();
        var now = DateTimeOffset.Now;
        lock (_gate)
        {
            // 同一条提示在极短时间内重复到达（一次操作走了两条处理路径）时只保留一条。
            if (string.Equals(_lastText, trimmed, StringComparison.Ordinal) &&
                now - _lastAt < TimeSpan.FromSeconds(2))
            {
                return;
            }
            _lastText = trimmed;
            _lastAt = now;
        }

        var notice = new Notice(trimmed, tier, kind ?? Classify(trimmed), now, source);
        History.Insert(0, notice);
        while (History.Count > MaxHistory) History.RemoveAt(History.Count - 1);

        if (tier == NoticeTier.Corner) CornerRequested?.Invoke(notice);
        else if (tier == NoticeTier.Floating) FloatingRequested?.Invoke(notice);
    }

    public void Clear() => History.Clear();

    /// <summary>没有显式类别时按措辞兜底：失败/无法 归红，完成/已保存 归绿。</summary>
    internal static NoticeKind Classify(string text)
    {
        foreach (var marker in ErrorMarkers)
            if (text.Contains(marker, StringComparison.Ordinal)) return NoticeKind.Error;
        foreach (var marker in SuccessMarkers)
            if (text.Contains(marker, StringComparison.Ordinal)) return NoticeKind.Success;
        return NoticeKind.Info;
    }

    private static readonly string[] ErrorMarkers =
        ["失败", "错误", "异常", "无法", "中断", "未找到", "未安装", "未就绪", "不可用"];

    private static readonly string[] SuccessMarkers =
        ["完成", "成功", "已保存", "已启动", "已恢复", "已切换", "已提交"];
}
