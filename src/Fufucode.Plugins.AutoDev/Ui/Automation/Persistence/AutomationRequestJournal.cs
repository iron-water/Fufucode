using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.App.Features.Automation.Persistence;

/// <summary>请求记录视图：状态 + 回执 + 原始不可变字段，供 UI 列出「未知结果」供人工核对。</summary>
public sealed record AutomationRequestView(
    string CommandId,
    AutomationRequestKind Kind,
    string PayloadJson,
    string PayloadSha256,
    long CreatedAtMs,
    AutomationRequestState State,
    string? ReceiptJson,
    long? ReceiptObservedAtMs);

/// <summary>
/// 发送前的不可变请求日志：记录请求、记录观察到的回执、列出 pending_unknown 供对账。
/// 本类绝不发送命令、绝不重写 revision、绝不后台重发，也不调用 Core 或任何 harness。
/// </summary>
public sealed class AutomationRequestJournal
{
    private readonly AutomationPersistenceWriter _writer;
    private readonly AutomationPersistenceLimits _limits;

    public AutomationRequestJournal(string dataRoot, AutomationPersistenceLimits? limits = null, IAutomationPersistenceFileIo? fileIo = null)
    {
        _limits = limits ?? AutomationPersistenceLimits.Default;
        _writer = new AutomationPersistenceWriter(dataRoot, _limits, fileIo ?? new AutomationPersistenceFileIo());
    }

    public string StoreDirectory => _writer.StoreDirectory;

    /// <summary>
    /// 记录一条待发送请求（state = pending_unknown）。必须携带 expectedGeneration，省略 → MissingExpectedGeneration（不盲写）；
    /// commandId 已存在且 payload 摘要相同 → 幂等成功且不重复落盘；
    /// 摘要不同 → <see cref="AutomationPersistenceErrorCode.DuplicateIdentity"/>，原文件字节不动（不覆盖已发出的请求）。
    /// </summary>
    public AutomationPersistenceResult<AutomationPersistenceWriteResult> Record(
        AutomationPersistenceScope scope, AutomationRequestRecord request, long? expectedGeneration = null)
    {
        if (!scope.IsBound)
            return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Fail(
                AutomationPersistenceErrorCode.InvalidArgument, "scope 未绑定");
        if (request.State != AutomationRequestState.PendingUnknown)
            return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Fail(
                AutomationPersistenceErrorCode.InvalidState, "新记录的请求必须是 pending_unknown；回执请用 ObserveReceipt");
        if (string.IsNullOrEmpty(request.PayloadSha256))
            return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Fail(
                AutomationPersistenceErrorCode.CorruptRecord, "payloadSha256 必须由调用方按 payloadJson 原始 UTF-8 字节计算");
        var contentError = AutomationRequestValidator.Check(request, _limits);
        if (contentError is not null)
            return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Fail(contentError);

        return _writer.Mutate(scope, expectedGeneration, document =>
        {
            var existing = document.Requests.FirstOrDefault(row => string.Equals(row.CommandId, request.CommandId, StringComparison.Ordinal));
            if (existing is not null)
            {
                // 同 commandId 同 payload：幂等，不重复写。
                if (string.Equals(existing.PayloadSha256, request.PayloadSha256, StringComparison.Ordinal)) return false;
                // 同 commandId 不同 payload：拒绝，绝不覆盖已记录内容。
                throw new AutomationPersistenceRejectException(AutomationPersistenceErrorCode.DuplicateIdentity,
                    $"commandId {request.CommandId} 已记录不同的 payload（已存 {existing.PayloadSha256}，新值 {request.PayloadSha256}）。原记录保持不变。");
            }
            document.Requests.Add(request);
            return true;
        });
    }

