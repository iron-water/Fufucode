# Fufucode 多 agent 可视化协作页面研究（轨道 I）

> 日期：2026-10-02。分支 track-i。研究性质：为右栏"协作可视化"页面产出**设计依据**，非实现授权。
> 取证纪律：每条参照物标注来源与置信度（[HIGH] 官方文档/README 明示；[MED] 官方博客/可信二手；[LOW] 推断；[实测] 本机截图；[未取证] 查不到）。过程与未取证项见 `docs/archive/progress/progress-i.md`。
> 关键前提（贯穿全文）：Fufucode 今天**没有**多 agent 编排、父子 agent 元数据、跨进程聚合事件流。凡涉及这些的形态一律标"契约需求"，不当作已有能力。

## 摘要（结论先行）

1. 参照物共同收敛出的"底线四件套"：**每个 agent 的标识 / 当前任务 / 状态（四态）/ 产出指针**；升级项是**主子层级、等待关系、人工介入队列**。
2. 最贴近 Fufucode 诉求的参照模型是 **Claude Code 的 `agent view`**（一屏行列表：名称/当前活动/时长 + 六态色 + Space peek 浮层 + Enter attach）与 **Codex 子智能体卡**（聚合计数 + 下钻线程）；二者都是"运行期观测"，不需编排后端即可成立。
3. 绝大多数成熟形态（Devin 多会话缩进树、LangGraph 图、AutoGen Studio 画布、CrewAI Traces、Agents SDK span 树）都建立在"有一个编排/事件/追踪后端"之上；Fufucode 当前只有单连接会话 + 进程内审批 + 只读变更/用量投影。
4. 因此**推荐 MVP = 右栏新增一个"协作"tab 的多会话状态仪表盘**（行列表 + 可选 peek），只消费已存在的 `ListSessions + TurnSettled + ChangeRangeQuery.SessionWrites + 挂起审批`；层级树与泳道时间线列为后置（依赖尚未存在的父子元数据与聚合事件流）；graph canvas 类"设计期编排"形态明确不采用。

---

## 步骤 1：本机参照物——Codex Desktop 子智能体卡

**证据 A（[实测]，置信度高）**：`C:\Projects\Fufucode\tmp\codex-atlas\00-full.png`（2026-10-01 后台 UIA/截图，窗口最小化态）。右上角"任务附属卡"三段：
- `变更`（Changes）——产出指针入口。
- `子智能体`——一排小状态图标 + 文本 `6 完成`（**聚合计数**，非逐个列表）。折叠态未显示单个子 agent 的名称/各自任务/层级/等待关系。
- `来源 · 1`（Sources）+ `+`——上下文引用（文件/网页），与 agent 无关。

**证据 B（[MED-HIGH]，官方文档 learn.chatgpt.com/docs/agent-configuration/subagents，H 报告 O8）**：Codex 并行 spawn 子 agent、root 会话聚合结果；ChatGPT Desktop/Web"surface 每个子 agent 线程供查看"并把摘要回灌主会话；IDE 扩展有"background-agent UI"钉在 composer 上方，可展开看状态、批量 halt、逐个打开线程；CLI `/agent` 查看/切换活动会话。→ 与图集"子智能体卡 + 完成计数 + 可点开线程"一致。

**证据 C（[未取证]）**：子智能体卡**展开态**的字段级枚举（每个 agent 显示哪些列）无截图、文档未给字段清单，未取证。

**信息维度小结（Codex）**：显示=子 agent 存在性 + 完成计数 + 可下钻到各自线程 + 产出（变更）+ 上下文来源；不显示（折叠态）=层级树、等待/依赖关系、跨 agent 时间线。

**实时再取证受限说明**：本轮 `cua-driver get_window_state` 命中 Codex 窗口（ChatGPT.exe pid 17932），但其处最小化态，后台 UIA 仅暴露 3 个非客户区按钮，Electron 内容树不可读；按纪律不抢前台，故未取新截图。以图集 + 官方文档为准。

---

## 步骤 2：参照物盘点

> 取证方式：官方文档 / GitHub README（不装不运行），分派 4 个后台研究 agent 各覆盖 2 项并逐条附来源 URL + 置信度；本文合并时保留其置信度标注。URL 为 agent 实际抓取来源。

