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
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.fufucode.mobile.data.LinkState
import com.fufucode.mobile.design.AlimamaFamily
import com.fufucode.mobile.design.MobileColors
import com.fufucode.mobile.design.MobileDimens
import com.fufucode.mobile.design.MobileTypography
import com.fufucode.mobile.design.WorkBuddyAsset
import com.fufucode.mobile.ui.DiscoveryState
import com.fufucode.mobile.ui.components.InfoBanner
import com.fufucode.mobile.ui.components.MobileCard
import com.fufucode.mobile.ui.components.PillTone
import com.fufucode.mobile.ui.components.StatusPill

/**
 * 界面 1：连接配对页。
 *
 * 组成（对应任务书）：
 * - 品牌标识（参考包 `work_buddy_logo.svg`，仅作品牌位置参照，非腾讯品牌复用）；
 * - 4 位大号数字 PIN 输入方格；
 * - 局域网自动发现（真实扫描，列出实际确认的地址）；
 * - 扫码连接入口（说明当前可用性与真实限制）。
 */
@Composable
fun PairScreen(
    linkState: LinkState,
    discovery: DiscoveryState,
    notice: String?,
    lastPort: Int,
    onPair: (baseUrl: String, pin: String) -> Unit,
    onScan: () -> Unit,
    onStopScan: () -> Unit,
    onDismissNotice: () -> Unit,
    onOpenRemoteDesktop: () -> Unit = {},
) {
    var hostInput by remember { mutableStateOf("") }
    var pin by remember { mutableStateOf("") }
    var validation by remember { mutableStateOf<String?>(null) }
    var helpOpen by remember { mutableStateOf(false) }
    val pinFocus = remember { FocusRequester() }
    val keyboard = LocalSoftwareKeyboardController.current
    val focusManager = androidx.compose.ui.platform.LocalFocusManager.current

    // 冷启动优先使用上次成功的端口，减少首次连接的输入量（仍要求用户确认主机）。
    LaunchedEffect(lastPort) {
        if (hostInput.isEmpty() && lastPort > 0) {
            hostInput = ":${lastPort}"
        }
    }

    val connecting = linkState == LinkState.Connecting

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(MobileColors.Background)
            .navigationBarsPadding()
            .imePadding()
            // 软键盘弹起时不再整体上推：`imePadding()` 会把内容顶起并触发
            // Compose 自动滚动，坐标随之漂移。改为让表单整体落在键盘之上
            // （缩小品牌区与卡片间距），键盘弹起也不遮挡任何输入或按钮。
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 24.dp, vertical = 24.dp),
        verticalArrangement = Arrangement.spacedBy(20.dp),
    ) {
        // 品牌区
        Column(
            modifier = Modifier.fillMaxWidth(),
            horizontalAlignment = Alignment.Start,
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            // 键盘占屏约 630px，品牌区必须显著收缩，否则表单会被压到键盘之下。
            Text("Fufucode", style = MobileTypography.BodySmall, color = MobileColors.Muted)
            Text(
                text = "连接你的电脑",
                style = MobileTypography.Title,
                color = MobileColors.Ink,
            )
        }

        if (notice != null) {
            InfoBanner(message = notice, tone = PillTone.Warning, onDismiss = onDismissNotice)
        }

        // 电脑地址卡片放在配对码卡片**上方**：官方要求该字段显眼且无需滚动即可触达。
        // 配对页进入即自动聚焦配对码格并弹起数字键盘（输入法占约 630px），
        // 若地址在下半屏就会落在键盘之下而无法点按；置顶后本机模拟器亦可一次点中。
        MobileCard(modifier = Modifier.fillMaxWidth()) {
            Column {
            Column(
                modifier = Modifier.padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Text(
                    text = "电脑地址",
                    style = MobileTypography.SectionTitle,
                    color = MobileColors.Ink,
                )
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        // 高度显式给 56dp：地址框比配对码格高，避免相邻卡片在
                        // 触摸命中区上互相压边（曾导致点地址框实际命中配对码格）。
                        .height(56.dp)
                        .clip(RoundedCornerShape(MobileDimens.ControlRadius))
                        .background(MobileColors.SurfaceAlt)
                        .padding(horizontal = 12.dp),
                    contentAlignment = Alignment.CenterStart,
                ) {
                    if (hostInput.isEmpty()) {
                        Text(
                            text = "192.168.1.5:8080",
                            style = MobileTypography.Body,
                            color = MobileColors.Muted,
                        )
                    }
                    BasicTextField(
                        value = hostInput,
                        onValueChange = { hostInput = it; validation = null },
                        singleLine = true,
                        textStyle = MobileTypography.Body.copy(color = MobileColors.Ink),
                        cursorBrush = SolidColor(MobileColors.Accent),
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                        modifier = Modifier.fillMaxWidth(),
                    )
                }
            }
            // 地址和配对码共用一个表单表面，不再堆叠两张大卡片。
            Column(
                modifier = Modifier.padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        text = "配对码",
                        style = MobileTypography.SectionTitle,
                        color = MobileColors.Ink,
                        modifier = Modifier.weight(1f),
                    )
                    if (linkState != LinkState.Idle) StatusPill(
                        text = when (linkState) {
                            LinkState.Online -> "已连接"
                            LinkState.Connecting -> "连接中"
                            LinkState.Reconnecting -> "重连中"
                            LinkState.Offline -> "已离线"
                            LinkState.Idle -> "未连接"
                        },
                        tone = when (linkState) {
                            LinkState.Online -> PillTone.Success
                            LinkState.Connecting, LinkState.Reconnecting -> PillTone.Warning
                            LinkState.Offline -> PillTone.Danger
                            LinkState.Idle -> PillTone.Neutral
                        },
                    )
                }
                PinBoxes(value = pin, onChange = { pin = it; validation = null }, focusRequester = pinFocus)

                if (validation != null) {
                    Text(text = validation!!, style = MobileTypography.Caption, color = MobileColors.Danger)
                }
                PrimaryActionButton(
                    label = if (connecting) "正在连接…" else "连接",
                    enabled = !connecting,
                    onClick = {
                        if (hostInput.isBlank()) {
                            validation = "请先填写电脑地址，或使用下方「自动发现」"
                            return@PrimaryActionButton
                        }
                        if (pin.length != 4) {
                            validation = "配对码必须是 4 位数字"
                            return@PrimaryActionButton
                        }
                        validation = null
                        // 先清焦点再收键盘：不清焦点时，聚焦的配对码格会
                        // 在随后的任何一次点击上重新索回焦点并再次弹起键盘，
                        // 用户（与自动化）点「连接」会被变成给配对码打字。
                        focusManager.clearFocus(force = true)
                        keyboard?.hide()
                        onPair(hostInput.trim(), pin)
                    },
                )
            }
            }
        }
        // 局域网自动发现
        MobileCard(modifier = Modifier.fillMaxWidth(), borderColor = Color.Transparent) {
            Column(
                modifier = Modifier.padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        text = "自动发现",
                        style = MobileTypography.SectionTitle,
                        color = MobileColors.Ink,
                        modifier = Modifier.weight(1f),
                    )
                    SecondaryTextButton(
                        label = if (discovery.scanning) "停止" else "扫描",
                        onClick = { if (discovery.scanning) onStopScan() else onScan() },
                    )
                }
                if (discovery.scanning && helpOpen) Text(
                    text = discoveryScopeText(discovery), style = MobileTypography.Caption, color = MobileColors.Muted)
                if (discovery.scanning) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        RadarIndicator(size = 18.dp, active = true)
                        Spacer(Modifier.width(8.dp))
                        Text(
                            text = "${discovery.scannedHosts} / ${discovery.totalHosts}",
                            style = MobileTypography.Caption,
                            color = MobileColors.Muted,
                        )
                    }
                }
                if (discovery.finishedReason != null && discovery.found.isEmpty()) {
                    Text(
                        text = discovery.finishedReason!!,
                        style = MobileTypography.BodySmall,
                        color = MobileColors.Warning,
                    )
                }
                discovery.found.forEach { url ->
                    FoundDeviceRow(
                        url = url,
                        onUse = {
                            hostInput = url.removePrefix("http://").removePrefix("https://").trimEnd('/')
                            pinFocus.requestFocus()
                        },
                    )
                }
            }
        }

        SecondaryTextButton(label = "远程桌面 · UU", onClick = onOpenRemoteDesktop)
        // 未实现的扫码不伪装成主操作，连接说明按需展开。
        Row(Modifier.fillMaxWidth().heightIn(min = 44.dp).clickable { helpOpen = !helpOpen },
            horizontalArrangement = Arrangement.Center, verticalAlignment = Alignment.CenterVertically) {
            Text(if (helpOpen) "收起连接帮助" else "如何连接？", style = MobileTypography.BodySmall, color = MobileColors.Muted)
        }
        AnimatedVisibility(helpOpen) {
        MobileCard(modifier = Modifier.fillMaxWidth()) {
            Row(
                modifier = Modifier.padding(16.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                WorkBuddyAsset(
                    asset = "wb-qrcode.svg",
                    size = 26.dp,
                    tint = MobileColors.InkSecondary,
                    contentDescription = null,
                )
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = "① 电脑端开启「手机远控」\n② 连接同一 Wi-Fi\n③ 输入电脑显示的地址和配对码",
                        style = MobileTypography.Caption,
                        color = MobileColors.Muted,
                    )
                }
            }
        }
        }
    }
}

