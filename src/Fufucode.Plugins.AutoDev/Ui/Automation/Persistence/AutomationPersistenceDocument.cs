using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fufucode.App.Features.Automation.Persistence;

/// <summary>落盘文档在内存中的表示，以及它到 JSON 的双向严格转换。</summary>
internal sealed class AutomationPersistenceDocument
{
    public const string FormatName = "fufucode.automation-control-state";
    public const int CurrentFormatVersion = 1;

    private static readonly string[] TopLevelFields = ["format", "formatVersion", "scope", "generation", "updatedAtMs", "drafts", "requests"];
    private static readonly string[] ScopeFields = ["projectKey", "databasePath", "authorityIdentity"];
    private static readonly string[] DraftFields = ["draftId", "todoId", "todoKind", "todoVersion", "contextJson", "contextSha256", "evidenceToken", "evidenceTokenSha256", "reason", "evidence", "response", "selectedCandidateIds", "createdAtMs", "updatedAtMs"];
    private static readonly string[] RequestFields = ["commandId", "kind", "payloadJson", "payloadSha256", "createdAtMs", "state", "receiptJson", "receiptSha256", "receiptObservedAtMs"];

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    public required string ProjectKey { get; init; }
    public required string DatabasePath { get; init; }
    public required string AuthorityIdentity { get; init; }
    public required long Generation { get; set; }
    public required long UpdatedAtMs { get; set; }
    public List<AutomationDraftEntry> Drafts { get; } = new();
    public List<AutomationRequestRecord> Requests { get; } = new();

