"""生成 Fufucode 移动端启动图标（自有资产，非参考应用品牌图）。

设计语言与 WorkBuddy 参考包同族（青绿渐变圆角方块），但图形为 Fufucode 自有标记：
终端提示符 `>` + 光标块，语义为「本地 Agent 控制台」。
不复制参考应用的 claw 品牌图形，避免商标混同。

用法：python tools/generate_launcher_icons.py
输出：app/src/main/res/mipmap-*/ic_launcher.png 与 ic_launcher_round.png
"""

from __future__ import annotations

import pathlib

from PIL import Image, ImageDraw

ROOT = pathlib.Path(__file__).resolve().parent.parent
RES = ROOT / "app" / "src" / "main" / "res"

# 与参考包同一族配色（app_icon.svg 的 linearGradient 端点），用于视觉一致性。
GRADIENT_TOP = (0x0E, 0xC8, 0xA9)
GRADIENT_BOTTOM = (0x01, 0xC8, 0x86)

# 各密度下的启动图标边长（px）；48dp 基准，加上自适应图标前景留白余量。
DENSITIES = {
    "mipmap-mdpi": 48,
    "mipmap-hdpi": 72,
    "mipmap-xhdpi": 96,
    "mipmap-xxhdpi": 144,
    "mipmap-xxxhdpi": 192,
}


def _render(size: int, *, rounded: bool) -> Image.Image:
    scale = 8  # 超采样后缩小，得到干净边缘。
    canvas = size * scale

    # 垂直渐变底板（自上而下插值到参考包的两个渐变色）。
    base = Image.new("RGB", (1, canvas))
    pixels = base.load()
    for y in range(canvas):
        t = y / max(1, canvas - 1)
        pixels[0, y] = (
            round(GRADIENT_TOP[0] + (GRADIENT_BOTTOM[0] - GRADIENT_TOP[0]) * t),
            round(GRADIENT_TOP[1] + (GRADIENT_BOTTOM[1] - GRADIENT_TOP[1]) * t),
            round(GRADIENT_TOP[2] + (GRADIENT_BOTTOM[2] - GRADIENT_TOP[2]) * t),
        )
    plate = base.resize((canvas, canvas), Image.NEAREST).convert("RGBA")

    # 圆形或圆角方形遮罩。
    mask = Image.new("L", (canvas, canvas), 0)
    draw = ImageDraw.Draw(mask)
    if rounded:
        draw.ellipse((0, 0, canvas - 1, canvas - 1), fill=255)
    else:
        radius = round(canvas * 0.22)
        draw.rounded_rectangle((0, 0, canvas - 1, canvas - 1), radius=radius, fill=255)

    icon = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    icon.paste(plate, (0, 0), mask)

    # 提示符：折线 ">"。坐标按画布比例给出，保证各密度一致。
    glyph = ImageDraw.Draw(icon)
    stroke = max(1, round(canvas * 0.075))
    left = canvas * 0.28
    mid_y = canvas * 0.50
    tip_x = canvas * 0.50
    top_y = canvas * 0.32
    bottom_y = canvas * 0.68
    glyph.line((left, top_y, tip_x, mid_y), fill=(255, 255, 255, 255), width=stroke, joint="curve")
    glyph.line((left, bottom_y, tip_x, mid_y), fill=(255, 255, 255, 255), width=stroke, joint="curve")

    # 光标块：与提示符底部对齐。
    cursor = ImageDraw.Draw(icon)
    cursor.rounded_rectangle(
        (canvas * 0.58, canvas * 0.56, canvas * 0.76, canvas * 0.685),
        radius=canvas * 0.03,
        fill=(255, 255, 255, 255),
    )

    return icon.resize((size, size), Image.LANCZOS)


def main() -> None:
    written: list[str] = []
    for folder, size in DENSITIES.items():
        target = RES / folder
        target.mkdir(parents=True, exist_ok=True)
        for name, rounded in (("ic_launcher.png", False), ("ic_launcher_round.png", True)):
            out = target / name
            _render(size, rounded=rounded).save(out)
            written.append(str(out.relative_to(ROOT)))
    for item in written:
        print(item)


if __name__ == "__main__":
    main()
