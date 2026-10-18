# Fufucode 资产清单 · 2026-09-30 返工版

本轮替换上轮图形。生产文件为 **441 SVG、576 PNG、1 ICO**，共 1,018 个（多尺寸、主题、状态与旧路径别名均计入）；实际逐文件清单及尺寸见 [exports.json](exports.json)。生成入口为 [generate.py](generate.py)，依赖固定于 [requirements.txt](requirements.txt)。SVG 是绘制源，PNG 直接由 resvg 渲染；小尺寸动作图标有独立坐标与笔画校准，ICO 使用各原生 PNG 帧。

## 生产资产

| 路径 / 族 | SVG 源与 PNG 分辨率 | 用途 / 消费方 | 来源 |
|---|---|---|---|
| `icon/icon.svg` / `icon/icon-{16,20,24,32,48,64,128,256}.png` | 96×96 主源；PNG 为列出的原生方形尺寸 | 应用标识 / D、F | 原创折页代码图形 |
| `icon/icon-small.svg` / `icon/icon-small-32.png` | 32×32 光学校准源；用于 16/20/24/32 PNG 与 ICO 小帧 | 窗口与任务栏小图 / D | 原创；比主源减少背面与渐变细节 |
| `icon/fufucode.ico` | 16/20/24/32/48/64/128/256，八个原生 PNG 帧 | Windows 应用与安装器 / D、F | 上述原始 SVG 派生 |
| `icon/icon-{light,dark}.svg` / `icon/icon-{light,dark}-{96,192,288}.png` | 96 矢量；96/192/288 | 明暗背景品牌图 / D | 原创；深色版单独校准明度 |
| `icon/preview-{light,dark}.svg` / `.png` / `@2x.png` / `@3x.png` | 320×200 / 640×400 / 960×600 | 品牌主题预览 / 评审 | 原创，预览含中性背景 |
| `empty/conversation-{light,dark}.svg` / `.png` / `@2x.png` / `@3x.png` | 320×200 / 640×400 / 960×600 | 对话空态 / D | 原创纸面与终端对话构图 |
| `empty/extensions-{light,dark}.svg` / 同分辨率 PNG | 同上 | 扩展空态 / D | 原创轻透视模块构图 |
| `empty/interrupted-{light,dark}.svg` / 同分辨率 PNG | 同上 | 用户停止 / 中断 / D | 原创，暂停符号与中性蓝灰 |
| `empty/error-{light,dark}.svg` / 同分辨率 PNG | 同上 | 实际失败 / D | 从原“错误/中断”共用图拆出；灰阶纸面、低饱和红标记 |
| `actions/{name}.svg` | 24×24 中性浅色常规源 | 动作图标旧路径别名 / D | 原创线性几何 |
| `actions/{name}-{16,20,24,32}.svg` / 对应 `.png` | 各原生方形尺寸 | 动作图标，常规浅色 / D | 原创；笔画 1/1.25/1.5/2 px |
| `actions/{light,dark}/{name}-{normal,hover,pressed,disabled}-{16,20,24,32}.svg` / 对应 `.png` | 各原生尺寸，双主题四态 | 图标按钮四态 / D | 原创；hover/pressed 自带底板，disabled 保留图形并降对比 |
| `titlebar/{light,dark}/{minimize,maximize,restore,close}-{normal,hover,pressed,disabled}.svg` / `.png` / `@2x.png` / `@3x.png` | 46×32 / 92×64 / 138×96 | 自绘标题栏 / D | 原创 1 px caption 几何；hover/pressed 自带整槽底板 |
| `titlebar/{control}-{state}.svg` / 同分辨率 PNG | 同上，浅色别名 | 兼容上轮标题栏文件名 / D | 同一源派生 |
| `installer/wizard-side-{light,dark}.svg` / `.png` / `@2x.png` / `@3x.png` | 330×630 / 660×1260 / 990×1890 | Inno `WizardImageFile` / F | 原创透明品牌图，留白对齐参考安装器 |
| `installer/wizard-small-{light,dark}.svg` / 同分辨率 PNG | 256×256 / 512×512 / 768×768 | Inno `WizardSmallImageFile` / F | 原创品牌图 |
| `installer/complete-{light,dark}.svg` / 同分辨率 PNG | 同上 | 自定义完成页图形 / F | 原创品牌与低饱和完成标记 |
| `installer/{wizard-side,wizard-small,complete}.svg` / PNG 多分辨率 | 对应浅色版本 | 兼容旧安装器路径 / F | 同一源派生 |

动作 `name` 共十个：`new-session`、`expand`、`collapse`、`pause`、`resume`、`stop`、`window-minimize`、`window-maximize`、`window-restore`、`window-close`。expand/collapse 表达会话窗扩大/缩小，不替代列表折叠的上下 chevron。

## 消费说明

- 普通背景为浅色 `#F3F3F3` / 深色 `#202020`。空态与安装器 PNG 保留透明通道；实际主题背景由 UI 或 Inno 提供。
- 空态自身有留白，推荐按 320×200 DIP 的源比例显示，高清屏选 2x/3x，不再额外画大块彩色背景。说明文字与按钮放在图形外，由消费方本地化。
- 图标选匹配的原生尺寸与主题。完整状态文件已带背景层，不应再把整张图统一染色；若控件自行画底板，则用常规单色 SVG，由控件分别处理前景和底板。
- 原 caption 槽位 46×32 保持兼容；UniGetUI 主标题栏源码为 46×44，本轮只对照其 glyph 与状态语言，未修改 Fufucode 标题栏布局。
- 只消费本表 / `exports.json` 列出的路径。**`review/` 为旧版留档、对比和验证记录，不进入应用或安装包。**

## 来源与审阅资料

生产路径的图形全部由 Codex 用 SVG 代码重新绘制，设计要点为“Fluent 中性线性几何、光学小尺寸、原生状态层次、轻透视纸面、克制蓝灰、独立明暗校准”。未采用小书匠 emoji 路径或 UniGetUI 图形路径，未引入第三方字体文件。

用户要求的参考图只存在于 `review/compare/` 与参考 atlas。其来源、边界与许可说明见 [review/REFERENCE-NOTICE.md](review/REFERENCE-NOTICE.md)。当前参考克隆根 `LICENSE` 实际为 MIT，原文已保留；这项观察不改变生产图形原创的约束。对比板的 SVG 是内嵌 PNG 的可移植审阅容器；生产 SVG 仍为原生矢量图形。
