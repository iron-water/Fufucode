# Fufucode 前端设计准则：五区骨架与区内交互

日期：2026-10-01。五区骨架为用户定案：左栏任务/一般选项、中央对话、右栏tab化视图、底栏终端、独立设置界面。本文按此组织，不再将布局当可选组件集合。功能与布局对齐Codex Desktop，视觉沿UniGetUI；ZCode只作区内交互参考。本文为研究与实施规格，未代表产品验收。

## 层0：五区骨架总览

```text
主工作界面
┌──────────────────────────────────────────────────────────┐
│ Caption / 全局导航、窗口控制                              │
├─────────────┬──────────────────────────┬─────────────────┤
│ ① 左栏      │ ② 中央主面板             │ ③ 右栏          │
│ 任务/会话   │ 会话头                   │ 视图tab         │
│ 分组/搜索   │ 对话时间线               │ diff/文件/审查  │
│ 一般选项    │ 队列/恢复提示 + composer │ 预览等          │
├─────────────┴──────────────────────────┴─────────────────┤
│ ④ 底栏：可加入的终端窗口 / tab / 高度调节与收起           │
└──────────────────────────────────────────────────────────┘
⑤ 独立设置界面：分类导航 → 设置内容 → 返回主工作界面
```

①左栏负责找到任务与进入一般选项，包含全局入口和任务/项目层级；图标轨是左栏内部组织方式，不是第六区。

②中央承载请求、执行过程、答案与继续输入，不被成果区替代。

③右栏承载tab化工作视图，与会话/文件上下文联动，具有自己的滚动与宽度状态。

④底栏承载可加入的交互式终端，不将agent工具日志误当终端。

⑤设置是独立的界面目的地，与主工作界面明确往返；其在Codex中是独立页还是OS窗口需证据确认，二者不由产品定案推断。

五区是产品要求（用户本轮定案），不等于已有截图展示了全部五区。现有S00只直接展示部分主窗口状态；右栏展开、底栏、设置与各区完整交互需后文静态核实或补图。

本轮版本说明：指定26.928.3736.0路径已不存在；本轮Get-AppxPackage读取到26.928.4866.0，使用现存app.asar作补充E4866，绝不把它归到3736。C13/CX/ZP/U/G等保留上轮证据及其版本/静态边界；本轮新增证据单独列出。没有对旧版本重新取证、降级、安装或修改配置。

### 0.1 使用方法与证据口径

以 `docs/decisions.md` 的双轴定案为最高口径：功能与布局交互对齐 Codex Desktop，审美对齐 UniGetUI；功能路线图沿 `docs/research/codex-feature-study.md` 的采用/改造采用判断。D 按真实 Core 能力实现，不以截图上的按钮推定 Pi 已有同等能力。

每条规格用编号追踪；方括号为证据索引（附录），`高/中/低` 表示对应事实的证据置信度；“建议”表示 Fufucode 原生设计选择，不表示从参考产品测得该值。截图能证明可见形态，不能证明点击后的行为；包内资源能证明分支与声明，不能证明账号功能门可达、运行成功或实际 computed style；官方页面统一标“文档声称”。

上轮只读 Codex/ZCode 安装资源、既有图集、官方资料与项目契约；不运行 ZCode，不修改配置，不安装包，不构建产品，不执行真实模型任务。三个子研究分别负责 Codex 深挖、ZCode 静态研究、UniGetUI 与 G 映射，主研究负责交叉核对和本文汇总。

## 1. 左栏：任务列表与一般选项

### 1.1 区定位与尺寸行为

左栏由一般选项入口与任务导航组成。默认260DIP、可调240–320是既有建议值；列表独立滚动，顶部快捷入口与底部一般选项可保留。准确Codex尺寸区分S00像素与静态CSS，后文补证。

| 编号 | 基准证据与置信度 | Fufucode规格（DIP；建议值） |
|---|---|---|
| L01 | 完整截图有图标轨、侧栏、主体，右上有任务附属卡。[S00] 高 | `Window → Caption → Rail / Sidebar / Workspace`。Rail 初始 48–56，Sidebar 初始 260、可调 240–320；图标轨只放真实可用目的地。侧栏折叠后项目/会话入口仍可访问。 |
| L07 | 截图线程项hover尾键，静态行有稳定尾轨/截断/focus。[S00,C13] 高 | 行高32–36，状态/标题/固定尾区三列；尾区默认相对时间，hover、focus-within或菜单打开时显动作；标题宽度不跳。完整标题、项目路径可从详情读出。 |

### 1.2 区内组件清单与既有编号

| 编号/组件 | Codex行为或形态→Fufucode规格 | G映射 | 视觉条目 | 证据/置信度与接线边界 |
|---|---|---|---|---|
| K01 图标轨/侧栏 | 顶部全局入口、项目折叠组、线程列表、最近分区；列表内加载/失败/空态分开。会话切换先保持身份，迟到回调校验sessionId | `icon/icon-small.svg`、`actions/.../new-session-...`；搜索/文件夹/chevron无G对应 | U01/U03/U06 | [S00,C13] 高；新增分组/置顶/搜索按元数据能力实施 |
| K02 会话行/行菜单 | hover/focus动作与当前项独立；归档、重命名等入上下文菜单。归档与删除明确区分，切换失败留在原会话 | 无专用G资产 | U03/U06 | [S00,C13] 高（可见/静态）；删除流程不是截图证明 |

| 编号/场景 | 可见状态与动作 | 实施约束、来源/置信度 |
|---|---|---|
| I10 分叉/归档 | 分叉注明从哪条消息之前开始；归档可恢复；状态切换有明确所属项目 | Pi fork是新文件/ID，clone是当前leaf复制，同文件树导航不同；产品元数据/锁交接未交付，不能把RPC结果直接当已创建桌面会话。[A8] 高 |

| 编号 | 可证模式与来源/置信度 | Fufucode借鉴与实施边界 |
|---|---|---|
| Z01 会话/任务组织 | 官方task-management描述Grouped/Workspace/Timeline、时间排序、搜索/归档；包内存在task/session/agent服务。[ZP,ZO1] 高（存在）/中（实际UI） | 侧栏按项目、最近、已实现的归档/置顶组织；运行/待操作/失败/未读分开，稳定session ID优先。ZCode task模型不替换Pi持久会话和Desktop元数据。 |

### 1.3 既有深挖挂接

研究报告§7.2用户消息导航/书签与任务列表不是同一层；§7.4桌面元数据/服务端状态分层用于选中、未读、折叠持久化；§13.8稳定行、尾部动作、项目/最近分组与新标签最近工作列表挂入本区。共用模块的所有分组不等于当前Codex可见。[C13,P] 高（静态）/中（建议）。

### 1.4 按区补深：形态、规格、挂接/缺口

**Codex实测形态与证据。** 本轮重新查看S00：左部有Codex切换标题、铃铛/搜索、新聊天、项目组/会话项、展开显示/最近；hover项可见尾键与会话/项目/相对时间详情。[S00] 高（截图可见）。图标轨中多种图标可见，但不能仅凭glyph认定其一般选项名称/路由；列表运行状态、排序菜单、搜索结果和一般选项展开状态未实测。官方Projects页面描述置顶/重命名/归档/搜索，属于文档声称[O5]，不是本机点击证据。

