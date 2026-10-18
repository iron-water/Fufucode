# 受管agent结构化事件协议 v1

2026-10-10。执行器为OMP JSONL；M仍拥有审核与集成判断。协议不把文字中的“已整合”或进程exit0当验收。实现在scripts/agent-events.ps1和scripts/agent-queue.ps1。

## 事件信封

每次运行manifest含schemaVersion、独立runId、task、worktree、base、model和protocol=omp-json-v1。events目录每个事件一个原子JSON文件；序号在本运行内固定，跨运行按runId隔离。已存在相同事件重放不重复写；内容不同拒绝覆盖。允许多运行的待审交付同时积压。

```json
{
  "schemaVersion": 1,
  "eventId": "<runId>/delivery.received",
  "runId": "<runId>",
  "taskId": "S1",
  "sequence": 4,
  "type": "delivery.received",
  "occurredAt": "<UTC ISO8601>",
  "payload": {
    "head": "<40位SHA>",
    "sessionId": "<OMP会话ID>",
    "cardStatus": "module_ready",
    "receipt": "delivery.json",
    "legacy": false
  }
}
```

| 序号 | type | 含义 |
| --- | --- | --- |
| 1 | job.created | 派单记录已落盘，尚未证明agent启动 |
| 2 | agent.started | 受管OMP已启动，记录PID和时间 |
| 3 | agent.exited | OMP已退出，记录真实exitCode/HEAD/超时 |
| 4 | delivery.received | 终态交付卡校验通过，只是待审 |
| 4 | delivery.invalid | 异常退出、缺终态、坏JSON、错误任务/提交等 |
| 5 | review.decided | M结构化审核决定已落盘 |
| 7 | supervisor.failed | 管理进程故障，记录可能仍活跃的agent |

received与invalid同槽互斥。状态快照state.json供兼容；有delivery.received且无review.decided的任务才属于待审队列。M必须再核对进程已结束和实际工作树，不以事件代替写入锁。结构化事件仅由受管脚本生成，执行者不能自行发accept事件。

## 执行者交付卡

OMP以--print --mode json运行，原始协议保存stdout.log。读取在进程退出之后发生：最后terminal agent_end的最后assistant必须正常stop、无未完成工具调用。只取该消息的text，不取用户提示、工具输出或中间消息。最终text只能一个JSON对象，可用一个json围栏；Launch自动附加要求。

```json
{
  "schemaVersion": 1,
  "type": "delivery.card",
  "taskId": "S1",
  "status": "module_ready",
  "summary": "样稿已交付，生产风格待用户选择",
  "commits": [],
  "changedFiles": [],
  "verification": {"passed": [], "failed": [], "notRun": []},
  "evidence": [],
  "integrationNeeds": [],
  "blockers": [],
  "writerStopped": true
}
```

status枚举：module_ready（有效模块待M接线）、submitted（提交待审核）、blocked（须有blockers）、incomplete。数组填真实事实，不复制示例冒称完成。commits仅本次运行起点之后的完整SHA，必须存在于实测Git提交列表；此前轮次证据放evidence。verification各组分别记录通过/失败/未运行，不由监督器推断。M仍独立复跑必要验证。进程成功但卡无效产生invalid，不生成有效交付。

## M审核决定

```json
{
  "schemaVersion": 1,
  "type": "review.decision",
  "runId": "<对应runId>",
  "taskId": "S1",
  "reviewedHead": "<被审核40位SHA>",
  "decision": "accept",
  "integrationStatus": "pending_wiring",
  "mainSha": null,
  "evidence": ["真实核查或验证日志位置"],
  "findings": [],
  "nextAction": "由M接线；仍未整合"
}
```

decision仅accept/rework/blocked/defer；必须证据和nextAction，rework须findings。integrationStatus仅not_integrated/pending_wiring/integrated；只有accept可写integrated且必须完整mainSha。accept不是整项验收或依赖门通过；incomplete/blocked卡不可accept。M核对真实main提交与验收门后才填写integrated，脚本只校验数据，不执行merge。nextAction里的“未整合”等文字不参与分流。

M写一个输入JSON文件，再执行`pwsh -NoProfile -File scripts/agent-queue.ps1 -Mode Review -JobDirectory <运行目录> -ReviewFile <决定文件>`。入口检查无受管活跃写入者、记录PID不存活、工作树HEAD没变；task/run/HEAD必须一致。原子事件加review.json快照，重放去重、冲突拒绝，不重新收编。

## 已在运行的旧任务

不重启S1、不注入提示、不读取运行中正文。旧任务停止后用ConvertLegacy转换事件；旧卡正文不猜字段：card=null、legacy=true、requiresManualCard=true，只保留原始卡日志引用。M人工解析/独立核查后仍以新review.decision做决定。重复转换保留原始日志引用。旧故障也可转换invalid；running不能转换。监督器异常后活跃子进程必须先处理，不能自动重派。

JSON原始流不是通知接口。Desktop heartbeat保持PAUSED；CLI事件门房由Task Scheduler托管，本地等待/补扫不调用模型，待审交付才投递独立审核。真实机制夹具已验证原会话回修和接受；自动生产收编/派工仍open，不能用免费夹具冒充产品门通过。见M-CLI-event-gate-handoff-2026-10-10.md。不声称对已关闭Desktop即时推送。
# U 产品演进说明（2026-10-10）

用户要求自动开发图以对话线程为节点，一线程包含多个任务，打回绑定具体交付部分。U1/U2 将显式建模 Thread、Task、DeliveryPart/Revision，并保留 threadId/taskId/partId/runId/HEAD/decision 的身份约束。**本文件现有 v1 脚本协议没有因此自动升级**；不修改在途卡、不覆盖已有review、不把示例HTML当新协议验收。旧CLI运行保持原身份，新增版本化适配与迁移验收见 [U任务书](automation-workbench.md)。
