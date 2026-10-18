# B1 交付记录：工作台宿主基础（C1 → C2）

## 交付卡（执行会话，2026-10-11）

- 任务：`docs/tasks/hourly-B1-host.md`，难度 L4，推荐 lead `group/auto-qfmodel`（本会话沿用原模型，未更换、未开新树、未派下一包）。
- 审核状态：v1（`e8d3e43`）被 M 判 rework（缺陷 B1-R1：task 身份未持久）；v2（`f3510fa` 记录的那轮）后又收到 B1-HEAD-1（交付可借用他人 HEAD）。本记录含 v3 修复；模块状态仍为**提交待验收**，256 断言不构成整项验收。
- 实际工作树：`C:\Users\mio\.codex\worktrees\8bf3\Fufucode`（detached HEAD，非分支）。
- 起点：`4ccaf6a3d1f6bec812fcada6c478953c5b0b3359`（本任务书提交）；任务书声明的前序交接 `14ff2b3`。
- 起始状态：继承自上一段实现会话，工作树干净、无未提交内容；未执行 reset/同步 main/新建工作树。主仓 main 在本地已前进（当前 `2edf35d`），本任务未合并、未 rebase。
- 提交（4 个，均在本任务允许路径内）：
  - `581205eda0ad830b34a8f0a78da03ca21dc06185` — C1：所有者/子写入者核实、受控 Unknown 解决、交付接收门（13 文件，+1727）。
  - `516b65122147012f0008b03712b1c98afbc22285` — C2：唯一后台宿主、持久队列、客户端命令/快照/事件续读（9 文件，+1615）。
  - `32ebf729a6698caa5d66f416e4b40553ad0d1bcf` — C2：宿主中途被杀的接管证据 + 显式真实模型探针（3 文件，+197）。
  - `5f8bc7f87e3de5dc2127f09a2c13fb8c9fb71921` — C2：队列不得为未注册线程造活（外键实证，+7）。
  - `ebb6864`（rework B1-R1）— 持久派发 task 身份 + 接收门/解决入口按身份强制比对；先写忠实红反例并确认失败，7 文件 +297/-14。
  - `9267fd3`（rework B1-HEAD-1）— 交付绑定到本 run 自己记录的 settle 时 HEAD；先写忠实红反例并确认失败，7 文件 +239/-14。
- 代码增量 tip：rework B1-HEAD-1 提交 `9267fd3`；其上仅追加本记录的文档提交。以 `git rev-parse HEAD` 为准，工作树干净、无未跟踪任务文件。
- 全分支改动清单（`git diff --name-only 4ccaf6a..HEAD`）只落在允许路径：Harnesses 8 文件、Hosting 5 文件、`tests/Fufucode.Hosting.Tests/**`、本记录。`AutomationStore.cs`、`AutomationModels.cs`、App、Pi、`scripts/**`、`Fufucode.sln` 零改动。
- 范围：`src/Fufucode.Core/Automation/Harnesses/**`（8 文件）、新增 `src/Fufucode.Core/Automation/Hosting/**`（5 文件）、`tests/Fufucode.Hosting.Tests/**`（7 文件）、本记录。未触碰 `AutomationStore`/`AutomationModels`、App、Pi、`scripts/**`、解决方案文件与任何权威计划/进度/缺陷登记。

## 交付卡（rework B1-HEAD-1，本轮）

