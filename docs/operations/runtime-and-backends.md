# 本地运行时与后端

日期：2026-10-21。

- 默认后端：**FUMO 原生内核**，纯 C#，零 Node/Python/外部 CLI 依赖。
- Pi / OMP / Codex / DSH / Claude：可选后端插件，默认不随包发布。
- `runtime/` 是本地开发载荷（Node、Pi 等），被 `.gitignore` 忽略；仓库只跟踪少量 provenance 文件。它不是源码，也不是默认安装包内容。
- 主包不得包含 `runtime/node`、`runtime/pi`、`extensions/fufucode-bridge` 或 `Fufucode.Pi.dll`。
- 需要 Pi 后端时，按 [pi-upgrade-sop.md](pi-upgrade-sop.md) 的“历史 Pi 规程”使用；不要把它当默认部署步骤。
- 后端选择、插件安装与回退语义见 [AGENTS.md](../../AGENTS.md) 和 `docs/decisions.md` 2026-10-17 条目。
