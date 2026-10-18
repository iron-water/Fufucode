# 皮肤教师参考书：Fluent 基线 × Vista Aero（E7-DESIGN）

2026-10-13。本文写给**接手实现的低成本模型**：照着做，不用猜。样板在 `docs/frontend-prototype/skins/teacher/`。

边界（四件事分开）：

| 事项 | 状态 |
|---|---|
| 教师样板（HTML + 可编译 AXAML 片段 + 截图） | 本任务完成，待 M 审核 |
| 生产接线（Styles/MainWindow/Settings/皮肤引擎） | **未做**，由 E7-ENGINE/M 负责 |
| 用户视觉确认（Aero 是否采用、Aero Night 暗色是否接受） | **未确认** |
| 原生验证（真实 DWM、DPI、IME、真实指针/键盘） | **未验证** |

默认皮肤仍是 Fluent。Liquid Glass 不做。Luna 只给策略（§8），不出素材。

---

## 1. 理念：一句话一皮肤

- **Fluent（默认）**：中性表面靠 1px 分隔和轻填色分层；强调色只给主动作、选中指示条和焦点。参考 UniGetUI / Windows 11 设置。
- **Vista Aero（可选）**：**“玻璃在框，内容在纸”**。光泽和渐变只放在窗口外框、标题栏、页签头、按钮这类 *chrome*；正文、编辑器、终端、diff 是不透明的平面“纸”。辨识点只有四个：
  1. 50% 处硬切的四段渐变（上亮下深，模拟玻璃反光）；
  2. 双线描边：外 1px 深线 + 内 1px 白色高光；
  3. 蓝色光泽 hover/pressed（WPF Aero ButtonChrome 色值）；
  4. 标题栏右上角三键连体按钮，关闭键红色。

做不到这四点就不是 Aero；超出这四点（正文加玻璃、到处发光）就是噪声。

## 2. 视觉层级（从远到近）

| 层 | Fluent | Aero | 规则 |
|---|---|---|---|
| L0 窗口外框 | 1px `AppBorderBrush`，圆角 8 | 外 `#3D5A78` + 内 `#9EFFFFFF` 两层 1px，圆角 7 | 外框只画在顶层窗口/浮动窗口/对话框 |
| L1 标题栏 48 | `AppChromeBackground` 实色 | 四段渐变（§3.2）；inactive 去饱和 | 只有这里允许“玻璃”半透明（§7.2） |
| L2 停靠区头 40 | 同窗口底 + 1px 分隔 | 浅渐变头 + 立体页签 | 页签行高度不变（生产 `DockRegionView` 40） |
| L3 停靠区体 | `AppDockBackground` | 极浅竖向渐变 `#F7FAFD→#EAF1F8` | 渐变幅度 ≤ 3% 亮度，不可见条纹 |
| L4 内容纸 | `AppContentBackground` | 纯白 `#FFFFFF` / 暗 `#1A1F26` | **禁止**渐变、透明、纹理 |
| L5 浮层 | flyout 阴影 + 圆角 8 | 菜单直角 + 2px 2px 4px 硬阴影；对话框带 Aero 标题条 | 浮层不透明 |

截图对照：`shots/fluent-light-shell-1440.png` vs `shots/aero-light-shell-1440.png`。

## 3. Token

### 3.1 命名与落点

HTML 用 `--t-*`（`teacher/css/teacher-tokens.css`），Avalonia 用 `Aero*` / `App*` 键。Fluent 列全部引用 S1 已接受值（`skins/css/tokens.css`），**本任务没改 Fluent 任何值**。

规则：皮肤字典里给**具体值**（`SolidColorBrush`/`LinearGradientBrush`），不要用 `StaticResource` 别名；模板/样式用 `DynamicResource`。原因：`FufucodeTheme.axaml` 现有 `App*` 是 `StaticResource` 别名（S1-2），运行时换字典不刷新。工装已证明具体值 + DynamicResource 可在同一控件实例上热切换（`harness-run.txt` 的 `hot swap` 四条）。

### 3.2 颜色（亮 / 暗）

