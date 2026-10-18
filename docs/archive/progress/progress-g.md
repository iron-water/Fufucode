# 轨道 G 进度

## 2026-10-01 返工交付复核

本次核对独立 G worktree 已存在的完整返工交付，没有覆盖重画或重复生成。重新读取设计研究、资产清单和参考来源，查看三列总览、UniGetUI 实际界面截图 atlas、新会话四档双主题四态对比，并核对参考源码的 caption 状态与 Empty_inbox 绑定。实际重新运行 `python assets/verify_assets.py`，退出码 0、PASS：441 SVG、576 PNG、1 ICO、32 张对比图，错误列表为空。证据更新于 `assets/review/verification.json`。

交付位置为 `C:\Users\21894\.codex\worktrees\track-g-assets\Fufucode`；`C:\Projects\Fufucode` 中的旧资产没有同步覆盖。本次未执行任何 Git 操作；仅更新本进度文件与资产验证记录。用户审美认可、D/F 实际接线及安装器显示仍未验证，具体对照边界见下节。

## 2026-09-30 审美返工：文件已交付，视觉与接线验收待完成

状态：待外部条件。返工与资产文件验证已完成；“达到 UniGetUI 水准”的审美裁决仍由用户作出，D/F 接线后的真实效果未验证。本轮 **未执行任何 Git 操作**，只改 `assets/` 与本文件，改动留在 `C:\Users\21894\.codex\worktrees\track-g-assets\Fufucode`。下方历史段落描述旧批次，以本节和新清单为当前交付依据；未改 `docs/research/design-notes-g.md`。

### 盘点与依据

- 上轮实际落盘为 25 SVG、55 PNG、1 ICO，共 81 个文件，原样留档于 `assets/review/previous/`，返工前哈希见 `assets/review/inventory-before.json`。十个 action 图标当时只有生成脚本中的路径定义，目录和 PNG 并未落盘，本轮已补齐。
- 已读 `../../AI_DEVELOPMENT_PLAN.md`、`../../research/UNIGETUI_DESIGN_STUDY.md`，查看参考仓库的实际深/浅界面截图、标题栏、任务控制、品牌、空集合插画及安装器素材；同时核对 `MainWindow.axaml` caption 状态、Fluent 令牌、空态绑定与 `.iss` 引用。具体参考文件和哈希见 `assets/review/reference-sources.json`。
- 小书匠对话/电脑/包裹 emoji SVG 的格式和色彩已查看，未把其彩色 emoji 路径混入本套生产图形。

### 返工结果与消费位置

| 资产 | 本轮改动 | 交付与建议接线 |
|---|---|---|
| 品牌与小尺寸标识 | 重画为有折面层次的代码纸页；缩小版简化为清楚的 `>_` 剪影，移除重描边外框 | `icon/icon.svg`、`icon/icon-small.svg`、八层 `icon/fufucode.ico` → D 的应用/窗口/任务栏图标与 F 安装器 |
| 十个动作图标 | 中性线性几何；每档独立校准坐标，16/20/24/32 的笔画约 1/1.25/1.5/2 px；扩大/缩小用两组箭头减少小尺寸拥挤 | `actions/{name}-{size}.svg/.png`；完整双主题四态在 `actions/{theme}/` → D 的会话窗与操作按钮 |
| 四个标题栏按钮 | 统一 1 px caption 线重、视觉居中；补深色与禁用，悬停/按下有整槽状态底板；关闭保留系统红底语义 | `titlebar/{theme}/{control}-{state}.svg/.png` 及 2x/3x → D；原浅色文件名保留兼容 |
| 对话、扩展空态 | 从“大色块＋粗轮廓”改为中性纸面/轻透视模块；低饱和蓝灰只作局部强调，浅深主题独立校准 | `empty/conversation-{theme}`、`extensions-{theme}`，SVG + 1x/2x/3x PNG → D 对话与扩展空态 |
| 中断与错误 | 将旧“错误/中断”共用图拆开：中断为中性暂停标记，失败才用低饱和红标记 | `empty/interrupted-{theme}` / `error-{theme}` → D 按实际终态选择 |
| 安装器侧图、小图、完成图 | 移除整块深色背景与波纹；使用透明品牌构图与留白，补明暗及 1x/2x/3x | `installer/wizard-side-{theme}` / `wizard-small-{theme}` / `complete-{theme}` → F 对应 Inno 槽位；旧路径保留浅色别名 |

