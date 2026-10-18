# 跨harness全流程模拟与恢复（2026-10-10）

## 最新结论：冻结生产源码全流程通过（20:25UTC）

下文较早进展保留为历史，不代表当前仍在等待。最终证据根为 `C:/Users/mio/AppData/Local/Temp/fufucode-flow-887b9a1c406a47e09fa2bc13a0a35bfa`；`tmp/agent-flow/validation.json` 的 status=passed、sourceConsistencyVerified=true，八份生产脚本开工指纹与实际测试及当前文件完全一致。实现提交37eecae/eecae1d/3e1380e/7a494ab，强化真实子代理收回与失败隔离测试提交376bf42。

- A=OMP、B=Codex两个执行lead实际并行；A1故意41被独立M打回，A2原OMP会话修复42后接受。B1/C1缺少真实子代理收回被打回，B2/C2均在原Codex UUID、原worktree、原devin/swe-2补齐并接受。共六次正式审核，accept夹具并非因为module_ready而失败。
- 六个有效原生子代理：OMP两个实际yield成功、正常session_exit且父会话收到async-result；Codex四个保存线程实际模型devin/swe-2、终态CHILD标记与父流wait收回一致。另有B1/C1未收回的失败尝试，不能计入六个成功。
- 唯一固定M `01a1275f-c5ac-7810-a109-ca2f9c404096` 串行审核并承担三次实际来源合并、共享wire.ps1接线与验证；wire实际输出84。C的启动base严格等于B完成集成SHA，最终main `032218bf88f4da9a1cb9dc89fc18c2618c5de29a`。中文空格未跟踪文件与已有.gitignore改动字节保护不变，整体index保护及任务范围干净由实际integration guard检查。
- `tmp/flow/backlog-restart.json`：gate32204先真实停止，C1正常结束交付积压且没有dispatch claim，重启gate30632启动补扫后仍投到原固定M；没有合成交付事件。
- `tmp/agent-flow/validation.json` 的replayEvidence：正式完成后停止gate，重写全部delivery事件并真实-Once Dispatch，固定M绑定及所有真实模型日志SHA不变，新增模型请求0；空待审同一次真实扫描也为0，随后恢复本地监听。
- `tmp/flow/console-isolation.json`：一次性私有控制台实际Ctrl+C提前中断，gate/workflow保留原PID。早期eac夹具同一机制还确认两worker/模型在途六进程存活。没有关闭用户当前CLI、没有实际睡眠或电脑重启，因此这些后两种场景仍未实测。

全部测试lead、worker、原生子代理显式devin/swe-2；模拟只写无意义41/42/84与临时Git数据。没有启动昂贵测试lead。测试断言最初只认OMP yield的marker键，现场实际结构为result；已依实际结构修正并加父会话真实async-result收回核对，不靠执行者交付卡自述通过。生产Dispatch仍只审核；完整职责由独立agent-workflow.ps1按已批准plan承担，与gate共享单main写入lease。不宣称任意网络/关机中断均可无人工恢复；未知投递保留冻结。

用户明确要求AI自行跑通，不把测试交给用户。所有模型试跑/子代理均显式devin/swe-2；没有意义的模块标记41/42/84，不做真实业务开发。当前目标包括正式启用自动派工/审核/原会话修复/共享集成/依赖续派，再启动允许的实际任务；本记录随真实验证更新，不以局部通过代替整项通过。

## 试跑与故障

- 首个fixture `C:/Users/mio/AppData/Local/Temp/fufucode-flow-540632e1934b496f857464958d980ebd`：direct pwsh Scheduler控制器与旧生产gate均发现0xC000013A退出，快照未收敛；保留未知bootstrap，不自动重发（R-040）。
- 恢复夹具 `C:/Users/mio/AppData/Local/Temp/fufucode-flow-5bc4007ca47a4baa94149e203fef6629`：wscript隐藏宿主；OMP task原生两子代理与Codex spawn_agent原生两子代理实跑。初轮坏schema严格拒收；原保存会话补卡。Codex工作树Git沙箱阻止提交，执行器改与当前授权一致的danger-full-access，审核workspace-write/初始化read-only不变（R-041）。A rework→原会话修复accept；固定M集成AB并写wire.ps1，实测84，main c1812b559ae88930fa6d61ad21172bef7ed89e3c。C原生子代理已实跑，但两轮JSON语法仍错误，受测有界流程停在needs_attention，没有放宽解析。
- 通用框架夹具 `C:/Users/mio/AppData/Local/Temp/fufucode-flow-d042113a2613456d8fca6157d6d0f7fc`：直接使用agent-executor.ps1和agent-workflow.ps1，批准plan/契约指纹；A=OMP、B/C=Codex，每执行线程至少两个原生子代理；A故意41须rework，B待A集成再由M共享接线，C依赖A/B真实整合。固定M 01a1271e-a3a6-7eb3-b8d2-e97999f8a2bd。目前A/B真实交付、A rework及原会话修复已跑；最终全程未完成，待继续。

## 本地验证

