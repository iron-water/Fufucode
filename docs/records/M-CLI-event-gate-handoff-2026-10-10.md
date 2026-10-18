# CLI M 接任与事件门房验证

任务：M-CLI-event-gate｜L4｜沿用当前 CLI 主模型 gpt-6.1-sol（本会话 turn_context 实测）。所有真实模型试跑显式 devin/swe-2。

## 现场与保护

- 起点 main `410009f9818ee51af9c734852e6da6a6e1eb1de2`。已读当前执行计划、事件/无人值守/执行/交接/难度协议、原门房记录及进度/决策。
- automation `fufucode` 的本地配置为 PAUSED，未修改。没有活跃受管 OMP/Worker/gate；未知 Codex、MCP 和用户会话未接管。
- S1 `s1-02/events/05.json` 已接受并集成；不重复审查。保存会话和全部既有 worktree 保留。
- 继承 `scripts/demo-verify/demo-verify-results.json` 改动、`.playwright-cli/`、`.workbuddy/` 和用户文档/日志均未纳入提交。未 push、重置、删除 worktree、登录、采购或部署。

## 已实现与本地验证

- gate 校验 review.decided 的身份/HEAD/decision，不将任意 05.json 当有效决定。停止进程及受管 mutex/HEAD 检查后才读 receipt；receipt、state、事件与持久 request 一致才投递。
- Review 入口再次校验 delivery.received、receipt、state 身份/HEAD。最终决定仍只能经 agent-queue Review 生成。
- gate 持有主区协作单写入 lease，逐项串行审核。投递前持久 claim；重启遇到 dispatching 冻结全部自动投递，防止旧 reviewer 仍运行时开启新写入者。needs_attention 不自动重发。
- stdout/stderr 持续复制到文件，保存审核退出/失败日志和审核 thread ID。显式 --no-daemon 不依赖交互 CLI 的共享服务；超时结束本审核进程树并留失败状态。
- agent-queue MechanismTest 只接受 devin/swe-2，允许临时仓库真实工具/提交测试；不冒用 no-tools TransportTest。Worker 明确 cwd。
- agent-gate-control 用 Windows Task Scheduler 的当前用户 InteractiveToken/最低权限托管门房；不保存密码、不自动登录。登录触发及本地故障重启不是模型 heartbeat。Pause 阻止新投递，让在途审核完成；Stop 禁用任务并停止门房，投递不明需检查；Resume 保留原配置。
- Task Scheduler 不是任意用户写入者的系统级隔离。main-writer.lock 只约束遵守该 lease 的集成/审核入口；未知用户会话必须先核实，不声称全机强制单写入。

命令（均在真实主仓库显式 workdir）：

```powershell
pwsh -NoProfile -File scripts/agent-events.tests.ps1
pwsh -NoProfile -File scripts/agent-gate.tests.ps1
pwsh -NoProfile -File scripts/agent-gate.scheduler-tests.ps1
git diff --check
```

结果：事件测试 30 断言通过；gate 资格/活跃 PID/已审/probe/重放/零投递通过；坏 review/receipt、变化 HEAD、受管 writer mutex、失败 reviewer 重放、崩溃后不明 claim 冻结通过。故障 transport 使用 pwsh.exe 有意失败，不调用模型。scheduler 测试的启动 shell 已退出，门房父进程为 svchost.exe；暂停/恢复、停止/重启、空闲零 dispatch 通过。

保留证据：

- `C:/Users/mio/AppData/Local/Temp/fufucode-gate-4d54854f4be2422bbc6c63a250c4b6ee`
- `C:/Users/mio/AppData/Local/Temp/fufucode-event-tests-ebe0659eebe54374a9e1074bba7b0cad`
- `C:/Users/mio/AppData/Local/Temp/fufucode-gate-scheduler-1be2d4e39ae3412698c2b1c005e02bf4` 的 running.json/resumed.json（测试任务最后已禁用/停止，未删除）。

## 真实模型闭环（已通过，不作生产集成闭环验收）

临时独立 Git 仓库：`C:/Users/mio/AppData/Local/Temp/fufucode-gate-e2e-a8dcce576be14347a66cb21eb90224a1`。由 agent-gate.e2e.ps1 新建，与用户真实数据无关。BAD/GOOD 各自独立 worktree；仅 answer.txt 可修改。

- BAD 首轮 OMP/devin/swe-2 提交 `046accafd25eae1781de8030fde2c7a1af0ef431`，值41；exit0/terminal JSON→delivery.received。
- gate 实际调用独立 Codex/devin/swe-2 读 acceptance.md、文件和 diff，通过 Review 落 rework；原模型/工作树/保存会话 Resume 修复提交 `7a25f14d593ff74bf14138b9d4cba3dafb477609`，值42，重新交付。修复后第三个独立审核接受，pending_wiring/mainSha=null；完整事件/决定在 bad-02。
- GOOD 首轮提交 `91b40faab4e95b7ea74b304535db0cfa8995f490`，值42；实际独立审核 accept/pending_wiring，mainSha=null，证明 module_ready 未被无条件打回。
- Codex --no-daemon/devin/swe-2 无工具传输探针正常 PROBE_OK；没有使用昂贵模型测试 lead。
- 原始输出、session、manifest/state、events/04/05.json、review.json、审核 verify.log 与 dispatch/stdout/stderr 均保留在该临时仓库 tmp 下。
- BAD 两轮的真实 OMP sessionId 均为 `01a126d7-e525-7106-a46d-cd935b489b04`，修复仍在原worktree/原模型；未夹带文件。第三个 Codex 审核保存 thread `01a126dd-7412-7bd3-bca4-1a694e80049b`。
- 三个真实 reviewer 输出文件各保存 SHA256，重启 gate 再 Dispatch 时无待审且所有输出 hash/投递数量保持不变；没有第二次模型调用。Assert 已验证所有源脚本和夹具脚本 hash 相同，生成 `tmp/validation.json`，主区副本 `tmp/agent-gate/dispatch-validated.json` 仅授权 event-review-only。
- 真实两份交付积压后审核串行；第二轮 gate 重启恢复 bad-02 新积压。丢文件事件由本地补扫覆盖；未知投递重启不重发用本地故障夹具验证，未杀真实模型进程制造故障。

