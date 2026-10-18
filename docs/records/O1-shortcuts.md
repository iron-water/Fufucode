# O1 快捷键模块（M1）

日期：2026-10-10。任务：O1 → OMP，快捷键模块（L2 常规）。软件：OMP 执行会话。
本文是本任务的实际交付记录；状态见文末交付卡。**模块交付，待 Codex 公共接线。**

## 1. 任务与范围

按 `docs/tasks/management-surfaces.md` M1：从实际动作注册表生成可搜索的快捷键列表，
显示作用范围、当前绑定与不可用原因；支持修改、禁用、逐项/全部恢复默认与持久化；
区分全局/聊天/浏览器/终端上下文；冲突展示双方动作、不静默覆盖；明确保留键与组合键规则；
保守默认（沿用现有绑定，配置损坏回退默认并报告、不覆盖损坏文件）。

允许范围：`App/Features/Shortcuts`、独立测试与本记录。共享动作注册表、MainWindow、
SettingsPage 接线交 Codex，本任务未改。

## 2. 开工身份卡（事实）

| 项目 | 事实 |
|---|---|
| 工作树 | `C:\Users\mio\.omp\wt\wt-20261010-055614-c2a5227`（独立 worktree） |
| 分支 / HEAD | `wt/20261010-055614` / 起点 `83045c9`，与 main 同点 |
| 继承用户改动 | `M scripts/demo-verify/demo-verify-results.json` + 6 项未跟踪文件；未覆盖、未夹带提交 |
| 依赖 | G0 已交付（本任务仅依赖 G0） |

## 3. 核实的现状（决定了设计）

- 全仓 **不存在** `KeyGesture`/`KeyBindings`；`AppActionDefinition.Shortcut`
  （`Infrastructure/AppActionRegistry.cs:9-10`）只是**展示字符串**。
- 当前唯一全局键路由是硬编码：`Views/MainWindow.Global.cs:162-180`（Alt+Left、Ctrl+K、
  Ctrl+Shift+P、Ctrl+P）与 `Views/MainWindow.Workspace.cs:421-428`（Ctrl+J、Escape）。
- 全仓没有**任何**已持久化的用户按键绑定；`ui.json` 只存界面摆放，
  `settings.json` 存模型/网络，均无按键位。
- 输入法守卫 `Infrastructure/ImeEnterGuard.cs` 只服务于 composer/palette/terminal 三处
  Enter，不覆盖通用按键录制。

结论：M1 的「修改/禁用/恢复/持久化/冲突/保留键」目前**没有落点**，
O1 的实质就是建立这一层：手势模型 + 保留键规则 + 默认表 + 冲突检测 +
命令绑定持久化 + 管理界面，并让动作目录可由注册表注入。

## 4. 交付内容

新增 `src/Fufucode.App/Features/Shortcuts/`（**9 个源文件**，本任务全部新代码）：

| 文件 | 职责 |
|---|---|
| `ShortcutGesture.cs` | 手势规范化：稳定文本 `Ctrl→Alt→Shift→Meta`，解析大小写/全角加号/多绑定串取首段；非法返回 null |
| `ShortcutAction.cs` | 动作投影 + `ShortcutScope`/`ShortcutScopeInfo`/`ShortcutOrigin`：全局/聊天/浏览器/终端/文本编辑 |
| `ShortcutRules.cs` | 保留键与组合键规则（见 §5） |
| `ShortcutDefaults.cs` | 保守默认表 + 范围分类 + 终端输入动作集合 |
| `ShortcutConflicts.cs` | 同范围同手势冲突检测，结果同时列出双方动作 |
| `ShortcutBindingsStore.cs` | `{dataRoot}/keybindings.json` 持久化：版本守卫、临时文件+`.bak`+原子替换、损坏拒绝覆盖 |
| `ShortcutManagerViewModel.cs` | 列表/搜索/范围过滤/冲突解释/改绑/禁用/逐项恢复/全部恢复/保存/放弃 |
| `ShortcutsPage.cs` | 管理页：沿用设置页分组卡与行式条目，内联录制与冲突可见 |
| `ShortcutRecorder.cs` | 录制器：组合期忽略、Esc 取消、修饰键单按等待主键 |

自测：`src/Fufucode.App/SelfTest/ShortcutManagerSelfTest.cs`（修复后 143 条断言），
登记进 `SelfTestRunner` 的 `--shortcuts-only` 频道与全量自测。

