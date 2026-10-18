// Fufucode 独立 Android 移动端：独立于桌面解决方案的 Gradle 根工程。
plugins {
    alias(libs.plugins.android.application) apply false
    alias(libs.plugins.kotlin.android) apply false
    alias(libs.plugins.kotlin.compose) apply false
}