**Fufucode对应规格建议。** 左栏顶部放新任务/搜索/活动入口，中部按项目与最近组织稳定ID任务，底部或一般选项菜单通向设置/扩展/已实现的全局页。选中、运行、待操作、失败、未读互相独立；选择排序时说明最近更新/手动顺序，不能未实现排序却放假选项。较多任务允许折叠/分页，hover与focus显同一尾区，菜单打开不改变标题宽度。[S00,C13,P] 中（建议）。

**编号挂接或缺口。** L01/L07、K01/K02、I10、Z01为本区主体；K14活动入口、K08命令搜索是跨区共享。一般选项的当前可见入口/路由与运行状态glyph仍需截图；新版静态入口见§1.5，Z01不当Codex证据。

### 1.5 本轮包内调用链补证

**当前包静态补证 [E4866，静态高；账号可达性未核实]。** app-initial 的 8065170–8066490 附近把“Organize sidebar”组织模式定义为 project / connection / list，把排序定义为 updated_at / created_at / manual；1971966附近区分recencyAt/updatedAt与createdAt。6926500–6928800定义重命名、置顶/取消置顶、已读/未读、归档、复制链接/工作目录、移动项目/自定义分区；各项是否提供由动作集合决定，不保证每一行都有全部动作。6947000–6949000的remote行分开传statusState、unread、indicatorHoverNode和useStableTrailingRail；这是远程行静态路径，不能移植为本地行运行glyph已实测。搜索S00只见按钮，搜索字段、排序实际生效及本地运行标记仍未核实。

一般选项不是任务行操作：6619000–6621100的导航目的地明确含Scheduled、Plugins或Skills、Sites（非默认可见）及Library；Plugins/Skills的选中路径检查/skills与/plugins，Sites检查/sites。Scheduled受能力条件控制；这些是新版共用宿主中的条件入口，S00图标不能据此逐一命名。6743219/6750093有设置触发与profile footer“Open profile menu / Open settings”分支；一般选项的完整可见菜单、所有目标当前路由仍需截图。Fufucode按K01/K02承接任务导航，K08/K14承接共享入口；一般选项建议仅给已有目的地，以标签/tooltip说明去向。

## 2. 中央主面板：对话

### 2.1 区定位与尺寸行为

中央是主阅读与输入面。正文与composer共享768DIP建议阅读列；会话头、时间线、输入slot分开，队列/恢复提示归输入上沿。

| 编号 | 基准证据与置信度 | Fufucode规格（DIP；建议值） |
|---|---|---|
| L02 | CSS desktop 侧栏 clamp 含 preferred 275px，正文列48rem，宽Markdown56rem。[C13] 高（静态） | 正文与 composer 共用居中阅读列，初始 MaxWidth 768、水平安全留白20–24；宽代码/表格允许局部横滚或成果面板。不得把 CSS rem 换算写成屏幕实测。 |
| L03 | 截图正文、用户弱背景块、成果卡和输入区有明显层级。[S00] 高 | 页头48–56：会话标题、项目/分支、一个活动摘要、面板入口。正常连接/用量/扩展数量进入详情；阻止发送的错误必须直接露出。避免每种状态各占一颗彩色 chip。 |
| L04 | 包内主正文滚动层与底部composer slot分离。[C13] 高（静态） | Grid 分页头/时间线/输入区；右成果区独立滚动，不能用全窗 ScrollViewer。composer增长后最后一条消息仍可见；向上阅读时保留锚点，底部提供返回最新入口。 |

### 2.2 区内组件清单与既有编号

| 编号/组件 | Codex行为或形态→Fufucode规格 | G映射 | 视觉条目 | 证据/置信度与接线边界 |
|---|---|---|---|---|
| K03 用户请求/助手正文 | 请求弱表面、助手以文档段落为主；复制/更多动作hover及键盘可达，完整答案复制与流式状态区分 | 无 | U02/U04 | [S00,C13] 高 |
| K04 工具活动卡 | read/search/edit/command用短摘要；展开参数、输出、耗时；成功过程紧凑，失败与待审批直接露出。长输出内滚动上限，不让每段都成为滚动窗 | 无对应tool图标；不将pause当运行工具 | U04/U05 | [C13] 高（静态）；执行事实来自Core |
| K05 变更摘要卡 | 头部计数、路径列表、展开剩余、成果入口；±行数只有真实diff统计才显示。点击文件进入对应范围成果；不能凭卡标题承诺撤销 | 无diff/undo资产 | U04/U05/U06 | [S00,A8] 高；A8只有文件范围DTO，patch/行数/还原待契约 |
| K06 Composer | 上沿队列/恢复提示，中部多行草稿，下沿附件/模型/思考/权限与主动作。未支持附件/语音时不放可点假入口 | `stop`用于停止；`resume`只用于明确恢复；send/attach/mic无G对应 | U02/U03/U07 | [S00,C13,P] 高；IME、草稿恢复和投递契约优先 |
| K07 权限选择器/审批卡 | trigger给当前行为配置，flyout给可比较说明；审批留在工具上下文，显示请求内容/范围/待处理和选择。说明加载信任与审批桥，OS隔离单独标 | 无权限盾牌资产 | U03/U05/U06 | [C13,O2,P] 中/高；通用本地审批卡真实视觉未核实 |
| K12 空态/中断/失败 | 空会话轻插图+任务入口；主动停止用中性状态，失败附原因/恢复动作；加载不用空态图 | `empty/conversation-*`、`empty/interrupted-*`、`empty/error-*` | U04/U05 | [G,C13] 高（资产）/中（建议）；插图不替代状态文字 |

| 编号/场景 | 可见状态与动作 | 实施约束、来源/置信度 |
|---|---|---|
| I01 空闲发送 | 一个明确发送动作；保留草稿至投递状态明确 | 使用现有Coordinator能力快照；受理不等于完成。[P] 高 |
| I02 运行中补充 | “插入本轮”与“下一轮排队”分开，说明生效时机；有队列显示计数与展开 | 映射已核实steer/followUp，不能靠改按钮名产生能力。已提交队列与本地未提交草稿分开；修改/取消只在有接口时开放。[P,C13] 高 |
| I03 停止 | Stop持续可达；停止处理中反馈；区分用户中断/工具失败/未知结果 | 沿现有clear_queue→abort语义，是否清队列按实际策略说明，不把最后一条文本当停止完成。[P] 高 |
| I04 恢复 | 原会话标题/项目保留，历史加载与运行时连接分开；失败给原因和重试 | 不静默新建、不自动重发未知投递请求；迟到响应校验sessionId/连接代次。[P,C13] 高 |
| I05 审批 | 当前工具位置摘要；展开目标/操作/范围；批准/拒绝后pending可见，过期不可再次提交 | 不把项目信任标为工具沙箱；作用范围来自bridge契约，不提供未实现的永久允许选项；卡图形是原生建议。[P,O2,C13] 中/高 |
| I06 变更卡完整流 | settled/查询后展示范围→展开文件→查看详情→返回仍保留阅读位置；外部变更时刷新 | workspace=整个Git工作树状态；session=当前分支确认成功write/edit路径。不能说后者涵盖bash副作用、全部修改或全部由agent创建。[A8,S00] 高 |
| I12 键盘/中文输入 | Enter/快捷键策略可见；中文组合时Enter不发送；Esc优先关浮层再处理页面 | 焦点所在终端/编辑器有自己的快捷键域；命令面板不得抢IME组合；只对相关场景作验收。[C13,P] 中/建议 |

