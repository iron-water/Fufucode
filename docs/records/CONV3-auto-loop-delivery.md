# CONV-3 AUTO-LOOP 自动续推闭环交付记录

日期：2026-10-15
任务书：`docs/tasks/conversation-workbench-v4.md`（AUTO-LOOP 包）
工作树：`C:/Projects/Fucode/worktrees/conv3-auto-loop`
分支：`agent/conv3-loop`

## 1. 范围与结论

CONV-3 实现「对话组织开发」的程序化闭环：解除 Core 对 Automatic 模式的硬编码拒绝，实装
空闲自动对账续推引擎，并把主控对话的目标输入真正提交到核心。三个纵向入口都已在产品代码中跑通，
不依赖 Codex 桌面专属工具，也不使用回显桩。

本包是模块级纵向交付，按项目约定仍需主控 M 在唯一 main 租约下收编与整项验收。产品默认策略、
后台/登录自启、原生 UI 的组合验收属于 AUTO-OPERATE（CONV-2）与 M 的组合根范围。

## 2. 实现内容

### 2.1 解除 Automatic 限制（Core）

- `AutomationStore.Control.cs`：删除 `SetMode(Automatic)` 的 `NotSupportedException`；改为
  `ControlExecutionMode.AllowsDispatch` 统一判定，Manual 不派工，SemiAutomatic 与 Automatic
  都可派工，差别只在审核/集成的决策者。
- `RefreshControlIntents`（抽出 `RefreshControlIntentsForGoal`）：非 Manual 模式为每个已批准
  包物化 `awaiting_binding` 执行意图，Manual 只挂起不删除。
- 模式门禁同步到绑定/修复/收件三条路径：`ControlQueue`、`ControlRepair`（含
  `ControlRepairRunAdmissible`）、`ControlInbox` 的 repair 可操作性判断，全部改用同一判定，
  避免出现「模式允许 + 队列拒绝」的不一致。
- 持久化仍走既有 `control_state`（SQLite），未新增并行真源。

### 2.2 空闲自动对账续推引擎（Core）

新增 `src/Fufucode.Core/Automation/Conv/AutoAdvancementEngine.cs`：

- `AutomationDelegation`：显式委托范围（并发、自动审核、自动集成、派发预算、允许路径、模型），
  单独表 `control_delegation` 持久化；未登记范围时引擎返回 `NeedsDelegation`，**不把未知权限当成无限**。
- `AdvanceAsync(projectId, nowUtc)`：确定性有序推进，顺序为
  1. 结算待审交付（仅接受当前、已停写、线程就绪且证据完整的交付）；
  2. 授权已接受且合格的候选集成（只落授权，不代替集成者做 Git 合并）；
  3. 在并发空位与派发预算内，通过 `ControlIntentConsumer` 绑定并派发就绪任务；
  4. 无可推进动作时给出真实终态：`ProjectCompleted` / `Stalled`（含具体原因），绝不模型空转。
- 每个动作都以既有持久回执/唯一行为准，重跑零副作用；每次推进写入
  `control_advancement_runs` 与 `control.advancement` 事件。
- `BackgroundHost`：队列空闲时按 `AdvanceMs`（默认 2s）节流执行一次推进；失败只记录停滞原因，
  不结束宿主、不重试模型。

### 2.3 主控对话目标直达核心（Core + App）

- 新增 `src/Fufucode.Core/Automation/Conv/AutomationStore.Goal.cs`：
  `ProjectGoalCommand` / `ProjectGoalVersion` / `ApplyProjectGoal`，目标版本化落库
  （`conv_goals` / `conv_goal_versions` / `conv_goal_receipts`），命令去重、乐观版本校验、
  旧版本置 `superseded` 不删除；可选 `ActivateMode` 在同一事务内切换模式并物化意图。
- `AutomationControlViewModel.V4.cs`：`SubmitGoalRequestAsync` 真正提交目标并登记委托范围，
  成功后递增目标版本（`GoalVersion` / `GoalVersionText`）并刷新；失败保留输入并如实显示原因。
  对应的 `SubmitGoalRequestCommand` 与既有 AXAML `SendGoal` 按钮绑定保持一致。

## 3. 实际验证（本工作树实跑）

| 验证 | 命令 | 结果 |
| --- | --- | --- |
| 全量构建 | `dotnet build Fufucode.sln -v q --nologo` | 0 警告 0 错误 |
| CONV-1 回归 | `dotnet run --project tests/Fufucode.Conv.Tests -c Debug` | 73 项全通过 |
| 控制/C4 纵向 | `dotnet run --project tests/Fufucode.Control.Tests -c Debug` | 538 项全通过 |
| 持久化 | `dotnet run --project tests/Fufucode.App.Automation.Persistence.Tests -c Debug` | 192 项全通过 |
| V4 投影 | `dotnet run --project tests/Fufucode.App.Automation.V4.Tests -c Debug` | 59 项全通过 |

CONV-3 端到端证据（`tests/Fufucode.Control.Tests/AutoAdvancementChecks.cs`，25 项断言）：真实 git 仓库 +
真实 `BackgroundHost` + 真实子写入进程 + 真实收件/决定门禁；覆盖 Automatic 持久化、未登记委托拒绝、
空闲推进（自动接受 + 授权集成）、重跑幂等、终态 `ProjectCompleted`、目标版本化与陈旧版本拒绝。

## 4. 顺带修复（诚实标注）

`tests/Fufucode.Conv.Tests/WatchdogChecks.cs` 原本 26 处 `check(...)` 漏写 `await`，导致断言被静默跳过；
补 `await` 后暴露出事件类型断言写错（`delivery.*` 实际是 `conv.received` / `conv.failed`），
一并修正。修复后 CONV-1 全部 73 项断言真正生效并通过。

## 5. 未验证 / 未执行

- 未做真机 GUI 操作验收（属 AUTO-OPERATE / M 组合根范围）。
- 未接入真实付费模型执行路径（测试显式使用 fixture，未消耗模型额度）。
- 未推送、未合并 main、未清理工作树、未改动本机开机项或外部 Codex 监督任务。

## 6. 公共接线需求

- 主控 M：在唯一 main 租约下收编本分支提交，按目标版本 + 候选 SHA + 独立证据做整项验收。
- 产品策略：`AutomationDelegation` 的默认值应来自 UI 委托抽屉（CONV-2 已提供字段），由 M 在组合根接线。
- `ResolveExecutable` 目前按 `FUFUCODE_<HARNESS>_RUNTIME` 环境变量解析原生运行时；原生内核就绪后，
  由 M 改为从已注册部署配置读取，本轮不臆造 provider。
