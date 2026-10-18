任务：O6-RULES｜难度：L4（跨状态项）/原任务等级见session-difficulty｜推荐主模型：group/auto-hy4-preview-f｜实际执行：devin/swe-2（免费，明确成本选择）
先读AGENTS、AI_DEVELOPMENT_PLAN、execution-contract、session-handoff-protocol、agent-event-protocol和本任务书。真实主区在C:/Projects/Fucode/Fufucode，但只在已分配独立worktree实施。
只实现独立本地调度规则与持久状态：一次性/周期、时区/DST、漏跑策略、幂等、取消、恢复、可注入时钟。规则计算不能启动模型或访问真实用户数据。不接后台宿主/App共享入口；O6的后端接线仍依赖D5/G2。行为测试覆盖春季跳时、秋季重复、重启、取消和重复触发。
本轮允许路径：src/Fufucode.Core/Scheduling/*, tests/Fufucode.Scheduling.Tests/*, docs/records/O6-RULES-delivery.md
范围不足先交协调卡，不扩写共享入口。新增独立可执行测试工程；模型试跑及子代理必须devin/swe-2，不启动其他任务，不push/登录/删除工作树/改权威记录。所有验证用临时数据。每个必要增量明确git add并commit，保护继承内容；交付记录完整列实现、实际测试、未验门禁。最终回复必须是单份纯JSON：{"schemaVersion": 1, "type": "delivery.card", "taskId": "O6-RULES", "status": "module_ready", "summary": "事实", "commits": [], "changedFiles": [], "verification": {"passed": [], "failed": [], "notRun": []}, "evidence": [], "integrationNeeds": [], "blockers": [], "writerStopped": true}
所有数组保持数组，commits列真实SHA。模块待共享接线是有效交付，不假称原生/整项验收完成。