| 编号 | 可证模式与来源/置信度 | Fufucode借鉴与实施边界 |
|---|---|---|
| Z02 工具卡生命周期 | `toolCall`含inputStreaming/pendingApproval/running/success/error/cancelled，另有tool-input流事件；官方变更记录声称修复失败卡错误显示/不完整残留。[ZP,ZO6] 高（静态） | 每个tool call有稳定身份与可展开参数/结果；流式输入标为尚未定稿，message_end定稿覆盖。失败卡露出原因，取消不画成红失败；整个turn完成仍取Pi agent_settled，不套ZCode枚举判Pi终态。 |
| Z03 任务审批关联 | 包内四行为模式及PermissionRequest schema；官网描述精确动作、pending归属、单次与长期规则。[ZP,ZO3] 高（schema）/中（行为） | 审批绑定sessionId+请求ID/tool call，切会话仍能定位，批准/拒绝后有pending反馈；长期规则/Full access仅供认识交互层次，Pi桥未提供的范围不呈现。官网提到审批阻塞composer，Fufucode应按Core可用动作决定禁用范围，不能整页一律锁死。 |

L08：以上是根据 Codex 布局归纳的 Fufucode 结构建议，置信度中 [S00,C13]。首页只突出项目上下文与输入；空会话、加载、配置缺失和故障分别呈现。建议卡点击先填草稿，若立即发送必须明确动作。完整 Codex 新聊天hero与全局搜索结果页仍未有截图证据，不把共用Work/ChatGPT模块当当前Codex首页。

I15 [S00,CX，高（形态/静态）/中（建议）]：截图显示变更卡前3项和剩余数量，当前包sharedSnapshot卡也有前三项/更多/行内diff分支。Fufucode初始3项、剩余展开是合理建议；从A8文件列表扩展至Diff须另取内容契约。该sharedSnapshot分支不是live卡“撤销/查看变更”的点击处理器证据，完整live流程仍未核实。

### 2.3 既有深挖挂接

研究报告§7.1长对话窗口/稳定锚点：虚拟化估计不能当固定消息高度；§8.2压缩/上下文与普通token统计分开；§9.1权限模式与审批范围解耦；§11输入队列与草稿、未知投递状态区分；§13.3–13.7页头、过程组、用户/助手块、composer与状态矩阵归本区。§6材料版本/证据等级沿附录保留。[C13,P] 高（静态/契约）/中（设计）。

### 2.4 按区补深：形态、规格、挂接/缺口

**Codex实测形态与证据。** S00可见用户弱背景请求、助手正文/用时分隔、最终回复、变更摘要和composer；右上任务附属卡不能当右栏展开。输入上沿没有可辨认队列/恢复条，底部有附件/权限/模型与声音相关图标；上方会话头被截图当前滚动状态弱化，完整字段和点击行为未实测。[S00] 高（可见）/低（不可见状态，不作结论）。旧C13与CX有slot、用户/助手/activity分块、审批/权限条目路径，属于静态而非实测。

**Fufucode对应规格建议。** 页头给标题、项目/分支和单一运行摘要；时间线分用户请求、assistant文档、工具/思考活动及成果，审批/失败就地可见。composer按队列/恢复条→草稿→选项与主动作组织；无队列不占空行，停止恢复与普通草稿分开，长草稿增长后更新底部视区，保持阅读锚点。[C13,P] 中（建议）。

**编号挂接或缺口。** L02–L04/L08、K03–K07/K12、I01–I06/I12/I15及Z02/Z03保持原条。会话头与queue/restore的当前本地渲染仍需核实；共用静态队列声明见§2.5；真实运行/待审批/恢复状态截图缺失，不能把ZCodependingApproval当Codex实测。

### 2.5 本轮包内调用链补证

**当前包静态补证 [E4866，静态高；视觉位置中/未核实]。** local-conversation-page-c957e56ff16a.js 的31621附近将clientThreadId/conversationId、archived preview、renderComposer/showComposer分开；34085/34783有shouldResume、summary panel与MainContentLayout分支，49493–50300将辅助组件和composer作为不同slot组合。不能用这些压缩符号反推所有提示条名称。queued-message-list-339997195d26.js 的3781附近明列编辑、删除、发送现在、重排与恢复回调；4660/5023给“因中断暂停队列”和Resume条，9941给投递未确认提示，7422给队列独立纵滚与max-height:30dvh。这是共用composer组件静态能力，尚未实测Codex本地任务每个状态的挂载与按钮生效。

对应I02/I03/I04/I05/I12：Fufucode将暂停恢复与投递未确认分开显示，按真实能力给编辑/删除/重排动作，未知投递先让用户核对时间线。会话头具体字段、工具/思考分块的展开态、composer附件上沿和队列条相对位置继续列截图缺口；不将共用组件能力当作Codex运行通过。

## 3. 右栏：tab化工作视图

### 3.1 区定位与尺寸行为

右栏必须承载diff/文件/审查/预览等视图；视图内容按能力接线，宏观区位不再列为可选。初始面板最小360DIP为建议；宽窗并排、窄窗抽屉/专页保持相同视图身份。

| 编号 | 基准证据与置信度 | Fufucode规格（DIP；建议值） |
|---|---|---|
| L05 | split-view有持久化、separator与抽屉分支，默认720可被调用覆盖。[C13] 高（静态） | 并排最小预算约 Rail56 + Sidebar240 + Chat520 + Panel360 + gap；不足预算先将成果区转抽屉/专页，再收侧栏。该预算是建议，D应在800/1200/1600 DIP实测断点。正文不随窗口缩小字号。 |
| L06 | 分割条有当前/最小/最大语义与focus。[C13] 高（静态） | GridSplitter视觉1–2、命中8–12；支持键盘调整、关闭与焦点恢复。重开按当前窗口限幅偏好，不能恢复到屏外。面板打开不清草稿、不重置主对话阅读位置。 |

### 3.2 区内组件清单与既有编号

| 编号/组件 | Codex行为或形态→Fufucode规格 | G映射 | 视觉条目 | 证据/置信度与接线边界 |
|---|---|---|---|---|
| K09 Diff成果区 | 范围→文件→sticky文件头→unified/split；小宽用unified、宽布局可split。完整路径可复制，长代码换行/横滚可选 | 无 | U02/U04/U05 | [C13,A8] 高（静态/契约）；内容读取/stage/提交另需Core |

| 编号/场景 | 可见状态与动作 | 实施约束、来源/置信度 |
|---|---|---|
| I07 Diff/撤销后续 | snapshot范围→打开Diff→选择局部/整文件操作→确认影响→执行→重查→反馈 | 当前A8没有patch/还原接口；禁用或省略写操作并注明待契约。后续撤销必须说明对象/范围，避免覆盖用户原改动；这是建议流程，非Codex点击实测。[A8,C13] 中/高 |

| 编号 | 可证模式与来源/置信度 | Fufucode借鉴与实施边界 |
|---|---|---|
| Z05 编辑记录/安全撤销 | 包有gitService/gitCheckpointService；官网Edit History声称整组安全性检查、不能安全还原则不撤销、终端改动不在范围。[ZP,ZO5] 中（仅服务名+文档） | 明确会话直接文件写入与Git工作区状态，展示范围/基线/外部改动；未来撤销先比对可恢复快照，有冲突保持原文件。此为借鉴建议，A8当前不含还原接口，也不能声称Codex/ZCode撤销点击已实测。 |

