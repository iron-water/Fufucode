package com.fufucode.mobile.design

import androidx.compose.material3.Typography
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.runtime.SideEffect
import androidx.compose.ui.platform.LocalView
import androidx.core.view.WindowCompat
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.fufucode.mobile.R

/**
 * Fufucode 移动端设计令牌。
 *
 * 取值来源：WorkBuddy-v2.6.1/theme/tokens.json 与 docs/D-视觉基调复刻包.md。
 * 这里映射远控产品角色；字号等未解析项按系统可读性选取，不声称像素级还原。
 */
object MobileColors {
    /** v2.6.1 薄荷绿强调色；深色用亮描边色保证可读。 */
    val Accent @Composable get() = if (LocalMobileDark.current) Color(0xFF3FBA94) else Color(0xFF10A37F)
    val AccentStrong @Composable get() = Accent
    val AccentLight = Color(0xFF3FBA94)

    /** 参考包 primaryTint 与深色18%填充。 */
    val AccentSoft @Composable get() = if (LocalMobileDark.current) Color(0x2E10A37F) else Color(0xFFE8F5F0)
    val AccentRing = Color(0x3310A37F)
    // 中性操作色，与语义强调色分开；不将图表主题色套到每个按钮。
    val Action @Composable get() = if (LocalMobileDark.current) Color(0xFFEDEDED) else Color(0xFF202020)
    val OnAction @Composable get() = if (LocalMobileDark.current) Color(0xFF202020) else Color.White

    // 前景色（亮色主题）
    val Ink @Composable get() = if (LocalMobileDark.current) DarkInk else Color(0xFF171717)
    val InkSecondary @Composable get() = if (LocalMobileDark.current) Color(0xFFBDBDBF) else Color(0xFF525252)
    val Muted @Composable get() = if (LocalMobileDark.current) DarkMuted else Color(0xFF8B8B8B)

    // 背景与表面（亮色主题）
    val Background @Composable get() = if (LocalMobileDark.current) DarkBackground else Color.White
    val Surface @Composable get() = if (LocalMobileDark.current) DarkSurface else Color.White
    val SurfaceAlt @Composable get() = if (LocalMobileDark.current) Color(0xFF38383A) else Color(0xFFF2F2F7)
    val Border @Composable get() = if (LocalMobileDark.current) Color(0xFF38383A) else Color(0xFFE5E5E5)
    val Divider @Composable get() = Border

    // 暗色主题（深色模式补齐官方规范条目）
    val DarkBackground = Color(0xFF1C1C1E)
    val DarkSurface = Color(0xFF2C2C2E)
    val DarkInk = Color(0xFFE5E5EA)
    val DarkMuted = Color(0xFF98989D)
    val DarkBorder = Color(0x14FFFFFF)

    val Warning @Composable get() = if (LocalMobileDark.current) Color(0xFFFFD980) else Color(0xFFAA640E)
    val WarningSoft @Composable get() = if (LocalMobileDark.current) Color(0xFF3D2E00) else Color(0xFFFFF4D6)

    /** 危险红：官方亮色 #FF3B30，浅底为 8% 叠加、边框 20% 叠加。 */
    val Danger @Composable get() = if (LocalMobileDark.current) Color(0xFFFF6961) else Color(0xFFD54941)
    val DangerSoft @Composable get() = Danger.copy(alpha = .10f)
    val DangerBorder = Color(0x33FF3B30)

    /** 成功绿：官方规范 #1D9E75。 */
    val Success @Composable get() = Accent
    val SuccessSoft @Composable get() = AccentSoft

    val Info @Composable get() = if (LocalMobileDark.current) Color(0xFF72AAFF) else Color(0xFF1677FF)
    val InfoSoft @Composable get() = Info.copy(alpha = .10f)

    /** 远控会话气泡使用低对比主色浅底，正文与主题同源。 */
    val UserBubble @Composable get() = SurfaceAlt
    val UserBubbleInk @Composable get() = Ink
    val AssistantBubble @Composable get() = Background
}

