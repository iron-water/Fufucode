# open-claude-ui 逆向还原审计 → Fufucode Avalonia 迁移清单

> 仓库: https://github.com/Caho1/open-claude-ui (commit `main`, 克隆于 2026-10-17)
> 该仓库是对 Claude Desktop (`@ant/desktop v2.37337.1`) 的 clean-room 逆向还原，价值不在 UI 风格模仿，而在其公布的**精确生产数值**（弹簧刚度/阻尼、7 档 alpha 遮罩、WebKit IME 时戳守卫等）。下述每个"它怎么做"都对应到具体文件和常量；"我们能抄什么"落到 Fufucode 已有的 `App*` 资源键与控件 class 名。

参考约定：Fufucode 现有 `src/Fufucode.App/Styles/FufucodeTheme.axaml` 已有 `AppUserBubble` `AppCardBackground` `Border.composerCard` `Border.userBubble` `Border.thinkingBlock` `TextBox.composerInput` 等成品；下述条目只标**增量改动**。

---

## 1. 布局结构

### 1.1 三栏桌面骨架（MainWindow ↔ Claude Desktop）

**它怎么做**
- `createClaudeMainWindow.ts` — `BrowserWindow` + `WebContentsView` 双层架构；`backgroundColor: isDark ? '#151515' : '#fcfcfb'`，`show:false` + `ready-to-show` 打开，避免白闪。
- `ClaudeDesktopHeader.tsx` — 45px 全局拖拽条 `height: var(--desktop-titlebar-inset, 45px)`，`WebkitAppRegion: 'drag'`；交互控件必须包 `.draggable-none`/`no-drag`；macOS 红绿灯预留 `74px` 占位，缩放时 `Math.round(74 / Math.min(zoomFactor, 1))` 逆补偿。
- 窗口默认 `1200×800`，最小 `600×400`，非最大化时 `setBorderRadius(8)`，最大化/全屏时 `0`。

**我们能抄什么**
- `MainWindow.axaml` 已有 `ChromeRoot RowDefinitions="48,*,26"` 与自定义 `CaptionAssetButton`。需要新增：**窗口未最大化时 `CornerRadius=8`、最大化时 `0`** 的动态切换（绑定 `WindowState` 到 `OffScreenMargin`/`ContentMargin`），以及启动 `Background={DynamicResource AppWindowBackground}` 显式赋色避免白色闪烁帧。
- 拖拽区隔离：Fufucode `CaptionDrag_PointerPressed` 已处理；可借鉴"拖拽带 → 固定 48px"这一单一职责分层，把 `CustomTopLeft/Center/Right` 的 `ScrollViewer` 均加 `PointerPressed` 透传拖拽，与 `LegacyCaptionActions` 同层。

### 1.2 会话列表宽度与折叠

**它怎么做**
- `DraggableSessionList.tsx` — 侧边栏固定 `w-72` (288px)，分为"已固定 (N)"和"最近对话"两段；行 `px-2.5 py-2 rounded-lg`；标题 `text-xs`、时间 `text-[10px]`。

**我们能抄什么**
- 对照 `Styles/WorkbenchStyles.axaml` `ListBox.workspaceList` 与 `Border.sidebar`；将侧边栏行高统一为 `Height=32`（CDS `h-8`），行内边距 `10,0`，圆角 `8`（映射 `ControlCornerRadius`）。
- 分段头 `11px SemiBold uppercase tracking-wider` 对应 `TextBlock.settingsGroupHeader` 但需新增 `LetterSpacing=0.5` 与 `Opacity=0.75` 弱化。

### 1.3 响应式断点（容器查询）

**它怎么做**
- `ContainerResponsiveBar.tsx` — 命名容器 `@container/toolbar`，4 阶断点：
  - `>=576px` 完整显示
  - `<480px` 隐藏次要徽章（如 "Thinking" 标签）
  - `<384px` 缩短模型名 `max-w-[72px]`、缩小内边距 `px-3 → px-2`
  - `<320px` 极简安全网（`gap-0` 防爆）

**我们能抄什么**
- Avalonia 无 `@container`；在 `WorkspacePanelView.axaml` / 任意窄栏容器上改为绑定 `Bounds.Width` + `Converter`/`Class` 切换：定义 `paneState` 附加属性（`wide | medium | narrow | mini`），阈值 `576/480/384/320`，样式用 `Control.paneState-narrow` 派生。
- `ChatComposerChin.tsx` 的 `--cds-h-control: 36px`/`--cmp-chin-row-h: 28px` 负边距补偿 → 映射为 `MinHeight=28` + `Margin=0,-2` 的紧凑模式。

### 1.4 双栏分割（Artifacts / 左右分栏）

