# E7-ENGINE：Avalonia 可选皮肤模块交付记录

任务：E7-ENGINE（推荐主模型 `group/auto-deepseek-v4-1-flash`，难度 L3）｜执行：真实独立 worktree 内完整实施。
本记录是本包局部 finding 与交付卡的落点；不写共享计划/进度/决策/全局失败登记。

## 1. 开工身份卡（实际核对结果）

| 项 | 值 |
|---|---|
| 工作树（唯一写入） | `C:\Users\mio\.codex\worktrees\d611\Fufucode`（detached HEAD） |
| 起点 SHA | `58a06e3cb92c95c515aa6af4b9c7662d6318c603`（docs: prioritize parallel skin teacher and Avalonia resource implementation） |
| 开工时已有改动 | 无（`git status --porcelain` 空），未继承未提交用户改动 |
| 允许范围 | 新 `src/Fufucode.App/Features/Skins/**`、`src/Fufucode.App/Styles/Skins/**`、`tests/Fufucode.App.Skins.Tests/**`、`docs/records/E7-skin-engine.md` |
| 未触碰 | `App.axaml(.cs)`、原 `Styles/*.axaml`/`Icons.axaml`、`MainWindow*`、`Settings*`、`WorkbenchAppearance` 存储、solution/csproj/SelfTestRunner/Core/Pi、共享计划/进度/决策/全局 failure-registry |
| 依赖输入 | 已接受 S1 样稿（`docs/records/S1-skins.md`、`docs/frontend-prototype/skins/TOKENS-MAPPING.md`）、`docs/tasks/E7-style-brief.md`、真实 `FluentTokens.axaml`/`FufucodeTheme.axaml`/`WorkbenchStyles.axaml` 与 `WorkbenchAppearance.cs`；教师会话在另一 docs 范围，不互相覆盖 |

## 2. 交付物（真实 Avalonia 模块，不是 HTML 原型）

| 路径 | 内容 |
|---|---|
| `src/Fufucode.App/Features/Skins/SkinCatalog.cs` | `SkinId`（Fluent/Metro/Material3）、`SkinDefinition`（稳定键/描述/资源 URI/是否跟随系统强调色）、`SkinResolution`、`SkinCatalog.TryParse/Resolve`（未知键解释性回退 fluent，保留原始请求文本） |
| `src/Fufucode.App/Features/Skins/SkinLease.cs` | `SkinLease.Apply(Control, SkinResolution, ThemeVariant)` 与幂等 `Dispose()`；`SkinHostExtensions.ApplySkin(...)` 便捷入口 |
| `src/Fufucode.App/Styles/Skins/Metro.axaml` | Metro 令牌（Light/Dark ThemeDictionaries，具体值，含 App*/Fluent 桥接/形状/焦点/字体/阴影/新语义键） |
| `src/Fufucode.App/Styles/Skins/MetroStyles.axaml` | Metro 限定控件样式（直角、平面、整块强调色选择态、直角焦点环） |
| `src/Fufucode.App/Styles/Skins/Material3.axaml` | M3 令牌（固定强调色 #6750A4/#D0BCFF、色调分层、状态层、角/阴影） |
| `src/Fufucode.App/Styles/Skins/Material3Styles.axaml` | M3 限定控件样式（胶囊按钮/页签、secondary-container 选择态、16/28 圆角容器、圆角焦点环） |
| `tests/Fufucode.App.Skins.Tests/**` | 独立测试工程（真实无头 Window + 真实编译控件，Skia 截帧）；`evidence/` 6 张实测渲染帧 |

`src` 新文件自动进入既有 App 工程（未改 csproj）；测试工程未注册 solution/全量自测，独立 `dotnet run` 入口。

## 3. 接口与接线卡（M 用）

### 3.1 在哪里 new / apply / reapply / theme / 浮动窗口 / dispose

