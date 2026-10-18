# M：受管任务结构化事件v1

2026-10-10，用户要求将无人值守流程改为结构化事件。实现脚本agent-events.ps1，更新agent-queue.ps1；协议真源docs/tasks/agent-event-protocol.md。没有创建新的Desktop主会话、重启App或接管用户会话。

## 改动

- 新OMP运行显式--mode json，运行独立runId，启动prompt自动附JSON卡协议。仅进程停止后读取terminal agent_end最后assistant正常stop文本；用户提示、工具输出和中间文本不作交付。
- job.created/agent.started/agent.exited/delivery.received或invalid/review.decided/supervisor.failed原子事件，唯一序号文件按run隔离。重放去重，内容冲突拒绝覆盖。delivery仍待M独立验收。
- M明确accept/rework/blocked/defer，绑定taskId/runId/reviewedHead、证据、下一步及integrationStatus；不按“已整合”正则。module_ready允许M接线，不一律打回受限worker。真正集成必须记录mainSha，审核accept不等同整项验收。
- Review入口核对进程结束、受管worktree锁、实际HEAD一致。旧运行ConvertLegacy只保留原卡引用，card=null/requiresManualCard=true，不猜JSON字段；running不能转换。
- ACTIVE heartbeat fufucode已更新按事件筛选和Review入口落审核，不绕过结构化校验。仍15分钟检查，不冒称即时事件推送。

## 验证

1. 免费devin/swe-2真实无工具JSONL探针退出0，tmp/events-probe.jsonl；确认实际session/message_end/turn_end/terminal agent_end结构，不从文档猜协议。
2. `pwsh -NoLogo -NoProfile -NonInteractive -File scripts/agent-events.tests.ps1 -RealProtocolLog tmp/events-probe.jsonl -IntegrationWorktree C:/Projects/Fucode/Fufucode-agent-protocol`：35断言通过。临时夹具C:/Users/mio/AppData/Local/Temp/fufucode-event-tests-18e080f0906a4f53875095c05e332b8f。覆盖错误任务/状态/SHA/字段、非布尔标记、缺终态/坏JSON/工具未结束、运行中/退出失败拒收、提示词中的卡不能冒充最终卡、多卡积压、正文误导、旧HEAD/审核冲突、重复事件/不同属性顺序、旧格式转换/重放/在途拒绝。
3. 独立协议测试worktree（仅夹具，无源码改动）：C:/Projects/Fucode/Fufucode-agent-protocol，detached2a62b69。通过新版Worker显式devin/swe-2、TransportTest启动event-transport-01，exit0/无改动，生成01~04事件、sessionId及结构化待审卡（incomplete，夹具不宣称产品交付）。
4. 用Review入口写defer/not_integrated，重复执行相同决定成功且只保留一份05 review.decided。证据tmp/agent-queue/event-transport-01/events、delivery.json、review.json。真实运行ID e57f04e5fb20448badeb8ca680e29e72。未按该夹具整合代码。

## 保留边界

旧S1 s1-02检查时仍running，只读state/PID，未重启、未读交付正文，不将旧worker强制切为JSON。未来在结束后转换。真实生产卡、修复、公共接线收编、事件驱动CLI主agent、强制超时及重启恢复没有新增通过证据。脚本/文档改动不涉及App代码，不重复跑App全量。既有用户demo与未跟踪内容保持未暂存；不push、不清理worktree。
