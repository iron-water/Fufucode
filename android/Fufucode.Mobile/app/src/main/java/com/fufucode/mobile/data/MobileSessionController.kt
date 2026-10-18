package com.fufucode.mobile.data

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/** 移动端连接状态。界面的可操作性只由此状态驱动。 */
enum class LinkState { Idle, Connecting, Online, Reconnecting, Offline }

/** 一条时间线条目的本地合并结果。 */
data class TimelineEntryState(val entry: RemoteTimelineEntry)

/** 审批卡片的本地视图状态（含失效标记，失效不得显示为成功）。 */
data class ApprovalState(
    val request: RemoteApprovalRequest,
    val inFlight: Boolean = false,
    val resolutionFailedReason: String? = null,
)

data class MobileState(
    val link: LinkState = LinkState.Idle,
    val baseUrl: String? = null,
    val sessionId: String? = null,
    val title: String = "",
    val activity: String = "Idle",
    val connection: String = "Stopped",
    val model: String? = null,
    val canSend: Boolean = false,
    val canStop: Boolean = false,
    val queuedCount: Int = 0,
    val timeline: List<TimelineEntryState> = emptyList(),
    val approvals: List<ApprovalState> = emptyList(),
    val notice: String? = null,
    val reconnectAttempt: Int = 0,
    val server: RemoteServerStatus? = null,
    val sendInFlight: Boolean = false,
    val stopInFlight: Boolean = false,
    val diagnostics: List<WireDiagnostic> = emptyList(),
) {
    /** 只有真正在线且服务端声明可发送时才允许写入；其余状态一律只读。 */
    val writable: Boolean get() = link == LinkState.Online && canSend
    val stoppable: Boolean get() = link == LinkState.Online && canStop
    val pendingApprovalCount: Int get() = approvals.count { it.resolutionFailedReason == null }
}

/**
 * 手机端会话状态机。
 *
 * 职责：
 * 1. 配对（PIN 换令牌）与令牌持久化；
 * 2. 事件流消费：按 id 覆盖条目、审批新增/解除；
 * 3. 连接代次：每次重连递增，旧连接的回调在写状态前比对代次后丢弃；
 * 4. 发送/停止/审批的在途去重：同一动作未返回前重复触发不会重复提交；
 * 5. 断线退避重连并重新拉取快照补齐。
 *
 * 明确不做：不伪造成功、不把“已受理”显示为“已完成”、离线时不假装可发送。
 */