    /// <summary>
    /// 记录观察到的回执（同样必须携带 expectedGeneration）：只允许 pending_unknown → receipt_observed 前进一步；
    /// 回执内容此后不可变（重复观察同 commandId 的不同回执会被拒绝，相同回执幂等）。
    /// 回执文本只是「观察到的内容」，本模块不把它当决定或成功。
    /// </summary>
    public AutomationPersistenceResult<AutomationPersistenceWriteResult> ObserveReceipt(
        AutomationPersistenceScope scope, string commandId, string receiptJson, long? expectedGeneration = null, long? observedAtMs = null)
    {
        if (!scope.IsBound)
            return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Fail(
                AutomationPersistenceErrorCode.InvalidArgument, "scope 未绑定");
        var identifierError = AutomationPersistenceValidation.CheckIdentifier(commandId, "commandId", _limits.MaxIdentifierLength);
        if (identifierError is not null)
            return AutomationPersistenceResult<AutomationPersistenceWriteResult>.InvalidArgument(identifierError);
        if (!AutomationStrictJson.IsValidObjectOrArray(receiptJson, _limits.MaxReceiptUtf8Bytes, out var receiptError))
            return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Fail(AutomationPersistenceErrorCode.InvalidFileContent, "回执 JSON：" + receiptError);

        var observed = observedAtMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var receiptSha = AutomationPersistenceValidation.Sha256HexUtf8(receiptJson);
        return _writer.Mutate(scope, expectedGeneration, document =>
        {
            var index = document.Requests.FindIndex(row => string.Equals(row.CommandId, commandId, StringComparison.Ordinal));
            if (index < 0)
                throw new AutomationPersistenceRejectException(AutomationPersistenceErrorCode.UnknownIdentifier,
                    "本 scope 内没有 commandId " + commandId + " 的请求记录；不新增、不猜测。");

            var request = document.Requests[index];
            if (request.State == AutomationRequestState.ReceiptObserved)
            {
                if (string.Equals(request.ReceiptSha256, receiptSha, StringComparison.Ordinal)) return false;
                throw new AutomationPersistenceRejectException(AutomationPersistenceErrorCode.DuplicateIdentity,
                    $"commandId {commandId} 的回执已记录且不可变（已存 {request.ReceiptSha256}，新值 {receiptSha}）。原记录保持不变。");
            }

            if (observed < request.CreatedAtMs)
                throw new AutomationPersistenceRejectException(AutomationPersistenceErrorCode.InvalidState,
                    "回执记录时间早于请求创建时间，拒绝。");

            document.Requests[index] = request with
            {
                State = AutomationRequestState.ReceiptObserved,
                ReceiptJson = receiptJson,
                ReceiptSha256 = receiptSha,
                ReceiptObservedAtMs = observed,
            };
            return true;
        });
    }

    /// <summary>读取本 scope 全部请求（按记录顺序）。文件不存在返回空列表。</summary>
    public AutomationPersistenceResult<IReadOnlyList<AutomationRequestView>> Load(AutomationPersistenceScope scope)
        => _writer.Read(scope,
            document => (IReadOnlyList<AutomationRequestView>)document.Requests.Select(ToView).ToArray(),
            Array.Empty<AutomationRequestView>());

    /// <summary>
    /// 列出 pending_unknown 请求供人工对账。返回的就是需要人去看的结果，本模块不执行、不重发、不改 revision。
    /// </summary>
    public AutomationPersistenceResult<IReadOnlyList<AutomationRequestView>> ListPendingUnknown(AutomationPersistenceScope scope)
        => _writer.Read(scope,
            document => (IReadOnlyList<AutomationRequestView>)document.Requests
                .Where(row => row.State == AutomationRequestState.PendingUnknown).Select(ToView).ToArray(),
            Array.Empty<AutomationRequestView>());

    /// <summary>按 commandId 精确取回原样记录（payload 与回执字节不变）；不存在返回 null。</summary>
    public AutomationPersistenceResult<AutomationRequestView?> Find(AutomationPersistenceScope scope, string commandId)
    {
        var identifierError = AutomationPersistenceValidation.CheckIdentifier(commandId, "commandId", _limits.MaxIdentifierLength);
        if (identifierError is not null)
            return AutomationPersistenceResult<AutomationRequestView?>.InvalidArgument(identifierError);
        return _writer.Read(scope,
            (Func<AutomationPersistenceDocument, AutomationRequestView?>)(document =>
            {
                var record = document.Requests.FirstOrDefault(row => string.Equals(row.CommandId, commandId, StringComparison.Ordinal));
                return record is null ? null : ToView(record);
            }),
            absent: null);
    }

    /// <summary>当前 generation，供 UI 准备预期值。</summary>
    public AutomationPersistenceResult<long> ReadGeneration(AutomationPersistenceScope scope) => _writer.ReadGeneration(scope);

    private static AutomationRequestView ToView(AutomationRequestRecord record) => new(
        record.CommandId, record.Kind, record.PayloadJson, record.PayloadSha256, record.CreatedAtMs,
        record.State, record.ReceiptJson, record.ReceiptObservedAtMs);
}

/// <summary>
/// mutator 内部用于「拒绝且不写盘」的信号异常。由 <see cref="AutomationPersistenceWriter"/> 捕获后转成失败结果，
/// 原文件字节一定保持不动。
/// </summary>
public sealed class AutomationPersistenceRejectException(AutomationPersistenceErrorCode code, string message) : Exception(message)
{
    public AutomationPersistenceErrorCode Code { get; } = code;
}