```csharp
// 解析持久值（未知键不回写、不抛异常，返回 FellBack + Reason）
var resolution = SkinCatalog.Resolve(persistedSkinKey);      // "fluent" | "metro" | "material3"(兼容 m3/material-3)
// 应用：窗口级（推荐，顶层接管变体）
var lease = SkinLease.Apply(window, resolution, ThemeVariantFor(theme));  // theme: light|dark|system→Light/Dark
// 或 window.ApplySkin(SkinId.Metro, ThemeVariant.Dark)
```

- **reapply/换肤**：先 `lease.Dispose()` 再 `Apply` 新皮肤（实测 dispose 后属性/资源回到 Fluent 基线，无积累）。
- **亮暗变化**：同一租约下直接改 `TopLevel.RequestedThemeVariant`（实测热切换，重挂资源不是必需）。
- **新浮动窗口**：每个 Window/浮动容器单独持有自己的租约；不要跨窗口共享租约实例。Control 目标（子树/面板）只追加自身 `Resources.MergedDictionaries` 与 `Styles`，不向窗口注入；Control 不拥有变体作用域，请求与宿主不一致时 `VariantApplied=false` + `VariantNote` 如实报告。
- **dispose/recover**：`Dispose()` 幂等，只按引用移除本租约加入的字典/样式并恢复本租约改过的 `RequestedThemeVariant`；不清空、不重建别人的 `Resources`/`Styles`（哨兵用例实测保留）。恢复 Fluent = 应用 Fluent 租约（不追加覆盖）或直接释放当前租约。
- **Fluent 是默认**：`SkinCatalog.Resolve(null/未知)` 回退 `fluent`；本包不改默认，不等用户选新默认。

### 3.2 M 需要补消费的键（本包已定义，尚未接生产）

- 状态语义：`AppFocusBrush`、`AppDisabledForeground`、`AppSelectionForeground`、`AppSuccessBackground`、`AppCautionBackground`、`AppCriticalForeground`（已有）、`AppDiffAddedBackground/Foreground`、`AppDiffRemovedBackground/Foreground`。
- 形状：`AppCornerCard`/`AppCornerInput`/`AppCornerPill`/`AppCornerFlyout`（新增）；`ControlCornerRadius`/`OverlayCornerRadius` 已被原样式消费。
- 排版：`AppFontUi`/`AppFontDisplay`/`AppFontMono`、`AppFontSizeBase/Secondary/Section/Page`、`AppFontWeightStrong`。
- 阴影/焦点：`AppShadowFlyout/Toast/Pill`、`AppFocusThickness`、`AppFocusOffset`。
- 持久化与外壳（M 核查更正）：`WorkbenchAppearance.Surface` 是既有材质字段，已有透明级别与局部样式消费者；S1-1 的旧“无消费者”假设不适用当前源。皮肤用独立 `Skin` 字段解析到 `SkinCatalog`，保留原 Surface 配置；本包未改存储，公共接线由M。
- 需要教师确认的项：Metro 大标题字重/字体（本包用 Segoe UI Light 样本）、M3 无 Roboto 的系统回退观感（S1-5）、各皮肤 elevation 阴影档位。

### 3.3 覆盖率边界（M 需要知道）

- 应用样式/视图里仍存在 `{StaticResource ControlCornerRadius}`（`ChatPage.axaml:243,481`、`ExtensionsPage.axaml:58,222`、`ProjectTrustDialog.axaml:49,52`、`TerminalDockView.axaml:9`）与 `{StaticResource AppFontFamily/AppMonoFont}`：这些在加载时静态解析，窗口级皮肤不改变它们；本包用限定样式显式覆盖同类工作台类（settingsRow/card/composerCard/toolCard/overlayPanel/toast 等）保证皮肤实际生效。若要全量覆盖，M 需把对应引用改为 `DynamicResource`（本包未改原文件）。
- 遇到复杂内置 `ControlTheme` 只给限定样板：模板部件状态的覆盖要使用带状态的 `/template/` 选择器（实测反例：`TabItem /template/ Border#PART_SelectedPipe` 通用选择器被 ControlTheme 的选中态压过，改为 `TabItem:selected /template/ ...` 才生效）；未批量复制 Fluent 模板，未重写 ComboBox popup/浮动标签模板。

