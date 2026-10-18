# UI-1 交互质感（向 open-claude-ui 学习）：交付记录

日期：2026-10-15。状态：模块交付（worktree 内实现与定向验证均通过）→ 待主控验收。
工作树：`C:/Projects/Fucode/worktrees/ui1-claude-polish`，分支 `agent/ui1-claude`，起点 `5f1ea0e`。

## 1. 任务来源与范围

用户 2026-10-15 指示（来源线程 01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07）：
“向 Caho1/open-claude-ui 学习，把交互质感压缩成可执行的令牌、常数、不变量与门禁清单，落地到 Fufucode 桌面端。”

本任务（UI-1）在该 worktree 内落地四条迁移项 + 验证与交付：

1. **单闭合边框原则**（Single Enclosing Border Principle）：视线路径上至多穿 1 层闭合边框；
   已闭合容器内的子输入框/工具栏/列表项降级为无框轻质填充，容器内结构划分只用单向 1px 发丝细线。
2. **CJK 输入法双重互锁**（IME Dual-Interlock Guard）：发送判定严格 `!(isComposing || keyCode == 229)`。
3. **4px 拖拽死区**：Tab 拖拽、分栏 Splitter、浮动页签位移 < 4px 绝不起拖；键盘增量拖动不受影响。
4. **排版微调**：时间戳、指标计数、审计/日志等套用 Tabular Numbers（`tnum`）。

**证据边界**：open-claude-ui 是**设计参考与数值来源**，非可运行库；其 Web/CSS 数值在 Avalonia 侧按本地
实现独立验证，不把上游“反编译还原”声明当实现证据。本项目唯一可运行证据见 §4。

## 2. 改动清单

### 2.1 令牌与样式真源

| 文件 | 作用 |
| --- | --- |
| `src/Fufucode.App/Styles/FufucodeTheme.axaml` | 新增 `AppDividerSubtle`（浅色 6% 黑 / 深色 6% 白发丝细线）、`AppHairlineThickness`、`AppTabularNumericFeatures`（tnum 字体特性集）；新增 `Border.nestedFill`（无框轻质填充）、`Border.innerRule`（单向 1px 细线）、`TextBlock.numeric`（等宽数字）三组样式；`mono` 锁定 tnum；composer 内 `queueStrip`/`restoreInfo` 降级无框；新增 composer 内 ComboBox 无框降级。 |
| `src/Fufucode.App/Styles/WorkbenchStyles.axaml` | `TextBlock.meta` 锁定 tnum；新增 `Border.settingsRow` 内 TextBox/ComboBox 无框降级。 |

### 2.2 单闭合边框原则（迁移项 A）

- `src/Fufucode.App/Views/ChatPage.axaml`：工具卡（`toolCard` 已闭合）内的输出区由闭合 `codeCard` 改为
  `nestedFill`（无框）；代码块头部结构划分由 `AppCodeBorder` 改为单向 `AppDividerSubtle` 细线。
- `src/Fufucode.App/Features/Automation/AutomationControlPageView.axaml`：新增 `Border.automationCardInner`
  （无框内层）、`Border.automationRule`（单向细线）与卡片内 TextBox/ComboBox 无框降级；主控对话外卡内的
  消息条目改用 `automationCardInner`。
- `src/Fufucode.App/Views/SettingsPage.axaml`（经 WorkbenchStyles 样式生效）：闭合设置行内的输入框降级无框。

### 2.3 CJK 输入法双重互锁（迁移项 E1）

- `src/Fufucode.App/Infrastructure/ImeEnterGuard.cs`：新增 `ImeProcessKeyCode = 229` 常量与
  `ShouldSwallowEnter(int keyCode, bool isComposing)` 纯判定（等价 `!(isComposing || keyCode == 229)`），
  在既有 preedit 时间窗之上叠加“合成键”维度。
- `src/Fufucode.App/Views/ChatPage.axaml.cs`：输入框 KeyDown 增加第二重——`Key.ImeProcessed`
  （Avalonia 对 VK 229 的映射）一律吞掉，不进入发送分支；组合态时间窗为第一重。

### 2.4 4px 拖拽死区（迁移项 E3）

