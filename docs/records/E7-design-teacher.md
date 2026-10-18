# E7-DESIGN 有限设计教师：交付记录

任务：`docs/tasks/hourly-E7-design-teacher.md`｜难度 L4｜推荐主模型 `group/auto-claude-opus-5-5`（设计教师，非测试 lead）｜日期 2026-10-13。

## 1. 开工身份

| 项 | 值 |
|---|---|
| 工作树 | `C:\Users\mio\.codex\worktrees\566d\Fufucode` |
| HEAD/分支 | detached HEAD |
| 起点 | `58a06e3cb92c95c515aa6af4b9c7662d6318c603`（任务书提交） |
| 继承改动 | 无（`git status` 干净） |
| 风格意见输入 | 用户未跟踪 `docs/人类意见/前端风格.md` 本树不存在 → 使用已提交快照 `docs/tasks/E7-style-brief.md`（只读） |
| 已读 | AGENTS、任务书、E7-style-brief、frontend-design-guideline、floating-workbench、agent-event-protocol、S1-skins 记录、skins/README 与 TOKENS-MAPPING、S1 tokens.css；生产 `App.axaml`、`FufucodeTheme.axaml`、`WorkbenchStyles.axaml`、`MicaOverrides.axaml`、`MainWindow.axaml`、`MainWindow.Chrome.cs`、`CaptionAssetButton.cs`、`DockRegionView.cs`、`MainWindow.DockFloating.cs`（只读） |
| 并行任务边界 | E7-ENGINE（Metro/M3 真实资源模块）不碰；本任务不写 src/ |

## 2. 产物

| 文件 | 说明 |
|---|---|
| `docs/frontend-prototype/skins/teacher/index.html`、`css/teacher-tokens.css`、`css/teacher.css`、`js/teacher.js` | 可交互教师 HTML：五区、Windows 设置式目的地、可浮动页签/整栏、通知/用量功能页、默认关闭的极扁状态栏+独立开关；Fluent⇄Aero、亮暗、active/inactive、实色/有模糊、状态画廊、菜单、对话框、800 窄窗规则 |
| `teacher/avalonia/AeroCaption.axaml` | Aero 标题栏/窗口按钮画刷（亮/暗）+ `AeroCaptionButtonTheme` ControlTheme |
| `teacher/avalonia/AeroCaption.Styles.axaml` | caption 容器、inactive、glass 作用域样式 |
| `teacher/avalonia/harness/**`、`harness-run.txt` | 最小无头工装（不进 sln）与实际输出 |
| `teacher/shots/*.png`、`html-shots-log.txt` | 13 张 HTML 截图 + 1 张 Avalonia 真实帧 |
| `teacher/README.md` | 打开/复现/限制 |
| `docs/design/skin-teacher-reference.md` | 参考书：理念→层级→token→组件状态→示范→Avalonia 落点→降级→Luna 策略→4 个低成本任务包→验收清单 |

未改：任何 `src/`、`App.axaml`、Styles、MainWindow、Settings、WorkbenchAppearance、共享计划/进度/决策/失败登记、S1 原目录与 shots、主仓库。

## 3. 验证（实际运行）

| 验证 | 命令 | 结果 |
|---|---|---|
| AXAML 编译 | `dotnet build docs/frontend-prototype/skins/teacher/avalonia/harness/AeroCaptionHarness.csproj -c Release --artifacts-path %TEMP%\e7-aero-harness`（TreatWarningsAsErrors） | exit 0，0 警告 0 错误 |
| 无头控件断言 | `%TEMP%\e7-aero-harness\bin\AeroCaptionHarness\release\AeroCaptionHarness.exe docs/frontend-prototype/skins/teacher/shots` | exit 0，`RESULT pass=45 fail=0`（`harness-run.txt`） |
| Avalonia 渲染帧 | 同上 `CaptureRenderedFrame` | `avalonia-aero-caption-900x398.png`，已目视 |
| HTML 渲染 | Chrome headless，DPR 1，参数见 `html-shots-log.txt` | 13 张写出，已逐张目视 |
| 对比度 | Python WCAG 公式 | 结果表在参考书 §3.3 |