### ① Claude Code — agent teams / subagents / agent view
来源：code.claude.com/docs/en/agent-teams、/sub-agents、/agents、/agent-view；anthropics/claude-code README。置信度 [HIGH]。
- **表面（均在终端内）**：lead 终端在输入框下方列 teammate 面板，或 split panes 每人一格、"可一次看所有人输出并点入某格直接交互"；subagent 面板行 + @typeahead 显状态；`claude agents` 打开 "agent view：一屏显示每个会话、其状态、以及哪些需要你输入"。
- **维度**：共享任务列表三态 pending/in-progress/completed（Ctrl+T 切换）；"运行中/失败/正在查看的 teammate 永远各占一行"，超过 3 个折叠为一行计数；层级 lead→subagent 在面板中以**树**呈现（主下约 3 层，第二层再 spawn 失败 [MED]）；产出=各 teammate transcript（Enter 打开），"只有最终结果回到 lead"；HITL 权限提示"统一经协调者接口路由"。
- **agent view 行**：名称 / 当前活动 / 时长；六色+glyph 状态（animating=working、yellow=blocked-on-you、dimmed=idle、green=success、red=error、grey=stopped）；Space 开 peek 浮层看截断输出/PR 链接/回复而不离开仪表盘，Enter attach 全转录。
- **布局/层级**：行列表 + 可选 split-pane 网格 + 嵌套树 + peek 浮层；一等交互式 TUI（非自由日志），"agent teams 实验性、默认关闭"；README 未提 subagents/teams（[HIGH] 就"缺失"而言）。

### ② Devin（Cognition）— 会话视图
来源：docs.devin.ai release-notes/2025.md、dynamic-workflows.md、security-swarm.md、cli/subagents.md、get-started/devin-intro.md、devin-session-tools.md；cognition.com/blog/devin-can-now-manage-devins。置信度 [HIGH]（blog [MED]）。
- **表面**：Web 会话侧栏 + Session Manager（按 PR 状态/用户/playbook 过滤）；Dynamic Workflows 面板"显示每个阶段、其 agents 及 live status，可从那里打开任一 agent 会话"；Security Swarm 左=按严重度分组的 findings 列表、右=选中详情、状态 tab 显 live 计数；CLI 子 agent 面板列全部 active+completed，含 profile/title/status/elapsed time/tool call count。
- **维度**：层级=批处理会话在侧栏**缩进挂到父会话下**（"理解 session hierarchy"），managed Devins 并行、各有 session 链接可 inspect/message、可 pause/stop、per-child compute 跟踪；状态=workflow live status、swarm findings Open/Reviewed/Dismissed，跨会话统一状态板 [未取证]；产出=每会话 Workspace tabs（terminal 看命令与输出日志 / 内嵌编辑器 / Browser-Computer tab）、可点 progress steps；HITL="Wait for my approval"、🟡/ 置信度门控审批，workflow "默认 auto-approved"。
- **布局/层级**：侧栏列表+缩进树、每会话 tab 分屏、面板内分阶段；无文档化泳道/时间线 [未取证]。一等 web GUI（CLI 有终端面板）。

### ③ AutoGen Studio（Microsoft）
来源：microsoft.github.io/autogen autogenstudio-user-guide（index/usage/installation/experimental）；github.com/microsoft/autogen README。置信度 [HIGH]。
- **表面**：真 web GUI（`autogenstudio ui --port 8081`），"no-code GUI for building multi-agent applications"；四区 Team Builder / Playground / Gallery / Deployment。
- **维度**：组件 teams/agents/tools/models/termination；层级=画布"一个 main team 节点 + 一组连接的 agent 节点"，从 Component Library 拖放、属性侧板、可切原始 JSON；输出流=agent 间 "Live message streaming"，Playground 可 "Monitor team inner monologue"、track tool/code 执行、review artifacts（图/代码/文本）、看性能指标（turn 数/token）；控制流="control transition graph"；HITL=UserProxyAgent 交互会话 + "pause or stop execution" 全运行控制。**显式 running/waiting/failed 状态机标签 [未取证]**；run history 列表 [未取证]；teams/runs 持久化到 `database.sqlite`。
- **布局/层级**：tab 分区 + **graph canvas（拖放节点图）** + chat 消息流 + 属性侧板 + JSON 视图。一等 GUI（自称 prototype、非生产）。

