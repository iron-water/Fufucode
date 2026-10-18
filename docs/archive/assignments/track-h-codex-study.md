# 轨道 H 任务书：Codex 新功能研究（参考移植评估）

> 你是轨道 H，独立研究轨道。工作区：`C:\Users\21894\.codex\worktrees\track-h-study\Fufucode`（分支 track-h，已基于最新 main）。开工先 `git status` 确认干净。产出**研究报告**，不写产品代码。

## 文件范围（只允许写这些）

- `docs/research/codex-feature-study.md`（新建，本轨独占）
- `docs/archive/progress/progress-h.md`（新建，本轨独占）

## 交付物

`docs/research/codex-feature-study.md`，结构：

1. **功能清单**：Codex CLI + Codex Desktop + Codex Cloud（如可及）当前版本功能面——逐条：名称、入口（命令/快捷键/UI 位）、行为摘要、引入版本（官方 docs/changelog 或本机 `codex --help` 实测取证，标注出处）。至少覆盖：审批/权限模式体系、会话恢复与分叉（resume/fork/rollback）、steer/排队语义、计划模式、子任务/后台任务、diff 查看与文件还原、用量与限额展示、MCP/扩展机制、通知与声音、斜杠命令体系、IDE 集成、云端任务。
2. **对照判断**：每条三档——`采用`（fufucode 直接要学，附我们现状差距）/ `改造采用`（思想学形态改，说明理由）/ `不采用`（说明理由）。判定须引用我们现状的代码位置或任务书锚点（如 `src/Fufucode.Core/ChatSessionCoordinator`、`docs/archive/assignments/track-a-cli.md`），不许泛泛。
3. **优先级建议**：给协调者的排序（用户价值×实现成本），标注适合轨道 D（UI）/A（Core）/新轨道。
4. 未取证项标"未核实"，不虚构 changelog。

## 边界与诚信

- 不复制 codex 的资产/文案/商标到仓库；产物是我们的判断与差距分析。
- 只写上面两个文件；不动 `src/`、`tests/`、`extensions/`、`scripts/`、他人 docs。
- 研究完如实写 progress-h.md：已取证/未取证/结论。
- **不做 git commit/merge/push**——改动留工作区，协调者代验证与合并。
