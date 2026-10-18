# 项目派发恢复

实际启用证据（2026-10-11 06:59UTC）：提交48ade61；生产workflow PID21936，独立wscript父PID60240；门房PID33452沿用原Scheduler。TaskControl宿主PID54440。U1实际Codex agent PID36396/worker48880，runId561af0d2689a4cb6a4645d1c31ccbaaa；O6-RULES实际OMP agent PID53044/worker34236，runId0ba0d1d0c0d4463a90d2766e54efdff3。两者state均running且模型显式devin/swe-2，工作树分别auto-project-U1、auto-project-O6-RULES。workflow状态waiting_events表示正在等待交付，不是暂停。控制台已重启加载新版本，http://127.0.0.1:18765。尚未交付，不宣称这些任务已完成。

用户明确要求恢复实际开发。停顿原因：生产计划仅包含已完成的D4-SNAPSHOT，完整候选计划未审批启用，生产paused标记仍在。不是模型繁忙。

启用项目级23项执行契约及27项规划清单，保留D4原运行、原决定和集成记录。U1及O6-RULES允许先行，实际执行模型均显式devin/swe-2；固定生产M沿用原批准模型/线程。G1/G2/S2等验收门不改变，不接管旧未知会话。

版本化控制支持撤销围栏、原工作树保留、新版本依赖重映射、命令去重、投递不明冻结，以及M忙时持久排队。独立Task Scheduler控制宿主监听本地事件，空闲不调用模型。

增量验证：控制台12项通过；原生撤销/重建/修改/排队通过，证据临时仓库fufucode-control-77f8c85c519847d1ac24e5c1ff8731c0；实际派发故障隔离通过，证据fufucode-isolation-28d0825ba538449ebd0b4a6be3255a22；所有权/范围/撤销规则通过，证据fufucode-revision-rules-2505b0b8f2604d5d95e195198b7a31f0。以上本地测试无模型调用。

此前完整闭环证明不代表新源码完整冻结证明；本次为既有生产流程的定向验证增量恢复，不改写旧证明。免费跨harness完整回归仍独立运行，睡眠/整机重启、原生UI和所有未来任务验收不宣称完成。保持15分钟Desktop heartbeat暂停。

操作：在C:/Projects/Fucode/Fufucode执行 `pwsh -NoProfile -File scripts/agent-workflow.ps1 -Mode Status`。暂停/恢复分别使用该脚本Mode Pause/Resume及agent-gate-control.ps1同名Mode。事件日志tmp/agent-flow/events.jsonl，任务队列tmp/agent-queue，固定M记录tmp/agent-gate/manager.json。
