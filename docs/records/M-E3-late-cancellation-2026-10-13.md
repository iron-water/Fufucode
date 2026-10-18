# E3 恢复成功后、选定提交前的迟到取消

任务 E3-M-HOST 迟到取消增量｜L4｜M 自做宿主及原真实测试。起点 main `5ca6c6b25ac82dcc1d4c26c6d7ae1569258ad61b`；实际实现 `4a9e0343e87ec88fc82d9ed25a8e0f959b35394d`（2源/自测+记录/缺陷登记，共4允许文件）。原 R1/E3 没有新执行或交付；R1 本轮 UI 状态变为 notLoaded、cursor :12，但 latestTurn 仍原 completed，只返回旧只读确认卡，不当新交付或故障。

## 问题与实现

上一增量覆盖初始化仍在进行时取消。本轮把边界向后推进到真实 Pi 的 Resume 已成功返回、持久启用列表尚未提交：此时旧代码不再检查取消，继续提交候选；窗口关闭也会先持久化候选再处置进程。

新增内部确定性测试接缝 `ExtensionsResumedForTesting`，只暂停实际 Resume 之后、选定提交之前的连续流程；产品未赋值，不替换 Pi、数据库或恢复结果。在真实候选连接已 Connected 且原 sessionID 保持之后明确取消或关闭。未修行为出现四 FAIL：晚取消仍发布候选、仍报告 live、没有明确恢复；晚关闭仍发布候选。

宿主在该提交边界重新检查请求令牌及 disposed。取消/关闭已经到达时，把未提交候选标为 released 并实际释放；外层既有异常链恢复 Manager 原活动列表。持久启用列表与 ActiveExtensionPaths 不前移。外壳复用原明确恢复路径，未关闭时可显式恢复原 session/草稿；已关闭不重启窗口或进程。本增量不声称取消可以撤销已经完成的持久提交。

## 实跑证据

| 实际命令 | 结果 | 日志 |
| --- | --- | --- |
| `dotnet build Fufucode.sln -c Release -v quiet` | 0警告、0错误、exit0 | `tmp/m-cycle49-solution-build.log` |
| App DLL `--selftest --local-plugins-cancel-real-only` | 原18+新增9=27 PASS、0失败、exit0 | `tmp/m-cycle49-late-green.log` |
| App DLL `--selftest --local-plugins-real-only` | 原12真实安全应用/恢复/回退路径全部通过、exit0 | `tmp/m-cycle49-shell-real.log` |
| App DLL `--selftest` | 1255 PASS、0失败、exit0 | `tmp/m-cycle49-app-full.log` |
| Core / Pi 测试 DLL | 359 PASS+1可选云SKIP / 21 PASS、均exit0 | `tmp/m-cycle49-core.log`、`tmp/m-cycle49-pi.log` |

DLL 使用各工程 `bin/Release/net10.0/` 中真实产物。默认 App 全量与真实路径串行；无同 DLL 运行中重建。只临时 project/data、自有插件与显式 `proof/devin/swe-2` 离线 provider，没有云 prompt 或昂贵测试 lead；Core 可选真实云开关显式为 0。

`tmp/m-cycle49-late-red.log` 保留同接缝、原实际四 FAIL。最终相同强断言全部通过，新明确恢复检查也通过，不把“已经请求关闭”代替“已关闭”、不将逻辑字段当真实 Pi 已就绪。只读 CIM 核对本轮专有测试根 `fufucode-plugin-cancel-f0fcdf70a74b464ba9c10a46e997ad65`：Node 残留 0，证据 `tmp/m-cycle49-late-processes.json`。

## 未验证及下一步

- 验收限 Resume 成功后、选定提交前已到达的取消/关闭；已经提交后到达的取消、unknown 回执的人类核对/持久在途释放、长时间压力、生产迁移仍待做。无头外壳+真实 Pi 不是系统原生键盘/IME/DPI证据，当前工具仍没有 node_repl。
- R1 旧候选仍固定 cc44eee，未包含插件增量。未知 WindowsSandboxServer PID15712/出生未变，不接管/停止；旧安装门继续 defer，不重复投递或重建。
- 原 demo 与未跟踪目录保留；本轮独占可续期租约 PID35864，编辑/提交前活检续期。无 push、历史改写、worktree 删除、登录/采购/部署。后续 M 推进 unknown 结果只读核对与当前源候选，不能把本增量等同插件整项或生产六门已验收。
