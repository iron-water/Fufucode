# S1 皮肤样稿：Fluent 基线 / Metro / Material 3（HTML 原型）

**这不是 Avalonia 生产实现。** 本目录是可直接打开、可交互的视觉样稿，用于在写生产主题资源前先确定风格；
生产接线建议见 [`TOKENS-MAPPING.md`](TOKENS-MAPPING.md)，由 S2/Codex 实施。样稿不调用模型、不读项目文件、不执行命令、不访问网络、不写本地存储。

## 打开方式

直接双击 `index.html`（无需服务器、无需联网）。`compare.html` 是同一份标记的三皮肤对照页。

URL 参数决定初始画面，便于复现与截图：

```text
index.html?skin=fluent|metro|material3&theme=light|dark&scale=1|1.25|1.5&view=shell|settings|states&chat=filled|empty|error|loading
```

页面内也有等价控件：顶栏的皮肤分段、明暗按钮、缩放下拉、`工作台 / 设置 / 状态` 视图切换。

## 目录

| 文件 | 作用 |
|---|---|
| `index.html` | 五区外壳 + 独立设置目的地 + 状态画廊；三皮肤共用这一份标记 |
| `compare.html` | 三皮肤 × 亮暗同屏对照 + 令牌差异表 |
| `css/tokens.css` | 三皮肤 × 明暗令牌，变量名与生产 `App*` 语义键、`ControlCornerRadius` 一一对应 |
| `css/base.css` | 结构层：五区布局、控件骨架、状态画廊（不含颜色/圆角常量） |
| `css/skin-fluent.css` / `skin-metro.css` / `skin-material3.css` | 各皮肤的形态差异（圆角、描边、阴影、页签、输入区、焦点环） |
| `js/app.js` | 皮肤/主题/缩放/视图切换与画廊渲染；无网络与存储副作用 |
| `shots/` | headless Chrome 实际渲染截图（见下） |
| `TOKENS-MAPPING.md` | 令牌映射表与生产接入建议（含现状事实与未验证项） |

## 覆盖范围

- **五区外壳**：顶栏（品牌/项目/全局入口）、左栏（图标轨 + 会话侧栏）、中央（会话头 + 时间线 + composer）、右栏（页签化工作视图 + diff）、底栏（终端）、状态栏。
- **独立设置目的地**：Windows 设置式分类导航 + 卡片行（含已保存 / 待重启 / 失败回退三种反馈，以及一行“未接线”禁用说明）。
- **亮暗**：三皮肤 × 浅色/深色全量取值。
- **缩放**：100% / 125% / 150%（样稿用 CSS `zoom` 近似 DPI 缩放）。
- **控件状态**：默认、悬停、键盘焦点、按下、选中、禁用、错误、空、加载中——`view=states` 画廊逐项展示。
- **会话状态变体**：`chat=empty|error|loading` 直接在阅读列顶部呈现空会话、加载失败、加载中。

## 截图（headless Chrome 实测渲染，1440 宽基准）

| 文件 | 内容 |
|---|---|
| `shots/fluent-light-shell.png`、`fluent-dark-shell.png` | Fluent 基线五区外壳（亮/暗） |
| `shots/metro-light-shell.png`、`metro-dark-shell.png` | Metro 对比（亮/暗） |
| `shots/material3-light-shell.png`、`material3-dark-shell.png` | Material 3 对比（亮/暗） |
| `shots/*-light-settings.png` | 三皮肤设置目的地 |
| `shots/*-light-states.png` | 三皮肤状态画廊（1440×1560 长图） |
| `shots/fluent-light-shell-125.png`、`material3-light-shell-150.png` | 缩放 125% / 150% |
| `shots/fluent-light-chat-empty.png`、`metro-light-chat-error.png`、`fluent-dark-chat-loading.png` | 空 / 错误 / 加载中变体 |
| `shots/compare-light.png` | 三皮肤 × 亮暗对照页 |

渲染命令（工作树根目录执行，Chrome 154 headless）：

```text
chrome --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 \
  --allow-file-access-from-files --window-size=1440,900 --virtual-time-budget=5000 \
  --screenshot=shots/<name>.png "file:///<worktree>/docs/frontend-prototype/skins/index.html?skin=<skin>&theme=<theme>&view=<view>"
```

## 限制（如实说明）

- 页面里所有数据都是示例：不会调用模型、不读取真实项目文件、不执行命令、不生成分享链接。
- 缩放用 CSS `zoom` 近似，**不等于** Avalonia 原生 DPI 缩放；原生指针/命中区/IME 与真实窗口尺寸必须在 V 任务用真机复核。
- 悬停/按下/焦点在画廊中以 `.is-hover` / `.is-pressed` / `.is-focus` 静态展示；外壳里的同名状态可真实交互（鼠标 + 键盘 Tab）。
- 浮动标签（Material 3 输入框）与状态层按规范以 CSS 近似，不代表 Avalonia 模板能力已存在。
- 未实现 Aero / Liquid Glass（用户意见列为后续/暂缓）。
- 样式与结构沿用现有原型 `../index.html`、`../workbench.css` 的五区尺寸（左 232 / 右 320 / 底 240 / 状态 26），未改生产 `Styles`、`MainWindow`、设置页与辅助栏。