class MobileSessionController(
    private val transport: RemoteTransport,
    private val storage: PairingStorage,
    private val scope: CoroutineScope,
    private val reconnectDelaysMillis: List<Long> = listOf(600, 1_200, 2_400, 4_000, 6_000, 8_000),
) {
    private val _state = MutableStateFlow(
        MobileState(
            baseUrl = storage.baseUrl,
            title = storage.lastSessionTitle.orEmpty(),
            model = storage.lastModel,
        ),
    )
    val state: StateFlow<MobileState> = _state.asStateFlow()

    /**
     * 连接代次：每次启动连接、断开或重连递增。
     * 异步回调写状态前比对代次，不符即丢弃，避免旧连接的过期数据覆盖新状态。
     */
    private var generation = 0
    private var streamJob: Job? = null
    private var reconnectJob: Job? = null

    val baseUrl: String? get() = storage.baseUrl
    val hasPairing: Boolean
        get() = !storage.baseUrl.isNullOrBlank() && !storage.token.isNullOrBlank()

    /** 使用 4 位 PIN 配对并立即建立连接；成功后写入持久化供冷启动恢复。 */
    suspend fun pairAndConnect(baseUrl: String, pin: String): Boolean {
        val normalized = normalizeBaseUrl(baseUrl)
        if (normalized == null) {
            setNotice("地址无效：请填写形如 192.168.1.5:8080 的主机与端口")
            return false
        }

        reconnectJob?.cancel()
        streamJob?.cancel()
        val mine = ++generation
        _state.update { MobileState(link = LinkState.Connecting, baseUrl = normalized) }
        val result = transport.pair(normalized, pin)
        if (mine != generation) return false
        return when (result) {
            is RemoteClient.Result.Ok -> {
                val token = result.value.token
                if (!result.value.success || token.isNullOrBlank()) {
                    _state.update {
                        it.copy(
                            link = LinkState.Idle,
                            notice = result.value.error ?: "配对失败：服务端未返回令牌",
                        )
                    }
                    false
                } else {
                    storage.baseUrl = normalized
                    storage.token = token
                    _state.update { it.copy(baseUrl = normalized) }
                    connect(forceFresh = true)
                    true
                }
            }
            is RemoteClient.Result.Http -> {
                _state.update {
                    it.copy(
                        link = LinkState.Idle,
                        notice = result.message.ifBlank { "配对失败（HTTP ${result.status}）" },
                    )
                }
                false
            }
            is RemoteClient.Result.Network -> {
                _state.update {
                    it.copy(link = LinkState.Idle, notice = "无法连接 $normalized：${result.message}")
                }
                false
            }
        }
    }

    /** 用已保存的配对信息连接（冷启动恢复）；无配对记录时保持 Idle。 */
    fun connectIfPaired() {
        if (hasPairing) connect(forceFresh = true)
    }

    /** 建立连接并订阅事件流。重复调用安全：先取消既有连接。 */
    fun connect(forceFresh: Boolean = false) {
        val base = storage.baseUrl
        val token = storage.token
        if (base.isNullOrBlank() || token.isNullOrBlank()) {
            _state.update { it.copy(link = LinkState.Idle, notice = "尚未配对，请先输入 4 位配对码") }
            return
        }

        reconnectJob?.cancel()
        streamJob?.cancel()
        val mine = ++generation
        _state.update {
            it.copy(
                link = if (forceFresh) LinkState.Connecting else LinkState.Reconnecting,
                baseUrl = base,
                notice = null,
                sendInFlight = false,
                stopInFlight = false,
                canSend = false,
                canStop = false,
            )
        }

        streamJob = scope.launch {
            // 先拉一次快照：即便事件流首帧丢失，也能得到确定的初始状态。
            when (val snapshot = transport.snapshot(base, token)) {
                is RemoteClient.Result.Ok -> {
                    if (mine != generation) return@launch
                    applySnapshot(snapshot.value)
                }
                is RemoteClient.Result.Http -> {
                    if (mine != generation) return@launch
                    if (snapshot.status == 401) {
                        onTokenRejected()
                        return@launch
                    }

                    _state.update { it.copy(notice = "读取会话快照失败：${snapshot.message}") }
                }
                is RemoteClient.Result.Network -> {
                    if (mine != generation) return@launch
                    _state.update { it.copy(notice = "读取会话快照失败：${snapshot.message}") }
                }
            }

            refreshServerStatus(base, token, mine)
            if (mine != generation) return@launch

            transport.events(base, token).collect { message ->
                if (mine != generation) return@collect
                when (message) {
                    RemoteClient.EventStreamMessage.Connected ->
                        _state.update { it.copy(link = LinkState.Online, reconnectAttempt = 0, notice = null) }
                    is RemoteClient.EventStreamMessage.Payload -> applyPayload(message.text, mine)
                    is RemoteClient.EventStreamMessage.Closed -> onStreamEnded(mine, message.reason)
                    is RemoteClient.EventStreamMessage.Failed -> onStreamEnded(mine, message.message)
                }
            }
            // 通道拥塞时关闭通知也可能未入队；流结束本身同样要求重连补快照。
            onStreamEnded(mine, "事件流已结束")
        }
    }

    private suspend fun refreshServerStatus(base: String, token: String, mine: Int) {
        when (val status = transport.status(base, token)) {
            is RemoteClient.Result.Ok -> {
                if (mine != generation) return
                storage.lastPort = status.value.port
                _state.update { it.copy(server = status.value) }
            }
            is RemoteClient.Result.Http -> {
                if (mine == generation && status.status == 401) onTokenRejected()
            }
            is RemoteClient.Result.Network -> Unit
        }
    }

    private fun onStreamEnded(mine: Int, reason: String) {
        if (mine != generation) return
        _state.update { it.copy(link = LinkState.Reconnecting, canSend = false, canStop = false) }
        scheduleReconnect(mine, reason)
    }

    private fun scheduleReconnect(mine: Int, reason: String) {
        if (reconnectJob?.isActive == true) return
        reconnectJob = scope.launch {
            val attempt = _state.value.reconnectAttempt
            val delayMillis = reconnectDelaysMillis[attempt.coerceAtMost(reconnectDelaysMillis.lastIndex)]
            _state.update {
                it.copy(
                    link = LinkState.Reconnecting,
                    reconnectAttempt = attempt + 1,
                    notice = "连接中断（$reason），${delayMillis} 毫秒后重试",
                )
            }
            delay(delayMillis)
            if (mine != generation) return@launch
            connect()
        }
    }

    /** 令牌被服务端拒绝：清空本地令牌并要求重新配对，不反复重试无效令牌。 */
    private fun onTokenRejected() {
        streamJob?.cancel()
        reconnectJob?.cancel()
        generation++
        storage.clearPairing()
        _state.update {
            it.copy(
                link = LinkState.Offline,
                canSend = false,
                canStop = false,
                approvals = emptyList(),
                sendInFlight = false,
                stopInFlight = false,
                notice = "配对令牌已失效，请在电脑端读取新的 4 位配对码后重新配对",
            )
        }
    }

    /** 主动断开：清除本机令牌并回到未配对状态（设备页“一键断开”）。 */
    fun disconnect() {
        streamJob?.cancel()
        reconnectJob?.cancel()
        generation++
        storage.clearPairing()
        _state.update {
            MobileState(link = LinkState.Idle, notice = "已断开与电脑的连接，本机令牌已清除")
        }
    }

    fun release() {
        streamJob?.cancel()
        reconnectJob?.cancel()
        generation++
    }

    /**
     * 提交一条用户消息；在途去重，未返回前重复触发不会重复提交。
     *
     * @return true 表示本次请求已排入发送流程；服务端受理后才调用 onAccepted。false 表示因不可发送、
     * 在途去重或缺少凭据而未提交，调用方应保留用户输入。
     */
    fun send(text: String, onAccepted: () -> Unit = {}): Boolean {
        val content = text.trim()
        if (content.isEmpty()) return false
        if (!_state.value.writable) {
            setNotice("当前不可发送：连接未就绪或会话处于不可发送状态")
            return false
        }
        if (_state.value.sendInFlight) return false

        val base = storage.baseUrl ?: return false
        val token = storage.token ?: return false
        val mine = generation
        _state.update { it.copy(sendInFlight = true) }
        scope.launch {
            try {
                when (val result = transport.sendMessage(base, token, content)) {
                    is RemoteClient.Result.Ok -> {
                        if (mine != generation) return@launch
                        if (!result.value.accepted) {
                            _state.update { it.copy(notice = result.value.message ?: "消息未被受理") }
                        } else onAccepted()
                    }
                    is RemoteClient.Result.Http -> {
                        if (mine != generation) return@launch
                        if (result.status == 401) onTokenRejected()
                        else _state.update { it.copy(notice = result.message) }
                    }
                    is RemoteClient.Result.Network -> {
                        if (mine != generation) return@launch
                        _state.update { it.copy(notice = "发送失败：${result.message}") }
                    }
                }
            } finally {
                if (mine == generation) _state.update { it.copy(sendInFlight = false) }
            }
        }
        return true
    }

    /** 紧急停止：清空桌面端排队消息并中止在途一轮。 */
    fun stop() {
        if (!_state.value.stoppable) {
            setNotice("当前没有可停止的在途任务")
            return
        }
        if (_state.value.stopInFlight) return

        val base = storage.baseUrl ?: return
        val token = storage.token ?: return
        val mine = generation
        _state.update { it.copy(stopInFlight = true) }
        scope.launch {
            try {
                when (val result = transport.stop(base, token)) {
                    is RemoteClient.Result.Ok -> {
                        if (mine != generation) return@launch
                        _state.update {
                            it.copy(
                                notice = if (result.value.accepted) "已发送紧急停止" else result.value.message,
                            )
                        }
                    }
                    is RemoteClient.Result.Http -> {
                        if (mine != generation) return@launch
                        if (result.status == 401) onTokenRejected()
                        else _state.update { it.copy(notice = result.message) }
                    }
                    is RemoteClient.Result.Network -> {
                        if (mine != generation) return@launch
                        _state.update { it.copy(notice = "停止请求未送达：${result.message}") }
                    }
                }
            } finally {
                if (mine == generation) _state.update { it.copy(stopInFlight = false) }
            }
        }
    }

    /**
     * 提交一次审批决定（单次有效语义）。
     *
     * 无论服务端接受与否，本地立即把该卡片置为在途，阻止重复点击；
     * 服务端返回 accepted=false（审批已过期或已被桌面端处理）时保留卡片并标注
     * 失效原因，不显示为“已批准”。
     */
    fun resolveApproval(approvalId: String, decision: ApprovalDecision) {
        val current = _state.value.approvals.firstOrNull { it.request.approvalId == approvalId }
        if (current == null) {
            setNotice("该审批已不在待处理列表中")
            return
        }
        if (current.inFlight) return
        if (_state.value.link != LinkState.Online) {
            setNotice("离线状态无法审批：请在恢复连接后重试")
            return
        }

        val base = storage.baseUrl ?: return
        val token = storage.token ?: return
        val mine = generation
        _state.update { state ->
            state.copy(
                approvals = state.approvals.map {
                    if (it.request.approvalId == approvalId) it.copy(inFlight = true) else it
                },
            )
        }

        scope.launch {
            val result = transport.resolveApproval(base, token, approvalId, decision)
            if (mine != generation) return@launch
            when (result) {
                is RemoteClient.Result.Ok -> {
                    val resolution = result.value
                    if (resolution.accepted) {
                        _state.update { state ->
                            state.copy(
                                approvals = state.approvals.filterNot {
                                    it.request.approvalId == approvalId
                                },
                                notice = if (decision == ApprovalDecision.Approve) {
                                    "已批准一次执行：" + current.request.toolName
                                } else {
                                    "已拒绝：" + current.request.toolName
                                },
                            )
                        }
                    } else {
                        _state.update { state ->
                            state.copy(
                                approvals = state.approvals.map {
                                    if (it.request.approvalId == approvalId) {
                                        it.copy(
                                            inFlight = false,
                                            resolutionFailedReason = resolution.message ?: "审批已失效",
                                        )
                                    } else {
                                        it
                                    }
                                },
                                notice = resolution.message ?: "审批已失效：可能已在电脑端处理或已超时",
                            )
                        }
                    }
                }
                is RemoteClient.Result.Http -> {
                    if (result.status == 401) {
                        onTokenRejected()
                    } else {
                        _state.update { state ->
                            state.copy(
                                approvals = state.approvals.map {
                                    if (it.request.approvalId == approvalId) {
                                        it.copy(inFlight = false, resolutionFailedReason = result.message)
                                    } else {
                                        it
                                    }
                                },
                                notice = result.message,
                            )
                        }
                    }
                }
                is RemoteClient.Result.Network -> {
                    _state.update { state ->
                        state.copy(
                            approvals = state.approvals.map {
                                if (it.request.approvalId == approvalId) {
                                    it.copy(inFlight = false, resolutionFailedReason = "请求未送达")
                                } else {
                                    it
                                }
                            },
                            notice = "审批请求未送达：${result.message}",
                        )
                    }
                }
            }
        }
    }

    fun dismissNotice() = _state.update { it.copy(notice = null) }

    private fun setNotice(text: String) = _state.update { it.copy(notice = text) }

    private fun applySnapshot(snapshot: RemoteSessionSnapshot) {
        _state.update { applySnapshotTo(it, snapshot) }
    }

    private fun applyPayload(text: String, mine: Int) {
        if (mine != generation) return
        val update = RemoteWire.parseUpdate(text)
        _state.update { current ->
            val next = if (update == null) {
                current
            } else {
                when (update) {
                    is RemoteUpdate.Snapshot -> applySnapshotTo(current, update.session)
                    is RemoteUpdate.Entry -> current.copy(timeline = mergeEntry(current.timeline, update.entry))
                    is RemoteUpdate.ApprovalAdded -> {
                        val exists = current.approvals.any {
                            it.request.approvalId == update.approval.approvalId
                        }
                        if (exists) current else current.copy(approvals = current.approvals + ApprovalState(update.approval))
                    }
                    is RemoteUpdate.ApprovalResolved -> current.copy(
                        approvals = current.approvals.filterNot {
                            it.request.approvalId == update.resolution.approvalId
                        },
                    )
                    is RemoteUpdate.Status -> current.copy(
                        activity = update.activity,
                        connection = update.connection,
                        canSend = update.canSend,
                        canStop = update.canStop,
                        queuedCount = update.queuedCount,
                    )
                    is RemoteUpdate.Notice -> current.copy(notice = update.message)
                }
            }
            next.copy(diagnostics = RemoteWire.diagnostics_)
        }
    }

    private fun applySnapshotTo(current: MobileState, snapshot: RemoteSessionSnapshot): MobileState {
        storage.lastSessionTitle = snapshot.title
        storage.lastModel = snapshot.model
        return current.copy(
            sessionId = snapshot.sessionId,
            title = snapshot.title,
            activity = snapshot.activity,
            connection = snapshot.connection,
            model = snapshot.model,
            canSend = snapshot.canSend,
            canStop = snapshot.canStop,
            queuedCount = snapshot.queuedCount,
            timeline = snapshot.entries.map { TimelineEntryState(it) },
            approvals = snapshot.pendingApprovals.map { ApprovalState(it) },
        )
    }

    companion object {
        /** 按 id 覆盖；新条目追加。桌面端约定同一 id 即同一条目。 */
        fun mergeEntry(
            timeline: List<TimelineEntryState>,
            entry: RemoteTimelineEntry,
        ): List<TimelineEntryState> {
            val index = timeline.indexOfFirst { it.entry.id == entry.id }
            return if (index < 0) {
                timeline + TimelineEntryState(entry)
            } else {
                timeline.toMutableList().also { it[index] = TimelineEntryState(entry) }
            }
        }

        /**
         * 规范化用户输入的基地址：接受 `192.168.1.5:8080`、`http://192.168.1.5:8080/` 等写法，
         * 统一补 scheme 并去掉尾斜杠。返回 null 表示无法解释为可用地址（不猜测默认端口）。
         */
        fun normalizeBaseUrl(raw: String): String? {
            val trimmed = raw.trim().removeSuffix("/")
            if (trimmed.isEmpty()) return null
            val withScheme = when {
                trimmed.startsWith("http://") || trimmed.startsWith("https://") -> trimmed
                else -> "http://$trimmed"
            }
            val uri = try { java.net.URI(withScheme) } catch (_: Exception) { return null }
            if (uri.scheme !in listOf("http", "https") || uri.host.isNullOrBlank() ||
                uri.rawUserInfo != null || uri.rawQuery != null || uri.rawFragment != null ||
                !uri.rawPath.isNullOrEmpty() || uri.port !in 1..65535) return null
            return withScheme
        }
    }
}
