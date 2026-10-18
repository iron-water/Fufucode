# S1 皮肤令牌与生产接入建议（样稿 → Avalonia 主题资源）

本文只描述**建议**与现状事实，未修改任何生产文件，接线由 S2/Codex 决策与实施。
所有色值取自 `css/tokens.css`（样稿实际渲染值）；“现状”一列来自对本仓库源码的阅读，标注处为未验证推断。

## 1. 现状事实（读源码得出）

| 事实 | 位置 | 含义 |
|---|---|---|
| Fluent 原语令牌是 Light/Dark 两套 ThemeDictionary | `src/Fufucode.App/Styles/FluentTokens.axaml` | 移植自 UniGetUI 的 WinUI Fluent v2 键（`SolidBackgroundFillColor*`、`TextFillColor*`、`AccentFillColor*`、`SystemFillColor*`、`Fluent*Shadow`） |
| `App*` 语义键由原语派生，且是 `StaticResource` 别名 | `src/Fufucode.App/Styles/FufucodeTheme.axaml:15-120` | `FufucodeTheme.axaml` 注释明确 “App* 键名为既有契约” |
| 形状键目前只有两个 | `FufucodeTheme.axaml:124-125` | `ControlCornerRadius=4`、`OverlayCornerRadius=8` |
| 组件样式只引用 `App*` / Fluent 语义键 | `Styles/WorkbenchStyles.axaml`（抽查全部 Background/Foreground/BorderBrush） | 换肤只需覆盖令牌层，不必改组件样式 |
| `WorkbenchAppearance.Surface` 字段存在但**没有渲染消费者** | `Infrastructure/WorkbenchAppearance.cs:22,85`（白名单 `fluent/glass/flat/classic`）；`SelfTest/WorkbenchCustomizationSelfTest.cs:353-357` 只做持久化自测 | 全仓 grep 未发现任何 XAML/样式读取 `Surface`；即 “glass/flat/classic” 目前不改变外观 |
| 主题切换通过 `RequestedThemeVariant` | `App.axaml.cs:45`、`ViewModels/SettingsPageViewModel.cs:324,343` | 只使用 `ThemeVariant.Light/Dark/Default` |
| Avalonia 版本 `12.1.3`；生产样式已在用 `:focus-visible`、`:focus-within`、`pointerover` | `Fufucode.App.csproj:38`、`FufucodeTheme.axaml:261,290,324`、`WorkbenchStyles.axaml:222` | 焦点环可按皮肤给出不同 thickness/offset，不需要额外平台判断 |

## 2. 接入路径（两个方案，二选一由 S2 决定）

### 方案 A（推荐）：皮肤 = 追加的 ThemeDictionary，用自定义 ThemeVariant 选择

1. 新增 `src/Fufucode.App/Styles/Skins/Metro.axaml`、`Material3.axaml`，各自是 `ResourceDictionary.ThemeDictionaries`，Light/Dark（或自定义变体）两套里放**具体值**的 `App*` 与形状/字体/阴影键。
2. 变体选择：`MetroLight = new ThemeVariant("MetroLight", ThemeVariant.Light)` 等，切换皮肤时设 `Application.RequestedThemeVariant`（与现有 `ThemeVariantFor` 同一入口）。**须先验证 Avalonia 12.1.3 自定义 ThemeVariant 的继承与查找顺序**（[INFERENCE]，本样稿未实测）。
3. 优点：运行时切换即时生效，不触发 `StaticResource` 别名陈旧问题；缺点：变体数量随皮肤 × 明暗增长。

### 方案 B：启动时合并皮肤字典 + `Surface` 落位

1. `WorkbenchAppearance.Surface` 白名单扩展为 `fluent/metro/material3`，按值在启动时合并对应皮肤字典（覆盖 `App*`）。
2. **必须先解决别名陈旧**：`App*` 现为 `StaticResource` 别名，运行时替换底层原语字典后不会刷新已解析的别名——要么让皮肤字典直接给出具体值，要么把 `App*` 改为 `DynamicResource` 形式。
3. 换皮肤若走此路径，建议明确提示“下次启动生效”，与设置页现有 `WaitRestart` 状态一致。

