namespace Fufucode.App.Features.Automation.Persistence;

/// <summary>
/// 保守容量上限。写入前逐项校验，超限即拒绝且不触碰磁盘；读取时同样按上限拒绝，
/// 避免畸形或超大文件被当成正常状态加载。可按实例覆盖，但没有关闭校验的开关。
/// </summary>
public sealed record AutomationPersistenceLimits
{
    /// <summary>projectKey / authorityIdentity / todoId / commandId 等标识的最大字符数。</summary>
    public int MaxIdentifierLength { get; init; } = 512;

    /// <summary>reason / evidence / response 单字段最大字符数。</summary>
    public int MaxTextLength { get; init; } = 65_536;

    /// <summary>草稿 context JSON 的最大 UTF-8 字节数。</summary>
    public int MaxContextUtf8Bytes { get; init; } = 262_144;

    /// <summary>请求 payload JSON 的最大 UTF-8 字节数。</summary>
    public int MaxPayloadUtf8Bytes { get; init; } = 262_144;

    /// <summary>回执 JSON 的最大 UTF-8 字节数。</summary>
    public int MaxReceiptUtf8Bytes { get; init; } = 262_144;

    /// <summary>单 scope 最多保留的请求条目数。</summary>
    public int MaxRequestsPerScope { get; init; } = 512;

    /// <summary>单 scope 最多保留的草稿条目数。</summary>
    public int MaxDraftsPerScope { get; init; } = 256;

    /// <summary>草稿可选候选 ID 的最大个数。</summary>
    public int MaxSelectedCandidates { get; init; } = 64;

    /// <summary>整个状态文件的最大 UTF-8 字节数。</summary>
    public int MaxFileUtf8Bytes { get; init; } = 4_194_304;

    public static AutomationPersistenceLimits Default { get; } = new();
}
