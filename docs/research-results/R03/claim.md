# R03 认领记录

- 认领者：WorkBuddy 研究 agent（本会话）
- harness：WorkBuddy（Claude），Windows x64，Git Bash
- 时间：2026-10-05（America/Los_Angeles）
- 任务编号：R03 对话与图结合的节点控制台
- 工作目录：C:\Projects\Fufucode（唯一常驻工作区）
- 分支：main
- 基线 commit：4d7c2a26cbdab744924dadb466990783ef2f7725
- 状态：待主 agent 审查
- 允许写入：仅 `docs/research-results/R03/`
- 约束：不执行 Git 写操作、不修改产品代码与 reference/、不安装插件、不更新 Pi、不访问凭据、不自建 worktree、不运行需要授权的实验。
- 开工检查：`docs/research-results/R03/` 开工前不存在，无重复认领（R01、R02 已存在但属不同任务）。

## 版本基线（实测，不是引用旧文档）

| 项目 | 本次实测值 | 证据 |
|---|---|---|
| main HEAD | `4d7c2a26cbdab744924dadb466990783ef2f7725` | `git rev-parse HEAD` |
| HEAD 声明的 Pi 版本 | `1.0.0` | `git show HEAD:runtime/pi/package.json` → `"@earendil-works/pi-coding-agent":"1.0.0"` |
| 工作区实际安装 | `1.0.0` | `runtime/pi/node_modules/.../package.json` version 字段 |
| lock 记录 | `1.0.0`，integrity `sha512-/FtbxoSQU/...` | `runtime/pi/package-lock.json` |
| `runtime/pi` 是否脏 | 否（`git status --short runtime/pi` 为空） | 与 HEAD 一致 |

**提请注意（交主 agent 判断）**：`docs/runtime-baseline.md` 顶部仍写 "main 仍锁定 0.87.1"，并把 1.0.0 描述为 `track-a-pi10` 分支上的候选。但在当前 main HEAD 上，`runtime/pi` 已实际为 1.0.0 且干净未改。本报告因此**以 Pi 1.0.0 为当前产品基线**取证；若主 agent 认为基线文档更权威，需要复核并明确取舍。

## 未提交原型与文件（SHA-256）

工作区开工时已有未提交内容，本任务未改动它们：

| 文件 | 状态 | SHA-256 |
|---|---|---|
| `docs/design/frontend-functional-plan.md` | 未跟踪（新增） | `6fbffe6ccf25bfffa7df0bc8c6048f45be5dd825ea4bcf02ce747cf58ead664f` |
| `docs/design/multiagent-collab-mvp-spec.md` | 已跟踪 | `a34428d6c097df4876adec942cc904a94e7a06e1bce7081efcb4126995e4c483` |
| `docs/frontend-prototype/index.html` | 未跟踪（新增） | `a0d7d8be201038f1b1d6f642b548b46660832ebe96fe73e6b9ad7a1c9c34d0c3` |
| `docs/research-tasks/R03-graph.md` | 未跟踪 | `b3498821e5a8feeca03b2a233cb64a7f81b627ffd116a42af0e97c1f87c3a75f` |
| `docs/research-tasks/README.md` | 未跟踪 | `d5a4dec45f56afba3d8cbfce0e8a244d082bb00eba6244d31f57c9d8d7af1c7c` |

本任务**只读取**以下产品源码，未修改（SHA-256 见 `report.md` §11）：
`src/Fufucode.Core/PersistentSessionManager.cs`、`SessionHistory.cs`、`DesktopMetadataStore.cs`、`ChatSessionCoordinator.cs`。

## 交付

- 报告：`docs/research-results/R03/report.md`
- 证据清单：`docs/research-results/R03/evidence.md`（一手来源、代码定位、SHA-256）

## 边界复核

- 未执行任何 `git add/commit/checkout/branch/worktree` 等写操作；`git status` 与本任务开工时一致。
- 未启动 Pi、未调用模型、未运行构建或测试、未运行实验（本任务为纯研究，实验方案已在报告中提出并留待授权）。
- 未写入 `docs/research-results/` 之外的任何路径。
- 改动全部留在工作区，由主 agent 核对、决定与收编；本报告不构成正式架构决定。
- 完成时间：2026-10-05（America/Los_Angeles）
