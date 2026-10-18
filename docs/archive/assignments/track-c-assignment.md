# 轨道 C 任务书：UI 外壳与消息时间线（mock 数据）

> 本文件是轨道 C agent 的启动指令。开工前先读 `docs/AI_DEVELOPMENT_PLAN.md` 第 6.6、9 节与第 10 节 M2，以及根目录 `AGENTS.md`。

## 入场条件（未满足前不得开工）

1. 轨道 A 已在 `docs/progress.md` 确认：`src/` 目录骨架落定，且 `src/Fufucode.App/` 归属轨道 C（A 在 M2 之前不会创建它）。
2. 轨道 A 已提交 RPC DTO 与会话接口定义（哪怕客户端不完整），或明确声明接口由 C 先行定义、A 对接。

在满足前，C 只允许在 `docs/` 下追加设计笔记，不写代码。

## 目标

M2 的界面部分提前并行：Avalonia 外壳（项目/会话侧栏、对话时间线、输入区、状态栏）与消息时间线组件，全部由 mock 数据驱动，不依赖真实 Pi。

## 文件范围（只允许写这些）

- `src/Fufucode.App/` —— Views、ViewModels、样式、组合入口（Mock 实现放本项目的 `Mock/` 子目录）
- `docs/archive/progress/progress-c.md` —— 进度与验证记录（自建）

## 实现要求

1. 时间线：用户消息、assistant 文本、可折叠思考块、工具卡片（输入/输出/状态）、错误条目；流式追加用 mock 生成器模拟 delta 节奏。
2. 消息重建规则按计划 §6.6：文本/思考/工具调用分块维护，最终消息整体替换，不重复追加；流式刷新合并执行。
3. 交互细节按计划 §9：中文输入法组合期间 Enter 不误发送；流式期间上翻历史不被拉回底部，提供"回到最新"；代码块复制；长输出折叠。
4. 定义 `IChatSession`（发送、取消、事件流）接口 + Mock 实现；这是与轨道 A 的 M1 客户端对接点，接口一旦冻结写进 `docs/decisions.md` 再改动需双方同步。
5. 不引用 `Fufucode.Pi`；不实现审批、设置页、会话持久化（后续里程碑）。

## 验收

- 独立可构建运行（无 Pi、无凭据），mock 会话演示：流式输出、工具卡片、停止按钮清空队列并恢复草稿。
- 长会话（≥500 条消息）滚动与输入不卡顿（自测记录，如实标注测试环境）。
- 证据与未验证项写入 `docs/archive/progress/progress-c.md`。

## 参考资料（必读）

- `docs/research/UNIGETUI_DESIGN_STUDY.md`——UniGetUI（Avalonia 桌面应用）源码研读。与本任务相关的是 §3（UI 层设计风格）、§6.1.6/6.2.6（Avalonia 惯用法与 Fluent 令牌）、§3.6（编译绑定纪律）。**状态标签：设计参考与灵感，不是需求规范**——界面需求的权威是开发计划 §9 与本任务书，冲突时以后者为准；文中"对 Fufucode 的启示"是建议，采纳与否记录到 `docs/archive/progress/progress-c.md`。
- 建议直接采纳的一条（来自研究 §6.2.7）：项目启用 `AvaloniaUseCompiledBindingsByDefault=true`，所有 DataTemplate 带 `x:DataType`。
- 回查源码时用研究文档 §7 的文件索引（`reference/UniGetUI/` 只读）。

## 协作规则

- 遵守 `AGENTS.md`；不碰 `src/Fufucode.Pi/`、`tests/`、`scripts/`、`runtime/`。
- Git：分支 `track-c` 开发，接口冻结点之后可并入 `main`。
- 与 A 的对接冲突（接口命名、事件形状）：以 A 的 DTO 为准做适配层，冲突记入 `docs/decisions.md`。
