using System;
using System.Text.Json.Serialization;

namespace Fufucode.Core.Updates;

/// <summary>
/// 主程序更新包元数据清单（版本说明、下载位置、完整性哈希与签名政策）。
/// </summary>
public sealed record AppUpdateManifest(
    string Version,
    DateTimeOffset ReleaseDate,
    string ReleaseNotes,
    string PackageUrl,
    string Sha256,
    long PackageSizeBytes,
    string MinCompatibleVersion = "1.0.0",
    string SignaturePolicy = "Unsigned",
    bool? IsSignatureValid = null)
{
    /// <summary>是否属于未签名发布包。</summary>
    [JsonIgnore]
    public bool IsUnsigned => string.Equals(SignaturePolicy, "Unsigned", StringComparison.OrdinalIgnoreCase);

    /// <summary>签名合规政策说明文本。</summary>
    [JsonIgnore]
    public string PolicyNotice => IsUnsigned
        ? "未签名更新包：由 SHA-256 哈希校验完整性，不伪造数字签名。"
        : "数字签名更新包：由系统 Authenticode 证书链校验。";
}

/// <summary>更新检查状态。</summary>
public enum UpdateCheckStatus
{
    /// <summary>当前已是最新版本，无需更新。</summary>
    UpToDate,

    /// <summary>发现新版本，可供用户下载安装。</summary>
    UpdateAvailable,

    /// <summary>发现新版本，但当前版本过低或架构不兼容，需要手动全量安装。</summary>
    Incompatible,

    /// <summary>检查失败（网络错误、清单损坏等）。</summary>
    Failed,
}

/// <summary>更新检查结果。</summary>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    AppUpdateManifest? Manifest,
    string Message);

/// <summary>下载进度通知。</summary>
public sealed record UpdateProgressInfo(
    long BytesReceived,
    long? TotalBytes,
    double Percentage,
    string StatusText);

/// <summary>挂起的更新记录（待独立安装器或重启时应用）。</summary>
public sealed record UpdatePendingState(
    string TargetVersion,
    string SourceVersion,
    string InstallerPath,
    DateTimeOffset CreatedAtUtc,
    string? BackupDirectory = null);

/// <summary>更新中断/失败回滚记录（下一启动用于向用户解释）。</summary>
public sealed record UpdateInterruptedState(
    string AttemptedVersion,
    string RestoredToVersion,
    DateTimeOffset FailedAtUtc,
    string ErrorMessage);

/// <summary>应用更新准备方案。</summary>
public sealed record UpdateApplyPlan(
    bool IsBlocked,
    string? BlockReason,
    string? InstallerPath = null,
    string? Arguments = null)
{
    public static UpdateApplyPlan Blocked(string reason) => new(true, reason);
    public static UpdateApplyPlan Ready(string path, string args) => new(false, null, path, args);
}