## 4. 验证命令与实际结果

```text
dotnet build src\Fufucode.App\Fufucode.App.csproj -c Release -t:Rebuild
  → 已成功生成，0 个警告，0 个错误
dotnet build tests\Fufucode.App.Skins.Tests\Fufucode.App.Skins.Tests.csproj -c Release -t:Rebuild
  → 已成功生成，0 个警告，0 个错误（WebView2 传递的 WPF/WinForms 引用按 O3 既有实践剥离，未用 NoWarn）
dotnet run --project tests\Fufucode.App.Skins.Tests -c Release --no-build
  → 全部通过：120 项断言，exitCode 0
```

120 项断言覆盖（均为真实无头 Window + 真实编译控件/真实控件属性读取）：

- 目录解析/别名/未知键解释性回退且保留原始请求；Fluent 不携带覆盖资源。
- Metro 亮/暗、M3 亮/暗：`AppWindowBackground`/`AppAccent`/`ControlCornerRadius`/新语义键解析值、真实 Button/TextBox/ComboBox/ListBox 选中项/TabItem 指示条/Border card/settingsRow/overlayPanel/toast 属性逐一断言。
- 亮暗热切换不改租约；`:pointerover` 由真实无头指针输入触发；键盘 Tab 导航真实落入焦点。
- 连续 3 轮 Fluent→Metro→M3→Fluent：字典/样式计数每步基线+1、结束回基线（无泄漏/积累），控件属性回到 Fluent 基线。
- 双窗口（Metro 暗 vs M3 亮）令牌/形状/按钮互不污染；一窗释放不影响另一窗。
- 哨兵资源/样式在皮肤 apply+dispose 后保留（只清自身）；重复 Dispose 幂等。
- Control 子树作用域：子树按钮换肤、同窗子树外按钮保持 Fluent；释放后回基线；换肤不改动控件树/内容（业务 VM 不受影响）。
- Control 目标请求变体与宿主不一致时如实报告（`VariantApplied=false`）。
- 两皮肤 × 明暗语义令牌齐备 + WCAG 对比度：主文字 ≥ 4.5（实测 14.35–17.40）、次文字 ≥ 3.0（实测 8.45–10.91）。

截图（`tests/Fufucode.App.Skins.Tests/evidence/`，`CaptureRenderedFrame` 实测帧，非 HTML）：

| 文件 | 说明 |
|---|---|
| `metro-light-1100x800.png` / `metro-dark-1100x800.png` | Metro 亮/暗：直角平面、整块蓝色选择态、方形页签指示条 |
| `material3-light-1100x800.png` / `material3-dark-1100x800.png` | M3 亮/暗：胶囊按钮、secondary-container 选择态、16/28 圆角容器、Toast 阴影 |
| `material3-light-800x640.png` | 800×640 窄窗覆盖一组 |
| `metro-dark-800x640-scale125.png` | 125% 工装缩放帧（SetRenderScaling，仅工装近似） |

已对这些帧逐张目视检查：Metro 与 M3 在圆角、选择容器、按钮形态、阴影、页签指示条上可区分；亮暗对比可读；无空白/错位/错误文本。

## 5. 本任务内 findings（不写全局表中）