**它怎么做**
- `ArtifactsSplitPaneResizer.tsx` — 左 `minLeftWidth=380`、右 `minRightWidth=420`；`collapseThreshold=0.18`（<18% 或 >82% 自动吸附折叠）；拖拽中向根容器注入 `data-pane-dragging`，并在右侧 iframe 区域盖 `pointer-events:none` 透明护盾防止捕获丢失。
- 双击手柄归位 `50/50`，Escape 回滚到 `previousRatioRef`。

**我们能抄什么**
- `MainWindow.axaml` 的 `DeadZoneGridSplitter` 已承担大部分职责；增量：
  1. 双击复位 — `GridSplitter` 加 `DoubleTapped` → 重置 `ColumnDefinitions` 到 `*`。
  2. 折叠阈值 — 新增 `CollapseThresholdRatio` 附加属性（`0.18`），`Splitter` 拖动跨越时把列宽 `GridLength(0)` 并在右缘显示 `展开产物` 浮钉按钮（`absolute right-2 top-3` 映射为 `Margin="0,12,8,0" HorizontalAlignment=Right`）。
  3. 拖拽护盾 — `Splitter` 拖动开始/结束事件里给右侧 `Panel` 挂一个 `IsHitTestVisible=false` 的 `Border`（用于未来内嵌 WebView 防吞噬；目前对 Avalonia 原生控件也安全）。

### 1.5 滚动容器与边缘羽化

**它怎么做**
- `ScrollFadeContainer.tsx` + `cds-theme.css` `.scroll-fade` — 28px 羽化（`--cds-scroll-fade-size: 28px`），**7 档非线性 alpha**（15%/30%/45%/60%/75%/87%/100%），精确贴合 `y≈x^1.5` 幂律感知曲线，杜绝高分屏色阶断层。垂直/水平双轴，触顶/触底时对应边缘置 0。

**我们能抄什么**
- `Views/ChatPage.axaml` `TimelineScroller` 目前是裸 `ScrollViewer`。
- 迁移方案：Avalonia 无 CSS `mask-image`，但可用 `ScrollViewer.OpacityMask` 配合 `LinearGradientBrush`；或在 `TimelineScroller` 上盖两层 28px `Border` + `LinearGradientBrush`（顶部 `Transparent → AppContentBackground` 8 档渐变，`IsHitTestVisible=False`）。渐变 stop 偏移直接抄 15/30/45/60/75/87 百分比。
- 滚动状态绑定：已存在 `TimelineScroller_ScrollChanged`，在其中改 `TopFade.IsVisible = e.Offset.Y > 2` / `BottomFade.IsVisible = ...`（`2px` 阈值与 CDS 相同）。

### 1.6 滚动图钉（scroll pinning）

**它怎么做**
- `useClaudeScrollPinning.ts` — `distanceFromBottom <= 40` 判 pinned；`>120` 显示"跳至最新"按钮；`150ms` 用户输入冷却（`performance.now() - lastUserInputAt > 150`），防止流式追加时抢用户视口。

**我们能抄什么**
- `ChatPage` 已有 `TimelineScroller_ScrollChanged`；落到三个常量：`PinnedThreshold=40`、`JumpButtonThreshold=120`、`UserInputCooldownMs=150`，再加 `LastUserInputUtc` 字段。`behavior:'auto'` 对应 Avalonia 的 `ScrollToEnd()` 立即跳（默认非平滑）。

---

## 2. 动画

### 2.1 弹簧物理矩阵（核心可复制）

**它怎么做**
- `ClaudeMotionPresets.ts` — 二阶阻尼谐振子 `m·x'' + c·x' + k·(x-x_target) = 0`；提供求解器 `calcDampedHarmonicOscillator` 及四个 Framer Motion 预设：

| 预设 | stiffness | damping | mass | ζ | 超调 | 收敛 | 用途 |
|---|---|---|---|---|---|---|---|
| `SPRING_BOUNCY` | 600 | 30 | 1.0 | 0.612 | +8.8% | 0.22s | 按钮按压、图标反馈 |
| `SPRING_SNAPPY` | 320 | 28 | 1.0 | — | +1.8% | 0.35s | 气泡/卡片/Popover 入场 |
| `SPRING_GENTLE` | 380 | 18 | 1.0 | — | +19.4% | 0.58s | 点赞、复制成功勾选 |
| `SPRING_HEAVY_SNAP` | 1200 | 80 | 0.1 | 3.65 | 0% | — | 抽屉吸附、分割条拖拽 |

**我们能抄什么**
- Avalonia `SpringEasing`/`CubicBezierEasing` 近似：在 `Styles/` 中定义 `Easing` 资源：
  ```xml
  <CubicBezierEaseIn x:Key="AppSpringBouncy">0.34,1.3,0.64,1</CubicBezierEaseIn>
  <CubicBezierEaseIn x:Key="AppSpringSnap">0.32,0.72,0,1</CubicBezierEaseIn>
  <CubicBezierEaseIn x:Key="AppEaseOutExpo">0.16,1,0.3,1</CubicBezierEaseIn>
  <CubicBezierEaseIn x:Key="AppEaseExpand">0.19,1,0.22,1</CubicBezierEaseIn>
  ```
  对应 `--cds-ease-overshoot/--cds-ease-snap/--cds-ease-swift/--cds-ease-expand`。
