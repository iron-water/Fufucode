# Pi 1.1.0 升级交付

日期：2026-10-08（用户时区）。任务：升级当前受管 Pi 至最新版，确认可以直接复用的官方能力。

## 变更

- npm latest 和官方 GitHub latest release 均为 1.1.0（release 发布时间 2026-10-07T22:26:31Z）。从 1.0.0 升级 `runtime/pi/package.json`、`package-lock.json` 和实际 node_modules；Node 保持 24.15.0，CLI 仍为 `dist/bundle/cli.js`。
- 完整旧运行环境：`C:\Projects\Fufucode\tmp\pi-upgrade-2026-10-08\pi-1.0.0-backup`。旧 ZIP 保留为同目录 `Fufucode-before-pi11.zip`。未改用户生产会话、认证或运行中的 Pi 进程。
- 探针保留旧断言，增加已审阅的 1.1.0 严格 builtin oracle；跨版本夹具仅改测试副本头部 cwd；增加 `-Features` 实际 codemode/MCP/内层审批验证。
- FakePi 标注 1.1.0 的既有已验证子集，不声称模拟完整新功能；打包 README 改读实际包版本；旧 Core 测试输出中的 0.87.1 标签改为 managed Pi。
- 更新运行基线、追新 SOP、决策/进度/验证和文档导航。原生接入矩阵：`docs/research/pi-native-capabilities-2026-10-08.md`。

## 验收

| 实际入口 | 结果 | 证据 |
|---|---|---|
| 旁装 1.1.0 RPC/双向 fixture 探针 + SelfTest | 10 PASS、0 FAIL/SKIP；自测 14 PASS | `tmp/pi-upgrade-2026-10-08/candidate-rebased/` |
| 实际 runtime/pi 1.1.0，Compare 旧备份，SelfTest + Features | 11 PASS、0 FAIL/SKIP；自测 14 PASS | `tmp/pi-upgrade-2026-10-08/locked-final/` |
| `dotnet build Fufucode.sln --no-restore -v quiet` | 10 projects、0 errors/warnings | 实际执行输出 |
| Pi Tests 当前 Debug DLL | 21 PASS、退出 0 | `tmp/pi-upgrade-2026-10-08/pi-final.log` |
| Core Tests 当前 Debug DLL | 333 PASS、退出 0；Atria 专用真实模型验收 SKIP | `tmp/pi-upgrade-2026-10-08/core-final.log` |
| App 当前 Debug DLL `--selftest` | 退出 0、0 项失败 | 本轮执行输出；自测 evidence 在临时目录 |
| `verify-clean-start.ps1 -ExtractedPackage out/build` | PASS；包内 App 全量 selftest 0 失败；两次真实 get_state 成功；Pi 735ms ready、84ms 正常退出，无包内进程残留 | `labs/packaging/clean-start-report.jsonl` 最后 6 条（2026-10-08T23:31 -07:00） |

该包内检查实际使用独立临时 cwd/数据目录与精简 PATH，不依赖全局 Node/Pi；仍是本机检查，不等同干净 Windows 安装。ZIP 归档已验证 15,542 个文件的实际 SHA-256；新版包内 CLI `--version` 实际输出 1.1.0。

包内自测结束后再次执行 `integrity.ps1 -Mode Verify -PackageRoot out/build`：`ok:true`，检查 15,542 个文件，missing/changed/extra 均为空。证据：`tmp/pi-upgrade-2026-10-08/package-integrity.json`。

## 交付产物

- `C:\Projects\Fufucode\out\Fufucode-1.4.1-setup.exe`：安装器编译成功，包含 Pi 1.1.0。本轮没有安装到用户正式应用目录。
- `C:\Projects\Fufucode\out\Fufucode.zip`：190,577,624 bytes，SHA-256 `14E4DCB29000B36F994F07C98E8C69AEDA033AFAEE142DE6F22A669DDC5E7C36`。
- 安装器 SHA-256：`F79BE2323813E9235F5356F4869B918B5B2D5B0D09050EC786BD3986B7051A80`。
- 已验证的包内应用：`C:\Projects\Fufucode\out\build\app\Fufucode.App.exe`；包内根启动器 `out\build\Fufucode.cmd`。安装器编译日志：`tmp/pi-upgrade-2026-10-08/package.log`，整个 packaging 入口退出 0。

本次提交只包含升级代码/记录和仓库已跟踪的打包产物；开工前的 demo-verify 结果、用户意见文件及未跟踪工具目录不收编。安装器和 ZIP 的哈希用于辨认本次输出，不代表已通过干净机器安装或生产数据回退验收。

双向 fixture 实际 producer/consumer 为 1.0.0→1.1.0 和 1.1.0→1.0.0，每个 consumer 加入新内容后 entries 由 9 增至 12，原 entry 的内容/ID/parentId 完整保持。模型是本机合成 SSE。Features 使用实际 Pi QuickJS + 本机 stdio MCP echo + 项目真实 bridge，写入允许/拒绝和 server 实际调用均验证。

失败记录保留：最初 baseline/candidate 的第 10 类因脱敏 placeholder cwd 不存在而启动失败；修工装副本 cwd 后通过，没有改原 fixture。`locked/` 在 npm 安装尚未完成时误提前执行，两个输入同为 1.0.0，被断言拒绝；它不计入升级成功。实际产品升级结论只依赖安装完成后的 `locked-final/`。

## 接入结论

Codemode、MCP、tool-search、模型/工具循环、会话/分支/压缩可以复用 Pi；桌面实现入口、配置、展示和本项目审批策略。产品启动仍带 `--no-extensions`，本次升级不会自动启用新 builtin。隔离探针已确认显式 builtin 加载可用，下一步可以据此实施功能开关。

Pi Durable 是独立实验性 Harness/Storage，不是当前 RPC 开关。普通 Pi subagent 是官方扩展示例；Durable subagents 属于另一套 harness。两者都需要独立适配验证，不虚构稳定 subagent/Durable RPC。

## 回退与未验证

未运行生产数据跨版本升级/回退、干净机器安装、远程 MCP/OAuth、云供应商模型/分类/图像、Durable 或子代理验收。既有用户进程仍运行它启动时的 Pi；重启会话才使用新内核。

代码/运行环境可从保留的精确旧 package/lock 和完整旧目录恢复；不能据 fixture PASS 对新版写过的全部用户数据直接降级。必须先保留新数据，在生产数据副本上验证，再决定恢复旧环境或升级前数据。