### 3.3 既有深挖挂接

K10中的文件编辑器子职责挂本区：tab、完整路径、dirty、文件树、编辑区域、外改/失效处理，原K10记录见底栏原条。研究报告§10.1结构化diff（scope/viewType/file/hunk）、§10.2对话回滚≠磁盘恢复、§10.3成果tab绑定会话、§10.5浏览器面板未核实、§13.7工作面板独立滚动挂本区。§12功能架构图中的Git/文件/浏览器服务边界保持不变。[C13,A8,P] 高（静态/契约）；预览可达与实际页面未核实。

### 3.4 按区补深：形态、规格、挂接/缺口

**Codex实测形态与证据。** S00没有展开的右栏tab strip/正文；只有右上任务摘要和页头面板按钮，所以右栏tab枚举、宽度、断点与独立滚动未由截图实测。旧C13/CX确认Diff/editor/terminal等静态模块及split-view机制，但模块存在不证明都属于当前右栏或所有tab同一布局。当前五区必做是用户定案，形态细节另取证。

**Fufucode对应规格建议。** 右栏tab使用稳定panelId、projectId/sessionId、kind与路径/来源身份；Diff/文件/审查/预览各自维护选择、滚动、dirty与加载错误。中央变更卡/文件链接唤起相应tab并定位路径/行，右栏关闭后回中央原锚点；窄窗转抽屉不丢tab身份。审查标明工作树/分支/提交范围，批注保留side/line/hunk fingerprint，基线变化标过期。预览内容能力未接时区域仍保留可解释状态。[C13,A8,P] 中（建议）。

**编号挂接或缺口。** L05/L06、K09与K10文件职责、I07/Z05；K05/I06/I15是中央→右栏联动。浏览器归属、审查tab与Diff-tab关系、所有具体视图类型和最小宽/默认开关需要当前静态调用链与截图，不以Z05作Codex证据。

### 3.5 本轮包内调用链补证

**当前包静态补证 [E4866，静态高；当前可见性未核实]。** app-initial 8693300–8696500的实际New Tab动作集合含Files（open-file）、Side chat、Browser、Desktop（远程Devbox）、Changes（review，展示local diffs）、New page、Terminal；插件/MCP动作仅在target=right路径追加。列表由能力布尔值、requiresCodexAccess、dropDestinations与showInNewTabLauncher过滤，故这是条件全集，不是账号当前可见tab清单。Files入口不等于每个文件editor tab；Changes是review动作的显示名，不能凭名字把Diff和审查写成必然两个独立tab。3563513的openReviewTab命令带codexLocal条件；3670941的浏览器动作发open-browser-tab host消息；中央文件链接和变更卡的完整点击链仍未实测。

thread-app-shell-chrome 17050–18000分别读取right/bottom tabs；28600–31900的Tools / More tools / Plugins and MCPs承接动作列表。split-view 596–1000定义可覆盖的minLeftWidth/minRightWidth、defaultLeftPercent、drawerBreakpoint；16308附近默认值为左右最小160px、handle12px、断点720px、比例0.5，持久化key为id:split-left-width。这些属于通用split-view，未证明该页调用不覆盖，也未量得右栏默认宽度。9870–12600两侧绝对定位/overflow-hidden隔离外框；每个tab正文是否独立纵滚需按内容组件/截图核对，不能由外框overflow推出全部内滚。

Fufucode的K09/K10、I07按“视图种类→稳定tab身份→上下文定位”实现；文件、审查、预览各自保留状态，浏览器/插件等只列条件扩展。K05/I06/I15提供中央唤起入口。L05/L06保持既有建议尺寸及历史CSS边界；新版通用默认参数不冒充3736或实时像素。

## 4. 底栏：可加入的终端窗口

### 4.1 区定位与尺寸行为

底栏是主工作界面下方可唤起/收起的终端工作区；高度可调且持久化后按当前窗口限幅是Fufucode建议。terminal tab的布局和唤起/实例策略在第二步核实；不以右栏存在terminal模块就认定真实底栏形态。

### 4.2 区内组件清单与既有编号

| 编号/组件 | Codex行为或形态→Fufucode规格 | G映射 | 视觉条目 | 证据/置信度与接线边界 |
|---|---|---|---|---|
| K10 文件/终端面板 | tab、路径/dirty、文件树和文本区；终端是独立PTY生命周期，切tab保留选择与滚动 | 无 | U02/U04 | [C13] 高（静态）；后续独立切片，不以工具TextBlock冒充PTY |

| 编号 | 可证模式与来源/置信度 | Fufucode借鉴与实施边界 |
|---|---|---|
| Z04 终端与预览 | 包服务注入terminalService，shortcut含toggleTerminal；官网ADE Tools声称终端快捷键与预览tab/后台命令。[ZP,ZO4] 中高（静态/文档） | 原生dock/PTY与Pi RPC进程分开，stdout仍只解析JSONL；工具输出在时间线可追踪。后续做独立终端生命周期，不以工具输出卡冒充终端。 |

### 4.3 既有深挖挂接

K10跨区：文件编辑器归右栏，交互式PTY归底栏，tool命令输出归中央K04。研究报告§10.4独立PTY会话、§12终端宿主服务边界、§13.7 terminal tab/shrink0/min-height0/xterm填满剩余高度挂本区；原“后续切片”只表示运行能力需接线，不撤销本轮底栏必做骨架。[C13,P] 高（静态）/中（原生建议）。

### 4.4 按区补深：形态、规格、挂接/缺口

