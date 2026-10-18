using System;
using System.Linq;

namespace Fufucode.App.Features.Automation.Persistence;

/// <summary>草稿条目自校验：标识、容量、JSON 合法、摘要相符、时间单调。</summary>
internal static class AutomationDraftValidator
{
    public static AutomationPersistenceFailure? Check(AutomationDraftEntry draft, AutomationPersistenceLimits limits)
    {
        if (AutomationPersistenceValidation.CheckIdentifier(draft.DraftId, "draftId", limits.MaxIdentifierLength) is { } draftIdError)
            return new(AutomationPersistenceErrorCode.InvalidArgument, draftIdError);
        if (AutomationPersistenceValidation.CheckIdentifier(draft.TodoId, "todoId", limits.MaxIdentifierLength) is { } todoIdError)
            return new(AutomationPersistenceErrorCode.InvalidArgument, todoIdError);
        if (AutomationPersistenceValidation.CheckIdentifier(draft.TodoKind, "todoKind", limits.MaxIdentifierLength) is { } kindError)
            return new(AutomationPersistenceErrorCode.InvalidArgument, kindError);
        if (AutomationPersistenceValidation.CheckText(draft.Reason, "reason", limits.MaxTextLength, required: false) is { } reasonError)
            return new(AutomationPersistenceErrorCode.Oversize, reasonError);
        if (AutomationPersistenceValidation.CheckText(draft.Evidence, "evidence", limits.MaxTextLength, required: false) is { } evidenceError)
            return new(AutomationPersistenceErrorCode.Oversize, evidenceError);
        if (AutomationPersistenceValidation.CheckText(draft.Response, "response", limits.MaxTextLength, required: false) is { } responseError)
            return new(AutomationPersistenceErrorCode.Oversize, responseError);
        if (draft.EvidenceToken.Length > limits.MaxTextLength)
            return new(AutomationPersistenceErrorCode.Oversize, "evidenceToken 超过上限 " + limits.MaxTextLength);
        if (draft.SelectedCandidateIds.Count > limits.MaxSelectedCandidates)
            return new(AutomationPersistenceErrorCode.Oversize, "候选 ID 超过上限 " + limits.MaxSelectedCandidates);
        foreach (var candidate in draft.SelectedCandidateIds)
        {
            var candidateError = AutomationPersistenceValidation.CheckIdentifier(candidate, "候选 ID", limits.MaxIdentifierLength);
            if (candidateError is not null) return new(AutomationPersistenceErrorCode.InvalidArgument, candidateError);
        }
        if (draft.SelectedCandidateIds.GroupBy(id => id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            return new(AutomationPersistenceErrorCode.DuplicateIdentity, "草稿含重复候选 ID");
        if (draft.CreatedAtMs < 0 || draft.UpdatedAtMs < 0)
            return new(AutomationPersistenceErrorCode.InvalidState, "草稿时间戳必须是非负整数");
        if (draft.UpdatedAtMs < draft.CreatedAtMs)
            return new(AutomationPersistenceErrorCode.InvalidState, "草稿 updatedAtMs 早于 createdAtMs");

        if (!AutomationStrictJson.IsValidObjectOrArray(draft.ContextJson, limits.MaxContextUtf8Bytes, out var contextError, out var contextCode))
            return new(contextCode, "草稿 contextJson：" + contextError);
        if (!AutomationPersistenceValidation.IsSha256Hex(draft.ContextSha256))
            return new(AutomationPersistenceErrorCode.CorruptRecord, "草稿 contextSha256 不是 SHA-256 小写十六进制");
        if (draft.ContextSha256 != AutomationPersistenceValidation.Sha256HexUtf8(draft.ContextJson))
            return new(AutomationPersistenceErrorCode.CorruptRecord, "草稿 contextSha256 与 contextJson 原文不符");
        if (!AutomationPersistenceValidation.IsSha256Hex(draft.EvidenceTokenSha256))
            return new(AutomationPersistenceErrorCode.CorruptRecord, "草稿 evidenceTokenSha256 不是 SHA-256 小写十六进制");
        if (draft.EvidenceTokenSha256 != AutomationPersistenceValidation.Sha256HexUtf8(draft.EvidenceToken))
            return new(AutomationPersistenceErrorCode.CorruptRecord, "草稿 evidenceTokenSha256 与 evidenceToken 原文不符");
        return null;
    }

    public static AutomationPersistenceResult<AutomationDraftEntry> CheckEntry(AutomationDraftEntry entry, AutomationPersistenceLimits limits) =>
        Check(entry, limits) is { } failure
            ? AutomationPersistenceResult<AutomationDraftEntry>.Fail(failure)
            : AutomationPersistenceResult<AutomationDraftEntry>.Ok(entry);
}

/// <summary>请求条目自校验：标识、类型、payload JSON 合法、原始字节摘要相符、状态机合法。</summary>
internal static class AutomationRequestValidator
{
    public static AutomationPersistenceFailure? Check(AutomationRequestRecord request, AutomationPersistenceLimits limits)
    {
        if (AutomationPersistenceValidation.CheckIdentifier(request.CommandId, "commandId", limits.MaxIdentifierLength) is { } idError)
            return new(AutomationPersistenceErrorCode.InvalidArgument, idError);
        if (!Enum.IsDefined(request.Kind))
            return new(AutomationPersistenceErrorCode.InvalidArgument, "未知请求类型 " + request.Kind);
        if (request.CreatedAtMs < 0)
            return new(AutomationPersistenceErrorCode.InvalidState, "请求 createdAtMs 必须是非负整数");
        if (!AutomationStrictJson.IsValidObjectOrArray(request.PayloadJson, limits.MaxPayloadUtf8Bytes, out var payloadError, out var payloadCode))
            return new(payloadCode, "请求 payloadJson：" + payloadError);
        if (!AutomationPersistenceValidation.IsSha256Hex(request.PayloadSha256))
            return new(AutomationPersistenceErrorCode.CorruptRecord, "请求 payloadSha256 不是 SHA-256 小写十六进制");
        if (request.PayloadSha256 != AutomationPersistenceValidation.Sha256HexUtf8(request.PayloadJson))
            return new(AutomationPersistenceErrorCode.CorruptRecord, "请求 payloadSha256 与 payloadJson 原始 UTF-8 字节不符");

        switch (request.State)
        {
            case AutomationRequestState.PendingUnknown:
                if (request.ReceiptJson is not null || request.ReceiptSha256 is not null || request.ReceiptObservedAtMs is not null)
                    return new(AutomationPersistenceErrorCode.InvalidState, "pending_unknown 不得携带回执字段");
                break;
            case AutomationRequestState.ReceiptObserved:
                if (string.IsNullOrEmpty(request.ReceiptJson))
                    return new(AutomationPersistenceErrorCode.InvalidState, "receipt_observed 必须有回执内容");
                if (request.ReceiptObservedAtMs is not { } observed || observed < request.CreatedAtMs)
                    return new(AutomationPersistenceErrorCode.InvalidState, "receipt_observed 的记录时间非法");
                if (!AutomationStrictJson.IsValidObjectOrArray(request.ReceiptJson, limits.MaxReceiptUtf8Bytes, out var receiptError, out var receiptCode))
                    return new(receiptCode, "请求 receiptJson：" + receiptError);
                if (!AutomationPersistenceValidation.IsSha256Hex(request.ReceiptSha256))
                    return new(AutomationPersistenceErrorCode.CorruptRecord, "回执 receiptSha256 不是 SHA-256 小写十六进制");
                if (request.ReceiptSha256 != AutomationPersistenceValidation.Sha256HexUtf8(request.ReceiptJson))
                    return new(AutomationPersistenceErrorCode.CorruptRecord, "回执 receiptSha256 与回执原文不符");
                break;
            default:
                return new(AutomationPersistenceErrorCode.InvalidState, "非法请求状态 " + request.State);
        }
        return null;
    }

    public static AutomationPersistenceResult<AutomationRequestRecord> CheckRecord(AutomationRequestRecord record, AutomationPersistenceLimits limits) =>
        Check(record, limits) is { } failure
            ? AutomationPersistenceResult<AutomationRequestRecord>.Fail(failure)
            : AutomationPersistenceResult<AutomationRequestRecord>.Ok(record);
}