- 任务：B1-HEAD-1 修复（M 的 finding：已完成的 run 可呈现仓库当前 HEAD，而该 HEAD 可能是后续 run 的提交）。难度 L4；沿用原模型（未更换）、沿用原工作树，未同步 main、未清理、未 push、未派下一包。
- 实际工作树/cwd：`C:\Users\mio\.codex\worktrees\8bf3\Fufucode`（detached HEAD）。
- 起点：`f3510fa`（上一轮记录提交）。本轮两个提交：`9267fd3`（代码 + 测试，7 文件 +239/-14）与本记录文档提交；工作树干净。
- 红反例：修复前实跑 `FAIL head: a run cannot deliver a HEAD that another run produced`（`C:\tmp\red2.log` / `C:\tmp\red2.err`），先确认缺陷复现再改实现。
- 实现：`settle_head` 列与 status 同语句落盘；执行器在 writer 退出后、落终态前观察 HEAD（异常路径不记）；接收门与 `ResolveAsync` 的成功认证都要求候选/证据 HEAD 等于本 run 的 settle HEAD；无观察者或旧形状行一律 fail closed（`settle-head-unobserved`）。
- 验证（全部本机实跑）：6 个工程 `dotnet build` 各 0 警告 0 错误；Hosting 套件 **`PASS 256 checks`**（230 → +26：`head:` 19、`no-observer:` 5、`gate:` 2），exit 0、无 FAIL；Harness `PASS 51 checks`；Automation `ALL PASS`。既有断言一条未放宽，只新增。
- 未做/未验证：未复跑真实模型探针（本轮规则禁止非必要探针）；`tools/Fufucode.HarnessHost` 未改动，其无观察者执行器的后果已作为公共需求 9 写清（可传输、永不可认证，装配点归主控）；跨机器并发提交同一工作树未验证。
- 模块状态：**提交待验收**（接受 ≠ 集成；工作台端到端产品行为与生产接入仍未验收）。

## 实现内容

### C1 运行所有权、受控解决与交付接收门（`581205e`）

复用 U1 与 `HarnessExecutor`，不新增调度器。

- `Harnesses/ProcessFacts.cs` — 进程事实与 `IProcessProbe` 接缝；身份 = pid + 启动时间（3 秒容差）；`Alive/Dead/Indeterminate` 三态，可注入以确定性测 pid 复用。
- `Harnesses/ProcessTree.cs` — 真实子进程枚举（Windows Toolhelp 快照 / Linux `/proc`），并在会收养孤儿的平台按 cwd 扫描工作树内写入者。
- `Harnesses/GitEvidence.cs` — `IGitEvidence`/`GitCliEvidence`：`--no-optional-locks` 读真实 HEAD 与已跟踪干净度；完整 SHA 校验。
- `Harnesses/HarnessRunStore.cs` — 追加列 `supervisor_pid/supervisor_start_utc/writer_pid/writer_start_utc/host_id/worktree_head`（`AddColumn`，保留旧行）；追加式 `harness_resolutions` 日志；`ListActive()`、`TryProjectUnknown()`（只向不确定方向）、`TryResolve()`（一次不可变结算）、`ListResolutions()`。（后续两轮再追加 `task_id` 与 `settle_head`，见下面两节 rework。）
- `Harnesses/HarnessExecutor.cs` — 构造改为 `(store, evidenceDirectory, hostId="cli", IGitEvidence? git=null)`；在第一次等待读取之前落盘 supervisor 身份、writer 身份与启动时 HEAD。（B1-HEAD-1 后又在 writer 退出、落终态之前落盘 settle 时 HEAD。）
- `Harnesses/RunReconciliation.cs` — `RunReconciler.AuditAsync/AuditAllAsync` 判定 `OwnedHere | ActiveElsewhere | ConfirmedFinished | Unverifiable`；`MarkOwnerLostAsync` 投射 Unknown 并冻结线程，绝不重发；`ResolveAsync` 在调用时重新校验（仅 Unknown、线程身份一致、完整 SHA 等于真实 HEAD、writer 已停、目标状态对应终态信号、日志完整且成功需干净工作树）并只落一条结算。
- `Hosting/DeliveryReceiver.cs` — 接收门：未知 run、SHA 格式、卡片状态、终态日志、run/thread/task/part 绑定、冻结线程、工作树替换、丢回执重放（`already-recorded`）、当前 HEAD 一致、交付溯源（B1-HEAD-1）、内容冲突、已跟踪脏工作树、不可读工作树、已决定 revision 不可替换；accept/integration 留给上层。
- 说明：schema 变更全部在 Core 内的日志追加列，未修改共享模型；因此本轮无需向主控申请 `AutomationStore`/`AutomationModels` 改动。

