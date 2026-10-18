# U1 交付记录：持久任务与门房规则

## M 主区收编交付卡（2026-10-11）

- 任务 U1；接受运行 `d92ab3ab5db24f08b9bf62ce6ec103aa`；需求版本 0；精确来源 `428a0267e18708a1cc75b2882202431740571554`。accept、真实来源 HEAD、停止 writer 与允许范围重新核对一致；来源工作树干净。
- 原始起点 `48ade612374464c9572eb176aa2348e56c3f37b4`；主区开始 `b74293d4ffed8fef071c2dcc3780ab10b3c564b1`；真实 no-ff 合并 `82873da4bb8dea30f92e0a7e1714fb0cc55472de`，父提交为主区开始及精确来源。
- 仅收编 Core/Automation 两个源码文件、独立测试工程/Program 与本记录，共五个批准文件。SDK 默认包含源码，独立测试引用 Core；无需新增项目引用、共享宿主或 UI 入口。生产接线保留为 U2 工作，不越 G1/G2 门。
- 主区实际运行批准脚本 `pwsh -NoProfile -File docs/automation/contracts/U1-verify.ps1 -Repository C:/Projects/Fucode/Fufucode`：exit 0，临时 SQLite 持久规则行为测试 ALL PASS；App Release 构建 0 警告、0 错误。未修改验收脚本、未放宽断言；没有以构建代替行为验证，也未启动模型试跑。
- 行为证据包括同线程多任务/部分打回隔离、审核必填身份与过期身份拒绝、相同交付重放幂等/冲突内容拒绝、同线程租约互斥、接受与集成依赖区分、claim 不明冻结及持久重开恢复。均为库级临时数据检查，不是生产宿主或原生 UI 验证。
- 证据：`tmp/agent-gate/d92ab3ab5db24f08b9bf62ce6ec103aa.integration-verify.log`；继承保护清单 `.integration-preserve.json`。继承跟踪及未跟踪文件逐项 SHA-256 校验，不夹带其他任务内容。
- 未验证：G1/G2、U2 脚本适配/迁移、真实 Pi/OMP 执行、关窗运行、睡眠/重启宿主恢复、UI 原生交互、端到端生产工作台。批准脚本虽构建 App，未运行原生 UI 交互。
- 状态：U1 独立持久规则模块已收编 main，不代表产品整项验收或上述门通过。未 push、登录、派工、删除工作树或更改权威门禁；下一步由外部 M 更新权威记录并在独立授权下推进后续接线。

日期：2026-10-11。执行：devin/swe-2（免费，明确成本选择）；无模型试跑。
工作树：`C:\Projects\Fucode\worktrees\auto-project-U1`，分支 `agent/prod-readonly-20261010-U1`。
范围：`src/Fufucode.Core/Automation/*`、`tests/Fufucode.Automation.Tests/*`、本记录。未改共享会话宿主、UI、权威进度/计划。

## 实现

`src/Fufucode.Core/Automation/AutomationModels.cs` — 领域模型：
Thread → Task → DeliveryPart → DeliveryRevision；PartDecision（accept/rework/blocked/defer）；
AutomationEvent（单调游标）、AutomationCommand（commandId）、AutomationClaim（Claimed/Unknown/Resolved）、
WriteLease（TaskWriter/Integrator）、TaskDependency（版本化 DAG 边，可要求集成证据）。

`src/Fufucode.Core/Automation/AutomationStore.cs` — 每项目一个 SQLite 文件（schemaVersion=1），全部写操作走事务：

- 部分打回只改对应 (part, revision)，同线程其他 Task/Part 与已接受决定不变；
  重新交付产生新 revision_number，历史决定行插入即不可覆盖（UNIQUE(part_id, revision_number) + 仅 Delivered 态可决定）。
