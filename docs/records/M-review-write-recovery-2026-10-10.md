# 生产审核写入故障修复

当地日期2026-10-10，验证时间2026-10-11 03:54UTC。首项生产D4-SNAPSHOT于629cf375交付后，固定M真实收到事件并正常审核完成，但Windows workspace-write拒绝创建主仓库tmp审核输入与行为探针，没有Review事件，claim保持needs_attention；不是传输未投递或产品已接受。

## 原因与最小修复

保存会话的turn_context确认resume已切换workspace-write，没有残留初始化read-only。独立无模型原生Codex sandbox :workspace对照：Temp目录写入成功，C:/Projects/Fucode/Fufucode/tmp/agent-gate新目录Access denied；显式sandbox_workspace_write.writable_roots加入门房目录仍拒绝。当前目录仅Authenticated Users继承修改权限、无reparse，不据此假定具体Codex内部根因。没有修改全局配置、ACL或供应商网络设置。

受管审核启动参数改为当前已授权的danger-full-access，与受管worker/集成一致；初始化仍read-only。固定M与非固定审核入口一致，保留审核职责、唯一main-writer lease、结构化Review检查和未知投递不重发。该权限不等于OS级路径白名单：角色范围仍靠任务协议及受管检查，不能声称沙箱限制了所有未知进程。

Reconcile补齐已知正常结束但未落盘的情况：root独立复核后先经agent-queue -Mode Review落明确决定，再按原保存session的repo/thread/run/task_started/terminal及真实HEAD、停止进程、有效review/event身份对账，needs_attention→decided，零模型调用。completed但无决定依旧拒绝resume；不从正文自动推测、不直接写review.json，不换线程或覆盖原日志。

## 已实际验证

- scripts/agent-review-recovery.tests.ps1：独立临时Git仓库，真实固定M初始化与resume均显式devin/swe-2；实际审核accept经Review落盘，保存turn_context的审核权限danger-full-access。随后仅夹具移除决定模拟正常结束无决定，Reconcile拒绝；经Review恢复再对账成功，重放零模型调用；错误task与非终态state均拒绝。证据 `C:/Users/mio/AppData/Local/Temp/fufucode-review-recovery-e6d127e1e9f942d99c92d7467fad55a1/tmp/agent-gate/repair-validated.json`，同份复制生产tmp/agent-gate/repair-validated.json。
- scripts/agent-review-permission.tests.ps1：真实主仓库CWD，独立devin/swe-2模型仅用apply_patch创建自己唯一tmp探针并读回REVIEW_WRITE_OK，真实file_change completed与turn.completed通过。证据 `C:/Users/mio/AppData/Local/Temp/fufucode-review-permission-3b2a1d13932543c0bc9777a9fe73ee1e/validation.json`。未改用户文件，无Git提交或子代理。
- gate定向反例完整通过，证据 `C:/Users/mio/AppData/Local/Temp/fufucode-gate-e5890ef32ea7413097659609edb334fc`：坏卡/事件/receipt、HEAD、活跃writer、失败transport、重放及不明dispatching重启冻结。新增脚本语法及git diff --check通过。
- 本次改变三个生产脚本，旧八份冻结源码完整流程证明不再代表当前全部指纹。此次为明确权限/对账增量验证，保留旧完整证据和本次三脚本SHA，未冒充重跑完整跨harness流程。

## 真实生产交付恢复

原run3848c5ea6f5f42f6965deb78e0b669fd、HEAD629cf375、固定M01a126ff-597f-7231-9ecb-c287e8d17458不变。root主M用独立.NET/Git临时反例验证原M三个finding：子目录sub/a读取SUB内容但跟踪状态来自已跟踪root/a；删除的已跟踪文件标false；path-escapes-project错误条目IsFreshAsync为true。证据 `C:/Users/mio/AppData/Local/Temp/fufucode-d4-review-7cb458df/bin/Release/net10.0/validation.json`，源worktree未改，夹具索引未变。

明确rework经agent-queue Review入口落盘，原保存日志对账将claim转decided，不重发原审核，不重复建工作树。后续由既有workflow按原模型devin/swe-2、原Codex线程01a1277f-6793-7411-bbdf-f3f6d9564f0c、原worktree恢复；仍只原四文件，不集成未验来源。产品缺陷与整项D4/G1另待复验，不因管理故障修复而标接受。

03:55UTC实际恢复：实现/验证提交a878f38。门房已重启为PID33452（父wscript20084），加载本轮修复；workflow22072恢复事件监听。D4第2轮已由workflow实际启动，run791deb5bb655448bad77f3995c5b6191、worker42212、Codex25996，原thread01a1277f-6793-7411-bbdf-f3f6d9564f0c、原worktree、显式devin/swe-2、base629cf375均现场核对。manager仍原固定thread、ready；原claim decided，无重复审核。当前真实修复正在执行，第2轮交付/审核/集成尚未完成。heartbeat仍PAUSED，S2/G1/D5门禁不变。