### ④ CrewAI
来源：docs.crewai.com flows/tasks/processes/tracing；docs-platform.crewai.com introduction/deploy-to-amp；github.com/crewAIInc/awesome-crewai。置信度 [HIGH]。
- **开源侧无 GUI**，官方唯一可视化=`flow.plot()` 生成 HTML 交互图（"nodes 代表 tasks、有向边指示执行流"，可 zoom/hover，CLI `crewai flow plot`）——**结构图非运行态**。顺序模型 sequential vs hierarchical（"managerial hierarchy / chain of command"）。任务字段 `human_input`（是否人工 review 最终答案）、`async_execution`，`TaskOutput.raw/json_dict/pydantic/summary/agent`；`@human_feedback` decorator；运行观测仅 prints + `flow.usage_metrics`，**状态机视图 [未取证]**。
- **商业平台（app.crewai.com）是一等 GUI**：Dashboard、Crew Studio（no-code 建 crew）、Crew Deployments、Observability；tab 面板 Status / Executions / Traces；Traces 展示 agent reasoning、"task 序列与依赖的可视化"、tool calls、prompts/responses、时长/token/cost、错误堆栈。
- **布局**：开源=静态 HTML graph；企业=tab 面板。amigoviz / 第三方 visualizer 官方 README/docs/awesome 均未收录 [未取证]。

### ⑤ LangGraph Studio（现更名 LangSmith Studio）
来源：docs.langchain.com/langsmith/studio、use-studio；oss/python/langgraph/interrupts、use-time-travel。旧 `langgraph/studio` URL 与 `langgraph-studio` repo 已 404。置信度 [HIGH]。
- **表面**：每 app 一张图 "Visualize your graph architecture"，Graph mode "暴露遍历的节点、中间状态"；并行工作以 **threads** 表达（右栏 thread 下拉/列表 + New Thread）。
- **维度**：任务=threads + 每 thread run history + Cancel；状态机=节点遍历 + interrupt 暂停点，**显式 running/waiting/done/failed 标签集 [未取证]**；层级=可折叠/展开 turn、node、state key（暗示嵌套 [MED]），subgraph 专档 [未取证]；输出=Submit 旁 streaming 开关、AI 消息下 retry；HITL="Click Interrupt" 选节点 before/after 暂停、thread log 点 Continue 恢复、interrupt schema 渲染为类型化输入字段；时间旅行=顶部 slider 拖拽、"Re-run from here"、编辑节点输出 Fork 从 checkpoint 分叉。
- **布局/层级**：上 graph canvas + 左下 Input 面板 + 右 thread/对话面板，Graph/Chat 模式 tab。一等 GUI（web IDE）。slider 之外的泳道/时间线 [未取证]。

### ⑥ Claude Squad（开源）
来源：smtg-ai/claude-squad README（raw）、repo、smtg-ai.github.io。置信度 [HIGH]。
- **表面**：TUI "manages multiple agents in separate workspaces"，一个终端窗管理实例与任务；用 tmux 为每 agent 建隔离终端会话 + git worktree 隔离代码库；支持 Claude Code/Codex/OpenCode/Amp。
- **维度**：会话列表=任务列表（↑↓/j k 导航）；生命周期 create `n/N`、kill `D`、pause `c`、resume `r`，**无自动 running/waiting/done 状态徽章 [未取证]**；父子层级 [未取证]；输出=attach 到所选会话看实时终端，`tab` 切 preview/diff（"发货前 review 工作"）；HITL=attach 重问，或后台无人值守（含 yolo/auto-accept）。
- **布局/层级**：会话列表格 + attach 格 + 底部命令菜单 + preview/diff tab；无卡片/泳道/graph。TUI（Go 写），无 GUI/web 层 [MED]。

