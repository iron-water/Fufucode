package com.fufucode.mobile.data

/**
 * 配对信息的存储抽象。
 *
 * 抽出接口的目的：会话状态机（[MobileSessionController]）可以在 JVM 单元测试里
 * 用内存实现验证，不必依赖 Android 的 SharedPreferences。
 */
interface PairingStorage {
    var baseUrl: String?
    var token: String?
    var lastSessionTitle: String?
    var lastPort: Int
    var lastModel: String?
    fun clearPairing()
    fun clearAll()
}