    /// <summary>序列化为落盘字节（UTF-8，无 BOM）。调用前文档必须已通过 <see cref="ValidateForWrite"/>。</summary>
    public byte[] ToUtf8()
    {
        using var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("format", FormatName);
            writer.WriteNumber("formatVersion", CurrentFormatVersion);
            writer.WriteStartObject("scope");
            writer.WriteString("projectKey", ProjectKey);
            writer.WriteString("databasePath", DatabasePath);
            writer.WriteString("authorityIdentity", AuthorityIdentity);
            writer.WriteEndObject();
            writer.WriteNumber("generation", Generation);
            writer.WriteNumber("updatedAtMs", UpdatedAtMs);

            writer.WriteStartArray("drafts");
            foreach (var draft in Drafts)
            {
                writer.WriteStartObject();
                writer.WriteString("draftId", draft.DraftId);
                writer.WriteString("todoId", draft.TodoId);
                writer.WriteString("todoKind", draft.TodoKind);
                if (draft.TodoVersion is { } version) writer.WriteNumber("todoVersion", version); else writer.WriteNull("todoVersion");
                writer.WriteString("contextJson", draft.ContextJson);
                writer.WriteString("contextSha256", draft.ContextSha256);
                writer.WriteString("evidenceToken", draft.EvidenceToken);
                writer.WriteString("evidenceTokenSha256", draft.EvidenceTokenSha256);
                writer.WriteString("reason", draft.Reason);
                writer.WriteString("evidence", draft.Evidence);
                writer.WriteString("response", draft.Response);
                writer.WriteStartArray("selectedCandidateIds");
                foreach (var candidate in draft.SelectedCandidateIds) writer.WriteStringValue(candidate);
                writer.WriteEndArray();
                writer.WriteNumber("createdAtMs", draft.CreatedAtMs);
                writer.WriteNumber("updatedAtMs", draft.UpdatedAtMs);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("requests");
            foreach (var request in Requests)
            {
                writer.WriteStartObject();
                writer.WriteString("commandId", request.CommandId);
                writer.WriteString("kind", request.Kind.ToString().ToLowerInvariant());
                writer.WriteString("payloadJson", request.PayloadJson);
                writer.WriteString("payloadSha256", request.PayloadSha256);
                writer.WriteNumber("createdAtMs", request.CreatedAtMs);
                writer.WriteString("state", StateToText(request.State));
                WriteOptionalString(writer, "receiptJson", request.ReceiptJson);
                WriteOptionalString(writer, "receiptSha256", request.ReceiptSha256);
                WriteOptionalNumber(writer, "receiptObservedAtMs", request.ReceiptObservedAtMs);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
            writer.Flush();
        }
        return buffer.ToArray();
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteString(name, value);
    }

    private static void WriteOptionalNumber(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteNumber(name, value.Value);
    }

    /// <summary>写出前的自校验（scope 齐备、ID 不重复、摘要相符、状态合法、容量不超限）。</summary>
    public AutomationPersistenceFailure? ValidateForWrite(AutomationPersistenceLimits limits)
    {
        if (string.IsNullOrWhiteSpace(ProjectKey) || string.IsNullOrWhiteSpace(AuthorityIdentity) || string.IsNullOrWhiteSpace(DatabasePath))
            return Failure(AutomationPersistenceErrorCode.InvalidArgument, "文档 scope 未绑定");
        if (Generation < 0)
            return Failure(AutomationPersistenceErrorCode.InvalidState, "generation 不能为负");
        if (Drafts.Count > limits.MaxDraftsPerScope)
            return Failure(AutomationPersistenceErrorCode.Oversize, "草稿 " + Drafts.Count + " 条超过上限 " + limits.MaxDraftsPerScope);
        if (Requests.Count > limits.MaxRequestsPerScope)
            return Failure(AutomationPersistenceErrorCode.Oversize, "请求 " + Requests.Count + " 条超过上限 " + limits.MaxRequestsPerScope);
        foreach (var duplicate in Drafts.GroupBy(draft => draft.DraftId, StringComparer.Ordinal).Where(group => group.Count() > 1))
            return Failure(AutomationPersistenceErrorCode.DuplicateIdentity, "文档内重复 draftId：" + duplicate.Key);
        foreach (var duplicate in Requests.GroupBy(request => request.CommandId, StringComparer.Ordinal).Where(group => group.Count() > 1))
            return Failure(AutomationPersistenceErrorCode.DuplicateIdentity, "文档内重复 commandId：" + duplicate.Key);
        foreach (var draft in Drafts)
            if (AutomationDraftValidator.Check(draft, limits) is { } draftError) return draftError;
        foreach (var request in Requests)
            if (AutomationRequestValidator.Check(request, limits) is { } requestError) return requestError;
        return null;
    }

    /// <summary>
    /// 严格读取：非法 JSON、重复键、未知字段、scope 错配、未来格式、摘要不符、非法状态、超限
    /// 全部拒绝，由调用方保留原字节。任何失败都不返回部分可用的文档。
    /// </summary>
    public static AutomationPersistenceResult<AutomationPersistenceDocument> TryParse(
        byte[] bytes, AutomationPersistenceScope scope, AutomationPersistenceLimits limits)
    {
        if (bytes.LongLength > limits.MaxFileUtf8Bytes)
            return Fail(AutomationPersistenceErrorCode.Oversize, "文件 " + bytes.LongLength + " 字节超过上限 " + limits.MaxFileUtf8Bytes);
        if (!AutomationStrictJson.Validate(bytes, out var jsonError))
            return Fail(AutomationPersistenceErrorCode.InvalidFileContent, jsonError);

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(Encoding.UTF8.GetString(bytes), documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = AutomationStrictJson.MaxDepth,
            });
        }
        catch (JsonException error) { return Fail(AutomationPersistenceErrorCode.InvalidFileContent, "非法 JSON：" + error.Message); }
        if (parsed is not JsonObject root)
            return Fail(AutomationPersistenceErrorCode.InvalidFileContent, "根值必须是对象");

        var unknown = UnknownFields(root, TopLevelFields);
        if (unknown.Length > 0) return Fail(AutomationPersistenceErrorCode.InvalidFileContent, "未知顶层字段：" + string.Join(",", unknown));
        foreach (var field in TopLevelFields)
            if (!root.ContainsKey(field)) return Fail(AutomationPersistenceErrorCode.InvalidFileContent, "缺少顶层字段：" + field);

        if (Text(root, "format") != FormatName)
            return Fail(AutomationPersistenceErrorCode.InvalidFileContent, "format 不是 " + FormatName);

        var formatVersion = Number(root, "formatVersion");
        if (formatVersion is null) return Fail(AutomationPersistenceErrorCode.InvalidFileContent, "formatVersion 必须是整数");
        if (formatVersion.Value > CurrentFormatVersion)
            return Fail(AutomationPersistenceErrorCode.InvalidFileContent, "formatVersion " + formatVersion.Value + " 属于未来格式，本模块不自动降级");
        if (formatVersion.Value < CurrentFormatVersion)
            return Fail(AutomationPersistenceErrorCode.InvalidFileContent, "formatVersion " + formatVersion.Value + " 不受支持（当前 " + CurrentFormatVersion + "），不做隐式迁移");

