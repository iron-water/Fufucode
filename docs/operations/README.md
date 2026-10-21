# 运行维护

日期：2026-10-21。

当前默认后端是 FUMO，零外部运行时依赖；Pi/OMP/Codex/DSH/Claude 是可选后端插件。

- [pi-upgrade-sop.md](pi-upgrade-sop.md)：**历史 Pi 规程**，只适用于安装/维护 Pi 后端插件；不是默认部署步骤。
- [runtime-and-backends.md](runtime-and-backends.md)：FUMO 默认与本地 `runtime/` 载荷边界。
- [worktrees.md](worktrees.md)：11 个 worktree 的清单与回收边界。
- 本地运行时载荷（`runtime/`）与后端插件安装策略见仓库根 `AGENTS.md`。
- 构建、部署、更新通道见根 [README.md](../../README.md) 与 `docs/decisions.md` 2026-10-17 条目。