### C2 唯一后台宿主与客户端协议（`516b651`+`32ebf72`+`5f8bc7f`）

不实现 AUTO、不新增调度器、不迁移生产 Pi/OMP 会话、不启动生产后台服务。

- `Hosting/HostJournal.cs` — 持久投递队列 + 单宿主槽位（同项目库，无第二真源）。
  - 槽位为一行（`id=1`），记录 host 身份 + pid + 启动时间 + 心跳 + 显式停止时间。接管只允许在“记录进程可证明消失”时发生；`Indeterminate` 视为占用；pid 存在但启动时间不符（复用）判定为 `Crashed` 而非同一宿主。
  - `HostSlotState = Empty | Occupied | Stopped | Crashed`：显式 `Release` 保留行并打停止时间戳，崩溃残留不打 —— 明确停止与退出因此可区分。
  - 队列以 `run_id` 为主键，`thread_id`/`task_id` 外键绑定 U1 行；同 payload 重放为 no-op，同 run 不同 payload 拒绝并零变更；状态机 `Queued→Started→Settled`／`→Abandoned` 单向，`Move` 返回拒绝而非抛出（避免宿主排空循环因竞争死亡）。
  - `RecoverAfterRestart` 以 harness 日志为准而非自有列：`Started` 且无任何派发记录 = 从未进入派发器，重新排队；`Started` 且 run 处于 `Dispatching/Running/Unknown` = 结果不确定，Abandon 交给对账，绝不重发；run 已终态 = 直接记 Settled。原 `Queued` 积压按接受顺序保留。
- `Hosting/BackgroundHost.cs` — 唯一后台执行宿主进程。声明槽位后在服务任何客户端之前完成恢复（首个 HELLO 即报告恢复动作）；排空循环经 `HarnessExecutor` 逐条执行；回环 TCP 行协议 `HELLO/PING/SNAPSHOT/EVENTS/SUBSCRIBE/COMMAND(dispatch|abandon)/STOP/BYE`；`MarkStarted` 返回 false 时不执行（防重复执行）；显式 STOP 由排空循环的 `finally` 释放槽位，因此飞行中的 run 由本宿主跑完并落日志后才记为“停止”；`RUNNING` 事件仍写进 U1 事件日志（复用游标，无第二事件存储）。
- `Hosting/HostClient.cs` — 可丢弃客户端：后台读取按 `id` 路由回执、把推送帧排入 Channel，因此关闭客户端只停止读取，不影响宿主或 run；`CatchUpAsync` 按游标补齐断线期间事件。
- `Hosting/HostProtocol.cs` — 帧名常量与协议版本。

### Rework B1-HEAD-1：交付绑定到本 run 自己的 settle 时 HEAD（`9267fd3`）

M 的新 finding 成立：`harness_runs` 只记启动时 `worktree_head`，接收门核对的是“候选 HEAD == 当前仓库 HEAD”。同一线程共享一个工作树时，A 的运行跑完后 B 的运行又提交一次，A 的完整卡配合“当前 HEAD”（其实是 B 的提交）能通过当时全部守卫被接收。C1 不能带这个缺口验收，故本轮直接闭合，不作接线请求。

先写忠实红反例并确认失败（场景 `provenance`，两个真实 task/part、两次真实子进程运行、两次真实提交）：

```
PASS head: task A's run completes
PASS head: task B's run completes
PASS head: B really moved the repository forward
Unhandled exception. System.Exception: FAIL head: a run cannot deliver a HEAD that another run produced
  at ...Program.<Main>$(String[] args) in .../tests/Fufucode.Hosting.Tests/Program.cs:line 692
```

原始输出：`C:\tmp\red2.log` / `C:\tmp\red2.err`（临时文件，非仓库内容）。

修复（都在允许路径内）：