当前生产清单为 **441 SVG、576 PNG、1 ICO**（包含尺寸/状态/主题/兼容别名），实际源与尺寸见 [manifest](../../../assets/manifest.md) 和 [逐文件 exports](../../../assets/exports.json)。生产图形均为 Codex 原创 SVG 代码绘制，PNG 由 resvg 直接渲染；来源说明在 manifest。参考内容只在 review 对比中，当前参考克隆根 LICENSE 实际为 MIT，原文及说明保留于 [REFERENCE-NOTICE](../../../assets/review/REFERENCE-NOTICE.md)。

### 实际验证

已实际运行：

```text
python assets/generate.py
python assets/review.py --reference C:/Projects/Fufucode/reference/UniGetUI
python assets/verify_assets.py
```

- 三条命令退出码均为 0，验证结果为 PASS；本轮改用 resvg，PNG 已真实生成，之前的 Cairo DLL 缺失不再阻塞本套导出。
- 全部生产 SVG 可解析，PNG 可解码、尺寸符合逐文件清单；生产 SVG 没有内嵌位图、文字或外部字体依赖。十个动作图标四档均有完整双主题四态，原生轮廓留有透明边界，无边缘裁切。
- 从原生 Pause PNG 测得单笔画覆盖宽度为 1.000 / 1.255 / 1.502 / 2.000 px，符合四档统一线重。ICO 的 16/20/24/32/48/64/128/256 八帧与各原生 PNG 逐像素一致。
- 在本套校准背景下，可用态前景对比最低 6.18:1；hover→pressed 的底板对比递进：浅色 1.114→1.272，深色 1.369→1.726。按下态前景沿 Fluent 次级层次降低，禁用态保留轮廓并减弱前景，不只替换色相。
- 81 个旧文件留档哈希保持一致；32 张逐项三列对比均已生成 PNG 与审阅 SVG。已人工查看原生小图与各类明暗资产的渲染结果，不把文件检查称作审美验收。

完整证据：[verification.json](../../../assets/review/verification.json)。未跑 .NET 构建、App UI 或安装器验收；本轮没有接线改动。

### 逐项旧版 / 返工版 / UniGetUI 对照

[打开离线对照图库](../../../assets/review/index.html) 可筛选资产并切换动作图标的原生尺寸、主题和状态。图库全部引用实际导出的 PNG。下面每张图的列序均为旧版、返工版、UniGetUI 参考，32 项均有单独对照。

![重点素材三列总览](../../../assets/review/compare/overview.png)

<details>
<summary>品牌与原生小尺寸（4 项）</summary>

![主标识三列对比](../../../assets/review/compare/brand-main.png)
![16/20/24/32 小标识三列对比](../../../assets/review/compare/brand-small.png)
![浅色品牌留白三列对比](../../../assets/review/compare/brand-preview-light.png)
![深色品牌留白三列对比](../../../assets/review/compare/brand-preview-dark.png)

</details>

<details>
<summary>对话、扩展、中断、错误的浅深空态（8 项）</summary>

![对话浅色三列对比](../../../assets/review/compare/empty-conversation-light.png)
![对话深色三列对比](../../../assets/review/compare/empty-conversation-dark.png)
![扩展浅色三列对比](../../../assets/review/compare/empty-extensions-light.png)
![扩展深色三列对比](../../../assets/review/compare/empty-extensions-dark.png)
![中断浅色三列对比](../../../assets/review/compare/empty-interrupted-light.png)
![中断深色三列对比](../../../assets/review/compare/empty-interrupted-dark.png)
![错误浅色三列对比](../../../assets/review/compare/empty-error-light.png)
![错误深色三列对比](../../../assets/review/compare/empty-error-dark.png)

