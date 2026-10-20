package com.fufucode.mobile.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.withContext
import okhttp3.Call
import okhttp3.Callback
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONObject
import java.io.IOException
import java.util.concurrent.TimeUnit

/**
 * 桌面端内置局域网服务的手机侧客户端。
 *
 * 直接对接 `Fufucode.Core.RemoteControl.RemoteControlServer` 的既有端点：
 * `POST /api/pair`（PIN 换令牌）、`GET /api/status`、`GET /api/snapshot`、
 * `POST /api/message`、`POST /api/stop`、`POST /api/approval`、
 * `GET /api/events`（WebSocket 增量推送）。
 *
 * 桌面端实现为「请求-响应即关闭」的短连接 + 独立 WebSocket 推送，因此
 * HTTP 侧不做连接保持假设。
 */
class RemoteClient(
    private val okHttp: OkHttpClient = defaultHttpClient(),
) {
    private val jsonMediaType = "application/json; charset=utf-8".toMediaType()

    companion object {
        /** 远控服务免认证 Web UI 的页面标题标记；用于确认目标确为 Fufucode 桌面端。 */
        private const val PROBE_MARKER = "Fufucode 手机远控"

        fun defaultHttpClient(): OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(6, TimeUnit.SECONDS)
            .readTimeout(20, TimeUnit.SECONDS)
            .writeTimeout(20, TimeUnit.SECONDS)
            .pingInterval(20, TimeUnit.SECONDS)
            .build()
    }

    sealed interface Result<out T> {
        data class Ok<T>(val value: T) : Result<T>
        data class Http(val status: Int, val body: String, val message: String) : Result<Nothing>
        data class Network(val message: String, val cause: Throwable?) : Result<Nothing>
    }

    /** PIN 配对；不依赖已持有令牌。 */
    suspend fun pair(baseUrl: String, pin: String): Result<RemotePairResult> =
        post(baseUrl, "/api/pair", JSONObject().put("pin", pin), token = null).parse { body ->
            RemoteWire.parsePairResult(body)
        }

    suspend fun status(baseUrl: String, token: String?): Result<RemoteServerStatus> =
        get(baseUrl, "/api/status", token).parse { body -> RemoteWire.parseStatus(body) }

    suspend fun snapshot(baseUrl: String, token: String?): Result<RemoteSessionSnapshot> =
        get(baseUrl, "/api/snapshot", token).parse { body -> RemoteWire.parseSnapshot(body) }

    suspend fun sessions(baseUrl: String, token: String?): Result<RemoteSessionCatalog> =
        get(baseUrl, "/api/sessions", token).parse { body -> RemoteWire.parseSessionCatalog(body) }

    suspend fun switchSession(baseUrl: String, token: String?, sessionId: String): Result<RemoteCommandResult> =
        post(baseUrl, "/api/sessions/switch", JSONObject().put("sessionId", sessionId), token)
            .parse { body -> RemoteWire.parseCommandResult(body) }

    suspend fun sendMessage(baseUrl: String, token: String?, text: String): Result<RemoteCommandResult> =
        post(baseUrl, "/api/message", JSONObject().put("text", text), token).parse { body ->
            RemoteWire.parseCommandResult(body)
        }

    suspend fun stop(baseUrl: String, token: String?): Result<RemoteCommandResult> =
        post(baseUrl, "/api/stop", JSONObject(), token).parse { body -> RemoteWire.parseCommandResult(body) }

    suspend fun resolveApproval(
        baseUrl: String,
        token: String?,
        approvalId: String,
        decision: ApprovalDecision,
    ): Result<RemoteApprovalResolution> =
        post(
            baseUrl,
            "/api/approval",
            JSONObject().put("approvalId", approvalId).put("decision", decision.wireValue),
            token,
        ).parse { body -> RemoteWire.parseApprovalResolution(body) }

    /**
     * 订阅增量事件流。连接建立后桌面端立即下发一帧完整快照，随后推送增量。
     * 流结束（onClosed/onFailure）即代表需要重连；调用方据此退避重连并重新拉快照。
     */
    fun events(baseUrl: String, token: String): Flow<EventStreamMessage> = callbackFlow {
        val url = baseUrl.replaceFirst("http://", "ws://").replaceFirst("https://", "wss://") +
            "/api/events?token=" + java.net.URLEncoder.encode(token, "UTF-8")
        val request = Request.Builder().url(url).build()
        val socket = okHttp.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                trySend(EventStreamMessage.Connected)
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                if (trySend(EventStreamMessage.Payload(text)).isFailure) {
                    // 不静默丢增量；断开后重新获取完整快照。
                    webSocket.cancel()
                }
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                trySend(EventStreamMessage.Closed("closing($code)${reason.takeIf { it.isNotBlank() }?.let { ": $it" }.orEmpty()}"))
                webSocket.close(1000, null)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                trySend(EventStreamMessage.Closed("closed($code)"))
                close()
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                trySend(EventStreamMessage.Failed(t.message ?: t::class.java.simpleName))
                close()
            }
        })
        awaitClose { socket.cancel() }
    }

    sealed interface EventStreamMessage {
        data object Connected : EventStreamMessage
        data class Payload(val text: String) : EventStreamMessage
        data class Closed(val reason: String) : EventStreamMessage
        data class Failed(val message: String) : EventStreamMessage
    }

    /**
     * 探测某个主机是否为 Fufucode 桌面端远控服务。
     *
     * 依据：远控服务在**认证之前**提供 Web UI 外壳（`/`），页面标题为
     * 「Fufucode 手机远控」。因此对 `/` 做一次 GET 并按标记判定，
     * 不是随机猜测端口，也不使用任何云端广播。
     */
    suspend fun probeDesktop(host: String, port: Int, timeoutMillis: Long = 450): ProbeResult =
        withContext(Dispatchers.IO) {
            val client = okHttp.newBuilder()
                .connectTimeout(timeoutMillis, TimeUnit.MILLISECONDS)
                .readTimeout(timeoutMillis, TimeUnit.MILLISECONDS)
                .callTimeout(timeoutMillis, TimeUnit.MILLISECONDS)
                .build()
            val request = Request.Builder().url("http://$host:$port/").get().build()
            try {
                client.newCall(request).execute().use { response ->
                    if (!response.isSuccessful) return@withContext ProbeResult.NotFufucode(response.code)
                    val body = response.body?.string().orEmpty()
                    when {
                        PROBE_MARKER in body -> ProbeResult.Found("http://$host:$port")
                        else -> ProbeResult.NotFufucode(response.code)
                    }
                }
            } catch (error: IOException) {
                ProbeResult.Unreachable(error.message ?: "io error")
            }
        }

    sealed interface ProbeResult {
        data class Found(val baseUrl: String) : ProbeResult
        data class NotFufucode(val status: Int) : ProbeResult
        data class Unreachable(val message: String) : ProbeResult
    }

    private suspend fun get(baseUrl: String, path: String, token: String?): Raw =
        execute(baseUrl, path, token, body = null)

    private suspend fun post(baseUrl: String, path: String, body: JSONObject, token: String?): Raw =
        execute(baseUrl, path, token, body = body.toString())

    private suspend fun execute(baseUrl: String, path: String, token: String?, body: String?): Raw =
        withContext(Dispatchers.IO) {
            val builder = Request.Builder().url(baseUrl.trimEnd('/') + path)
            if (token != null) builder.header("Authorization", "Bearer $token")
            if (body == null) {
                builder.get()
            } else {
                builder.post(body.toRequestBody(jsonMediaType))
            }

            try {
                okHttp.newCall(builder.build()).execute().use { response ->
                    Raw.HttpResponse(response.code, response.body?.string().orEmpty())
                }
            } catch (error: IOException) {
                Raw.NetworkError(error.message ?: "网络错误", error)
            }
        }

    private sealed interface Raw {
        data class HttpResponse(val status: Int, val body: String) : Raw
        data class NetworkError(val message: String, val cause: Throwable) : Raw
    }

    private fun <T> Raw.parse(transform: (String) -> T?): Result<T> = when (this) {
        is Raw.NetworkError -> Result.Network(message, cause)
        is Raw.HttpResponse -> {
            if (status !in 200..299) {
                val hint = transform(body)?.let { describe(it) }
                Result.Http(status, body, hint ?: "服务端返回 $status")
            } else {
                val value = transform(body)
                if (value == null) Result.Http(status, body, "响应无法解析为预期结构") else Result.Ok(value)
            }
        }
    }

    private fun describe(value: Any): String? = when (value) {
        is RemotePairResult -> value.error
        is RemoteCommandResult -> value.message
        is RemoteApprovalResolution -> value.message
        else -> null
    }

}
