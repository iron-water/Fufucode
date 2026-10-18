# 固定总控M传输验证（2026-10-10）

用户允许在当前Herdr交互线程无法可靠自动投递时使用另一个固定线程。本次不接管未知窗格，不使用`--last`；新增独立持久thread绑定，门房每个事件显式`exec resume <threadId>`。线程身份、模型、仓库、inflight/needs_attention状态先持久化，manager.lock及main-writer.lock约束受管单写入。异常不自动新建线程、不自动重发。

当前验证范围仍仅事件审核。自动共享接线/集成/回修/新任务调度未实现，R-037保持open。固定线程不是常驻模型请求；空闲仅本地门房。当前交互界面不会因此收到消息。

## 本地证据

- manager纯本地守卫测试通过：`C:/Users/mio/AppData/Local/Temp/fufucode-manager-tests-5f84c1b11df449adaacf8ff26206a19e`。
- gate纯本地异常/重放/未知投递冻结通过：`C:/Users/mio/AppData/Local/Temp/fufucode-gate-2b83bd9684b847b79ba45b2e9f564852`。
- Scheduler启动窗口退出、svchost拥有门房、同所有者配置更新、暂停/恢复/重启通过：`C:/Users/mio/AppData/Local/Temp/fufucode-gate-scheduler-1d9ac6c03d41477f80e227a25e399a8f`。Windows调度器返回短用户名，改为解析SID核对，未放宽所有者限制。

## 真实模型验证

全部试跑显式`devin/swe-2`。首轮初始化越权主动审核，记R-038，旧夹具只作失败证据。初始化增加read-only及明确禁止扫描队列/调用Review；新夹具先完成初始化，再启动worker。

新夹具：`C:/Users/mio/AppData/Local/Temp/fufucode-gate-e2e-ffb94b64f5d34e389b6fb0700c1202dd`。固定试跑thread：`01a126f5-c73a-7cb0-be81-cd285b205f41`。初始化正常结束且未扫描交付队列；真实多事件恢复审核与原会话修复通过。

- GOOD：提交`02fd8a349bd9b5aeb601e56b0354f784eb5fb71e`，精确42，accept/pending_wiring。
- BAD初次提交`3186f724c67ec4c23fe9c3b82b50e6bab4924dff`，41；最终JSON夹带前言被严格拒收，原会话补交bad-card成功，固定thread独立rework。
- 原保存会话`01a126f7-7285-7290-b750-df069d76fb89`恢复修复，提交`aecbd8693c8a84346dc32ad5c027a6c948650ef7`，新交付bad-02，固定thread独立accept/pending_wiring。
- 三次真实事件审核全为上述固定thread，三个dispatch claim均decided；gate重新执行模拟重启，积压已审事件不重发，空队列/重放不新增模型调用，三份日志SHA保持一致。
- `tmp/validation.json`保存证据、线程、原sessionId、脚本指纹与日志SHA；复制至生产`tmp/agent-gate/dispatch-validated.json`作为固定线程配置前置。
- 机制代码提交`fa88d1996b5f143031d0aeb6cc7209f7093e30de`，后续小步补齐多轮原session路径读取及生产证据。

生产固定M已建立并配置：thread `01a126ff-597f-7231-9ecb-c287e8d17458`，模型`gpt-6.1-sol`，bootstrap正常退出且未扫描队列、生成审核或派工。生产初始化是正式身份交接，不是模型能力测试；全部夹具试跑均devin/swe-2。

生产计划任务`Fufucode-EventGate-0BB7DB6C31FD`，门房PID`38616`，父PID`3748`（svchost），2026-10-10T18:08:21Z启动。原任务XML已备份在tmp/agent-gate/task-backup-*.xml。生产切为FixedManager并恢复事件监听；当前无待审事件、无dispatch claim、无模型审核进程。仅生产bootstrap已验，首个真实生产交付恢复审核仍未验，不能把夹具通过称生产闭环完成。

启动参数：

```text
pwsh.exe -NoLogo -NoProfile -NonInteractive -File "C:\Projects\Fucode\Fufucode\scripts\agent-gate.ps1" -Repository "C:\Projects\Fucode\Fufucode" -Dispatch -Model "gpt-6.1-sol" -CodexExecutable "C:/Users/mio/AppData/Roaming/npm/node_modules/@openai/codex/node_modules/@openai/codex-win32-x64/vendor/x86_64-pc-windows-msvc/bin/codex.exe" -FixedManager
```

日志/绑定：tmp/agent-gate/manager.json、runtime.json、gate.jsonl、<runId>.stdout.jsonl、<runId>.stderr.log、<runId>.dispatch.json。初始化日志位于manager/bootstrap.*。没有发生审核时gate.jsonl可能尚不存在。

Desktop heartbeat持续PAUSED。关闭当前CLI窗口不停止scheduler门房；受管Worker脱离启动shell继续的机制已验。实际关闭Herdr服务、睡眠/整机重启未验；Herdr内当前交互线程不能保证继续自动处理工作，生产门房独立于Herdr。睡眠不能执行，重启须用户登录触发，旧Worker不会自动重启，未知在途状态冻结而非自动重发。无人值守结论为部分可行：固定事件审核可运行，完整M职责仍R-037 open。

## 本地操作

所有命令先进入真实仓库：

```powershell
Set-Location C:/Projects/Fucode/Fufucode
pwsh -NoProfile -File scripts/agent-gate-control.ps1 -Mode Status
pwsh -NoProfile -File scripts/agent-manager-control.ps1 -Mode Status
pwsh -NoProfile -File scripts/agent-gate-control.ps1 -Mode Pause
pwsh -NoProfile -File scripts/agent-gate-control.ps1 -Mode Resume
pwsh -NoProfile -File scripts/agent-gate-control.ps1 -Mode Stop
```

Pause只暂停后续投递，允许在途审核完成；Stop禁用计划任务并停止门房，在途状态须人工核实。Resume启用已验证配置并恢复监听，不消除未知claim；需先核对日志与真实进程。不要一边自动审核一边人工打开相同固定线程作写入者。

S1既有review.decided不重审。S2生产皮肤仍等待用户选择；G1原生补验仍open，不开D5。上面进程/仅审核为历史快照；20:27UTC冻结源码完整机制实测后已启动独立workflow承担原会话修复、集成及已批准任务图派工，同一固定M未更换。当前PID、真实D4子任务开工与未验边界见[M生产事件监督](M-event-workflow-production-2026-10-10.md)。
