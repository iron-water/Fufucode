package com.fufucode.mobile.ui

import android.app.Application
import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.lifecycle.AndroidViewModel
import com.fufucode.mobile.BuildConfig
import com.fufucode.mobile.data.UuPending
import com.fufucode.mobile.data.UuRemoteProtocol
import com.fufucode.mobile.data.UuRemoteState
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONObject

data class UuUiState(val binding: UuRemoteState, val message: String? = null)

class UuRemoteViewModel(application: Application) : AndroidViewModel(application) {
    private val prefs = application.getSharedPreferences("uu_remote", Context.MODE_PRIVATE)
    private val protocol = UuRemoteProtocol("${BuildConfig.APPLICATION_ID}.uu")
    private val mutable = MutableStateFlow(UuUiState(load()))
    val state = mutable.asStateFlow()
    private val callbackCounter = MutableStateFlow(0)
    val callbackVersion = callbackCounter.asStateFlow()

    private fun load(): UuRemoteState = runCatching {
        val json = JSONObject(prefs.getString("state", "{}")!!)
        val pending = json.optJSONObject("pending")?.let {
            UuPending(it.getString("kind"), it.getString("nonce"), it.optString("device").ifEmpty { null })
        }
        UuRemoteState(json.optString("device").ifEmpty { null }, pending)
    }.getOrDefault(UuRemoteState())

    private fun save(value: UuRemoteState, message: String? = null): Boolean {
        val json = JSONObject().put("device", value.deviceId)
        value.pending?.let {
            json.put("pending", JSONObject().put("kind", it.kind).put("nonce", it.nonce).put("device", it.deviceId))
        }
        // 先持久化请求身份，再离开本 App；系统回收进程后也能核对回调。
        if (!prefs.edit().putString("state", json.toString()).commit()) {
            this.message("无法保存 UU 绑定状态，请重试")
            return false
        }
        mutable.value = UuUiState(value, message)
        return true
    }

    private fun message(text: String?) { mutable.value = mutable.value.copy(message = text) }
    fun dismissMessage() = message(null)
    fun forget() { save(UuRemoteState(), "已移除本机 UU 绑定") }
    fun cancelWaiting() { save(mutable.value.binding.copy(pending = null), "已结束等待；UU 中的操作未停止") }

    fun receive(raw: String?) {
        if (raw == null) return
        val result = protocol.receive(raw, mutable.value.binding) ?: return
        if (save(result.state, result.message)) callbackCounter.value++
    }

    fun bind(context: Context) {
        if (mutable.value.binding.pending != null) return
        launch(context, protocol.newBinding(), binding = true)
    }

    fun control(context: Context, smallWindow: Boolean) {
        val current = mutable.value.binding
        if (current.pending != null) return
        val id = current.deviceId ?: return
        launch(context, if (smallWindow) protocol.newControl(id) else null, binding = false)
    }

    private fun launch(context: Context, pending: UuPending?, binding: Boolean) {
        val previous = mutable.value.binding
        val url = if (binding) protocol.bindUrl(pending!!) else protocol.controlUrl(previous.deviceId!!, pending)
        // 限定正式客户端，避免将绑定标识交给其他声明同一 scheme 的应用。
        val intent = Intent(Intent.ACTION_VIEW, Uri.parse(url)).setPackage("com.netease.uuremote")
        if (intent.resolveActivity(context.packageManager) == null) {
            message("请安装或更新 UU 远程至 4.42.0 及以上")
            return
        }
        if (!save(previous.copy(pending = pending))) return
        try {
            context.startActivity(intent)
            message(if (pending == null) "已打开 UU；全屏连接结果请在 UU 中查看" else "请在 UU 中完成操作")
        } catch (_: Exception) {
            save(previous, "无法打开 UU，请更新客户端后重试")
        }
    }

    fun install(context: Context) {
        try {
            context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse("market://details?id=com.netease.uuremote")))
        } catch (_: Exception) {
            message("无法打开应用市场，请手动安装 UU 远程 4.42.0 或更高版本")
        }
    }
}
