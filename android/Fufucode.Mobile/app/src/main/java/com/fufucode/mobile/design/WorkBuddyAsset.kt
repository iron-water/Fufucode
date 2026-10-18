package com.fufucode.mobile.design

import android.content.Context
import android.graphics.BitmapFactory
import android.graphics.Picture
import android.graphics.RectF
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.runtime.produceState
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.drawIntoCanvas
import androidx.compose.ui.graphics.nativeCanvas
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import com.caverock.androidsvg.SVG
import java.util.concurrent.ConcurrentHashMap

/**
 * 参考包资产的**真实**加载器：SVG 走 AndroidSVG 解析（支持参考包实际用到的
 * 渐变、clipPath、mask 与 CSS class），PNG 走 BitmapFactory。
 *
 * 关键约束：所有资产都来自 `assets/icons`（清单见 [WorkBuddyIcons]），
 * 加载失败返回 null 由调用方降级，绝不静默画一个形状相似的替代图形。
 */
object WorkBuddyAssetLoader {

    private data class Key(val asset: String, val tint: Int)

    private val cache = ConcurrentHashMap<Key, Picture>()
    private val failures = ConcurrentHashMap<String, String>()

    /** 已失败资产名 -> 失败原因；用于诊断面板与测试断言。 */
    val failureReport: Map<String, String> get() = failures.toMap()

    /**
     * 把参考包 SVG 渲染为可绘制对象。
     *
     * 参考包 123 个 SVG 用 `currentColor` 表达主题色（浅/深色共用一份资源）。
     * AndroidSVG 未公开 currentColor 的注入 API，因此这里在解析前把 `currentColor`
     * 文本替换为调用方指定的十六进制色值——替换是确定性的，并且
     * [countCurrentColor] 让测试可以核对替换确实命中。
     */
    fun loadPicture(context: Context, asset: String, tint: Color?): Picture? {
        val key = Key(asset, tint?.value?.let { (it shr 32).toInt() } ?: 0)
        cache[key]?.let { return it }
        return try {
            val source = context.assets.open("${WorkBuddyIcons.ROOT}/$asset")
                .use { it.readBytes().toString(Charsets.UTF_8) }
            val patched = if (tint != null) {
                source.replace("currentColor", tint.toHexString(), ignoreCase = false)
                    .replace(Regex("""(?i)(fill|stroke)="(?:#000000|#000|black)"""")) {
                        "${it.groupValues[1]}=\"${tint.toHexString()}\""
                    }
            } else {
                source
            }
            val themed = if (tint != null && asset.endsWith("-white.svg"))
                patched.replace(Regex("""(?i)(fill|stroke)="(?:#ffffff|#fff|white)"""")) {
                    "${it.groupValues[1]}=\"${tint.toHexString()}\""
                } else patched
            val svg = SVG.getFromString(themed)
            val width = svg.documentWidth.takeIf { it > 0f } ?: svg.documentViewBox?.width() ?: 24f
            val height = svg.documentHeight.takeIf { it > 0f } ?: svg.documentViewBox?.height() ?: 24f
            if (svg.documentViewBox == null) {
                svg.setDocumentViewBox(0f, 0f, width, height)
            }
            val picture = Picture()
            val canvas = picture.beginRecording(width.toInt().coerceAtLeast(1), height.toInt().coerceAtLeast(1))
            svg.renderToCanvas(canvas)
            picture.endRecording()
            cache[key] = picture
            failures.remove(asset)
            picture
        } catch (error: Exception) {
            failures[asset] = "${error::class.simpleName}: ${error.message}"
            null
        }
    }

    /** PNG 资产（参考包中 9 个位图图标）。 */
    fun loadBitmap(context: Context, asset: String) = try {
        context.assets.open("${WorkBuddyIcons.ROOT}/$asset").use { BitmapFactory.decodeStream(it) }
    } catch (error: Exception) {
        failures[asset] = "${error::class.simpleName}: ${error.message}"
        null
    }

    private fun Color.toHexString(): String {
        // SVG 使用 CSS RGB；Android ARGB 的八位顺序会把黑图标渲染成半透明红色。
        return String.format("#%06X", toArgb() and 0xFFFFFF)
    }
}

/** 统计一份 SVG 源里 `currentColor` 出现的次数（供单元测试核对替换语义）。 */
fun countCurrentColor(svgSource: String): Int =
    Regex("currentColor", RegexOption.IGNORE_CASE).findAll(svgSource).count()

/**
 * 渲染参考包中的一个资产。
 *
 * @param asset `assets/icons` 下的文件名，必须存在于 [WorkBuddyIcons]。
 * @param tint 非 null 时替换 SVG 中的 `currentColor`；PNG 资产忽略该参数。
 * @param contentDescription 无障碍描述；图形本身无信息时传 null。
 */
@Composable
fun WorkBuddyAsset(
    asset: String,
    modifier: Modifier = Modifier,
    size: Dp? = null,
    tint: Color? = null,
    contentDescription: String? = null,
) {
    val context = LocalContext.current
    val density = LocalDensity.current
    val baseModifier = if (size != null) modifier.size(size) else modifier
    val resolvedModifier = if (contentDescription != null) {
        baseModifier.semantics { this.contentDescription = contentDescription }
    } else {
        baseModifier
    }
    val isBitmap = asset.lowercase().endsWith(".png")

    if (isBitmap) {
        val bitmap = produceState<androidx.compose.ui.graphics.ImageBitmap?>(null, asset) {
            value = WorkBuddyAssetLoader.loadBitmap(context, asset)?.asImageBitmap()
        }
        val image = bitmap.value
        if (image != null) {
            androidx.compose.foundation.Image(
                bitmap = image,
                contentDescription = contentDescription,
                modifier = resolvedModifier,
            )
        } else {
            MissingAssetPlaceholder(resolvedModifier, size, density)
        }
        return
    }

    val picture = produceState<Picture?>(null, asset, tint) {
        value = WorkBuddyAssetLoader.loadPicture(context, asset, tint)
    }
    val resolved = picture.value
    if (resolved == null) {
        MissingAssetPlaceholder(resolvedModifier, size, density)
        return
    }

    val fallback = with(density) { 24.dp.toPx() }
    Canvas(modifier = resolvedModifier) {
        val width = this.size.width.takeIf { it > 0f } ?: fallback
        val height = this.size.height.takeIf { it > 0f } ?: fallback
        val scale = minOf(width / resolved.width.coerceAtLeast(1), height / resolved.height.coerceAtLeast(1))
        val w = resolved.width * scale
        val h = resolved.height * scale
        val box = RectF((width - w) / 2, (height - h) / 2, (width + w) / 2, (height + h) / 2)
        drawIntoCanvas { canvas -> canvas.nativeCanvas.drawPicture(resolved, box) }
    }
}

/**
 * 资产缺失/解析失败的可见占位：一条虚线方框。
 * 明确区别于“画了别的图标”，便于在验收截图中直接发现资产问题。
 */
@Composable
private fun MissingAssetPlaceholder(
    modifier: Modifier,
    size: Dp?,
    density: androidx.compose.ui.unit.Density,
) {
    val dimension = size ?: with(density) { 24.dp }
    Canvas(modifier = modifier.size(dimension)) {
        val stroke = 1.5.dp.toPx()
        val inset = stroke / 2f
        drawRect(
            color = Color(0xFFE5484D),
            topLeft = androidx.compose.ui.geometry.Offset(inset, inset),
            size = androidx.compose.ui.geometry.Size(this.size.width - stroke, this.size.height - stroke),
            style = androidx.compose.ui.graphics.drawscope.Stroke(width = stroke),
        )
    }
}