</details>

<details>
<summary>动作图标四档、双主题、四态（10 项）</summary>

![新会话三列对比](../../../assets/review/compare/action-new-session.png)
![扩大三列对比](../../../assets/review/compare/action-expand.png)
![缩小三列对比](../../../assets/review/compare/action-collapse.png)
![暂停三列对比](../../../assets/review/compare/action-pause.png)
![继续三列对比](../../../assets/review/compare/action-resume.png)
![停止三列对比](../../../assets/review/compare/action-stop.png)
![最小化动作三列对比](../../../assets/review/compare/action-window-minimize.png)
![最大化动作三列对比](../../../assets/review/compare/action-window-maximize.png)
![还原动作三列对比](../../../assets/review/compare/action-window-restore.png)
![关闭动作三列对比](../../../assets/review/compare/action-window-close.png)

</details>

<details>
<summary>标题栏双主题四态（4 项）</summary>

![最小化标题栏三列对比](../../../assets/review/compare/titlebar-minimize.png)
![最大化标题栏三列对比](../../../assets/review/compare/titlebar-maximize.png)
![还原标题栏三列对比](../../../assets/review/compare/titlebar-restore.png)
![关闭标题栏三列对比](../../../assets/review/compare/titlebar-close.png)

</details>

<details>
<summary>安装器浅深侧图、小图、完成图（6 项）</summary>

![向导侧图浅色三列对比](../../../assets/review/compare/installer-wizard-side-light.png)
![向导侧图深色三列对比](../../../assets/review/compare/installer-wizard-side-dark.png)
![向导小图浅色三列对比](../../../assets/review/compare/installer-wizard-small-light.png)
![向导小图深色三列对比](../../../assets/review/compare/installer-wizard-small-dark.png)
![完成图浅色三列对比](../../../assets/review/compare/installer-complete-light.png)
![完成图深色三列对比](../../../assets/review/compare/installer-complete-dark.png)

</details>

### 无法声称完成的对照与外部验收

- UniGetUI 不存在会话页或对话/扩展空态、独立 Pause/Resume 图标；相应板给的是工具栏 / 任务卡片 / 空集合槽位的实际参考与功能类比，不宣称逐页同位实测。alert_laptop 只查到素材文件，未查到现快照绑定。
- 参考仓库没有标题栏还原态以及 hover/pressed/disabled 的运行截图；这些部分只能核对源码与令牌。参考 caption 槽位为 46×44，本轮兼容上轮 Fufucode 的 46×32 槽位，布局调整需 D 执行。
- UniGetUI 安装完成页复用向导侧图，没有独立 256×256 完成页图形。新版 complete 是原创补充，F 接线后的 Inno 透明通道、缩放和裁切仍待真实安装器验证。
- 用户对审美的认可、D 接线后的明暗/交互状态，以及 F 安装器实际效果均待外部验收。不能将上述文件 PASS 写成“审美已通过”。本轮生产文件只从 manifest/exports 消费，`review/` 不进入产品包。

---

以下为旧批次历史记录。

## 2026-09-29 第一批：品牌与空态资产

状态：进行中。已在独立 `track-g` worktree 合并 main（当时已最新）。研究记录见 `../../research/design-notes-g.md`，文件与尺寸见 `assets/manifest.md`。

交付与建议接线：

- `assets/icon/fufucode.ico` → D 接应用窗口/任务栏与 csproj，F 接 Inno `SetupIconFile`；`icon/icon.svg` 为主源，`icon/icon-small.svg` 为小尺寸源。
- `assets/empty/conversation-{light,dark}.png` → D 接对话空态图片；按主题切换，高清屏可选 `@2x`。
- `assets/empty/extensions-{light,dark}.png` → D 接扩展管理空态；图片外由 UI 写本地化文案。
- `assets/empty/interrupted-{light,dark}.png` → D 接错误或中断状态说明区，避免把用户主动停止视作警报。
- `assets/installer/wizard-side.png` → F 接 `WizardImageFile`；`wizard-small.png` → `WizardSmallImageFile`；`complete.png` → 安装完成页图形组件。

