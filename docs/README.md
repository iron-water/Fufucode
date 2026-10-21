# Fufucode 文档导航

日期：2026-10-21。本文是 docs 的唯一入口。

## 一、当前权威（按优先级）

1. [AGENTS.md](../AGENTS.md)：协作、文件所有权、当前后端与构建约定。
2. [决策记录](decisions.md)：追加式权威决定；以最新日期条目为准。
3. [前端能力分级与生命周期](design/frontend-capability-hierarchy.md)：2026-10-20 前端权威基线。
4. [设置页设计规范](settings-design-spec.md)：设置页结构、样式与硬性纪律。
5. [项目总原则](design/project-principles.md)：目录、状态、分级、验证的总纲。
6. [执行契约](tasks/execution-contract.md)：先读顶部状态说明；后端默认已由 Pi 改为 FUMO。
7. [当前进度](progress.md)：追加式进度日志，只看最新日期段。
8. [运行基线](runtime-baseline.md)：顶部已声明 FUMO 默认；Pi 段落是历史基线。
9. [验证记录](verification.md)：历史验证；最新验证以 `records/` 与交付记录为准。

## 二、前端设计文档状态

| 文档 | 日期 | 状态 |
|---|---|---|
| [frontend-capability-hierarchy.md](design/frontend-capability-hierarchy.md) | 2026-10-20 | **当前权威**：5 个物理承载、7 级梯队、三大藏点 |
| [conversation-workbench-v4.md](design/conversation-workbench-v4.md) | 2026-10-15 | 自动开发工作台的当前产品决定；不覆盖全局前端分级 |
| [frontend-functional-plan.md](design/frontend-functional-plan.md) | 2026-10-05 | **历史提案，已被取代**；保留三栏方案讨论 |
| [frontend-design-guideline.md](design/frontend-design-guideline.md) | 2026-10-01 | **历史研究，已被取代**；保留五区骨架证据 |
| [frontend-interaction-inventory.md](design/frontend-interaction-inventory.md) | 2026-10-06 | 小交互讨论清单，不等于已实现 |

## 三、执行与任务

- 当前执行协议：[execution-contract.md](tasks/execution-contract.md)、[session-handoff-protocol.md](tasks/session-handoff-protocol.md)。
- [launch-prompts.md](tasks/launch-prompts.md) 中的 D/O/U 段落是历史模板，不再直接派发。
- [development-topology-v2.md](tasks/development-topology-v2.md) 是 2026-10-11 的旧拓扑，保留追溯。
- `tasks/hourly-*`、`tasks/M-E7-*` 等是历史派工材料，不代表当前队列。
- [archive/README.md](archive/README.md) 是历史资料说明，不是待执行指令。

## 四、记录、证据与调研

- [records/README.md](records/README.md)：记录分类、命名与状态规则。
- [records/INDEX.md](records/INDEX.md)：交付、日志、证据的入口索引。
- [records/evidence/](records/evidence/)：截图、日志与验收证据。
- [research/](research/)：内部分析与设计参考，不自动成为实现承诺。
- [research-tasks/README.md](research-tasks/README.md)：**已归档**的旧外部认领任务，不再派发。
- [research-results/README.md](research-results/README.md)：外部调研结果；R01 evidence 是第三方证据，R05 是原型副本。
- [frontend-prototype/README.md](frontend-prototype/README.md)：可交互前端原型；不是产品验收证据。

## 五、归档与运维

- [archive/](archive/)：旧轨道任务、分轨进度、历史队列、旧自动化契约。
- [automation/README.md](automation/README.md)：旧 D/O/U 机器契约与验证脚本，已不参与当前调度。
- [operations/README.md](operations/README.md)：运行维护入口；`pi-upgrade-sop.md` 是历史 Pi 规程。
- [面向人类/README.md](面向人类/README.md)：面向用户的成品说明。
- [人类意见/README.md](人类意见/README.md)：用户原始意见，不是实现规格。

## 六、使用规则

1. 新文档先写 `status`（current / historical / superseded / evidence）、日期和来源。
2. 当前结论只放“当前权威”；历史原文保留并加 `SUPERSEDED` 横幅，不改写历史。
3. 旧路径查询只用于追溯；[records/document-path-map.json](records/document-path-map.json) 是 2026-10-05 的一次性迁移表，不是当前索引。
4. 证据放 `records/evidence/` 或 `research-results/`，不要把截图、日志和脚本混进设计文档。
5. 任何“Pi 默认”表述先确认日期；2026-10-17 之后默认是 FUMO。
