package com.fufucode.mobile.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import com.fufucode.mobile.data.LinkState
import com.fufucode.mobile.data.MobileState
import com.fufucode.mobile.design.MobileColors
import com.fufucode.mobile.design.MobileDimens
import com.fufucode.mobile.design.MobileTypography
import com.fufucode.mobile.design.WorkBuddyAsset
import com.fufucode.mobile.ui.components.InfoBanner
import com.fufucode.mobile.ui.components.MobileCard
import com.fufucode.mobile.ui.components.MobileTopBar
import com.fufucode.mobile.ui.components.PillTone
import com.fufucode.mobile.ui.components.StatusPill
import java.time.Instant

/**
 * 界面 6：设备与网络监控。
 *
 * 展示的都是可核验事实：
 * - 已连接桌面端 IP:端口、连接数、服务启动时间、令牌有效期（来自 `/api/status`）；
 * - 会话活动/排队（来自快照与增量）；
 * - 连到桌面的往返延迟（本页发起一次真实 `/api/status` 请求并计时）。
 */
@Composable
fun DeviceScreen(
    state: MobileState,
    pairedBaseUrl: String?,
    latencyMillis: Long?,
    onBack: () -> Unit,
    onPing: () -> Unit,
    onDisconnect: () -> Unit,
    onReconnect: () -> Unit,
    onDismissNotice: () -> Unit,
    onOpenRemoteDesktop: () -> Unit = {},
) {
    val server = state.server
    Column(modifier = Modifier.fillMaxSize().background(MobileColors.Background)) {
        MobileTopBar(
            title = "设备与网络",
            subtitle = pairedBaseUrl,
            leadingAsset = "common-arrow-left-20.svg",
            leadingDescription = "返回会话",
            onLeadingClick = onBack,
            trailing = {
                StatusPill(
                    text = if (state.link == LinkState.Online) "在线" else "离线",
                    tone = if (state.link == LinkState.Online) PillTone.Success else PillTone.Danger,
                )
                Spacer(Modifier.size(12.dp))
            },
        )

        Column(
            modifier = Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = MobileDimens.ScreenPadding, vertical = 12.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            state.notice?.let { InfoBanner(message = it, tone = PillTone.Info, onDismiss = onDismissNotice) }

            ActionTile(
                asset = "drawer-computer-428.svg",
                label = "远程桌面 · UU",
                onClick = onOpenRemoteDesktop,
                modifier = Modifier.fillMaxWidth(),
            )
            MobileCard(modifier = Modifier.fillMaxWidth()) {
                Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        WorkBuddyAsset(
                            asset = "drawer-computer-428.svg",
                            size = 26.dp,
                            tint = MobileColors.AccentStrong,
                            contentDescription = null,
                        )
                        Spacer(Modifier.size(10.dp))
                        Column(modifier = Modifier.weight(1f)) {
                            Text(
                                text = server?.address ?: pairedBaseUrl?.substringBefore("://") ?: "未连接",
                                style = MobileTypography.SectionTitle,
                                color = MobileColors.Ink,
                            )
                            Text(
                                text = "Fufucode 桌面端",
                                style = MobileTypography.Caption,
                                color = MobileColors.Muted,
                            )
                        }
                    }
                    DeviceFact(label = "服务地址", value = pairedBaseUrl ?: "—")
                    DeviceFact(
                        label = "端口",
                        value = server?.port?.takeIf { it > 0 }?.toString() ?: "—",
                    )
                    DeviceFact(
                        label = "服务状态",
                        value = when {
                            server == null -> "未知（未取到 /api/status）"
                            server.running -> "运行中"
                            else -> "已停止"
                        },
                    )
                    DeviceFact(
                        label = "局域网连接数",
                        value = server?.connectionCount?.toString() ?: "—",
                    )
                    DeviceFact(
                        label = "往返延迟",
                        value = latencyMillis?.let { "$it ms" } ?: "未测量",
                    )
                    DeviceFact(
                        label = "令牌有效期",
                        value = server?.tokenLifetimeText ?: "—",
                    )
                    DeviceFact(
                        label = "服务启动时间",
                        value = formatInstant(server?.startedAtUtc),
                    )
                }
            }

            MobileCard(modifier = Modifier.fillMaxWidth()) {
                Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text(text = "会话", style = MobileTypography.SectionTitle, color = MobileColors.Ink)
                    DeviceFact(label = "标题", value = state.title.ifBlank { "—" })
                    DeviceFact(label = "模型", value = state.model ?: "未知")
                    DeviceFact(label = "活动", value = state.activity)
                    DeviceFact(label = "连接状态", value = state.connection)
                    DeviceFact(label = "排队消息", value = "${state.queuedCount} 条")
                    DeviceFact(label = "待审批", value = "${state.pendingApprovalCount} 项")
                }
            }

            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                ActionTile(
                    asset = "refresh-white.svg",
                    label = if (state.link == LinkState.Online) "测量延迟" else "重连",
                    onClick = { if (state.link == LinkState.Online) onPing() else onReconnect() },
                    modifier = Modifier.weight(1f),
                )
                ActionTile(
                    asset = "drawer-wifi-off-428.svg",
                    label = "一键断开",
                    onClick = onDisconnect,
                    danger = true,
                    modifier = Modifier.weight(1f),
                )
            }

            Text(
                text = "断开只清除本机保存的配对令牌；电脑端服务与正在执行的任务不受影响。",
                style = MobileTypography.Caption,
                color = MobileColors.Muted,
            )

            if (state.diagnostics.isNotEmpty()) {
                MobileCard(
                    modifier = Modifier.fillMaxWidth(),
                    background = MobileColors.WarningSoft,
                    borderColor = MobileColors.Warning,
                ) {
                    Column(modifier = Modifier.padding(14.dp)) {
                        Text(
                            text = "协议诊断（${state.diagnostics.size} 条）",
                            style = MobileTypography.SectionTitle,
                            color = MobileColors.Ink,
                        )
                        Text(
                            text = "电脑端推送了本版本未识别的字段或类型；已按“忽略未知、保留已知”处理，未崩溃。",
                            style = MobileTypography.Caption,
                            color = MobileColors.Muted,
                            modifier = Modifier.padding(top = 6.dp),
                        )
                        state.diagnostics.take(5).forEach { diagnostic ->
                            Text(
                                text = "[${diagnostic.scope}] ${diagnostic.detail}",
                                style = MobileTypography.Mono,
                                color = MobileColors.InkSecondary,
                                modifier = Modifier.padding(top = 4.dp),
                            )
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun DeviceFact(label: String, value: String) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalAlignment = Alignment.Top,
    ) {
        Text(
            text = label,
            style = MobileTypography.BodySmall,
            color = MobileColors.Muted,
            modifier = Modifier.weight(0.34f),
        )
        Text(
            text = value,
            style = MobileTypography.BodySmall,
            color = MobileColors.Ink,
            modifier = Modifier.weight(0.66f),
        )
    }
}

/** 解析桌面端 `DateTimeOffset` ISO-8601 文本；无法解析时如实返回原文而不是伪造时间。 */
private fun formatInstant(raw: String?): String {
    if (raw.isNullOrBlank()) return "—"
    return try {
        val instant = Instant.parse(raw)
        "${instant.toString().replace("T", " ").substringBefore(".")} UTC"
    } catch (_: Exception) {
        raw
    }
}

@Composable
private fun ActionTile(
    asset: String,
    label: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    danger: Boolean = false,
) {
    Row(
        modifier = modifier
            .heightIn(min = MobileDimens.TouchTarget)
            .clip(RoundedCornerShape(12.dp))
            .background(if (danger) MobileColors.DangerSoft else MobileColors.AccentSoft)
            .border(
                1.dp,
                if (danger) MobileColors.Danger else MobileColors.Accent,
                RoundedCornerShape(12.dp),
            )
            .clickable(onClick = onClick)
            .padding(horizontal = 12.dp, vertical = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.Center,
    ) {
        WorkBuddyAsset(
            asset = asset,
            size = 18.dp,
            tint = if (danger) MobileColors.Danger else MobileColors.AccentStrong,
            contentDescription = null,
        )
        Spacer(Modifier.size(8.dp))
        Text(
            text = label,
            style = MobileTypography.Button,
            color = if (danger) MobileColors.Danger else MobileColors.AccentStrong,
        )
    }
}