- `HarnessRunStore` — 追加列 `harness_runs.settle_head`（`AddColumn`，旧行为 NULL）；`Save(...)` 新增 `settleHead` 参数，`settle_head=COALESCE($settle,settle_head)` 与 status 在同一条语句内落盘，崩溃不会留下“已 Completed 但没有溯源绑定”的行；`Get` 读出挂到 `HarnessRun.SettleHead`。不设 `start_head` 新列：`worktree_head` 本就是启动时 HEAD。
- `HarnessExecutor` — 在 `await stderrTask`（本 run 自己的 writer 已退出）之后、落终态日志之前读一次 HEAD 并随 status 一起持久化；`settled` 事件带 `settleHead`。`catch` 分支刻意不记任何 HEAD：宿主丢失或杀掉了 writer，就无法证明 writer 退出时仓库停在哪儿，之后的读取可能已含别人的提交。无 `IGitEvidence` 时取 null，不猜。
- `DeliveryReceiver` — 在既有“当前 HEAD 一致性”之后加溯源门：`SettleHead` 为空 → `settle-head-unobserved`（诊断点名旧形状行或未注入观察者的执行器）；`SettleHead != 候选 HEAD` → `head-not-from-run`（诊断引用日志里的那个值）。丢回执重放分支在前，故重放语义不变。
- `RunReconciliation.ResolveAsync` — 把 Unknown 结算为 `Completed` 需要该 run 自己观察到的 settle HEAD：为空 → `settle-head-unobserved`；与证据 HEAD 不符 → `head-not-from-run`。`Failed`/`Interrupted` 的诚实结算不受影响，也不会因此补造溯源。
- 测试新增 26 项：`head:` 19 项（借用 HEAD 被拒且诊断点名本 run 的 HEAD、零 revision；A 呈现自己的固定提交在工作树前进后仍按原规则 `head-mismatch`；B 的诚实交付正常接收为 rev 1 且丢回执重放、`ConfirmAsync` 命中；借来的身份 `ConfirmAsync` 返回 `not-recorded`；`SeedLegacy` 造的无溯源 Completed 行既不可投递也不可被认证为成功、被拒后日志不动、仍可诚实结算为 Interrupted 且 `SettleHead` 仍为空；真实派发计数恒为 2）+ `no-observer:` 5 项（用 `tools/Fufucode.HarnessHost` 实际构造的无观察者执行器：传输仍 `Completed`、`WorktreeHead`/`SettleHead` 双空、提交真实发生、交付被 `settle-head-unobserved` 拒、认证被拒、零 revision）+ `gate:` 2 项（场景 6 的已决定检查改为两个候选：先断借 HEAD 被 `head-not-from-run` 拒且未写第三 revision，再用 B 自己的 run 与不同卡片断 `already-decided`；原断言一律未放宽）。

Rework B1-R1 中“刻意未做的反向缺口”本轮已闭合，相应注释与公共需求条目同步更新，不再写成 open。

### Rework B1-R1：持久派发任务身份并在接收门强制比对（`ebb6864`）

M 的静态 finding 成立：`harness_runs` 只落 `request_hash` 与 `thread_id`，从未持久 `HarnessRequest.TaskId`；`DeliveryReceiver` 只核对卡片自报的 task 与 part 归属，没有与“这个 run 实际是替哪个 task 跑的”比对。因此同一线程内 A 任务跑完，可以用 B 任务/B part 的卡片配合真实 HEAD 申请交付。

先写忠实红反例并确认失败（不是把 task 改成不存在的值）：

1. 同线程建立两个真实 task/part（`task`/`part`、`task-b`/`part-b`），`run-a` 真跑真提交、终态日志完整；
2. 用 `run-a` + 真实 HEAD + 完整终态提交 `task-b`/`part-b` 的卡；
3. 旧实现下该项 **FAIL**（实测 `Unhandled exception: FAIL identity: a completed run of task A cannot deliver task B's card`，见修复前运行记录），证明反例确实命中缺陷。

