# FUI

全屏终端中的 FUMO 会话。它是一个独立可执行文件：不启动 OMP、不包装 CLI，也不需要打开桌面应用。模型和凭据沿用 Fufucode 设置；没有端点时，发送会报错，不会换用其他后端。

```powershell
fui                       # 当前项目，新会话
fui --continue            # 当前项目，最近一次 FUMO 会话
fui --project C:\work\demo --session <ID>
```

`Enter` 发送，`Alt+Enter` 或 `Ctrl+J` 换行；`PgUp` / `PgDn` 翻看记录，`Esc` 停止当前回合，`Ctrl+D` 退出。工具确认使用 `F2` 允许、`F3` 或 `Esc` 拒绝。按 `F1` 看完整按键与本地命令。长审批参数可在对话区翻阅。

`--app-data <目录>` 可隔离设置和会话，也可设 `FUFUCODE_DATA_ROOT`。会话 ID 可在 `/session` 查看，或用同一项目、同一数据根的 `fufucode sessions list --json` 查询。管道、批处理、设置管理和 JSON 输出仍使用底层 CLI `fufucode`。

从仓库根开发运行：

```powershell
dotnet run --project src/Fufucode.Fui -- --project C:\work\demo
dotnet run --project tests/Fufucode.Fui.Tests
pwsh scripts/packaging/publish-fui.ps1          # 可选，自包含，不改 App/CLI 安装
```

默认 Windows 发布位置为 `out/fui/win-x64/fui.exe`。复制这个文件即可单独安装 FUI；无需复制调试符号。需要可交互的 VT 终端，建议 Windows Terminal。