- 决定仅 accept/rework/blocked/defer；rework 必填 findings。
- 事件按 event_id 去重：同 payload 重放返回同游标，异 payload 拒绝；命令按 command_id 去重，重放不重复执行 handler。
- 投递前持久 claim；MarkClaimUnknown + FreezeThread 处理启动/投递不明；RecoverAfterCrash 重启后对账：Claimed→Unknown、所属线程 Frozen，不自动重发。
- 版本化 DAG：IsTaskUnlocked 按所需 part 的 Accepted 与（可选）integration mark 判断；AddDependency 拒绝环。
- 撤销围栏：integration_marks 只允许打在已 Accept 的 revision 上。
- 单写入租约：TaskWriter 每 task、Integrator 每 project，过期可接管，被取代租约不可释放。
- 冻结/解冻、线程完成判定、快照后按游标补齐（ListEventsAfter）。

`tests/Fufucode.Automation.Tests/` — 独立可执行测试工程（console harness，无外部测试框架依赖，不接入 Fufucode.sln），11 组 39 项断言，全部使用临时目录数据。

## 实际验证

- `dotnet build src/Fufucode.Core/Fufucode.Core.csproj` — 0 警告 0 错误。
- `dotnet run --project tests/Fufucode.Automation.Tests/Fufucode.Automation.Tests.csproj` — ALL PASS（51/51）。
  首轮曾发现 `ResolveClaim` 允许 Unknown→Resolved 缺陷（1 FAIL），修复后复跑全绿。

## Rework 修复（M 审核 review.decision，runId 561af0d2…，reviewedHead 8231b41）

- U1-R1：`RecordDelivery` 先做绑定身份查重——同 (partId, runId, headSha) 重放返回既有
  revision（不再新增、不重发事件，允许调用方换了 revisionId）；同 run 不同 HEAD 在任何
  变更前拒绝。新增重放/冲突/重开断言（12 组 8 项）。
- U1-R2：TaskWriter 租约 scope 从 taskId 解析为所属 threadId，固定线程跨任务单写入；
  不同线程相互独立。新增同线程竞争与跨线程独立断言（9b 组 2 项）。
- U1-R3：`RecordDecision` 新增可选 expectedRevisionNumber/expectedRunId/expectedHeadSha，
  在事务内对当前 revision 原子校验，旧 review 不能接受替换后的 revision。
  新增 stale revision/runId/HEAD 拒绝与匹配身份接受断言（13 组 5 项）。

## Rework 第二轮（review.decision，runId fe492ffb…，reviewedHead bc86f5a）

- U1-R3 回归：expectedRevisionNumber/expectedRunId/expectedHeadSha 改为必填
  （无省略身份的重载）；空 runId/HEAD 在参数校验即拒绝（ArgumentException），
  revision/runId/HEAD 在事务内原子校验。新增缺省/部分身份与 stale 值回归断言。
- U1-R1 回归：重放守卫同时校验 cardJson——同 (partId,runId,headSha) 但卡内容
  不同的投递按冲突拒绝且无任何变更（重开后仍只有原 revision/事件）；仅完全
  相同内容的重放幂等返回既有 revision。
- 测试 51 → 56 项，全部 PASS。

覆盖：多任务同线程、部分打回隔离、决定不可变、事件/命令去重、claim 生命周期与不明冻结、
DAG 解锁（accept 不够，须集成标记）、环拒绝、撤销围栏、单写入/集成租约、崩溃恢复与重开身份重建。

## 未验证门禁（按契约保留）

- G1/G2 真实接线：本模块未接共享宿主、UI、真实 Pi/OMP 会话；不声称产品可用。
- 既有 v1 脚本协议未升级；本交付是新协议数据层，适配与迁移属 U2。
- 无真实关窗运行、睡眠/重启恢复实测（仅有库级 RecoverAfterCrash 行为测试）。
- 无主区集成验证；module_ready，接线归 M。

## 接口需求（待 M 接线）

- U2 宿主侧调用 `AutomationStore`：命令入口用 `RunCommand` 保证幂等；UI 观察用 `ListEventsAfter(cursor)`。
- 冻结由宿主在 claim 不明/M 状态不明时显式调用 `FreezeThread`。
- 集成写入者必须先取 `WriteLeaseKind.Integrator` 租约，集成成功后 `MarkIntegrated`。
