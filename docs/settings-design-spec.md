# 设置页设计规范

对齐目标：所有设置页样式收敛到语义 `App*` token 与 `settings*`/`nav*` 类族，不自创、不突兀。本规范是当前实现的对账基线；新功能页接入时按本规范取用，不要新起类名。

## 1. 层级模型

```
UserControl (AppMicaPageBackground = AppWindowBackground → SolidBackgroundFillColorBaseBrush)
├─ 页头 Grid (Margin 16,10,16,10)
│   ├─ Button.iconButton.toolButton 返回
│   ├─ TextBlock.settingsPageTitle "设置"
│   └─ TextBlock.meta 搜索摘要
│
└─ SettingsBody Grid (ColumnDefinitions 340,* / Margin 12,0,12,0 / ColumnSpacing 8)
    ├─ Border.settingsNavHost (Transparent, Padding 0,0,8,0)
    │   ├─ TextBox 搜索框 (MinHeight 36, CornerRadius ControlCornerRadius, Margin 4,4,4,10)
    │   └─ ListBox.settingsNav
    │       └─ ListBoxItem (MinHeight 40, CornerRadius 4, Margin 0)
    │           ├─ Border.navFill (Transparent→AppListHover / AppListSelected, BrushTransition 120ms)
    │           ├─ Border.navAccent (3×20, CornerRadius 2, AppNavAccentBrush, Margin 4,0,0,0)
    │           └─ Grid (Auto,* / ColumnSpacing 14 / Margin 12,0,8,0)
    │               ├─ SemanticIcon 20×20 (CategoryColor)
    │               └─ TextBlock 14px, AppTextPrimary
    │
    └─ Border.settingsContentHost (Transparent, Padding 24,20,20,24)
        ├─ 页标题行：Button.crumbLink (20px次级) + TextBlock.breadcrumb (22px次级 "›") + TextBlock.settingsPageTitle (22px SemiBold)
        └─ ScrollViewer → StackPanel (MaxWidth 880)
            ├─ TextBlock.settingsGroupHeader (14px SemiBold, Margin 0,28,0,10)
            │   └─ .first 变体：Margin 0,0,0,10（页首组无需上间隔）
            └─ Border.settingsGroup (Transparent, Margin 0,0,0,16 —— 纯间距容器，不是卡)
                └─ StackPanel.settingsGroupList (Spacing 8)
                    └─ Border.settingsRow ×N
```

## 2. 行卡（settingsRow）

所有"设置条目"用 `settingsRow` 卡片，不要另起 `card`/`automationCard` 等平行卡类。

- `Border.settingsRow`：`AppCardBackground` + `AppCardStroke` + 1px + `ControlCornerRadius` + `Padding 18,12` + `MinHeight 64`，BrushTransition 83ms
- `.compact`：`MinHeight 52` `Padding 18,8`（无描述行时用）
- `.clickable`：Cursor Hand；`:pointerover` → `AppListSelected`
- `.last`：组内末行 `Margin 0`（清掉组间距）
- `Button.settingsRow`：同款卡面 + `HorizontalAlignment Stretch` + `HorizontalContentAlignment Stretch`（点击行）

行内结构固定：

```xml
<Grid Classes="settingsControlRow" ColumnDefinitions="Auto,*,Auto" ColumnSpacing="14">
  <PathIcon Grid.Column="0" Width="18" Height="18" VerticalAlignment="Center"/>
  <StackPanel Grid.Column="1" Spacing="2" VerticalAlignment="Center">
    <TextBlock Classes="settingsRowTitle"/>   <!-- 14px AppTextPrimary -->
    <TextBlock Classes="settingsRowDesc"/>    <!-- 12px AppTextSecondary -->
  </StackPanel>
  <Control Grid.Column="2"/>                  <!-- ComboBox/ToggleSwitch/Button -->
</Grid>
```

行内控件已定制：`settingsRow TextBox` 用 `AppInnerWell` 无边框、`settingsRow ComboBox` 用 `CardBackgroundFillColorSecondary` 无边框 MinHeight 32、`settingsRow ToggleSwitch` MinHeight 20 FontSize 12。

## 3. 字号阶梯

