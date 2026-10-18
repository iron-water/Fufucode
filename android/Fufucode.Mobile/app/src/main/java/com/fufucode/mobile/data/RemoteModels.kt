package com.fufucode.mobile.data

/**
 * 桌面端 `Fufucode.Core.RemoteControl` 线协议的**手机侧投影**。
 *
 * 字段名与桌面 `RemoteControlModels.cs` / `RemoteControlUpdates.cs` 一一对应；
 * 枚举按名称上线（桌面已启用 `JsonStringEnumConverter`），因此手机端按字符串比较。
 */

enum class EntryRole { User, Assistant;
    companion object {
        fun fromWire(value: String?): EntryRole =
            if (value.equals("User", ignoreCase = true)) User else Assistant
    }
}

enum class ToolStatus { Pending, AwaitingApproval, Running, Completed, Failed, Cancelled;
    companion object {
        fun fromWire(value: String?): ToolStatus =
            entries.firstOrNull { it.name.equals(value, ignoreCase = true) } ?: Pending
    }
}

enum class ApprovalDecision(val wireValue: String) {
    Approve("approve"),
    Deny("deny"),
}

/** 消息内容块；未知 `kind` 由解析层丢弃并记录，不让新版本协议打崩旧客户端。 */
sealed interface RemoteBlock {
    data class Text(val text: String) : RemoteBlock
    data class Thinking(val text: String) : RemoteBlock
    data class Tool(val call: RemoteToolCall) : RemoteBlock
    data class Error(val message: String) : RemoteBlock
}

data class RemoteToolCall(
    val toolCallId: String,
    val toolName: String,
    val argsJson: String,
    val status: ToolStatus,
    val statusText: String?,
    val output: String?,
    val isErrorOutput: Boolean,
)

data class RemoteTimelineEntry(
    val id: String,
    val role: EntryRole,
    val isStreaming: Boolean,
    val model: String?,
    val blocks: List<RemoteBlock>,
)

data class RemoteApprovalRequest(
    val approvalId: String,
    val toolCallId: String,
    val toolName: String,
    val argsJson: String,
    val summary: String?,
    val requestedAtUtc: String?,
)

data class RemoteApprovalResolution(
    val approvalId: String,
    val toolCallId: String,
    val decision: ApprovalDecision,
    val accepted: Boolean,
    val message: String?,
)

data class RemoteSessionSnapshot(
    val sessionId: String,
    val title: String,
    val activity: String,
    val connection: String,
    val model: String?,
    val canSend: Boolean,
    val canStop: Boolean,
    val queuedCount: Int,
    val entries: List<RemoteTimelineEntry>,
    val pendingApprovals: List<RemoteApprovalRequest>,
)

data class RemoteCommandResult(val accepted: Boolean, val message: String?)

data class RemotePairResult(val success: Boolean, val token: String?, val error: String?, val lockedOut: Boolean)

/** 桌面 `/api/status` 投影；`address`/`port` 即“当前已连接桌面端 IP、端口”。 */
data class RemoteServerStatus(
    val running: Boolean,
    val address: String?,
    val port: Int,
    val connectionCount: Int,
    val pairingUrl: String?,
    val pin: String?,
    val tokenLifetimeText: String?,
    val startedAtUtc: String?,
)

/** WebSocket 增量事件。 */
sealed interface RemoteUpdate {
    data class Snapshot(val session: RemoteSessionSnapshot) : RemoteUpdate
    data class Entry(val entry: RemoteTimelineEntry) : RemoteUpdate
    data class ApprovalAdded(val approval: RemoteApprovalRequest) : RemoteUpdate
    data class ApprovalResolved(val resolution: RemoteApprovalResolution) : RemoteUpdate
    data class Status(
        val activity: String,
        val connection: String,
        val canSend: Boolean,
        val canStop: Boolean,
        val queuedCount: Int,
    ) : RemoteUpdate
    data class Notice(val message: String) : RemoteUpdate
}

/** 解析过程中遇到无法识别的线协议内容时的诊断记录（不静默丢弃）。 */
data class WireDiagnostic(val scope: String, val detail: String)
