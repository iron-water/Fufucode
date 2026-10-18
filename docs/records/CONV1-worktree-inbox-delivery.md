# CONV-1 交付记录：受控工作树生命周期、持久跨线程信箱与收卡看门狗

日期：2026-10-16
任务：CONV-1（AUTO-LOOP 核心底层：工作树受控管理 + 持久消息信箱 + 宿主终态自动收卡看门狗）
工作树：C:/Projects/Fucode/worktrees/conv1-worktree-inbox
分支：agent/conv1-cdd

---

## 一、交付范围

1. **受控 Git 工作树生命周期管理（WorktreeLifecycleManager）**：
   - 声明式意图持久化（`DeclareIntent`），意图先行，不盲目操作磁盘；
   - 路径边界强校验（`PathKey` 唯一索引，防路径逃逸，防重复占用）；
   - 真实 `git worktree add` 创建并核验真实 HEAD；
   - 幂等复用与安全释放（`ReleaseOwnership` 仅改变持久状态，绝不擅自删除用户代码）。

2. **跨线程可靠事务持久信箱（PersistentMessageInboxOutbox）**：
   - 基于现有 `AutomationStore` 事务机制，实现跨线程消息持久队列；
   - 消息体严格绑定 `projectId`、`threadId`、`taskId`、`runId`、`deliveryRevision`、`headSha`；
   - 全局稳定 `eventId` 去重，保障顺序消费游标，进程崩溃重启后游标与去重状态完好恢复。

3. **宿主主动终态与交付卡自动捕获（HostDeliveryWatchdog）**：
   - 彻底打破“模型必须记得调用发消息工具”的脆弱性假定；
   - 宿主在子执行者退出或终态时主动扫描工作树交付卡并校验 `writerStopped: true` 与 `head`；
   - 自动在数据库事务中引发 `delivery.received` 进入待审队列；
   - 遇到缺卡、坏卡、未停写或异常退出时，主动引发 `delivery.failed`，绝不无休止静息等待！

---

## 二、实际验证结果

- `dotnet build Fufucode.sln`: 19 个项目全部通过，**0 警告 · 0 错误**。
- `dotnet run --project tests/Fufucode.Conv.Tests`: **71 项端到端架构断言全部通过**：
  - 工作树生命周期 16 项全通（意图声明、幂等性、路径逃逸拒绝、真实创建、HEAD核验、冲突拒绝、安全释放）；
  - 持久信箱 17 项全通（消息入队、稳定 eventId 去重、顺序游标消费、防串线泄漏、跨进程重启幂等）；
  - 收卡看门狗 38 项全通（真实终端运行捕获、有效收卡、重复观测幂等、缺卡结构化失败、破损卡拒绝、未停写拒绝、篡改HEAD拒绝、任务不匹配拒绝、异常退出显式失败、在途运行推迟观测）。

状态：module_ready，writerStopped: true。