- 原子写3000次，并发读243588次，无缺文件/半截JSON；同时验证测试模型成本、路径白名单、循环/未知依赖、重复身份与仓库身份拒绝。证据 `C:/Users/mio/AppData/Local/Temp/fufucode-local-flow-10a4cbf4a3474f1488c697486c9aaf95/validation.json`。旧Move-Item Force有短暂缺文件，改原子rename+Windows共享读取/有界本地rename重试，R-042。
- Scheduler隐藏wscript父进程、启动shell退出、暂停/恢复/停止/重启、零空闲模型通过：`C:/Users/mio/AppData/Local/Temp/fufucode-gate-scheduler-33af692b082141d6a4cbf63287b64752`。
- 实际一次性私有控制台Ctrl+C反例通过：通用夹具tmp/flow/console-isolation.json，launcher被中断退出0（PowerShell处理信号），gate/workflow保留原PID。脚本确认独立console句柄、提前结束且未到正常exit13，不能把普通退出误计信号通过。
- events30断言、gate结构化身份/坏卡/HEAD/活跃writer/未知投递冻结本地反例通过；本轮源码指纹更新后生产Dispatch须重新绑定本轮证明，旧证明不直接复用。

## 崩溃对账

M热更新误终止了刚开始A修复审核的gate，属真实竞态错误，不隐瞒。已知旧reviewer进程8236后来退出；原保存session日志明确turn_aborted，无review.decided。Reconcile只读核对仓库/thread/run/HEAD/无写入者及原task_started→terminal，在本地持久recovery记录后才恢复同thread。unknown没有terminal仍冻结；不是自动重发不明投递。原日志保留，新恢复日志用runId-recovery-1，不覆盖。

通用框架夹具已于19:04:06UTC进入idle_complete。独立AssertWorkflow确认四次审核（A1 rework/A2 accept/B1 accept/C1 accept）、A原OMP会话不变、三次真实来源提交已为main祖先、固定M thread不变、wire实跑84、C起点恰为B完成集成SHA、两OMP yield成功及正常session_exit、四Codex原生子代理已完成且保存模型均devin/swe-2。保护.gitignore及untracked-user.dat哈希不变。证据tmp/agent-flow/validation.json；本轮中途修代码/显式崩溃对账，sourceConsistencyVerified=false，不用于生产源码指纹证明。

第二轮冻结源码夹具为C:/Users/mio/AppData/Local/Temp/fufucode-flow-f2fd11799c9f4b968b27d61d4fa7ed4b。采用无超时Wait-Event，启动补扫/OS文件事件/Watcher Error唤醒，暂停恢复写control.json，不以定时检测唤醒M。开工前记录八份生产源码SHA，主区预置已修改.gitignore及中文空格未跟踪文件保护夹具。需全流程自动收敛及重放/空闲/重启反例后才能启用生产。

最新Scheduler无模型验证通过：C:/Users/mio/AppData/Local/Temp/fufucode-gate-scheduler-bced3dbd8fc24c85bb49a73b3dd686b7；events30断言与gate坏卡/HEAD/活跃writer/未知投递冻结通过，新证据分别8bb6e109a5d54036b2081c53b1cd17b0、07b2895ab68c429a9c08f1960e24d807。生产当前paused，旧gate已退出；没有声称生产仍运行。Desktop heartbeat持续PAUSED。S2等待皮肤、G1原生门open、D5不解锁；验证完成后安排独立非依赖部分任务。

## 最终源码验证进展（19:38UTC，尚未生产启动）

实现提交37eecae、eecae1d、3e1380e。后两次独立复核补齐继承index/未提交接线保护、原生worker Exited监督及创建时间匹配，详见R-043/044/045。0cce162ba3ce4dcc8b241c2082c6234b模型B实际碰到PID复用，旧只看PID造成保守阻塞；该夹具已暂停后续调用，当前turn可完成，记录不清理。

8cb1e76e16e6417e8768b87ed3e30873初始化实际读完协议但300秒超时，thread01a12745-2c14-7723-8ed7-d65a03540a34保存、needs_attention，无自动重发。无工具/只读/显式devin的独立FREE-PROBE在WebSocket426后实际turn.completed，不能把426直接认定为不可用或故障根因。

最终冻结源码候选eac87a94279f4d939313b566bce865f7使用900秒初始化时限，成功固定M thread01a1274b-f62a-7af0-8e20-1ae94a3f822f，于19:34:50启动通用workflow。任务A OMP/B Codex并行，原生子代理和模型都devin/swe-2。workerStarted/agentStarted存精确创建时间。门房35816、workflow20072、两worker17908/38484及模型10020/21656的实际私有console Ctrl+C中断反例通过（tmp/flow/console-isolation.json）。依赖C前安排自动门房停机→实际C积压→启动补扫恢复固定M；全部完成后自动AssertWorkflow、实际-Once Dispatch重放、比对全部真实模型日志及manager SHA，生成对应当前八份源码指纹的证明。当前尚在运行，不预标passed。

当前源码本地验证：Scheduler生命周期bbb0b29ee6144b3098d357d44b950814；worker强制退出5e5c2f076341409d91a0798d20f4fc42；实际保护函数a15ae7b40a9e475d89900e563c5c7832；gate全部反例3bbd2362ac2a438495061dae39ba977e。均为临时数据，未修改用户数据。实际D4-SNAPSHOT非G1依赖子任务四文件契约/验收已准备并批准在真实repo tmp/agent-flow，尚未Register或派工，等最终源码证明通过才开始。
