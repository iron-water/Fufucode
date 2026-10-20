namespace Fufucode.Core.RemoteControl;

/// <summary>手机端可见的桌面会话元信息；只读目录，不暴露文件路径或凭据。</summary>
public sealed record RemoteSessionInfo(
    string SessionId,
    string Title,
    string ProjectName,
    string Backend,
    bool IsCurrent,
    bool IsPinned,
    bool IsUnread,
    bool CanSwitch,
    DateTimeOffset LastOpenedUtc);

/// <summary>桌面会话目录；当前会话仍由 SessionSnapshot 提供完整时间线。</summary>
public sealed record RemoteSessionCatalog(
    string CurrentSessionId,
    IReadOnlyList<RemoteSessionInfo> Sessions);

/// <summary>命令（发送/停止/审批/切换）的受理结果。受理不等于执行完成。</summary>
public sealed record RemoteCommandResult(bool Accepted, string? Message);
