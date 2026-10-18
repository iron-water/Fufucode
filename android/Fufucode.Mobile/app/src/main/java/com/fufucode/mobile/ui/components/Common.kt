package com.fufucode.mobile.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.fufucode.mobile.design.MobileColors
import com.fufucode.mobile.design.MobileDimens
import com.fufucode.mobile.design.MobileTypography
import com.fufucode.mobile.design.WorkBuddyAsset

/** 顶部工具栏：返回/菜单按钮 + 标题 + 尾部动作，命中区不低于 44dp。 */
@Composable
fun MobileTopBar(
    title: String,
    subtitle: String? = null,
    leadingAsset: String? = null,
    leadingDescription: String? = null,
    onLeadingClick: (() -> Unit)? = null,
    trailing: @Composable (() -> Unit)? = null,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(MobileColors.Background)
            .padding(horizontal = 8.dp)
            .heightIn(min = MobileDimens.ToolbarHeight),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (leadingAsset != null && onLeadingClick != null) {
            Box(
                modifier = Modifier
                    .size(MobileDimens.TouchTarget)
                    .clip(RoundedCornerShape(12.dp))
                    .clickable(onClick = onLeadingClick),
                contentAlignment = Alignment.Center,
            ) {
                WorkBuddyAsset(
                    asset = leadingAsset,
                    size = 22.dp,
                    tint = MobileColors.Ink,
                    contentDescription = leadingDescription,
                )
            }
        } else {
            Box(Modifier.size(8.dp))
        }

        Column(
            modifier = Modifier
                .weight(1f)
                .padding(horizontal = 4.dp),
        ) {
            Text(
                text = title,
                style = MobileTypography.ToolbarTitle,
                color = MobileColors.Ink,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            if (!subtitle.isNullOrBlank()) {
                Text(
                    text = subtitle,
                    style = MobileTypography.Caption,
                    color = MobileColors.Muted,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }

        trailing?.invoke()
    }
}

/** 状态胶囊：颜色与语义一一对应，不使用装饰性配色。 */
@Composable
fun StatusPill(text: String, tone: PillTone, modifier: Modifier = Modifier) {
    val (background, ink) = tone.colors()
    Box(
        modifier = modifier
            .clip(RoundedCornerShape(999.dp))
            .background(background)
            .padding(horizontal = 10.dp, vertical = 4.dp),
    ) {
        Text(text = text, style = MobileTypography.Caption, color = ink, maxLines = 1)
    }
}

enum class PillTone { Neutral, Success, Warning, Danger, Info, Accent }

@Composable
private fun PillTone.colors(): Pair<Color, Color> = when (this) {
    PillTone.Neutral -> MobileColors.SurfaceAlt to MobileColors.InkSecondary
    PillTone.Success -> MobileColors.SuccessSoft to MobileColors.Success
    PillTone.Warning -> MobileColors.WarningSoft to MobileColors.Warning
    PillTone.Danger -> MobileColors.DangerSoft to MobileColors.Danger
    PillTone.Info -> MobileColors.InfoSoft to MobileColors.Info
    PillTone.Accent -> MobileColors.AccentSoft to MobileColors.AccentStrong
}

/** 信息条：用于提示连接状态、错误与通知；点击可关闭。 */
@Composable
fun InfoBanner(
    message: String,
    tone: PillTone,
    onDismiss: (() -> Unit)? = null,
    action: (@Composable () -> Unit)? = null,
) {
    val (background, ink) = tone.colors()
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 12.dp, vertical = 6.dp)
            .clip(RoundedCornerShape(24.dp))
            .background(background)
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalAlignment = Alignment.Top,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Text(
            text = message,
            style = MobileTypography.BodySmall,
            color = ink,
            modifier = Modifier.weight(1f),
        )
        action?.invoke()
        if (onDismiss != null) {
            Box(
                modifier = Modifier
                    .size(44.dp)
                    .clip(RoundedCornerShape(8.dp))
                    .clickable(onClick = onDismiss),
                contentAlignment = Alignment.Center,
            ) {
                WorkBuddyAsset(asset = "close.svg", size = 14.dp, tint = ink, contentDescription = "关闭提示")
            }
        }
    }
}

/** 卡片外壳：统一圆角、边框与内边距。 */
@Composable
fun MobileCard(
    modifier: Modifier = Modifier,
    background: Color = MobileColors.Surface,
    borderColor: Color = MobileColors.Border.copy(alpha = .45f),
    content: @Composable () -> Unit,
) {
    Box(
        modifier = modifier
            .clip(RoundedCornerShape(MobileDimens.CardRadius))
            .background(background)
            .border(1.dp, borderColor, RoundedCornerShape(MobileDimens.CardRadius)),
    ) {
        content()
    }
}

/** 空状态：参考包的官方空态插图 + 真实说明文案。 */
@Composable
fun EmptyState(
    illustrationAsset: String,
    title: String,
    description: String,
    action: (@Composable () -> Unit)? = null,
) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 32.dp, vertical = 48.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        WorkBuddyAsset(asset = illustrationAsset, size = 132.dp, contentDescription = null)
        Text(text = title, style = MobileTypography.SectionTitle, color = MobileColors.Ink)
        Text(
            text = description,
            style = MobileTypography.BodySmall,
            color = MobileColors.Muted,
        )
        action?.invoke()
    }
}
