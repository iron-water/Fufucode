# 视觉与交互统一 第一轮（2026-10-07）

依据：`docs/design/visual-unification-brief.md`。本轮按用户要求少做自测，一次写完。

## 改动范围

- **令牌**（`Styles/FufucodeTheme.axaml`）新增以下语义键，明暗主题各一套：
  - `AppChromeBackground`、`AppDockBackground`、`AppDockHeaderBackground`、`AppTabSelectedBackground`：外框与停靠区的表面层级。
  - `AppScrimBackground`：抽屉遮罩。
  - `AppTerminalBackground`、`AppTerminalForeground`：终端固定深色，与 `TerminalBuffer` 的 ANSI 默认色一致。
- **共享组件**（`Styles/WorkbenchStyles.axaml`）：
  - 外框与停靠：`chromeBar`、`dockHost`、`dockHeader`、`dockHeaderRule`。
  - 页签：`dockTab`。未选中的页签使用次级文字色；选中的页签使用主文字色加 2px 强调色指示条；关闭钮在悬停、聚焦或选中时显示。
  - 拖放、空状态与分隔：`dockDropTarget`、`dockEmpty`、`dockSplitter`。分隔条静止时为 1px 分隔线，悬停时显示强调色。
  - 工具按钮与浮层：`toolButton`（28px 命中区，`.active` 表示开启态）、`toast`、`overlayPanel`。
  - 会话元素：`meta`、`metaName`、`turnSummary`、`toolCard`。
  - 终端与色块：`terminalSurface`、`accentSwatch`。
- **主窗口**：标题栏与状态栏原用卡片底，改为 chrome 层同底。三个停靠区共用 `dockHost`。三条分隔条原用 `AppInnerWell` 粗条，改为 `dockSplitter`。遮罩的硬编码 `#33000000` 改为令牌。通知浮层改用 `toast`，原文本"×"改为带提示的关闭图标。
- **停靠区**（`DockRegionView`）：
  - 拖放高亮改用令牌。原实现用 `SystemAccentColor` 现取，失败时回退 `DodgerBlue`。
  - 页签关闭图标从 `DismissCircle` 改为 `Dismiss`。
  - 空状态改为说明下一步操作。
  - "隐藏此区域"的提示注明页面与进程保留。
- **终端**：
  - 去除三处硬编码颜色。
  - 文本字形 `›_` 改为 Terminal 图标。
  - 关闭钮改为 `RecordStop` 图标，提示写明会终止进程，与"隐藏底区"区分。
- **中央会话**：
  - 时间和名称降为 `meta` 和 `metaName` 层级。
  - 用户头像原为白色图标叠在浅色底上，对比度不足，现改为强调色文字。助手头像与用户头像统一为浅底加强调色文字。
  - 工具卡改为内凹表面，工具名用等宽字体，输出可选中。
  - "本轮变更"和"生成产物"原为浮起卡片，改为参与正常布局的左侧竖线条目。
  - 引用草稿用左侧强调色竖线标明来源，移除钮带名称。
- **图标语义**：
  - 意见模式与反馈原与"会话"共用 Chat 图标，改为 Lightbulb。
  - "明暗主题"原用 Lightbulb，改为 Color。
  - 思考块与"思考等级"改为 Brain。
  - "模型"行改为 Sparkle。
  - trace 原与"分叉"共用 Branch 图标，改为 History。
- **其他**：意见模式开启态不再在代码中直接设置背景，改用 `.active` 类。强调色色块不再固定白色描边，改用 `accentSwatch.selected`（主文字色描边，明暗主题都可见）。命令面板、通知中心、用量详情三个浮层改用 `overlayPanel`。

## 保护项

以下内容未改动：
- 页面 ID、消息 AutomationId（`MSG-*`）、`dock-tab-*`、动作 ID。
- 配置 JSON 结构、Pi RPC、会话与进程归属逻辑。
- `App*` 旧键全部保留。

## 验证

| 项 | 状态 |
|---|---|
| `dotnet build src/Fufucode.App` | 通过（0 警告 0 错误） |
| 自测套件、定向测试 | 未运行（按用户要求减少自测） |
| 明暗 × 宽窄截图 | 未验证 |
| 与 UniGetUI 对照视觉验收 | 未验证，待用户确认 |

## 已知风险与未做项

- `WorkbenchTabsSelfTest` 按提示文字"打开功能页"查找按钮，该提示未改。其余自测依赖的名称已检查过，均未改动。
- 设置页（`WorkbenchCustomizationView`，代码构建）的整体排版与 UniGetUI 设置页的卡片行形态，本轮未重做。
- 消息"更多"菜单、会话头操作仍用 ChevronDown 图标，尚未统一为专用"更多"图标（图标库暂无 MoreHorizontal）。
