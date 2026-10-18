package com.fufucode.mobile.data

/** 附件的来源分类，决定图标与是否可直接读取字节。 */
enum class AttachmentKind { Image, Document, Camera }

/**
 * 一条待发送的附件引用。
 *
 * 语义边界：远控协议（`/api/message`）只接受纯文本，**没有**附件上传通道。
 * 因此附件在本客户端的作用是「把本地文件的绝对路径/显示名作为引用文本附到消息前」，
 * 手机路径在电脑端不可直接读取。这只是文件说明，不是二进制上传。
 */
data class PendingAttachment(
    val uri: String,
    val displayName: String,
    val kind: AttachmentKind,
    /** 可读取到时记录大小；系统不返回时为 null，不猜 0。 */
    val sizeBytes: Long? = null,
    /** 从媒体库解析到的本地路径；无法解析时为 null（此时只发文件名，不假装路径可用）。 */
    val localPath: String? = null,
)

/** 把附件列表组合为随消息一并发送的引用块。 */
fun buildAttachmentReferenceBlock(attachments: List<PendingAttachment>): String {
    if (attachments.isEmpty()) return ""
    val lines = attachments.map { attachment ->
        val location = attachment.localPath ?: attachment.displayName
        val size = attachment.sizeBytes?.let { "（${formatSize(it)}）" }.orEmpty()
        "- $location$size"
    }
    return buildString {
        appendLine("【手机附件说明：未上传文件内容，电脑不能直接读取以下手机路径】")
        lines.forEach { appendLine(it) }
    }
}

fun formatSize(bytes: Long): String = when {
    bytes < 1024 -> "$bytes B"
    bytes < 1024 * 1024 -> "%.1f KB".format(bytes / 1024.0)
    bytes < 1024L * 1024 * 1024 -> "%.1f MB".format(bytes / (1024.0 * 1024))
    else -> "%.2f GB".format(bytes / (1024.0 * 1024 * 1024))
}
