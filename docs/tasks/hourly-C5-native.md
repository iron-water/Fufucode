# C5 原生工作台（M 单写）

## 第七增量（用户时区2026-10-12继续轮，M，L3）：失效待办输入保留

起点350fa461，App公共VM/View与其自测，由M单写，不派新的共享执行者。原待办被外部决定/版本变化移除时，刷新不得丢正在编辑的理由/依据/回答/候选；保留原context/token/version的只读临时输入，明确保存/复制或丢弃，旧决定禁用。成功提交仅完成该原选中输入，其他待办不借此丢稿。保持手动保存与scope边界，不自动换版本/重发/迁移库；项目切换清临时输入，已保存稿不删不串。

验收先红后绿：真实编译绑定回答框编辑→另一SQLite客户端回答→刷新→只读旧输入/焦点保存→严格核对原版本和最新多行Unicode、没有新Core事件；重复观察不多稿、明确丢弃不删保存稿；未曾保存的失效输入仍可保存；项目切换清缓冲且旧文件字节保持。必要App全量与标准solution零警告错误；Core/宿主源码无改不机械重复同一回归。IME/DPI/跨进程长稳/新包不冒称已验，记录M-C5-invalidated-input-2026-10-12。

## 第六增量（2026-10-13，M，L4）：持久化模块公共接线

起点9c04ec9；C5-PERSIST独立原树157d03b停写后M复核192和9反例接受，合格代码收编f6a52b1。M接本地显式保存/恢复创建、决定、取消与对账输入；按项目key+绝对库路径+原宿主生成并持久保存的authority GUID隔离，同路径换库不恢复旧稿。客户端不创建/升级权威；缺稳定身份只观察并拒绝未落盘的决定。手动保存是保守首版假设，旧输入显式恢复、版本/证据变化保留旧稿而不换版本。

所有10类原生命令固定typed DTO/ID/CAS/JSON，先本地Record pending_unknown成功再Apply；真实Core回执观察后原样ObserveReceipt，失败保留未知，禁止后台重发。最近决定和重启未知请求仅只读查Core原ID/类型/精确payload hash匹配回执，不调用Apply。本地generation竞争或损坏失败必须阻止新提交且保输入；原scope结果不写入新项目。客户缓存authority身份，每次连接实际读写库前再查，同路径换库拒绝旧客户端动作。Core权威身份表由owner增量建立，U1 schema2保持，旧客户端snapshot默认格式不扩字段。

范围M公共VM/View/创建与决定partial、MainWindow数据根接线、Core/Control稳定身份/只读receipt与AutomationStore客户guard、定向SelfTest及独立suite solution注册/记录。验收真实窗口编译绑定保存→新VM重连恢复精确Unicode/候选/表单字段；旧版本/库替换拒绝且原文件保持；Core成功但本地尚无回执的未知跨重启只读核对、真正未提交未知不执行；Root真实锁/两个VM同generation失败不吞稿；全部既有原生决定及取消/对账原回执仍过，无云模型，新构建和App/Core/Pi/Control/Hosting及模块独立套件必要验证。原生指针/IME/DPI/跨进程长稳/新包另验，模块收编不当整项生产完成。

## 第五增量（2026-10-13，M，L4）：已取消Unknown执行的明确中断对账

起点daee283。只处理有原取消请求、原Host已记录尝试结束且队列Settled的Unknown运行，不能将它直接接受或当完成。原生按具体run选择并明确只读核查原writer/子进程、实际Git HEAD与tracked dirty状态，输入人工核查依据和理由后确认Interrupted。Core复用原RunReconciler进程规则，绑定run/thread/task、原stop请求、证据token/HEAD/CAS；重新核查后同事务更新Unknown→Interrupted、唯一resolution、原取消结果、人类事件/revision/原回执及该run的未决claim。保持原session/model/runtime/工作树及无settle HEAD，不自动解冻/恢复/重派或审核输出。PID复用/不可核查/活writer/未settled/过期身份必须拒绝；无正式完成证据不得变Completed。客户端核查不迁移库，关闭/浮动/刷新不作决定。M只改Core/Control和共享audit方法/App现有VM/View及新partial/定向selftest；C5-PERSIST独立目录不碰。真实取消后窗口Enter、错误scope/证据/HEAD/revision、已改变外部状态/幂等回执/重新连接、dirty数据保真及必要回归验证。

起点main@db5a439，难度L4：原生graph/公共注册/同库快照与身份决定跨层，M实施，不派另一个共享写入者。免费模型探针仅devin/swe-2；本增量无需模型调用。

第一增量覆盖：连接明确选中的现有U1工作台数据库、不自动建第二权威库或迁移；同一事务读控制/收件箱/真实线程任务部分与依赖元数据；图节点是线程、任务/部分分别可选择，实际Canvas子容器定位，运行过滤不把Active线程误当真实运行；必需决定（计划批准、四类分项审核、再审、返工授权、集成授权/撤销）走原Core带revision/token身份与回执。动作不启动模型/消费者或伪造集成。浮动/关闭生命周期接现有Workbench注册，默认Manual来自数据库真实值。

