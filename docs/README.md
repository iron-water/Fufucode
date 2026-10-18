# 文档导航

- [Pi 原生能力接入核查（1.1.0）](research/pi-native-capabilities-2026-10-08.md)：codemode、MCP、Durable、subagent 的入口与适配边界。
- [Pi 1.1.0 升级交付](records/pi-upgrade-2026-10-08.md)：锁定版本、验证、产物和回退限制。

## 核心入口

- [权威开发计划](AI_DEVELOPMENT_PLAN.md)：目标、职责、开发与验证规则。
- [当前开发拓扑 v2](tasks/development-topology-v2.md)：实际成果盘点、三阶段交付、依赖与旧任务承接。
- [开发控制架构](design/autonomous-development-control.md)：三模式、委托、自主决策、纠偏与项目验收；上层设计与底层执行分开。
- [当前进度](progress.md)：交付状态与下一步。
- [决策记录](decisions.md)：追加式记录，原文和历史路径保留。
- [运行基线](runtime-baseline.md)：实际 Pi 版本、协议与行为证据。
- [验证记录](verification.md)：已运行验证及未验证边界。

## 分类目录

| 目录 | 内容 | 常用入口 |
|---|---|---|
| `design/` | 产品功能、交互清单、CLI 和协作规格 | [前端功能规划](design/frontend-functional-plan.md)、[小交互清单](design/frontend-interaction-inventory.md) |
| `frontend-prototype/` | 可直接打开的前端交互原型及交付说明 | [原型说明](frontend-prototype/README.md)、[打开原型](frontend-prototype/index.html) |
| `research/` | 已有调研和设计参考，不自动作为实现承诺 | [Codex 功能研究](research/codex-feature-study.md)、[多 agent 研究](research/multiagent-collab-study.md) |
| `research-tasks/` | 当前外部 agent 可认领的研究任务书 | [认领协议与四项任务](research-tasks/README.md) |
| `research-results/` | 外部 agent 按 Rxx 独立交付的结果目录，认领后创建 | 见认领协议 |
| `operations/` | 运行维护和升级操作规程 | [Pi 升级规程](operations/pi-upgrade-sop.md) |
| `records/` | 审查、反馈、故障、验证日志与整理记录 | [本轮目录整理](records/docs-organization-2026-10-05.md) |
| `archive/` | 旧轨道任务、分轨进度、历史队列与调度材料 | [历史资料说明](archive/README.md) |
| `面向人类/` | 面向用户阅读的自包含报告 | 保持原文件名 |

## 使用规则

五个核心入口保留原路径，满足 AGENTS.md 的引用约定。原型目录不移动，既有浏览器地址及图片相对路径不变。
新产品方案放 design，调研放 research；未定任务进入 research-tasks，交付进入 research-results，正式决定仍由主 agent 追加到 decisions。
archive 是历史参考，不是待执行指令；旧 pending 不代表新授权。旧固定五区设计文档仍在 design 供参考，当前三栏可配置方向以最新功能规划和用户意见为准。
历史决策原文不改写；遇到旧路径，可查 [路径迁移表](records/document-path-map.json)。目录整理不意味着旧结论仍然适用。