## 边界与仍 open

当前 Dispatch 仍是**事件审核器**，不是完整自动集成/派单 M。共享接线、集成、下一任务的依赖/所有权判定，以及自动回修调度尚无受测生产执行入口；不能通过改一段 prompt 宣称补齐。此次 M 主会话确实自主启动原会话回修，但不能冒称门房已自动承担该责任。

2026-10-10 17:34:38 UTC 已启动生产事件审核门房：任务 `Fufucode-EventGate-0BB7DB6C31FD`，现场 PID `46052`，父 PID `3748` 为 Task Scheduler 的 svchost.exe；与本交互 CLI 独立。生产审核显式沿用当前 CLI 的 gpt-6.1-sol，不作为测试 lead；没有生产待审事件，尚未实际调用该模型。为完成本轮主区记录先设置 paused，记录提交后解除；不恢复15分钟heartbeat。临时测试调度器任务已禁用/停止，未删除。

生产首个事件在 Scheduler 环境下的模型调用、复杂生产审核能力、长时间运行、电脑真实睡眠/重启、恶意并发用户写入均未验。scheduler 的本地空闲/控制/重启已验，不能扩大为这些保证。S2 等用户生产皮肤选择；G1 原生补验仍 open，不开 D5/G2 下游。

关闭交互 CLI：scheduler-owned 门房不依赖该 shell（短生命周期启动器退出已测），受管 Worker 在启动 shell 退出后继续并完成已测；未实际关闭当前整个 Codex 进程/窗口，不能声称覆盖外部强杀全进程树。睡眠时本地进程和模型推进暂停，恢复后本地补扫；网络中的模型请求可能失败/未知，不自动重发。重启结束所有进程，当前用户下一次登录可启动 scheduler gate；旧 running worker 不自动恢复，旧 dispatching 冻结，需核对保存会话/HEAD/PID。实际睡眠/重启未执行。

## 本增量交付卡

任务与软件：M-CLI-event-gate / Codex CLI + PowerShell 7。工作区 main，起点410009f，实现增量提交 `18069836973b9784333c095eb315061305f3e350`。改动仅门房/队列/事件脚本、定向工装及记录；继承内容保护。本地验证、真实打回/原会话修复/接受、接受夹具和实际重放全部通过。状态：部分交付；生产事件审核器已启动，自动接线/集成/派工/回修调度未完成。不报告全自动完成。原执行夹具均已停止，生产无活跃受管实现worker。

## 下一步操作卡与控制

- 无需新用户会话。生产门房只在待审交付事件出现时调用独立 CLI 审核 M；空闲纯本地。审核日志/失败和决定保存在本地，不声称可推送到已关闭Desktop会话。
- S1 已验收样稿；S2 等生产皮肤选择。G1/各模块原生补验仍open；当前原生工具不可用不能冒称补验。D5不启动，G2/G3及下游不解锁。R-037继续由CLI M补齐，不能默认交给Desktop。
- 后续共享接线/集成必须取得 `tmp/agent-gate/main-writer.lock` 同一独占lease（FileShare.None），或先Pause并确认无在途reviewer；未知用户会话不纳入接管。自动集成入口尚无完整实现，用户不需自行合并。
- 查看：下列 Status 输出 task/gate PID/父进程、实时存活、pause和claim；检查 gate.jsonl、各 run stdout/stderr 和队列 review.json/events/05.json。dispatching/needs_attention 先核对真实 reviewer/saved thread/HEAD/决定，不删除claim来盲重发。

```powershell
Set-Location 'C:/Projects/Fucode/Fufucode'
pwsh -NoProfile -File scripts/agent-gate-control.ps1 -Mode Status
pwsh -NoProfile -File scripts/agent-gate-control.ps1 -Mode Pause
pwsh -NoProfile -File scripts/agent-gate-control.ps1 -Mode Resume
# Stop 禁用登录/重启触发并停止门房；在途投递须先核查。
pwsh -NoProfile -File scripts/agent-gate-control.ps1 -Mode Stop
Get-Content tmp/agent-gate/gate.jsonl -Tail 30
pwsh -NoProfile -File scripts/agent-queue.ps1 -Mode Status -JobDirectory tmp/agent-queue
```

实际生产启动入口（只登记一次；已登记后用 Resume）：

```powershell
pwsh -NoProfile -File scripts/agent-gate-control.ps1 -Mode Start -Dispatch -Model gpt-6.1-sol -CodexExecutable 'C:/Users/mio/AppData/Roaming/npm/node_modules/@openai/codex/node_modules/@openai/codex-win32-x64/vendor/x86_64-pc-windows-msvc/bin/codex.exe'
```