### 两个方案都必须新增的令牌键（当前不存在）

| 建议键 | 用途 | 样稿出处 |
|---|---|---|
| `AppCornerCard` / `AppCornerInput` / `AppCornerPill` / `AppCornerFlyout` | 卡片、输入、胶囊、浮层圆角（现只有 `ControlCornerRadius`/`OverlayCornerRadius`） | `--app-corner-card/-input/-pill/-flyout` |
| `AppFontUi` / `AppFontDisplay` / `AppFontMono` | UI / 标题 / 等宽字体族 | `--app-font-*` |
| `AppFontSizeBase` / `AppFontSizeSecondary` / `AppFontSizeSection` / `AppFontSizePage` | 字号刻度（Fluent 14/12/20/28，Metro 14/12/22/32，M3 同 Metro） | `--app-font-size-*` |
| `AppFontWeightStrong` | 强调字重（Fluent 600，M3 500） | `--app-font-weight-strong` |
| `AppShadowFlyout` / `AppShadowToast` / `AppShadowPill` | 浮层阴影；Metro 为 `none` | `--app-shadow-*` |
| `AppFocusThickness` / `AppFocusOffset` / `AppFocusStyle` | Fluent 双环 / Metro 直角环 / M3 强调色圆角环 | `css/base.css` 焦点段 + 三个 `skin-*.css` |

## 3. 颜色映射表（样稿值 → 生产键）

生产键取自 `FufucodeTheme.axaml` 的 `App*` 语义键；Fluent 列即当前交付基调，Metro/Material 3 列是本次对比样稿取值。

| 语义 | 生产键 | Fluent 亮 / 暗（现状） | Metro 亮 / 暗 | Material 3 亮 / 暗 |
|---|---|---|---|---|
| 窗口底 | `AppWindowBackground` | `#F3F3F3` / `#202020` | `#FFFFFF` / `#1A1A1A` | `#FEF7FF` / `#141218` |
| 侧栏 | `AppSidebarBackground` | `#EEEEEE` / `#1C1C1C` | `#F4F4F4` / `#101010` | `#F3EDF7` / `#211F26` |
| 内容面 | `AppContentBackground` | `#F9F9F9` / `#282828` | `#FFFFFF` / `#1F1F1F` | `#FEF7FF` / `#141218` |
| 卡片 | `AppCardBackground` | `#FBFBFB` / `#2C2C2C` | `#FFFFFF` / `#262626` | `#F7F2FA` / `#2B2930` |
| 卡片描边 | `AppCardStroke` | `#E1E1E1` / `#1C1C1C` | `#D6D6D6` / `#3A3A3A` | `transparent`（用表面分层） / `transparent` |
| 分隔线 | `AppBorderBrush` | `#0F000000` / `#15FFFFFF` | `#D6D6D6` / `#3A3A3A` | `#E7E0EC` / `#36343B` |
| 主文字 | `AppTextPrimary` | `#E4000000` / `#FFFFFF` | `#1A1A1A` / `#F0F0F0` | `#1D1B20` / `#E6E0E9` |
| 次文字 | `AppTextSecondary` | `#9E000000` / `#C5FFFFFF` | `#4D4D4D` / `#C8C8C8` | `#49454F` / `#CAC4D0` |
| 弱文字 | `AppTextDim` | `#72000000` / `#87FFFFFF` | `#808080` / `#9A9A9A` | `#79747E` / `#938F99` |
| 强调 | `AppAccent` | `#0078D4` / `#60CDFF`（系统强调色） | `#2D89EF` / `#4DB2FF` | `#6750A4` / `#D0BCFF` |
| 强调前景 | `AppAccentForeground` | `#FFFFFF` / `#000000` | `#FFFFFF` / `#101010` | `#FFFFFF` / `#381E72` |
| 强调文字 | `AppAccentText` | `#005A9E` / `#99EBFF` | `#1E6FC4` / `#8CCBFF` | `#6750A4` / `#D0BCFF` |
| diff 加 / 删 | 生产 `Styles/*.axaml` 中**未找到** diff 令牌（仅 HTML 原型 `../workbench.css` 与内联样式有值）→ 需新增 | `#E7F5EC`/`#316347`、`#FCECEF`/`#9B4050` | `#EAF5EC`/`#1F5C2C`、`#FBE9EA`/`#94202A` | `#DDEFE3`/`#23503A`、`#F7DFE2`/`#8C1D18` |
| 悬停 / 按下 | `AppHover` / `AppPressed` | `#09000000` / `#06000000` 级 | `#EDEDED` / `#DCDCDC`（实色） | 8% / 12% 强调叠加 |
| 选中 | `AppListSelected` | 同 hover 级 | 浅蓝 `#E6F1FB` | `#EADDFF` |
| 错误 | `AppErrorBackground/Border` | `#FDE7E9` / `#C42B1C` | `#FFFFFF` / `#B5121B` | `#F9DEDC` / `#B3261E` |
| 成功 / 警告 | `AppSuccessForeground` / `AppCautionForeground` | `#0F7B0F` / `#9D5D00` | `#107C10` / `#9D5D00` | `#3F6653` / `#7A5900` |
| 终端 | `AppTerminalBackground/Foreground` | `#14171D` / `#DEE3EC` | `#1E1E1E` / `#E6E6E6` | `#1D1B20` / `#E6E0E9` |
| 强调浅底 | `AppAccentSubtle` | `#E5F1FA` / `#1B2A35` | `#D6E9F8` / `#1E3A56` | `#E8DEF8` / `#4F378B` |