- 时长映射：`fast=100ms`、`normal=200ms`、`slow=350ms` 三档，对应 CDS 的 `var(--cds-dur-fast)/-slow`。

### 2.2 关键帧（直接抄数值）

**它怎么做（cds-theme.css 第 348–453 行）**
- `_grapheme-reveal`: `0%{opacity:0;filter:blur(6px)} → 100%{blur(0)}`，350ms easeOut — **字符级流式晕染**
- `timeline-quiet-dot`: `0%,8%{opacity:0;translateY(2px)} → 22%{translateY(-2.5px)} → 36%,70%{translateY(0)} → 90%,100%{opacity:0}`，**1.9s cubic-bezier(.4,0,.2,1) infinite**，三点依次延迟 `0/.15/.3s`
- `timeline-expand`: `grid-template-rows:0fr→1fr` + `opacity`，**0.2s cubic-bezier(.19,1,.22,1)** — 解决高度抖动
- `message-fade-up`: `0%{translateY(12px)} → 100%{0}`，**0.4s cubic-bezier(.34,1.3,.64,1)**
- `saved-check-decay`: `0%,75%{scale(1)} → 100%{scale(.92);opacity:0}`，**0.35s cubic-bezier(.165,.84,.44,1) 延迟 2.2s forwards** — 复制成功"勾选"自动消退
- `cds-skeleton-sweep`: `translate(-100%)→translate(100%)`，**1.8s cubic-bezier(.4,0,.2,1) infinite**，105° 倾斜渐变
- `dframe-clay-breathe`: `scale(.86)→scale(1)` + `brightness(.96→1.06)`，**3.0s ease-in-out infinite**（陶土呼吸灯）
- `dframe-dot-blink`: `opacity .3↔1`，**1.2s ease-in-out**（运行中状态点）
- `file-thumbnail-grow-in`: `scale(.85)→scale(1)`，**250ms cubic-bezier(.165,.84,.44,1)**（附件磁贴入场）
- `epitaxy-linear-sweep`: `translate(-100%)→translate(350%)`，**1.1s cubic-bezier(.45,0,.55,1)** — 底部进度扫光条
- `cds-shimmer-text-shine`: `background-position 83.333%→16.667%`，3s linear，非对称 `cubic-bezier(.714,.121,.211,.888)` 蓄力段 — "Claude 正在思考"扫光文字

**我们能抄什么**
- Avalonia `KeyFrame`/`Animation` 直接用 `CubicBezierEasing` 重写上述 keyframe：
  - 流式字符逐字渐显（`Grapheme` 级）→ 对应 `Timeline` 子项 `TextBlock` 入场动画 `blur+fade`（Avalonia 不支持 `filter:blur` 高效动画，降级为 `Opacity+TranslateY(4px)`）。
  - `timeline-quiet-dot` 三点跳 → `Border.thinkingBlock` 内嵌 3 个 `Ellipse Width=3`，`Animation` 1.9s infinite，`KeySpline=.4,0,.2,1`，`BeginTime=0/.15/.3`。
  - `saved-check-decay` 2.5s 衰减 → `Button` 复制成功后 `Icon=✓`，`Task.Delay(2200)` 触发 fade-out `350ms`。
  - `cds-skeleton-sweep` → `Border.skeleton` 覆盖 `LinearGradientBrush` + `TranslateXAnimation`（用 `VisualBrush` 或在 `RenderTransform` 上跑 `DoubleAnimation` -100→100）。

### 2.3 页面/气泡入场变体

**它怎么做（`ClaudeMotionPresets.ts` Variants）**
- `motionMessageFadeUp`：enter `y:12→0` + `SPRING_SNAPPY`；exit `y:-8` `0.18s easeIn`
- `motionUserBubbleEnter`：`scale(.94)+y:8` → `scale(1)+y:0`，`spring(450,24)`
- `motionPopover`：`scale(.95)+y:4` → `1+0` `SNAPPY`；exit `scale(.96)+y:2` `0.12s`
- `motionModalDialog`：`scale(.96)+y:10` → 1，`spring(400,32)`；exit `0.15s`

**我们能抄什么**
- `Border.userBubble` 入场：加 `Transitions` `TransformOperationsTransition` `350ms` `AppSpringSnappy`（先 `RenderTransform=TranslateY(8)`，加载后 → `0`）。
- `Flyout`/`MenuFlyout`：`ShowMode` 默认即时；需要自定义 `Popup` 模板注入 `ScaleTransition` `0.12s`。
- `ConfirmationDialog`（CDS）的 `motionModalDialog` 对应 `ProjectTrustDialog.axaml` / `ExtensionUiDialogWindow.axaml`：统一 `OpenAnimation = Scale+Fade 200ms (0.96→1, y+10→0)`，`CloseAnimation = 150ms easeInOut`。