        if (root["scope"] is not JsonObject scopeObject)
            return Fail(AutomationPersistenceErrorCode.InvalidFileContent, "scope 必须是对象");
        var scopeUnknown = UnknownFields(scopeObject, ScopeFields);
        if (scopeUnknown.Length > 0) return Fail(AutomationPersistenceErrorCode.InvalidFileContent, "未知 scope 字段：" + string.Join(",", scopeUnknown));
        var recordedProjectKey = Text(scopeObject, "projectKey");
        var recordedDatabasePath = Text(scopeObject, "databasePath");
        var recordedAuthority = Text(scopeObject, "authorityIdentity");
        if (recordedProjectKey is null || recordedDatabasePath is null || recordedAuthority is null)
            return Fail(AutomationPersistenceErrorCode.InvalidFileContent, "scope 三字段必须是字符串");
        if (!scope.MatchesRecordedScope(recordedProjectKey, recordedDatabasePath, recordedAuthority))
            return Fail(AutomationPersistenceErrorCode.InvalidFileContent,
                "文件内 scope 与请求 scope 不符（改名/换库/换项目），拒绝恢复：记录为 " + recordedProjectKey + " · " + recordedDatabasePath + " · " + recordedAuthority);

        var generation = Number(root, "generation");
        if (generation is null || generation.Value < 0) return Fail(AutomationPersistenceErrorCode.InvalidFileContent, "generation 必须是非负整数");
        var updatedAt = Number(root, "updatedAtMs");
        if (updatedAt is null || updatedAt.Value < 0) return Fail(AutomationPersistenceErrorCode.InvalidFileContent, "updatedAtMs 必须是非负整数");

        var document = new AutomationPersistenceDocument
        {
            ProjectKey = scope.ProjectKey,
            DatabasePath = scope.DatabasePath,
            AuthorityIdentity = scope.AuthorityIdentity,
            Generation = generation.Value,
            UpdatedAtMs = updatedAt.Value,
        };

        if (root["drafts"] is not JsonArray drafts) return Fail(AutomationPersistenceErrorCode.InvalidFileContent, "drafts 必须是数组");
        if (drafts.Count > limits.MaxDraftsPerScope) return Fail(AutomationPersistenceErrorCode.Oversize, "文件内草稿 " + drafts.Count + " 条超过上限 " + limits.MaxDraftsPerScope);
        foreach (var node in drafts)
        {
            var readDraft = ReadDraft(node, limits);
            if (!readDraft.IsSuccess) return AutomationPersistenceResult<AutomationPersistenceDocument>.Fail(readDraft.Failure!);
            var draftEntry = readDraft.ValueOrThrow();
            if (document.Drafts.Any(existing => string.Equals(existing.DraftId, draftEntry.DraftId, StringComparison.Ordinal)))
                return Fail(AutomationPersistenceErrorCode.DuplicateIdentity, "文件内重复 draftId：" + draftEntry.DraftId);
            document.Drafts.Add(draftEntry);
        }

        if (root["requests"] is not JsonArray requests) return Fail(AutomationPersistenceErrorCode.InvalidFileContent, "requests 必须是数组");
        if (requests.Count > limits.MaxRequestsPerScope) return Fail(AutomationPersistenceErrorCode.Oversize, "文件内请求 " + requests.Count + " 条超过上限 " + limits.MaxRequestsPerScope);
        foreach (var node in requests)
        {
            var readRequest = ReadRequest(node, limits);
            if (!readRequest.IsSuccess) return AutomationPersistenceResult<AutomationPersistenceDocument>.Fail(readRequest.Failure!);
            var requestRecord = readRequest.ValueOrThrow();
            if (document.Requests.Any(existing => string.Equals(existing.CommandId, requestRecord.CommandId, StringComparison.Ordinal)))
                return Fail(AutomationPersistenceErrorCode.DuplicateIdentity, "文件内重复 commandId：" + requestRecord.CommandId);
            document.Requests.Add(requestRecord);
        }

