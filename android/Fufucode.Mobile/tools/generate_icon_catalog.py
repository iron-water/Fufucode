"""把 app/src/main/assets/icons 下的真实资产清单生成为 Kotlin 目录对象。

目的：界面只能引用实际存在的参考资产，禁止凭记忆编图标名。
用法：python tools/generate_icon_catalog.py
输出：app/src/main/java/com/fufucode/mobile/design/WorkBuddyIcons.kt
"""

from __future__ import annotations

import pathlib

ROOT = pathlib.Path(__file__).resolve().parent.parent
ICONS = ROOT / "app" / "src" / "main" / "assets" / "icons"
OUT = ROOT / "app" / "src" / "main" / "java" / "com" / "fufucode" / "mobile" / "design" / "WorkBuddyIcons.kt"

HEADER = '''package com.fufucode.mobile.design

/**
 * 参考包（com.tencent.workbuddy.app 2.5.0）提取资产的**完整清单**。
 *
 * 本文件由 `tools/generate_icon_catalog.py` 依据 `assets/icons` 实际文件生成，
 * 不允许手写或凭记忆增删。共 {count} 个资产：{svg} 个 SVG + {png} 个 PNG。
 *
 * 许可状态：来自本机下载的参考安装包，仅用于内部对标研发；对外分发前须由产品
 * 侧确认授权，本目录不代表已获得再分发许可。
 */
object WorkBuddyIcons {{

    /** 资产根目录（相对 app assets）。 */
    const val ROOT = "icons"

    /**
     * 参考包提取的专属字体（阿里妈妈数黑体 Bold）。
     * 为让 Compose 直接按 `R.font.*` 引用，字体放在 `res/font/` 而非 assets。
     */
    const val FontAlimamaShuHeiTiBold = "res/font/alimama_shu_hei_ti_bold.ttf"

    /** 全部资产的文件名（含扩展名），按名称升序。 */
    val all: List<String> = listOf(
{entries}    )

    private val index: Set<String> = all.toHashSet()

    /** 资产是否存在；界面引用前可校验，避免静默显示空白。 */
    fun has(name: String): Boolean = index.contains(name)
}}
'''


def main() -> None:
    files = sorted(p.name for p in ICONS.iterdir() if p.is_file())
    svg = sum(1 for name in files if name.lower().endswith(".svg"))
    png = sum(1 for name in files if name.lower().endswith(".png"))
    entries = "".join(f'        "{name}",\n' for name in files)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(
        HEADER.format(count=len(files), svg=svg, png=png, entries=entries),
        encoding="utf-8",
    )
    print(f"{OUT.relative_to(ROOT)}  assets={len(files)} svg={svg} png={png}")


if __name__ == "__main__":
    main()