| 用例 | FontSize | 类/资源 |
|---|---|---|
| 页面大标题 | 22 SemiBold | `settingsPageTitle` |
| 面包屑分隔符 | 22 | `breadcrumb`（AppTextSecondary）|
| 面包屑父级链接 | 20 | `crumbLink`（AppTextSecondary→pointerover 主色）|
| 组标题 | 14 SemiBold | `settingsGroupHeader` |
| 侧栏项/行标题 | 14 | 侧栏直接字号 / `settingsRowTitle` |
| 行说明/meta | 12 | `settingsRowDesc` / `caption` / `meta` |
| 输入框 | Fluent 默认 | 不重写 |

## 4. 圆角阶梯

| 用途 | 值 | token |
|---|---|---|
| 侧栏项 | 4 | 字面量（item 级）|
| 输入框/小控件 | 4 | `ControlCornerRadius` |
| 行卡 | 4 | `ControlCornerRadius` |
| 弹出/覆盖层 | 8 | `OverlayCornerRadius` |
| 卡片（Border.card 通用）| 6 | `AppCornerCard` |
| Badge/Chip | 10 | `Border.badge` |

## 5. 间距阶梯

| 用途 | 值 |
|---|---|
| 微缝 | 2, 4 |
| 组内项间距 | 8 (`settingsGroupList` Spacing) |
| 行内标题↔说明 | 2 (StackPanel Spacing) |
| 行内列间距 | 14 (`settingsControlRow` ColumnSpacing) |
| 组间 | 16 (`settingsGroup` Margin 底) |
| 页内缩 | 24,20,20,24 (`settingsContentHost` Padding) |

## 6. 状态色

| 状态 | token | 应用 |
|---|---|---|
| 悬停 | `AppListHover` | navFill pointerover、行卡 pointerover |
| 选中 | `AppListSelected` | navFill selected、行卡 clickable:pointerover |
| 侧栏 accent 条 | `AppNavAccentBrush` | navAccent（深色 #4CC2FF / 浅色 #0067C0）|
| 错误 | `AppCriticalForeground` / `AppErrorBorder` | errorCard |
| 成功 | `AppSuccessForeground` | 复制状态、已启用 |
| 警告 | `AppWarningForeground` / `AppCautionForeground` | 中断通知 |

## 7. 侧栏三级架构

目录：`SettingsNavCatalog.All`（Basic → Features → Other 顺序）。

- **Basic**（基础类）：外观/关于/网络/键盘/模型/储存——固定一级，`CanMove=false`
- **Features**（大功能）：插件市场/浏览器/终端/文件管理/自动开发——默认一级，`Demoted` 后进"其他"
- **Other**（小功能）：通知/配置文件/用量/工作台模块/会话与消息——默认收"其他"占位项，`Promoted` 后升一级

"其他"是一级占位项 `OtherHubId="other"`，点开后内容区列 `OtherHubItems` 二级卡（`settingsRow clickable` + 图标 + 标题 + ChevronRight）。点二级卡只切 `SelectedCategory`，**不往 FilteredCategories 塞**（避免误升级）；侧栏高亮回退到"其他"占位项。

## 8. 不自创样式清单

**允许新增的**：
- 新语义 token（`App*`，须同时给 Dark 和 Light 值）
- `settings*`/`nav*` 下新类（须在 WorkbenchStyles.axaml 集中定义，不进 `UserControl.Styles`）

**禁止的**：
- 视图 axaml 里内嵌 `<Style>`（除 `UserControl.Styles` 的极少量行内覆盖）
- 裸 hex 字面量（除 QR 卡这种固定白底例外，须注释说明）
- 平行卡类（`automationCard`/`customRow` 等）——一律用 `settingsRow`/`Border.card`
- 页面级命名空间类（`settingsXxx` 不该出现在非设置页视图）

## 9. 已知例外

- **QR 配对卡**：`Background="White"` 固定白底（扫码可读性），内部文字 `Foreground="Black"` + `Opacity 0.8/0.6`。这是页面唯一固定反色区，不动态主题化。
- **`settingsPathRow` Grid 类**：是 `UpdateResponsiveLayout` 的布局钩子（code-behind 读类名重排），不是样式类，无 selector 正常。
- **侧栏 15px SemanticIcon**：compact 模式（`settingsNav compactBar`）折叠态用小图标，仅此一处。