旧U3 b23120e仍rework，原树/会话不重启/同步/清理。已核查停写/干净及三finding：模板点击类型不符、Canvas定位在错误容器、没有部分选择入口。仅参考其分组布局想法，不直接收编不合格页面或旧JSON控制器，不重写已完成设置/辅助栏。

范围：Core/Automation现有库非迁移客户端入口及图快照、App/Features/Automation、MainWindow.Workspace公共注册/模块首选项与定向SelfTest，记录/进度/决策。验收：不存在/旧格式库不创建或变更；同快照身份一致；真实控件回调绑定duplicate title的不同thread/task/part；宽/窄实际Arrange、依赖任务端口与选中详情；过期/运行者/错误identity拒绝，回执原ID重放不双决定；模式不执行、接受无集成、关闭/移栏/浮动不新建宿主。构建零警告错误及相关Core/App回归。

后续增量：高级知识/问答/重组/竞争选择的原生表单、图动画/编辑、真实指针/IME/DPI、生产宿主连接与持续事件策略。首增量不宣称C5整项或生产全自动验收。界面未支持的决定如实提示主控处理，不提供假按钮或JSON编辑器。

## 第二增量（2026-10-12，M，L3）

起点main@ab7cef5。只补现有收件箱的知识发布/拒绝、定向回答、知识采用、竞争胜者选择、重组批准/拒绝；创建知识/问题/重组方案、图编辑与后台消费者不在本增量。展示原提案文本/来源/版本、双方契约、竞争基准和依据、重组原目标/后继规范与映射，不从证据正文推断决定。按Options显示真实按钮，回答与胜者有独立输入，过期提案只保留Core允许的拒绝。

验收：真实编译绑定表单及点击提交Core；空回答/未选胜者不提交；竞争对象按ID与证据token绑定，不按同名标题；知识发布不自动采用、采用只版本化批准契约而不执行；重组展示映射且不继承接受/集成；外部契约/证据变化拒绝旧决定，刷新不替用户自动重试；同ID回执重放幂等。定向C5和App全量自测、零警告错误构建。无头控件证据不替代系统原生输入/IME/DPI验收。

## 第三增量（2026-10-13，M，L3）

起点main@9b06d6c，既有产品a35f360。M只改现有VM/View及新Creation partial/定向SelfTest、当前任务/记录；独立C5-PERSIST worker拥有新Persistence目录和自身测试，互不重叠。本增量新建/修订任务规范、知识提案、定向问题、四种关系（流水线顺序必须显式）、单来源→单后继替代方案；复杂拆合编辑器仍另增量，已存在复杂重组的批准表单保持可用。

验收：输入完整scope/acceptance/harness/model/artifact，不捏造线程；修改已有规范明确当前版本，不借输入ID静默覆盖。知识来源必填，提案不发布；问答绑定两个不同原版本；关系成员实际规范、竞争基准/依据、流水线显式顺序。单项替代预览原目标→新规范和被影响关系的新ID/成员，保留目标映射并走Core正式proposal，不直接退役/批准。打开表单时固定revision/选项/知识版本，后台刷新不自动给旧输入换版本；过期拒绝保留输入，重读必须明确操作；项目/库切换/返回清旧表单，不连生产宿主/启动模型。真实编译绑定、焦点Enter、无效/过期/回执用例及App全量、构建零警告错误。

核对既有Core门发现规范变更/采用/重组仍只查control绑定的writer，可能漏掉C5首增量已覆盖审核的同线程/同树额外运行者。本增量由M增补Core/Automation/Control变更门与实际hold反例；保持合法未开始受管队列的明确废弃能力，不能以笼统ReviewStopped阻断该能力。worker持久化范围不变。

## 第四增量（2026-10-13，M，L4）：明确取消一次执行

起点63f71c5。现有Host STOP是停止新任务并等待当前执行结束，不可冒称取消writer。本增量人类按实际run/thread/task/owner及证据token/CAS提交持久取消请求，由原唯一宿主取消其正在等待的HarnessExecutor；UI只写Core命令，不按PID杀进程，不另建宿主，不碰C5-PERSIST所有权。

范围Core/Automation/Control新增typed stop命令/同事务请求与回执，Hosting/BackgroundHost消费，App现有VM/View/native selftest，记录。只支持确认为当前宿主启动且Running的一次执行；未知/已结束/queued/外部直接writer拒绝，不新增全自动策略。请求受理不等于停止；取消后无settle HEAD，Harness仍Unknown并需对账，不能变Completed/验收或自动再跑；原Host STOP语义保持。host出生身份不匹配不消费，进程不确定不强杀。

验收：真实hold协议子进程与唯一BackgroundHost，原生输入请求→宿主token取消→实际writer退出且Run Unknown/无settle HEAD、请求结果仅完成取消尝试仍待对账；错误身份/过期/无理由拒绝、原回执幂等、新ID重复请求拒绝；UI关窗/断线不取消，已排队另任务继续，未知不能再取消/复派。无云模型，参数显式devin/swe-2；App全量及Core/Hosting/Control/Pi、零警告错误构建。native无头不代替系统IME/DPI。
