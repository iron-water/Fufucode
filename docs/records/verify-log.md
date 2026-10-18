# 验证执行日志（轨道 B 常驻验证岗）

> 格式：每轮一行区块——时间戳、被验证的 main 提交、各命令 PASS/FAIL/SKIP、异常摘要。只记录事实，不做修复，不下结论。

## 执行环境约定

- 验证工作区：`C:\Users\21894\.codex\worktrees\track-b-bridge\Fufucode`（track-b，已与 main 同步）。
- 运行时接入：worktree 内 `runtime/` 仅含被跟踪的 `pi/package.json`+lockfile；以 junction 接入共享 pinned 运行时（`runtime/node`、`runtime/pi/node_modules`），gitignored，按任务书红线"库内既有方法"处理。
- `dotnet` 10.0.401；Node v24.15.0（runtime/node）；Pi 0.87.1。

## 轮次记录

### 2026-09-29T10:12 (America/Tijuana, UTC-7) main@eaf5134

| 命令 | 结果 |
|---|---|
| `dotnet build Fufucode.sln` | PASS — 0 错误 0 警告（18.7s） |
| `dotnet run --project src/Fufucode.App -- --selftest` | PASS — 0 项失败（mock 段，含 H2/H3 截图产物） |
| `dotnet run --project src/Fufucode.App -- --selftest --realpi` | PASS — 0 项失败（含 Atria 真实模型段：A01 审批链路、停止收敛、会话恢复） |
| `dotnet run --project tests/Fufucode.Core.Tests` | PASS — 150 PASS / 0 FAIL（exit 0） |
| `pwsh -File scripts/packaging/verify-clean-start.ps1` | PASS — clean PATH 下 selftest+Pi 启动/get_state/无残留进程（报告见 F 轨 `labs/packaging/clean-start-report.jsonl`） |
| `scripts/bridge-verify`（默认路径推导，无 env 覆盖） | PASS — 8/8 |
| `scripts/demo-verify` | PASS — 10/10 |

异常摘要：无。备注：Core 测试中 5 条含 "failure/失败" 字样的行均为 PASS 用例名，非失败。

### 2026-09-29T22:54 (America/Tijuana, UTC-7) main@56e46f8

执行环境：main 工作区 `C:\Users\21894\.codex\worktrees\track-a-core\Fufucode`（运行前 `git status` 干净，`git rev-parse HEAD` = `56e46f8` = main tip，含 D Wave1 + A/F 交付合并）。本工作区自带完整 `runtime/`（无 junction 需求）。

| 命令 | 结果 |
|---|---|
| `dotnet build Fufucode.sln` | PASS — 0 错误 0 警告（14.0s） |
| `dotnet run --project src/Fufucode.App -- --selftest` | PASS — 0 项失败 |
| `dotnet run --project src/Fufucode.App -- --selftest --realpi` | PASS — 0 项失败（含 Atria 真实模型段、P2-17 steer 段、扩展管理 realpi） |
| `dotnet run --project tests/Fufucode.Core.Tests` | PASS — 156 PASS / 0 FAIL（exit 0；5 条 "failure" 字样均为 PASS 用例名） |
| `pwsh -File scripts/packaging/verify-clean-start.ps1` | PASS — 解包 main 工作区 `out/Fufucode.zip`，clean PATH selftest + Pi 启动 + 无残留进程 |
| `scripts/bridge-verify` | PASS — 8/8 |
| `scripts/demo-verify` | PASS — 10/10 |

异常摘要：无。
