# R04 保持布局的三种视觉风格探索

日期：2026-10-05。认领者：Codex /root（Codex CLI / GPT-5，Windows x64）。
范围：只交付研究；不改产品代码、不提交、不推送、不安装依赖、不动 `reference/`。

## 0. 结论摘要

在用户已认可的"中央会话 + 左/右/底三通用停靠区"布局之上，三种风格都可以用同一套 Avalonia 样式 token 实现，**不需要改布局结构或控件集**。结论：

- **推荐默认 / 首选增量：液态玻璃（Liquid Glass，Acrylic+Mica 取向）**。最贴近当前 Fluent 倾向与用户"简洁"偏好；可直接复用 `src/Fufucode.App/Styles/MicaOverrides.axaml` + `MainWindow.axaml.cs` 中已有的 `ApplyMicaOverrides`/`ActualTransparencyLevel` 监听机制，改造成本最低、回退路径最清晰。
- **次选：Windows 8 纯平面**。实现最简单（无模糊、无渐变、无阴影），Avalonia 侧几乎零风险；但与现有 Fluent/Mica 倾向距离最远，视觉差异最大，适合作为"高对比/低装饰"可选主题而不是默认。
- **不建议作为默认：拟物（Win7 Aero / 早期 iOS）**。Avalonia 中可用 `LinearGradientBrush` + `BoxShadows` + 双层 `Border` 实现，但视觉密度与"年代感"和工具型产品定位冲突，深色系最易翻车；可作为皮肤扩展点探索。

> 最终选型仍由用户决定；本报告不构成正式架构决定。

## 1. 共同前提与约束

- 布局不变：顶栏 / 左停靠 / 中央会话 / 右停靠 / 底停靠 / 状态栏；≤880px 时左/右区转绝对定位抽屉（与 frontend-prototype 一致）。
- 控件集合不变：顶栏搜索/通知/设置、停靠区标签与关闭、会话头、摘要卡、消息（用户气泡 / 助手回答 / 操作条 / 代码块 / 表格）、主输入区（模型 + 思考等级、上下文条、发送）、终端页、侧栏提问。
- 信息密度不变：字号、行高、栅格间距沿用 `frontend-prototype` 现值（11/12/13px 梯度）。
- 现有默认外观是基线，三套风格仅是"可切换皮肤"，不替换默认。

## 2. 候选方案表

| 维度 | A 液态玻璃 | B 纯平面 (Metro) | C 拟物 (Aero/早期 iOS) |
|---|---|---|---|
| 材质 | 半透明面板 + 系统级合成模糊（Mica/Acrylic）；浅色近磨砂白，深色近墨黑 | 不透明纯色块，主题色仅用于重点 | 线性渐变 + 内/外阴影模拟高光与凹槽 |
| 边界 | 1px 玻璃描边 + 大圆角（14–20px） | 1–2px 实色描边，圆角 0 | 双层描边（高光 + 投影），4–8px 圆角 |
| 层级 | 面板浮起（柔影 + 背景透视） | 全靠色块分割，无投影 | 浮雕凸起；输入区内凹 |
| 图标 | Lucide 描边即可；半透明下靠 --fg 保证对比 | Lucide 最佳；色彩仅留给状态与主操作 | 图标需更厚重；可加 text-shadow/描边在浮雕上站稳 |
| 交互反馈 | hover=加深一层玻璃（--hover）；focus=--accent 描边 | hover=相邻色块；active=主题色反色 | hover=提亮高光；按下=内凹反相 |
| 无障碍 | 必须提供"降低透明度/减弱动效"回退；对比度实测 | 最稳；无模糊无渐变，最易过对比 | 最易踩对比坑；高光让对比失真，需严格验证 |
| 实现成本（相对现有 Mica） | 中：扩展 token + Acrylic 面板 + 回退 | 低：仅换 SolidColorBrush + CornerRadius=0 | 中高：每个表面要 2–3 个梯度/阴影资源，约为平面 2 倍 |
| 主要风险 | Acrylic 在 Win10/部分 GPU 退化；半透明下文字可读性 | 风格跨度大，用户可能觉得"简陋" | 与"简洁"定位冲突；深色易显脏 |

