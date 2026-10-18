using System;
using System.Collections.Generic;
using System.Linq;
using Fufucode.Core.Automation;

namespace Fufucode.App.Features.Automation;

/// <summary>
/// v4 工作台的纯投影层：把 Core 的真实快照映射为界面对象。
/// 只做确定性映射，不推断完成、不解析自然语言、不产生任何命令。
/// 没有观测到的字段一律显示“未知”，不用空串伪装成已知值。
/// </summary>
public static class AutomationV4Projection
{
    public const int DefaultConcurrency = 2;
    public const string Unknown = "未知";

    public static string FormatSha(string? sha) => string.IsNullOrWhiteSpace(sha) ? Unknown
        : sha.Trim().Length <= 12 ? sha.Trim() : sha.Trim()[..12];

    public static string FormatWorktree(string? worktree) => string.IsNullOrWhiteSpace(worktree) ? Unknown : worktree!;

    public static string FormatDuration(long nowUtc, long? fromUtc)
    {
        if (fromUtc is not { } start || start <= 0) return Unknown;
        var elapsed = nowUtc - start;
        if (elapsed < 0) return Unknown;
        var span = TimeSpan.FromMilliseconds(elapsed);
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} 分 {span.Seconds} 秒";
        return $"{span.Seconds} 秒";
    }

    public static string FormatAgo(long nowUtc, long? atUtc)
    {
        if (atUtc is not { } at || at <= 0) return Unknown;
        var elapsed = nowUtc - at;
        if (elapsed < 0) return Unknown;
        var span = TimeSpan.FromMilliseconds(elapsed);
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} 小时前";
        return $"{(int)span.TotalDays} 天前";
    }

    public static string FormatClock(long? atUtc) => atUtc is not { } at || at <= 0 ? Unknown
        : DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>用户可读的项目状态。字符串取自本页真源，不解析 Core 的自然语言。</summary>
    public static string DeliveryStatusLabel(bool settled, DeliveryPartStatus status) => settled
        ? status switch
        {
            DeliveryPartStatus.Accepted => "已接受",
            DeliveryPartStatus.ReworkRequested => "已请求返工",
            DeliveryPartStatus.Blocked => "已阻塞",
            DeliveryPartStatus.Deferred => "已暂缓",
            DeliveryPartStatus.Superseded => "已过期",
            _ => "已交付",
        }
        : status == DeliveryPartStatus.Superseded ? "已过期（旧版本）" : "未验收";

    public static string VerificationLabel(bool integrated, bool evidencePresent)
    {
        if (!evidencePresent) return "验证待补 · 无证据留档";
        return integrated ? "已验证 · 已整合" : "证据已留档 · 未整合";
    }

    /// <summary>旧项目默认只观察：只有在显式提供旧模式时才沿用旧设置，否则不臆造旧值。</summary>
    public static DevelopmentMode EffectiveDefault(bool hasKnownPreviousState, DevelopmentMode previous)
        => hasKnownPreviousState ? previous : DevelopmentMode.Automatic;
}