---

## 3. 交互细节

### 3.1 键盘导航

**它怎么做**
- `useClaudeRovingFocus.ts` — 方向键漫游（vertical/horizontal/both 可选），`Home/End/PageUp/PageDown` 首尾定位；**`Tab` 立即跳出**漫游组交还全局焦点流；动态 `tabIndex`：仅当前项 `0`，其余 `-1`；RTL 镜像。
- `ArtifactsSplitPaneResizer.tsx` — 拖拽中 `Escape` 回滚。
- `ChatComposer.tsx` — **IME 合成守卫**：`e.nativeEvent.isComposing || e.keyCode === 229` 放行 Enter；`RichComposerWithMentions` 更严格，记录 `compositionend` 的 `timeStamp`，`KeyDown` 时 `Math.abs(e.timeStamp - lastCompositionEnd) < 500` 拦截（WebKit 中文输入法假 Enter bug）。

**我们能抄什么**
- `ListBox.workspaceList` / `ListBox.settingsNav`：新增 `KeyDown` 处理 `Alt+Up/Down` 重排项（对照 CDS 的 `handleKeyDown` 实现）。
- `TextBox.composerInput`：**必须加 IME 守卫**。`TextInput` 的 `KeyDown` 中 `e.Key == Key.Enter && !e.KeyModifiers.HasFlag(Shift)` 发送前，检查 `_imeCompositionEndUtc`，`DateTime.UtcNow - t < 500ms` 直接 return。这能解决中文候选词按 Enter 误发。
- `CommandPaletteView.axaml`：`Esc` 关闭、`ArrowUp/Down` 已具备；补 `PageUp/PageDown` 跳 10 项。

### 3.2 拖拽

**它怎么做（`DraggableSessionList.tsx`）**
- 4px 死区：`Math.abs(dx)>4 || Math.abs(dy)>4` 才激活 `isDraggingRef`，防止单击/右键误触发。
- 拖拽幽灵：`opacity:.4; scale(.98); border-dashed 陶土边框`。
- 落点对撞：行中点切分 `before/after`；跨区拖拽自动赋予 `isPinned`。
- `Alt+Up/Down` 键盘重排。

**我们能抄什么**
- `MainWindow.axaml` 侧边 `DockRegionView` 已支持拖拽；增量：拖拽起点记录 + 4px 死区（Avalonia `PointerMoved` 里 `Math.Max(|dx|,|dy|) < 4` return），与 `DockRegionView` 现有命中测试共存。
- 拖拽指示线：`overIndex` 行上/下缘显示 `Height=2` `CornerRadius=1` `Background=AppAccent` 的 `Border`，`Margin` 垂直 `-1`。

### 3.3 悬停 / focus-visible / 按压

**它怎么做**
- `.cds-hover-ghost`：`transition: background-color 150ms var(--cds-ease-swift), transform 100ms`；hover `bg=alpha-1`；`:active` `bg=alpha-2 + scale(.98)`。
- `.cds-btn-squish`：`--cds-btn-press-sx: .975`，`transition: transform var(--cds-dur-slow) var(--cds-btn-spring)`，`:active` 缩到 `.975`。
- `focus-visible`：标准 `ring-2 ring-clay/50` + `outline-hidden`，以及更复杂的 `--cds-focus-shadow: inset 0 0 0 1px surface-1, 0 0 0 1.5px clay, 0 0 6px 1px rgba(217,119,87,.25)`（三层光晕）。

**我们能抄什么**
- `Button.subtle` / `Button.iconButton`：加 `RenderTransform` `Scale` `1→.975` 按压，`Transitions` 100ms `AppEaseSnap`。
- `focus-visible`：Avalonia `:focus-visible` 已可用；新增 `FocusAdorner` 全局样式 → 外发光 `BoxShadows "0 0 6 1 rgba(217,119,87,.25)"`（需自定义 `Control` 或在 `FocusAdorner` 模板用 `Border` + `BoxShadow`）。

### 3.4 空态 / 错误态 / 加载态

**它怎么做**
- 空态：`ChatMessageBubbles` 空会话用 `EmptyArt` 图 + `bodyStrong` 标题 + `caption` 副标（Fufucode 已实现，`Timeline.IsEmpty` 分支一致）。
- 错误态：用户气泡失败 → `border-red-500/30 bg-red-50/10`，底部 `Failed to send` + `Retry` 按钮。
- 加载态：`QuietDotsWave`（3 点 1.9s 跳跃）、`SkeletonSweep`（105° 扫光）、`PulseRunningDot`（`animate-ping` / `dframe-dot-blink`）。
- `useRateLimitBackoff.ts`：429 限流指数退避（含 `Retry-After` 解析）。