## 3. 一手来源与版本证据（实测，不引用旧文档）

### 3.1 本仓库现状（本次实测）

- `src/Fufucode.App/Fufucode.App.csproj`：`Avalonia`/`Avalonia.Desktop`/`Avalonia.Themes.Fluent`/`Avalonia.Fonts.Inter`/`Avalonia.Headless` 均为 **12.1.3**；`net10.0`。
- `src/Fufucode.App/Views/MainWindow.axaml`：`Background="{DynamicResource AppMicaPageBackground}"`；`Width=1440 Height=900 MinWidth=800 MinHeight=640`；`WindowDecorations="BorderOnly"`。
- `src/Fufucode.App/Views/MainWindow.axaml.cs`：`PropertyChanged` 中监听 `ActualTransparencyLevelProperty`，调用 `ApplyMicaOverrides()`；`ApplyMicaOverrides` 仅在 `ActualTransparencyLevel == WindowTransparencyLevel.Mica` 时合并 `avares://Fufucode.App/Styles/MicaOverrides.axaml`（注释明示参照 UniGetUI 的 MIT 实现）。
- `src/Fufucode.App/Styles/MicaOverrides.axaml`：仅定义 `AppMicaPageBackground` 与 `AppMicaSidebarBackground` 两个键；浅色 `#66FFFFFF`、深色 `#66262626`。
- 仓库内 `rg "ExperimentalAcrylicBorder|AcrylicPlatformCompensationLevels|TransparencyLevelHint"` 均无命中 → 现有 Mica 是通过 `WindowTransparencyLevel` + 自维护资源键实现的，不是 Acrylic 控件。

### 3.2 Avalonia 12.1.3 ref 程序集证据（实测）

`%USERPROFILE%\.nuget\packages\avalonia\12.1.3\ref\net10.0\Avalonia.Base.dll` / `Avalonia.Controls.dll`：

- `WindowTransparencyLevel` 枚举成员：`None`、`Transparent`、`Blur`、`AcrylicBlur`、`Mica`、`ForceTransparent`、`ForceBlur`。
- `TopLevel` 提供：`TransparencyLevelHint`、`ActualTransparencyLevel`、`TransparencyBackgroundFallback`。
- `Avalonia.Controls` 提供：`ExperimentalAcrylicBorder`、`AcrylicPlatformCompensationLevels`（12.x 仍带 `Experimental` 前缀）。
- `Avalonia.Base` 提供：`IExperimentalAcrylicMaterial`、`ImmutableExperimentalAcrylicMaterial`、`ServerCompositionExperimentalAcrylicVisual`、`Blur`、`IBlurEffect`、`ImmutableBlurEffect`。

### 3.3 Pi 基线（口径一致）

- `runtime/pi`：`git show HEAD:runtime/pi/package.json` → `"@earendil-works/pi-coding-agent":"1.0.0"`；`runtime/pi/node_modules/.../package.json` version=1.0.0。与 R03 结论一致：本任务也以 Pi 1.0.0 为基线。本任务不调用 Pi/RPC。

## 4. 三套设计 Token（可直接落到 axaml ResourceDictionary）

token 命名沿用 `App*` 前缀，与现有 `AppMicaPageBackground`/`AppMicaSidebarBackground` 共存。

### 4.1 风格 A · 液态玻璃（对应 `mockups/style-liquid.css`）

