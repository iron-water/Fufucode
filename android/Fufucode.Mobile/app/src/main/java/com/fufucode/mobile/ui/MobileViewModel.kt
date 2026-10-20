package com.fufucode.mobile.ui

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.fufucode.mobile.data.ConnectionStore
import com.fufucode.mobile.data.LanDiscovery
import com.fufucode.mobile.data.MobileSessionController
import com.fufucode.mobile.data.OkHttpRemoteTransport
import com.fufucode.mobile.data.PendingAttachment
import com.fufucode.mobile.data.RemoteClient
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/** 配对页的扫描/连接进度状态。 */
data class DiscoveryState(
    val scanning: Boolean = false,
    val scannedHosts: Int = 0,
    val totalHosts: Int = 0,
    val prefixes: List<String> = emptyList(),
    val ports: List<Int> = emptyList(),
    val found: List<String> = emptyList(),
    val finishedReason: String? = null,
)

/**
 * 界面层持有的状态容器。
 *
 * 状态来源分层明确：
 * - 会话/连接事实：来自 [MobileSessionController]（真网络）；
 * - 发现进度：来自 [LanDiscovery]（真扫描）；
 * - 本页临时输入：草稿与附件（不落盘，仅本次会话内存）。
 */
class MobileViewModel(application: Application) : AndroidViewModel(application) {

    private val store = ConnectionStore(application)
    private val transport = OkHttpRemoteTransport()
    private val lanDiscovery = LanDiscovery(RemoteClient())

    val controller = MobileSessionController(transport, store, viewModelScope)

    private val _draft = MutableStateFlow("")
    val draft: StateFlow<String> = _draft.asStateFlow()

    private val _attachments = MutableStateFlow<List<PendingAttachment>>(emptyList())
    val attachments: StateFlow<List<PendingAttachment>> = _attachments.asStateFlow()

    private val _discoveryState = MutableStateFlow(DiscoveryState())
    val discoveryState: StateFlow<DiscoveryState> = _discoveryState.asStateFlow()

    private var scanJob: Job? = null

    init {
        // 冷启动自动续连：仅有真实配对记录时才尝试，避免“默默绑定演示会话”。
        controller.connectIfPaired()
    }

    fun updateDraft(value: String) {
        _draft.value = value
    }

    fun addAttachment(attachment: PendingAttachment) {
        _attachments.update { current ->
            if (current.any { it.uri == attachment.uri }) current else current + attachment
        }
    }

    fun removeAttachment(uri: String) {
        _attachments.update { current -> current.filterNot { it.uri == uri } }
    }

    fun clearDraft() {
        _draft.value = ""
        _attachments.value = emptyList()
    }

    /**
     * 发送：把附件引用拼到正文前一并提交（远控协议只有文本通道）。
     * 只有在 [MobileSessionController.send] 真正受理后才清空输入，避免失败时丢字。
     */
    fun send() {
        val body = _draft.value.trim()
        if (body.isEmpty()) return
        val references = com.fufucode.mobile.data.buildAttachmentReferenceBlock(_attachments.value)
        val submittedDraft = _draft.value
        val submittedAttachments = _attachments.value
        controller.send(if (references.isEmpty()) body else references + "\n" + body) {
            // 等服务器受理，并且只清除此请求的草稿，保留等待期间的新输入。
            if (_draft.value == submittedDraft && _attachments.value == submittedAttachments) clearDraft()
        }
    }

    /** 用 4 位 PIN 配对指定地址。 */
    fun pair(baseUrl: String, pin: String) {
        viewModelScope.launch { controller.pairAndConnect(baseUrl, pin) }
    }

    /** 在当前局域网内自动发现桌面端。 */
    fun scanLan(rememberedPort: Int? = null, manualHost: String? = null) {
        if (scanJob?.isActive == true) return
        scanJob = viewModelScope.launch {
            _discoveryState.value = DiscoveryState(scanning = true)
            lanDiscovery
                .scan(rememberedPort = rememberedPort ?: store.lastPort.takeIf { it > 0 }, manualHost = manualHost)
                .collect { event ->
                    _discoveryState.update { current ->
                        when (event) {
                            is LanDiscovery.ScanEvent.Started -> current.copy(
                                prefixes = event.prefixes,
                                ports = event.ports,
                            )
                            is LanDiscovery.ScanEvent.Progress -> current.copy(
                                scannedHosts = event.scannedHosts,
                                totalHosts = event.totalHosts,
                            )
                            is LanDiscovery.ScanEvent.Found -> current.copy(
                                found = (current.found + event.baseUrl).distinct(),
                            )
                            is LanDiscovery.ScanEvent.Finished -> current.copy(
                                scanning = false,
                                finishedReason = event.reason,
                            )
                        }
                    }
                }
            _discoveryState.update { it.copy(scanning = false) }
        }
    }

    fun stopScan() {
        scanJob?.cancel()
        scanJob = null
        _discoveryState.update { it.copy(scanning = false) }
    }

    fun disconnect() = controller.disconnect()

    fun sendMessage(text: String): Boolean = controller.send(text)

    fun stop() = controller.stop()

    fun resolveApproval(approvalId: String, approve: Boolean) = controller.resolveApproval(
        approvalId,
        if (approve) com.fufucode.mobile.data.ApprovalDecision.Approve
        else com.fufucode.mobile.data.ApprovalDecision.Deny,
    )

    fun switchSession(sessionId: String) = controller.switchSession(sessionId)

    fun dismissNotice() = controller.dismissNotice()

    /**
     * 用一次真实的 `/api/status` 请求测量到桌面端的往返延迟。
     * 失败返回 null，界面显示“未测量”而不是伪造一个数值。
     */
    suspend fun measureLatency(): Long? {
        val base = controller.baseUrl ?: return null
        val token = store.token ?: return null
        val started = System.nanoTime()
        val result = transport.status(base, token)
        val elapsed = (System.nanoTime() - started) / 1_000_000
        return if (result is RemoteClient.Result.Ok) elapsed else null
    }

    override fun onCleared() {
        controller.release()
        super.onCleared()
    }
}
