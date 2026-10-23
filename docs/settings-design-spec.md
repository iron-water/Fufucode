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
        ├─ 页标题行：Button.crumbLink (22px次级) + TextBlock.breadcrumb (22px次级 "›") + TextBlock.settingsPageTitle (22px SemiBold)
        └─ ScrollViewer → StackPanel (MaxWidth 880, Spacing 0)
            └─ 页面分区 StackPanel (Spacing 0)   ← 每个页面一个分区，由 IsVisible 切换
                ├─ TextBlock.settingsGroupHeader (14px SemiBold, Margin 0,32,0,10)
                │   └─ .first 变体：Margin 0,0,0,10（页首组无需上间隔）
                ├─ StackPanel.settingsGroupList (Spacing 8)   ← 唯一规范组容器
                │   └─ Border.settingsRow ×N
                └─ Border.settingsPanel / .subtle             ← 非行式说明卡
```

**迁移期兼容**：旧页面仍在用 `Border.settingsGroup > StackPanel`。它只提供同样的 8px
行距，不得在新代码中使用；新页面统一 `StackPanel.settingsGroupList`，由
`SettingsLayout.Group()` 生成。

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
| 面包屑父级链接 | 22 SemiBold | `crumbLink`（AppTextSecondary→pointerover 主色）|
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
| 组间（有标题） | 8 (`settingsGroupList` 尾部) + 32 (`settingsGroupHeader` 上边距) |
| 无标题组/跨组下钻入口 | 与普通行相同 8，禁止额外组级 margin |
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

### 6.1 Fluent 暗色用色

Fluent 暗色只允许三档底色（`AppBaseDeep/AppBaseBar/AppBaseHighlight`）、一种主题色（`AppAccent`）和一种模型输出强调色（`AppEmphasis`）。三档底色各只有三个备选；主题色/强调色可任选。图标、diff 红绿、代码高亮、终端和状态色属于已声明例外。详见 [color-principles.md](design/color-principles.md)。

## 7. 侧栏三级架构

目录：`SettingsNavCatalog.All`（Basic → Features → Other 顺序）。

- **Basic**（基础类）：外观/关于/网络/键盘/模型/储存——固定一级，`CanMove=false`
- **Features**（大功能）：插件市场/浏览器/终端/文件管理/自动开发——默认一级，`Demoted` 后进"其他"
- **Other**（小功能）：通知/配置文件/用量/工作台模块/会话与消息——默认收"其他"占位项，`Promoted` 后升一级

"其他"是一级占位项 `OtherHubId="other"`，点开后内容区列 `OtherHubItems` 二级卡（`settingsRow clickable` + 图标 + 标题 + ChevronRight）。点二级卡只切 `SelectedCategory`，**不往 FilteredCategories 塞**（避免误升级）；侧栏高亮回退到"其他"占位项。

### 7.1 功能状态页

`terminal`、`files`、`automation` 不是可编辑设置页，而是**功能状态页**：

- 状态来自宿主真实事实（插件页/控制器是否存在、内建页是否注册），不写“尚未接线”。
- 只提供一个真实入口（打开终端/文件/自动开发）和两个配置入口（插件市场、工作台模块）。
- 状态由 `WorkbenchCustomizationViewModel.FeatureStatus` 注入；页面不自行判断插件状态。
- 插件页 ID 与内建页壳冲突时，内建注册优先；插件贡献不得删除 core/optional 页注册。

### 7.2 页面位置目录

“前端功能排布 → 页面位置”的行来自宿主注册表（`WorkbenchCustomizationViewModel.Pages`），不维护第二份硬编码页面清单。插件页启用后自动出现，停用后由布局裁剪移除；`ui.json` 解析不得丢弃插件页 ID。

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

---

## 10. 硬性纪律（新增/修改设置页必须满足）

以下条目是代码审查的阻断项，不是建议。违反其中任意一条，设置页改动不得合入。

### 10.1 单一构件来源

1. 所有“设置条目”必须经 `SettingsLayout.Row / ToggleRow / ChevronRow` 构建，或使用
   `Border.settingsRow` 的标准行内结构。禁止在页面里另画一套行卡。
2. 所有分组必须经 `SettingsLayout.Group` 或 `StackPanel.settingsGroupList` 构建。
   `Border.settingsGroup` 只允许在迁移期保留，禁止新增。
3. 所有页面内容必须由 `WorkbenchSectionsView` 按页面 id 渲染，或使用
   `Border.settingsPanel` 承载非行式信息。禁止页面级自建“卡片类”。

### 10.2 页面分区与节奏

4. 一个页面 = 一个 `StackPanel` 分区（`Spacing="0"`），用 `IsVisible` 切换；不得把多个
   页面的组直接平铺在同一个 `Spacing` 容器里。
5. 每个分区的第一个组标题必须带 `.first`；无标题首组也必须消费“页首”状态
   （`WorkbenchSectionsView.Group` 已处理，禁止绕开它直接 `rows.Children.Add` 组）。
6. 组内行距固定 8；组头上边距 32（首个 0）；无标题组和跨组下钻入口
   同样是 8，不得手工加组级 top margin。禁止出现 6/10/16/20/24/28 混用。
7. 页面内容区外边距只允许：`settingsContentHost` 的 24,20,20,24 与根 StackPanel 的
   `MaxWidth=880`。其他数值必须来自 token。

### 10.3 样式集中

8. `SettingsPage.axaml` 不得包含 `<UserControl.Styles>`。所有设置样式集中在
   `Styles/WorkbenchStyles.axaml` 的 `settings*` / `nav*` 段。
9. 视图里禁止裸 hex 颜色、禁止内联 `CornerRadius` 数值、禁止内联 `FontSize`
   （侧栏 14px 是唯一既有例外）。圆角一律 `ControlCornerRadius` /
   `OverlayCornerRadius` / `AppCornerCard`。
10. 禁止内联 `Background` / `BorderBrush` / `Padding` 去“修一下”某个卡片；
    应新增或使用语义类（`settingsPanel`、`settingsPanel.subtle`、`badge`、`innerWell`）。
11. `DynamicResource` 只能引用已存在的键。`App*` 与 `Icon.*` 键必须在主题/图标
    资源里有定义；不得出现 `AppDivider` 这类拼写漂移。

### 10.4 字号与状态

12. 字号只用阶梯：22（页标题/面包屑）、14（组标题/侧栏/行标题）、12（说明/meta）。
    不得出现 13/15/16/18 等中间值。
13. 状态色只用 token：`AppTextPrimary/Secondary/Dim`、`AppAccent`、
    `AppCriticalForeground`、`AppSuccessForeground`、`AppWarningForeground`。
14. 行卡只有三种形态：普通、`.compact`（无说明）、`.clickable`（可点击/可下钻）。
    禁止第四种“看起来像行卡但不是行卡”的容器。

### 10.5 导航与能力

15. 分类只能通过 `SettingsNavCatalog.All` 注册；新增条目必须同时给出
    `Tier`、`SearchTerms`、`Icon`、`IconColor`，否则视为未完成。
16. 新增页面必须有稳定 id，并在 `SettingsPageViewModel` 的分页映射与
    `WorkbenchSectionsView` 的 `switch` 中各出现一次；禁止只做其中一半。
17. 每个页面必须支持搜索：能命中标题或 `SearchTerms`；不匹配时隐藏整组，
    不得留下空组头。
18. 能力门禁页面（插件/后端/外部服务）必须显示真实状态与不可用原因，
    不得用假开关或占位成功文案。
19. 功能入口分类（终端/文件/自动开发/用量）必须显示宿主真实状态并提供真实入口；
    禁止恢复“尚未接线”占位页。
20. 页面位置行必须来自宿主注册表（`WorkbenchCustomizationViewModel.Pages`）；
    禁止在设置页硬编码页面清单。
21. 每个注册动作必须有 `WorkbenchActionDefaults.ExplicitIcon` 显式图标；
    自测发现 `null` 即失败，禁止静默回退 `AppsList`。
22. 插件页注册必须与内建页壳分离：插件注册失败或 ID 冲突只能记录原因，
    不得删除 core/optional 页注册。

### 10.6 自测与守卫

23. 每个新增设置页至少增加一条断言：页面可被选中、关键行存在、关键控件可交互。
24. `tests/Fufucode.App.Settings.Tests` 的 `SettingsStyleTests` 必须通过：
    它检查裸 hex、内联样式、`App*`/`Icon.*` 资源键存在性、禁用类名与结构漂移。
25. 修改设置页后必须运行：
    - `dotnet build Fufucode.sln -c Debug`
    - `dotnet run --project tests/Fufucode.App.Settings.Tests`
    - `dotnet run --project tests/Fufucode.App.Backend.Tests`

### 10.7 审查清单（复制到 PR/交付卡）

- [ ] 行全部经 `SettingsLayout` 或标准 `settingsRow` 构建
- [ ] 组全部是 `settingsGroupList` 或迁移期 `settingsGroup`
- [ ] 首个组标题带 `.first`，无标题首组也消费页首状态
- [ ] 无 `<UserControl.Styles>`、无裸 hex、无内联圆角/字号
- [ ] 所有 `App*`/`Icon.*` 资源键存在
- [ ] 页面 id 在导航、VM、渲染 switch 三处一致
- [ ] 搜索词已登记，空结果不残留组头
- [ ] 能力门禁显示真实状态
- [ ] 功能状态页有真实入口，无占位页
- [ ] 页面位置来自宿主注册表，插件页不被丢弃
- [ ] 所有注册动作有显式图标
- [ ] 定向测试通过

### 10.8 已知且允许的例外

- QR 配对卡的固定白底与黑色文字（扫码可读性），必须保留注释。
- `Grid.settingsControlRow` 的 `RowSpacing` 与
  `Border.settingsContentHost ItemsControl` 的 ItemsPanel：属于布局钩子，
  已集中在 `WorkbenchStyles.axaml`。
- `ComboBox.Styles` 内针对单项 `IsEnabled` 的绑定覆盖（思考等级能力），
  属于唯一允许的模板级内联样式。