| Token | 浅色 | 深色 | 用途 |
|---|---|---|---|
| `AppGlassPageBackground` | `#71FFFFFF`（44% 白） | `#6E1C1E26` | 停靠区面板 |
| `AppGlassCardBackground` | `#99FFFFFF`（60% 白） | `#941E2028` | 摘要卡 / 浮起控件 |
| `AppGlassSoftBackground` | `#47FFFFFF`（28% 白） | `#4D181A20` | 顶栏 / 状态栏底 |
| `AppGlassStroke` | `#2EFFFFFF` | `#1FFFFFFF` | 玻璃描边 |
| `AppGlassStrokeStrong` | `#52FFFFFF` | `#3DFFFFFF` | 选中态描边 |
| `AppAccent` | `#FF0A84FF` | `#FF0A84FF` | 主强调（iOS 蓝） |
| `AppOnAccent` | `#FFFFFFFF` | `#FFFFFFFF` | 强调上文字 |
| `AppTextPrimary` | `#FF1C1E21` | `#FFECEEF2` | 正文 |
| `AppTextDim` | `#9E1C1E21` | `#9EECEEF2` | 次要文字 |
| `AppHover` | `#59FFFFFF` | `#14FFFFFF` | 悬停加深 |
| `AppCodeBackground` | `#EB14161C` | `#990A0B0F` | 代码块底（不透明） |
| `AppCodeForeground` | `#FFE8ECF1` | `#FFE8ECF1` | 代码文字 |
| `AppRadiusS/M/L` | `8 / 14 / 20` | 同左 | 圆角阶梯 |
| `AppGlassBlur` | `24`，`saturation 1.6` | 同左 | 参考值；真实 Acrylic 由系统决定 |

Avalonia 落地：

- 窗口级：`TransparencyLevelHint="Mica, AcrylicBlur, Blur"`；`TransparencyBackgroundFallback` 指向不透明实色。
- 面板级：先用半透明 `SolidColorBrush` + `Border`；`ExperimentalAcrylicBorder` 可作为后续可选增强，不建议首版全量替换。
- 阴影：`BoxShadows="0 10 32 0 #3D000000, 0 1 2 0 #1F000000"`。

### 4.2 风格 B · 纯平面（`style-flat.css`）

| Token | 浅色 | 深色 |
|---|---|---|
| `AppFlatPageBackground` | `#FFFFFFFF` | `#FF1B1B1B` |
| `AppFlatPanelBackground` | `#FFF3F3F3` | `#FF252525` |
| `AppFlatPanel2Background` | `#FFEBEBEB` | `#FF2C2C2C` |
| `AppFlatChrome`（顶栏） | `#FF0078D7` | `#FF1BA1E2` |
| `AppFlatAccent` | `#FF0078D7` | `#FF1BA1E2` |
| `AppFlatText` | `#FF1B1B1B` | `#FFEAEAEA` |
| `AppFlatTextDim` | `#FF5F5F5F` | `#FFA5A5A5` |
| `AppFlatStroke` | `#FFD6D6D6` | `#FF3A3A3A` |
| `AppFlatStrokeStrong` | `#FFB0B0B0` | `#FF4A4A4A` |
| `AppFlatHover` | `#FFE4E4E4` | `#FF333333` |
| `AppFlatBubble` | `#FF0078D7`（白字） | `#FF1BA1E2` |
| `AppFlatCode` | `#FF1E1E1E` | `#FF111111` |
| `AppRadius*` | `0 / 0 / 0` | 同左 |
| 标签 | 全大写 + `letter-spacing .4` | 同左 |

Avalonia 落地：仅 `SolidColorBrush` + `CornerRadius="0"`；无 `BoxShadows`；主题色仅用于顶栏、选中态、强调按钮。

### 4.3 风格 C · 拟物（`style-skeuo.css`）

| Token | 浅色 | 深色 |
|---|---|---|
| `AppSkeuoPanelGrad` | `#D5DCE4 → #BCC6D2` | `#33383F → #23262C` |
| `AppSkeuoBubbleGrad` | `#FDFDFE → #E8EDF3` | `#454C56 → #363B43` |
| `AppSkeuoAccent` | `#FF3A7BD5` | `#FF5B9BD5` |
| `AppSkeuoText` | `#FF1E2430` | `#FFE4E8EE` |
| `AppSkeuoTextDim` | `#FF5A6472` | `#FF9AA4B0` |
| `AppSkeuoStroke` | `#FF9AA6B4` | `#FF1C1F24` |
| `AppSkeuoStrokeStrong` | `#FF7C8899` | `#FF14161A` |
| `AppSkeuoBevelHigh` | `#B3FFFFFF` | `#24FFFFFF` |
| `AppSkeuoBevelLow` | `#26000000` | `#66000000` |
| `AppSkeuoCode` | `#FF2B2F36` | `#FF1A1D22` |
| `AppRadiusS/M/L` | `4 / 8 / 12` | 同左 |