**我们能抄什么**
- `Border.userBubble` 加 `state="failed"` 类：`BorderBrush=AppErrorBorder`、`Background=AppErrorBackground` + 底部 `Retry` 超链接按钮。
- 排队/限流提示：`Border.queueStrip` 已存在；参考 `ChatComposerChin.tsx` 的 `ChatComposerQueuedMessages` 黄色横幅样式 `bg-amber-50 border-amber-200` → `SystemFillColorCautionBackgroundBrush` / `CautionBrush`（已有）。
- 思考态：Fufucode `Border.thinkingBlock` 加 `IsThinkingLive` 时：强制展开（互斥锁）+ 3 点 1.9s 跳点动画。

### 3.5 复制反馈状态机

**它怎么做**
- `CodeBlock.tsx` / `ChatMessageBubbles.tsx` — 点击复制后立即 `✓ Copied`（emerald-400），**2.5s** 后 `saved-check-decay` 0.35s 淡出回退。

**我们能抄什么**
- 新增 `Controls/CopyFeedbackButton.cs`：`Click` → `Icon=✓`、`Foreground=SystemFillColorSuccessBrush`；`Task.Delay(2200)` → `OpacityTransition 350ms` 淡出后还原图标。统一替换 `CodeBlock`、消息行复制按钮。

---

## 4. 组件细节

### 4.1 按钮（CdsButton）

**它怎么做（`CdsButton.tsx`）**
- 尺寸阶梯：`xxs h-5 px-1.5 text-[11px] / xs h-6 text-[11.5px] / sm h-7 / md h-8 px-3.5 text-sm / lg h-10 px-5 text-[15px] / icon size-8`
- 变体：`secondary`（默认，`--cds-fill-secondary` 填充、无框）/ `primary`（clay 实底）/ `ghost` / `danger` / `brand` / `onContent`（媒体覆层 `bg-black/50 backdrop-blur`）
- **单闭合边框原则**：卡片内 `secondary` 严禁再带框
- 双层结构：外壳 `<button>` + 内层 `data-cds-part="paint"` 绝对定位 `span` 承载背景，杜绝 padding 与 border 干涉

**我们能抄什么**
- `Button` 体系映射：
  - `Button.subtle` → `ghost`（`Background=Transparent`，`:pointerover` `AppHover`）
  - `Button.accent` → `primary`（`AccentFillColorDefaultBrush`）
  - `Button.captionSlot` → `icon` (`30×30` → CDS `size-8` 即 `32px`，当前 `30` 可对齐到 `32`)
- 尺寸对齐：现有 `iconButton` `30×30` 微调到 `32×32` 更贴 CDS 规范（`h-8`）。
- 危险按钮：`Button.accent.danger` 新增，`Background=SystemFillColorCriticalBrush`。

### 4.2 输入区（Composer）

**它怎么做**
- `ChatComposer.tsx` — 20px 圆角（`--cds-radius-composer`）；`field-sizing:content` 自适应伸缩 `min-h-12 max-h-96`；三态双环投影 `--cds-shadow-composer/-hover/-focus`（`inset 0 0 0 1px border` + `0 4px 20px rgba(0,0,0,.035/.06/.08)`）。
- 底部行：`+ Add content`、模型切换药丸（含 `size-2 bg-clay` 状态点）、`Thinking` 开关（`bg-clay/15 text-clay`）、发送按钮 `size-8` clay 圆形 `active:scale-95`。

**我们能抄什么**
- `Border.composerCard` 现状：`CornerRadius=OverlayCornerRadius`（≈8px）。改为 `CornerRadius=20` 对齐 CDS，加 `Transitions` `BoxShadowsTransition 220ms`。
- 三态阴影转译：
  - 默认：`0 4 20 0 #09000000` + `BorderThickness=1` `AppCardStroke`
  - `:pointerover`：`0 4 24 0 #0F000000` + `BorderBrush=AppAccentSubtle`
  - `:focus-within`：`0 4 24 0 #14000000` + `BorderBrush=AppAccent`（替换现有 `composerUnderline` 方案为全域环绕光晕，或保留 underline 作为降级方案）
- `TextBox.composerInput` 加 `MaxHeight=384`（`max-h-96`）与 `MinHeight=48`（`min-h-[3rem]`），并保留 `AcceptsReturn=True` + 自适应高度逻辑（监听 `TextChanged` 调整 `Height`，因为 Avalonia 无 `field-sizing`）。
- IME 守卫：见 §3.1。

### 4.3 卡片（Card / Panel）

**它怎么做**
- `.cds-card-interactive`：`--cds-panel-shadow: 0 0 0 1px border, 0 1px 2px #0000000f, 0 2px 8px #00000014`；hover `translateY(-1.5px)` + `0 4px 16px -2px #00000017`；`border-radius: var(--radius-card) = calc(--radius(8px) + 4px * rem-scale(0.94))` ≈ `11.7px`。
- `AttachmentCard`：严苛正方形 `7.5rem × rem-scale ≈ 113px`，`rounded-card`，`-end-2 -top-2` 浮动圆形 `×` 移除按钮 `size-5`；文件名**中间截断**保留扩展名。
- `McpToolDecisionCard`：三态边框 `border-[accent]/50`（pending）/`emerald-500/40`（success）/`red-500/40`（error），含 `shadow-[0_4px_16px_rgba(217,119,87,.12)]` 品牌微晕。

