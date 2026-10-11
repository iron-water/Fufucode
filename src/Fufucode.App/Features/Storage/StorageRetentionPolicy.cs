using System;

namespace Fufucode.App.Features.Storage;

/// <summary>
/// 日志保留策略。只对日志区域生效，且只删除「超过保留天数」的正常日志文件；
/// 天数在 <see cref="MinLogRetentionDays"/>–<see cref="MaxLogRetentionDays"/> 之间被夹紧，不接受越界输入。
/// </summary>
public sealed record StorageRetentionPolicy
{
    public const int MinLogRetentionDays = 1;
    public const int MaxLogRetentionDays = 365;
    public const int DefaultLogRetentionDays = 14;

    public StorageRetentionPolicy(int logRetentionDays = DefaultLogRetentionDays)
        => LogRetentionDays = Clamp(logRetentionDays);

    public int LogRetentionDays { get; }

    public static int Clamp(int days) => Math.Clamp(days, MinLogRetentionDays, MaxLogRetentionDays);

    /// <summary>早于此时间的日志文件视为过期。</summary>
    public DateTime ExpiryCutoffUtc(DateTime utcNow) => utcNow.AddDays(-LogRetentionDays);

    public bool IsExpired(DateTime lastWriteUtc, DateTime utcNow) => lastWriteUtc < ExpiryCutoffUtc(utcNow);

    public string Describe() =>
        $"保留最近 {LogRetentionDays} 天的日志文件，更早的纳入清理目标";
}