| 语义 | Avalonia 键 | `--t-*` | Aero 亮 | Aero 暗（Aero Night，待用户确认） |
|---|---|---|---|---|
| 标题栏 | `AeroCaptionBackground` | `caption-bg` | `#DDEBF9 0 / #BFD6F0 .46 / #A3C3E8 .50 / #B4CEEC 1` | `#3A4B60 / #2A394B / #1B2634 / #223042` |
| 标题栏 inactive | `AeroCaptionBackgroundInactive` | `caption-bg-inactive` | `#F0F3F6 / #E3E8EE .48 / #D8DFE7 .50 / #DFE5EC` | `#33383F / #2A2E34 / #222529 / #272B30` |
| 标题栏（有模糊时） | `AeroCaptionGlass` | `caption-glass` | 同上色、alpha 0x8C–0xB8 | 同上、alpha 0x99–0xB3 |
| 标题文字 / inactive | `AeroCaptionText(Inactive)` | `caption-text` | `#10233A` / `#4F5B6A` | `#EAF2FB` / `#9AA4B0` |
| 标题文字光晕 | `AeroCaptionTextGlowColor` | `caption-glow` | `#F2FFFFFF`，blur 6 | `#D9000000`，blur 6 |
| 外框 外/内 | （窗口层） | `frame-outer/inner` | `#3D5A78` / `#9EFFFFFF` | `#05090F` / `#29FFFFFF` |
| 窗口底 | `AppWindowBackground` | `window-bg` | `#EEF3F8` | `#161B22` |
| 内容纸 | `AppContentBackground` | `content-bg` | `#FFFFFF` | `#1A1F26` |
| 分隔 / 强分隔 | `AppBorderBrush` / `AppStrongBorder`* | `border` | `#C9D6E3` / `#8E9FB2` | `#2E3946` / `#4F5E70` |
| 正文/次要/弱/禁用 | `AppTextPrimary/Secondary/Dim/Disabled`* | `text*` | `#1B1B1B` `#4A5462` `#66717F` `#8D96A0` | `#ECEFF3` `#B9C2CD` `#8E99A6` `#67717D` |
| 强调 / 强调文字 | `AppAccent` / `AppAccentText` | `accent` | `#2F7ED8` / `#0B5CAD` | `#4EA6EE` / `#8CCBFF` |
| 按钮 normal | `AeroButtonBackground`* | `btn-bg` | `#F2F2F2 / #EBEBEB .5 / #DDDDDD .5 / #CFCFCF`，边 `#707070` | `#3C4652/#333C47/#29313B/#2E3742`，边 `#0B0F14` |
| 按钮 hover | `AeroButtonBackgroundPointerOver`* | `btn-bg-hover` | `#EAF6FD / #D9F0FC / #BEE6FD / #A7D9F5`，边 `#3C7FB1` | `#3F6E96/#2F5A80/#214A6F/#2C5C86`，边 `#5FA8E0` |
| 按钮 pressed | `AeroButtonBackgroundPressed`* | `btn-bg-pressed` | `#E5F4FC / #C4E5F6 / #98D1EF / #68B3DB`，边 `#2C628B` | `#1D3D5C/#1A3752/#132A40/#183450`，边 `#3B85C2` |
| 默认（主）按钮 | 同 normal + 内环 | `btn-primary-*` | 内环 2px `#BF00CCFF`，边 `#3C7FB1`，字 `#1B1B1B` | 内环 `#B35FB6F2`，边 `#5FA8E0`，字白 |
| 焦点环 | `AeroFocusRing`* | `focus-ring` | 2px `#39A0E6` + 1px 白间隙 | 2px `#5FB6F2` + 1px 黑间隙 |
| 列表/设置行 hover | `AppListHover` | `row-hover-*` | `#FAFBFD→#EBF3FD`，边 `#B8D6FB` | `#26313D→#22303F`，边 `#3F6F99` |
| 列表/设置行 selected | `AppListSelected` | `row-selected-*` | `#DCEBFC→#C1DBFC`，边 `#7DA2CE` | `#24476A→#1C3A58`，边 `#4F8FC4` |
| 菜单 | `AeroMenu*`* | `menu-*` | 底 `#F5F6F7`，边 `#979797`，图标槽 28 `#F1F1F1` + 分隔 `#E2E3E3`/`#FFF`；hot `#F1F8FD→#D5EFFC` 边 `#A8D8EB` | 底 `#20262E`，边 `#4A5562`；hot `#2E5274→#234463` 边 `#4F8FC4` |
| 错误 底/边/字 | `AppErrorBackground/Border/Foreground`* | `error-*` | `#FFF1F0` / `#C42B1C` / `#A4231A` | `#3A1A1C` / `#F1707A` / `#FFB4B9` |
| 成功 / 警告 | `AppSuccessForeground` / `AppCautionForeground` | | `#1E7B1E` / `#8A5300` | `#7DD88A` / `#F2C14E` |
| 终端 底/字/提示符 | `AppTerminalBackground/Foreground`、`AppTerminalAccent`* | `term-*` | `#0C0F14` / `#E2E8F0` / `#7FD5A6` | `#07090C` / `#DDE4EC` / `#7FD5A6` |
| diff 加 底/字 | `AppDiffAddBackground/Foreground`* | `diff-add-*` | `#E6F5E9` / `#1D5C2E` | `#17331F` / `#9FE0AE` |
| diff 删 底/字 | `AppDiffDeleteBackground/Foreground`* | `diff-del-*` | `#FBE9EA` / `#922028` | `#3A1A1E` / `#FFAAB2` |

