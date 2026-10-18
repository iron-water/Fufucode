# 总控监督恢复与团队命名

用户明确：要求手动控制能力，不是要求暂停开发。此前把 manual 状态归因用户停工是 M 的错误解释，已撤回。手动编辑模式不再触发 Pause，只有明确暂停动作停止新调度。

实现提交 6cde225。控制台 14 项测试通过；隔离原生恢复测试证据 C:/Users/mio/AppData/Local/Temp/fufucode-manager-recovery-4fc166b27e584676bb859ea672c6907a：已知前执行分配失败可显式授权一次原线程恢复；重复、出现执行痕迹、未知断线均拒绝，零模型调用。

2026-10-11 08:26 UTC：原 M 线程 01a126ff-597f-7231-9ecb-c287e8d17458、原模型 gpt-6.1-sol 保留。失败轮次 561af0d2689a4cb6a4645d1c31ccbaaa 保存了启动及输入，没有工具执行或审核决定，原进程已退出、worker 停止、HEAD 一致。经 RecoverFailed 显式对账，生成 recovery-561af0d2689a4cb6a4645d1c31ccbaaa-20261011T082536977.json；原 claim 变为 resume_authorized、reviewAttempt=1。没有写假终态，没有接受产品，也没有删除原失败。

随后实际执行控制台 mode=auto 及 resume。门房和 workflow 的 paused 均 false，workflow waiting_events。磐石 PID59668、哨兵37712、行舟47668、控制宿主33600 持续由 Scheduler 托管；关闭交互 shell 不撤销这些服务。15分钟 Desktop heartbeat 仍 PAUSED。

真实动作：尺规 devin/swe-2 只读验证进程28848（父python53176），日志 tmp/agent-core/logs/5413b4fb19b8cd42d1bf121dcb6e13b88d174e732b8f772e0d6d9620766f4e97-0.stdout.jsonl 已有执行事件；D0 原 worktree/原保存会话格式补交 worker59984、agent55472，任务目录 tmp/agent-queue/prod-readonly-20261010-D0-1-card。该补交仍原免费模型。明镜 M ready，等待验证证据串行审核；尚未据此宣称本轮审核完成。

真实浏览器切到手动后 API 确认 mode=manual、workflow/gate paused=false；再切回自动。状态栏为“磐石在线 · 哨兵在线 · 行舟调度中 · 明镜 M 就绪 · 1执行/2待审”。模式切换不会再次隐式暂停。

英文代号：CORE 磐石、GATE 哨兵、FLOW 行舟、CHECK 尺规、M 明镜、ARCH 远见、PLAN 经纬、RISK 衡岳。名册 docs/automation/coordinator-team.json，M节点详情显示代号与职责；命名不触发专家请求，不改协议角色、模型、身份或权限。