## 4. 红例与修正

1. **工装 pressed 伪类**：首轮在挂视觉树前写 `:pressed`，实测挂树后只剩 `:pointerover`，4 条 pressed 断言 FAIL（pass=36 fail=4）。修正为挂树后强制，并新增真实无头指针输入（hover→pressed→Click→板下点击→离开复原）5 条独立断言。没有放宽断言。
2. **Aero 主按钮对比度**：白字蓝渐变实测 4.12（亮）/4.62（暗），渐变上段更低。改为 Vista 默认按钮形态（普通渐变 + 青色内环 + 深字），11.06/9.59。
3. **inactive 标题字**：`#5B6878` 4.23 → `#4F5B6A` 5.15（HTML 与 AXAML 同步）。
4. **HTML 层叠**：inactive 规则误作用到浮动窗口与对话框标题；限定到 `#win > .caption`。Aero 窗口按钮首版缩窄了 min/max 命中宽度，改回 46×32。

## 5. 未验证

- 生产接线：未做（不在允许范围）。
- 原生：真实 Windows 窗口、DWM/透明级别、125%/150% DPI、IME、真实键鼠、远程桌面、高对比度：全部未验证。
- HTML 其余组件（按钮/输入/页签/菜单/设置行/对话框/终端）的 Avalonia 版本：未写，交参考书 §9 包 B。
- 用户视觉确认：Aero 是否采用、Aero Night 暗色是否接受：未确认。
- 未委派任何子代理，未发起模型请求；未用 devin/swe-2（机械工具足够）。

## 6. 源控件映射

| 样板 | 生产控件/选择器 |
|---|---|
| `.caption` / `Border.aeroCaption` | `MainWindow.axaml` `CustomCaption`（`Border.chromeBar`） |
| `.capbtn` / `AeroCaptionButtonTheme` | `CaptionAssetButton`（46×32，`MainWindow.Chrome.cs` 处理 Click/拖拽/双击） |
| `.cap-slot` | `Button.captionSlot` |
| `.tab` | `Button.dockTab`（`DockRegionView`，页签行 40） |
| `.tool` | `Button.iconButton` / `.toolButton` |
| `.item` | `ListBoxItem` |
| `.row` | `Border.settingsRow`（64/52） |
| `.menu .mi` | `ContextMenu` / `MenuItem`（“浮动整栏”等） |
| `.terminal` | `TerminalDockView` 容器 |
| `.statusbar` | `CustomStatusBar`（默认关闭） |
| `.win.float` | `MainWindow.DockFloating.cs` 浮动 `Window` |

## 7. 交付卡

```text
交付卡
任务ID与软件：E7-DESIGN｜Codex 桌面执行会话（group/auto-claude-opus-5-5）
工作树、HEAD、起点：C:\Users\mio\.codex\worktrees\566d\Fufucode，detached，起点 58a06e3cb92c95c515aa6af4b9c7662d6318c603
本任务提交：ef3975f（HTML 样板）、9a05a4c（AXAML+工装）、cd389f6（截图+对比度修正）、ecf981e（参考书+README）、本记录提交
改动文件：仅 docs/frontend-prototype/skins/teacher/**、docs/design/skin-teacher-reference.md、docs/records/E7-design-teacher.md；无继承改动
实际验证：工装 build 0警告0错误；无头 45/45 通过；Avalonia 帧 1 张、HTML 截图 13 张已目视；对比度已计算
未运行：生产接线、原生窗口/DPI/IME/DWM、其余控件 AXAML、用户视觉确认
公共接线需求：E7-ENGINE/M 按参考书 §6.3 把 AeroCaption*.axaml 放入 Styles/Skins/Aero 并挂皮肤租约；新增 §3.2 标 * 的键；主窗口与浮动窗口同租约
状态：module_ready（教师样板待 M 审核；非生产皮肤、非用户视觉验收）
原会话是否仍在写：否
```
