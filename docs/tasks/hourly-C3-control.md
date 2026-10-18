# C3 公共控制接线（M 单写）

2026-10-12 C4后续：真实Codex免费模型双任务/部分返工/实际Git收编已过59检查，Control511/Hosting267/App996及构建零警告错误。生产/Unknown失败路线/自动stop/权限不随临时环境通过而关闭；下一C5原生最小客户端，见M-C4-vertical-2026-10-12。

难度 L4，由当前主控M在main实现，不另派共享Core写入者。基线740b7d5；P2继续固定e64dd1b，不随本任务升级。无模型的持久化/进程测试优先；必要模型探针仅devin/swe-2。

实际差距：parallel_control.py 的工作包/批准/意图存在独立JSON，AutomationStore拥有线程/任务/交付/事件，HostJournal只有真实派发队列。不能把Python的awaiting_adapter直接当作运行，更不能在JSON与SQLite双写后声称同一事务。

允许M修改Core/Automation、独立Control测试、必要CLI及客户端适配；本阶段不做原生图、不启动生产后台服务、不迁移用户数据、不恢复旧门房、不修改P2包装范围。继承改动保持未暂存。

可验证增量：
1. 同项目SQLite建立版本化工作包、模式、计划批准、命令回执和执行意图。单事务写批准/意图/事件，默认全人工批准无执行意图；全自动明确未启用。先验证重放、同ID不同payload拒绝、过期版本拒绝、重开保留和事务回滚。纯Control表扩展维持U1 schema兼容；改变U1决定历史语义须正式版本迁移，旧不兼容写入者拒绝新库，验证历史保真、异常回滚及并发开库。
2. 受信消费者把满足依赖的意图绑定已登记thread/task/runtime，再进入HostJournal；调用方仅提交决定，不能伪造执行/集成报告。记录意图与run关联；崩溃在入队前后都先对账，不能重新生成run。身份/版本/冻结/已有写入者检查与B1共用。
3. 旧JSON只做显式、版本化导入/导出；保留来源、旧run/review/integration，不覆盖已知SQLite事实，先临时夹具影子对账，不自动导入用户生产状态。
4. Python/HTML转换为客户端，读取唯一状态与事件；旧接口兼容显式标记，不用旧JSON副本当权威。复杂关系/重组/公共知识规则必须复用既有语义与反例，不因首增量删需求。

首增量完成不是C3整项验收；C4另验两个任务积压、人类串行决定、局部打回、原模型原会话修复、接受与集成分离。每个增量构建与必要行为验证后提交、更新记录/决策/进度；未知状态只冻结/对账，不接管未知会话。无push/历史改写/工作树删除/登录采购部署。

2026-10-12 第六增量落点99ccc50：同库规划/受信绑定/历史归档/四关系/公共知识与定向问答/知识采用真实门禁已逐增量实现，记录M-C3-control-2026-10-11。当前Control194/Hosting257/U1 58/CLI29及solution0警告0错误。仍须实现自动目标停写传输、后继拆合/转交及目标历史映射、完整人类集成授权收件箱和Python/HTML唯一库客户端；之后C4，不把已持久等待条件当已发送stop。E1原生工具阻塞另记，不停本任务、不扩大共享写入者。

2026-10-12 第七增量：后继拆合/转交、源目标/验收完整快照、逐源目标及跨组关系明确映射已实现，退役身份接真实执行/集成/知识/问题门禁。Control257/U1 58/Hosting257/CLI29及零警告错误构建。全项目目标/委托/预算不是这份承接映射，后续单独接；完整人类集成授权、自动stop传输、Python/HTML唯一库客户端与C4/C5仍待。证据M-C3-control第七增量，不把首增量或后继表当整项完成。

2026-10-12 第八增量：同库人类决策inbox读投影和显式集成Authorize/Revoke已接实际MarkIntegrated，Card正文/直接review的活/Unknown门同步落实。Control310/U1 58/Hosting257/CLI29及零警告错误构建；见M-C3-control第八增量。剩余唯一库Python/HTML客户端、部分review后的完整路由、自动stop传输/项目权限与C4/C5；现有review走U1绑定接口，不伪造万能决策或可信Git集成报告。

2026-10-12 第九增量：显式数据库的Python/HTML模式已接真实CLI/U1，单事务state/inbox、公开决定路由、未知命令原ID重试与旧JSON隔离。8项真实CLI/HTTP、8条命令绑定、Control310/U1 58/CLI29和solution0警告错误，Chrome创建/批准/模式及420px图已验。旧入口退役切换、完整部分review后的原会话路由、自动stop/权限、C4/C5仍待；JSON编辑器是维护者过渡界面，不是最终原生UI。见M-C3-client-2026-10-12，不当生产状态已迁移。

2026-10-12 第十增量：公开control.review已接唯一Core/U1库和CLI/Python/HTML，最新批准/停写、task/part revision/run/HEAD/token/CAS门及原ID重放，四决定分项隔离，决定/状态/事件/回执同事务。Control346/U1 58/Hosting257/CLI29、客户端8/HTTP9/模板23及零警告错误构建，真实Chrome审核流通过；PascalCase漏读与缺省decision默认为Accept均红绿修复。原会话修复投递/恢复、Blocked/Deferred明确再审、自动stop/权限/C4/C5仍待；accept不是集成，JSON表单仍过渡入口。证据M-C3-review-2026-10-12。

2026-10-12 第十一增量：Blocked/Deferred显式再审入口已接Core/CLI/Python/HTML，保原decision/卡/run/HEAD/版本，追加历史按插入顺序读，理由/CAS/停写/批准/冻结/集成门及原ID回执同事务。U1 schema2保真迁移/回滚/并发经过真实v1夹具；旧写入者版本拒绝是代码依据，未跑旧发布二进制或迁移用户生产数据。Control388/U1 58/Hosting257/CLI29/App996全量、构建0警告错误、客户端10/模板29/HTTP9+9/Chrome通过。新断连双响应缺陷红绿修复；原身份部分修复意图/受信Host续接、自动stop/权限/C4/C5仍待。见M-C3-reconsider-2026-10-12。

2026-10-12 第十二增量：部分返工授权/受信CLI消费接原HostJournal与唯一宿主，原runtime/model/session续接、指定部分新revision/独立审核，writer投影接共用门与实际start baseline/clean重查。Control442/Hosting257/U1 58/Core340/Pi21/App996通过，Atria选跑SKIP；构建0警告错误、Python10/HTTP10/模板35及Chrome通过，无云模型调用。纯计划不初始化Host，授权不是执行；C4真实最低成本模型/双任务积压/commit和集成、失败恢复/自动stop/权限/C5/生产与跨版本仍待，尚未启用生产自动消费器。见M-C3-repair-2026-10-12。