`*` = 当前 `FufucodeTheme.axaml` **没有**的键，需要 E7-ENGINE 新增（与 S1 TOKENS-MAPPING §2 的新增键合并）。窗口按钮全套颜色见 `teacher/avalonia/AeroCaption.axaml`，那里是唯一真源。

按钮色值基准：WPF Aero `ButtonChrome`（[dotnet/wpf](https://github.com/dotnet/wpf) `PresentationFramework.Aero/.../ButtonChrome.cs`，MIT）的公开常量，本文只取数值、不复制代码。

### 3.3 对比度（WCAG 相对亮度公式，用 Python 计算）

| 组合 | 前景 / 背景 | 比值 |
|---|---|---|
| Aero 亮 正文 / 内容纸 | `#1B1B1B` / `#FFFFFF` | 17.22 |
| Aero 亮 次要 / 窗口底 | `#4A5462` / `#EEF3F8` | 6.88 |
| Aero 亮 弱 / 内容纸 | `#66717F` / `#FFFFFF` | 4.96 |
| Aero 亮 标题字 / 渐变最深段 | `#10233A` / `#A3C3E8` | 8.71 |
| Aero 亮 inactive 标题字 | `#4F5B6A` / `#D8DFE7` | 5.15 |
| Aero 亮 按钮字 / hover 最深段 | `#1B1B1B` / `#A7D9F5` | 11.39 |
| Aero 亮 默认按钮字 / 最深段 | `#1B1B1B` / `#CFCFCF` | 11.06 |
| Aero 亮 选中行字 / 选中最深段 | `#1B1B1B` / `#C1DBFC` | 12.14 |
| Aero 亮 error / diff 加 / diff 删 | 见 §3.2 | 6.74 / 7.09 / 7.30 |
| Aero 暗 正文 / 次要 / 弱 | | 14.36 / 9.61 / 5.72 |
| Aero 暗 标题字 / 选中行字 / error | | 7.89 / 8.34 / 9.28 |
| 终端（两套） | | 15.57 / 15.55 |
| 窗口按钮白图形 / hover 蓝、close 红 | `#FFF` / `#2E86C9`、`#C4462D` | 3.91、4.93（另有深色光晕描边，非文字） |
| 禁用文字 亮/暗 | | 2.73 / 2.99（禁用态按 WCAG 豁免，但必须配说明文字） |

首轮样板“白字蓝渐变主按钮”实测只有 4.12（亮）/ 4.62（暗），且渐变上段更低，**已弃用**，改为 Vista 原生“默认按钮”形态（普通按钮 + 青色内环）。这是本任务的一个真实修正。

### 3.4 形状 / 字体 / 间距 / 阴影

| 项 | Fluent（现状） | Aero |
|---|---|---|
| 控件圆角 | `ControlCornerRadius` 4 | 3（按钮/行），输入 2 |
| 浮层圆角 | `OverlayCornerRadius` 8 | 菜单 0，对话框/窗口 7 |
| 页签 | 0 圆角 + 底部 2px 指示条 | 顶部 3,3,0,0；选中页签高 34（其余 32）、白底、顶部 2px `#3C7FB1` 内线、与内容“连通”（`margin-bottom:-1`） |
| 描边 | 1 | 1；外框/按钮额外 1px 内高光 |
| 阴影 | flyout `0 8 24 #29000000` | 菜单 `2 2 4 #52000000`（硬阴影）；浮动窗口 `0 10 28 #6B081E3C` |
| UI 字体 | `Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Inter` | `Segoe UI, Microsoft YaHei UI, Microsoft YaHei`（Vista 原字体就是 Segoe UI；不用 Variable） |
| 等宽 | `Cascadia Mono, Consolas, monospace` | 同 |
| 字号 | 14 / 12 / 20 / 28 | 14 / 12 / 标题栏 13 / 设置页标题 24 常规字重 + 强调文字色（Vista 控制面板风格） |
| 间距 | 4 的倍数（guideline U04） | 同；不改布局度量 |

不打包第三方字体（guideline U02）。所有字体都是 Windows 自带。

## 4. 组件与状态

状态全集：normal · hover · pressed · focus（仅键盘，`:focus-visible`）· selected · disabled · error · inactive（仅窗口级）。截图：`shots/aero-light-states-1440.png`、`aero-dark-states-1440.png`、`fluent-light-states-1440.png`。

### 4.1 标题栏与窗口按钮（已有可编译 AXAML，§6）

| 规则 | 值 |
|---|---|
| 命中区 | 每键 **46×32 顶对齐**，与生产 `CaptionAssetButton` 相同；Aero 不改宽度 |
| 视觉板 | 高 21，吊在顶部；min 左下圆角 4，close 右下圆角 4，三键首尾相接 |
| 板下 11px | 透明但**属于按钮**（工装 `y=28` 命中断言） |
| 图形 | 10×10 Path，白色 1.6 描边 + 深色 3.2 光晕底层（替代 CSS drop-shadow） |
| hover / pressed | 蓝光泽 / 深蓝；close 为亮红 / 深红 |
| focus | 2px `AeroCaptionFocusRing` 叠在板上，只在 `:focus-visible` |
| disabled | 图形 opacity 0.45，板不变 |
| inactive | 板换 `AeroCaptionPlateInactive`、图形灰、去光晕；**hover 仍给完整反馈** |
| 不变 | 拖拽区仍是 caption 中间 `*` 列；双击最大化、`BeginMoveDrag`、按钮排除逻辑（`MainWindow.Chrome.cs`）一行不改；AutomationName 保持“最小化/最大化/关闭” |

### 4.2 其余组件（HTML 已实现，AXAML 迁移交给 §9 包 B）

| 组件 | 生产选择器 | Aero normal | hover | pressed | focus | selected | disabled | error |
|---|---|---|---|---|---|---|---|---|
| 按钮 | `Button`（默认）、`Button.accent` | 灰四段渐变、边 `#707070`、内白高光 | 蓝四段、边 `#3C7FB1` | 深蓝四段 + 内阴影 `1 1 2 #40000000` | 焦点环 | — | `#F4F4F4` 平底、边 `#ADB2B5`、字禁用色 | 2px 错误边 |
| 图标按钮 | `Button.iconButton`/`.toolButton` | 透明 | 蓝四段 + 1px 内边 | 深蓝 | 焦点环 | — | 图形禁用色 | — |
| 输入框 | `TextBox` | 白底、边 `#ABADB3`、底边 `#E3E9EF`、内阴影 `0 1 1 #14000000` | 边 `#5794BF` | — | 边 `#3D7BAD` + 4px 同色 45% 外光 | — | 灰底、禁用边 | 边+环 `#C42B1C` |
| 页签 | `Button.dockTab` | 灰四段、边 `#8C99A8`、顶内高光 | 蓝四段 | — | 焦点环 | 白底、高 34、顶 2px 强调内线 | 禁用字 | — |
| 列表项 | `ListBoxItem` | 透明 | `#FAFBFD→#EBF3FD` + 边 | — | 焦点环 | `#DCEBFC→#C1DBFC` + 边 `#7DA2CE` | — | — |
| 设置行 | `Border.settingsRow` | 白、边 `#D5DFE8`、圆角 3 | 同列表 hover | — | 焦点环 | 同列表 selected | 文字禁用色 | 底 `#FFF1F0` 边 `#C42B1C` |
| 菜单 | `ContextMenu`/`MenuFlyoutPresenter`/`MenuItem` | §3.2 菜单行 | hot 渐变 + 边 | — | 同 hot | 勾选图标在图标槽 | 禁用字 | — |
| 对话框 | 现有 dialog 宿主 | Aero 标题条（=caption 渐变，32 高）+ 白体 + `#F0F0F0` 底栏 | — | — | — | — | — | — |
| 终端容器 | `TerminalDockView` 外框 | 底 `#0C0F14`、顶 1px `#22303F`、内阴影 `inset 0 1 3 #99000000` | 内容区无任何皮肤效果 | | | | | |
| 状态栏 | `CustomStatusBar` | 高 22、`#F1F5FB→#DFE7F2`、11.5px | — | — | — | — | — | — |

## 5. 应用示范（截图索引）

全部截图：headless Chrome，`--force-device-scale-factor=1`，参数见 `shots/html-shots-log.txt`；Avalonia 帧由工装 `CaptureRenderedFrame` 输出。

| 文件 | 尺寸 | 内容 |
|---|---|---|
| `fluent-light-shell-1440.png` / `fluent-dark-shell-1440.png` | 1440×900 | Fluent 基线五区（核心 4 帧之二） |
| `aero-light-shell-1440.png` / `aero-dark-shell-1440.png` | 1440×900 | Aero 五区（核心 4 帧之二） |
| `aero-light-states-1440.png` / `aero-dark-states-1440.png` / `fluent-light-states-1440.png` | 1440×1300 | 状态画廊 |
| `aero-light-shell-800x640.png` | 800×640 | 窄窗：右栏收起、底栏 150 |
| `aero-light-settings-800x640-statusbar.png` | 800×640 | 窄窗设置页 + 状态栏开启示例 |
| `fluent-light-settings-1440.png` / `aero-dark-settings-1440.png` | 1440×900 | Windows 设置式目的地 |
| `aero-light-inactive-float-dialog-1440.png` | 1440×900 | inactive 主窗 + 右栏整栏浮动 + 对话框 |
| `aero-light-glass-menu-1440.png` | 1440×900 | “有模糊时”caption + 区域菜单 |
| `avalonia-aero-caption-900x398.png` | 900×398 | **Avalonia 真实渲染**：亮/暗 × active/inactive × 5 状态 + 对话框单键 |

状态栏默认关闭（所有工作台截图都没有状态栏）；只有标了 `statusbar` 的那张打开了它。浮动窗口与主窗口用同一页签模型，没有第二套页面。

## 6. Avalonia 落点（可直接照抄）

文件：`teacher/avalonia/AeroCaption.axaml`（资源 + ControlTheme）、`AeroCaption.Styles.axaml`（作用域样式）、`harness/`（最小工装）。版本 Avalonia 12.1.3 / net10.0，与 `Fufucode.App.csproj` 一致。

### 6.1 三种容器，各放什么

| 容器 | 放什么 | 为什么 |
|---|---|---|
| `ResourceDictionary` 的 `ThemeDictionaries` Light/Dark | 所有颜色/渐变画刷（具体值） | 亮暗跟随 `ThemeVariant`；`ThemeVariantScope` 可局部覆盖 |
| `ControlTheme`（`x:Key`，`TargetType`） | 控件**自身**的模板与自身状态（`^:pointerover`、`^.close`、`^:focus-visible`） | 替换模板而不改控件类型；不影响未设 `Theme` 的同类控件 |
| `Styles`（作用域选择器） | 依赖**祖先**状态的规则（`Border.aeroCaption.inactive Button…`）和容器外观 | ControlTheme 的嵌套选择器必须以 `^` 开头，选不到祖先 |

### 6.2 CSS → AXAML 对照

| CSS | AXAML |
|---|---|
| `linear-gradient(180deg, A 0%, B 46%, C 50%, D 100%)` | `<LinearGradientBrush StartPoint="0%,0%" EndPoint="0%,100%">` + 4 个 `GradientStop`，硬切 = 两个 stop 的 Offset 相同或相邻 |
| `:hover` / `:active` | `:pointerover` / `:pressed` |
| `:focus-visible` | `:focus-visible`（12.1.3 已支持，生产已在用） |
| `.a .b`（后代） | `Border.a Button.b`（Styles 内）；模板内部件 `/template/ Border#PART_Plate` |
| `box-shadow: inset 0 0 0 1px` | 内层 `Border` 的 `BorderBrush`/`BorderThickness`（模板里的 `PART_Highlight`） |
| `box-shadow` 外阴影 | `BoxShadow="2 2 4 0 #52000000"`（`Border.BoxShadow`） |
| `text-shadow` 光晕 | `TextBlock.Effect` = `DropShadowEffect OffsetX=0 OffsetY=0 BlurRadius=6` |
| `filter: drop-shadow` 于图标 | 两层 `Path`：粗深色描边在下、细白描边在上 |
| `var(--x)` | `{DynamicResource X}`（不要 `StaticResource`） |
| CSS 后声明覆盖 | Avalonia 也按声明顺序覆盖：hover 写在 pressed 前，`.close:pressed` 写在最后 |

### 6.3 接入步骤（给 E7-ENGINE/M）

1. 两个 axaml 复制到 `src/Fufucode.App/Styles/Skins/Aero/`（文件名不变）。
2. 皮肤租约 apply 时：`topLevel.Resources.MergedDictionaries.Add(ResourceInclude AeroCaption.axaml)`；`topLevel.Styles.Add(StyleInclude AeroCaption.Styles.axaml)`。dispose 时只移除这两个引用。
3. `MainWindow.axaml`：`CustomCaption` 加类 `aeroCaption`（Fluent 时移除）；三个 `CaptionAssetButton` 设 `Theme="{DynamicResource AeroCaptionButtonTheme}"` 并加类 `aeroCaptionButton` 和 `min/max/close`。最大化后把 `max` 换成 `restore`（替代现在的 `Kind` 换图）。也可以新增并列的 `AeroCaptionButton`，二选一；**不要**改 `Click` 处理器、拖拽区、ToolTip、AutomationName。
4. `inactive`：`Activated/Deactivated` 事件里切 `CustomCaption.Classes` 的 `inactive`。
5. `glass`：沿用 `ApplyMicaOverrides` 的判断（`ActualTransparencyLevel` 为 Mica/AcrylicBlur/Blur）时加 `glass` 类；其余（含无头）保持实色。不改 `TransparencyLevelHint`，那是 M 的决定。
6. 浮动窗口（`MainWindow.DockFloating.cs` 里 `new Window`）同样挂租约，避免浮窗仍是 Fluent。

### 6.4 工装证据（实际运行）

```text
dotnet build docs/frontend-prototype/skins/teacher/avalonia/harness/AeroCaptionHarness.csproj -c Release --artifacts-path %TEMP%\e7-aero-harness
  → 0 个警告 0 个错误（TreatWarningsAsErrors=true）
AeroCaptionHarness.exe docs/frontend-prototype/skins/teacher/shots
  → RESULT pass=45 fail=0（全文 teacher/avalonia/harness-run.txt）
```

45 条覆盖：亮暗解析、四段硬切、inactive 切换（背景/标题/三键）、标题光晕仅 active、46×32 命中区、21px 板、`y=28` 命中、AutomationName、各状态画刷、`:focus-visible`、disabled 0.45、restore 图形、**真实无头指针**（hover→pressed→一次 Click→板下点击也触发→移开复原）、**同实例热切换**（主题、`.inactive`、`.glass`）。

红例（保留）：首轮在挂入视觉树前写 `:pressed` 伪类，挂树后只剩 `:pointerover`，pressed 断言 4 条 FAIL。修正为挂树后强制，并另加真实指针输入作为独立证据；断言未放宽。

## 7. 可行性与降级

### 7.1 能做 / 不能做

| 效果 | Avalonia 12.1.3 | 结论 |
|---|---|---|
| 四段渐变、双线描边、圆角 | `LinearGradientBrush`、嵌套 `Border` | 能，工装已渲染 |
| 标题文字光晕 | `DropShadowEffect` | 能，工装已渲染；大面积使用有性能成本，只给标题 |
| 按钮/页签状态 | ControlTheme + 伪类 | 能，工装已证明 |
| Vista 原生 DWM “玻璃”（窗口框后的桌面模糊） | 无 Vista DWM API；只有 `TransparencyLevelHint`（Blur/AcrylicBlur/Mica，取决于 Windows 版本） | **不承诺**。Win11 上得到的是 Acrylic/Mica 质感，不是 Vista 玻璃 |
| 标题栏区域单独模糊 | 透明级别是整窗级 | 不能局部；只能整窗透明 + 内容区不透明覆盖 |
| 关闭键外发光（Vista hover 时的红色光晕溢出） | 可用 `BoxShadow` 但会溢出命中区外、可能被裁 | 本样板未做，不影响辨识度 |

### 7.2 降级梯度

1. **默认 = 实色渐变**（`AeroCaptionBackground`）：任何平台、无头、远程桌面都一样。截图全部是这一档。
2. **有模糊时**：`.glass` 换 `AeroCaptionGlass`（alpha 0x8C–0xB8）。只作用于 caption；内容纸、终端、编辑器、diff 永远不透明。
3. **高对比度 / 用户关闭“标题栏玻璃效果”**：退回第 1 档；高对比度下应整体回 Fluent（系统高对比色），Aero 不适配高对比（未验证，交 M 决定）。

## 8. Luna 资源策略（只定策略，不出素材）

- Luna 的窗口按钮、页签、滚动条是**位图多状态**风格（圆润高光），用纯渐变还原度差。
- 复用生产已有的 `CaptionAssetButton` 机制：`assets/titlebar/luna/{light,dark}/{kind}-{state}@{1,2,3}x.png`，kind = minimize/maximize/restore/close，state = normal/hover/pressed/disabled/inactive（比现有多 inactive 一态）= 每主题 20 张 × 3 倍率。
- 其余控件（按钮、页签、输入框）优先用渐变 + 圆角 3–4 近似；只有渐变做不出的（滚动条拇指、复选框勾）才进位图清单。
- 素材必须原创（XP 原图有版权），走 G 资产流程；本任务不生成。

## 9. 给低成本模型的限定任务包

模型选择按 `docs/tasks/session-difficulty.md` 与 AGENTS 分级表；测试/试跑显式 `devin/swe-2`。每包只写列出的文件；不碰 App.axaml、MainWindow*、Settings*、共享计划；不 push；完成后写交付卡。以下 prompt 可直接复制。

### 包 A：Token 补齐（L2）

```text
推荐主模型：group/auto-deepseek-v4-1-flash｜难度L2｜理由：按表填值的机械任务，有完整输入与校验口径。备选 group/auto-qfmodel。测试显式 devin/swe-2。
任务：把 docs/design/skin-teacher-reference.md §3.2 中 Aero 列全部颜色写成 Avalonia ThemeDictionaries 资源。
允许写入：src/Fufucode.App/Styles/Skins/Aero/AeroTokens.axaml（新）、docs/records/E7-aero-tokens.md（新）。
输入：参考书 §3.2/§3.4；样板 docs/frontend-prototype/skins/teacher/css/teacher-tokens.css（Aero 段为真源）；格式照抄 teacher/avalonia/AeroCaption.axaml。
规则：具体值，不用 StaticResource 别名；渐变用 4 个 GradientStop；键名用参考书列出的 App*/Aero* 名；不改 FufucodeTheme.axaml。
完成条件：App 构建 0 警告 0 错误；记录里列出每个键与 CSS 变量的一一对应表；未接线如实写。
```

### 包 B：控件状态迁移（L3）

```text
推荐主模型：group/auto-deepseek-v4-1-flash｜难度L3｜理由：需写 ControlTheme 与伪类，但有可编译样板照抄。备选 group/auto-qmodel-38max。测试显式 devin/swe-2。
任务：按参考书 §4.2 表，为 Button、Button.accent、Button.dockTab、TextBox、ListBoxItem、Border.settingsRow、MenuItem 写 Aero ControlTheme/作用域样式。
允许写入：src/Fufucode.App/Styles/Skins/Aero/AeroControls.axaml、AeroControls.Styles.axaml（新）；docs/frontend-prototype/skins/teacher/avalonia/harness/**（仅扩展工装断言）；docs/records/E7-aero-controls.md。
输入：AeroCaption.axaml（ControlTheme 写法）、AeroCaption.Styles.axaml（作用域写法）、参考书 §6.1/§6.2 对照表、状态截图 shots/aero-*-states-1440.png。
规则：每个状态一个选择器；声明顺序 hover→pressed→变体；focus 只用 :focus-visible；ControlTheme 必须 x:Key，不得改全局默认主题；不复制整套 Fluent 模板，只替换需要的部件。
完成条件：工装 dotnet build 0 警告 0 错误；每个控件×每个状态至少一条“实际画刷 == 资源”断言并全绿；输出亮/暗两张 CaptureRenderedFrame；先写一条失败的断言再修绿（红例留在记录）。
```

### 包 C：资产补齐（L2）

```text
推荐主模型：group/auto-qfmodel｜难度L2｜理由：清单整理与 SVG 绘制，无业务逻辑。备选 group/auto-deepseek-v4-1-flash。不需要模型试跑。
任务：为 Aero 产出缺失图标/形状的 SVG 原稿与清单：窗口按钮 restore 图形、页签关闭 ×、菜单勾选、对话框单键。只做 SVG 与清单，不做 Luna。
允许写入：assets/skins/aero/**（新）、docs/records/E7-aero-assets.md。
输入：AeroCaption.axaml 的 Path Data（10×10 网格）、参考书 §4.1、§8 命名规则。
规则：原创，无第三方位图；10×10 或 16×16 网格，描边 1.6 圆头；每个文件注明尺寸与用途；不接入 csproj。
完成条件：清单列出文件名/尺寸/状态/用途；SVG 能被浏览器打开；记录写明未接线。
```

### 包 D：像素比对（L2）

```text
推荐验证主模型：devin/swe-2｜难度L2｜理由：脚本化截图与差异统计属于验证，优先免费模型；免费模型不可用时才用 group/auto-qfmodel。普通工具运行不需要模型请求。
任务：在包 B 完成后，把 Avalonia 工装帧与 HTML 样板截图按组件裁切比对，输出差异报告。
允许写入：docs/frontend-prototype/skins/teacher/compare/**（新）、docs/records/E7-aero-pixel.md。
输入：shots/aero-light-states-1440.png、shots/aero-dark-states-1440.png、包 B 的 Avalonia 帧。
规则：只比对颜色与尺寸（渐变 stop 位置、边框色、圆角、命中区），字体渲染差异不算缺陷；用本机 Python/Pillow，不联网。
完成条件：每个组件一行：HTML 采样色、Avalonia 采样色、ΔE 或 RGB 差、尺寸差、结论（一致/需修/可接受）；超阈值项列为包 B 回修清单，不自行改 axaml。
```

## 10. 实施验收清单（给 M）

- [ ] Fluent 默认值与现状逐像素不变（Fluent 帧前后对比）。
- [ ] Aero 亮/暗 × active/inactive × 5 状态的 Avalonia 帧与 §5 截图一致（包 D）。
- [ ] 窗口按钮命中区 46×32、拖拽/双击最大化/关闭语义不变；UIA 名称不变。
- [ ] 主窗口与浮动窗口同皮肤；关闭皮肤后资源与样式引用全部移除。
- [ ] 内容纸/终端/编辑器/diff 无渐变无透明；§3.3 对比度在实现中复核。
- [ ] 原生：真实指针、键盘焦点、125%/150% DPI、模糊开/关、远程桌面——全部未验，需 V 任务。
- [ ] 用户选择：是否采用 Aero、Aero Night 暗色是否接受——未确认。
