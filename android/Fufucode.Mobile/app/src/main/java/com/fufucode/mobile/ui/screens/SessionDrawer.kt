package com.fufucode.mobile.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.fufucode.mobile.data.LinkState
import com.fufucode.mobile.data.MobileState
import com.fufucode.mobile.design.MobileColors
import com.fufucode.mobile.design.MobileDimens
import com.fufucode.mobile.design.MobileTypography
import com.fufucode.mobile.design.WorkBuddyAsset
import com.fufucode.mobile.ui.components.PillTone
import com.fufucode.mobile.ui.components.StatusPill

/**
 * 界面 2：会话列表与抽屉。
 *
 * 范围边界（重要，且与桌面协议一致）：桌面远控协议的 `/api/snapshot` 只暴露
 * **当前绑定的一个会话**，没有「历史会话列表」端点（见 `IRemoteSessionBridge`）。
 * 因此：
 * - 「当前会话」来自真实快照；
 * - 「最近连接过的电脑」来自本机配对记录（真实存在，不含伪造的历史会话）；
 * - 「置顶 / 未读徽标」按真实可得数据表达：置顶 = 当前会话，未读 = 当前待审批数量。
 *
 * 界面**不**发明多会话列表或假消息预览；缺少的部分在交付记录中列为需桌面端扩展协议。
 */
@Composable
fun SessionDrawer(
    state: MobileState,
    pairedBaseUrl: String?,
    onClose: () -> Unit,
    onOpenDevice: () -> Unit,
    onDisconnect: () -> Unit,
) {
    Row(modifier = Modifier.fillMaxSize()) {
        Column(
            modifier = Modifier
                .width((LocalConfiguration.current.screenWidthDp * .86f).coerceAtMost(320f).dp)
                .fillMaxHeight()
                .background(MobileColors.Surface)
                .navigationBarsPadding()
                .verticalScroll(rememberScrollState()),
        ) {
            // 抽屉头部：品牌与关闭
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(min = 64.dp)
                    .padding(horizontal = 14.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text("Fufucode", style = MobileTypography.Title, color = MobileColors.Ink)
                Spacer(Modifier.weight(1f))
                Box(
                    modifier = Modifier
                        .size(MobileDimens.TouchTarget)
                        .clip(RoundedCornerShape(12.dp))
                        .clickable(onClick = onClose),
                    contentAlignment = Alignment.Center,
                ) {
                    WorkBuddyAsset(
                        asset = "wb_close.svg",
                        size = 18.dp,
                        tint = MobileColors.Ink,
                        contentDescription = "关闭抽屉",
                    )
                }
            }

            DrawerSection(title = "当前会话")
            if (state.title.isBlank()) {
                DrawerHint("尚未连接到电脑端会话。")
            } else {
                DrawerRow(
                    asset = "chat.svg",
                    title = state.title,
                    meta = buildString {
                        append(state.model ?: "模型未知")
                        if (state.queuedCount > 0) append(" · 排队 ${state.queuedCount}")
                    },
                    selected = true,
                    pinned = false,
                    badge = state.pendingApprovalCount.takeIf { it > 0 }?.toString(),
                    onClick = onClose,
                )
            }

            // 未读/待处理：只有真实待审批数量才显示徽标。
            if (state.pendingApprovalCount > 0) {
                DrawerSection(title = "待处理")
                DrawerRow(
                    asset = "tip-warning-fill.svg",
                    title = "待审批工具调用",
                    meta = "需要你在手机端决定",
                    selected = false,
                    pinned = false,
                    badge = state.pendingApprovalCount.toString(),
                    onClick = onClose,
                )
            }

            DrawerSection(title = "已配对的电脑")
            if (pairedBaseUrl.isNullOrBlank()) {
                DrawerHint("本机尚未保存配对信息。")
            } else {
                DrawerRow(
                    asset = "drawer-computer-428.svg",
                    title = "Fufucode 电脑",
                    meta = pairedBaseUrl,
                    selected = state.link == LinkState.Online,
                    pinned = false,
                    badge = null,
                    onClick = onOpenDevice,
                )
            }

            DrawerSection(title = "连接")
            DrawerRow(
                asset = "computer.svg",
                title = "设备与网络",
                meta = "查看 IP、端口与延迟",
                selected = false,
                pinned = false,
                badge = null,
                onClick = onOpenDevice,
            )
            DrawerRow(
                asset = "drawer-wifi-off-428.svg",
                title = "断开连接",
                meta = "清除本机令牌",
                selected = false,
                pinned = false,
                badge = null,
                onClick = onDisconnect,
                danger = true,
            )

            Spacer(Modifier.height(12.dp))
            Column(modifier = Modifier.padding(horizontal = 16.dp)) {
                StatusPill(
                    text = when (state.link) {
                        LinkState.Online -> "已连接"
                        LinkState.Connecting -> "连接中"
                        LinkState.Reconnecting -> "重连中"
                        LinkState.Offline -> "已离线"
                        LinkState.Idle -> "未连接"
                    },
                    tone = when (state.link) {
                        LinkState.Online -> PillTone.Success
                        LinkState.Connecting, LinkState.Reconnecting -> PillTone.Warning
                        LinkState.Offline -> PillTone.Danger
                        LinkState.Idle -> PillTone.Neutral
                    },
                )
            }
        }

        // 遮罩：点击关闭抽屉
        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(Color(0x66000000))
                .clickable(onClick = onClose),
        )
    }
}

@Composable
private fun DrawerSection(title: String) {
    Text(
        text = title,
        style = MobileTypography.Caption,
        color = MobileColors.Muted,
        modifier = Modifier.padding(start = 16.dp, end = 16.dp, top = 16.dp, bottom = 6.dp),
    )
}

@Composable
private fun DrawerHint(text: String) {
    Text(
        text = text,
        style = MobileTypography.BodySmall,
        color = MobileColors.Muted,
        modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp),
    )
}

@Composable
private fun DrawerRow(
    asset: String,
    title: String,
    meta: String,
    selected: Boolean,
    pinned: Boolean,
    badge: String?,
    onClick: () -> Unit,
    danger: Boolean = false,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 10.dp, vertical = 3.dp)
            .clip(RoundedCornerShape(12.dp))
            .background(if (selected) MobileColors.AccentSoft else Color.Transparent)
            .clickable(onClick = onClick)
            .padding(horizontal = 10.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        WorkBuddyAsset(
            asset = asset,
            size = 22.dp,
            tint = when {
                danger -> MobileColors.Danger
                selected -> MobileColors.AccentStrong
                else -> MobileColors.InkSecondary
            },
            contentDescription = null,
        )
        Column(modifier = Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    text = title,
                    style = MobileTypography.BodySmall,
                    color = if (danger) MobileColors.Danger else MobileColors.Ink,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f, fill = false),
                )
                if (pinned) {
                    Spacer(Modifier.width(6.dp))
                    WorkBuddyAsset(
                        asset = "drawer-menu-pin.svg",
                        size = 13.dp,
                        tint = MobileColors.AccentStrong,
                        contentDescription = "已置顶",
                    )
                }
            }
            Text(
                text = meta,
                style = MobileTypography.Caption,
                color = MobileColors.Muted,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        if (badge != null) {
            Box(
                modifier = Modifier
                    .clip(RoundedCornerShape(999.dp))
                    .background(MobileColors.Danger)
                    .padding(horizontal = 7.dp, vertical = 2.dp),
            ) {
                Text(text = badge, style = MobileTypography.Caption, color = Color.White)
            }
        }
    }
}