**我们能抄什么**
- `Border.card`：新增 `BoxShadows "0 1 2 0 #0F000000, 0 2 8 0 #14000000"`，hover `RenderTransform=TranslateY(-1.5)` `Transitions 200ms`。
- `AttachmentCard` 中间截断算法（`truncateFileName`）：移植为 `StringHelpers.TruncateFileNameMiddle(name, maxLen)`，保留 `head…ext`；`AttachmentChip` 用同款。
- `Border.toolCard` 加状态类 `.pending/.running/.success/.error`，分别映射 `AppAccent/…Caution/Success/Critical` 边框（30–50% 透明度）。

### 4.4 分栏分隔条（GridSplitter）

**它怎么做**
- `ArtifactsSplitPaneResizer` — `w-2 -mx-1`（命中区 8px，可视线 1px），中心 16×16 `rounded-full` 抓手胶囊含两条 `w-0.5 h-1.5` 竖杠；拖拽时 `bg-clay/20` 高亮 + `scale-110`。

**我们能抄什么**
- `GridSplitter.dockSplitter`：模板加 `Border#SplitGrip` 中心 16×16 `Ellipse`+两条 `Rectangle`，`:pointerover` `Opacity=1`/`Scale=1.05`，拖动中 `Background=AppAccent` `Opacity=.2`（现状已带 `SplitGrip`，只需加视觉手柄）。

### 4.5 Tooltip

**它怎么做**
- CDS 未提供独立 Tooltip 组件，沿用 `title` 属性原生提示。

**我们能抄什么**
- Fufucode `ToolTip.Tip` 已用；可统一 `ToolTip` 样式：`Background=AppCardBackground` `CornerRadius=6` `Padding=8,6` `FontSize=12` `BoxShadows=FluentFlyoutShadow`（对齐 CDS `shadow-popover`）。

### 4.6 上下文菜单（右键）

**它怎么做**
- `ChatComposer` 弹层 `mb-2 w-52 rounded-xl p-1.5` `shadow-popover`；菜单项 `px-2.5 py-1.5 rounded-lg` `hover:bg-neutral-100`。

**我们能抄什么**
- `MenuFlyout` / `ContextMenu` 样式：项 `MinHeight=28` `Padding=10,6` `CornerRadius=8`，`MenuFlyoutPresenter` `CornerRadius=12` `Padding=6` `BoxShadows=FluentFlyoutShadow`。

### 4.7 头像 / 气泡 / 流式文本

**它怎么做**
- `ChatMessageBubbles`：
  - 用户气泡 `max-w-85%` `rounded-18px` `px-16 py-12` `bg=[--cds-bg-user-message-personal]`（浅蓝 `#f0f4f8` 个人模式）`shadow-2xs`
  - 模型气泡无底色，直接呈现在 `surface-1`；头部 `size-5` `bg-clay` `✦` spark 图标 + 模型徽章 `font-mono text-[10px]`
  - `motionGraphemeReveal`：`blur(6px)→0` 350ms 字符级流式晕染
- `StatusIndicators` — `status-dot` 严格 `6px` 圆形；`timeline-quiet-dots` `3px` 点 `gap:3.5px`

**我们能抄什么**
- `Border.userBubble` 改 `MaxWidth=560`→`MaxWidth=85%` 相对容器（绑定 `TimelineItems.Bounds.Width * .85`），`CornerRadius` 从 `OverlayCornerRadius`（8px）改为 `18`。
- 助手消息头：新增 `AssistantHeader` 模板，`16×16` `Border` `Background=AppAccent` `CornerRadius=4` 内 `TextBlock "✦" FontSize=10 Foreground=White`。
- 流式晕染：在 `StreamingDiffViewer`/`Timeline` 的 `TextBlock` 上，新 token 到达时用 `Opacity 0→1` + `BlurEffect.Radius 6→0`（`Transitions 350ms EaseOut`）做**近 token 级**渐变（Avalonia 无逐字 `filter`，按整段 chunk 做即可）。

### 4.8 SegmentedControl / Pill

**它怎么做（`SegmentedControl.tsx`）**
- 轨道 `h-7(sm)/h-8(md)` `p-[1px]` `bg=[--cds-segmented-track]`（`rgba(0,0,0,.06)`）
- 指示器 `top:1px bottom:1px` `border-radius: calc(--cds-radius - 1px)`，几何等分 `left = 1px + i*(100%-2px)/N`
- 磁吸 `120ms cubic-bezier(.32,.72,0,1)`；容器查询 `<180px(2项)/<254px(3项)` 隐藏未激活文本

