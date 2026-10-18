# M 主 agent 开工准备

状态：准备完成，执行任务未启动。日期：2026-10-10。

## 已核实

- 主仓库：`C:\Projects\Fucode\Fufucode`，main 起点 `352caa1`。
- 已通读 AGENTS、权威计划开工入口、execution-contract、session-handoff-protocol、launch-prompts 首批任务及 production-delivery。当前执行协议优先于旧常驻路径/旧队列。
- 首批为 D1（Codex 双后端）、O1（OMP 快捷键）、O3（OMP 资料库）；G1/G2 尚未验收，下游保持未解锁。
- 现有 OMP 工作树 `C:\Projects\Fucode\Fufucode-omp`，分支 codex/omp-work，HEAD `af9e1f6`；无未提交内容、无独有提交，落后 main 四个文档提交。实际启动前仍需确认没有活跃写入者，再快进同步。
- 主目录已有用户修改：demo-verify-results.json，以及未跟踪的 .playwright-cli、.workbuddy、人类意见文件和人类说明文件；均保持原状，不夹带提交。
- 应用列表已有另一活跃对话「主agent（管理）」；本轮不向其发送消息，不假定其已交接写入权。当前对话先负责只读准备与本记录，不启动第二个 main 源码写入者。

## 执行边界

本轮用户要求准备开工，未明确要求创建新对话或向现有对话发送消息，因此未创建或派发对话。未启动 OMP、模型试跑、产品构建或安装。准备阶段不引用旧验收结果作为当前通过证据。

首批执行任务使用独立工作树，一任务一会话；共享接线由 M 统一收编。模型试跑显式指定最低成本可用模型，首选 devin/swe-2。执行者读取当前 launch-prompts 对应完整段落及引用任务书，收尾主动提供交付卡。

## 下一步操作卡

- M：维护进度、依赖与收编；当前仍未持有另一活跃主对话的源码写入权交接证据。
- D1：Codex / Worktree，以最新 main 为起点，只执行 D1 和修复。
- O1、O3：用户从两个独立终端启动的交互 OMP 会话，各自使用独立工作树；已有 OMP 工作树仅可分配给其中一个任务。本方案不将它们改为 Codex 调用的非交互进程。
- 原执行会话：本轮未发现已启动的 D1/O1/O3，不能据此排除应用列表外的终端进程。
- 未解锁：D5/D2/D3/D4/O5 等等待 D1 整合及 G1；D6/O6 等等待 G2。
- 开工 prompt：使用 docs/tasks/session-handoff-protocol.md 第 1 节完整开工指令，将任务ID替换为 D1、O1 或 O3；模型再读取 launch-prompts 对应段落。

## 验证

实际运行 git status --short、git worktree list、工作树双向 git log，并读取应用对话列表。仅完成准备核查，未运行产品验证。

## 分工纠正

用户指出上一轮混淆会话所属 harness 与会话内部工具调用。重新读取会话图、交接协议与首批任务 prompt 后确认：D1 在 Codex 应用的新 Worktree 对话；O1/O3 分别在用户终端创建的独立交互 OMP 会话。Codex 内部调用 OMP/Pi/其他 harness 不改变该任务的 Codex 会话身份，也不能替代规划中的两个独立 OMP 会话。未启动任何执行任务。