Avalonia 落地：所有"面"用 `LinearGradientBrush`；`Border` 双层（外 1px `--stroke-strong`，内 1px `--bevel-hi`）；`BoxShadows` 用 `inset` + 外投；输入控件用 `inset` 阴影反相。

## 5. 同一视图样张

`mockups/styles-lab.html`（引用 `shared.css` + 三张风格 css）。任意现代浏览器双击打开；右上角"风格 × 主题"切换器；窗口 ≤880px 时左/右停靠自动转抽屉。

**验证证据状态**：

- 本任务运行环境未提供截图/无头浏览器能力，未产出 PNG 截图；浏览器渲染未实测。
- 1440 / 390 宽度下的视觉走查建议由用户或主 agent 在评审阶段执行，或授权后用 Playwright 出图。
- 样张中的 `backdrop-filter:blur(24px)` 只是浏览器内近似演示，**不等同真实 Acrylic/Mica**。

## 6. 真实系统材质 vs CSS blur（关键区别）

"玻璃"指**系统级合成材质**（Win11 Mica/Acrylic，由 DWM 合成桌面背景后投递给窗口）；HTML 样张中的 `backdrop-filter` 只是浏览器内近似演示。

| 项 | CSS `backdrop-filter` | Avalonia `TransparencyLevelHint=AcrylicBlur/Mica` |
|---|---|---|
| 模糊对象 | 元素后面的 DOM 内容 | 窗口后面的桌面/其他窗口像素 |
| 在 Avalonia | 不可用 | 由 `WindowTransparencyLevel` 驱动 |
| Win10 | 可用 | Acrylic 可用；Mica 不可用 |
| Win11 | 可用 | Mica/Acrylic 均可用；Mica 更省 GPU |
| 深色 | CSS 控制 | `RequestedThemeVariant` + 系统合力 |
| 风险 | 演示通过 ≠ 产品通过 | `ActualTransparencyLevel` 可能回落到 `Blur`/`None`，必须监听兜底 |

实施建议：`TransparencyLevelHint` 写 `"Mica, AcrylicBlur, Blur"`；`TransparencyBackgroundFallback` 始终不透明；`ActualTransparencyLevel` 变化时切换到对应回退字典——这正是现有 `MicaOverrides.axaml` 已有的模式，可扩为多分支（Mica / AcrylicBlur / Blur / None）。

## 7. 可读性与无障碍回退

**液态玻璃**

- 文本只在 `--glass-strong`（≥58% 不透明度）之上；`--glass-soft`（≤30%）只用于非文字密集背景。
- 代码块强制不透明 `--code-bg`，不参与玻璃。
- 提供"降低透明度"开关：开启后 `AppGlass*Background` 切到不透明等价色、`AppGlassBlur=0`。
- `ActualTransparencyLevel` 变化即触发回退字典切换；`MainWindow.axaml.cs` 已有挂点。

**纯平面**

- 最易过 WCAG；注意中文标签不要强制 `text-transform:uppercase`。

**拟物**

- 输入框 `inset` 阴影+描边与背景 ≥3:1 对比；选中态不能只靠"浮雕翻面"，需加 `--accent` 边框或底色。
- 深色拟物容易显脏；建议先把浅色做稳再决定是否提供深色。
- 拟物装饰（高光、纹理）不得压缩工作空间：本样张把高光放在 `topbar`/`composer`/`statusbar` 非内容区，中央会话区保留纯色阅读面。

## 8. 最小验证方案（建议主 agent 授权后执行）

