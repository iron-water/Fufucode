package com.fufucode.mobile.ui.screens

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
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
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.rememberCoroutineScope
import kotlinx.coroutines.launch
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.fufucode.mobile.data.ApprovalState
import com.fufucode.mobile.data.EntryRole
import com.fufucode.mobile.data.LinkState
import com.fufucode.mobile.data.MobileState
import com.fufucode.mobile.data.PendingAttachment
import com.fufucode.mobile.data.RemoteBlock
import com.fufucode.mobile.data.RemoteToolCall
import com.fufucode.mobile.data.TimelineEntryState
import com.fufucode.mobile.data.ToolStatus
import com.fufucode.mobile.data.formatSize
import com.fufucode.mobile.design.MobileColors
import com.fufucode.mobile.design.LocalMobileDark
import com.fufucode.mobile.design.MobileDimens
import com.fufucode.mobile.design.MobileTypography
import com.fufucode.mobile.design.WorkBuddyAsset
import com.fufucode.mobile.ui.components.EmptyState
import com.fufucode.mobile.ui.components.InfoBanner
import com.fufucode.mobile.ui.components.MobileCard
import com.fufucode.mobile.ui.components.MobileTopBar
import com.fufucode.mobile.ui.components.PillTone
import com.fufucode.mobile.ui.components.StatusPill

/**
 * 界面 3：聊天时间线（含界面 2 的抽屉入口与界面 4 的审批卡）。
 *
 * 依据桌面端真实协议：
 * - 时间线条目按 id 覆盖，桌面端推送什么就渲染什么；
 * - 思考块默认折叠（桌面端也有思考块）；
 * - 工具卡展示工具名、参数与状态；`AwaitingApproval` 时直接给出单次批准/拒绝；
 * - 发送键与停止键按服务端 `canSend`/`canStop` 互斥，触摸命中区 ≥44dp。
 */
