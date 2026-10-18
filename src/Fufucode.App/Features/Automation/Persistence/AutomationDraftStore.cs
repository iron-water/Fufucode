using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Fufucode.App.Features.Automation.Persistence;

/// <summary>
/// 草稿存储：按 scope 保存/重新打开用户在原生表单里的未发送输入。
/// 只做存储，不调用 Core、不启动任何 harness、不执行任何决定。
/// </summary>
public sealed class AutomationDraftStore
{
    private readonly AutomationPersistenceWriter _writer;
    private readonly AutomationPersistenceLimits _limits;

    public AutomationDraftStore(string dataRoot, AutomationPersistenceLimits? limits = null, IAutomationPersistenceFileIo? fileIo = null)
    {
        _limits = limits ?? AutomationPersistenceLimits.Default;
        _writer = new AutomationPersistenceWriter(dataRoot, _limits, fileIo ?? new AutomationPersistenceFileIo());
    }

    /// <summary>本模块固定目录（只由本模块读写）。</summary>
    public string StoreDirectory => _writer.StoreDirectory;

    /// <summary>当前 generation（文件不存在为 0），供 UI 准备预期值与冲突提示。</summary>
    public AutomationPersistenceResult<long> ReadGeneration(AutomationPersistenceScope scope) => _writer.ReadGeneration(scope);

