package com.fufucode.mobile.data

import java.net.URI
import java.net.URLDecoder
import java.net.URLEncoder
import java.util.UUID

data class UuPending(val kind: String, val nonce: String, val deviceId: String? = null)
data class UuRemoteState(val deviceId: String? = null, val pending: UuPending? = null)
data class UuCallback(val state: UuRemoteState, val message: String)

/** UU URL Scheme 4.42+。只负责跨 App 桌面远控，不是本地聊天的网络隧道。 */
class UuRemoteProtocol(private val callbackScheme: String) {
    private fun encode(value: String) = URLEncoder.encode(value, "UTF-8")
    private fun callback(host: String) = "$callbackScheme://$host"

    fun newBinding() = UuPending("bind", UUID.randomUUID().toString())
    fun newControl(deviceId: String) = UuPending("control", UUID.randomUUID().toString(), deviceId)

    fun bindUrl(pending: UuPending): String =
        "uuremote://external/device/authorize?callback=${encode(callback("bindresult"))}&nonce=${encode(pending.nonce)}"

    fun controlUrl(deviceId: String, pending: UuPending?): String {
        require(deviceId.isNotBlank())
        val base = "uuremote://external/device/control?anonymous_device_id=${encode(deviceId)}"
        // 全屏没有结果回调；小窗将请求身份放入 callback 原查询参数，由 UU 保留。
        return if (pending == null) "$base&window_type=0" else
            "$base&window_type=1&callback=${encode(callback("controlresult") + "?request_id=" + encode(pending.nonce))}"
    }

    fun receive(raw: String, current: UuRemoteState): UuCallback? = runCatching {
        if (raw.length > 32768) return null
        val uri = URI(raw)
        if (uri.scheme != callbackScheme || uri.rawAuthority != uri.host ||
            !uri.rawPath.isNullOrEmpty() || uri.rawFragment != null) return null
        val pairs = (uri.rawQuery ?: return null).split("&").map {
            val parts = it.split("=", limit = 2)
            URLDecoder.decode(parts[0], "UTF-8") to URLDecoder.decode(parts.getOrElse(1) { "" }, "UTF-8")
        }
        // 不选择重复字段的“第一项”或“最后一项”，避免状态含义不确定。
        if (pairs.map { it.first }.distinct().size != pairs.size) return null
        val params = pairs.toMap()
        val pending = current.pending ?: return null
        when {
            uri.host == "bindresult" && pending.kind == "bind" -> {
                if (params["nonce"] != pending.nonce) return null
                val id = params["anonymous_device_id"]
                val error = params["error"]
                if ((id != null) == (error != null)) return null
                if (id != null) {
                    if (id.isBlank() || id.length > 8192 || id.any { it.isISOControl() }) return null
                    UuCallback(UuRemoteState(deviceId = id), "电脑已绑定")
                } else {
                    if (error.isNullOrBlank()) return null
                    UuCallback(current.copy(pending = null), errorMessage(error))
                }
            }
            uri.host == "controlresult" && pending.kind == "control" -> {
                if (params["request_id"] != pending.nonce || pending.deviceId != current.deviceId) return null
                when (params["status"]) {
                    "success" -> {
                        if (params.containsKey("reason")) return null
                        UuCallback(current.copy(pending = null), "UU 已确认小窗启动")
                    }
                    "failure" -> {
                        val reason = params["reason"]?.takeIf { it.isNotBlank() } ?: return null
                        UuCallback(
                            current.copy(pending = null, deviceId = if (reason == "device_not_found") null else current.deviceId),
                            errorMessage(reason),
                        )
                    }
                    else -> null
                }
            }
            else -> null
        }
    }.getOrNull()

    companion object {
        fun errorMessage(code: String): String = when (code) {
            "no_device" -> "请先在 UU 中添加 Windows 或 Mac 电脑"
            "user_cancel" -> "已取消"
            "timeout", "login_timeout" -> "登录或设备加载超时，请重试"
            "not_logined", "logout" -> "请在 UU 中登录后重试"
            "superseded" -> "请求已被替换，请重试"
            "busy" -> "请先在 UU 中结束当前远控"
            "device_offline" -> "电脑离线，请开机或唤醒"
            "device_not_found" -> "绑定已失效，请重新绑定"
            "pip_unsupported" -> "小窗不可用，可改用全屏"
            "network_unavailable" -> "网络不可用，请检查网络"
            "not_allowed" -> "请在电脑端 UU 中允许被控"
            "connect_timeout" -> "连接超时，请重试"
            "invalid_request" -> "UU 无法处理请求，请更新客户端"
            else -> "UU 连接失败，请重试"
        }
    }
}