| # | 现象与证据 | 处理 |
|---|---|---|
| E7-1 | 应用样式/视图存在 `{StaticResource ControlCornerRadius}` 等静态引用（见 3.3） | 本包以限定样式显式覆盖工作台类保证生效；全量覆盖建议 M 改 DynamicResource |
| E7-2 | `MenuFlyoutPresenter` 无 `BoxShadow` 属性（AVLN2000 编译失败反例） | 阴影改由 `Border.toast`/`overlayPanel` 与令牌承接；菜单阴影需模板级方案（未做） |
| E7-3 | TabItem 指示条：通用 `/template/` 选择器被内置选中态覆盖（实测管道颜色回落 #0078D7/ControlCornerRadius） | 改为 `TabItem:selected /template/ Border#PART_SelectedPipe` 后生效（Metro/M3 均实测） |
| E7-4 | 禁用 Button 的 `Foreground` 属性仍是启用色，真实状态在模板呈现层（`ContentPresenter.Foreground=#9A9A9A`） | 断言修正为读取模板呈现层，按真实渲染面验证 |
| E7-5 | 程序化 `Focus()` 不足以出现键盘焦点环观感 | 关键帧改用键盘 Tab 导航（IsFocused 实测）；焦点环样式已定义，像素级视觉未逐张比对 |

## 6. 未运行验证与原因

- 真实系统 DPI/缩放（125%/150% 原生窗口）、原生指针/输入法/浮动 HWND/多显示器：无头工装不覆盖，`SetRenderScaling` 仅工装近似。
- 浏览器（WebView2）页面内主题、Mica 真机透出与皮肤叠加：需原生窗口，未验。
- M3 复杂组件模板（ComboBox popup、浮动标签、状态层动效）未重写；未批量复制 Fluent 模板。
- 主外壳接线（设置项、`Surface` 持久化/迁移、浮动窗口租约、全量自测注册）与整项产品验收：由 M 完成；本包不声称完整皮肤生产可用。
- 未读写真实 `ui.json`/`Desktop.db`（模块无 IO）；未 push/同步 main/清理工作树。

## 7. 交付卡

```text
交付卡
任务ID与软件：E7-ENGINE Avalonia 可选皮肤模块｜Codex 执行会话（本 worktree）
工作树绝对路径、分支/HEAD、起点SHA：
  C:\Users\mio\.codex\worktrees\d611\Fufucode，detached HEAD，起点 58a06e3cb92c95c515aa6af4b9c7662d6318c603
本任务提交SHA（多个提交注明范围）：
  ed7f99d Features/Skins：SkinCatalog + SkinLease
  d6d535e Styles/Skins：Metro/Material3 令牌与限定样式（4 文件）
  7908409 tests/Fufucode.App.Skins.Tests：独立无头测试 + 6 张实测渲染帧
  随后一次提交仅含 docs/records/E7-skin-engine.md（本记录）
改动文件、继承用户改动的保护情况：
  仅新增上述允许路径；未改 App.axaml/原 Styles/Icons/MainWindow/Settings/WorkbenchAppearance 存储/solution/csproj/SelfTestRunner/Core/Pi/共享计划进度决策；开工工作区干净，未夹带
实际验证命令、结果、证据位置：
  App Rebuild 0 警告 0 错误；测试工程 Rebuild 0 警告 0 错误；dotnet run 独立入口 120 项断言全过（exit 0）；
  截图 evidence/ 6 张（两条皮肤×亮暗、800×640、125% 工装）已逐张目检
未运行验证与原因：
  原生 DPI/指针/IME/浮动 HWND/WebView2/Mica 透出——无头工装不覆盖；M3 复杂组件模板与全量自测——属 M 接线与本包范围外
公共接线需求、接口/入口/依赖：
  SkinCatalog.Resolve + SkinLease.Apply/Dispose（见 §3）；M 用独立 WorkbenchAppearance.Skin 和设置项、逐窗口持有租约，保留旧 Surface 消费；补消费 §3.2 新键；
  教师待确认字体/阴影档位
状态：模块交付待接线（M 接组合根/设置/持久化后方可整项验收）
原会话是否仍在写代码：否（本包结束，不自行集成/不开始下一任务）
```