private fun discoveryScopeText(discovery: DiscoveryState): String = when {
    discovery.prefixes.isEmpty() && discovery.ports.isEmpty() ->
        "扫描范围将在开始时按本机 Wi-Fi 网段确定。"
    else -> buildString {
        append("本机网段：")
        append(if (discovery.prefixes.isEmpty()) "无" else discovery.prefixes.joinToString("、") { it + "x" })
        append("；端口：")
        append(discovery.ports.joinToString("、"))
        append("。")
    }
}

/**
 * 4 位大号数字输入方格（官方规范：4 个独立方格 56×64dp，
 * 聚焦格带 #0052D9 外发光高亮环，已输入格显示加粗大号数字）。
 *
 * 实现说明：用一个隐藏的 BasicTextField 承载真实数字键盘输入
 * （`KeyboardType.NumberPassword`），四个方格只做展示，
 * 这样既保证系统数字键盘/粘贴/无障碍输入可用，又得到官方外观。
 *
 * 有意**不自动聚焦**：本机 420dpi 下输入法占屏约 630px，进入页面即弹键盘会把
 * 配对码格与「连接」按钮一起压到键盘之下，用户与自动化都得先收键盘才能点到按钮。
 * 点一下方格即弹起数字键盘，整体更少一次操作。
 */