    /// <summary>
    /// 保存草稿。语义：
    /// <list type="bullet">
    /// <item>同 DraftId 且内容一致 → 幂等成功，generation 不变；</item>
    /// <item>同 DraftId 但内容不同 → 视为用户主动改写，更新 UpdatedAtMs 后覆盖该条目（其它条目不动）；</item>
    /// <item>不同 DraftId → 新增，互不覆盖；</item>
    /// <item><paramref name="expectedGeneration"/> 与文件内 generation 不符 → <see cref="AutomationPersistenceErrorCode.GenerationConflict"/>，
    /// 不写入（两个 store 实例同 scope 竞争时只有一方成功，绝不静默丢稿）。</item>
    /// </list>
    /// <paramref name="expectedGeneration"/> 省略（null）→ <see cref="AutomationPersistenceErrorCode.MissingExpectedGeneration"/>：
    /// 每次写入都必须声明预期值（新 scope 用 0），不允许盲写同一草稿。为兼容调用点仍保留可选签名，
    /// 但缺失即失败，不会退化成接受当前磁盘状态。
    /// 校验失败或写盘失败时原文件字节保持不变。
    /// </summary>
    public AutomationPersistenceResult<AutomationPersistenceWriteResult> Save(
        AutomationPersistenceScope scope, AutomationDraftEntry draft, long? expectedGeneration = null)
    {
        if (!scope.IsBound)
            return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Fail(
                AutomationPersistenceErrorCode.InvalidArgument, "scope 未绑定（projectKey / 数据库路径 / authorityIdentity 必须齐备）");
        var contentError = AutomationDraftValidator.Check(draft, _limits);
        if (contentError is not null)
            return AutomationPersistenceResult<AutomationPersistenceWriteResult>.Fail(contentError);

        return _writer.Mutate(scope, expectedGeneration, document =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var existing = document.Drafts.FirstOrDefault(row => string.Equals(row.DraftId, draft.DraftId, StringComparison.Ordinal));
            if (existing is null)
            {
                document.Drafts.Add(draft with { CreatedAtMs = draft.CreatedAtMs == 0 ? now : draft.CreatedAtMs, UpdatedAtMs = now });
                return true;
            }

            // 同 ID 同内容：幂等，不改写时间戳也不推进 generation。
            if (existing.ContentFingerprint == draft.ContentFingerprint) return false;

            var index = document.Drafts.IndexOf(existing);
            document.Drafts[index] = existing with
            {
                TodoId = draft.TodoId,
                TodoKind = draft.TodoKind,
                TodoVersion = draft.TodoVersion,
                ContextJson = draft.ContextJson,
                ContextSha256 = draft.ContextSha256,
                EvidenceToken = draft.EvidenceToken,
                EvidenceTokenSha256 = draft.EvidenceTokenSha256,
                Reason = draft.Reason,
                Evidence = draft.Evidence,
                Response = draft.Response,
                SelectedCandidateIds = draft.SelectedCandidateIds.ToArray(),
                UpdatedAtMs = now,
            };
            return true;
        });
    }

    /// <summary>
    /// 读取指定 scope 的全部草稿。文件不存在返回空列表；scope 错配、未来格式、摘要不符、
    /// 非法 JSON、重复键、超限一律返回失败并保留原文件（不重置、不覆盖、不"修复"）。
    /// </summary>
    public AutomationPersistenceResult<IReadOnlyList<AutomationDraftEntry>> Load(AutomationPersistenceScope scope)
        => _writer.Read(scope, document => (IReadOnlyList<AutomationDraftEntry>)document.Drafts.ToArray(), Array.Empty<AutomationDraftEntry>());

    /// <summary>按 DraftId 取回一条草稿；不存在返回 null 值结果（不是失败）。</summary>
    public AutomationPersistenceResult<AutomationDraftEntry?> Find(AutomationPersistenceScope scope, string draftId)
    {
        var identifierError = AutomationPersistenceValidation.CheckIdentifier(draftId, "draftId", _limits.MaxIdentifierLength);
        if (identifierError is not null)
            return AutomationPersistenceResult<AutomationDraftEntry?>.InvalidArgument(identifierError);
        return _writer.Read(scope, document => (AutomationDraftEntry?)document.Drafts
            .FirstOrDefault(row => string.Equals(row.DraftId, draftId, StringComparison.Ordinal)), absent: null);
    }

    /// <summary>
    /// 重新打开待办时可用草稿：只有待办 ID/kind 一致、且记录的 version / evidenceToken / context 摘要
    /// 与当前待办仍然相符才返回。版本已前进的草稿不会自动套用，改为返回 <see cref="AutomationPersistenceErrorCode.DraftSuperseded"/>
    /// 供 UI 明示「草稿属于旧版本」。
    /// </summary>
    public AutomationPersistenceResult<AutomationDraftEntry?> OpenFor(
        AutomationPersistenceScope scope, string draftId, string todoId, string todoKind,
        long? currentVersion, string? currentEvidenceToken, string? currentContextJson)
    {
        var loaded = Find(scope, draftId);
        if (!loaded.IsSuccess) return loaded;
        var draft = loaded.Value;
        if (draft is null) return AutomationPersistenceResult<AutomationDraftEntry?>.Ok(null);
        if (draft.IsCurrentFor(todoId, todoKind, currentVersion, currentEvidenceToken, currentContextJson))
            return AutomationPersistenceResult<AutomationDraftEntry?>.Ok(draft);
        return AutomationPersistenceResult<AutomationDraftEntry?>.Fail(AutomationPersistenceErrorCode.DraftSuperseded,
            $"草稿 {draftId} 绑定的待办版本/证据已变化，不自动套用；保留原草稿供用户自行核对。");
    }

    /// <summary>
    /// 作废一条草稿：只把条目从索引移除，不删除任何用户来源文本以外的东西，也不动其它条目。
    /// 必须给出预期 generation（同 Save；省略即 MissingExpectedGeneration）。
    /// </summary>
    public AutomationPersistenceResult<AutomationPersistenceWriteResult> Remove(
        AutomationPersistenceScope scope, string draftId, long? expectedGeneration = null)
    {
        var identifierError = AutomationPersistenceValidation.CheckIdentifier(draftId, "draftId", _limits.MaxIdentifierLength);
        if (identifierError is not null)
            return AutomationPersistenceResult<AutomationPersistenceWriteResult>.InvalidArgument(identifierError);
        return _writer.Mutate(scope, expectedGeneration, document =>
        {
            return document.Drafts.RemoveAll(row => string.Equals(row.DraftId, draftId, StringComparison.Ordinal)) > 0;
        });
    }
}
