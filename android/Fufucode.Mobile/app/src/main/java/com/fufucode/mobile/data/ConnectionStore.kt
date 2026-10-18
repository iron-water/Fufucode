package com.fufucode.mobile.data

import android.content.Context
import android.content.SharedPreferences
import androidx.core.content.edit

/**
 * 已配对目标的本地持久化。
 *
 * 只保存「主机地址 + 令牌 + 最近一次已知的服务状态摘要」；
 * 不保存桌面端的会话正文（会话数据每次连接后由桌面端快照下发）。
 * 令牌过期或被桌面端撤销后，下一次请求会得到 401，界面据此要求重新配对。
 */
class ConnectionStore(context: Context) : PairingStorage {

    private val prefs: SharedPreferences =
        context.getSharedPreferences("fufucode.mobile.connection", Context.MODE_PRIVATE)

    override var baseUrl: String?
        get() = prefs.getString(KEY_BASE_URL, null)
        set(value) = prefs.edit { if (value == null) remove(KEY_BASE_URL) else putString(KEY_BASE_URL, value) }

    override var token: String?
        get() = prefs.getString(KEY_TOKEN, null)
        set(value) = prefs.edit { if (value == null) remove(KEY_TOKEN) else putString(KEY_TOKEN, value) }

    /** 最近一次连接成功时的桌面端会话标题，用于列表页在未连接时如实展示历史。 */
    override var lastSessionTitle: String?
        get() = prefs.getString(KEY_TITLE, null)
        set(value) = prefs.edit { if (value == null) remove(KEY_TITLE) else putString(KEY_TITLE, value) }

    /** 最近一次成功连接时解析到的桌面端端口；用于设备页在未连接时展示历史值。 */
    override var lastPort: Int
        get() = prefs.getInt(KEY_PORT, 0)
        set(value) = prefs.edit { putInt(KEY_PORT, value) }

    override var lastModel: String?
        get() = prefs.getString(KEY_MODEL, null)
        set(value) = prefs.edit { if (value == null) remove(KEY_MODEL) else putString(KEY_MODEL, value) }

    override fun clearPairing() = prefs.edit {
        remove(KEY_TOKEN)
        remove(KEY_BASE_URL)
    }

    override fun clearAll() = prefs.edit { clear() }

    private companion object {
        const val KEY_BASE_URL = "baseUrl"
        const val KEY_TOKEN = "token"
        const val KEY_TITLE = "lastSessionTitle"
        const val KEY_PORT = "lastPort"
        const val KEY_MODEL = "lastModel"
    }
}
