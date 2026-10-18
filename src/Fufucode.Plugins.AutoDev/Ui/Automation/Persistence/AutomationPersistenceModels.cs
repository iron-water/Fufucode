using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Fufucode.App.Features.Automation.Persistence;

/// <summary>
/// 允许的不可变请求类型，与 C5 原生工作台的「已支持决定」一一对应。
/// 没有任何 runtime / shell 执行类型：本模块只存发送前的请求文本，绝不代为执行。
/// </summary>
public enum AutomationRequestKind
{
    Apply,
    Review,
    Reconsider,
    Repair,
    Integration,
    Knowledge,
    Relation,
    Restructure,
    Stop,
    StopResolution,
}

/// <summary>请求生命周期状态。只允许这两个值；回执内容不可变。</summary>
public enum AutomationRequestState
{
    /// <summary>已落盘但结果未知：只列出供人工核对，绝不自动执行、不重写 revision、不后台重发。</summary>
    PendingUnknown,
    /// <summary>已观察到 Core 回执并原样记录（同 commandId 重放幂等）。</summary>
    ReceiptObserved,
}

/// <summary>草稿与请求类型等公共校验（标识、摘要、上限）。</summary>
public static class AutomationPersistenceValidation
{
    /// <summary>SHA-256 小写十六进制（64 字符）。用于摘要字段格式核验。</summary>
    public static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit) && value == value.ToLowerInvariant();

    public static string Sha256HexUtf8(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string Sha256HexBytes(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static bool HasControlChar(string value) => value.Any(char.IsControl);

    /// <summary>
    /// 标识校验：值一律**逐字保真**（不 Trim、不折叠大小写），长度按原始长度判定。
    /// 与 scope 的不透明身份同一条规则（C5-PERSIST-OPAQUE-ID-1）：调用方给的原文就是身份本身。
    /// </summary>
    public static string? CheckIdentifier(string name, string label, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(name)) return label + " 不能为空或纯空白";
        if (name.Length > maxLength) return $"{label} 原始长度 {name.Length} 超过上限 {maxLength} 字符";
        if (HasControlChar(name)) return label + " 含控制字符";
        return null;
    }

    public static string? CheckText(string? value, string label, int maxLength, bool required)
    {
        if (string.IsNullOrEmpty(value)) return required ? label + " 不能为空" : null;
        if (value.Length > maxLength) return $"{label} 超过上限 {maxLength} 字符";
        return null;
    }
}

/// <summary>
/// 一条本地草稿：用户在原生表单里未发送的输入。绑定到具体待办（todoId + todoKind）与其
/// context / evidenceToken 摘要；重新打开时只有版本与摘要仍一致才可用（见 <see cref="AutomationDraftEntry.IsCurrentFor"/>）。
/// 本模块不把新版本的草稿自动套用，也不推断用户意图。
/// </summary>
public sealed record AutomationDraftEntry
{
    /// <summary>草稿的稳定 ID（同一 ID 重写视为同一草稿的新内容，不同 ID 互不覆盖）。</summary>
    public required string DraftId { get; init; }

    /// <summary>绑定的待办 ID（Core 的 ControlDecisionItem.Id 或线程/任务/成果 ID，由 M 决定语义）。</summary>
    public required string TodoId { get; init; }

    /// <summary>待办类型（Core 的 kind 文本，如 review / knowledge / question）。非空，不做本模块枚举约束。</summary>
    public required string TodoKind { get; init; }

    /// <summary>待办的 version（不知道时传 null；null 与具体值不等价，摘要核验仍生效）。</summary>
    public long? TodoVersion { get; init; }

    /// <summary>待办 context JSON 原文（展示与恢复用，本模块不解析其结构）。</summary>
    public required string ContextJson { get; init; }

    /// <summary>context JSON 的 UTF-8 SHA-256（落盘时计算并核验，读取时不符即拒绝）。</summary>
    public required string ContextSha256 { get; init; }

    /// <summary>Core 给出的 evidenceToken 原文（为空时记空串，摘要为固定值）。</summary>
    public string EvidenceToken { get; init; } = "";

    /// <summary>evidenceToken 的 UTF-8 SHA-256。</summary>
    public required string EvidenceTokenSha256 { get; init; }