@Composable
fun ChatScreen(
    state: MobileState,
    draft: String,
    attachments: List<PendingAttachment>,
    onDraftChange: (String) -> Unit,
    onOpenDrawer: () -> Unit,
    onSend: () -> Unit,
    onStop: () -> Unit,
    onResolveApproval: (String, Boolean) -> Unit,
    onOpenAttachments: () -> Unit,
    onRemoveAttachment: (String) -> Unit,
    onDismissNotice: () -> Unit,
    onOpenDevice: () -> Unit,
) {
    val listState = rememberLazyListState()
    val scrollScope = rememberCoroutineScope()
    val followEnd by remember { derivedStateOf { !listState.canScrollForward } }

    // 新内容到达时跟随到底部；用户手动上滑阅读历史时不强制回拉。
    val entryCount = state.timeline.size
    LaunchedEffect(entryCount, state.timeline.lastOrNull()?.entry?.blocks?.hashCode()) {
        if (entryCount > 0 && followEnd) listState.animateScrollToItem(state.approvals.size + entryCount - 1)
    }

    Column(modifier = Modifier.fillMaxSize().background(MobileColors.Background)) {
        MobileTopBar(
            title = state.title.ifBlank { "当前会话" },
            subtitle = subtitleFor(state),
            leadingAsset = "wb_menu.svg",
            leadingDescription = "打开会话与项目抽屉",
            onLeadingClick = onOpenDrawer,
            trailing = {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    if (state.activity != "Idle" || state.link != LinkState.Online)
                        StatusPill(text = activityLabel(state), tone = activityTone(state))
                    Box(
                        modifier = Modifier
                            .size(MobileDimens.TouchTarget)
                            .clip(RoundedCornerShape(12.dp))
                            .clickable(onClick = onOpenDevice),
                        contentAlignment = Alignment.Center,
                    ) {
                        WorkBuddyAsset(
                            asset = "computer.svg",
                            size = 22.dp,
                            tint = MobileColors.Ink,
                            contentDescription = "设备与网络",
                        )
                    }
                }
            },
        )

        if (state.link != LinkState.Online) {
            InfoBanner(
                message = linkMessage(state),
                tone = if (state.link == LinkState.Online) PillTone.Success else PillTone.Warning,
            )
        }
        state.notice?.let { message ->
            InfoBanner(message = message, tone = PillTone.Info, onDismiss = onDismissNotice)
        }

        Box(modifier = Modifier.weight(1f).fillMaxWidth()) {
            // 待审批卡必须显式渲染：桌面端推的是 approval_added 事件，
            // 它不是时间线条目，若不在这里渲染，手机端就永远没有批准/拒绝入口
            // （实测：工具卡显示「待审批」，但整页没有任何按钮可点，审批无法回传）。
            if (state.timeline.isEmpty() && state.approvals.isEmpty()) {
                EmptyState(
                    illustrationAsset = if (LocalMobileDark.current) "assistant-empty-illustration-dark.svg" else "assistant-empty-illustration-light.svg",
                    title = if (state.link == LinkState.Online) "开始一项任务" else "连接你的电脑",
                    description = if (state.link == LinkState.Online) {
                        "把想做的事告诉 Fufucode。"
                    } else {
                        "配对后即可继续工作。"
                    },
                )
            } else {
                LazyColumn(
                    state = listState,
                    modifier = Modifier.fillMaxSize(),
                    contentPadding = androidx.compose.foundation.layout.PaddingValues(
                        start = 20.dp,
                        end = 20.dp,
                        top = 16.dp,
                        bottom = 12.dp,
                    ),
                    verticalArrangement = Arrangement.spacedBy(16.dp),
                ) {
                    // 待审批在前：审批需要用户尽快处理，不能埋在历史消息后面。
                    items(state.approvals, key = { "approval-" + it.request.approvalId }) { approval ->
                        ApprovalCard(
                            approval = approval,
                            online = state.link == LinkState.Online,
                            onDecide = { approve -> onResolveApproval(approval.request.approvalId, approve) },
                        )
                    }
                    items(state.timeline, key = { it.entry.id }) { entry ->
                        TimelineEntry(entry, onResolveApproval)
                    }
                }
            }
            if (!followEnd && state.timeline.isNotEmpty()) {
                Box(Modifier.align(Alignment.BottomEnd).padding(12.dp)
                    .clip(RoundedCornerShape(24.dp)).background(MobileColors.Surface)
                    .border(1.dp, MobileColors.Border, RoundedCornerShape(24.dp))
                    .clickable { scrollScope.launch { listState.animateScrollToItem(state.approvals.size + state.timeline.size - 1) } }
                    .padding(horizontal = 16.dp, vertical = 12.dp)) {
                    Text("↓ 最新消息", style = MobileTypography.BodySmall, color = MobileColors.Ink)
                }
            }
        }

        Composer(
            state = state,
            draft = draft,
            attachments = attachments,
            onDraftChange = onDraftChange,
            onSend = onSend,
            onStop = onStop,
            onOpenAttachments = onOpenAttachments,
            onRemoveAttachment = onRemoveAttachment,
        )
    }
}

private fun subtitleFor(state: MobileState): String? {
    val parts = buildList {
        if (state.model != null) add(state.model!!)
        if (state.queuedCount > 0) add("排队 ${state.queuedCount} 条")
        if (state.pendingApprovalCount > 0) add("待审批 ${state.pendingApprovalCount}")
    }
    return parts.takeIf { it.isNotEmpty() }?.joinToString(" · ")
}

private fun activityLabel(state: MobileState): String = when (state.link) {
    LinkState.Online -> when (state.activity) {
        "Running" -> "运行中"
        "Compacting" -> "压缩中"
        "Retrying" -> "重试中"
        "Cancelling" -> "停止中"
        "Idle" -> "空闲"
        else -> state.activity
    }
    LinkState.Connecting -> "连接中"
    LinkState.Reconnecting -> "重连中"
    LinkState.Offline -> "已离线"
    LinkState.Idle -> "未连接"
}

private fun activityTone(state: MobileState): PillTone = when (state.link) {
    LinkState.Online -> if (state.activity == "Idle") PillTone.Neutral else PillTone.Accent
    LinkState.Connecting, LinkState.Reconnecting -> PillTone.Warning
    LinkState.Offline -> PillTone.Danger
    LinkState.Idle -> PillTone.Neutral
}