**Codex实测形态与证据。** S00未显示终端底栏；01–03错位裁图也不提供底栏证据，因此底栏高度、tab数、终端唤起/收起后的视觉未实测。官方Commands区分Windows Ctrl+J底栏与Ctrl+`终端，changelog26.601描述默认终端位置可为bottom/right，均为文档声称[O3,O4]；旧C13仅确认terminal/xterm与PTY宿主静态生命周期，不能替代底栏调用路径。

**Fufucode对应规格建议。** 底栏独立于composer，显示终端tab、创建/关闭/收起动作与可拖高度；初始建议240DIP、高度下限120，按主窗口剩余视区限幅，非Codex测得值。收起仅隐藏视图，结束shell明确为另一动作。为多实例terminalSessionId保留模型，但是否第一片开放多tab按已交付服务；终端输入/resize走独立PTY，agent工具命令仍在中央活动卡，不把shell用户输入写到Pi JSONL。[P,C13] 中（建议）。

**编号挂接或缺口。** K10终端子职责、Z04，I12焦点快捷键为共享，L06分割语义可复用但原L06是横向右栏不能冒充底栏高度证据。新版bottom-panel宿主与高度声明已补于§4.5；默认位置实际选择、tab生命周期仍需展开/双终端/收起/agent命令并行的截图。

**本轮当前包补证。** E4866的`thread-app-shell-chrome-8b68684a5e05.js`字符偏移96032/96167附近明确区分bottomPanel close/hide，后者描述隐藏而不关闭其tabs；96908附近分别注册toggle-bottom-panel和toggle-terminal；98446附近有页头底栏toggle。17167附近按panelTarget读取right/bottom两套tabs集合。[E4866] 高（静态）。这补足底栏是有独立宿主/命令和保留tabs状态的机制；尚不证明每次隐藏shell都保持运行，也不证明当前窗口已启用该功能门，终端多实例/高度仍待下文补证。

### 4.5 本轮包内调用链补证

**当前包静态补证 [E4866，静态高；运行与显示未核实]。** app-initial 3767800附近的DXr将高度限制为max(160,min(height,mainContentHeight×0.5))；默认280px，持久化key为app-shell:bottom-panel-height。3767965–3770400的NXr使用顶边拖柄，低于PS(160)触发隐藏，否则更新/拖动结束保存；PS转换与实际屏幕缩放未测，因此不能把阈值写成实测像素。终端选项菜单在thread-app-shell-chrome 32801附近明列Open bottom terminal；96032/96167区分Close与Hide bottom panel（保留tabs）。

terminal-tab-bacac6dd9db8.js 5229–6400按sessionId读取环境/连接/退出/错误状态，并传onNewTerminalTab；terminal-panel-8d873f4df63d.js 9377/9845以sessionId或conversationId标识面板，fit后发送cols/rows resize。多tab/独立终端会话是静态支持，尚未启动两个shell验证；agent后台命令另有local-conversation-background-terminal-tab/open-local-conversation-background-terminal-tab路径，仅名称/模块存在不能证明与交互终端共享同一实例。Fufucode沿K10/Z04的参考职责建立独立PTY服务，保留agent工具卡；240/120DIP仍是建议值，Codex新版280/160px作为带版本的比较依据。

## 5. 独立设置界面

### 5.1 区定位与尺寸行为

设置是独立于聊天工作面的界面目的地：分类导航、组标题、控件行、应用状态。是否OS独立窗口/应用内独立页不得凭名称判定，第二步核对路由与宿主。

### 5.2 区内组件清单与既有编号

| 编号/组件 | Codex行为或形态→Fufucode规格 | G映射 | 视觉条目 | 证据/置信度与接线边界 |
|---|---|---|---|---|
| K11 设置/扩展行 | 分类、组标题、左说明右控件，窄窗转上下；未接线解释原因，已保存/当前生效/待重启分开。扩展未安装、无匹配、失败三种空态 | `empty/extensions-{light,dark}` | U02/U04/U05 | [C13,P] 高（静态）；只呈现本产品真实设置 |

| 编号/场景 | 可见状态与动作 | 实施约束、来源/置信度 |
|---|---|---|
| I11 设置保存 | 保存成功、当前已应用、等待重启和失败回退各有文案；dirty离页可继续编辑/丢弃 | 主题即时应用、模型/代理按现有链路；未接线字段不得可点伪保存。提醒靠对应控件/InfoBar，不重复每次toast。[C13,P] 高 |

### 5.3 既有深挖挂接

研究报告§9.4模型/扩展/MCP能力边界、§13.6设置导航搜索/分类、离页dirty、扩展modal内滚动与窄窗重排挂本区。设置保存/当前会话已应用/待重启分开；主题即刻应用不推出全部设置即存即生效。[C13,P] 高（静态/契约）。

### 5.4 按区补深：形态、规格、挂接/缺口

**Codex实测形态与证据。** 现有图集不含设置，独立OS窗口或应用内整页未由截图确认。旧C13可证settings-page的SlateLayout、sidebar/content、可见分类过滤、搜索和dirty离页dialog；官方Commands有Settings入口/deep link但不证明窗口宿主。[C13] 高（静态），[O3] 文档声称；当前独立页/窗口和返回路径需包内路由/宿主补证。

**Fufucode对应规格建议。** 先按独立设置界面目的地实现：分类导航+内容，进入时保存原会话/右tab/底栏布局，返回恢复身份与焦点。搜索结果显示所属分类、未接线项说明原因；控件左说明右当前值，窄窗转上下。全局默认与当前会话生效分开，dirty离页按真实保存方式处理；设置期间会话执行状态由宿主保持，不自动停止Pi。[C13,P] 中（建议）。

**编号挂接或缺口。** K11/I11为主；U01–U09、I14通知偏好、I13快捷键为全局在设置中的呈现，K07当前权限配置与全局默认不混淆。缺分类当前列表、独立窗口/页的真实Chrome、返回聊天后布局恢复截图。

### 5.5 本轮包内调用链补证

**当前包静态补证 [E4866，静态高；宿主窗口未核实]。** settings-page-8b619b95675d.js 39700–42100含Personal、Teams、Integrations、Coding、Archived分类集合；Personal包含general-settings/notifications/appearance/keyboard-shortcuts等，Integrations包含plugins-settings/file-type-handlers/MCP/skills等，Coding包含hooks/connections/git/local-environments/worktrees等，Archived含data-controls。这是共用全集；可见导航来自settingsNavigationSections与功能条件过滤，不能把account/billing/Work专属分类一概列作当前Codex可见。

该组件匹配/settings/:section/*，有搜索、远程host过滤和离页dirty保护。38940“Back to app”文案，48129仅legacy分支传onBack；49833的xi优先history(-1)，其次记录目标replace导航，最后返回/。因此已确认应用内设置路由与往返机制存在，尚未确认当前Codex使用legacy/另一宿主路径，也未排除Electron独立窗口承载该路由；“独立页还是独立OS窗口”继续未核实。K11/I11的Fufucode规格明确为独立设置目的地、分类导航/内容、回到原会话；是否另开OS窗口由产品实现选择，不能借未核实Codex宿主做已验证结论。

## 6. 跨区与全局

### 6.1 通知、用量、命令与Caption

| 编号/组件 | Codex行为或形态→Fufucode规格 | G映射 | 视觉条目 | 证据/置信度与接线边界 |
|---|---|---|---|---|
| K08 命令面板 | 搜索可用动作，分组、快捷键、禁用原因；上下选择/Enter执行/Esc返回触发焦点。会话动作绑定当前会话，破坏性动作另有说明；与composer的斜杠命令入口区分 | 无 | U03/U06 | [C13,CX] 高（静态入口）/中（建议）；注册与按钮共用能力门控，不宣称全部命令可用 |
| K13 用量详情 | 会话累计/本轮/全部记录范围标签；未知与真0分开，已知小计注明不完整；数字可读，不在页头堆满计数 | 无 | U02/U05 | [A8] 高；不推造费用、额度、context占用 |
| K14 通知/需操作标记 | 页内对应任务标记、通知中心或OS提醒可回所属会话；结束、失败、待审批分别定义，焦点内局部反馈优先 | 无bell资产 | U05/U06 | [O1,A8] 中；OS投递未交付，见I09 |
| K15 Caption | 独立品牌与窗口控制，控制命中区和状态与内容层分开；不用品牌图充当窗口按钮 | `titlebar/{light,dark}/{control}-{state}`、`icon/fufucode.ico` | U01/U03 | [G] 高；G46×32资产与UniGetUI46×44布局不是同一规格 |

| 编号/场景 | 可见状态与动作 | 实施约束、来源/置信度 |
|---|---|---|
| I08 用量 | scope、统计时间/不完整标记、各分项；unknown显示“暂无完整数据”，小计注明已知部分 | live delta不能叠加；最终assistant计一次；所有记录含分支与当前分支分开。null≠0；partialTail不显示完整统计。[A8] 高 |
| I09 通知 | 后台完成/失败/待审批可提醒，点击返回所属会话；当前窗口局部反馈即可 | 完成只取agent_settled→TurnSettled的可靠终态，Unknown不发成功；sessionId+连接代次+本地run序号去重。OS投递/跨重启exactly-once尚未实现；无需每条工具消息toast。[A8,O1] 高（契约）/中（建议） |

I13 [CX，高（静态）/中（建议）]：当前Codex命令菜单注册CmdOrCtrl+K与CmdOrCtrl+Shift+P，Files为CmdOrCtrl+P，Review为Ctrl+Shift+G。Fufucode可沿用用户熟悉的入口，但先检查原生编辑器/终端焦点冲突，展示实际绑定，允许后续重绑定；快捷键触发必须与菜单使用同一命令和能力条件。

I14 [CX,O1，高（静态）/中（建议）]：通知偏好分完成策略（不提醒/失焦时/总是）与权限/问题开关；当前包有共享声音偏好。Fufucode先定义事件→类别→焦点策略→OS/页内反馈→归属路由，再接声音。静态设置存在不等于本机已播放；研究报告§5“声音设置未核实”缩小为实际声音/触发/OS投递仍未实测。

### 6.2 全局视觉令牌与UniGetUI约束

所有 token 由D集中映射到现有 `FluentTokens.axaml` / `FufucodeTheme.axaml`；优先复用语义键，不按每页硬编码一套颜色。以下为 Fufucode 建议值，精确参考证据与差异见附录 U/G；不把Codex暗色截图色值当本产品主题。

本表逐条证据与置信度：U01 `[U,G]` 高（语义色事实）/中（材质建议）；U02 `[U,C13]` 高（D字号现状与参考无全局字号）/中（中文排版建议）；U03 `[U,C13]` 高（控件状态/focus静态）/中（命中尺寸建议）；U04 `[U,C13]` 高（控件radius/padding）/中（网格/卡片建议）；U05 `[U,A8]` 高（语义色/unknown契约）/中（呈现建议）；U06 `[U,C13]` 高（焦点/浮层结构静态）/中（原生行为建议）；U07证据与置信度见该行。表中推荐值不宣称都来自UniGetUI。

| 编号 | 约束 | 初始规格 |
|---|---|---|
| U01 表面/材质 | UniGetUI中性Fluent表面、原生标题栏、层级轻分隔 | 明暗/跟随系统；背景、surface、hover、selected、border独立语义。透明材质仅可用平台启用，提供不透明回退；正文区不铺装饰渐变。 |
| U02 排版 | 中文与代码各有字号/字体，次要说明不能压成低可读小字 | UI14；正文14–15、行距约1.5；secondary12–13；section18–20；page28。Windows优先Segoe UI Variable/Segoe UI及中文系统回退；代码Cascadia Mono/Consolas 13–14。字体不打包第三方文件。 |
| U03 触达/状态 | pointerover/pressed/selected/disabled/focus分别表达 | 控件32–36、图标16/20，主要图标按钮命中32–36；禁用仍可解释。hover动作也在focus-within显示；选中使用轻填色/窄强调边，不与hover共用唯一状态。 |
| U04 间距/容器 | 4的刻度、单层容器、克制圆角 | 4/8/12/16/20/24/32；行内8、卡内12–16、组间24、页边20–24。控件radius4–6、卡8、浮层8–12、输入8–12；同层不重复边框，正文不逐段包卡。 |
| U05 色彩/语义 | 强调色服务主动作，状态文字+图标+颜色 | 常规border1 DIP；success/error/warning分别用于真实状态；diff加/删除颜色外保留+/−符号。未知是文字状态，不用绿色0。长正文对比由实际明暗/高对比验证。 |
| U06 浮层/焦点 | Fluent轻阴影、局部说明、原生键盘行为 | 普通卡不加阴影；popover/dialog用语义shadow，焦点描边可辨。Esc退出、焦点回trigger、Tab顺序跟视觉顺序；Tooltip不承载唯一关键说明。 |
| U07 动效 | 使用参考中有证据的短时反馈与原生导航 | opacity100ms、搜索下划线120ms；页面切换250ms/28DIP、Spline(0,0,0,1)。后两项D已有对应实现，新增面板动画需D按同语言选值，不能把统一200ms当参考事实。reduced motion即时切换，不做无限装饰动画。[U] 高（静态） |

#### 6.2.1 可直接映射的颜色与原生样式

以下是 UniGetUI `Styles.Common.axaml` 的静态值，D的 `FluentTokens.axaml` 已有对应体系 [U，高]；字体、统一spacing、所有卡片圆角并没有同等全局参考值，上一表对应项是本产品建议/现状。

| 语义 | Light | Dark |
|---|---|---|
| 主背景/次背景 | `#F3F3F3` / `#EEEEEE` | `#202020` / `#1C1C1C` |
| 其他层级表面 | `#F9F9F9` / `#FFFFFF` | `#282828` / `#2C2C2C` |
| 正文/次要/更弱/禁用文字 | `#E4000000` / `#9E000000` / `#72000000` / `#5C000000` | `#FFFFFFFF` / `#C5FFFFFF` / `#87FFFFFF` / `#5DFFFFFF` |
| 强调色 | 系统 `SystemAccentColor` 的Dark1/2变体 | 系统accent的Light2/Light1/base变体 |