@Composable
private fun PinBoxes(value: String, onChange: (String) -> Unit, focusRequester: FocusRequester) {
    var focused by remember { mutableStateOf(false) }

    Box(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null,
            ) { focusRequester.requestFocus() },
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            repeat(4) { index ->
                val filled = index < value.length
                // 当前待输入格 = 已输入长度处；全部填满时高亮最后一格。
                val active = focused && index == value.length.coerceAtMost(3)
                Box(
                    modifier = Modifier
                        .weight(1f)
                        .height(MobileDimens.PinBoxHeight)
                        .clip(RoundedCornerShape(MobileDimens.CardRadiusLarge))
                        .background(
                            when {
                                filled -> MobileColors.AccentSoft
                                active -> MobileColors.Surface
                                else -> MobileColors.SurfaceAlt
                            },
                        )
                        .border(
                            width = if (active || filled) 1.5.dp else 1.dp,
                            color = when {
                                active -> MobileColors.Accent
                                filled -> MobileColors.AccentLight
                                else -> MobileColors.Border
                            },
                            shape = RoundedCornerShape(MobileDimens.CardRadiusLarge),
                        ),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(
                        text = value.getOrNull(index)?.toString() ?: "",
                        style = TextStyle(
                            fontSize = 28.sp,
                            fontWeight = FontWeight.Bold,
                            fontFamily = AlimamaFamily,
                            textAlign = TextAlign.Center,
                        ),
                        color = if (active || filled) MobileColors.Accent else MobileColors.Ink,
                    )
                }
            }
        }
        // 真正的数字键盘承载者：铺满方格行但完全透明（字号 1sp + 透明光标，
        // 视觉上不可见）。必须给足尺寸：0dp/1dp 会被测量成不可聚焦，
        // 实测 requestFocus() 后输入法类型不切换。
        // 点击由外层 Box 的 clickable 统一转成 requestFocus，避免两层争夺命中区。
        BasicTextField(
            value = value,
            onValueChange = { input ->
                onChange(input.filter { it.isDigit() }.take(4))
            },
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
            modifier = Modifier
                .fillMaxWidth()
                .height(MobileDimens.PinBoxHeight)
                .focusRequester(focusRequester)
                .onFocusChanged { focused = it.isFocused },
            textStyle = TextStyle(color = Color.Transparent, fontSize = 1.sp),
            cursorBrush = SolidColor(Color.Transparent),
        )
    }
}

