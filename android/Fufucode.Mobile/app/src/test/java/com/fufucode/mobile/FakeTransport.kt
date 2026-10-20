package com.fufucode.mobile

import com.fufucode.mobile.data.ApprovalDecision
import com.fufucode.mobile.data.PairingStorage
import com.fufucode.mobile.data.RemoteApprovalRequest
import com.fufucode.mobile.data.RemoteApprovalResolution
import com.fufucode.mobile.data.RemoteClient
import com.fufucode.mobile.data.RemoteCommandResult
import com.fufucode.mobile.data.RemotePairResult
import com.fufucode.mobile.data.RemoteServerStatus
import com.fufucode.mobile.data.RemoteSessionSnapshot
import com.fufucode.mobile.data.RemoteSessionCatalog
import com.fufucode.mobile.data.RemoteSessionInfo
import com.fufucode.mobile.data.RemoteTransport
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow

/** 内存配对存储，供 JVM 单元测试替换 SharedPreferences。 */
class MemoryPairingStorage : PairingStorage {
    override var baseUrl: String? = null
    override var token: String? = null
    override var lastSessionTitle: String? = null
    override var lastPort: Int = 0
    override var lastModel: String? = null

    override fun clearPairing() {
        baseUrl = null
        token = null
    }

    override fun clearAll() {
        clearPairing()
        lastSessionTitle = null
        lastPort = 0
        lastModel = null
    }
}

/**
 * 可编排的传输实现：让状态机测试可以精确制造「审批过期」「令牌失效」
 * 「事件流断开」等真实场景，而无需真机或网络。
 */
open class FakeTransport : RemoteTransport {
    var pairResult: RemoteClient.Result<RemotePairResult> =
        RemoteClient.Result.Ok(RemotePairResult(true, "token-1", null, false))
    var snapshotResult: RemoteClient.Result<RemoteSessionSnapshot> =
        RemoteClient.Result.Ok(emptySnapshot())
    var statusResult: RemoteClient.Result<RemoteServerStatus> = RemoteClient.Result.Network("not stubbed", null)
    var sendResult: RemoteClient.Result<RemoteCommandResult> =
        RemoteClient.Result.Ok(RemoteCommandResult(true, null))
    var stopResult: RemoteClient.Result<RemoteCommandResult> =
        RemoteClient.Result.Ok(RemoteCommandResult(true, null))
    var approvalResult: RemoteClient.Result<RemoteApprovalResolution> =
        RemoteClient.Result.Ok(RemoteApprovalResolution("a", "t", ApprovalDecision.Approve, true, null))
    var sessionsResult: RemoteClient.Result<RemoteSessionCatalog> =
        RemoteClient.Result.Ok(RemoteSessionCatalog("", emptyList()))
    var switchResult: RemoteClient.Result<RemoteCommandResult> =
        RemoteClient.Result.Ok(RemoteCommandResult(true, "已切换"))

    /** 提交调用的计数，用于断言在途去重确实生效。 */
    var sendCount = 0
    var stopCount = 0
    var approvalCount = 0
    var snapshotCount = 0
    var sessionsCount = 0
    var switchCount = 0

    /** 每次调用额外延时的毫秒数，用于构造在途窗口。 */
    var callDelayMillis: Long = 0

    /**
     * 显式闸门：非 null 时 [sendMessage] 会等待它完成再返回。
     * 相比按时间构造在途窗口，闸门是确定性的，不受虚拟时间调度影响。
     */
    var sendGate: kotlinx.coroutines.CompletableDeferred<Unit>? = null
    var pairGate: kotlinx.coroutines.CompletableDeferred<Unit>? = null

    /** 显式闸门：非 null 时 [resolveApproval] 会等待它完成再返回。 */
    var approvalGate: kotlinx.coroutines.CompletableDeferred<Unit>? = null

    /**
     * 每次 [events] 调用创建独立的通道，模拟「每次重连都是一条新连接」。
     *
     * 这一点对代次淘汰测试是必要的：真实世界里旧连接的推送只属于旧 socket；
     * 若所有连接共享一个通道，新连接会读到本该属于旧连接的帧，测试会得出错误结论。
     */
    private val channels = java.util.concurrent.CopyOnWriteArrayList<Channel<RemoteClient.EventStreamMessage>>()

    /** 最近一次建立的连接（即当前活动事件流）。 */
    val currentStream: Channel<RemoteClient.EventStreamMessage>
        get() = channels.lastOrNull() ?: error("事件流尚未建立")

    /** 第 n 次建立的连接（从 0 开始）；用于向旧连接投递过期事件。 */
    fun streamAt(index: Int): Channel<RemoteClient.EventStreamMessage> = channels[index]

    val streamCount: Int get() = channels.size

    /** 事件流是否在被收集后立即正常结束。 */
    var closeStreamImmediately = false

    override suspend fun pair(baseUrl: String, pin: String): RemoteClient.Result<RemotePairResult> {
        pairGate?.await()
        delay(callDelayMillis)
        return pairResult
    }

    override suspend fun status(baseUrl: String, token: String?): RemoteClient.Result<RemoteServerStatus> {
        delay(callDelayMillis)
        return statusResult
    }

    override suspend fun snapshot(baseUrl: String, token: String?): RemoteClient.Result<RemoteSessionSnapshot> {
        delay(callDelayMillis)
        snapshotCount++
        return snapshotResult
    }

    override suspend fun sessions(baseUrl: String, token: String?): RemoteClient.Result<RemoteSessionCatalog> {
        delay(callDelayMillis)
        sessionsCount++
        return sessionsResult
    }

    override suspend fun switchSession(
        baseUrl: String,
        token: String?,
        sessionId: String,
    ): RemoteClient.Result<RemoteCommandResult> {
        delay(callDelayMillis)
        switchCount++
        return switchResult
    }

    override suspend fun sendMessage(
        baseUrl: String,
        token: String?,
        text: String,
    ): RemoteClient.Result<RemoteCommandResult> {
        sendCount++
        sendGate?.await()
        delay(callDelayMillis)
        return sendResult
    }

    override suspend fun stop(baseUrl: String, token: String?): RemoteClient.Result<RemoteCommandResult> {
        stopCount++
        delay(callDelayMillis)
        return stopResult
    }

    override suspend fun resolveApproval(
        baseUrl: String,
        token: String?,
        approvalId: String,
        decision: ApprovalDecision,
    ): RemoteClient.Result<RemoteApprovalResolution> {
        approvalCount++
        approvalGate?.await()
        delay(callDelayMillis)
        return approvalResult
    }

    override fun events(baseUrl: String, token: String): Flow<RemoteClient.EventStreamMessage> = flow {
        val channel = Channel<RemoteClient.EventStreamMessage>(Channel.UNLIMITED)
        channels.add(channel)
        emit(RemoteClient.EventStreamMessage.Connected)
        if (closeStreamImmediately) {
            emit(RemoteClient.EventStreamMessage.Closed("test closed"))
            return@flow
        }

        for (message in channel) emit(message)
    }

    companion object {
        fun emptySnapshot(
            title: String = "测试会话",
            canSend: Boolean = true,
            canStop: Boolean = false,
            approvals: List<RemoteApprovalRequest> = emptyList(),
        ) = RemoteSessionSnapshot(
            sessionId = "s-1",
            title = title,
            activity = "Idle",
            connection = "Connected",
            model = "test-model",
            canSend = canSend,
            canStop = canStop,
            queuedCount = 0,
            entries = emptyList(),
            pendingApprovals = approvals,
        )
    }
}
