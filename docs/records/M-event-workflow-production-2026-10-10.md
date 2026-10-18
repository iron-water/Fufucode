# 事件无人值守验证与实际开发启动

最新更新：2026-10-11 03:55UTC（当地10日），首个生产交付真实自动投递后因Windows审核写入权限冻结，已独立免费模型复验修复并按原日志零模型对账恢复。当前门房33452、workflow22072，原D4 Codex线程/模型/worktree的第2轮修复已启动，worker42212、Codex25996。下文PID和仅首项开工为历史快照；最新证据、a878f38变更及未完成边界见[M审核写入恢复](M-review-write-recovery-2026-10-10.md)。

2026-10-10 20:27UTC现场结论：部分可行。批准任务图的完整机制已实测通过；生产监督及第一项真实开发已启动，产品首项交付尚未完成。模拟成功不是生产整项验收。

## 验证与边界

实现提交37eecae/eecae1d/3e1380e/7a494ab；子代理/失败隔离测试376bf42；完整源码证明及记录470c752。最终夹具 `C:/Users/mio/AppData/Local/Temp/fufucode-flow-887b9a1c406a47e09fa2bc13a0a35bfa` 的 `tmp/agent-flow/validation.json` 证明八份生产脚本指纹全程一致，OMP/Codex并行、六个原生子代理完成且实际收回、三项原会话修复、固定M六次串行审核、三次实际集成、共享接线84、依赖续派全部通过。详见[M跨harness记录](M-cross-harness-flow-2026-10-10.md)。

真正停止门房使C积压，再重启恢复同一M通过。真正Dispatch重放全部交付及空待审扫描新增模型请求0；manager及所有真实模型日志SHA不变。坏卡/HEAD变化/活跃writer/失败模型/不明claim重启冻结、worker原生退出事件、用户中文空格文件及index保护、单项失败隔离都有临时反例。测试lead、worker、原生子代理均显式devin/swe-2，只处理无意义41/42/84和临时Git数据，未使用昂贵测试lead。

生产证明为 `tmp/agent-flow/workflow-validated.json`、`tmp/agent-gate/dispatch-validated.json`；批准任务及契约SHA见plan.json/approval.json。R-037机制入口已补齐并完整实测，生产首轮实物验收仍open。模型延迟/断开R-046/047未解决；不明投递持久冻结并需严格本地对账，不保证任何故障均无需人工恢复。R-040旧宿主退出原因未证实，独立wscript私有控制台Ctrl+C隔离实测通过。

## 生产运行快照

- 门房PID41636，父wscript32712；任务 `Fufucode-EventGate-0BB7DB6C31FD` enabled/running，未暂停。
- workflow PID22072，父wscript33848；任务 `Fufucode-Workflow-0BB7DB6C31FD` 已注册运行，未暂停。
- 固定M thread `01a126ff-597f-7231-9ecb-c287e8d17458`，正式模型gpt-6.1-sol。未重新初始化昂贵lead；当前ready，暂无生产审核claim。当前Herdr交互线程没有已验安全投递入口，采用用户允许的另一固定线程。
- D4-SNAPSHOT实际worker32876、Codex31420、run `3848c5ea6f5f42f6965deb78e0b669fd`，显式devin/swe-2。工作树 `C:/Projects/Fucode/worktrees/m-unattended-d4-snapshot`，base `470c7523350e68d9cd30df22dd4f6fa394516771`。仅允许Core GitDiffSnapshot.cs、独立GitSnapshot.Tests工程/Program、本任务交付记录四文件，不改App/solution/用户数据，不开G1/D5。

PID为现场快照，复查以Status为准。主区根agent写入也持有main-writer lease，不能与自动M集成并行。继承demo结果、.playwright-cli/.workbuddy及用户文档保持未提交、不夹带。

实际启动由Task Scheduler调用隐藏wscript宿主，私有PowerShell执行以下命令；不要复制额外启动第二个监督器：

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File C:/Projects/Fucode/Fufucode/scripts/agent-gate.ps1 -Repository C:/Projects/Fucode/Fufucode -Dispatch -FixedManager -Model gpt-6.1-sol -CodexExecutable C:/Users/mio/AppData/Roaming/npm/node_modules/@openai/codex/node_modules/@openai/codex-win32-x64/vendor/x86_64-pc-windows-msvc/bin/codex.exe
pwsh -NoLogo -NoProfile -NonInteractive -File C:/Projects/Fucode/Fufucode/scripts/agent-workflow.ps1 -Mode Run -Repository C:/Projects/Fucode/Fufucode
```

## 状态与控制

```powershell
Set-Location C:/Projects/Fucode/Fufucode
pwsh -NoProfile -File scripts/agent-gate-control.ps1 -Mode Status -Repository C:/Projects/Fucode/Fufucode
pwsh -NoProfile -File scripts/agent-workflow.ps1 -Mode Status -Repository C:/Projects/Fucode/Fufucode
pwsh -NoProfile -File scripts/agent-manager-control.ps1 -Mode Status -Repository C:/Projects/Fucode/Fufucode

# 暂停后续动作，允许在途请求完成。
pwsh -NoProfile -File scripts/agent-workflow.ps1 -Mode Pause -Repository C:/Projects/Fucode/Fufucode
pwsh -NoProfile -File scripts/agent-gate-control.ps1 -Mode Pause -Repository C:/Projects/Fucode/Fufucode

# 恢复监听，不清除或重发不明投递。
pwsh -NoProfile -File scripts/agent-gate-control.ps1 -Mode Resume -Repository C:/Projects/Fucode/Fufucode
pwsh -NoProfile -File scripts/agent-workflow.ps1 -Mode Resume -Repository C:/Projects/Fucode/Fufucode
```

本地日志：`tmp/agent-gate/gate.jsonl`、manager目录bootstrap及各run stdout/stderr，`tmp/agent-flow/events.jsonl`；执行/保存会话/交付/审核在 `tmp/agent-queue/prod-readonly-20261010-D4-SNAPSHOT-1/`。结论经agent-queue.ps1 -Mode Review校验落盘，不声称向关闭Desktop即时推送。

## 关窗口、睡眠、重启与门禁

关闭CLI后独立Scheduler宿主及worker继续，实际一次性私有控制台Ctrl+C中断后监督器保留原PID，早期夹具还确认六个在途进程存活；未关闭用户当前窗口。睡眠暂停CPU，网络请求可能中断或状态不明，真实睡眠未验。重启终止进程，下次用户登录触发本地监督启动补扫，正常积压交付可恢复；不明投递冻结，旧未完成worker不自动重派。不自动登录，未登录时不保证运行，真实电脑重启未验。

已解锁限定批准任务图的自动执行/审核/回修/集成/依赖续派和D4非G1依赖子任务；整项D4/O4尚未验收。S2等待用户选生产皮肤，G1原生补验仍open，D5不越门。首项生产交付、审核、集成继续待证，不能称产品全自动闭环已完成。