### 4.1 修复轮次（2026-10-10，按 M 复核 R-026/027/028）

M 独立复核（`docs/records/M-O1-review-2026-10-10.md`）确认三处缺陷，本会话在原工作树修复，
仅改 Shortcuts 模块与定向自测：

| 缺陷 | 根因 | 修复 | 忠实回退后的定向红灯 |
|---|---|---|---|
| R-026 错误配置被视为合法并覆盖 | `Parse` 对 `bindings` 非对象静默返回空绑定；`version` 缺失/类型错误取默认值；手势字符串不校验 | `Parse` 要求 `version` 为数字字面量、`bindings` 为对象、每个条目为非空且可解析手势；`Save` 增加同一套写盘前校验 | 9 条 |
| R-027 录制器保留已释放修饰键 | `_pressed` 只增不减，`KeyDown` 与历史状态取并集，无 `KeyUp` | 以当前实际按住状态为唯一来源；新增 `KeyUp`/`ReleaseAllHeld`；页面加隧道 `KeyUp` 路由与失活清理 | 11 条 |
| R-028 候选与全表冲突规则不一致 | `DetectFor` 不按范围过滤，`Detect` 按范围分桶；`Refresh` 重复计算导致行/列表不同源 | `DetectFor` 增加 `candidateScope` 并按范围过滤；`Refresh` 只算一次 `Detect`，行标记与冲突列表共用结果 | 3 条 |

「忠实回退」指把被修逻辑改回原实现（保留新 API 以免掩盖），确认对应断言确实变红，
以证明断言覆盖缺陷本身而非同义反复。三处共 23 条断言被证明有效。

## 5. 保留键与组合键规则（M1 明确要求项）

1. **中文输入法组合期间不误触**：`ShortcutRecorder` 在组合进行中（宿主注入的
   `CompositionProbe`，语义同 `ImeEnterGuard` 的 `TextPresenter.PreeditText`）
   一律忽略按键并保持录制；不会落定任何绑定。
2. **终端输入优先级不被全局快捷键抢走**：`ShortcutScope.Terminal` 独立于 Global；
   `ShortcutRules.IsTerminalOwnedKey` 覆盖 Enter/Tab/Esc/方向/翻页/退格/Delete/Space
   ——这些键在终端范围与终端输入动作上不可占用，全局处理器须在终端焦点下让路。
3. **无修饰键不绑定**：拒绝任何裸键，避免抢占普通输入。
4. **固定编辑语义组合**：`Ctrl+A/C/V/X/Z/Y` 的**规范写法**仅 `Editor` 范围可绑定；
   `Ctrl+Shift+Z`、`Ctrl+Alt+X` 等非规范组合不受限。
5. **系统外壳组合拒绝**：`Meta+L/D/Tab/E/R`、`Alt+Tab/Escape/F4`、`Ctrl+Alt+Delete` 等。
6. **修饰键本身不可作为绑定**。
7. **修饰键状态以当前实际按键为准**（R-027 修复）：录制中只有当前仍按住的修饰键参与成形；
   `KeyUp` 与窗口失活都会清除已释放的修饰键状态，`Ctrl` 松开后按裸 `K` 不会录成 `Ctrl+K`。

## 6. 实际验证

构建与定向自测（命令均在上述工作树根执行）：

```
dotnet restore Fufucode.sln
dotnet build src\Fufucode.App\Fufucode.App.csproj --no-restore -v quiet -o tmp\o1-fix
    → 0 errors, 0 warnings
dotnet tmp\o1-fix\Fufucode.App.dll --selftest --shortcuts-only
    → Shortcuts failures: 0（143 条 ok，0 FAIL；首轮 93 条）
```