private fun linkMessage(state: MobileState): String = when (state.link) {
    LinkState.Connecting -> "正在连接电脑端会话，请稍候…"
    LinkState.Reconnecting ->
        "连接已中断（第 ${state.reconnectAttempt} 次重试中）。重连前本页为只读；电脑端任务不受影响。"
    LinkState.Offline -> "已断开，请在连接页重新输入 4 位配对码。"
    LinkState.Idle -> "尚未连接：请在连接页输入配对码。"
    LinkState.Online -> ""
}

@Composable
private fun TimelineEntry(
    entry: TimelineEntryState,
    onResolveApproval: (String, Boolean) -> Unit,
) {
    val isUser = entry.entry.role == EntryRole.User
    Column(
        modifier = Modifier.fillMaxWidth(),
        horizontalAlignment = if (isUser) Alignment.End else Alignment.Start,
    ) {
        entry.entry.blocks.forEach { block ->
            when (block) {
                is RemoteBlock.Text -> TextBubble(
                    text = block.text,
                    isUser = isUser,
                    streaming = entry.entry.isStreaming,
                )
                is RemoteBlock.Thinking -> ThinkingBlock(block.text, streaming = entry.entry.isStreaming)
                is RemoteBlock.Tool -> ToolCard(block.call, onResolveApproval)
                is RemoteBlock.Error -> ErrorBlock(block.message)
            }
            Spacer(Modifier.height(6.dp))
        }
    }
}

@Composable
private fun TextBubble(text: String, isUser: Boolean, streaming: Boolean) {
    Box(
        modifier = Modifier
            .widthIn(max = 320.dp)
            // 官方规范：气泡圆角 16px；用户右对齐 16/16/4/16，助手 16/16/16/4。
            .clip(
                RoundedCornerShape(
                    topStart = MobileDimens.BubbleRadius,
                    topEnd = MobileDimens.BubbleRadius,
                    bottomStart = if (isUser) MobileDimens.BubbleRadius else 4.dp,
                    bottomEnd = if (isUser) 4.dp else MobileDimens.BubbleRadius,
                ),
            )
            .background(if (isUser) MobileColors.UserBubble else MobileColors.AssistantBubble)
            .border(
                width = if (isUser) 0.dp else 1.dp,
                color = Color.Transparent,
                shape = RoundedCornerShape(
                    topStart = MobileDimens.BubbleRadius,
                    topEnd = MobileDimens.BubbleRadius,
                    bottomStart = if (isUser) MobileDimens.BubbleRadius else 4.dp,
                    bottomEnd = if (isUser) 4.dp else MobileDimens.BubbleRadius,
                ),
            )
            .padding(horizontal = 14.dp, vertical = 10.dp),
    ) {
        Text(
            text = if (streaming) "$text ▍" else text,
            style = MobileTypography.Body,
            color = if (isUser) MobileColors.UserBubbleInk else MobileColors.Ink,
        )
    }
}

/**
 * 思考折叠块：官方规范的折叠药丸栏，内嵌 `working.svg` 旋转动效，
 * 点击展开半透明浅底推理流。
 */
@Composable
private fun ThinkingBlock(text: String, streaming: Boolean) {
    var expanded by remember { mutableStateOf(false) }
    MobileCard(
        modifier = Modifier.widthIn(max = 340.dp),
        background = Color.Transparent,
        borderColor = Color.Transparent,
    ) {
        Column(modifier = Modifier.padding(horizontal = 12.dp, vertical = 10.dp)) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(min = 44.dp)
                    .clip(RoundedCornerShape(8.dp))
                    .clickable { expanded = !expanded },
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                if (streaming) {
                    WorkingSpinner(size = 16.dp)
                } else {
                    WorkBuddyAsset(
                        asset = if (expanded) "chevron-down.svg" else "chevron-right.svg",
                        size = 16.dp,
                        tint = MobileColors.InkSecondary,
                        contentDescription = null,
                    )
                }
                Text(
                    text = if (streaming) "正在思考…" else "思考过程",
                    style = MobileTypography.BodySmall,
                    color = MobileColors.InkSecondary,
                    modifier = Modifier.weight(1f),
                )
            }
            AnimatedVisibility(visible = expanded) {
                Text(
                    text = text,
                    style = MobileTypography.BodySmall,
                    color = MobileColors.Muted,
                    modifier = Modifier.padding(top = 8.dp),
                )
            }
        }
    }
}