**我们能抄什么**
- `Button.scopeChoice` + `Button.dockTab` 已有类似；新增 `Controls/SegmentedPill.axaml`：`Border` `CornerRadius=999` `Height=28/32` `Padding=1`，内部 `Canvas` 中 `Border` 指示器用 `TranslateX` + `Width = (Container.Width-2)/N`，`Transitions 120ms AppEaseSnap`。
- 折叠：绑定容器 `Bounds.Width` < `180`（2 项）或 `254`（3 项）时 `IsTextVisible=false`。

### 4.9 模态 / Popover

**它怎么做**
- `ConfirmationDialog`：遮罩 `bg-black/40 backdrop-blur-[2px]`，卡片 `rounded-2xl p-6` `max-w-[400/520/720]`；嵌套弹窗 `data-[nested-dialog-open]` 父级 `blur-[2px]`；`Escape` + 遮罩点击关闭。
- Popover 阴影 `--cds-shadow-popover: inset 0 0 0 [inner] ring, 0 0 0 [outer] ring, 0 8px 24px rgba(0,0,0,.12), 0 2px 6px rgba(0,0,0,.08)`；暗色模式内环 `1px white/10` 外环 `0`（**双环倒角物理**）。

**我们能抄什么**
- `ExtensionUiDialogWindow` / `ProjectTrustDialog`：遮罩 `AppScrimBackground`（已 `#33000000`）→ 可加 `BlurEffect.Radius=2`；卡片 `CornerRadius=16` `MaxWidth=400/520/720`；嵌套弹窗时对父窗口 `Effect=Blur(2)`。
- Popover `FluentFlyoutShadow` 已接近（`0 8 24`）；补 `BorderThickness=1` `BorderBrush=AppBorderBrush` 作为 outer ring。

### 4.10 状态灯与进度

**它怎么做**
- `status-dot` `6px`；`.animate-wl-glow` `1.8s` `box-shadow` 呼吸；`.animate-wl-halo` `2.2s`；`LinearSweepProgress` `1.1s cubic-bezier(.45,0,.55,1)` `translate(-100%)→translate(350%)`。

**我们能抄什么**
- `Border.badge`/`Ellipse.chipDot`：运行中状态加 `Animation 1.2s` `Opacity .3↔1`。
- `ProgressBar` 流式加载条：用 `LinearGradientBrush` + `TranslateX` 动画 `1.1s` 无限循环（等价 `epitaxy-linear-sweep`）。

---

## 5. 与 Fufucode 的可迁移点速查表