        return AutomationPersistenceResult<AutomationPersistenceDocument>.Ok(document);
    }

    private static AutomationPersistenceResult<AutomationDraftEntry> ReadDraft(JsonNode? node, AutomationPersistenceLimits limits)
    {
        if (node is not JsonObject draft) return Bad("草稿条目必须是对象");
        var unknown = UnknownFields(draft, DraftFields);
        if (unknown.Length > 0) return Bad("未知草稿字段：" + string.Join(",", unknown));
        foreach (var field in DraftFields.Where(name => name != "todoVersion"))
            if (!draft.ContainsKey(field)) return Bad("草稿缺少字段：" + field);

        var draftId = Text(draft, "draftId");
        var todoId = Text(draft, "todoId");
        var todoKind = Text(draft, "todoKind");
        var contextJson = Text(draft, "contextJson");
        var contextSha = Text(draft, "contextSha256");
        var evidenceToken = Text(draft, "evidenceToken");
        var evidenceSha = Text(draft, "evidenceTokenSha256");
        var reason = Text(draft, "reason");
        var evidence = Text(draft, "evidence");
        var response = Text(draft, "response");
        var created = Number(draft, "createdAtMs");
        var updated = Number(draft, "updatedAtMs");
        if (draftId is null || todoId is null || todoKind is null || contextJson is null || contextSha is null
            || evidenceToken is null || evidenceSha is null || reason is null || evidence is null || response is null)
            return Bad("草稿字符串字段类型不符");
        if (created is null || updated is null || created < 0 || updated < 0) return Bad("草稿时间戳必须是非负整数");

        long? todoVersion = null;
        // todoVersion 的读写契约（C5-PERSIST-NULL-VERSION-1）：**字段省略与显式 JSON null 同义**，
        // 都表示「这份待办没有版本号」（知识提案等本来就可能无版本）。写侧对 null 输出显式 JSON null，
        // 读侧对两者都还原成 long? null，Save→Load/Find/OpenFor 与再次写入一律保真。
        // 其它任何 JSON 类型（字符串/布尔/对象/数组）与超出 long 范围的数仍然严拒，不放松成「就当没有」。
        if (Kind(draft, "todoVersion") is JsonValueKind.Number)
        {
            todoVersion = Number(draft, "todoVersion");
            if (todoVersion is null) return Bad("todoVersion 必须是整数或 null");
        }
        else if (Kind(draft, "todoVersion") is not (JsonValueKind.Null or null))
            return Bad("todoVersion 必须是整数或 null");

        var candidates = new List<string>();
        if (draft["selectedCandidateIds"] is not JsonArray candidateArray) return Bad("selectedCandidateIds 必须是数组");
        if (candidateArray.Count > limits.MaxSelectedCandidates) return Bad("selectedCandidateIds 超过上限 " + limits.MaxSelectedCandidates);
        foreach (var candidate in candidateArray)
        {
            var value = candidate is null ? null : candidate.GetValueKind() == JsonValueKind.String ? candidate.GetValue<string>() : null;
            if (value is null) return Bad("selectedCandidateIds 元素必须是字符串");
            if (string.IsNullOrWhiteSpace(value) || value.Length > limits.MaxIdentifierLength || AutomationPersistenceValidation.HasControlChar(value))
                return Bad("候选 ID 非法");
            if (candidates.Contains(value, StringComparer.Ordinal)) return Bad("草稿内重复候选 ID：" + value);
            candidates.Add(value);
        }

        var entry = new AutomationDraftEntry
        {
            DraftId = draftId,
            TodoId = todoId,
            TodoKind = todoKind,
            TodoVersion = todoVersion,
            ContextJson = contextJson,
            ContextSha256 = contextSha,
            EvidenceToken = evidenceToken,
            EvidenceTokenSha256 = evidenceSha,
            Reason = reason,
            Evidence = evidence,
            Response = response,
            SelectedCandidateIds = candidates,
            CreatedAtMs = created.Value,
            UpdatedAtMs = updated.Value,
        };
        return AutomationDraftValidator.CheckEntry(entry, limits);
    }

    private static AutomationPersistenceResult<AutomationRequestRecord> ReadRequest(JsonNode? node, AutomationPersistenceLimits limits)
    {
        if (node is not JsonObject request) return BadRequest("请求条目必须是对象");
        var unknown = UnknownFields(request, RequestFields);
        if (unknown.Length > 0) return BadRequest("未知请求字段：" + string.Join(",", unknown));
        foreach (var field in RequestFields.Where(name => name is not ("receiptJson" or "receiptSha256" or "receiptObservedAtMs")))
            if (!request.ContainsKey(field)) return BadRequest("请求缺少字段：" + field);

        var commandId = Text(request, "commandId");
        var kindText = Text(request, "kind");
        var payloadJson = Text(request, "payloadJson");
        var payloadSha = Text(request, "payloadSha256");
        var stateText = Text(request, "state");
        var created = Number(request, "createdAtMs");
        if (commandId is null || kindText is null || payloadJson is null || payloadSha is null || stateText is null)
            return BadRequest("请求字符串字段类型不符");
        if (created is null || created < 0) return BadRequest("请求 createdAtMs 必须是非负整数");
        if (!TryParseKind(kindText, out var kind)) return BadRequest("未知请求类型：" + kindText);
        if (!TryParseState(stateText, out var state)) return BadRequest("非法请求状态：" + stateText);

        var receiptJson = Text(request, "receiptJson");
        var receiptSha = Text(request, "receiptSha256");
        var receiptAt = Number(request, "receiptObservedAtMs");
        if (OptionalKind(request, "receiptJson") is not null and not JsonValueKind.Null && receiptJson is null) return BadRequest("receiptJson 必须是字符串或 null");
        if (OptionalKind(request, "receiptSha256") is not null and not JsonValueKind.Null && receiptSha is null) return BadRequest("receiptSha256 必须是字符串或 null");
        if (OptionalKind(request, "receiptObservedAtMs") is not null and not JsonValueKind.Null && receiptAt is null) return BadRequest("receiptObservedAtMs 必须是整数或 null");

        var record = new AutomationRequestRecord
        {
            CommandId = commandId,
            Kind = kind,
            PayloadJson = payloadJson,
            PayloadSha256 = payloadSha,
            CreatedAtMs = created.Value,
            State = state,
            ReceiptJson = request.ContainsKey("receiptJson") ? receiptJson : null,
            ReceiptSha256 = request.ContainsKey("receiptSha256") ? receiptSha : null,
            ReceiptObservedAtMs = request.ContainsKey("receiptObservedAtMs") ? receiptAt : null,
        };
        return AutomationRequestValidator.CheckRecord(record, limits);
    }

    private static string[] UnknownFields(JsonObject owner, string[] allowed) =>
        owner.Where(pair => !allowed.Contains(pair.Key, StringComparer.Ordinal)).Select(pair => pair.Key).ToArray();

    /// <summary>
    /// 字段存在性 + JSON 类型。JsonNode 对 JSON null 就是 C# null 引用，因此不能直接用
    /// <c>owner[name]?.GetValueKind()</c>：那会把「显式 null」读成「字段省略」，
    /// 反过来用 `is not JsonValueKind.Null` 兜底又会把合法 null 判成非法（这正是 NULL-VERSION-1 的根因）。
    /// </summary>
    private static JsonValueKind? Kind(JsonObject owner, string name)
    {
        if (!owner.ContainsKey(name)) return null;
        return owner[name] is { } node ? node.GetValueKind() : JsonValueKind.Null;
    }

    private static JsonValueKind? OptionalKind(JsonObject owner, string name) => Kind(owner, name);

    private static string? Text(JsonObject owner, string name)
    {
        if (!owner.ContainsKey(name) || owner[name] is not { } node) return null;
        return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
    }

    private static long? Number(JsonObject owner, string name)
    {
        if (!owner.ContainsKey(name) || owner[name] is not { } node) return null;
        if (node.GetValueKind() != JsonValueKind.Number) return null;
        try { return node.GetValue<long>(); } catch (Exception) { return null; }
    }

    public static string StateToText(AutomationRequestState state) => state switch
    {
        AutomationRequestState.PendingUnknown => "pending_unknown",
        AutomationRequestState.ReceiptObserved => "receipt_observed",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static bool TryParseState(string text, out AutomationRequestState state)
    {
        state = text switch
        {
            "pending_unknown" => AutomationRequestState.PendingUnknown,
            "receipt_observed" => AutomationRequestState.ReceiptObserved,
            _ => (AutomationRequestState)(-1),
        };
        return state >= 0;
    }

    public static bool TryParseKind(string text, out AutomationRequestKind kind)
    {
        foreach (AutomationRequestKind candidate in Enum.GetValues<AutomationRequestKind>())
        {
            if (string.Equals(candidate.ToString().ToLowerInvariant(), text, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }
        kind = default;
        return false;
    }

    private static AutomationPersistenceResult<AutomationPersistenceDocument> Fail(AutomationPersistenceErrorCode code, string message) =>
        AutomationPersistenceResult<AutomationPersistenceDocument>.Fail(code, message);

    private static AutomationPersistenceFailure Failure(AutomationPersistenceErrorCode code, string message) => new(code, message);

    private static AutomationPersistenceResult<AutomationDraftEntry> Bad(string message) =>
        AutomationPersistenceResult<AutomationDraftEntry>.Fail(AutomationPersistenceErrorCode.InvalidFileContent, message);

    private static AutomationPersistenceResult<AutomationRequestRecord> BadRequest(string message) =>
        AutomationPersistenceResult<AutomationRequestRecord>.Fail(AutomationPersistenceErrorCode.InvalidFileContent, message);
}