/** `working.svg` 旋转动效：仅表达“进行中”，不伪造进度百分比。 */
@Composable
private fun WorkingSpinner(size: androidx.compose.ui.unit.Dp) {
    val transition = androidx.compose.animation.core.rememberInfiniteTransition(label = "working")
    val angle by transition.animateFloat(
        initialValue = 0f,
        targetValue = 360f,
        animationSpec = androidx.compose.animation.core.infiniteRepeatable(
            animation = androidx.compose.animation.core.tween(1100, easing = androidx.compose.animation.core.LinearEasing),
        ),
        label = "working-angle",
    )
    Box(
        modifier = Modifier
            .size(size)
            .rotate(angle),
    ) {
        WorkBuddyAsset(
            asset = "working.svg",
            size = size,
            tint = MobileColors.Accent,
            contentDescription = null,
        )
    }
}

@Composable
private fun ErrorBlock(message: String) {
    MobileCard(
        modifier = Modifier.widthIn(max = 340.dp),
        background = MobileColors.DangerSoft,
        borderColor = MobileColors.Danger,
    ) {
        Row(
            modifier = Modifier.padding(horizontal = 12.dp, vertical = 10.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalAlignment = Alignment.Top,
        ) {
            WorkBuddyAsset(
                asset = "alert-triangle.svg",
                size = 18.dp,
                tint = MobileColors.Danger,
                contentDescription = null,
            )
            Text(text = message, style = MobileTypography.BodySmall, color = MobileColors.Danger)
        }
    }
}

/** 工具调用卡（含界面 4 的单次批准/拒绝）。 */
@Composable
private fun ToolCard(tool: RemoteToolCall, onResolveApproval: (String, Boolean) -> Unit) {
    var expanded by remember(tool.toolCallId) { mutableStateOf(tool.status != ToolStatus.Completed) }
    val tone = when (tool.status) {
        ToolStatus.Completed -> PillTone.Success
        ToolStatus.Failed, ToolStatus.Cancelled -> PillTone.Danger
        ToolStatus.AwaitingApproval -> PillTone.Warning
        ToolStatus.Running, ToolStatus.Pending -> PillTone.Info
    }
    val label = when (tool.status) {
        ToolStatus.Completed -> "已完成"
        ToolStatus.Failed -> "失败"
        ToolStatus.Cancelled -> "已取消"
        ToolStatus.AwaitingApproval -> "待审批"
        ToolStatus.Running -> "执行中"
        ToolStatus.Pending -> "准备中"
    }

    MobileCard(modifier = Modifier.widthIn(max = 360.dp)) {
        Column {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable { expanded = !expanded }
                    .padding(horizontal = 12.dp, vertical = 10.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                WorkBuddyAsset(
                    asset = when (tool.status) {
                        ToolStatus.Completed -> "check-circle.svg"
                        ToolStatus.Failed, ToolStatus.Cancelled -> "alert-triangle.svg"
                        ToolStatus.AwaitingApproval -> "tip-warning-fill.svg"
                        else -> "task-spinner.svg"
                    },
                    size = 18.dp,
                    tint = when (tool.status) {
                        ToolStatus.Completed -> MobileColors.Success
                        ToolStatus.Failed, ToolStatus.Cancelled -> MobileColors.Danger
                        ToolStatus.AwaitingApproval -> MobileColors.Warning
                        else -> MobileColors.Info
                    },
                    contentDescription = null,
                )
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = tool.toolName.ifBlank { "工具调用" },
                        style = MobileTypography.SectionTitle,
                        color = MobileColors.Ink,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    if (!tool.statusText.isNullOrBlank()) {
                        Text(
                            text = tool.statusText!!,
                            style = MobileTypography.Caption,
                            color = MobileColors.Muted,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                }
                StatusPill(text = label, tone = tone)
            }
            AnimatedVisibility(visible = expanded) {
                Column(modifier = Modifier.padding(horizontal = 12.dp, vertical = 10.dp)) {
                    Text(text = "参数", style = MobileTypography.Caption, color = MobileColors.Muted)
                    Text(
                        text = tool.argsJson.ifBlank { "（无参数）" },
                        style = MobileTypography.Mono,
                        color = MobileColors.InkSecondary,
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(top = 4.dp),
                    )
                    if (!tool.output.isNullOrBlank()) {
                        Text(
                            text = "输出",
                            style = MobileTypography.Caption,
                            color = MobileColors.Muted,
                            modifier = Modifier.padding(top = 8.dp),
                        )
                        Text(
                            text = tool.output!!,
                            style = MobileTypography.Mono,
                            color = if (tool.isErrorOutput) MobileColors.Danger else MobileColors.InkSecondary,
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(top = 4.dp),
                        )
                    }
                }
            }
        }
    }
}

/**
 * 输入区：附件入口 + 多行输入 + 发送/停止。
 *
 * 发送与停止互斥（`canSend` / `canStop` 来自服务端真实状态），
 * 按键命中区 48dp（≥44dp 要求），离线时输入禁用并明确提示。
 */
@Composable
private fun Composer(
    state: MobileState,
    draft: String,
    attachments: List<PendingAttachment>,
    onDraftChange: (String) -> Unit,
    onSend: () -> Unit,
    onStop: () -> Unit,
    onOpenAttachments: () -> Unit,
    onRemoveAttachment: (String) -> Unit,
) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(MobileColors.Background)
            .navigationBarsPadding()
            .imePadding()
            .padding(horizontal = 10.dp, vertical = 8.dp),
    ) {
        if (attachments.isNotEmpty()) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(bottom = 6.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                attachments.forEach { attachment ->
                    AttachmentChip(attachment = attachment, onRemove = { onRemoveAttachment(attachment.uri) })
                }
            }
        }

        Row(
            modifier = Modifier.fillMaxWidth()
                .clip(RoundedCornerShape(28.dp))
                .background(MobileColors.SurfaceAlt)
                .border(1.dp, MobileColors.Border.copy(alpha = .6f), RoundedCornerShape(28.dp))
                .padding(6.dp),
            verticalAlignment = Alignment.Bottom,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Box(
                modifier = Modifier
                    .size(MobileDimens.TouchTarget)
                    .clip(RoundedCornerShape(MobileDimens.ControlRadius))
                    .background(Color.Transparent)
                    .clickable(onClick = onOpenAttachments),
                contentAlignment = Alignment.Center,
            ) {
                WorkBuddyAsset(
                    asset = "input-add.svg",
                    size = 22.dp,
                    tint = MobileColors.Ink,
                    contentDescription = "附件与扩展",
                )
            }

            // 官方规范：输入框为悬浮圆角卡片（radius 24px）。
            Box(
                modifier = Modifier
                    .weight(1f)
                    .heightIn(min = MobileDimens.ComposerMinHeight, max = 132.dp)
                    .clip(RoundedCornerShape(MobileDimens.ComposerRadius))
                    .background(Color.Transparent)
                    .padding(horizontal = 12.dp, vertical = 12.dp),
                contentAlignment = Alignment.CenterStart,
            ) {
                if (draft.isEmpty()) {
                    Text(
                        text = if (state.writable) "给电脑端 Agent 发消息" else "当前不可发送",
                        style = MobileTypography.Body,
                        color = MobileColors.Muted,
                    )
                }
                BasicTextField(
                    value = draft,
                    onValueChange = onDraftChange,
                    enabled = true,
                    textStyle = MobileTypography.Body.copy(color = MobileColors.Ink),
                    cursorBrush = SolidColor(MobileColors.Accent),
                    modifier = Modifier.fillMaxWidth(),
                )
            }

            if (state.stoppable) {
                // 官方规范：运行中发送键无缝切为红色方块停止键（#FF3B30）。
                CircleActionButton(
                    asset = "input-stop.svg",
                    tint = Color.White,
                    background = MobileColors.Danger,
                    description = "紧急停止",
                    onClick = onStop,
                    enabled = !state.stopInFlight,
                )
            } else {
                // 官方规范：发送键为品牌蓝圆形按钮（#0052D9）+ 纯白箭头；
                // 不可发送时降为 35% 透明蓝，仍保持同一图形语言。
                CircleActionButton(
                    asset = "send-white.svg",
                    tint = MobileColors.OnAction,
                    background = if (state.writable && !state.sendInFlight) {
                        MobileColors.Action
                    } else {
                        MobileColors.Action.copy(alpha = 0.35f)
                    },
                    description = "发送",
                    onClick = onSend,
                    enabled = state.writable && !state.sendInFlight,
                )
            }
        }
    }
}