修复（都在允许路径内）：

- `HarnessRunStore` — 追加列 `task_id`（`AddColumn`，旧行保留），在 `Claim` 写 run 行时直接从 `HarnessRequest.TaskId` 落盘；`Get` 读出并挂到 `HarnessRun.TaskId`。身份只有一个来源：派发请求本身，绝不从 stdout 正文、事件文本或 `request_hash` 猜。
- `HarnessExecutor` — 派发入口 `ArgumentException.ThrowIfNullOrWhiteSpace(request.TaskId)`：没有 task 身份的连接根本不允许建立 run 行（入口即 fail-closed）。
- `DeliveryReceiver` — 在“卡片自报身份”核对之前，先按持久身份核对：`run.TaskId` 为空 → `run-task-unknown` 拒收并给可操作诊断（说明旧行从未记录派发任务、绝不猜测、应重派或显式结算）；与卡片 task 不符 → `run-task-mismatch`；与 part 所属 task 不符 → `run-part-mismatch`。`ConfirmAsync` 同样重查持久身份，借来的身份确认不到任何东西。
- `RunReconciliation.ResolveAsync` — 结算不得改写派发身份：证据里写了别的 task → `run-task-mismatch` 拒绝；`task_id` 缺失的旧行永远不允许被认证为 `Completed`（`run-task-unknown`），但仍允许诚实地结算为 `Failed`/`Interrupted`，且不会因此补造一个身份。
- 测试新增 30 项（`identity:` 14 项 + `unknown:` 16 项），含：跨任务借用拒收且不写任何 revision；B 自己的 run 仍能正常接收；重放（丢回执）后身份仍保持、revision 不翻倍；`ConfirmAsync` 借身份返回 `not-recorded`；无 task 身份的派发被入口拒绝且不留 run 行、不启 writer；飞行中 run 已带身份；开放 Unknown 的跨任务卡被拒且未写 B 的 revision；证据改指他任务被拒且日志身份未被改写；无输出日志的运行不能被认证为成功；旧形状行（进程身份完整、无 `task_id`）不可认证为成功、诊断点名缺什么、诚实结算为 Interrupted 后身份仍为空。

（当时）刻意未做（越权，转为需求）：反向缺口 —— “A 的 run 呈现 B 的 run 产生的 HEAD”。该缺口已由下一轮 B1-HEAD-1 闭合，见上文与公共需求 9。

## 实际验证（全部本机实跑，非静态检查）

| 命令 | 结果 |
|---|---|
| `dotnet build src/Fufucode.Core/Fufucode.Core.csproj` | 0 警告 0 错误 |
| `dotnet build tests/Fufucode.Hosting.Tests/Fufucode.Hosting.Tests.csproj` | 0 警告 0 错误 |
| `dotnet build tools/Fufucode.HarnessHost/Fufucode.HarnessHost.csproj` | 0 警告 0 错误 |
| 红反例（修复前，仅落 `task_id` 列、门未改） | `FAIL identity: a completed run of task A cannot deliver task B's card`（缺陷确实复现） |
| 红反例（B1-HEAD-1 修复前，仅加 `settle_head` 列之前） | `FAIL head: a run cannot deliver a HEAD that another run produced`（`C:\tmp\red2.err`，缺陷确实复现） |
| `dotnet run --no-build --project tests/Fufucode.Hosting.Tests/...`（B1-R1 后） | **`PASS 230 checks`**（C1 基线 92 + C2 108 + rework 30），exit 0，`fufucode-hosting-tests-69d9b08331924459900dd9569a533071` |
| `dotnet build`：Core / HarnessHost / App / Hosting.Tests / Harness.Tests / Automation.Tests（B1-HEAD-1 后） | 各 0 警告 0 错误 |
| `dotnet run --no-build --project tests/Fufucode.Hosting.Tests/...`（B1-HEAD-1 后） | **`PASS 256 checks`**（230 + head 19 + no-observer 5 + gate 2），exit 0，无 FAIL，`fufucode-hosting-tests-163802eb349449b09a2ca35dd77622b2` |
| `dotnet run --no-build --project tests/Fufucode.Harness.Tests/...`（B1-HEAD-1 后） | `PASS 51 checks` |
| `dotnet run --no-build --project tests/Fufucode.Automation.Tests/...`（B1-HEAD-1 后） | `ALL PASS` |
| `... --probe <codex.exe> devin/swe-2`（rework 后复跑，免费档） | `PROBE PASS 11 checks`，会话 `01a12dcd-89b4-7b13-9116-015397cf0ecd`，输出「在线」，`Completed` / 队列 `Settled / Completed`，接收门按持久身份放行真实交付 |
| `dotnet run --project tests/Fufucode.Harness.Tests/...` | `PASS 51 checks` |
| `dotnet run --project tests/Fufucode.Automation.Tests/...` | `ALL PASS` |
| `dotnet build`：Core / HarnessHost / App / 三个测试工程 | 各 0 警告 0 错误 |

