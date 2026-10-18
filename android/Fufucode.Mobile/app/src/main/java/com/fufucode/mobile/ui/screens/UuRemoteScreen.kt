package com.fufucode.mobile.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.fufucode.mobile.design.MobileColors
import com.fufucode.mobile.design.MobileTypography
import com.fufucode.mobile.design.WorkBuddyAsset
import com.fufucode.mobile.ui.UuRemoteViewModel
import com.fufucode.mobile.ui.components.InfoBanner
import com.fufucode.mobile.ui.components.MobileTopBar
import com.fufucode.mobile.ui.components.PillTone

@Composable
fun UuRemoteScreen(viewModel: UuRemoteViewModel, onBack: () -> Unit) {
    val ui by viewModel.state.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val bound = ui.binding.deviceId != null
    val waiting = ui.binding.pending != null
    var confirmForget by remember { mutableStateOf(false) }
    Column(Modifier.fillMaxSize().navigationBarsPadding()) {
        MobileTopBar(
            title = "远程桌面", subtitle = "UU 远程",
            leadingAsset = "common-arrow-left-20.svg", leadingDescription = "返回",
            onLeadingClick = onBack,
        )
        Column(
            Modifier.verticalScroll(rememberScrollState()).padding(24.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            WorkBuddyAsset("drawer-computer-428.svg", size = 40.dp, tint = MobileColors.Ink)
            Text(if (bound) "已绑定电脑" else "连接远程电脑", style = MobileTypography.Title, color = MobileColors.Ink)
            Text("通过 UU 控制桌面。本地聊天连接独立保持。", style = MobileTypography.BodySmall, color = MobileColors.Muted)
            ui.message?.let {
                InfoBanner(it, PillTone.Info, onDismiss = viewModel::dismissMessage)
            }
            if (waiting) {
                Text("等待 UU 返回结果", color = MobileColors.Muted, style = MobileTypography.BodySmall)
                TextButton(onClick = viewModel::cancelWaiting) { Text("结束等待") }
            }
            if (bound) {
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    Button(
                        onClick = { viewModel.control(context, false) }, enabled = !waiting,
                        colors = ButtonDefaults.buttonColors(containerColor = MobileColors.Action, contentColor = MobileColors.OnAction),
                        modifier = Modifier.weight(1f),
                    ) { Text("全屏") }
                    OutlinedButton(
                        onClick = { viewModel.control(context, true) }, enabled = !waiting,
                        modifier = Modifier.weight(1f),
                    ) { Text("小窗") }
                }
            }
            OutlinedButton(
                onClick = { viewModel.bind(context) }, enabled = !waiting, modifier = Modifier.fillMaxWidth(),
            ) { Text(if (bound) "更换电脑" else "绑定电脑") }
            Row {
                TextButton(onClick = { viewModel.install(context) }) { Text("安装 / 更新 UU") }
                if (bound) TextButton(onClick = { confirmForget = true }, enabled = !waiting) { Text("解除绑定") }
            }
        }
    }
    if (confirmForget) AlertDialog(
        onDismissRequest = { confirmForget = false },
        title = { Text("移除本机绑定？") },
        text = { Text("不会停止 UU 远控，也不会断开本地聊天。") },
        confirmButton = { TextButton(onClick = { viewModel.forget(); confirmForget = false }) { Text("移除") } },
        dismissButton = { TextButton(onClick = { confirmForget = false }) { Text("取消") } },
    )
}
