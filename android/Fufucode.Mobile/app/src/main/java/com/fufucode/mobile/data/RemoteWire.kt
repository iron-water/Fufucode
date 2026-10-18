package com.fufucode.mobile.data

import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject

/**
 * 桌面线协议 JSON 解析器。
 *
 * 设计要点：
 * - 只做**如实映射**，不猜测或补默认业务值；
 * - 未知 `kind` / `type` 记录到 [diagnostics] 而不是抛异常（桌面升级不连坐手机）；
 * - 必填字段缺失时返回 null，由调用方明确报错，不产出半真半假的对象。
 */
object RemoteWire {

    private val diagnostics = mutableListOf<WireDiagnostic>()

    /** 解析期间的诊断记录（清空后可重复使用；测试会断言未知类型被记录）。 */
    val diagnostics_: List<WireDiagnostic> get() = synchronized(diagnostics) { diagnostics.toList() }

    fun clearDiagnostics() = synchronized(diagnostics) { diagnostics.clear() }

    private fun note(scope: String, detail: String) {
        synchronized(diagnostics) {
            if (diagnostics.size < MAX_DIAGNOSTICS) diagnostics.add(WireDiagnostic(scope, detail))
        }
    }

    private const val MAX_DIAGNOSTICS = 50

    fun parsePairResult(body: String): RemotePairResult? = try {
        val json = JSONObject(body)
        RemotePairResult(
            success = json.optBoolean("success", false),
            token = json.optStringOrNull("token"),
            error = json.optStringOrNull("error"),
            lockedOut = json.optBoolean("lockedOut", false),
        )
    } catch (error: JSONException) {
        note("pair", error.message ?: "invalid json")
        null
    }

    fun parseCommandResult(body: String): RemoteCommandResult? = try {
        val json = JSONObject(body)
        RemoteCommandResult(
            accepted = json.optBoolean("accepted", false),
            message = json.optStringOrNull("message"),
        )
    } catch (error: JSONException) {
        note("command", error.message ?: "invalid json")
        null
    }

    fun parseApprovalResolution(body: String): RemoteApprovalResolution? = try {
        val json = JSONObject(body)
        val decisionText = json.optStringOrNull("decision")
        val decision = ApprovalDecision.entries.firstOrNull {
            it.name.equals(decisionText, ignoreCase = true) || it.wireValue.equals(decisionText, ignoreCase = true)
        } ?: ApprovalDecision.Deny
        RemoteApprovalResolution(
            approvalId = json.optStringOrNull("approvalId").orEmpty(),
            toolCallId = json.optStringOrNull("toolCallId").orEmpty(),
            decision = decision,
            accepted = json.optBoolean("accepted", false),
            message = json.optStringOrNull("message"),
        )
    } catch (error: JSONException) {
        note("approval", error.message ?: "invalid json")
        null
    }

    fun parseStatus(body: String): RemoteServerStatus? = try {
        val json = JSONObject(body)
        RemoteServerStatus(
            running = json.optStringOrNull("state").equals("Running", ignoreCase = true),
            address = json.optStringOrNull("address"),
            port = json.optInt("port", 0),
            connectionCount = json.optInt("connectionCount", 0),
            pairingUrl = json.optStringOrNull("pairingUrl"),
            pin = json.optStringOrNull("pin"),
            tokenLifetimeText = json.opt("tokenLifetime")?.takeIf { it != JSONObject.NULL }?.toString(),
            startedAtUtc = json.optStringOrNull("startedAtUtc"),
        )
    } catch (error: JSONException) {
        note("status", error.message ?: "invalid json")
        null
    }

    fun parseSnapshot(body: String): RemoteSessionSnapshot? = try {
        parseSnapshotObject(JSONObject(body))
    } catch (error: JSONException) {
        note("snapshot", error.message ?: "invalid json")
        null
    }

    /** 期望 `snapshot` 类型；其他类型返回 null 并记录。 */
    fun parseUpdate(body: String): RemoteUpdate? = try {
        val json = JSONObject(body)
        when (val type = json.optStringOrNull("type")) {
            "snapshot" -> json.optJSONObject("session")?.let { RemoteUpdate.Snapshot(parseSnapshotObject(it)) }
                ?: recordUnknown("update", "snapshot 帧缺少 session")
            "entry" -> json.optJSONObject("entry")?.let { RemoteUpdate.Entry(parseEntry(it)) }
                ?: recordUnknown("update", "entry 帧缺少 entry")
            "approvalAdded" -> json.optJSONObject("approval")?.let { RemoteUpdate.ApprovalAdded(parseApproval(it)) }
                ?: recordUnknown("update", "approvalAdded 帧缺少 approval")
            "approvalResolved" -> json.optJSONObject("resolution")?.let {
                RemoteUpdate.ApprovalResolved(parseApprovalResolutionObject(it))
            } ?: recordUnknown("update", "approvalResolved 帧缺少 resolution")
            "status" -> RemoteUpdate.Status(
                activity = json.optStringOrNull("activity") ?: "Idle",
                connection = json.optStringOrNull("connection") ?: "Stopped",
                canSend = json.optBoolean("canSend", false),
                canStop = json.optBoolean("canStop", false),
                queuedCount = json.optInt("queuedCount", 0),
            )
            "notice" -> RemoteUpdate.Notice(json.optStringOrNull("message").orEmpty())
            null -> recordUnknown("update", "帧缺少 type 字段")
            else -> recordUnknown("update", "未知 type=$type")
        }
    } catch (error: JSONException) {
        note("update", error.message ?: "invalid json")
        null
    }