rework 后的两处 C1 期望更新（不是放宽，而是更强）：`gate: the part must belong to the claimed task` 原断 `task-mismatch`（只比卡片自报值），现断 `run-task-mismatch`（比持久派发身份）；`unknown:` 中开放 Unknown 的跨任务卡先在 `unknown-requires-resolution` 处被拒，随后单独断言未给 B 的 part 写入任何 revision。

夹具全部为真实子进程 + 真实 `git init` 仓库 + 真实 SQLite 日志，仅在 `%TEMP%` 下建立，不触碰用户数据；默认套件不联系任何模型。

原始证据目录（本机，仍可读）：

- `C:/Users/mio/AppData/Local/Temp/fufucode-hosting-tests-0f7f44b6a7664acd92f789f26d7a97a2` — 最终 200 项（各场景含 `state/automation.db`、`state/runs/<hash>/events.jsonl`、`worktree/`）。
- `C:/Users/mio/AppData/Local/Temp/fufucode-host-probe-f0266d7d1d0d4d94ac3296c24530ecd1` — 真实模型宿主探针（含 SQLite、原生事件与 stderr）。
- `C:/Users/mio/AppData/Local/Temp/fufucode-harness-tests-6e3f5056754144708d71aa0478e85806` — Harness 回归 51 项。
- 控制台完整输出：`C:/tmp/hosting-run.log`、`C:/tmp/host-probe.log`（临时，非仓库内容）。

### 真实子进程生命周期证据（与模拟证据分列）

真实进程观察：

- 宿主被杀（场景 5、12、14）：真实 supervisor 进程消失，`writer_pid` 由 `SystemProcessProbe` 实测为 `Dead`；槽位读作 `Crashed` 且端点文件残留；run 走 `Unknown → 受控解决`；接管宿主不宣称所有权、不重写派发日志、不派发新 run（`writer-starts.log` 行数恒为 1）。
- 孤儿写入者（场景 3）：`spawn` 角色父进程真实退出，`hold` 子进程真实存活并被 Toolhelp 枚举到，阻挡解决；杀掉后同一审计转 `ConfirmedFinished` 才允许结算。
- 双宿主竞争（场景 2、11、13）：第二个宿主进程真实启动并被拒（退出码 4 / `StartAsync` false），期间 `writer-starts.log` 行数不变；只有记录进程可证明消失后新宿主才能接管（场景 11 由杀进程后验证）。
- 客户端离开（场景 12）：客户端 socket 真实关闭后，宿主 PID 仍 `Alive`、writer 仍 `Alive`，run 自行完成；重连按游标补齐断线事件并收到推送帧。
- 显式停止（场景 11、12、13、探针）：宿主进程退出码 0、端点文件删除、槽位读作 `Stopped`、停止后再无人监听。
- 真实模型（探针）：`codex.exe` 子进程为真实写入者，由宿主监督完成，事件与结果均来自主机侧日志而非客户端断言。

