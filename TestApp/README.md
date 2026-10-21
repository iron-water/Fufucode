# TestApp（历史脚手架）

日期：2026-10-21。

`TestApp` 是最初的 Avalonia 欢迎页脚手架，不是产品入口，也不参与默认打包。产品应用是 `src/Fufucode.App`。

- 保留原因：历史追溯与 M0 基线参考。
- `bin/`、`obj/` 是生成物，已由 `.gitignore` 忽略；可随时清理。
- `scripts/verify-m0-m1.ps1` 仍引用本工程，属于历史基线脚本；新开发不要以它作为产品验证。