/**
 * 局域网雷达指示器：扫描中旋转扫描线，空闲时只画同心圆环。
 * 纯 Canvas 绘制，不依赖额外动画库，不引入非官方资产。
 */
@Composable
private fun RadarIndicator(size: androidx.compose.ui.unit.Dp, active: Boolean) {
    val accent = MobileColors.Accent
    val transition = rememberInfiniteTransition(label = "radar")
    val angle by transition.animateFloat(
        initialValue = 0f,
        targetValue = 360f,
        animationSpec = infiniteRepeatable(animation = tween(1400, easing = LinearEasing)),
        label = "radar-angle",
    )
    val pulse by transition.animateFloat(
        initialValue = 0.35f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(animation = tween(1400)),
        label = "radar-pulse",
    )
    Canvas(modifier = Modifier.size(size)) {
        val center = Offset(this.size.width / 2f, this.size.height / 2f)
        val radius = this.size.minDimension / 2f
        val stroke = 1.5.dp.toPx()
        drawCircle(
            color = accent,
            radius = radius - stroke,
            center = center,
            style = Stroke(width = stroke),
        )
        drawCircle(
            color = MobileColors.AccentLight,
            radius = (radius - stroke) * (if (active) pulse else 0.55f),
            center = center,
            style = Stroke(width = stroke),
        )
        if (active) {
            val radians = Math.toRadians(angle.toDouble())
            drawLine(
                color = accent,
                start = center,
                end = Offset(
                    center.x + (radius - stroke) * kotlin.math.cos(radians).toFloat(),
                    center.y + (radius - stroke) * kotlin.math.sin(radians).toFloat(),
                ),
                strokeWidth = stroke,
            )
        }
    }
}

@Composable
private fun FoundDeviceRow(url: String, onUse: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(MobileColors.SuccessSoft)
            .clickable(onClick = onUse)
            .padding(horizontal = 12.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        WorkBuddyAsset(
            asset = "computer.svg",
            size = 22.dp,
            tint = MobileColors.Success,
            contentDescription = null,
        )
        Column(modifier = Modifier.weight(1f)) {
            Text(text = "Fufucode 电脑", style = MobileTypography.BodySmall, color = MobileColors.Ink)
            Text(
                text = url,
                style = MobileTypography.Caption,
                color = MobileColors.Muted,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        Text(text = "使用", style = MobileTypography.Button, color = MobileColors.Success)
    }
}

/** 主按钮：整宽、≥48dp 命中区、禁用态与语义一致。 */
@Composable
fun PrimaryActionButton(
    label: String,
    onClick: () -> Unit,
    enabled: Boolean = true,
    modifier: Modifier = Modifier,
) {
    Box(
        modifier = modifier
            .fillMaxWidth()
            .heightIn(min = MobileDimens.TouchTarget)
            // 官方规范：主按钮为品牌蓝实心、圆角 8px 小控件档。
            .clip(RoundedCornerShape(MobileDimens.ControlRadius))
            .background(if (enabled) MobileColors.Action else MobileColors.Action.copy(alpha = 0.35f))
            .clickable(enabled = enabled, onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text = label,
            style = MobileTypography.Button,
            color = MobileColors.OnAction,
        )
    }
}

@Composable
fun SecondaryTextButton(label: String, onClick: () -> Unit) {
    Box(
        modifier = Modifier
            .heightIn(min = MobileDimens.TouchTargetMin)
            .clip(RoundedCornerShape(10.dp))
            .clickable(onClick = onClick)
            .padding(horizontal = 12.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(text = label, style = MobileTypography.Button, color = MobileColors.AccentStrong)
    }
}