| CDS 概念 | 值/公式 | Fufucode 落点 | 具体改动 |
|---|---|---|---|
| `--cds-radius-composer` | 20px | `Border.composerCard` `CornerRadius` | 8 → 20 |
| `--cds-message-bubble-radius` | `8 + 4×0.94 ≈ 11.7`→官方 `18` | `Border.userBubble` `CornerRadius` | `OverlayCornerRadius`(8) → `18` |
| `--cds-message-bubble-measure` | 85% | `Border.userBubble` `MaxWidth` | `560` → `Bounds.Width*.85` |
| `--cds-message-bubble-pad-x/y` | 16/12 | `Border.userBubble` `Padding` | `12,8` → `16,12` |
| `--cds-scroll-fade-size` | 28px, 7 档渐变 | `TimelineScroller` 遮罩 | 新增顶/底 28px 渐变 `Border` |
| `--cds-ease-overshoot` | `cubic-bezier(.34,1.3,.64,1)` | `AppSpringBouncy` 资源 | 新 `CubicBezierEaseIn` |
| `--cds-ease-snap` | `cubic-bezier(.32,.72,0,1)` | `AppEaseSnap` | 新 |
| `--cds-ease-swift` | `cubic-bezier(.16,1,.3,1)` | `AppEaseOutExpo` | 新 |
| `--cds-ease-expand` | `cubic-bezier(.19,1,.22,1)` | `AppEaseExpand` | 新 |
| `SPRING_BOUNCY` | `stiff=600 damp=30` | 按钮 `:pressed` Scale | `Scale=.975` `Transitions 200ms` |
| `timeline-quiet-dot` | 1.9s, 延迟 0/.15/.3, `translateY(2→-2.5→0)` | `Border.thinkingBlock` 三点 | 3 `Ellipse` 3px，`Animation` 1.9s infinite |
| `cds-skeleton-sweep` | 105°, 1.8s | `Border.skeleton` 新增 class | `LinearGradientBrush` + `TranslateX` |
| `saved-check-decay` | 2.2s 延迟 + 350ms 消退 | 复制按钮 | `Task.Delay(2200)` + `Opacity 350ms` |
| `message-fade-up` | 0.4s overshoot | `Border.userBubble` 加载动画 | `Opacity+TranslateY 12→0` |
| `dframe-clay-breathe` | 3s scale(.86→1)+brightness | `StatusDot` pending | `Scale`+`Opacity` 呼吸 3s |
| `cds-btn-squish` | scale .975 | `Button` 全局 `:pressed` | `RenderTransform Scale` |
| `--cds-focus-shadow` | 3 层光晕 | `FocusAdorner` | `Border`+`BoxShadows "0 0 6 1 #40D97757"` |
| `--cds-shadow-popover` | `inset ring + 0 8 24 + 0 2 6` | `FluentFlyoutShadow` | 已有 `0 8 24`，加 `Border` 1px 环 |
| `--cds-shadow-composer-*` | 三态 | `Border.composerCard` | 见 §4.2 |
| 拖拽死区 | 4px | `GridSplitter`/会话列表 | `PointerMoved` 阈值检查 |
| 折叠阈值 | 0.18 | `DeadZoneGridSplitter` | 新增 `CollapseRatio` 附加属性 |
| 双击归位 | 50/50 | `GridSplitter` | `DoubleTapped` 事件 |
| 底部吸附 | 40px/120px/150ms | `TimelineScroller` | `PinnedThreshold=40` 等常量 |
| IME 守卫 | 500ms 时间戳 | `TextBox.composerInput` | `_imeCompositionEndUtc` 比较 |
| 对话行 `h-8` | 32px | `ListBox.workspaceList` `ListBoxItem` | `MinHeight=32` `Padding=10,0` |
| 分段控件指示器 | 1px padding, `calc((100%-2px)/N)` | 新 `SegmentedPill` 控件 | 见 §4.8 |
| 超调阴影 hover | `translateY(-1.5px)` | `Border.card` 新增 class | `RenderTransform` + 200ms |
| 附件磁贴 | `7.5rem × .94 ≈ 113px` | 附件 chip 尺寸 | 113×113，中间截断算法 |
| 中间截断 | `head…ext` | 附件/文件名显示 | `StringHelpers.TruncateFileNameMiddle` |
| 三点状态灯 | 6px / 3px 3.5gap | `Ellipse` in `chipDot` | 尺寸对齐 |
| `field-sizing:content` | min48 max384 | `TextBox.composerInput` | `MinHeight=48 MaxHeight=384` + 手动 Measure |
| macOS 交通灯补偿 | `Math.round((16.5+6)*zoom-6)` | `MainWindow` caption | 仅 macOS 构建启用 |
| 窗口圆角 | 8→0 最大化切换 | `MainWindow` `WindowState` | `CornerRadius` 动态绑定 |
| 嵌套弹窗模糊 | `blur-[2px]` | Dialog 堆叠 | 父 `BlurEffect.Radius=2` |
| `@container` 断点 | 576/480/384/320 | 任意窄面板 | `paneState` 附加属性 |
| `--cds-rem-scale` | 16/17 ≈ .941 | 全局字号 | `FontSize` 整体 ×0.94（可选） |
| Roving focus | `Tab` 退出, `tabIndex` 漫游 | `ListBox`/`CommandPalette` | `KeyDown` + `Focus()` |
| `Alt+↑/↓` 重排 | 键盘拖拽 | 会话列表 | 已实现则对齐键位 |
| `--cds-fill-ghost-hover` | `rgba(0,0,0,.05)` | `AppHover` | 已对齐（`#09000000`），无需改 |
| `--cds-border` | `rgba(0,0,0,.10)` | `AppBorderBrush` | `#0F000000` 已对齐 |
| `--cds-clay` | `#d97757` | `SystemAccentColor` 主题 | 可新增 `Clay` 主题色系 `Skins/Clay.axaml` |

---

## 附录 A：最值得先做的 5 件事（按 ROI 排序）

1. **`composerCard` 圆角 20px + 三态阴影** — 视觉差异最大，改动仅一处 `Style`。
2. **`userBubble` 圆角 18px + `MaxWidth=85%` + `Padding=16,12`** — 贴近官方气泡比例。
3. **`TimelineScroller` 28px 7 档上下羽化遮罩** — 长会话体验质变。
4. **`thinkingBlock` 三点 1.9s 跳跃 + Grid 0fr 展开**（Avalonia 用 `RowDefinition.Height` 动画）— 让流式过程有"官方感"。
5. **IME 500ms 守卫 + `Enter` 发送拦截** — 解决中文用户真实 bug。

## 附录 B：不建议照搬的

- **Houdini `@property` / `animation-timeline: scroll()`**：Avalonia 无对应物，用 code-behind 等价实现。
- **`filter: blur()` 逐字动画**：Avalonia `BlurEffect` 作用于整个 `Visual`，无法逐 glyph；降级为 `Opacity+TranslateY`。
- **`backdrop-blur-[2px]`**：Avalonia `BlurEffect` 可用但成本高；对话框遮罩已用半透明色即可。
- **`cubic-bezier(.714,.121,.211,.888)` 等 exotic 曲线**：Avalonia `CubicBezierEaseIn` 支持任意四点，但收益边际，优先用 4 个主曲线即可。