覆盖点（每条均有独立断言）：手势解析/归一/拒绝、保留键规则（终端自有键、输入法键、
无修饰、编辑组合、外壳组合、修饰键单按）、冲突检测（同范围计冲突、跨范围不计、
列出双方、改绑排除自身、候选与全表规则一致）、持久化往返（自定义、禁用空数组、版本、
未知动作 ID）、**配置严格性**（bindings 为数组/字符串/null、version 缺失/字符串/布尔、
条目非字符串/空白/非法手势/纯修饰键 → 可见回退 + 拒绝覆盖 + 原文件字节不变）、
管理编辑（改绑生效/落盘、冲突标记双方、保留键与非法组合被拒且绑不变、禁用、
逐项恢复、全部恢复需保存、放弃改动回到磁盘）、搜索与范围过滤、
**录制器状态机**（当前修饰键权威、KeyUp 释放、多修饰部分释放、重复按下、
失活清理、Esc 取消、IME 组合忽略）、
**页面事件路由**（录制进入/释放后裸键不落定/IME 保持录制/Ctrl+K 落定/Esc 不改绑定/失活清理）、
管理页无头渲染与页面内搜索。

## 7. 全量回归

```
dotnet tmp\o1-fix\Fufucode.App.dll --selftest
    → == 自测结束：0 项失败 ==（exit 0，耗时约 146 s）
```

全量包含 O1 的 143 条断言，其余既有套件全部通过，未出现回归。
（`--selftest` 全量在隔离临时数据根运行；证据截图位于该次运行的
`%TEMP%\fufucode-selftest-*\evidence\h2-shots`、`h3-shots`。）

### 7.1 首轮实现中自测抓出的缺陷（O1-L1 / O1-L2 / O1-L3）

1. **保留键规则过宽**：原按「主键属于 A/C/V/X/Z/Y 即拒绝（非编辑范围）」实现，
   会误伤 `Ctrl+Alt+Z` 等非规范组合，导致改绑被静默拒绝、保存写空表。
   已改为按**规范组合文本**（`Ctrl+Z` 等）匹配；新增两条回归断言。
2. **`SettingsLayout.Row` 结构假设错误**：把行说明区当成 StackPanel 直接强转，
   实为 `Grid(Auto,*,Auto)` 第 1 列。已改为定位该 Grid 的说明 StackPanel。
3. **`ResetAll` 语义只清本次改动**：未清已保存的自定义，与「全部恢复默认」不符。
   已改为整体清空自定义（保存后落盘），并在状态文本说明「保存后生效」。

以上三条改用 **O1 局部编号 O1-L1 / O1-L2 / O1-L3**（仅本记录内引用），**不占用全局编号**：
全局 R-020/021/022 已由 D1 与审批工装占用；本任务的全局条目 R-026/027/028 由 M 登记在
`docs/records/failure-registry.md`，本会话不覆盖、不改号、不抢占新全局编号。
收编时由 M 决定是否归并。

## 8. 未运行 / 未验证 / 待接线

- **未接线**：`AppActionRegistry` → `ShortcutManagerViewModel.SetActions` 的投影，
  以及 `HandleGlobalShortcut`/`OnWorkspaceKeyDown` 改用绑定表的路由改造，
  按执行契约属 Codex 共享入口，本任务未改。
- **未原生验证**：M1 验收明确要求「修改后立即生效、重启保留；焦点不同产生正确动作；
  中文组合输入正常；必须做原生键盘行为验证，动作回调测试不能替代」。
  本会话只做无头自测（含真实控件渲染与按键事件注入），**原生键盘/真实 IME/
  跨焦点行为未验证**，须由 Codex 接线后用真实构建验收。
- **未验证**：真实中文输入法组合（需要系统 IME 与会话），仅按状态机语义验证。
- 未做任何 `reference/` 修改；未 push、未改写历史、未清理工作树。

## 9. 公共接线需求（交 Codex）

1. 在 `MainWindow` 构造动作表后调用：
   `shortcutManager.SetActions(_actions.Actions.Select(a => new ShortcutAction(a.Id, a.Title, a.Group, ShortcutDefaults.ScopeOf(a.Id), ShortcutDefaults.DefaultGestures(a.Id), a.Description == a.Title ? null : a.Description)), id => _actions.GetDisabledReason(id))`。
2. `ShortcutBindingsStore(dataRoot)` 用与 `ui.json` 相同的 `dataRoot`
   （`Program.SelfTestDataRoot ?? _options.CreateAppDataLayout().Root`）。
3. 设置页入口：`SettingsPageViewModel` 增加子页 + `WorkbenchSectionsView` 分支，
   或在模块页直接承载 `ShortcutsPage`。
4. `CompositionProbe` 由宿主接到 `TextPresenter.PreeditText` 并保持实时读取（不得缓存在启动时）。

### 9.1 绑定路由（关键约束：不得回退旧硬编码）