1. Playwright/Edge headless 在 1440×900 与 390×844 下分别截 3 风格 × 2 主题共 6 张 PNG，附到 `mockups/screenshots/`。
2. Avalonia 侧：建样式探针页（不动产品代码），`TransparencyLevelHint="Mica, AcrylicBlur, Blur"` + `ExperimentalAcrylicBorder` 各放一张卡片，记录 `ActualTransparencyLevel` 实际返回值（实测，不引用文档）。
3. 可读性：玻璃风格深/浅两版上，用对比度工具核对 `--fg` vs `--glass-strong`、`--fg-dim` vs `--glass-soft` 是否 ≥4.5 / ≥3.0。
4. `ActualTransparencyLevel == None` 时验证回退字典确实生效、文字可读。

## 9. 实施影响（最小可交付增量）

1. **增量 1：token 化**。把现有 `AppMica*`、`AppCardBackground`、`AppBorderBrush` 收敛为 `docs/design/tokens.md` + `App.Themes/*.axaml` ResourceDictionary（Fluent / Liquid / Flat / Skeuo），共用同一组语义键；不改控件结构。
2. **增量 2：液态玻璃皮肤**。复用 `ApplyMicaOverrides` 挂点，扩到多 `ActualTransparencyLevel` 分支；加"降低透明度"应用设置。
3. **增量 3：纯平面皮肤**。仅换 brush + radius，无新增机制。
4. **增量 4（可选）：拟物皮肤**。先浅色，深色由用户评审。

不建议把 `ExperimentalAcrylicBorder` 作为唯一玻璃实现路径；首版先用半透明 brush + `TransparencyLevelHint`，控件级 Acrylic 作为后续可选项。

## 10. 风险

- `ExperimentalAcrylicBorder` 仍是 Experimental，API 可能再改；建议走 `TransparencyLevelHint` + 半透明 brush。
- Acrylic 在 Win10/部分 GPU 退化：必须依赖 `ActualTransparencyLevel` 回退，不能假定生效。
- 半透明+文本对比度：停靠区文本密集，建议 `--glass-strong` ≥55% 不透明度（建议值，未实测）。
- 拟物深色翻车风险。
- 三套风格并存放大维护面；建议首版只交付 Liquid + Flat，Skeuo 保持为参考样张。

## 11. 开放问题（需用户/主 agent 决定）

1. 默认外观仍 Fluent，还是把 Liquid 设为默认？（本报告默认：仍 Fluent。）
2. "降低透明度"是公开设置，还是仅作内部回退？
3. 拟物是否进入产品，还是仅保留样张？建议后者。
4. 是否要求"区/页级独立换肤"（如仅顶栏玻璃化，其它 Fluent）？本样张默认整窗口同风格。
5. 拟物需要独有图标集吗？目前沿用 Lucide 描边；若产品化建议另设图标变体。

## 12. 读过的现有文件（仅读取，SHA-256）

| 文件 | SHA-256 |
|---|---|
| `docs/frontend-prototype/index.html` | `A0D7D8BE201038F1B1D6F642B548B46660832EBE96FE73E6B9AD7A1C9C34D0C3` |
| `docs/frontend-prototype/interactions.js` | `6BA4A4B6A1EB807BDD7BBF880D2D0635458C6F6D1B454519BC200EF318B38E19` |
| `docs/frontend-prototype/lucide.min.js` | `AB85225ECC1DAA2033607856F03DD2DABCBC08E2D260A5B97E17C690AA929766` |
| `docs/design/frontend-functional-plan.md` | `6FBFFE6CCF25BFFFA7DF0BC8C6048F45BE5DD825EA4BCF02CE747CF58EAD664F` |

另读取（不展开哈希）：`src/Fufucode.App/Fufucode.App.csproj`、`src/Fufucode.App/Views/MainWindow.axaml` / `MainWindow.axaml.cs`、`src/Fufucode.App/Styles/MicaOverrides.axaml`。

## 13. 边界复核

- 写入仅限 `docs/research-results/R04/`（`claim.md`、`report.md`、`mockups/*`、本生成脚本 `_gen_report.py`）。
- 未执行 `git add/commit/branch/worktree`；`git status` 与开工时一致。
- 未修改产品代码、`reference/`、`docs/frontend-prototype/`。
- 未启动 Pi、未调用模型、未运行构建/测试。
- 本报告不构成正式架构决定；最终选型由用户决定。