模拟/注入证据（仅用于无法等待 OS 行为的场景，明确标注）：

- `FakeProbe` 注入 `Alive` + 不同启动时间（pid 复用）与 `Indeterminate`（不可检视），场景 4；结论均为 `Unverifiable`，不接管、不结算。
- `Fixture.SeedRunning/SeedClosed/SetOwner/SetWorktree` 直接构造崩溃残留、已终态 run 与被替换工作树（场景 3、4、6、8、10）。
- 槽位接管/拒绝在场景 10 用注入探针验证“记录进程”语义；场景 11/13 用真实进程验证同一结论。

### 关键行为（结论级清单）

同一机双宿主是竞争者：活的异己 writer/子进程、被复用 pid 或不可检视进程一律不接管、不结算；宿主被杀 → Unknown + 线程冻结 + 拒绝新派发 + 零重启；解决拒绝错线程/错 HEAD/缩写 SHA/坏 JSON/空作者，重复决定被幂等挡住，结算日志追加式，线程仅在显式解冻后继续；传输完成不记投递；无终态/截断流 → Unknown，原生失败永不可投递；飞行中 run 不可投递；已跟踪脏改动阻挡投递直至还原；丢回执重放同 revision、内容不同即冲突；已接受 revision 不可被静默替换；打回后的再交付得到 revision 2；接受 ≠ 集成；队列不为未注册线程造活（外键实证）；恢复只重排“从未进入派发器”的意图，不确定结果只 Abandon 交对账；宿主中途被杀后接管者不重发、不宣称所有权、不能把未结算 run 呈现为投递；交付必须等于本 run 自己记录的 settle 时 HEAD（借用他 run 的提交被拒、无溯源行既不可投递也不可被认证为成功、无观察者的宿主可传输不可认证、异常路径绝不补记 HEAD）。

## 未验证项（按契约保留，未标记为通过）

- 生产接线：App/服务如何构造并生命周期管理 `BackgroundHost`（端点文件位置约定、开机/关窗、崩溃重启策略）未实现，属主控范围。
- 未启动任何生产后台服务；未迁移生产 Pi/OMP 会话；未实现 AUTO。
- `accept`/integration 上层入口未接线：本模块只把 revision 记为待审，`RecordDecision`/集成标记由上层调用。
- 跨机器（远程宿主）竞争未验证：槽位身份只有本机 pid + 启动时间，无法观察另一台机器的进程；需要租约到期/心跳超时策略才能覆盖远程接管（见公共需求 5）。
- Linux/macOS 路径未在本机执行：`ProcessTree` 的 `/proc` 收养扫描与 cwd 解析仅按 Windows 实测，其余为按平台分支实现，未在 Linux 运行。
- 真实模型探针只覆盖 Codex CLI（`devin/swe-2`，单次最小回答提示）；Pi/OMP/DSH 经 C2 宿主路径未探针（其传输在既有 harness 记录中已单独实测）。
- 宿主自身的长驻可靠性（系统休眠、机器重启后 OS pid 复用真实发生、端口被其他程序占用后的重连）未做真实环境验证。
- UI/工作台展示宿主状态未做，事件推送为 60ms 轮询 U1 游标，非内存实时流。
- `Fufucode.Hosting.Tests` 未注册进 `Fufucode.sln`（越权），解决方案级构建未验证。
- rework 相关：反向 HEAD 溯源缺口（B1-HEAD-1）已闭合并有 24 项断言覆盖（`head:` 19 + `no-observer:` 5）；仍未验证的是跨机器场景下同一工作树的 HEAD 前进（本机 fixture 只有宿主自己会提交）。
- B1-HEAD-1 后未复跑真实模型探针（本轮规则明确禁止非必要探针）：探针结论仍以 `ebb6864` 那次的 `PROBE PASS 11 checks` 为准；settle HEAD 在真实 codex 子进程路径上的落盘，仅有同一段代码的 fixture 真实子进程证据，未在真实模型运行中重测。
- 旧行迁移未做数据回填：`task_id` 列对既有行为 NULL，按 fail-closed 拒收/拒认证；真实历史项目里有多少这类行未统计（本任务不触碰用户数据）。