`HandleGlobalShortcut`/`OnWorkspaceKeyDown` 必须改为**先看绑定表，再决定是否回退**，
且回退规则是「仅当该动作没有用户绑定记录时」：

1. 用绑定表解析按下的手势；命中则执行对应动作并 `Handled = true`。
2. **已改绑的动作**：旧硬编码手势必须立即失效。例如用户把 `commands` 从 `Ctrl+K`
   改到 `Ctrl+Alt+K`，则 `Ctrl+K` 不再打开命令面板，`Ctrl+Alt+K` 才打开。
   实现上不得保留一条「硬编码链」在原手势上继续触发。
3. **已禁用的动作**（绑定为空数组）：手势不应触发该动作，且不得退回硬编码默认值。
   即禁用是「不绑定」，不是「恢复默认」。
4. **未配置的动作**：才允许回退到 `ShortcutDefaults.DefaultGestures(id)`
   （等价于与现有硬编码行为一致），保持「保守默认、行为不变」。
5. 终端焦点下的让路分支保持优先：`ShortcutRules.IsTerminalOwnedKey` 为真时，
   全局处理器直接返回，不消费该按键。
6. 动作目录与快捷键配置必须同源：`SetActions` 传入的就是动作注册表的投影，
   冲突检测、可用性与实际执行都基于同一份 ID，不得另建动作清单。

```text
交付卡（修复轮次重新交付）
任务ID与软件：O1（快捷键模块，L2 常规）· OMP 执行会话
工作树绝对路径、分支/HEAD、起点SHA：
  C:\Users\mio\.omp\wt\wt-20261010-055614-c2a5227
  分支 wt/20261010-055614（非 detached）；起点 83045c9
本任务提交SHA（多个提交注明范围）：
  4731024 → fc7ef9b → e5d7132（首轮实现/记录/SHA 补录）
  beca239（本轮 R-026/027/028 修复）
改动文件、继承用户改动的保护情况：
  本轮仅改 Shortcuts 模块 5 个文件（ShortcutBindingsStore / ShortcutRecorder /
  ShortcutConflicts / ShortcutManagerViewModel / ShortcutsPage）
  与定向自测 ShortcutManagerSelfTest.cs；记录 docs/records/O1-shortcuts.md
  failure-registry 已还原为与 main 完全一致（不覆盖 M 的 R-026/027/028 登记）
  本任务源文件共 9 个（首轮交付卡误记为 10 个，已修正）
  继承用户改动（M scripts/demo-verify/demo-verify-results.json 与 6 项未跟踪文件）
  未覆盖、未夹带提交；未改 MainWindow/Settings/组合根；未整合 main、未 push
实际验证命令、结果、证据位置：
  dotnet build src\Fufucode.App\Fufucode.App.csproj --no-restore -v quiet -o tmp\o1-fix
    → 0 errors, 0 warnings
  dotnet tmp\o1-fix\Fufucode.App.dll --selftest --shortcuts-only
    → Shortcuts failures: 0（143 ok / 0 FAIL，首轮 93 条）
  dotnet tmp\o1-fix\Fufucode.App.dll --selftest
    → == 自测结束：0 项失败 ==
  缺陷证明（忠实回退被修逻辑后定向必红）：
    R-026 → 9 条红；R-027 → 11 条红；R-028 → 3 条红；合计 23 条断言被证明有效
  证据：docs/records/O1-shortcuts.md §4.1 / §6 / §7
未运行验证与原因：
  原生键盘/真实 IME/跨焦点/重启/DPI 未验证——属 M1 验收项，需接线后真实构建验收；
  真实中文输入法组合需系统 IME 与会话，本会话仅按状态机语义与事件路由验证。
公共接线需求、接口/入口/依赖：
  见 §9 与 §9.1：动作目录投影、ShortcutBindingsStore(dataRoot)、
  绑定表路由（**改绑/禁用后不得回退旧硬编码**）、设置页入口、实时 CompositionProbe
状态：模块交付待接线（R-026/027/028 已修复，待 M 独立复核）
原会话是否仍在写代码：否
```

- `4731024` O1: 快捷键模块（手势/保留键/冲突/持久化/管理页）— 实现与自测（起点 `83045c9`）
- `fc7ef9b` O1: 交付记录与缺陷登记 — 本记录与 failure-registry