> 注：Fluent 列的 `AppAccent` 生产值来自 `SystemAccentColor`，样稿固定为本机默认蓝 `#0078D4`；换肤时若保留“跟随系统强调色”，Metro/M3 需决定是否仍允许系统强调色覆盖品牌色（样稿按各自规范使用固定强调色）。

## 4. 皮肤化实现要点（样稿已验证可区分，生产需另行评估）

| 项 | Fluent（基线，保留架构） | Metro | Material 3 |
|---|---|---|---|
| 圆角 | `ControlCornerRadius=4`、`OverlayCornerRadius=8` | 全部 `0`（含页签、按钮、卡片、浮层） | 按钮/页签胶囊、卡 16、浮层 28、输入 4 |
| 层次 | 1px 描边 + 中性表面，普通卡无阴影 | 平铺色块，无阴影，仅结构线 | 色调分层表面 + `FluentFlyoutShadow` 级别 elevation |
| 选中态 | 浅填充 + 底边 2px 强调线（页签） | 整块强调色填充（直角） | secondary-container 胶囊 + 强调色文字 |
| 输入区 | 描边容器，聚焦底边强调线（生产已有 `composerUnderline`） | 2px 直角描边，聚焦变强调色 | filled 字段 + 浮动标签 + 聚焦底线（**需自定义 TextBox 模板，工作量最大**） |
| 焦点环 | Windows 双环（外黑内白） | 2px 直角环绕，无内环 | 强调色描边，沿控件圆角 |
| 字体 | Segoe UI Variable Text/Display（现状） | Segoe UI Light 大标题 | Roboto —— Windows 不自带；按 `frontend-design-guideline.md` U02「字体不打包第三方文件」，只能回退系统字体 |

已知需要额外核对（[INFERENCE]，未验证）：
- FluentTheme 的 `MenuFlyout`、`ScrollBar`、`TabItem`、`ComboBox` 模板是否硬编码圆角/阴影，从而在 `ControlCornerRadius=0` 下仍显圆角。
- Material 3 状态层与浮动标签是否需要自定义控件模板（预计是三者中改动最大的）。
- 自定义 ThemeVariant 与本仓库自测（`SelfTest/WorkbenchCustomizationSelfTest.cs`）的交互。

## 5. 明确不做

Aero、Liquid Glass：用户意见列为后续/暂缓，执行契约也写明本轮不做；样稿同样未实现，仅以禁用行占位。
