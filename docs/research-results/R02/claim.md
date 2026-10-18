# R02 认领记录

- 任务编号：R02（Windows 多窗口与多进程）
- 任务书：`docs/research-tasks/R02-windows.md`
- 认领者：外部研究 agent（ZCode harness，GLM 会话）
- 认领时间：2026-10-05
- 基线 commit：`4d7c2a26cbdab744924dadb466990783ef2f7725`（main HEAD）
- 状态：待主 agent 审查（2026-10-05，报告完成于本目录 `report.md`）

## 使用的本地未提交文件（不假定 commit 含有原型）

| 文件 | SHA-256 |
|---|---|
| `docs/design/frontend-functional-plan.md` | `6fbffe6ccf25bfffa7df0bc8c6048f45be5dd825ea4bcf02ce747cf58ead664f` |
| `docs/design/frontend-interaction-inventory.md` | `a82ea82f6185a34dea215f1e6180f3fc6375ce9206b4db6bc6813c2da24307a8` |
| `docs/frontend-prototype/index.html` | `a0d7d8be201038f1b1d6f642b548b46660832ebe96fe73e6b9ad7a1c9c34d0c3` |

## 范围声明

- 只读源码与 `reference/`；不修改产品代码，不安装插件，不更新 Pi，不访问凭据。
- 写入仅限 `docs/research-results/R02/`；不执行任何 git 写操作；无实验（不启动 Pi/子进程/桌面程序）。
- 已读共同协议要求的：AGENTS.md、AI_DEVELOPMENT_PLAN.md、runtime-baseline.md、frontend-functional-plan.md、R02 任务书。
