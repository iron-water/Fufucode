# 轨道 A 任务书：Fufucode CLI 前端（F12）

> 来源：用户反馈 F12（2026-09-30）。用户明确不做 TUI，但要一个**能控制所有操作/事件/功能的命令行前端**。目标是：`fufucode` 命令行既能跑交互会话，也能被脚本/其他工具驱动。

## 定位（先想清楚再写码）

- **不是第二个 Core**：CLI 只消费既有 `Fufucode.Core`/`Fufucode.Pi` 的公共契约（会话生命周期、prompt/steer、审批桥、模型/端点/设置、诊断导出）。任何逻辑要在 CLI 里重写的，先回到 Core 提公共接口。
- **两态设计**：`fufucode <子命令>` 直接执行并退出（脚本态）；`fufucode` 裸跑进入 REPL 交互态。交互态不是 TUI——逐行读输入、逐行输出，不走全屏绘制。
- **审批是难点**：CLI 场景下的工具审批桥接（`fufucode-bridge` 扩展）需要在终端呈现 confirm/select/input 并回写响应——方案设计要含这一环，不要绕过扩展本身。

## 本阶段交付（第一切片，先设计后实现）

1. **命令面设计草案**：写入 `docs/design/cli-design.md`（归 A）。子命令全集草案（参考）：`chat <prompt>` / `chat --continue` / `chat --resume <id>` / `sessions list|show|delete` / `models list|set` / `settings get|set` / `extensions list|enable|disable` / `diag export` / `approve list|accept|reject`（审批挂起时）/ `--json` 全局开关 / `--project` / `--session`。每个子命令列出：RPC/核心调用对应、参数、退出码、stdout 形状（人读默认 + `--json` 机读）。
2. **工程落地**：`src/Fufucode.Cli/`（控制台工程，所有权归 A；并入 Fufucode.sln）实现最小可用子集：`sessions list`、`chat <prompt>`（驱动 ChatSessionCoordinator 真实会话）、`models list`。CLI 依赖注入沿用 Core 的既有服务注册方式。
3. **验证**：新增 `tests/` 内 CLI 命令测试（FakePi 驱动）+ `--selftest` 链路可选补 CLI 冒烟入口。派发纪律照旧：不构建/不 git/不装包，验证由驱动器跑。

## 边界

- CLI 是**新轨道交付物**，不抢 D 的 `src/Fufucode.App/`；App 与 CLI 并行消费 Core。
- 不实现内嵌编辑器、全屏布局、鼠标——那是 TUI，用户已明确不做。
- `pi --mode rpc` 仍是唯一子进程入口；CLI 不直连 Pi（那是 Core 的职责）。
