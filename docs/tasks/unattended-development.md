# M管理的无人值守开发

2026-10-10：用户授权M自主派工、审核、修复与集成，无需手动转交交付卡。授权仅覆盖项目开发；不自动登录、采购、公开发布、push、破坏用户数据或清理既有工作树。

## 交付队列

2026-10-10 产品化新增范围见 [U 自动开发工作台](automation-workbench.md)：本文件描述当前 CLI 监督机制，U 任务书描述 Fufucode 产品接入，两者不混作验收。真实首项 D4-SNAPSHOT 原会话打回修复后已经接受并集成，主线记录 SHA `57cedd5`，独立验证日志通过；下方 20:27UTC“生产首项尚未完成”为当时快照。G1/G2、整个 D4 与原生自动化工作台仍未验收。

入口为scripts/agent-queue.ps1（PowerShell 7）。M为每个任务准备干净独立worktree、明确group模型ID、完整prompt和唯一JobDirectory。Launch启动隐藏Worker，Worker启动非交互OMP，日志、保存会话、manifest和state保存在主区tmp/agent-queue/<唯一运行ID>。运行信息不是交付卡。

新任务按[结构化事件协议v1](agent-event-protocol.md)以OMP JSONL运行；只有进程成功退出及terminal最终消息中的JSON交付卡校验通过，才生成delivery.received和delivery.json，状态completed_pending_review。不再根据正文词语分流。M以绑定runId/taskId/HEAD的review.decision审核。该状态仅代表待审，执行者陈述仍须独立复核；缺卡、异常、超时不能标完成。每项单独持久化，允许多份积压。运行中的任务只检查PID和状态，不读取半成品卡或验收。

每worktree用命名Mutex约束受管写入者，不能防止用户另开未受管进程。M不得接管用户已有会话；自动任务使用独立新路径。主区仍单写入，只由M收编。任务依赖按真实验收门判断，待审不能解锁下游。

## 持续运行

### 当前状态（2026-10-10 20:27UTC，取代下方历史快照）

冻结生产源码的跨harness全流程已通过，生产gate与独立workflow已启动。固定M沿用thread `01a126ff-597f-7231-9ecb-c287e8d17458`、模型gpt-6.1-sol，只在明确事件时恢复。空闲使用无超时OS文件/Process.Exited事件等待及启动补扫，没有30秒周期检测或15分钟heartbeat。Desktop heartbeat仍PAUSED。当前Herdr界面自动消息入口未验，不依赖它监督。

Dispatch仍仅审核；agent-workflow.ps1另承担批准任务图的派工、原模型/worktree/保存会话回修、独立集成及共享接线、已集成依赖续派，共享唯一main-writer lease。未知投递冻结不重发；单项明确worker失败隔离，不停止独立任务。accept不等于集成或整项验收。

实际D4-SNAPSHOT非G1依赖Core只读子任务已在独立工作树以devin/swe-2启动。S2/G1/D5门禁不变，生产首项真实交付/审核/集成尚未完成，不能把模拟通过称为生产整项成功。进程、证据与控制见[最新生产监督记录](../records/M-event-workflow-production-2026-10-10.md)。

### 历史快照（不再用于判断当前进程与职责）

2026-10-10 固定M更新：当前Herdr交互线程没有已验自动投递绑定，使用独立固定thread `01a126ff-597f-7231-9ecb-c287e8d17458`，每次事件显式exec resume，模型沿用gpt-6.1-sol。devin/swe-2三次同thread接受/打回/原会话修复接受及重放通过后已启用生产FixedManager；初始化只读，不扫描交付队列。状态/控制/日志见[固定M记录](../records/M-fixed-manager-2026-10-10.md)。生产首个真实交付恢复仍未验；完整自动集成/派工R-037仍open，不冒称全自动完成。

2026-10-10 CLI M接任：15分钟heartbeat保持PAUSED。`scripts/agent-gate.ps1` 的真实最低成本提交/交付/审核/打回/原会话修复/接受及重放已验；生产由 `scripts/agent-gate-control.ps1` 经Windows Task Scheduler托管事件审核，显式沿用当前CLI主模型gpt-6.1-sol，空闲仅本地扫描。现场、证据、控制与限制见 [CLI接任记录](../records/M-CLI-event-gate-handoff-2026-10-10.md)。不得把已关闭Desktop视作仍在监督，不恢复模型定时轮询。

