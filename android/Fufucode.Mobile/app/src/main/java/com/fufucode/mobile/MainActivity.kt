package com.fufucode.mobile

import android.os.Bundle
import android.content.Intent
import androidx.lifecycle.ViewModelProvider
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import com.fufucode.mobile.ui.MobileApp
import com.fufucode.mobile.ui.UuRemoteViewModel

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // 边到边显示：内容自行处理状态栏/导航栏内边距，符合现代 Android 手势导航。
        enableEdgeToEdge()
        consumeUuCallback(intent)
        setContent { MobileApp() }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        consumeUuCallback(intent)
    }

    private fun consumeUuCallback(intent: Intent) {
        if (intent.action != Intent.ACTION_VIEW) return
        ViewModelProvider(this)[UuRemoteViewModel::class.java].receive(intent.dataString)
        intent.data = null
    }
}
