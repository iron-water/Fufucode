namespace Fufucode.Core;

/// <summary>
/// 持久化会话后端标识。Pi/OMP/Codex/DSH/Claude 的实现由后端插件/适配器提供；
/// 本枚举留在 Core 供元数据层与 UI 判定，不引入插件程序集。
/// </summary>
public enum BackendId { Pi, Omp, Codex, Dsh, Claude, NativeCSharp }

/// <summary>会话后端的能力清单；VerifiedVersion 只记录已实测版本，不虚构能力。</summary>
public sealed record BackendCapabilities(BackendId Id, string Name, string VerifiedVersion,
    bool PersistentHistory, bool ApprovalBridge, bool ProjectFork)
{
    public static BackendCapabilities For(BackendId id) => id switch
    {
        BackendId.Pi => new(id, "Pi", "1.3.0", true, true, true),
        BackendId.Omp => new(id, "OMP", "18.11.0", true, true, false),
        BackendId.Codex => new(id, "Codex", "0.163.0", true, true, false),
        BackendId.Dsh => new(id, "DSH", "0.2.0-rc.2", true, true, false),
        BackendId.Claude => new(id, "Claude", "2.1.302", true, true, false),
        // FUMO：历史持久化在本地 JSON；不使用外部审批桥；分叉在桌面会话层复制历史实现。
        BackendId.NativeCSharp => new(id, "FUMO", "native", true, false, true),
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    /// <summary>会话历史落盘格式：Pi 用 JSONL（逐行追加），其余后端用 JSON。</summary>
    public string SessionFileExtension => Id == BackendId.Pi ? ".jsonl" : ".json";

    public static string StorageId(BackendId id) => id switch
    {
        BackendId.Pi => "pi",
        BackendId.Omp => "omp",
        BackendId.Codex => "codex",
        BackendId.Dsh => "dsh",
        BackendId.Claude => "claude",
        BackendId.NativeCSharp => "fumo",
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown session backend."),
    };

    /// <summary>执行内核标识 → 持久化会话后端标识；没有会话契约的内核返回 null。</summary>
    public static BackendId? FromEngineId(Backend.EngineId engine) => engine switch
    {
        Backend.EngineId.Pi => BackendId.Pi,
        Backend.EngineId.Omp => BackendId.Omp,
        Backend.EngineId.Codex => BackendId.Codex,
        Backend.EngineId.Dsh => BackendId.Dsh,
        Backend.EngineId.Claude => BackendId.Claude,
        Backend.EngineId.NativeCSharp => BackendId.NativeCSharp,
        _ => null,
    };

    /// <summary>解析持久化标识；兼容历史值 "csharp"/"native-csharp"。</summary>
    public static BackendId Parse(string id) => id switch
    {
        "pi" => BackendId.Pi,
        "omp" => BackendId.Omp,
        "codex" => BackendId.Codex,
        "dsh" => BackendId.Dsh,
        "claude" => BackendId.Claude,
        "fumo" or "csharp" or "native-csharp" => BackendId.NativeCSharp,
        _ => throw new InvalidOperationException($"Unknown session backend '{id}'; refusing to open with another engine.")
    };
}
