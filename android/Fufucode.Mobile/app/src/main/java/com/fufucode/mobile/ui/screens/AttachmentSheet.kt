package com.fufucode.mobile.ui.screens

import android.content.Context
import android.database.Cursor
import android.net.Uri
import android.provider.OpenableColumns
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
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
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.runtime.remember
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.fufucode.mobile.data.AttachmentKind
import com.fufucode.mobile.data.PendingAttachment
import com.fufucode.mobile.design.MobileColors
import com.fufucode.mobile.design.MobileDimens
import com.fufucode.mobile.design.MobileTypography
import com.fufucode.mobile.design.WorkBuddyAsset

/**
 * 界面 5：附件与扩展面板。
 *
 * 真实能力：通过系统文件选择器（SAF）与图片选择器拿到用户显式选中的条目，
 * 读取其**真实**显示名与大小；能解析出本地路径时一并引用。
 *
 * 明确的能力边界（不夸大）：远控协议 `/api/message` 只接收文本，没有二进制上传通道。
 * 所以这里只发送文件说明；电脑端不能直接读取手机路径，也没有收到文件内容。
 */
@Composable
fun AttachmentSheet(
    onDismiss: () -> Unit,
    onPick: (PendingAttachment) -> Unit,
) {
    val context = LocalContext.current

    val imagePicker = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.PickVisualMedia(),
    ) { uri: Uri? ->
        uri?.let { onPick(describe(context, it, AttachmentKind.Image)) }
        onDismiss()
    }

    val documentPicker = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.OpenDocument(),
    ) { uri: Uri? ->
        uri?.let { picked ->
            // 持久化读权限，使引用在本次会话内保持可用（不改变原文件）。
            runCatching {
                context.contentResolver.takePersistableUriPermission(
                    picked,
                    android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION,
                )
            }
            onPick(describe(context, picked, AttachmentKind.Document))
        }
        onDismiss()
    }

    val cameraPicker = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.TakePicturePreview(),
    ) { bitmap ->
        if (bitmap != null) {
            onPick(
                PendingAttachment(
                    uri = "camera://preview/${bitmap.width}x${bitmap.height}",
                    displayName = "相机照片（${bitmap.width}×${bitmap.height}）",
                    kind = AttachmentKind.Camera,
                    sizeBytes = null,
                    localPath = null,
                ),
            )
        }
        onDismiss()
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color(0x99000000))
            .clickable(onClick = onDismiss),
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .align(Alignment.BottomCenter)
                .padding(horizontal = 8.dp, vertical = 8.dp)
                .clip(RoundedCornerShape(topStart = 20.dp, topEnd = 20.dp))
                .background(MobileColors.Surface)
                .clickable(interactionSource = remember { MutableInteractionSource() }, indication = null) {}
                .navigationBarsPadding()
                .verticalScroll(rememberScrollState())
                .padding(vertical = 16.dp),
        ) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = "附件与扩展",
                    style = MobileTypography.SectionTitle,
                    color = MobileColors.Ink,
                    modifier = Modifier.weight(1f),
                )
                Box(
                    modifier = Modifier
                        .size(MobileDimens.TouchTargetMin)
                        .clip(RoundedCornerShape(10.dp))
                        .clickable(onClick = onDismiss),
                    contentAlignment = Alignment.Center,
                ) {
                    WorkBuddyAsset(
                        asset = "close.svg",
                        size = 16.dp,
                        tint = MobileColors.Muted,
                        contentDescription = "关闭",
                    )
                }
            }

            Text(
                text = "这里只发送文件信息；不上传内容，电脑端无法直接读取手机文件。",
                style = MobileTypography.Caption,
                color = MobileColors.Muted,
                modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp),
            )

            Spacer(Modifier.height(6.dp))

            SheetAction(
                asset = "icon-album.svg",
                title = "照片",
                subtitle = "从系统图库选择图片",
                onClick = { imagePicker.launch(androidx.activity.result.PickVisualMediaRequest(
                    ActivityResultContracts.PickVisualMedia.ImageOnly,
                )) },
            )
            SheetAction(
                asset = "camera.svg",
                title = "拍照",
                subtitle = "用相机拍一张并作为引用",
                onClick = { cameraPicker.launch(null) },
            )
            SheetAction(
                asset = "phone-file.svg",
                title = "手机文件",
                subtitle = "从系统文件浏览选择任意文档",
                onClick = { documentPicker.launch(arrayOf("*/*")) },
            )

            Spacer(Modifier.height(8.dp))
            Text(
                text = "扩展能力",
                style = MobileTypography.Caption,
                color = MobileColors.Muted,
                modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp),
            )
            // 这些是参考包中真实存在的入口，本版本如实标注为未接入，不做假入口。
            SheetAction(
                asset = "mcp-tools.svg",
                title = "MCP 工具",
                subtitle = "由电脑端 Agent 管理与执行，手机端暂不提供配置入口",
                enabled = false,
                onClick = {},
            )
            SheetAction(
                asset = "my-skills.svg",
                title = "技能库",
                subtitle = "由电脑端 Agent 管理与执行，手机端暂不提供配置入口",
                enabled = false,
                onClick = {},
            )
        }
    }
}

@Composable
private fun SheetAction(
    asset: String,
    title: String,
    subtitle: String,
    onClick: () -> Unit,
    enabled: Boolean = true,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 10.dp, vertical = 2.dp)
            .clip(RoundedCornerShape(12.dp))
            .clickable(enabled = enabled, onClick = onClick)
            .padding(horizontal = 12.dp, vertical = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Box(
            modifier = Modifier
                .size(38.dp)
                .clip(RoundedCornerShape(10.dp))
                .background(if (enabled) MobileColors.AccentSoft else MobileColors.SurfaceAlt)
                .border(1.dp, MobileColors.Border, RoundedCornerShape(10.dp)),
            contentAlignment = Alignment.Center,
        ) {
            WorkBuddyAsset(
                asset = asset,
                size = 20.dp,
                tint = if (enabled) MobileColors.AccentStrong else MobileColors.Muted,
                contentDescription = null,
            )
        }
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = title,
                style = MobileTypography.BodySmall,
                color = if (enabled) MobileColors.Ink else MobileColors.Muted,
            )
            Text(text = subtitle, style = MobileTypography.Caption, color = MobileColors.Muted)
        }
        if (enabled) {
            WorkBuddyAsset(
                asset = "chevron-right.svg",
                size = 16.dp,
                tint = MobileColors.Muted,
                contentDescription = null,
            )
        }
    }
}

/** 读取内容提供者给出的真实显示名与大小；缺失字段记为 null，不填 0 假装知道。 */
private fun describe(context: Context, uri: Uri, kind: AttachmentKind): PendingAttachment {
    var name: String? = null
    var size: Long? = null
    runCatching {
        context.contentResolver.query(uri, null, null, null, null)?.use { cursor: Cursor ->
            if (cursor.moveToFirst()) {
                val nameIndex = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                if (nameIndex >= 0 && !cursor.isNull(nameIndex)) name = cursor.getString(nameIndex)
                val sizeIndex = cursor.getColumnIndex(OpenableColumns.SIZE)
                if (sizeIndex >= 0 && !cursor.isNull(sizeIndex)) size = cursor.getLong(sizeIndex)
            }
        }
    }

    return PendingAttachment(
        uri = uri.toString(),
        displayName = name ?: uri.lastPathSegment?.substringAfterLast('/') ?: "未命名文件",
        kind = kind,
        sizeBytes = size,
        localPath = uri.path?.takeIf { uri.scheme == "file" },
    )
}
