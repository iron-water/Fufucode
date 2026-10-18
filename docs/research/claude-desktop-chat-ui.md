# Claude Code Desktop（claude.ai/code / Claude Desktop Code 页）主对话页视觉研究

> 日期：2026-10-17。对象：Claude Desktop App 内 "Code" 页签（与 claude.ai/code Web 同一渲染层），参考版本 v2.1.x、Desktop 1.2581+。
> 本文只研究**主对话页**（transcript + composer），不覆盖侧栏会话列表、diff 面板、终端面板。
> 与 `open-claude-ui-audit.md` 的关系：那篇来自 clean-room 逆向的**数值**（弹簧、羽化、气泡尺寸）仍然有效；本文聚焦**官方正品的呈现决策**（来自官方博客、文档、issue tracker、第三方截图分析），两者互补。

## 来源与可信度

- 官方公告：[Redesigning Claude Code on desktop for parallel agents](https://claude.com/blog/claude-code-desktop-redesign)（2026-04-14）、[Preview, review and merge](https://claude.com/resources/articles/preview-review-and-merge-with-claude-code)。
- 官方文档：code.claude.com/docs/en/desktop（Transcript view：Normal / Thinking / Verbose 三档；Ctrl+O 切换）。
- 实测/截图评论：VentureBeat 上手评、aicodingflow 指南、mejba.me 日用评、Tom's Guide web 版测评。
- 用户侧真实吐槽（最有价值的像素线索）：
  - anthropics/claude-code#48158 — 深色改版后"纯黑背景 + 饱和蓝用户气泡"，说明**用户气泡是 accent 蓝色填充**，且深色基底分层变平是公认退步。
  - anthropics/claude-code#59931（归入 #37584）— web 版 Write/Edit 工具卡**内联渲染完整红绿 diff、且无法默认折叠**，证实工具卡在 transcript 里是"富媒体块"而非一行摘要。
  - anthropics/claude-code#57060 — Desktop 上工具输出默认展开、▸ 折叠钮失灵的 bug 串，证实**工具卡有 ▸ 折叠箭头、折叠态是预期默认**。
  - anthropics/claude-code#99338 — AskUserQuestion 卡以 chevron 展开/折叠，悬浮在 composer 上方。
  - anthropics/claude-code#42780 — 终端用户羡慕"desktop/web UI 的可折叠块"，侧面确认桌面端工具调用是可折叠卡片。
- 同形态参照：Paddock（第三方 Claude Code 客户端）文档逐条描述工具卡的图标+名称+副标题+时长+状态 chip+点击展开结构，以及 "live bar above the message box"（仍在跑的工作钉在输入框上方）——与官方桌面版结构一致。

## 官方对话页的总体结构

```
┌─────────────────────────────────────────────┐
│ 会话头：标题 / +N -M diff 指示 / CI / ⋯       │
├─────────────────────────────────────────────┤
│  transcript（滚动区，内容栏宽居中 ~700-760px）│
│   用户消息：右对齐有色气泡                    │
│   助手消息：无气泡，整栏宽，左 sparkle 图标     │
│     ├ 文本段（markdown 流式渲染）             │
│     ├ 代码块卡（语言头 + 复制 + 等高限）       │
│     ├ Thinking 卡（chevron，默认折叠）        │
│     ├ 工具卡（chevron 行 → 展开见输入/diff/输出）│
│     └ 错误/提示条（InfoBar 式）               │
├─────────────────────────────────────────────┤
│ composer 浮动卡：附件chip / TextBox 无边框     │
│   底行：＋附件 · 模型选择 · 权限模式 · ⏹/发送 │
└─────────────────────────────────────────────┘
```

三种 transcript 密度（会话菜单 Transcript view / Ctrl+O）：
- **Normal**：工具调用折叠成单行摘要（图标 + 名称 + 目标 + 耗时 + 状态 chip），点 ▸ 展开。
- **Thinking**：同上 + 显示思考块。
- **Verbose**：全部展开。Summary 为第四种（只留结果），见于 4 月改版公告。

**给我们最重要的启示**："默认折叠、点击展开、头部行承载摘要"是官方答案；Fufucode 的 `ToolCallBlockViewModel`/`ThinkingBlockViewModel` 结构与此同构，差距在细节呈现。

---

## 1. 消息气泡形态

**它怎么呈现**
- **用户消息是唯一有气泡的**：右对齐、圆角大（约 18px）、accent 色低饱和填充（亮色是暖灰/`#f0f4f8` 系；2026-04 深色改版后是 accent 蓝，被社区骂"饱和蓝太刺眼"——官方后来回调到更低饱和）。最大宽度约为栏宽 75–85%。无头像（或只有极小头像/无）。
- **助手消息完全没有气泡**：整栏宽、无底色、无框，与页面底同色。左侧一个小的 sparkle（✦/星形）图标或 Claude 标志做归属标记；名字与时间极弱化（11–12px 灰字）。多条助手段落间靠间距分，不靠卡片。
- 左右不对称就是全部区分手段：**用户=右+有色泡；助手=左+无泡+图标**。没有"用户头像在左、助手头像在右"这种对称式聊天布局。

**对应到我们的 XAML（`ChatPage.axaml`）**
- 现状：`Border.userBubble` 右对齐 + `AppUserBubble`（`SystemAccentColor` @ Opacity .12/.16）+ `OverlayCornerRadius`(8) + `MaxWidth=560`，旁边还有 24px `Icon.Person` 头像。方向已经对了。
- 对齐官方的增量：
  1. `userBubble` `CornerRadius` `OverlayCornerRadius`→固定 `14`~`18`（官方气泡明显比通用卡圆角大；`open-claude-ui-audit` 建议 18）。
  2. `MaxWidth=560` 改相对值 `~75%` 栏宽（绑定 `TimelineItems.Bounds.Width*0.75`），短消息不再撑一条等宽泡。
  3. **去掉用户头像**（`UiShowAvatars` 下用户侧可不渲染；官方正品用户侧无头像）。如保留开关，默认关。
  4. 用户消息尾部的 `metaName + 时间`行可整行去掉或合并为单行 11px 灰字右对齐——官方几乎不显示 per-message 时间。
  5. 助手头像列保留但缩小到 20px（现在 28px），图标改 `Icon.Sparkle` 即可（已是）；`UiShowAssistantNames` 默认显示一次名字即可，勿每条重复。

## 2. 代码块样式

**它怎么呈现**
- 深色 surface 卡（比页面底亮半级），圆角约 8–12px，1px 细分隔线出头行。
- 头行：左侧语言名（小写、灰字 mono），右侧 Copy 按钮（图标 + "Copy" 文字，复制后短暂变 ✓）。
- 代码区：等宽字体、有语法高亮、**有行号**（web/desktop 均有，左侧浅灰 gutter）、横向滚动、纵向有 max-height 限高+展开。
- 配色不用高饱和主题色：暗色是近黑底+白字+柔和高亮，亮色是浅灰底。

**对应到我们的 XAML**
- 现状：`Border.codeCard`（`AppCodeBackground` + `AppCodeBorder` + `OverlayCornerRadius`），头行 `caption` 语言名 + 右侧 `iconButton` 复制，内层 `ScrollViewer` 横滚 + `mono` 12.5 无行号。
- 增量：
  1. **加行号 gutter**：代码 `TextBlock` 换成两列 Grid——左列 `TextBlock` 生成行号串（`1…N`，`AppTextDim`，右对齐，`Margin=0,0,10,0`），右列才是代码。行号列 `IsHitTestVisible=False`，复制仍取 `Code`。
  2. 复制按钮加文字 `复制`（或 Copy）到图标右侧，复制成功后 1.5s 变 ✓（`saved-check-decay` 见 audit §3.5）。
  3. `FontSize=12.5`→`13`，`LineHeight` 显式 `18`，保证与行号列基线一致。
  4. `MaxHeight` 默认收紧到 ~320（对应官方折叠高度），`ExpandLabel` 已存在，保持。

## 3. 工具调用卡 / 思考块

**它怎么呈现（Normal 模式 = 默认）**
- 每次工具调用折叠为**一行**：`▸ 图标 工具名 目标摘要（文件路径/命令）` + 右侧耗时与状态。这不是"卡片"，更像一条可点击的 list row，行间无框无线，靠 hover 底色传达可点。
- 点 ▸ 展开才出现内容体：Edit/Write 展开为**内联 git-style diff**（红绿行、真实行号、`+N -M`）；Bash 展开为命令+输出（stderr 分红）；Read 展开为文件片段。展开的 body 是**无边框的浅色/深色填充区**或直接流式内容，不再套一层闭合框。
- 状态语义：运行中图标旋转/呼吸 + "running"；失败左侧红色小标或整行淡红 tint；成功只灰下去。工具卡**不显示完整 JSON**（那是 Verbose 的事）。
- 思考块（Thinking 模式）：单独一条 `▸ Thought for 12s` 行，展开后是灰字小字号段落。默认折叠。
- AskUserQuestion 卡是个例外：它浮在 composer 上方、默认展开（issue #99338 抱怨的就是这点），chevron 可收起为标题行。
- **正在跑的工作有汇总入口**：运行中工具不止躺在 transcript 里——composer 上方有一条常驻行（Paddock 复刻为 "live bar"：每行 `名称 + 正在做什么 + 步数/耗时`），官方对应物是页头/底部的活动指示与 "N tools running" 汇总。

**对应到我们的 XAML**
- 现状：`Border.toolCard` 是**闭合卡片**（`AppInnerWell` + 1px 描边 + `OverlayCornerRadius`），头行 wrench + `mono SemiBold` 工具名 + `badge` 状态 pill，下方常驻 `InputJson`（3 行截断）+ `nestedFill` 输出框 + `CanCollapseOutput` 按钮。
- 差距：官方 Normal 模式折叠态是**无框一行**，我们折叠也是整卡；官方不常驻显示 InputJson。
- 增量（按收益排序）：
  1. **折叠态去框**：`toolCard` 拆两态——`IsExpanded=false` 时外层 `Border` 改 `Background=Transparent BorderThickness=0`，只剩一行 `chevron + 图标 + mono 名称 + 灰字目标摘要 + 右侧耗时/chip`；整行套 `Button.subtle` 作为展开热区（现在只有输出区可折叠，头部不可点）。`IsExpanded=true` 才恢复现有卡面。这等价于官方 Normal 视图。
  2. **头行摘要承载目标**：`ToolCallBlockViewModel` 需要一个 `TargetSummary`（Edit→文件名 `+N -M`；Bash→命令首行；Read→`path lines a–b`；Grep→`N matches`）。现在只有 `InputJson` 原文 3 行，信噪比低——参照 Paddock 的 per-tool subtitle 规则实现。
  3. `InputJson` 从常驻改为展开后可见（或挪到 Verbose-only）。
  4. `thinkingBlock` 头行改成同构 `▸ 思考过程 · 耗时`，与工具行视觉同族（官方两者几乎一样，只差图标）。现在是卡面 + "思考过程" caption。
  5. **运行中汇总行**：composer 顶部 `queueStrip` 旁加一条 `runningStrip`（复用 `nestedFill`），列出 `IsRunning` 的 ToolCallBlock（名称 + 当前子状态），点击滚动定位到对应卡。对应官方 composer 上方的活动条。
  6. 状态徽章保留 `badge` pill，但**默认折叠态只留图标色**（成功灰、失败红、运行中 accent 呼吸），pill 文字只在展开态显示——减少行内噪声。
  7. transcript 密度三档：把 `ToolCallBlockViewModel`/`ThinkingBlockViewModel` 的 `IsExpanded` 绑定到全局 `Timeline.Density`（Normal/Thinking/Verbose），Normal 下工具卡默认折叠、Thinking 下多展开思考块、Verbose 全展开。这是官方最直接可抄的特性之一。

## 4. 输入区（composer）

**它怎么呈现**
- 一张**浮动圆角卡**（圆角约 16–20px，比消息泡还圆），与时间线同栏宽，底部有 ~12–16px 外边距不贴窗缘；浅阴影。
- TextBox **完全无边框无底**，placeholder 弱提示；Enter 发送、Shift+Enter 换行。
- 底行从左到右：`＋`（附件/命令菜单）→ 环境/仓库 chip（web 版）→ 弹性空间 → **模型选择器**（无框文字+ chevron，不是 ComboBox 观感）→ **权限模式选择器**（"Ask permissions / Auto accept edits / Plan / Auto / Bypass" 五档）→ 发送钮（圆形或圆角、accent 填充、纸飞机/↑图标；运行中变为 ⏹ 停止方块）。
- 附件是卡内顶部一排小磁贴（~113px 缩略图/文件名中间截断）；拖放整个 composer 区域都是投放目标。
- 状态提示：上下文用量、queued 消息数在底行或卡内顶部以小灰字/chip 呈现，不占主视觉。

**对应到我们的 XAML**
- 现状：`composerCard` 全宽贴底（`MaxWidth=880 HorizontalAlignment=Stretch`、`CornerRadius=0`、只留 `BorderThickness=0,1,0,0` 顶发丝线）、`composerInput` 无边框、`composerUnderline` 悬停/聚焦线、底行 `＋ iconButton + Attach iconButton + 两个无边框 ComboBox + Stop/插入/accent 发送钮`。
- 增量：
  1. **浮动化**：`composerCard` 改 `Margin="20,0,20,12"`、`CornerRadius`→`16`（新 token `AppCornerComposer`，皮肤可覆写）、`BorderThickness=1` `BorderBrush=AppCardStroke`、`BoxShadow=FluentPillShadow` 弱阴影。视觉上与时间线分层的差异最大、成本最低。
  2. `composerUnderline` 可退役——浮动卡已有边界，下划线是"贴底无边框"时代的补偿；保留则改成 `:focus-within` 仅显 accent 底部高亮。
  3. **权限模式选择器**：`ComposerModes` 里补一个与模型选择器并列的无框选择（Ask/Auto/Plan/…），绑定现有审批模式 VM。官方把它放在发送钮左侧，是桌面版标志性控件。
  4. 发送钮改**圆形图标钮**（38×38 `CornerRadius=19`，内 `Icon.Send`；运行中同位置换 `Icon.RecordStop` accent→中性色），现在的 "图标+文字" pill 偏 Fluent 对话框风。
  5. `queueStrip`/`restoreInfo` 已对齐官方"卡内顶部 chip 条"模式，保持；给 `AttachedFiles` WrapPanel 的磁贴补 `MaxWidth=113` + 文件名中间截断（`TruncateFileNameMiddle` 已有先例见 audit）。
  6. 整卡拖放：`DragDrop.AllowDrop=True` on `composerCard`，`Drop`/`DragOver` 高亮（`Background`→`AppHover` + 虚线描边或 "拖入以附加" 占位）。

## 5. 状态指示（流式 / 空态 / 错误态）

**它怎么呈现**
- 流式：助手消息逐字流入，无打字机光标；运行中的工具行图标呼吸/旋转； composer 发送钮变 ⏹。长任务有 "Claude is working…" 类小灰字 + 三点/扫光。
- 空态：新会话页中央是**问候语 + composer**（"What can I help you build?" 风格大标题居上、composer 居中的欢迎布局），而不是空插画；有历史会话列表的空态才用插画。session 列表为空时还有 usage 仪表盘占位。
- 错误：InfoBar 式横条（错误=浅红底+图标；中断=中性卡）插在 transcript 流内，同时 toast 通知（会话在后台完成时系统级通知）。
- "回到最新"：滚动离开底部后，右下角出现圆形/胶囊浮动钮（↓ + 有时带"新消息"文字），点击平滑回底。

**对应到我们的 XAML**
- 现状：空态=居中插画+标题+说明（`EmptyArt`）；错误=`errorCard` InfoBar 三态；回到最新=`backToLatest` pill + `FluentPillShadow`（右下）；流式=文本块直接更新。
- 增量：
  1. 空态改为"标题 + composer 上移居中"可选——低成本方案是保留插画但把 `Image` 缩小到 96–120、标题加 `subtitle` 字阶、并把 `composerCard` 在 `Timeline.IsEmpty` 时 `VerticalAlignment=Center`（需要一层 `IsEmpty` 驱动的布局态）。若不想动布局，至少把插画+文案往上挪到黄金分割（`VerticalAlignment=Center` + `Margin` 上移 ~10%）。
  2. 运行中指示：`toolCard` 运行态图标 `Icon.Wrench` 换旋转 `ProgressRing`(IsIndeterminate) 或 `status-dot` 呼吸（`Opacity .3↔1` `1.2s`）；页面级已有 `StatusBar.ActivityText`，补一个三点跳动（3 个 3px `Ellipse`、交错 150ms `TranslateY`）放页头。
  3. `backToLatest` 现在已经是右下 pill——与官方一致；官方只有图标圆钮，可把文字 `BackToLatestLabel` 在窄栏隐藏。
  4. 错误卡保持 InfoBar 式即可；补"已中断/已停止"的 `neutral` 态（已有）+ 重试 `Button.subtle` 行内动作。

## 6. 排版与栏宽

**它怎么呈现**
- 正文 ~15–16px（web rem 基准 16，桌面略缩 ~15），行高 ~1.5（22–24px）；中文产品对应 14–15px/20–22 很接近。
- 阅读栏宽：transcript 内容栏居中，**约 700–780px max**（不同资料 680–800），composer 同宽或略宽。
- 段落间距：消息间 ~20–28px；助手内部段落 ~8–12px；工具行与正文间 ~4–8px。
- 字体：UI 用系统无衬线（Anthropic 品牌侧是 Styrene/Tiempos，但产品 UI 实为 Inter/系统族），代码块等宽。用户气泡文字与正文同字号。

**对应到我们的 XAML**
- 现状：`TimelineItems MaxWidth=768 Padding=24,20,24,8`；`body` 14/20；`caption` 12；`meta` 11；`mono` 12–12.5。已基本命中。
- 增量：
  1. `MaxWidth=768`→`720` 或保留；更重要的是**让 composer 与内容栏同宽**——`composerCard` 目前 `MaxWidth=880` 比 768 宽，浮动化时统一为 `720`（或都居中 `MaxWidth=760`）。
  2. 消息外边距 `Margin="0,6"` 改 `0,10`（用户/助手消息块间），助手内部 blocks `Margin="0,4"` 保持。
  3. `body` 行高 20→21（14×1.5）；`mono` 行高显式 18（配合行号 gutter）。
  4. 数字混排场景（耗时、+N -M、行号）已全部走 `AppTabularNumericFeatures`/`numeric` class——保持。

## 7. 滚动行为

**它怎么呈现**
- 贴底自动跟随流式；用户上滚超过阈值后停止跟随并浮出"↓"钮；新内容到达但用户不在底部时，按钮有时会带未读提示。
- 顶部/底部有约 24–28px 内容羽化渐变（内容淡入边缘），长 transcript 阅读更柔和。
- 滚动条纤细、overlay 式（hover 才显）、深色系半透明。

**对应到我们的 XAML**
- 现状：`TimelineScroller` 裸 ScrollViewer + `ScrollChanged`（`distanceToBottom<=32` 判定贴底）+ `backToLatest` pill。
- 增量：
  1. 阈值对齐官方/audit 常量：`PinnedThreshold=40`、`JumpButtonThreshold=120`（离底>120 才显示回到最新，现在似乎任何离底都显示 `ShowBackToLatest`），`UserInputCooldown=150ms` 防抖。
  2. 顶/底羽化：`TimelineScroller` 上盖两条 `IsHitTestVisible=False` 的 28px `Border`，`LinearGradientBrush` 顶部 `Transparent→AppMicaPageBackground`（非线性 stop：15/30/45/60/75/87%），`IsVisible` 由 `ScrollChanged` 里 `Offset.Y>2` / 距底>2 驱动。
  3. 平滑回底：`backToLatest` 点击目前 `ScrollToEnd()` 瞬跳；可加 200ms `ScrollToOffset` 动画（Vector transition）。

## 8. 空态

并入 §5。官方的空态核心决策：**第一次进入的界面是"带问候的 composer"，不是插画**。插画/骨架留给列表和二级面板。

---

## 落地优先级（按视觉收益/改动成本）

| 序 | 改动 | 文件 | 成本 |
|---|---|---|---|
| 1 | composerCard 浮动化（Margin 20/12、圆角 16、描边+弱阴影、与内容栏同宽 720） | `ChatPage.axaml` + `FufucodeTheme.axaml` | 低，一处 Style + 几处属性 |
| 2 | toolCard 折叠态去框为单行（chevron 行 = 按钮），展开才出卡；头行加 TargetSummary | `ChatPage.axaml` DataTemplate + `ToolCallBlockViewModel` 加属性 | 中，需 VM 摘要逻辑 |
| 3 | transcript 密度三档 Normal/Thinking/Verbose 控制 IsExpanded | Timeline VM + 两个 BlockVM | 中 |
| 4 | userBubble 圆角 16、去用户头像、MaxWidth 75% | `FufucodeTheme.axaml` + `ChatPage.axaml` | 低 |
| 5 | 代码块行号 gutter + 复制钮带文字+✓反馈 | `ChatPage.axaml` codeCard 模板 | 低-中 |
| 6 | composer 加权限模式选择器（无框）+ 发送钮圆形化 | `ChatPage.axaml` ComposerToolbar | 低 |
| 7 | 顶/底 28px 羽化 + JumpButton 阈值 120 | `ChatPage.axaml` + code-behind | 低 |
| 8 | thinkingBlock 与工具行同构（▸ 单行） | `ChatPage.axaml` thinking 模板 | 低 |
| 9 | composer 上方 running 汇总条 | `ChatPage.axaml` queueStrip 旁 + VM | 中 |
| 10 | 空态：标题+composer 居中（或小插画+上移） | `ChatPage.axaml` | 低 |

## 明确不建议照搬

- **深色纯黑底 + 饱和 accent 气泡**：issue #48158 证明这是官方自己翻过车的组合。我们的 `#202020/#2C2C2C` 分层 + accent @12–16% 低透明气泡反而更接近"改版前好评版"——**保持 `AppUserBubble` 低透明度策略，不要学"实心蓝泡"**。
- **Write/Edit 默认展开完整 diff**：issue #59931/#37584 是官方被点名的噪声源。我们的 `CanCollapseOutput` + `nestedFill` 已是正确方向，折叠态摘要行是改进而非追赶。
- **对称双头像聊天布局**：官方不用，我们若去用户头像即对齐。
- 字体上不必追 Styrene/Tiempos；系统字体+现有字阶足够。

## 参考链接

- https://claude.com/blog/claude-code-desktop-redesign
- https://claude.com/resources/articles/preview-review-and-merge-with-claude-code
- https://code.claude.com/docs/en/desktop
- https://venturebeat.com/orchestration/we-tested-anthropics-redesigned-claude-code-desktop-app-and-routines-heres-what-enterprises-should-know
- https://aicodingflow.com/en/articles/claude-code-desktop-no-terminal
- https://www.mejba.me/blog/claude-code-desktop-app-review
- https://www.tomsguide.com/ai/i-tested-claude-codes-new-web-app-it-makes-coding-as-easy-as-chatting
- https://github.com/anthropics/claude-code/issues/48158 （深色气泡吐槽，附截图）
- https://github.com/anthropics/claude-code/issues/59931 （diff 卡无法折叠）
- https://github.com/anthropics/claude-code/issues/57060 （折叠箭头行为）
- https://github.com/anthropics/claude-code/issues/99338 （AskUserQuestion 卡 chevron）
- https://github.com/anthropics/claude-code/issues/42780 （桌面端可折叠块被终端用户羡慕）
- https://paddock.edspencer.net/using/reading-claudes-work/ （第三方复刻，per-tool 渲染细节最完整）
- 本仓既有：`docs/research/open-claude-ui-audit.md`（数值级常量：圆角 18/20、羽化 28px、pinning 40/120/150ms 等）