### ⑦ tmux / 多窗格终端范式
来源：github.com/tmux/tmux tmux.1。置信度 [HIGH]。
- **表面**：三层嵌套容器 session > window > pane，每 pane 是独立 PTY 流；可 detach 后台续跑、reattach（屏幕只是视图）——即"agent 继续跑、视图可离开"。
- **维度**：**状态行（底部）=聚合索引而非内容**，显当前 session 信息 + 窗口列表（`status-justify`）；活动高亮 `monitor-activity/bell/silence` → 状态行高亮（`window-status-activity-style`）；每流标签=pane title（程序经 escape 设，`select-pane -T`）；状态布尔 `window_flags/activity_flag/bell_flag/silence_flag/active/zoomed_flag/panes/session_name/window_name`。
- **布局/层级**：窗格网格（平铺矩形）+ 横向状态行窗口列表 + `choose-tree` 交互树（过滤/打标）。TUI 键盘驱动，无 GUI。
- **借鉴价值**："每 agent 一条独立流 + 状态行做聚合索引 + 活动高亮" 是并行 agent 观测的经典心智模型。

### ⑧ OpenAI Swarm / Agents SDK + 其他
来源：github.com/openai/swarm README；openai.github.io/openai-agents-python/tracing；github.com/openai/openai-agents-python；code.claude.com/docs/en/agent-view；conductor.build/docs。
- **OpenAI Swarm**：README 自述 "(experimental, educational)" 多 agent 编排、"已被 OpenAI Agents SDK 取代"；零 UI/dashboard/handoff 图/agent graph [未取证]；handoff=普通 Python 函数返回。**code-only** [HIGH]。（更正：GitHub 报 `archived:false`，属弃用告示非归档。）
- **OpenAI Agents SDK**：库 code-only；可观测=**Traces dashboard**（"debug, visualize, monitor workflows"），Traces=单次端到端操作、由 Spans 组成、span 带 `parent_id` 指父 span → 嵌套 span 树（waterfall），托管 web GUI。handoff 仅文档为委派；专门 agent-graph/handoff 图 [未取证]。
- **Conductor（conductor.build）**：[MED] "run Claude Code/Codex/Cursor/OpenCode in parallel"，"每任务独立 workspace、branch、files、terminal、diff、review path"；首页 "see at a glance what they're working on, then review and merge"；具体布局（卡片 vs 列表）[未取证]；native Mac GUI。

---

## 步骤 3：信息架构归纳

### 3.1 底线四件套（"运行期观测"型产品几乎都有）
1. **标识**：name/id + 项目/来源（Claude agent view 行名、Devin session link、Codex 子线程、CrewAI task）。
2. **当前任务**：此刻在做什么（Claude "current activity"、Devin progress steps、Codex 线程）。
3. **状态四态**：运行 / 等待（含待人工）/ 完成 / 失败。普遍存在；Claude 六态（working/blocked/idle/success/error/stopped）可归并为四态 + idle/blocked；Fufucode 的 `TurnSettled`（Completed/Failed/Interrupted/Unknown/Idle）天然映射。**关键区分**："等待"要分 blocked-on-you（人工介入）与依赖等待（等别的 agent）。
4. **产出指针**：指向 diff/artifact/transcript/session（Codex 变更、Claude Squad diff tab、Devin Workspace tabs、Conductor diff/review、AutoGen artifacts、Agents SDK span）。

### 3.2 升级项（仅在有编排后端的产品出现）
- **主子层级树**：Claude subagent 树、Devin 缩进父子会话、Codex root→子线程、Agents SDK span `parent_id`、LangGraph 可折叠 node。
- **等待/依赖关系**：CrewAI Traces "task 序列与依赖可视化"、LangGraph 图边、AutoGen control transition graph。
- **人工介入队列**：Claude "需要你输入的"聚合高亮、Devin approval gates、LangGraph interrupt、AutoGen UserProxyAgent；且 HITL 常"经协调者路由"（Claude）。
- **聚合摘要/计数**：Codex "6 完成"、Claude 超 3 折叠计数行、tmux 状态行、Devin 状态 tab live 计数。

