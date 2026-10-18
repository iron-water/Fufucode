# C4 半自动垂直闭环

难度 L4，由 M 单写公共接线与验证工装。起点 main@864c593；不新建昂贵测试 lead。模型试跑唯一允许 `devin/swe-2`，实际调用须显式指定，失败不换付费模型。

范围：`tests/Fufucode.Control.Tests/VerticalChecks.cs`、测试入口及本任务记录。真实阻塞修复扩至HarnessRuntime/BatchAdapters、ProcessTree/RunReconciliation和Host端点读写及独立反例。仅临时数据库和独立临时 Git 仓库，不迁移生产数据，不启动生产自动消费器，不恢复旧调度，不修改用户工作树。运行时描述由显式本机文件提供，路径不写入源码。

验收：两个批准任务入队、唯一宿主执行、停止后收取真实提交与结构化卡，三个部分积压；人类串行审核，A 一个部分接受、另一个打回，B 暂不审核。原模型/原会话只修 A 指定部分，保持兄弟卡与 B 不变；新 revision 独立接受，接受仍无集成记录。经明确授权，在独立集成仓库实际收编并重新检查文件，然后记录真实集成 SHA。重复消费/丢回执和空闲观察不多启进程。默认测试用真实子进程协议夹具；真实模型必须显式 opt-in，单次等待有界，失败保留证据并如实 open。

入口计划：`--c4-only` 为无需网络的闭环行为门；`--c4-live <runtime.json>` 为显式免费模型真实任务门。模型完成不替代独立文件/Git/卡身份检查；夹具不是云模型，临时集成 SHA 不是 Fufucode 主线发布证据。C5 原生图和生产服务另验。

首次真实Codex试跑发现默认workspace-write拒绝`.git`写入：不能用transport Completed/卡中的module_ready掩盖未提交成果。增量开放受信HarnessRuntime的可选CodexSandbox（默认不变，旧payload不新增null字段），限定CLI支持的三个值；隔离真实试跑显式danger-full-access，客户端返工不能更改该部署描述，恢复继承原值。增加真实持有子进程的超时退出反例；仅核实本测试supervisor/child PID与开始时间后终止自己的探针，不接管其他进程。