Dispatch当前只审核，不自动集成/接线/派工或启动回修（R-037 open）；accept不等于集成。CLI M的自主回修操作已真实验证，不等于门房自动回修执行入口已实现。生产首个Scheduler模型调用/复杂审核尚未验；不能称完整无人值守生产闭环成功。

### 每次事件审核或CLI M恢复的入口（历史heartbeat也引用此入口，不启用轮询）

在真实主仓库 `C:/Projects/Fucode/Fufucode` 显式 workdir 执行。先读 AGENTS.md、本文件、agent-event-protocol.md、execution-contract.md 与最新进度。自动化提示词只引用此入口，不复制全部规则或固化在途任务状态。

扫描 tmp/agent-queue，忽略 manifest 中 probe/transportTest 夹具。按 delivery.received 且无 review.decided 选择待审；必须再次确认 completed_pending_review、agent/worker 均停止、真实 HEAD 一致才读卡。监督器异常须检查 agentStillRunning，不重复启动。旧运行仅退出后 ConvertLegacy，legacy 卡须人工核查。

M 写 review.decision 输入，经 scripts/agent-queue.ps1 -Mode Review -JobDirectory <运行> -ReviewFile <决定> 校验落盘，不直接写 review.json。决定须绑定 taskId/runId/reviewedHead、证据、findings、nextAction、integrationStatus/mainSha。只按 accept/rework/blocked/defer 分流。accept 不等于集成或整项验收；模块待接线由 M 处理，真实集成记录 mainSHA 和门禁证据。

最多两个受管实现并行，原生 UI 验证串行；新派任务明确 L 等级与完整 group 模型 ID。修复优先原工作树、原模型、保存会话，先确认无活跃写入者。遵守执行契约的难度升级规则。模型测试/试跑显式 devin/swe-2。单项阻塞继续其他独立工作；全部完成后暂停 heartbeat 并给事实总结。仅新审核结论、真实故障、外部阻塞或产品决策通知，无变化安静。

OMP Worker在启动shell退出后继续并完成已实测。生产门房由Task Scheduler拥有，不依赖当前交互shell；30秒纯本地补扫恢复文件事件遗漏，不定时调用模型。每次delivery.received且无有效review.decided才审核，投递claim先落盘，dispatching重启冻结自动投递，失败/未知不自动重发。主区协作main-writer.lock保证受管审核串行；后续CLI M集成入口必须持有同lease，未知用户进程不能靠该锁强制隔离。

持久文件支持恢复，不代表关机/睡眠时仍可执行。睡眠后网络请求可能未知；重启结束所有进程，下次用户登录可启动门房，但不自动恢复旧running worker或重发不明review。实际睡眠/电脑重启未验。审核结论与失败本地持久化，不向已关闭Desktop线程声称即时推送。完整自动接线/集成/下一任务调度仍open；普通本地构建可由M执行，所有模型试跑显式devin/swe-2。

异常退出/电脑重启后先核对Worker和OMP的PID、进程启动时间、worktree状态与保存会话，再明确恢复；不把残留running状态自动当失败交付，不自动重开同一写入者。脚本没有实现任意崩溃后的全自动恢复，也没有自动合并代码。实现任务OMP内部2h时间限，Worker另有3h硬看门狗；免费机制探针分别60s/180s，超时只结束本运行的进程树并保留文件。

首批只派G0已解锁的S1样稿；生产风格选择等用户，G1仍待补验，D5不越门。原生computer-use、干净机器安装等没有证据不能以队列启动代替。

修复轮使用唯一新运行目录保留上一轮卡片，Launch传ResumeSession保存会话的绝对文件路径；只有M确认脏内容全属受管任务时才传InheritTaskChanges，启动前的脏清单写manifest。原worktree/原devin/swe-2/原sessionId的真实提交修复/重新交付/接受已验证；继承脏内容与任意崩溃自动恢复未验，不自动夹带。TransportTest仅免费无工具，gate忽略；MechanismTest只允许显式devin/swe-2，在独立临时仓库真实工具闭环，不当产品交付。
