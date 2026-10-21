# labs（历史打包实验）

日期：2026-10-21。

`labs/packaging/` 保存 2026-10 的打包、安装器、清理启动和 sandbox 实验脚本/日志。它们是实验证据，不是当前生产打包流程。

- 当前生产入口：`scripts/packaging/`。
- `run-drill.ps1` 仍把日志写到 `labs/packaging/drill/`；这是历史路径，新增自动化应改用 `out/evidence` 或 `tmp/`。
- 不要据本目录的旧日志判断当前包已验证。