    /// <summary>用户输入：理由 / 依据 / 回答（三者按 M 的表单语义使用，本模块只存文本）。</summary>
    public string Reason { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string Response { get; init; } = "";

    /// <summary>已选候选 ID（竞争选择等场景）。顺序保留，不含重复。</summary>
    public IReadOnlyList<string> SelectedCandidateIds { get; init; } = Array.Empty<string>();

    /// <summary>创建/更新时间（Unix 毫秒，UTC）。</summary>
    public long CreatedAtMs { get; init; }
    public long UpdatedAtMs { get; init; }

    /// <summary>草稿内容摘要：用于「同 ID 同输入幂等、同 ID 不同输入拒绝」判定。</summary>
    public string ContentFingerprint => AutomationDraftFingerprint.Of(this);

    /// <summary>
    /// 重新打开时核验：待办 ID/kind 必须一致；若草稿记录了 version 或 evidenceToken 或 context 摘要，
    /// 而当前待办的对应值不同，则草稿不属于当前版本（返回 false），由 M 决定是否提示用户重填。
    /// </summary>
    public bool IsCurrentFor(string todoId, string todoKind, long? currentVersion, string? currentEvidenceToken, string? currentContextJson)
    {
        if (!string.Equals(TodoId, todoId, StringComparison.Ordinal)) return false;
        if (!string.Equals(TodoKind, todoKind, StringComparison.Ordinal)) return false;
        if (TodoVersion is not null && currentVersion is not null && TodoVersion != currentVersion) return false;
        if (!string.IsNullOrEmpty(EvidenceToken) && !string.Equals(EvidenceToken, currentEvidenceToken ?? "", StringComparison.Ordinal)) return false;
        if (currentContextJson is not null && ContextSha256 != AutomationPersistenceValidation.Sha256HexUtf8(currentContextJson)) return false;
        return true;
    }
}

/// <summary>
/// 草稿内容指纹（不含时间戳，用于幂等判定）。
/// 每个字段都做长度前缀编码、候选 ID 序列同时编码元素数与元素边界（C5-PERSIST-FINGERPRINT-1）：
/// 字段值里出现 U+241F、逗号等任何字符都不会与「字段边界」混淆，
/// 因此 <c>Reason="one␟two"/Evidence="three"</c> 与 <c>Reason="one"/Evidence="two␟three"</c> 指纹必然不同，
/// <c>["a,b"]</c> 与 <c>["a","b"]</c> 也必然不同。不禁止、不转义、不丢弃任何合法用户文本。
/// </summary>
internal static class AutomationDraftFingerprint
{
    public static string Of(AutomationDraftEntry draft) => AutomationTupleEncoding.Encode(
        draft.DraftId, draft.TodoId, draft.TodoKind, draft.TodoVersion?.ToString() ?? "-",
        draft.ContextSha256, draft.EvidenceTokenSha256, draft.Reason, draft.Evidence, draft.Response,
        AutomationTupleEncoding.EncodeSequence(draft.SelectedCandidateIds));
}

/// <summary>
/// 一条发送前的不可变请求：commandId、类型、精确 payloadJson 与其 SHA-256、创建时间。
/// 落盘后 payload 与 hash 不再改变；回执只允许从 pending_unknown 前进一步到 receipt_observed。
/// </summary>
public sealed record AutomationRequestRecord
{
    /// <summary>Core 命令身份里的 commandId（幂等键）。</summary>
    public required string CommandId { get; init; }

    public required AutomationRequestKind Kind { get; init; }

    /// <summary>将要发送给 Core 的精确 JSON 文本（本模块不解析结构，仅按 JSON 合法性校验）。</summary>
    public required string PayloadJson { get; init; }

    /// <summary>payloadJson 原始 UTF-8 字节的 SHA-256；读取时不符即判损坏并保留原字节。</summary>
    public required string PayloadSha256 { get; init; }

    public required long CreatedAtMs { get; init; }

    public required AutomationRequestState State { get; init; }

    /// <summary>观察到的回执 JSON 原文（state 为 receipt_observed 时非空，之后不可变）。</summary>
    public string? ReceiptJson { get; init; }

    /// <summary>回执 JSON 的 UTF-8 SHA-256。</summary>
    public string? ReceiptSha256 { get; init; }

    /// <summary>回执记录时间（Unix 毫秒，UTC）。</summary>
    public long? ReceiptObservedAtMs { get; init; }

    /// <summary>请求内容指纹（不含时间戳），用于同 ID 同输入幂等判定；同样使用无歧义编码。</summary>
    public string ContentFingerprint => AutomationTupleEncoding.Encode(CommandId, Kind.ToString(), PayloadSha256);
}

/// <summary>
/// 一次写入的可观察结果：包含发布后的 generation 与实际写盘字节数，便于诊断与 M 的接线记录。
/// </summary>
public sealed record AutomationPersistenceWriteResult(long Generation, long FileBytes);