| 文件 | 作用 |
| --- | --- |
| `src/Fufucode.App/Infrastructure/DragDeadZone.cs`（新增） | 统一 `Start = 4`、`Report = 4` 阈值与切比雪夫度量 `max(|Δx|, |Δy|)` 的 `HasStarted`/`ShouldReport`。 |
| `src/Fufucode.App/Infrastructure/DeadZoneGridSplitter.cs`（新增） | `GridSplitter` 子类，重写 `OnDragDelta` 做累计位移门：指针拖动累计 < 4px 前吞掉增量（不改布局、不重排）；越过后放行。`StyleKeyOverride = typeof(GridSplitter)` 复用 Fluent 模板（否则键盘/指针拖动逻辑丢失）。**键盘方向键拖动（非指针发起）即时放行**，不受死区影响。 |
| `src/Fufucode.App/Views/DockRegionView.cs` | Tab 起拖阈值 5px → 4px、上报阈值 3px → 4px，改用 `DragDeadZone`。 |
| `src/Fufucode.App/Views/MainWindow.axaml` | 三处 `GridSplitter` 改为 `infra:DeadZoneGridSplitter`。 |

### 2.5 排版微调·等宽数字（迁移项 B）

- `TextBlock.mono` / `TextBlock.meta` 全局锁定 tnum；新增 `TextBlock.numeric`。
- `ChatPage.axaml` 时间戳文本、`AutomationControlPageView.axaml` 计数（需要你决定/工作中）与消息时间戳套用 `numeric`。

### 2.6 定向测试（新增）

`tests/Fufucode.App.Ui1.Tests/`：独立可执行测试工程（console harness，未接入 `Fufucode.sln`，与
`Fufucode.App.Skins.Tests` 同构），43 项断言，覆盖四条迁移项的纯逻辑判定 + 真实无头窗口的令牌解析。

## 3. 不变量（可机械检查）

1. 任意视线路径上至多 1 层闭合边框：已闭合容器内二级控件 `BorderThickness=0`/`Background=Transparent` 或轻质填充；容器内结构划分只用单向 1px 细线（`AppDividerSubtle`，6% 白/黑）。
2. `CanSubmit(Enter) = key==Enter ∧ ¬Shift ∧ ¬isComposing ∧ keyCode≠229`。
3. 指针拖动位移 < 4px 绝不进入拖拽态、不上报；键盘增量拖动即时生效。
4. 频繁变化的数字（时间戳/计数/审计）必须声明 `tnum` 或 `font-mono`。

## 4. 实际验证

- `dotnet build Fufucode.sln -v q` — 0 警告 0 错误。
- `dotnet run --project tests/Fufucode.App.Ui1.Tests -c Release` — 43 项断言全部通过；
  含 `DeadZoneGridSplitter.EvaluateDelta` 的确定性死区判定（指针 3px 吞、6px 放行、越区持续放行、
  键盘拖动即时生效、对角 3px 微颤吞）与真实无头窗口的 `AppTabularNumericFeatures`/`AppDividerSubtle`
  资源解析、`TextBlock.numeric` 实际携带 tnum 字体特性。
- `dotnet run --project src/Fufucode.App -c Debug -- --selftest` — 全量无头自测 0 失败
  （含 `L11: splitter keyboard resize persists without layout rebound` 与 `L07` 指针拖放）。
- 定向过滤复跑：`--workbench-only` 0 失败、`--tabs-only` 0 失败、`--sessions-only` 0 失败、
  `--automation-only`（Automation page failures）0 失败。
- 修复回归过程：首次全量自测出现 1 项 `L11` 失败（`DeadZoneGridSplitter` 未设 `StyleKeyOverride`，
  导致 Fluent 的 `GridSplitter` 模板未命中、键盘拖动逻辑丢失）；补 `StyleKeyOverride` 后经
  干净基线（stash 后 workbench-only 0 失败）对照确认回归来源并修复，复跑全量 0 失败。

## 5. 未执行 / 未验证

- **真实原生人工视觉审阅未执行**：未在真机交互式桌面按 100%/125%/150% DPI 对 composer/工作台/设置页做人工观感确认。
- **真实 IME 实机输入未执行**：CJK 双重互锁为库级 + 无头窗口定向验证；未在真机以中文拼音输入法
  实际选词回车验证（虚拟键 229 映射经 Avalonia API 契约确认）。
- **指针拖拽的端到端重排抑制未由定向工程证明**：合成 Grid 夹具无法驱动真实 Splitter 指针拖动；
  真实指针拖动路径由主 App `--selftest` 的 L07/L11 套件覆盖（本工作树内 0 失败）。
- open-claude-ui 的其它迁移项（弹簧预设、渐隐遮罩、Web Audio 等）不在本任务范围。

## 6. 交付边界

本任务为 **模块交付**：四个迁移项在 worktree 内实现并有定向测试与主 App 无头自测证据，**不代表原生
人工验收或整机产品验收**。open-claude-ui 的数值为设计参考，Avalonia 侧实现按本地证据等级独立验证。
未 push、未登录、未部署、未删除工作树、未改动权威门禁。
