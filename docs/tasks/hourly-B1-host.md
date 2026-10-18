# B1 工作台基础：C1 → C2

推荐模型 group/auto-qfmodel，难度 L4：持久所有者与受控恢复跨进程契约。隔离实现，不触碰生产旧门房。沿用 Core/U1 与 HarnessExecutor，不另造调度策略。主控负责公共接线及独立审核。

起点：主线14ff2b3及本任务书提交，新Codex隔离工作树。先读AGENTS、AI_DEVELOPMENT_PLAN、execution-contract、session-handoff-protocol、development-topology-v2、harness-execution-backend、automation-workbench及对应已交付记录。模型试跑显式devin/swe-2，普通本地测试无需模型。

允许：src/Fufucode.Core/Automation/Harnesses/**；新src/Fufucode.Core/Automation/Hosting/**；tests/Fufucode.Harness.Tests/**和新独立宿主测试工程；docs/records/B1-host.md。AutomationStore/AutomationModels的共享修改、项目引用/解决方案注册、App、Pi和所有scripts由主控负责，确需改动提交精确接口与接线需求，不越权。不能修改权威计划/进度/全局缺陷登记。

内部步骤：
1. C1：核实项目/线程/工作树所有者与子写入者，处理Unknown时检查PID及启动时间和会话/HEAD证据；活跃或身份不明不得接管。提供显式受控解决入口，不能直接改库重跑。交付接收校验run/task/真实HEAD/终态，accept仍上层负责。
2. 独立验证重复投递、丢回执、退出重启、坏终态、工作树替换、两个宿主竞争、不明执行不重发。夹具仅临时目录，不操作用户数据。
3. C2：在C1增量通过后实现一个唯一后台执行宿主及客户端命令/快照/事件续读，关闭客户端后后台任务继续；重连补事件、积压恢复、明确停止及退出。不实现AUTO或新调度器，不迁移生产Pi/OMP会话，不启动生产后台服务；共享契约变更交主控。

交付条件：Core及受影响测试构建0警告0错误，必要行为测试实际通过；真实子进程生命周期证据与模拟分别记录。无需模型的子进程夹具优先；模型集成探针devin/swe-2。隔离宿主可运行及客户端关闭/重连/重启行为有实证；工具或公共接线阻塞如实部分交付，不以多写测试冒称生产接入。

按可验证增量提交，完整卡写B1-host记录及终答：实际工作树、起点/HEAD、提交/范围、命令结果/证据、未验项、共享需求、已停止写入。不能集成main、push、清理、登录采购、擅派下一包。缺陷写本任务记录由主控汇总；保护继承状态。接受模块与整项验收分开。