/**
 * 参考包明确度量：点击区44、导航60、页面左右20。
 * Android主要动作取48dp；不能把截图像素尺寸误当成可触摸的dp尺寸。
 */
object MobileDimens {
    val TouchTarget: Dp = 48.dp
    val TouchTargetMin: Dp = 44.dp
    val ScreenPadding: Dp = 20.dp
    val ControlRadius: Dp = 8.dp
    val CardRadius: Dp = 12.dp
    val CardRadiusLarge: Dp = 16.dp
    val BubbleRadius: Dp = 16.dp
    val ComposerRadius: Dp = 24.dp
    val PinBoxWidth: Dp = 56.dp
    val PinBoxHeight: Dp = 64.dp
    val RowGap: Dp = 12.dp
    val ToolbarHeight: Dp = 60.dp
    val ComposerMinHeight: Dp = 44.dp
}

/** 阿里妈妈数黑体（参考包 `fonts/AlimamaShuHeiTi-Bold.ttf`），用于标题与按钮。 */
val AlimamaFamily: FontFamily = FontFamily(
    Font(R.font.alimama_shu_hei_ti_bold, FontWeight.Bold),
)

/**
 * 卡片圆角/间距集中在此，避免各页面各写一套数值产生漂移。
 */
object MobileTypography {
    val Title = TextStyle(fontSize = 24.sp, lineHeight = 32.sp, fontWeight = FontWeight.SemiBold)
    val ToolbarTitle = TextStyle(fontSize = 16.sp, lineHeight = 22.sp, fontWeight = FontWeight.Medium)
    val SectionTitle = TextStyle(fontSize = 15.sp, lineHeight = 20.sp, fontWeight = FontWeight.SemiBold)
    val Button = TextStyle(fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
    val Body = TextStyle(fontSize = 15.sp, lineHeight = 22.sp)
    val BodySmall = TextStyle(fontSize = 13.sp, lineHeight = 19.sp)
    val Caption = TextStyle(fontSize = 12.sp, lineHeight = 16.sp)
    val Mono = TextStyle(fontSize = 12.sp, lineHeight = 17.sp, fontFamily = FontFamily.Monospace)
}

val MobileMaterialTypography = Typography(
    titleLarge = MobileTypography.Title,
    titleMedium = MobileTypography.ToolbarTitle,
    titleSmall = MobileTypography.SectionTitle,
    bodyLarge = MobileTypography.Body,
    bodyMedium = MobileTypography.BodySmall,
    bodySmall = MobileTypography.Caption,
    labelLarge = MobileTypography.Button,
)

/**
 * 是否处于窄屏/紧凑布局。由根布局按可用宽度写入，供各页面按需收缩间距。
 */
val LocalCompactLayout = staticCompositionLocalOf { false }

/** 窄屏下的水平安全边距。 */
val LocalCompactPadding = staticCompositionLocalOf { 16.dp }
val LocalMobileDark = staticCompositionLocalOf { false }

@Composable
fun MobileTheme(dark: Boolean = isSystemInDarkTheme(), content: @Composable () -> Unit) {
    val view = LocalView.current
    SideEffect {
        (view.context as? android.app.Activity)?.let { activity ->
            WindowCompat.getInsetsController(activity.window, view).apply {
                isAppearanceLightStatusBars = !dark
                isAppearanceLightNavigationBars = !dark
            }
        }
    }
    CompositionLocalProvider(LocalMobileDark provides dark) {
    val colors = if (dark) androidx.compose.material3.darkColorScheme() else androidx.compose.material3.lightColorScheme()
    androidx.compose.material3.MaterialTheme(
        colorScheme = colors.copy(
            primary = MobileColors.Accent,
            onPrimary = Color.White,
            secondary = MobileColors.AccentStrong,
            background = MobileColors.Background,
            onBackground = MobileColors.Ink,
            surface = MobileColors.Surface,
            onSurface = MobileColors.Ink,
            surfaceVariant = MobileColors.SurfaceAlt,
            outline = MobileColors.Border,
            error = MobileColors.Danger,
        ),
        typography = MobileMaterialTypography,
        content = content,
    )
    }
}