验证：`python assets/generate.py` 成功；XML 解析 13 个 SVG、Pillow 解码 19 个 PNG；Pillow 读取 ICO，确认 16/24/32/48/64/128/256 七层；人工查看主图标、两幅空态与安装器侧图的渲染结果。未验证：mock 模式的实看（当前桌面自动化入口未能用于原生应用）、D 接线后的实际页面、安装器中图像裁切、用户对视觉方向的认可。

下一步：用 mock 界面核对图片在真实主题和空态容器内的视觉比例；D/F 接线后按实看反馈调整。

## 2026-09-30 本轮派单状态

状态：受阻。当前 worktree 中的 `docs/archive/assignments/track-g-assets.md` 仍是 2026-09-29 旧任务书，没有“本轮派单 2026-09-30”内容；在仓库可见文件中检索 `本轮派单` 和 `2026-09-30` 也没有命中。按 `AGENTS.md`，开工前须以 `main` 同步任务书；本轮用户明确禁止执行 Git merge，因此无法安全确认并取得本轮派单。未修改资产，也未运行构建或验证命令。待外部驱动器同步任务书后继续。

## 2026-09-30 本轮派单：会话标题栏动作图标

状态：资产已生成并登记，待外部驱动器运行验证与 D 轨接线。新增 `assets/actions/` 下 10 个原创 SVG 动作图标及每个图标 16/20/24/32 px PNG：新建会话、展开、收起、暂停、继续、停止、最小化、最大化、还原、关闭。`assets/generate.py` 增加对应图元定义与导出；`assets/manifest.md` 登记所有图标及用途；`docs/research/design-notes-g.md` 记录尺寸、色彩、使用约束与原创来源。

未运行构建、测试或资产生成脚本；PNG 文件需由外部驱动器执行 `python assets/generate.py` 生成并验证尺寸/渲染。D 轨接线建议将 `actions/*.png` 用作会话及窗口标题栏按钮图像，提供本地化 accessible name；运行状态变化时在 pause/resume 间切换。旧的 2026-09-30“派单状态受阻”记录已被本条实际派单结果取代。

补充状态（同日）：刚才记录中的“资产已生成”仅指图标源与导出逻辑已写入；本环境实际未生成 PNG。执行 `python assets/generate.py` 在导入 CairoSVG 时因缺少 Cairo 原生库失败（找不到 `cairo-2` / `libcairo-2.dll`）；未安装第三方依赖。D/F 可先评审 SVG 源，PNG 需在具备 Cairo 运行库的外部驱动器上执行 `python assets/generate.py` 后再接线。此前执行的 `python -m py_compile` 仅做了 Python 语法编译，未运行任何 .NET 构建或测试。

## 2026-09-30 标题栏按钮状态图标

完成 `assets/titlebar/` 下 12 个原创 SVG（最小化/最大化/还原/关闭 × 正常/悬停/按下）以及每个图标的 1x/2x/3x PNG 导出定义；本轮共落地 12 个 SVG 与 36 个 PNG。`assets/generate.py` 新增可重复的导出逻辑，`assets/manifest.md` 登记全部源文件和导出路径，`docs/research/design-notes-g.md` 记录尺寸、颜色与接入约束。

建议 D 在 Avalonia 自绘 TitleBar 的最小化、最大化/还原、关闭按钮中按 pointer 状态选择三态图，最大化时将最大化图标换成还原图标；命中区域应大于 46×32 DIP 素材槽位，并补齐本地化 accessible name、键盘操作及系统主题适配。关闭按钮悬停/按下显示红底。未改 App 或 Inno Setup 文件。

已用 Pillow 以 4x 超采样导出 PNG；未运行 .NET 构建/测试。当前环境过去运行总生成器时遇到 Cairo 原生库缺失，因此本轮只确认新增状态资产文件由本地 Pillow 生成，完整 `assets/generate.py` 总流程、D 侧实际显示、D 集成后的窗口行为和视觉验收仍待外部驱动器/消费轨道验证。
