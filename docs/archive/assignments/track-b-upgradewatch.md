# 轨道 B 任务：Pi 升级雷达——pinned 版本与上游差异监控

> 小任务，收尾性质。背景：项目把 Pi 固定在 0.87.1（`runtime/pi` manifest + lockfile），计划 §3 要求升级走"有限验证"流程；本工具把"人工核对协议变化"变成"跑一次脚本"。

## 范围（只允许写这些）

- `scripts/upgrade-watch/`（从 A 的 scripts/ 所有权中切出，AGENTS.md 已更新）
- `docs/archive/progress/progress-b.md`、`docs/decisions.md`（追加 `[轨道B]` 条目）

## 任务

1. **对比脚本**：给定 pinned 版本（读 `runtime/pi` 的 manifest/lockfile）与一个目标版本（本地 npm 拉取或 GitHub 上游），对比：
   - `rpc-types.ts` / RPC 文档中的命令清单增删、命令字段变更
   - 事件类型清单增删（`json.md` 的 `JsonAgentSessionEvent` 面）
   - `extension_ui_request/response` 子协议变化
   - session 格式版本号（`session-header.version`）变化
2. **输出报告**：Markdown，分"破坏性 / 需评估 / 无影响"三档，逐条给出证据（文件与行）。供人工决策是否升级，**不自动升级、不自动改 lockfile**（计划 §3 红线）。
3. **对当前 pinned 0.87.1 自检跑通**：与自身对比应输出"无差异"，作为工具正确性的基线验证。

## 完成标准

- 脚本对 0.87.1 自检通过；任选一个可获取的其他版本跑出真实对比报告（哪怕结论是"全部无影响"）。
- 用法与解读说明写入 `docs/archive/progress/progress-b.md`。