U08 [U，高（静态）]：常规控件radius4、搜索框5；常规按钮/菜单border1；焦点环外2/radius6、内1/radius4。下拉button padding10,6、menu item8,4,8,5、menu surface0,4，是组件例值，不是统一页间距。D新卡片/输入区角半径按U04建议并标明产品扩展。

U09 [U，高（静态）]：参考明确toast shadow：Light `0 4 16 0 #33000000`、Dark `0 4 18 0 #80000000`；不要推广到所有卡片。D已有flyout/pill阴影属于Fufucode扩展，应由D确认视觉一致性。参考仅明确DataGridColumnHeader字号14，不能称全局字体全部14。

#### 6.2.2 超出视觉轴、需D明确取舍

V01 [S00,U,G，中/建议]：Codex连续深色大画布、强圆角输入容器和细窄hover操作可以学信息层级，最终颜色/角半径/原生焦点仍用Fluent。不要把截图配色整套引入。

V02 [S00,U，中/建议]：Codex左图标轨属于布局基准；UniGetUI NavigationView未要求同样多目的地。先保留真实全局入口，未来能力再扩图标轨，不复制空按钮。D可把部分全局入口合入侧栏，但需提交导航等价性与截图说明。

V03 [G,U，高/建议]：G四态图标已带底板，控件自行画hover背景时用常规单色源，避免双底板；`actions/expand/collapse`是会话窗扩大/缩小，不替代分组chevron。G空态按源比例，缩放到不抢输入焦点；`review/`不能入生产。

V04 [G,U，高（静态）]：现有D代码已接应用图标与conversation/extensions/interrupted图；error空态、actions四态、caption尚未见对应引用，是待接线素材而非已完成效果。D仍用Fluent PathIcon的动作可保留其统一风格；需要用G时逐个分配语义，不能把expand/collapse接到项目组折叠。caption46×32资产与参考46×44主标题栏的差异需专门决定，不能仅拉伸PNG。

### 6.3 跨区一致性与深挖挂接