### 3.3 布局形态谱系（按成本排序）
| 形态 | 代表 | 需要的后端 | 适配 Fufucode |
|---|---|---|---|
| 行列表 | Claude agent view、Claude Squad、Devin 侧栏 | 会话元数据 + 状态 | **MVP 首选** |
| 卡片 | Codex 附属卡、Conductor（?） | 同上 | 次选 |
| peek 浮层 | Claude Space peek | 单会话详情读取 | 低成本高价值，可叠加 |
| 缩进树 | Devin 父子、Claude subagent | 父子关系数据 | 后置（缺 parent 列） |
| 分屏/窗格网格 | tmux、Claude split panes、Claude Squad attach | 每格一条实时流 | 重，后置 |
| Graph canvas | AutoGen Studio、LangGraph、CrewAI plot | 设计期拓扑 | **不采用**（类别错位） |
| 时间线/泳道/slider | LangGraph time-travel | 带 sequence 的事件流 | 后置（缺聚合事件流） |

### 3.4 三条关键洞察
1. **"运行期观测" ≠ "设计期编排"**：AutoGen/LangGraph/CrewAI 的 graph canvas 面向"搭建 agent 拓扑"，Fufucode 要的是"看多个 agent 正在跑什么"，属观测型（列表/树/时间线），别混。
2. **实时性都建立在持续事件/状态后端之上**（tmux server、Codex daemon、Devin server、Agents SDK tracing）。Fufucode 今天只有**进程内** `TurnSettled`、无跨进程订阅口 → 决定 MVP 只能覆盖本进程已 attach 的会话，其余必须诚实标"离线/未知"。
3. **人工介入是高频且最易被忽略的维度**：多数产品专门高亮"等你输入"，因为它是并行 agent 最大阻塞点。Fufucode 有 `PendingExtensionUiRequests`（限同进程），MVP 应显式呈现，而非只在中央对话里被动等待。

---

## 步骤 4：Fufucode 适配方案

### 4.0 落点：右栏 tab 化骨架（[HIGH]，frontend-design-guideline §3）

- 协作视图作为**右栏一个新 tab kind**（暂命名 `agents`/`协作`），复用既有 tab 身份约定：稳定 `panelId + projectId/sessionId + kind + 来源身份`，独立滚动/宽度状态，窄窗转抽屉不丢身份。
- 与中央联动：中央变更卡/会话头提供唤起入口（对应现有 K05/I06/I15 联动模式）。
- 现有可借范式：K09 Diff 成果区（范围→文件→定位）证明"右栏 tab 承载只读聚合视图 + 下钻"是成熟通路。

### 4.1 推荐形态（MVP）：多会话状态仪表盘（列表/卡片）

**形态**：右栏 `协作` tab 内一张**纵向卡片列表**，一行=一个活动会话/agent；顶部一行**聚合摘要条**。

```
┌ 右栏 · 协作 ────────────────────────────────┐
│  3 运行中 · 1 待操作 · 5 完成      [刷新]   │  ← 聚合摘要条
├─────────────────────────────────────────────┤
│ ● Track A core          运行中        ▸产出 │  ← 状态四态 + 产出指针
│   ├ 任务: 实现 changes_list 只读契约        │  ← 当前任务(最近 user 摘要)
│   └ 变更 11 · 审批 0 · 12m 前              │  ← 计数/时间
├─────────────────────────────────────────────┤
│ ⚠ Track H              待操作(审批) 2  ▸   │  ← 人工介入点(限同进程)
│   ├ 任务: D 页面接线验收                    │
│   └ 变更 3 · 需回应                          │
├─────────────────────────────────────────────┤
│ ✓ Track G              完成           ▸   │
│   └ 变更 0 · 无产出                          │
└─────────────────────────────────────────────┘
   点"▸产出"→ 打开该会话 Changes tab 并定位
   点行主体  → 中央切到该会话
```

**每行字段 → 数据来源（区分已有/契约需求）**：
- 标识（名称/ID/项目）：`DesktopMetadataStore.ListSessions`（[已有]）。
- 状态四态（运行/待操作/完成/失败）：`SessionEvent.TurnSettled`→Idle/Completed/Failed/Interrupted/Unknown（[已有，但仅进程内已订阅的活会话]）。**契约需求**：跨进程/后台会话的实时状态——现无跨进程订阅口，MVP 只能覆盖本 App 进程内已 attach 的会话，其余标"未知/离线"。
- 当前任务：会话最近一条 user prompt 摘要（[已有]，`sessions show`/JSONL 只读投影）。
- 产出指针：`ChangeRangeQuery.SessionWritesAsync` 计数 → 唤起 Changes tab（[已有]）。
- 人工介入：`IExtensionUiSession.PendingExtensionUiRequests`（[已有，限同进程]）。

