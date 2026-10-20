package com.fufucode.mobile.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.fufucode.mobile.data.LinkState
import com.fufucode.mobile.design.LocalCompactLayout
import com.fufucode.mobile.design.LocalCompactPadding
import com.fufucode.mobile.design.MobileColors
import com.fufucode.mobile.design.MobileTheme
import com.fufucode.mobile.ui.screens.AttachmentSheet
import com.fufucode.mobile.ui.screens.ChatScreen
import com.fufucode.mobile.ui.screens.DeviceScreen
import com.fufucode.mobile.ui.screens.PairScreen
import com.fufucode.mobile.ui.screens.SessionDrawer
import com.fufucode.mobile.ui.screens.UuRemoteScreen
import kotlinx.coroutines.launch
import androidx.compose.runtime.rememberCoroutineScope

/** 顶层页面路由。只有四个真实页面，不使用导航框架跳转到不存在的目标。 */
private enum class MobileRoute { Pair, Chat, Device, Uu }

/**
 * 应用根组合。负责：
 * - 页面切换（配对 / 会话 / 设备）；
 * - 抽屉与附件面板的显隐；
 * - Android 返回键的层级处理（先关面板，再回上一页，最后退出）。
 */
@Composable
fun MobileApp(
    viewModel: MobileViewModel = viewModel(),
    uuViewModel: UuRemoteViewModel = viewModel(),
) {
    val state by viewModel.controller.state.collectAsStateWithLifecycle()
    val draft by viewModel.draft.collectAsStateWithLifecycle()
    val attachments by viewModel.attachments.collectAsStateWithLifecycle()
    val discovery by viewModel.discoveryState.collectAsStateWithLifecycle()
    val uuCallbackVersion by uuViewModel.callbackVersion.collectAsStateWithLifecycle()

    var route by remember { mutableStateOf(MobileRoute.Pair) }
    var drawerOpen by remember { mutableStateOf(false) }
    var attachmentSheetOpen by remember { mutableStateOf(false) }
    var latencyMillis by remember { mutableStateOf<Long?>(null) }
    var uuReturnRoute by remember { mutableStateOf(MobileRoute.Pair) }
    val scope = rememberCoroutineScope()

    val configuration = LocalConfiguration.current
    val density = LocalDensity.current
    val compact = with(density) { (configuration.screenWidthDp.dp).value < 360f }

    // 冷启动与热启动回调都落在远程桌面页；拒绝的旧回调不会改变路由。
    LaunchedEffect(uuCallbackVersion) {
        if (uuCallbackVersion > 0) {
            if (route != MobileRoute.Uu) uuReturnRoute = route
            drawerOpen = false
            attachmentSheetOpen = false
            route = MobileRoute.Uu
        }
    }

    // 一旦连上就自动进入会话页；断开/离线则回到配对页。
    LaunchedEffect(state.link) {
        when (state.link) {
            LinkState.Online, LinkState.Reconnecting -> if (route == MobileRoute.Pair) {
                route = MobileRoute.Chat
            }
            LinkState.Idle, LinkState.Offline -> if (route != MobileRoute.Pair && route != MobileRoute.Uu) {
                route = MobileRoute.Pair
                drawerOpen = false
            }
            LinkState.Connecting -> Unit
        }
    }

    // 回到前台/网络恢复时补一次连接尝试（仅在已有令牌且不在线时）。
    LaunchedEffect(state.link) {
        if (state.link == LinkState.Offline && !viewModel.controller.hasPairing && route != MobileRoute.Uu) {
            route = MobileRoute.Pair
        }
    }

    CompositionLocalProvider(
        LocalCompactLayout provides compact,
        LocalCompactPadding provides (if (compact) 12.dp else 16.dp),
    ) {
        MobileTheme {
            Box(modifier = Modifier.fillMaxSize().background(MobileColors.Background).statusBarsPadding()) {
                when (route) {
                    MobileRoute.Pair -> PairScreen(
                        linkState = state.link,
                        discovery = discovery,
                        notice = state.notice,
                        lastPort = state.server?.port ?: 0,
                        onPair = viewModel::pair,
                        onScan = { viewModel.scanLan() },
                        onStopScan = viewModel::stopScan,
                        onDismissNotice = viewModel::dismissNotice,
                        onOpenRemoteDesktop = { uuReturnRoute = MobileRoute.Pair; route = MobileRoute.Uu },
                    )

                    MobileRoute.Chat -> ChatScreen(
                        state = state,
                        draft = draft,
                        attachments = attachments,
                        onDraftChange = viewModel::updateDraft,
                        onOpenDrawer = { drawerOpen = true },
                        onSend = viewModel::send,
                        onStop = viewModel::stop,
                        onResolveApproval = { id, approve -> viewModel.resolveApproval(id, approve) },
                        onOpenAttachments = { attachmentSheetOpen = true },
                        onRemoveAttachment = viewModel::removeAttachment,
                        onDismissNotice = viewModel::dismissNotice,
                        onOpenDevice = { route = MobileRoute.Device },
                    )

                    MobileRoute.Device -> DeviceScreen(
                        state = state,
                        pairedBaseUrl = viewModel.controller.baseUrl,
                        latencyMillis = latencyMillis,
                        onBack = { route = MobileRoute.Chat },
                        onPing = {
                            scope.launch {
                                latencyMillis = viewModel.measureLatency()
                            }
                        },
                        onDisconnect = {
                            viewModel.disconnect()
                            latencyMillis = null
                            route = MobileRoute.Pair
                        },
                        onReconnect = { viewModel.controller.connect() },
                        onDismissNotice = viewModel::dismissNotice,
                        onOpenRemoteDesktop = { uuReturnRoute = MobileRoute.Device; route = MobileRoute.Uu },
                    )
                    MobileRoute.Uu -> UuRemoteScreen(uuViewModel) {
                        route = if (state.link == LinkState.Online) uuReturnRoute else MobileRoute.Pair
                    }
                }

                if (drawerOpen) {
                    SessionDrawer(
                        state = state,
                        pairedBaseUrl = viewModel.controller.baseUrl,
                        onClose = { drawerOpen = false },
                        onOpenDevice = {
                            drawerOpen = false
                            route = MobileRoute.Device
                        },
                        onDisconnect = {
                            drawerOpen = false
                            viewModel.disconnect()
                            route = MobileRoute.Pair
                        },
                        sessions = state.sessions,
                        onSwitchSession = viewModel::switchSession,
                    )
                }

                if (attachmentSheetOpen) {
                    AttachmentSheet(
                        onDismiss = { attachmentSheetOpen = false },
                        onPick = viewModel::addAttachment,
                    )
                }
            }

            // 返回键层级：附件面板 → 抽屉 → 设备页 → 会话页 → 系统默认（退出）。
            BackHandler(enabled = attachmentSheetOpen) { attachmentSheetOpen = false }
            BackHandler(enabled = !attachmentSheetOpen && drawerOpen) { drawerOpen = false }
            BackHandler(enabled = !attachmentSheetOpen && !drawerOpen && route == MobileRoute.Device) {
                route = MobileRoute.Chat
            }
            BackHandler(enabled = route == MobileRoute.Uu) {
                route = if (state.link == LinkState.Online) uuReturnRoute else MobileRoute.Pair
            }
        }
    }
}

