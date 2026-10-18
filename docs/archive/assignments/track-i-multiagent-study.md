# 任务书 I1：多 agent 可视化协作页面研究

> worktree：`C:\Users\21894\.codex\worktrees\track-i-research\Fufucode`，分支 track-i。开工前 `git merge main`。
> 背景：用户提出在 Fufucode 右栏做多 agent 可视化协作页面，**没有清晰参照物可抄**，需要独立研究轨先产出设计依据。
> 所有权：只写 `docs/research/multiagent-collab-study.md`、`docs/archive/progress/progress-i.md`。

## 研究问题

Fufucode 将来会有多 agent 并行工作（多会话多任务），右栏需要一个协作可视化页面：用户能一眼看到"哪些 agent 在跑什么、各自状态、谁在等谁、产出在哪"。研究要回答：

1. **参照物盘点**：现有产品与开源项目的多 agent/多任务可视化都有什么形态——Codex Desktop 的子智能体卡（本机可实测，图集 `tmp/codex-atlas/00-full.png` 右上有样例）、Claude Code agent teams/swarm、Devin 会话视图、AutoGen Studio、CrewAI、LangGraph Studio、Claude Squad、tmux 多窗格模式等。逐个：形态描述+证据来源（实测/官方文档/仓库 README，标注置信度）。
2. **信息架构归纳**：这些形态共同呈现的维度——任务列表、状态机（运行/等待/完成/失败）、层级（主 agent/子 agent）、产出流、人工介入点（审批/提问）、时间线 vs 卡片 vs 泳道。
3. **Fufucode 适配方案**：结合右栏 tab 化骨架（docs/design/frontend-design-guideline.md §3）与既有 Core 能力（会话/审批/用量/ChangeRangeQuery），给出 1 个推荐形态+2 个备选，含 ASCII 布局草案；明确每方案需要什么 Core 契约（多会话编排、父子 agent 关系、聚合事件流——这些今天大多不存在，如实标注为"契约需求"不是"已有能力"）。
4. **MVP 建议**：能落地的最小第一版（大概率是"多会话状态仪表盘"而非真多 agent 编排），优先级与验收条件；与 A13⑤ fork、未来多 agent 编排的关系边界。
5. **不采用/后置清单**：哪些形态明确不适合 Fufucode 当前阶段，理由。

## 硬约束

- 参照物取证标注来源与置信度，不虚构不抄资产文案；本机 Codex Desktop 可后台 UIA/截图取证（规则读 C:\Projects\Fufucode\docs\archive\coordination\cua-dispatch.md，不抢前台）；外部产品以官方文档/仓库为准；
- 只写两个 docs 文件；不写产品代码；禁 git commit/merge/push；
- 研究是设计依据不是实现授权；交付后由协调者决定是否立项实现。