研究报告§7.3统一命令注册/能力门控、§8.1 fork事务、§9.2自动化与目标引擎、§9.3通知策略、§12分层架构与功能依赖图归全局。任务调度/计划/自动化不是新视觉区域：入口可在左栏一般选项，执行过程仍回中央，文件成果进右栏。§6版本与静态/实测等级归证据附录；不得把本轮未实测能力写成五区运行通过。[C13,P,A8] 高（已记录静态/契约）/中（映射建议）。

### 6.4 Wave5 投放与验收

五区骨架是W1的必做前置：左栏/中央/右栏/底栏区域与独立设置导航必须先搭齐；Diff、PTY等运行服务尚未接线时呈现明确能力状态，不以“后续切片”省略整个区域。保留原W编号用于与上轮任务对照，下面“后续”仅指组件功能服务，不指宏观布局是否存在。

W1–W4与后续切片是根据前述L/K/U/I规范和A8能力安排的研究建议 `[P,A8,C13,CX,U,G]`，置信度中；各前置事实的置信度沿对应条目。验收状态由本产品任务链导出，具体用例是建议，不能当本轮已通过证据。

| 切片 | D可实施内容 | 前置/交A清单 | 必需审阅状态 |
|---|---|---|---|
| W1 布局与层级 | L01–L08、K01–K04、K06、U01–U07；沿现有页面与VM重排 | 分组/置顶/搜索若无元数据接口单独列需求 | 空会话、长标题、运行/失败/待审批、宽窄窗口、明暗 |
| W2 成果与用量 | K05/K13、I06/I08的只读列表和详情 | A8两范围/unknown契约可用；±行数、patch读取、范围快照与写操作另列 | Git不可用≠空列表；session范围标签；新/删/二进制/未知；partialTail、真0/unknown |
| W3 输入与恢复 | I01–I05、I11–I12及队列条/权限说明 | 消费既有投递/停止/审批能力；缺少队列修改/范围则列契约 | 中文IME、长草稿、排队、停止、中断、恢复失败、未知投递 |
| W4 通知 | 页内需操作状态与可靠终态订阅；OS接线另验 | A8为事件语义设计；OS服务、点击路由、焦点策略由D实现 | 双通知去重、失败/Unknown、失焦、点击归属、断线边界 |
| 后续 | Diff写操作、原生文件/PTY、fork、搜索/命令全量扩展 | 各自能力与元数据立项，先接口后按钮 | 实际操作及回退、dirty/外改、锁交接、独立滚动与焦点 |

验收采用真实页面/控件树/截图与真实已接线动作；mock用于构造展示状态需标明，不能替代真实Pi投递/终态。每个切片提供与S00同职责的对照、窄窗、明暗和中文长内容；至少检查100%/150%/200%缩放、高对比和键盘焦点。只做变化相关验证，不以冗余全仓回归替代页面审阅。本文未运行上述验收。

## 7. 附录：证据索引与未核实项

