package com.fufucode.mobile

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import com.fufucode.mobile.ui.MobileApp

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // 边到边显示：内容自行处理状态栏/导航栏内边距，符合现代 Android 手势导航。
        enableEdgeToEdge()
        setContent { MobileApp() }
    }
}