### 4.2 备选 B：主子层级树（parent–child tree）

```
┌ 协作(树) ───────────────────────────┐
│  Root 会话 Track-I                 │
│   ├ ● 子A  运行中   ▸产出           │
│   ├ ⚠ 子B  待审批   ▸               │
│   └ ✓ 子C  完成     ▸               │
└─────────────────────────────────────┘
```
- 价值：还原 Codex/Devin 的"root 聚合子结果"心智。
- **契约需求（非已有）**：Pi `fork/clone/get_tree` 已返回 id/parentId/children/leafId（[HIGH]，cli-design P2-1 核实），但 **Desktop SQLite schema 2 sessions 无 parent 列、产品 fork 未实现**；需新元数据迁移 + 锁交接 + 原文件不变验证后才能落地。**后置**。

### 4.3 备选 C：泳道 / 时间线（swimlane timeline）

```
时间 →
TrackA │██████░░░░████│  (█运行 ░等待)
TrackH │   ██⚠██✓     │  ⚠=人工介入点
TrackG │      ██✓     │
```
- 价值：看清并行节律、等待/阻塞、谁先完成。
- **契约需求（非已有）**：需带 `sequence` 的**跨会话聚合事件流**（`ui serve`/`events stream` 仅设计、A7 首片不实现；TurnSettled 无回放/持久化/seq）。**后置**。

### 4.4 方案对比

| 方案 | 形态 | 依赖契约 | 今天可落地度 | 建议 |
|---|---|---|---|---|
| A 仪表盘 | 卡片列表+摘要条 | 仅已有（+跨进程状态受限） | 高 | **MVP** |
| B 层级树 | 树 | 父子元数据(缺) | 低 | 后置 |
| C 泳道 | 时间线 | 聚合事件流(缺) | 低 | 后置 |

### 4.5 MVP 验收条件（建议）

1. 右栏新增 `协作` tab，列出本项目**已登记会话**（来自 `ListSessions`），每行含标识/当前任务摘要/状态/产出计数。
2. 状态：本进程内已 attach 的会话用 `TurnSettled` 映射四态；其余显式标"离线/未知"，**不得**用最后一条文本或 `agent_end` 伪造完成（对齐 cli-design P1-2 硬规则）。
3. 产出指针点击→打开对应会话 Changes tab 并定位（复用 K09 通路）。
4. 人工介入：仅显示**本进程**挂起审批数，跨进程不承诺。
5. 空态/加载/失败三态分开；无会话时不显示假计数。
6. 不写产品代码即满足：本文件为设计依据，实现由协调者另行立项。

### 4.6 与 fork / 未来编排的边界

- A13⑤ fork、多 agent 编排属**未来 Core 能力**；本 MVP 只做"多会话状态仪表盘"，不声称具备编排、调度、结果聚合。
- 一旦父子元数据（备选 B 契约）与聚合事件流（备选 C 契约）落地，仪表盘可平滑升级为树/时间线，字段口径向后兼容。

---

## 步骤 5：不采用 / 后置清单

- **不采用（当前阶段）**：
  - 真·多 agent 编排 UI（新建/暂停/批量 halt 子 agent）——Fufucode 无编排后端，H 报告已判"不采用首版"，引入需资源/审批归属/结果聚合设计，不能靠多开 Pi 替代。
  - 后台 daemon 常驻执行视图——计划 §1 排除后台服务，进程生命周期即边界，不能承诺关窗续跑。
  - 图/画布式 agent 拓扑编辑（AutoGen Studio/LangGraph 式）——面向"设计期编排"，与 Fufucode"运行期观测"诉求错位，成本高收益低。
- **后置（依赖契约）**：
  - 主子层级树（需父子元数据迁移）。
  - 泳道/时间线（需带 sequence 的跨会话聚合事件流）。
  - 跨进程实时状态与审批（需跨进程订阅口）。
  - 等待/依赖关系可视化（需编排层暴露依赖）。