| 索引 | 来源与定位 | 类型/限制 |
|---|---|---|
| S00 | `C:\Projects\Fufucode\tmp\codex-atlas\00-full.png`，1568×984，2026-10-01图集；主研究实际查看 | 实测截图（协调者采集）；本轮未重抓。可见图标轨/侧栏/变更卡/composer，点击后行为未实测 |
| S01 | 同目录README、01/02/03裁图；01为主体裁图，02实际为报告分栏正文，03为另一处composer窄裁图 | 标签与内容不完全对应；不以文件名引证变更卡/侧栏实测尺寸。尺寸以00视觉与CSS静态分别表述 |
| C13 | `docs/research/codex-feature-study.md` §13.1–13.8；app-shared、split-view、thread-scroll-layout、user-message、local-conversation-turn、code-diff、settings-page及sidebar符号 | 已有包内静态研究；模块/偏移详见原报告，不当运行成功 |
| CX | 上轮子研究核实OpenAI.Codex **26.928.3736.0** 的 `app/resources/app.asar` | 当前静态证据；C13的26.928.2636.0路径是历史研究快照，旧路径现已不存在，不当当前包路径 |
| A8 | main工作区 `docs/design/cli-design.md` §Wave5契约支撑（起166行）与对应 `docs/progress.md` A8记录 | 项目契约/轨道实测记录；本轮只读，无重复产品验证 |
| P | `docs/runtime-baseline.md`、研究报告§7–13、`docs/AI_DEVELOPMENT_PLAN.md` | 固定Pi协议/项目范围；桌面UI需D独立验收 |
| U | main工作区 `docs/research/UNIGETUI_DESIGN_STUDY.md` 与只读 `C:\Projects\Fufucode\reference\UniGetUI` | UniGetUI源码审读，具体视觉锚点由子研究补充 |
| G | main工作区 `assets/manifest.md`，2026-09-30返工版，1018生产导出条目；`assets/exports.json` | 原创资产清单；本轮不声称已在App集成或用户验收 |
| O1 | [官方通知文档](https://learn.chatgpt.com/docs/notifications)，本轮实际打开；desktop通知设置有never/background/always和permission/question开关 | 文档声称；共用ChatGPT桌面文档，不等于当前Codex账号可达或本机OS投递验收 |
| O2 | [官方审批与安全文档](https://learn.chatgpt.com/docs/agent-approvals-security) | 文档声称；Codex安全模型不能直接套用Pi |
| ZP | `C:\Program Files\ZCode\resources\app.asar` 与exe版本元数据，详§7.2 | 包内静态/元数据；未启动应用 |
| ZO1 | [ZCode Task & File Management](https://zcode.z.ai/en/docs/task-management) | 官方文档声称任务/文件组织；本机UI未实测 |
| ZO3 | [ZCode Safety Confirmation](https://zcode.z.ai/en/docs/safety-confirm) | 官方文档声称审批流/范围；不作为Pi能力证明 |
| ZO4 | [ZCode ADE Tools](https://zcode.z.ai/en/docs/ADE-tools) | 官方文档声称终端/预览能力 |
| ZO5 | [ZCode Edit History](https://zcode.z.ai/en/docs/edit-history) | 官方文档声称安全撤销边界 |
| ZO6 | [ZCode changelog](https://zcode.z.ai/en/changelog) | 3.14.4条目发布日期2026-09-29；3.14.0记录审批/工具卡修复，发布日期不作能力首次引入日 |

未核实：完整新聊天hero、搜索结果页真实布局、通用本地审批卡真实视觉、所有功能门可达性、通知OS实际投递与回跳、变更撤销点击后的完整体验、ZCode实际运行与当前账号能力。规范给出后续实现建议，未核实项不以“通过”结案。

### 7.0 本轮新增证据与版本边界

| 索引 | 来源与范围 | 类别/置信度 |
|---|---|---|
| E4866 | Get-AppxPackage OpenAI.Codex：26.928.4866.0；实际ASAR `C:\Program Files\WindowsApps\OpenAI.Codex_26.928.4866.0_x64__2p2nqsd0c76g0\app\resources\app.asar`，547954491 bytes；内package `openai-codex-electron@26.928.40906` | 本轮现场元数据，高；3736路径已不存在，新证据只归4866 |
| O3 | [官方Commands](https://learn.chatgpt.com/docs/reference/commands)，Windows General及Settings deep links | 本轮实际打开，文档声称：bottom/terminal两个shortcut、设置入口，未实测本机触发 |
| O4 | [官方changelog](https://learn.chatgpt.com/docs/changelog)，2026-06-01 Terminal placement controls26.601 | 文档声称默认终端bottom/right；不当现机配置或当前界面实测 |
| O5 | [官方Projects and chats](https://learn.chatgpt.com/docs/projects)，Organize projects/chats | 文档声称导航管理；共用多表面内容不能混用 |
| O6 | [官方Developer settings](https://learn.chatgpt.com/docs/developer-settings)，Project and terminal behavior | 文档声称文件/终端打开位置配置；其后IDE专属设置不当Desktop证据 |

E4866补证定位：`webview/assets/thread-app-shell-chrome-8b68684a5e05.js` SHA256 `253fbee8bb2ec39544daff7a71f2897e3c71df4155435bb51920f149980bf0b4`；上述偏移为Python UTF-8解码后0基Unicode字符位置，非ASAR文件offset或源码行号。对应CSS `thread-app-shell-chrome-ecae05bb1b8f.css` SHA256 `db5fe7df0cfafef2ce8340c9cc86856bff9c4ecb45a47790dcd57763a0505439`；该CSS本身没有bottomPanel/高度声明，不能凭名字推断底栏高度。

### 7.1 UniGetUI/G精确锚点

- UniGetUI只读克隆提交 `9f55ba5f86e070bd74ce933b6c16e2c759fa545c`。`Styles.Common.axaml`：文字29–32/126–129，toast阴影175/362，radius377–380/409、border428–432，菜单617–630/648–667、焦点942、动效760/932。`MainWindow.axaml:198–201` 与 `EntrancePageTransition.cs:44`：250ms/28与Spline。
- D工作区 `src/Fufucode.App/Styles/FluentTokens.axaml`：颜色9–17/29–32/58–68/109–114/126–129/153–163；自加shadow104–105/198–199。`FufucodeTheme.axaml:108–173`：系统字体、14/12/20/28字号、控件radius4/overlay8。以上是静态源值，未声称屏幕最终computed效果。
- D `FadeSlidePageTransition.cs:13–22,56–57` 已有页面转场；`NavSelectionPill.cs:23,176–177`为300ms及前后缘不同Bezier，非Codex规格。`docs/archive/progress/progress-d.md:276–314`为Wave4记录；icon-only rail/hamburger与caption属于记录中的缺口，不因本文交付结案。
- G `assets/manifest.md:9–26,32–36` 列出完整族、四态/原生尺寸、透明背景与caption46×32约束；`assets/exports.json` 是准确文件清单。安装器图只供F使用。

### 7.2 ZCode静态材料锚点

ASAR内部路径不是普通磁盘文件；以下偏移为子研究报告的byte附近位置，不作源码行号。

- 根`package.json` SHA256 `ea547bac69013910c95b320069ecc5363f818a3f70e3a44e05f19cf222243dff`；exe SHA256 `2cd9f0672841f9a09d00aced4223114c3c7c37cfa8a308e7e79641d617497b0f`。
- `out/renderer/assets/src-Dovtcve-.js` SHA256 `44acd4da899ee58ad0d5f592cc95f30b159730ea530bd43d7fae64fc7b4cf4cd`；byte313029附近四行为模式，152146附近toolCall状态，154078附近PermissionRequest schema，208333附近流事件，97336附近toggleTerminal快捷键。模式名称仅作为证据定位，不进入产品文案。
- `out/renderer/assets/index-j49i-A1J.js` SHA256 `8ec85f38b952a36f7e7878aca23a864616851498b9daa6d3fb8d62dc621906f4`；byte约3500服务集合含gitService/gitCheckpointService/terminalService/task/session/agent服务，只证明注入边界。

### 7.3 上轮Codex基线3736锚点

ASAR实际路径 `C:\Program Files\WindowsApps\OpenAI.Codex_26.928.3736.0_x64__2p2nqsd0c76g0\app\resources\app.asar`，大小540844574 bytes；根package `openai-codex-electron@26.928.31416`。AppX版本与内部package版本分别记录，不混为一项。

| ASAR内部路径（均在webview/assets/） | 定位符号/证据范围 |
|---|---|
| `app-initial-3916b423772b.js` | openCommandMenu/searchFiles/openReviewTab及快捷键；commandMenuGroupKey/requiredAccess能力条件；turn-completed/includeEditedFiles编辑文件与成果路径。未定位live卡两按钮的确切处理器 |
| `composer-controls-18dddc0c9fa7.js` | 发送/听写标签、附件队列与上传状态，静态分支而非本机发送验收 |
| `local-conversation-page-32703d9d2371.js` | MainContentLayout/showComposer；布局slot关系 |
| `static-thread-transcript-c7b09aa52eae.js` | codex.sharedSnapshot.editedFiles/showMoreFiles；前三项/展开/inline diff，仅快照路径 |
| `permissions-mode-dropdown-fc41f25c1b25.js` | Full Access确认，静态模式选择流程 |
| `local-conversation-turn-dc4d2ba50bdd.js` | approvalItem/permissionRequestItems分开处理；不证明具体审批卡视觉 |
| `notifications-settings-2e52e83c8237.js` | 完成策略Never/Only when unfocused/Always、权限/问题开关和共享声音偏好；未触发OS通知 |

上述当前包证据补足入口/结构；旧C13的CSS精确值仍只作为已记录历史参考。本轮没有重新测当前computed style或执行live撤销，不把模块改名后的存在性当全页运行回归。

### 7.4 本轮新增静态证据定位（E4866）

下列文件均位于E4866 ASAR内的webview/assets/，本轮直接读取并计算SHA256；偏移统一为UTF-8解码后0基Unicode字符。静态高指源码结构可核对，不指运行/账号可达/视觉验收。压缩文件不提供虚构行号。

| 文件 | SHA256 | 挂接 |
|---|---|---|
| app-initial-9e0f03d3c485.js | 25fd85da4880d4911062c05ca9c094fb0d38f1651e57e6ac804de4cff3abc4da | 左栏组织/排序/动作/一般入口；右栏动作集合；底栏高度 |
| settings-page-8b619b95675d.js | a2addde116f6e57d0309f29a9a572f1473ae4e0e465ac425bcd2ca3dd550e149 | 设置分类过滤、路由、返回 |
| split-view-5fe910dc71c5.js | 30776f8965d1d7d82f8f475bd2d5633ba04975aa0eb9dc3ea8bfc438ead55479 | 通用宽度/断点默认参数，非当前调用实测 |
| terminal-tab-bacac6dd9db8.js | 38731d43b155e8d94a6b46649e8c7415143a43e541e6484702cc0fcb843cda54 | 终端session/多tab回调 |
| terminal-panel-8d873f4df63d.js | 46bef62d543838a7abaf935247602b4b6de73ba172d526a0145f70452bfe01e8 | xterm输入、fit/resize |
| queued-message-list-339997195d26.js | d87df4d85878b88b2c7c7a5e18b93f6f71245d4e0cc7aa74f1ddafb3dc9fe7c6 | 共用队列暂停/恢复、未确认提示与动作 |
| local-conversation-page-c957e56ff16a.js | 255b5413c87165d6b6520bca6cf4db163367e495a29de286109429238fba62c0 | 本地任务composer/恢复/布局slot |

指定3736基线沿§7.3保留，本轮没有其完整包重新核验；4866与3736不得混写。S00本轮再次实际查看，但不产生新截图；已有01–03文件名与内容错位问题沿用S01。所有需补图项目见progress-h末尾清单。
