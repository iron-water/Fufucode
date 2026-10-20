package com.fufucode.mobile.data

import kotlinx.coroutines.flow.Flow

/**
 * 会话状态机依赖的传输抽象。
 *
 * 抽出接口是为了让 [MobileSessionController] 的状态迁移（时间线合并、审批单次有效、
 * 断线只读、代次淘汰旧回调）能在 JVM 单元测试中用内存实现验证，
 * 而不必搭真机或真实网络。
 */
interface RemoteTransport {
    suspend fun pair(baseUrl: String, pin: String): RemoteClient.Result<RemotePairResult>
    suspend fun status(baseUrl: String, token: String?): RemoteClient.Result<RemoteServerStatus>
    suspend fun snapshot(baseUrl: String, token: String?): RemoteClient.Result<RemoteSessionSnapshot>
    suspend fun sessions(baseUrl: String, token: String?): RemoteClient.Result<RemoteSessionCatalog>
    suspend fun switchSession(baseUrl: String, token: String?, sessionId: String): RemoteClient.Result<RemoteCommandResult>
    suspend fun sendMessage(baseUrl: String, token: String?, text: String): RemoteClient.Result<RemoteCommandResult>
    suspend fun stop(baseUrl: String, token: String?): RemoteClient.Result<RemoteCommandResult>
    suspend fun resolveApproval(
        baseUrl: String,
        token: String?,
        approvalId: String,
        decision: ApprovalDecision,
    ): RemoteClient.Result<RemoteApprovalResolution>

    /** 事件流；每次连接产生一个新的流，结束即表示需要重连。 */
    fun events(baseUrl: String, token: String): Flow<RemoteClient.EventStreamMessage>
}

class OkHttpRemoteTransport(private val client: RemoteClient = RemoteClient()) : RemoteTransport {
    override suspend fun pair(baseUrl: String, pin: String) = client.pair(baseUrl, pin)
    override suspend fun status(baseUrl: String, token: String?) = client.status(baseUrl, token)
    override suspend fun snapshot(baseUrl: String, token: String?) = client.snapshot(baseUrl, token)
    override suspend fun sessions(baseUrl: String, token: String?) = client.sessions(baseUrl, token)
    override suspend fun switchSession(baseUrl: String, token: String?, sessionId: String) =
        client.switchSession(baseUrl, token, sessionId)
    override suspend fun sendMessage(baseUrl: String, token: String?, text: String) =
        client.sendMessage(baseUrl, token, text)
    override suspend fun stop(baseUrl: String, token: String?) = client.stop(baseUrl, token)
    override suspend fun resolveApproval(
        baseUrl: String,
        token: String?,
        approvalId: String,
        decision: ApprovalDecision,
    ) = client.resolveApproval(baseUrl, token, approvalId, decision)

    override fun events(baseUrl: String, token: String) = client.events(baseUrl, token)
}