## 公共需求（交主控，本会话未改动共享面）

1. 解决方案注册：把 `tests/Fufucode.Hosting.Tests/Fufucode.Hosting.Tests.csproj` 加入 `Fufucode.sln` 的 `tests` 文件夹（现只能单项目构建/运行）。
2. 生产宿主装配：新增后台服务装配点，注入真实 `HostOptions`（项目库路径、证据目录、hostId、端点文件路径约定）并管理 Start/Stop；`BackgroundHost` 目前只暴露构造函数与 `StartAsync/StopAsync/DisposeAsync`。
3. 客户端装配：App/`Integration/**` 使用 `HostClient`（端点发现、断线重连、持久化已处理游标）；UI 侧只读消费 `SNAPSHOT`/`EVENTS`。
4. 决定 `host_requests` 是否需要外键到 `harness_runs`：当前刻意不加（宿主必须先接受意图，之后由 `HarnessExecutor.Claim` 建立 run 行），若主控要求数据库级强约束需同步调整恢复语义。
5. 跨机接管策略：若允许远程宿主，需要心跳超时 + 显式接管授权（当前只有“记录进程可证明消失”一条路径，跨机不可观察）。
6. 上层交付决定入口：约定由谁在 `DeliveryReceiver` 之后调用 `store.RecordDecision` 与集成标记，并把 `ReceiveOutcome.Code` 映射到工作台展示。
7. `scripts/**`：若需运维可见的宿主 smoke/验收脚本，请主控新增；本任务未写脚本，未改 `docs/automation/contracts/**`。
8. 运行时登记表：真实 `codex.exe` 绝对路径目前由调用方提供（探针内硬编码），生产需接入运行时配置来源。
9. **宿主必须注入 `IGitEvidence`，否则其运行永远不可被认证为交付**（B1-HEAD-1 已闭合，剩余为装配决定，交主控）：`DeliveryReceiver`/`RunReconciliation` 现在要求 `harness_runs.settle_head` 非空且等于候选 HEAD。`BackgroundHost` 已注入 `GitCliEvidence.Shared`，但 `tools/Fufucode.HarnessHost/Program.cs` 用 `new HarnessExecutor(store, evidenceDirectory)`（无 hostId、无观察者），本轮按任务书未改动该工具；其后果如实记录：该宿主跑出的 run 仍会正常传输并被记为 `Completed`，但 `worktree_head`/`settle_head` 双空，任何交付卡都会被 `settle-head-unobserved` 拒收，且不能结算为 `Completed`。要让它可交付，主控需在该装配点补 `GitCliEvidence.Shared`（一行改动，属宿主装配范围，故未由本受限 worker 代做）。剩余诚实限制：无溯源的历史行（含本轮之前的所有 run）永不可投递，需要重派或显式结算；HEAD 相等只证明“本 run 退出时仓库停在它”，前提是该工作树是真实 git 仓库（非仓库 → `worktree-unreadable` 拒收）；跨机器/多宿主并发提交同一工作树未验证。

## 边界与停写声明

- 未集成 main、未 push、未改写历史、未清理任何工作树、未登录采购、未派下一任务包。
- 未修改权威开发计划/进度/全局缺陷登记；未改 `ExecutionContract`/交接协议文档。
- 保护继承状态：起点即为本任务书提交，四个增量线性叠加，历史提交（含 `14ff2b3`、`4ccaf6a`）未被重写。
- 本会话已停止写入。缺陷与风险全部记于本记录，由主控汇总。
- 接受模块 ≠ 整项验收：C1/C2 为可验证模块（提交待验收），工作台端到端产品行为与生产接入仍未验收。

日期：2026-10-11。执行：本会话原模型（L4，未更换）；模型试跑显式 `devin/swe-2`（免费档），仅一次最小回答调用，无其他模型使用。