/**
 * 圆形动作键：命中区 48dp。
 *
 * 说明：底圆由调用方按官方令牌着色（发送 = 品牌蓝，停止 = 危险红），
 * 图形用纯白矢量（`send-white.svg` / `input-stop.svg`）保持同一套视觉语言。
 */
@Composable
private fun CircleActionButton(
    asset: String,
    tint: Color?,
    background: Color,
    description: String,
    onClick: () -> Unit,
    enabled: Boolean,
) {
    Box(
        modifier = Modifier
            .size(MobileDimens.TouchTarget)
            .clip(RoundedCornerShape(999.dp))
            .background(background)
            .clickable(enabled = enabled, onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        WorkBuddyAsset(
            asset = asset,
            size = 22.dp,
            tint = tint,
            contentDescription = description,
        )
    }
}

@Composable
private fun AttachmentChip(attachment: PendingAttachment, onRemove: () -> Unit) {
    Row(
        modifier = Modifier
            .clip(RoundedCornerShape(10.dp))
            .background(MobileColors.AccentSoft)
            .padding(start = 10.dp, end = 4.dp, top = 6.dp, bottom = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        WorkBuddyAsset(
            asset = assetFor(attachment),
            size = 16.dp,
            tint = MobileColors.AccentStrong,
            contentDescription = null,
        )
        Text(
            text = attachment.displayName,
            style = MobileTypography.Caption,
            color = MobileColors.AccentStrong,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.widthIn(max = 120.dp),
        )
        attachment.sizeBytes?.let {
            Text(text = formatSize(it), style = MobileTypography.Caption, color = MobileColors.Muted)
        }
        Box(
            modifier = Modifier
                .size(28.dp)
                .clip(RoundedCornerShape(8.dp))
                .clickable(onClick = onRemove),
            contentAlignment = Alignment.Center,
        ) {
            WorkBuddyAsset(asset = "close.svg", size = 12.dp, tint = MobileColors.Muted, contentDescription = "移除附件")
        }
    }
}

private fun assetFor(attachment: PendingAttachment): String = when (attachment.kind) {
    com.fufucode.mobile.data.AttachmentKind.Image -> "file-image.svg"
    com.fufucode.mobile.data.AttachmentKind.Camera -> "camera.svg"
    com.fufucode.mobile.data.AttachmentKind.Document -> "file-document.svg"
}

/** 供审批页复用的卡片（时间线上也以内联形式出现）。 */
@Composable
fun ApprovalCard(
    approval: ApprovalState,
    online: Boolean,
    onDecide: (Boolean) -> Unit,
) {
    var expanded by remember(approval.request.approvalId) { mutableStateOf(true) }
    val failed = approval.resolutionFailedReason != null
    MobileCard(
        modifier = Modifier.fillMaxWidth(),
        background = if (failed) MobileColors.SurfaceAlt else MobileColors.WarningSoft,
        borderColor = if (failed) MobileColors.Border else MobileColors.Warning,
    ) {
        Column(modifier = Modifier.padding(14.dp), horizontalAlignment = Alignment.CenterHorizontally) {
            // 官方规范：卡片式浮层顶部带灰色居中拖拽把手。
            Box(
                modifier = Modifier
                    .padding(bottom = 10.dp)
                    .width(36.dp)
                    .height(4.dp)
                    .clip(RoundedCornerShape(999.dp))
                    .background(MobileColors.Border),
            )
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                WorkBuddyAsset(
                    asset = "common-shield-warning.svg",
                    size = 20.dp,
                    tint = if (failed) MobileColors.Muted else MobileColors.Warning,
                    contentDescription = null,
                )
                Text(
                    text = approval.request.toolName.ifBlank { "工具调用" },
                    style = MobileTypography.SectionTitle,
                    color = MobileColors.Ink,
                    modifier = Modifier.weight(1f),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                StatusPill(
                    text = if (failed) "已失效" else "待审批",
                    tone = if (failed) PillTone.Neutral else PillTone.Warning,
                )
            }

            if (!approval.request.summary.isNullOrBlank()) {
                Text(
                    text = approval.request.summary!!,
                    style = MobileTypography.BodySmall,
                    color = MobileColors.InkSecondary,
                    modifier = Modifier.padding(top = 8.dp),
                )
            }

            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(top = 8.dp)
                    .clip(RoundedCornerShape(8.dp))
                    .background(MobileColors.Surface)
                    .clickable { expanded = !expanded }
                    .padding(10.dp),
            ) {
                Column {
                    Text(
                        text = if (expanded) "参数（点击收起）" else "参数（点击展开）",
                        style = MobileTypography.Caption,
                        color = MobileColors.Muted,
                    )
                    Text(
                        text = if (expanded) {
                            approval.request.argsJson.ifBlank { "（无参数）" }
                        } else {
                            approval.request.argsJson.lineSequence().firstOrNull()?.take(80).orEmpty()
                        },
                        style = MobileTypography.Mono,
                        color = MobileColors.InkSecondary,
                        maxLines = if (expanded) Int.MAX_VALUE else 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.padding(top = 4.dp),
                    )
                }
            }

            if (failed) {
                Text(
                    text = approval.resolutionFailedReason!!,
                    style = MobileTypography.BodySmall,
                    color = MobileColors.Danger,
                    modifier = Modifier.padding(top = 10.dp),
                )
                Text(
                    text = "该审批已不可用（可能已在电脑端处理或已超时）。",
                    style = MobileTypography.Caption,
                    color = MobileColors.Muted,
                    modifier = Modifier.padding(top = 4.dp),
                )
            } else {
                Row(
                    modifier = Modifier.padding(top = 12.dp),
                    horizontalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    ApprovalButton(
                        label = "拒绝",
                        // 官方规范：拒绝 = 浅红底 + 危险红描边。
                        background = MobileColors.DangerSoft,
                        ink = MobileColors.Danger,
                        border = MobileColors.DangerBorder,
                        enabled = online && !approval.inFlight,
                        onClick = { onDecide(false) },
                        modifier = Modifier.weight(1f),
                    )
                    ApprovalButton(
                        label = if (approval.inFlight) "提交中…" else "批准执行",
                        background = MobileColors.Accent,
                        ink = Color.White,
                        border = Color.Transparent,
                        enabled = online && !approval.inFlight,
                        onClick = { onDecide(true) },
                        modifier = Modifier.weight(1.4f),
                    )
                }
                Text(
                    text = "单次有效：此决定只作用于本次调用，不会为该工具建立长期许可。",
                    style = MobileTypography.Caption,
                    color = MobileColors.Muted,
                    modifier = Modifier.padding(top = 8.dp),
                )
            }
        }
    }
}

@Composable
private fun ApprovalButton(
    label: String,
    background: Color,
    ink: Color,
    border: Color,
    enabled: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Box(
        modifier = modifier
            .heightIn(min = MobileDimens.TouchTargetMin)
            .clip(RoundedCornerShape(12.dp))
            .background(if (enabled) background else background.copy(alpha = 0.45f))
            .border(1.dp, border, RoundedCornerShape(12.dp))
            .clickable(enabled = enabled, onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Text(text = label, style = MobileTypography.Button, color = ink)
    }
}