    private fun <T> recordUnknown(scope: String, detail: String): T? {
        note(scope, detail)
        return null
    }

    private fun parseSnapshotObject(json: JSONObject) = RemoteSessionSnapshot(
        sessionId = json.optStringOrNull("sessionId").orEmpty(),
        title = json.optStringOrNull("title").orEmpty(),
        activity = json.optStringOrNull("activity") ?: "Idle",
        connection = json.optStringOrNull("connection") ?: "Stopped",
        model = json.optStringOrNull("model"),
        canSend = json.optBoolean("canSend", false),
        canStop = json.optBoolean("canStop", false),
        queuedCount = json.optInt("queuedCount", 0),
        entries = json.optJSONArray("entries").mapObjects(::parseEntry),
        pendingApprovals = json.optJSONArray("pendingApprovals").mapObjects(::parseApproval),
    )

    private fun parseEntry(json: JSONObject) = RemoteTimelineEntry(
        id = json.optStringOrNull("id").orEmpty(),
        role = EntryRole.fromWire(json.optStringOrNull("role")),
        isStreaming = json.optBoolean("isStreaming", false),
        model = json.optStringOrNull("model"),
        blocks = json.optJSONArray("blocks").mapObjects(::parseBlock),
    )

    private fun parseBlock(json: JSONObject): RemoteBlock? = when (val kind = json.optStringOrNull("kind")) {
        "text" -> RemoteBlock.Text(json.optStringOrNull("text").orEmpty())
        "thinking" -> RemoteBlock.Thinking(json.optStringOrNull("text").orEmpty())
        "tool" -> json.optJSONObject("tool")?.let { RemoteBlock.Tool(parseToolCall(it)) }
            ?: recordUnknown("block", "tool 块缺少 tool 对象")
        "error" -> RemoteBlock.Error(json.optStringOrNull("message").orEmpty())
        null -> recordUnknown("block", "块缺少 kind 字段")
        else -> recordUnknown("block", "未知 kind=$kind")
    }

    private fun parseToolCall(json: JSONObject) = RemoteToolCall(
        toolCallId = json.optStringOrNull("toolCallId").orEmpty(),
        toolName = json.optStringOrNull("toolName").orEmpty(),
        argsJson = json.optStringOrNull("argsJson").orEmpty(),
        status = ToolStatus.fromWire(json.optStringOrNull("status")),
        statusText = json.optStringOrNull("statusText"),
        output = json.optStringOrNull("output"),
        isErrorOutput = json.optBoolean("isErrorOutput", false),
    )

    private fun parseApproval(json: JSONObject) = RemoteApprovalRequest(
        approvalId = json.optStringOrNull("approvalId").orEmpty(),
        toolCallId = json.optStringOrNull("toolCallId").orEmpty(),
        toolName = json.optStringOrNull("toolName").orEmpty(),
        argsJson = json.optStringOrNull("argsJson").orEmpty(),
        summary = json.optStringOrNull("summary"),
        requestedAtUtc = json.optStringOrNull("requestedAtUtc"),
    )

    private fun parseApprovalResolutionObject(json: JSONObject): RemoteApprovalResolution =
        parseApprovalResolution(json.toString()) ?: RemoteApprovalResolution(
            approvalId = json.optStringOrNull("approvalId").orEmpty(),
            toolCallId = json.optStringOrNull("toolCallId").orEmpty(),
            decision = ApprovalDecision.Deny,
            accepted = json.optBoolean("accepted", false),
            message = json.optStringOrNull("message"),
        )

    private fun JSONObject.optStringOrNull(name: String): String? {
        if (!has(name) || isNull(name)) return null
        return optString(name, "")
    }

    private inline fun <T> JSONArray?.mapObjects(transform: (JSONObject) -> T?): List<T> {
        if (this == null) return emptyList()
        val result = ArrayList<T>(length())
        for (index in 0 until length()) {
            val item = optJSONObject(index) ?: continue
            transform(item)?.let { result.add(it) }
        }
        return result
    }
}
