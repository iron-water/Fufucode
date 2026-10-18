# R03 对话与图结合的节点控制台 —— 研究报告

遵守 [共同协议](../research-tasks/README.md)。本文件只交研究，不构成正式架构决定；由主 agent 核对、决定、实施与集成。

- 认领与边界：`claim.md`
- 证据与 SHA-256：`evidence.md`
- 基线：main `4d7c2a26cbdab744924dadb466990783ef2f7725`，Pi `@earendil-works/pi-coding-agent@1.0.0`（工作区实际安装与 HEAD 声明一致）
- 证据分级：**[源码事实]** 读 1.0.0 随包 `docs/` 与 `dist/` 得出；**[推断]** 由事实推导的设计判断；**[未证实]** 未运行、未验证。

> **本次未运行任何实验**：未启动 Pi、未调用模型、未构建、未跑测试。所有 Pi 行为结论均为 **[源码事实]**（读 1.0.0 随包源码与文档），不是实机实测。`docs/runtime-baseline.md` 中 0.87.1/0.99.2/1.0.0 的既有实测记录不被本报告覆盖或推翻。

---

## 1. 结论摘要

1. **Pi 只提供两类可图化的原生实体**：会话文件（JSONL，含 `parentSession` 血缘）与条目树（entry，`id`/`parentId`，`leafId` 标记当前位置）。**没有**"分支对象"、**没有**"agent 实例"、**没有**持久化"任务"、**没有**"上下文引用"。这四类只能由 Fufucode 作为投影承载。
2. **关键约束：`navigateTree` 不在 RPC 命令面内**。1.0.0 的 RPC 共 33 个命令（读 `dist/modes/rpc/rpc-mode.js` dispatch），树内导航只通过 `ExtensionCommandContextActions.navigateTree` 暴露给**扩展命令**。C# 客户端**无法**直接用 RPC 移动 leaf。同理 `context_edit`、`label` 两种条目类型也没有对应 RPC 命令 —— **历史消息不可通过 RPC 改写**。
3. 因此推荐 **只读历史图为最小范围**，且只开放**一个**写动作 `fork`（Pi 原生 + Core `ForkAsync` 已实现）。**任务 DAG 明确不做**：Pi 无任务实体，`AI_DEVELOPMENT_PLAN.md` §1 已把"多 Agent 编排"排除在首版之外。
4. `fork` 是**跨文件**操作（新建 JSONL，header 写 `parentSession`），并在**同一 Pi 进程**内把会话切换到新文件；`clone` 是 `fork(leafId, {position:"at"})`。fork 要求目标条目是 **user 消息**（默认 `position:"before"`），且当前会话文件**已落盘**。
5. **"显示上下文 ≠ 可以改写上下文"**：`get_entries` 返回压缩前与遗弃分支的原始条目；压缩只在重建上下文时替换，不删条目；`context_edit` 只改未来模型上下文，目标条目在原始历史、UI、导出、计费中一律不变。图可以**完整显示**历史，但除 fork 外不能改动它。
6. Core 已有大量可复用资产：`SessionHistory` 已区分当前分支 / 压缩前 / 遗弃分支；`ForkAsync` 已实现租约交接与父文件 SHA-256 校验；`DesktopMetadataStore` schema v3 已有 `parent_session_id`/`fork_entry_id`/`forked_at_utc`。**唯独 `get_tree` 从未被 Core 调用** —— 这是图页的第一个真实缺口。

---

## 2. 术语表

| 术语 | 定义 | 归属 |
|---|---|---|
| **会话 Session** | 一个 Pi JSONL 文件；header `{type:"session",version:3,id,cwd,parentSession?}` | Pi 原生实体 |
| **条目 Entry** | 会话文件内的一行，树节点 `{type,id,parentId,timestamp}` | Pi 原生实体 |
| **分支 Branch** | 同一文件内 `id`/`parentId` 的分叉路径；Pi 无独立分支对象 | Pi 原生**结构**，但"分支"作为命名实体是**前端投影** |
| **叶 leaf / leafId** | 条目树当前位置；`get_entries`/`get_tree` 均返回 | Pi 原生 |
| **父会话 parentSession** | 跨文件血缘，由 `fork`/`clone`/`new_session{parentSession}` 写入 header | Pi 原生（header 字段） |
| **压缩 Compaction** | `compaction` 条目：summary + `firstKeptEntryId` + `tokensBefore`；不删原条目 | Pi 原生 |
| **上下文编辑 context_edit** | 只改未来模型上下文的追加式条目；目标条目本身不变 | Pi 原生条目类型，**无 RPC 命令** |
| **分支摘要 branch_summary** | `/tree` 导航时为被离开路径生成的摘要，`parentId`=续接条目，`fromId`=原叶 | Pi 原生条目类型；触发它的导航**无 RPC 命令** |
| **agent 实例** | 一个 Pi 子进程 + 一个 Fufucode `PersistentSession` 的绑定 | **前端/本地投影**（Pi 无此概念） |
| **运行任务 Run** | 一次用户提交到终态的过程；可能跨多个 Pi `turn` | **前端投影 + 事件流**，Pi 无持久化任务实体 |
| **turn** | Pi 的一次 assistant 响应及其工具结果 | Pi 原生生命周期概念 |
| **上下文引用 Context ref** | 用户选取的文本/消息/文件/diff，送入旁路提问或另一会话 | **纯前端投影**（Pi 无此实体） |
| **图页 Graph page** | 三停靠区中的一个功能页，可视化上述节点 | Fufucode 功能页 |

---

## 3. 必答 1：六类节点的实体归属

| 节点类 | Pi 原生？ | 持久化位置 | 前端投影部分 | 判定依据 |
|---|---|---|---|---|
| **会话** | ✅ 原生 | `<session-dir>/--<path>--/<ts>_<id>.jsonl` | `sessions` 表行（id/name/project/parent_session_id/file_path） | [源码事实] `session-format.md` §File Location；`DesktopMetadataStore.cs:8` |
| **消息** | ✅ 原生（`message` entry） | 会话 JSONL | 渲染块、消息操作条、稳定定位点 | [源码事实] `session-format.md` §SessionMessageEntry |
| **分支** | ⚠️ **半原生** | 无独立存储，只是 entry 树的 `parentId` 分叉 + `leafId` | 分支命名、折叠分组、颜色、"当前分支"标签 | [源码事实] `session-format.md` §Tree Structure — Pi 只有条目树，无分支对象 |
| **agent 实例** | ❌ **非原生** | 无 | `PersistentSession` + Pi 子进程 + 租约（`SessionLease`） | [源码事实] 1.0.0 RPC 33 命令中无 spawn/agent 概念；Pi 为单会话进程模型 |
| **运行任务** | ❌ **非原生**（无持久实体） | 无；仅有运行期事件 | 由 `agent_start`/`agent_end`/`agent_settled`、`compaction_*`、`auto_retry_*` 合成 | [源码事实] `docs/json.md` §Agent and turn events；`AI_DEVELOPMENT_PLAN.md` §6.4 —— 桌面一轮任务可含多个 Pi turn |
| **上下文引用** | ❌ **非原生** | 无 | 完全由 Fufucode 承载（选区、消息 ID、文件路径、diff 范围） | [源码事实] 1.0.0 无相关 RPC/条目类型 |

**由此得到的第一条设计纪律**：图上只有**会话**与**消息/条目**两类节点有 Pi 端的稳定 ID，可以安全持久化引用；分支、agent 实例、运行任务、上下文引用四类节点的 ID 必须由 Fufucode 自己定义并自己保证稳定，且不得写回 Pi 会话文件。

---

## 4. 必答 2：fork / tree 导航 / 压缩 / 消息历史 / 可编辑边界（Pi 1.0.0 证据）

### 4.1 fork（[源码事实] `dist/core/agent-session-runtime.js:174-235`）

```
fork(entryId, options)
  position = options?.position ?? "before"
  1. emit session_before_fork(entryId,{position})  → 扩展可取消
  2. selectedEntry = getEntry(entryId)  不存在 → throw "Invalid entry ID for forking"
  3. position === "at"   → targetLeafId = entryId
     否则("before")      → 必须 type==="message" 且 role==="user"，否则 throw
                            targetLeafId = entry.parentId
  4. 要求当前会话已落盘 → 否则 "This session has not been saved yet..."
  5. createBranchedSession(targetLeafId)  → **新的 JSONL 文件**，header 写 parentSession
  6. teardownCurrent("fork") + createRuntime(session_start:{reason:"fork"})
     → **同一个 Pi 进程**改持新会话
```

推论（与产品直接相关）：

- **fork 是跨文件操作**，不是"在同一棵树里切分支"。父文件**不再**包含新分支；新分支在**新文件**里。图上"分支"因此有两种完全不同的边：**文件内** `parentId` 边，与**跨文件** `parentSession` 边。
- **fork 后同一进程切换到新会话** —— Fufucode `ForkAsync` 的注释与实现已印证："Pi keeps the same process; only the managed session identity, lease and metadata move."（`PersistentSessionManager.cs:143-146`）
- **fork 目标必须是 user 消息**（默认 `position:"before"`）。想在 assistant 节点后分叉，只能用 `clone`（`position:"at"`，无 role 限制，`rpc-mode.js:488-497`）。
- **响应陷阱**：取消时 `success` 仍为 `true`，`cancelled` 在 `data` 里（`rpc-commands.md:628-636`）。Core 已正确处理（`PersistentSessionManager.cs:160-162`）。
- **`get_fork_messages` 与文档口径不一致 [源码事实]**：文档说取"active branch 上的 user 消息"，但实现 `getUserMessagesForForking()`（`dist/core/agent-session.js:3325-3339`）遍历 `getEntries()` **全部条目，不过滤当前分支**，因此会返回被遗弃分支上的用户消息。产品若直接把它当"可 fork 列表"，会给出不在当前路径上的候选。**建议自行用 `get_entries` + `leafId` 过滤**，见 §5.3。

### 4.2 tree / 导航（[源码事实]，本任务最重要的发现）

| 能力 | RPC 命令 | 说明 |
|---|---|---|
| 读整棵树 | ✅ `get_tree` | 返回 `tree[]`（节点 `{entry, children, label?, labelTimestamp?}`）+ `leafId`；**数组**，因为导航可产生多根，断链孤儿也会成为根 |
| 读追加序条目 | ✅ `get_entries`（支持 `since` 游标） | 覆盖压缩前与遗弃分支；`since` 不匹配任何 id 时返回 `success:false` |
| **在文件内移动 leaf** | ❌ **无 RPC 命令** | `AgentSession.navigateTree()` 存在（`dist/core/agent-session.d.ts:811-831`），但**不在** `RpcCommand` 联合类型中，`docs/` 全文也未记载 |
| `navigateTree` 的实际出口 | 扩展命令 | 仅通过 `ExtensionCommandContextActions.navigateTree`（`dist/core/extensions/types.d.ts:1653`）暴露，RPC 模式下由 `rebindSession` 注入给扩展 |
| 写 `context_edit` / `label` | ❌ **无 RPC 命令** | 条目类型存在，SDK `SessionManager` 有 `branchWithSummary`/`resetLeaf`（`session-manager.d.ts:353,359`），但 RPC 面无入口 |

**结论**：桌面端当前**只能读树，不能导航树，也不能编辑条目**。可选路径三条：

- **路径 A（推荐，零 Pi 改动）**：图页只读；"去到某个节点"一律翻译为 `fork` → 新会话（新文件），或把该节点文本放进输入框草稿（纯前端，不动 Pi）。
- **路径 B（需扩展）**：在 `extensions/fufucode-bridge/` 里注册一个扩展命令，内部调用 `ctx.navigateTree(targetId, {summarize})`。这会引入**分支摘要的 LLM 调用**与"离开当前位置"的语义，风险高于 A。**需单独授权与验证**。
- **路径 C（不可行）**：假装 RPC 有 navigate 命令。已排除。

### 4.3 上下文压缩（[源码事实] `docs/compaction.md`、`session-format.md` §CompactionEntry）

- 压缩**追加**一个 `compaction` 条目（summary + `firstKeptEntryId` + `tokensBefore`），**不删除**任何原始条目。
- 重建上下文时：从 leaf 回溯到根；路径上若有多个 compaction，取**最新**一个；`firstKeptEntryId` 之后到 compaction 之前的非 system 条目保留；compaction 之后的条目全部保留。
- 所以 `get_entries` 返回的**原始历史永远完整**，压缩只影响"模型看到什么"。Core `SessionHistory.Rebuild()` 已正确重建 `firstKeptIndex` 并标记 `BeforeCompaction`（`SessionHistory.cs:67-86`）。
- 压缩后 `contextUsage.tokens/percent` 为 `null`，直到下一次 assistant 响应给出有效 usage（`rpc-commands.md:567`）—— 图上显示占用率时必须允许"未知"，不能显示 0。

### 4.4 消息历史与可编辑边界

| 想做的事 | Pi 是否支持 | 通过什么 | 边界 |
|---|---|---|---|
| 看压缩前与遗弃分支的原始消息 | ✅ | `get_entries` / `get_tree` | 只读 |
| 看模型当前实际看到的消息 | ✅ | `get_messages` | 只是当前模型对话状态，不代表全历史 |
| 改写一条历史消息 | ❌ | 无 RPC | `context_edit` 无 RPC 入口 |
| 让某条消息不再进入模型上下文 | ❌ | 无 RPC | 同上 |
| 从某点重开 | ✅ | `fork`（新文件）/ 前端草稿 | 不改原历史 |
| 给节点打标签 | ❌ | 无 RPC（`label` 条目可写但无命令） | 只能存 Fufucode 本地库 |

**核心纪律（对应任务书"不把上下文显示等同于任意改写"）**：图上把一条消息画出来、展开它的原文、甚至显示"它当前不在模型上下文里"，都**不等于**可以编辑它或改变它对模型的贡献。UI 上任何"编辑/删除/忽略这条"的入口，在当前 Pi 版本下**没有真实实现**，不得先画按钮后补能力（与 `frontend-functional-plan.md` D-06 "能力未完成时不显示伪可用按钮" 一致）。

---

## 5. 必答 3：三条路线比较与推荐

| 维度 | A. 只读历史图 | B. 可操作会话图 | C. 任务 DAG |
|---|---|---|---|
| 节点 | 会话 + 条目（含遗弃分支、压缩条目） | A + agent 实例 / 运行任务节点 | 任务节点 + 依赖边 |
| 写操作 | 仅 `fork`（→ 新会话） | fork / switch / 导航 / 编辑上下文 / 跨会话发送 | 编排：批量启停、依赖调度 |
| Pi 侧依赖 | `get_tree` + `get_entries`（**已具备**） | 需 `navigateTree`（**无 RPC**）+ `context_edit`（**无 RPC**）+ 扩展桥 | 需任务实体与编排器（**Pi 完全没有**） |
| Core 侧缺口 | 仅缺 `get_tree` 调用（很小） | 中：会话切换/租约/进程归属/副作用提示 | 大：新实体、调度、审批归属、结果聚合 |
| 与已定计划冲突 | 无 | 部分（需新增扩展能力，属 Pi 扩展变更） | **直接冲突**：`AI_DEVELOPMENT_PLAN.md` §1 首版不含多 Agent 编排；`multiagent-collab-mvp-spec.md` §8 已把编排列为"不采用/后置" |
| 真实性风险 | 低（只读，画不出来的不画） | 高（容易画出无法兑现的编辑/导航入口） | 极高（无实体支撑，必然产生假状态） |
| 交付价值 | 看得见历史全貌、定位来源、安全分叉 | 更高，但依赖未验证能力 | 当前不可信 |

### 推荐：**A（只读历史图）为最小范围，且只附带唯一写动作 `fork`**

理由：

1. **能力闭环**：`get_tree`/`get_entries` 在 1.0.0 已可用且 Core 已验证过 `get_entries`；`fork` 的 RPC 与 Core `ForkAsync` 都已实现并有租约交接与父文件校验。A 路线**不依赖任何未验证的 Pi 能力**。
2. **风险最低**：只读图不会产生不可逆副作用，最坏情况只是显示不准确；而 B 的导航会改变 Pi 会话当前位置并可能触发 LLM 分支摘要，C 需要凭空造实体。
3. **与既有决策一致**：`multiagent-collab-mvp-spec.md` §8 已明确"不采用 graph canvas（设计期编排）"与"多会话并发编排"。
4. **可增量扩张**：A 落地后，若确需树内导航，再按 §4.2 路径 B 单独立项评估。

**反对采用 B 全量的理由**：`navigateTree` 无 RPC 入口，走扩展桥意味着把"改变会话当前位置"这种**强副作用**操作放进一个未在本版本实测过的通道；且 `summarize:true` 会触发 LLM 调用产生费用与延迟，与"用户只是想看看"的意图不匹配。

**反对采用 C 的理由**：Pi 1.0.0 不存在任务实体与编排语义；任何 DAG 节点都只能由前端虚构，必然出现"图上显示运行中、实际无对应 Pi 状态"的假状态，直接违反 `AI_DEVELOPMENT_PLAN.md` §13"不靠假数据冒充真实 Pi"。

---

## 6. 必答 4：四个操作流程（全部需要显式用户授权）

统一前置规则：**跨会话发送默认关闭**。任何把内容送进另一个会话/agent 的动作，必须先弹出"目标 + 内容 + 来源"三要素确认，且默认按钮为"取消"。

### 流程 1：选节点查看来源（只读，无授权门槛）

```
点击图上节点
  → 右/底详情面板显示：
      entry.id / type / timestamp
      role（user/assistant/toolResult/custom/system）
      原文（消息内容、工具名与参数、压缩摘要文本）
      三个布尔状态：在当前路径上？ 压缩前？ 属于遗弃分支？
      来源指针：所属会话文件、所属项目
  → 按钮：「在对话中定位」（跳主时间线同一消息 ID）、「复制原文」
  → 不提供：编辑、删除、忽略、导航到此处
```

数据源：`get_tree`（结构）+ `get_entries`（原文）+ `SessionHistory` 已算出的 `OnCurrentBranch`/`BeforeCompaction`。**不需要新 RPC**。

### 流程 2：fork 到草稿（唯一写操作，需授权）

拆成两个**命名不同**的动作，避免用户混淆：

| 动作 | 是否动 Pi | 结果 |
|---|---|---|
| **复制到输入框**（默认，零副作用） | 否 | 把该 user 消息文本放进当前输入框草稿，不发送（对齐 I-03 草稿按会话保存） |
| **从此处分叉**（显式按钮，需确认） | 是，发 `fork` | 新会话文件 + 新 SessionRecord（`parent_session_id` 已自动填）；分叉出的会话**进入草稿态，不自动发送任何 prompt** |

确认对话框必须写明：**目标条目文本（截断+可展开）**、**将创建新会话文件**、**原会话不会被修改**、**当前 Pi 进程会切换到新会话**。

约束（来自 §4.1）：仅 user 消息可"从此处分叉"；会话未落盘时禁用并提示"先发送一条消息"；`Activity != Idle` 时禁用（Core 已强制，`PersistentSessionManager.cs:152-153`）。

### 流程 3：比较分支（只读）

```
选中两个节点（或一个节点 vs 当前 leaf）
  → 计算各自 leaf→root 路径，求最近公共祖先（LCA）
  → 显示：共同前缀（灰）、仅左路径（蓝）、仅右路径（橙）
  → 摘要：各自条目数、user 消息数、工具调用数、是否跨越压缩点
  → 不提供自动合并；不提供"把 A 的内容搬到 B"
```

纯前端计算，基于 `get_tree` 的 `id/parentId`。**跨文件比较**（两个不同会话文件）需要按 `parentSession` 血缘求共同祖先文件，实现成本更高，**列为后置**。

### 流程 4：将引用送入另一个 agent（默认关闭，必须授权）

```
选中文本/消息/文件/diff → 「发送到…」
  → 选择目标：当前项目的其他已登记会话（ListSessions）
  → 预览面板三要素：
       目标：会话名 + ID + 项目
       内容：将发送的文本/附件（可展开全文）
       来源：来自哪个会话的哪个 entry ID
  → 默认按钮 = 取消；确认后作为**草稿**写入目标会话的输入框
  → **不自动发送 prompt**，不自动触发任何模型调用
  → 目标会话若处于运行态：禁用并说明原因（避免与 steer/follow_up 语义混淆）
```

信息泄露边界：预览必须显示**完整内容**而非截断摘要（否则用户无法判断泄露范围）；跨项目发送默认禁止；被引用的消息若来自含敏感路径/凭据的会话，预览中不做自动脱敏但给出"含工具输出/命令结果"标记。

---

## 7. 必答 5：边语义、状态、不变性与边界

### 7.1 边的两种含义（必须视觉区分）

| 边类型 | 含义 | 存储位置 | 是否可跨文件 |
|---|---|---|---|
| **parentId 边（文件内）** | 条目树的父子关系，append-only | 会话 JSONL 内 | 否 |
| **parentSession 边（跨文件）** | fork/clone/new_session 产生的会话血缘 | 新文件 header + Fufucode `sessions.parent_session_id` | 是 |

另有两类**投影边**（Fufucode 自定义，不来自 Pi）：
- **引用边**：上下文引用从 A 会话送到 B 会话（本地库记录，默认不产生）。
- **运行归属边**：某次运行任务属于某 agent 实例（进程内，重启后失效，不得持久化声称）。

### 7.2 节点/会话运行状态

采用 `AI_DEVELOPMENT_PLAN.md` §7.1 的分层，不要压成一个 `IsRunning`：

| 层 | 取值 | 图上表现 |
|---|---|---|
| 连接 | Stopped / Starting / Connected / Faulted / Stopping | 会话节点边框 |
| 活动 | Idle / Running / Compacting / Retrying / Cancelling | 节点徽标 |
| 用户交互 | 待审批数量（可叠加） | 角标数字 |
| 最近任务结果 | Completed / Failed / Interrupted / **Unknown** | 节点底色或图标 |

**强制纪律**：终态以 `TurnSettled` / `agent_settled` 为准，**不得**用消息文本结尾或工具结果成功回灌判断完成（`multiagent-collab-mvp-spec.md` §4 已确立）。离线/未 attach 的会话显示"离线·未知"，**不得**显示其最后已知状态冒充实时。

### 7.3 父节点不变性

- **文件内**：`parentId` 一旦写入永不改变；条目 append-only，只读消费者不会破坏它。图上不得提供任何"改父节点"的入口。
- **跨文件**：`parentSession` 由 fork 一次性写入 header，之后不变。
- **Fufucode 侧**：`sessions.parent_session_id` 由 `CreateForkSession` 写入（`DesktopMetadataStore.cs:144-164`），父行保留、父文件不改；`ForkAsync` 已用 SHA-256 前后比对给出 `ParentFileUnchanged` 证据（`PersistentSessionManager.cs:156,170`）。
- **展示纪律**：`ParentFileUnchanged` 是**证据**而非 Pi 的保证（`SessionForkResult` 注释已声明）。UI 若展示"父会话未改动"，必须是这条实测证据的结果，不能当作通用承诺。

### 7.4 工具副作用提示

- 图上工具调用节点必须标注**是否可能有外部副作用**（write/bash 类 vs read 类）。
- **"从此处分叉并重跑"是危险动作**：分叉点之后若已执行过 write/bash，重放会产生第二次副作用。因此 §6 流程 2 明确**分叉后不自动发送**；若要提供"分叉并重发"，必须先列出分叉点之后发生过的工具调用清单并经用户逐项确认。
- 对齐 `frontend-functional-plan.md` A-03："已执行工具有副作用时不能盲目重放；建议从请求前创建分支。"

### 7.5 失败 / 取消 / 恢复

| 情形 | 行为 |
|---|---|
| fork 被扩展取消（`data.cancelled=true`，`success` 仍 true） | 图页回到原会话，提示"分叉被扩展取消"，**不**报错为 RPC 失败 |
| fork 目标非法（非 user 消息 / 会话未落盘 / 条目不存在） | RPC `success:false` + error 字符串；按钮前置禁用 + 禁用原因 |
| fork 进行中进程退出 | Core 必须结束等待中的请求并给出可解释错误（`AI_DEVELOPMENT_PLAN.md` §6.1）；已创建但未知归属的子会话须清理（`ForkAsync` 已有 catch → 释放租约 + 删除子行） |
| 刷新图时 Pi 正在压缩 | 显示"压缩中"，并在 `compaction_end` 后重取；压缩期间 `contextUsage` 为 null，显示未知 |
| 恢复 | 重连后先重建状态再接收新工作；图页在恢复完成前只读且标注"恢复中" |

### 7.6 权限与信息泄露边界

1. **图页不获得任何 Pi 权限**：它只是 `get_tree`/`get_entries` 的只读消费者，与浏览器页同类（`frontend-functional-plan.md` B-06："页面访问不等于 agent 已能读取/控制页面"）。
2. **跨会话发送默认关闭**，必须三要素确认，默认按钮取消（§6 流程 4）。
3. **导出/分享边界**：Pi 的 `export_html` 与 `/share` 会包含 prompts、模型响应、**工具参数、命令输出、文件内容、扩展消息**（`docs/sessions.md` §Export or share）。图页若提供"导出此分支"，必须显式提示这一点，且默认本地路径。
4. **跨项目隔离**：图页 scope 限定当前项目；跨项目引用默认禁止。
5. **本地库不是第二份权威历史**：`AI_DEVELOPMENT_PLAN.md` §7.2 明确桌面库只存路径/项目/排序/草稿等元数据。图页的折叠状态、标签、视图偏好存在本地库；**条目内容一律以 Pi 会话文件为准**，不得在本地库缓存一份并当作权威。
6. **会话文件所有权**：同一会话文件不能同时交给多个可写进程（`AI_DEVELOPMENT_PLAN.md` §7.3）。图页的"在新窗口打开这个分支"必须走租约检查。

---

## 8. 必答 6：可停靠功能页与布局保存

### 8.1 定位

- 图是**三通用停靠区（左/右/底）中的一个功能页**，遵循统一宿主协议：稳定页面 ID、标题/图标、页面实例 ID、所属项目/会话、可移动性、关闭前处理、可持久化状态（`frontend-functional-plan.md` §3）。
- **对话仍是主入口**：中央主工作面保持会话头 + 时间线 + 输入框，图页不承载发送。图页的"在对话中定位"是单向跳转（图 → 对话），对话不嵌入图。
- 建议默认放在**右停靠区**（与变更/预览/浏览器/侧栏提问同类，属"检视"性质），底部或左侧由用户自行移动（L-02）。

### 8.2 与窗口映射的关系

| 维度 | 处理 |
|---|---|
| 页面实例 | 建议**单实例**（与概览、文件树同类，L-07）；不做每个会话一个图实例 |
| scope | 当前项目 + 当前会话；切会话时图页**默认跟随**当前会话 |
| **锁定** | 提供"锁定到某会话"开关：锁定后切会话不跟随（用户在比较分支时不被打断）。锁定状态属于可持久化页面状态 |
| 跨区移动 | 移动到左/右/底不得重建数据或丢失展开/选中状态（L-01） |
| 关闭语义 | 关闭图页不结束任何任务、不影响 Pi；收起区域同样不结束（L-08） |
| 与窗口（多窗口） | 本任务不定义多窗口；图页是**页**不是**窗口**。若 R02（Windows 多窗口）后续允许独立窗口，图页作为可停靠页可被移入，但**不做**"每个窗口一张独立图"的假设 —— 该问题留给 R02 与主 agent |

### 8.3 布局保存

- 图页在布局中的位置、尺寸、停靠区、区内排序由**统一布局存储**负责（L-05：应用级默认 + 项目级覆盖 + 恢复默认入口），图页**不自建**布局存储。
- 图页自身的视图状态（展开层级、选中节点、锁定会话）作为**页面状态**随宿主持久化；已移除的页面从布局中跳过并给出可操作提示（L-09）。
- 窄窗口下不强行显示三栏（L-06）；图页在窄宽度下降级为**纵向列表**而非画布，避免节点溢出与滚动失控。

---

## 9. 实体关系与状态图（低保真）

### 9.1 两层结构：文件内条目树 + 跨文件血缘

```text
会话文件 A (session.jsonl, id=S1)
  header: {type:session, version:3, id:S1, cwd:..., parentSession:null}
  ┌─ e1 user ── e2 assistant ── e3 toolResult ── e4 user ── e5 assistant ─┬─ e6 user   ← leafId
  │                                                                       │
  │                                                                       └─ e7 user ── e8 assistant   (遗弃分支)
  └─ e9 compaction (firstKeptEntryId=e4)      ← 压缩条目，在路径上

会话文件 B (由 fork(e6) 产生)
  header: {..., id:S2, parentSession:".../A.jsonl"}
  └─ 复制 A 的根→e6 之前的路径，leaf 停在 e6 之前

Fufucode sessions 表:
  S1 (parent_session_id=null)
  S2 (parent_session_id=S1, fork_entry_id=e6, forked_at_utc=...)
```

图上的边：
```text
文件内:  e1 ──parentId──> e2 ──> e3 ──> e4 ──> e5 ──> e6
跨文件:  S1 ══parentSession══> S2        (视觉上必须与 parentId 边区分)
投影:    S1 ····引用····> S3             (用户授权后才产生，本地库记录)
```

### 9.2 节点状态机（运行态部分，前端投影）

```text
                  ┌──────────┐
                  │  离线/未知 │  ← 未 attach 或元数据缺失，不得冒充实时
                  └──────────┘
                        ▲ 重连失败 / 未 attach
   Idle ──提交──> Running ──工具待审批──> 待审批(Running 叠加)
     ▲                 │  │
     │                 │  └──压缩──> Compacting ──> Running
     │                 └──错误──> Retrying ──> Running | Failed
     │                 
     └── agent_settled / TurnSettled ──> Completed | Failed | Interrupted | Unknown
   用户取消 ──> Cancelling ──(abort 完成)──> Interrupted
```

纪律：Unknown 必须存在且可见；**不得**用文本结尾或工具成功结果推断 Completed。

### 9.3 低保真页面线框（右停靠区）

```text
┌─ 图 ────────────────────────────────────────────┐
│ [会话: 重构 auth] [锁定] [刷新]            [⋯]  │
│ ─────────────────────────────────────────────── │
│  ● e1 你: 读一下 login.ts                       │
│  └ ○ e2 助手 · ● e3 工具 read                    │
│     └ ● e4 你: 改成 OAuth          ← 当前路径    │
│        ├ ○ e5 助手 · ● e6 工具 edit  ⚠ 有写副作用 │
│        │  └ ● e7 你: 再加重试       ← leaf       │
│        └ ✕ e8 你: 换成 JWT          (遗弃分支)   │
│  ▣ 压缩点 (firstKept=e4)                        │
│  ══ 分叉 ──> 会话 S2 (fork @ e7)                 │
│ ─────────────────────────────────────────────── │
│ 选中: e6 工具 edit                              │
│  类型 toolResult · 有副作用 · 在当前路径          │
│  [在对话中定位] [复制原文]                        │
│  从此处分叉: (仅 user 消息可用，此处禁用)          │
└─────────────────────────────────────────────────┘
```

---

## 10. 三条可验收用户流程

### UF-1 查看来源（只读，零副作用）
1. 打开含压缩点与遗弃分支的历史会话，右停靠区切到「图」。
2. 图上同时显示当前路径、遗弃分支、压缩条目，且三者视觉可区分。
3. 点击遗弃分支上的一个 user 节点 → 详情显示原文 + "不在当前路径" 标记。
4. 点「在对话中定位」→ 中央时间线滚到同一消息 ID 并高亮。
5. **验收**：全流程未发出任何写 RPC；`get_tree`/`get_entries` 调用可审计；遗弃分支消息内容完整可见（证明压缩未删历史）。

### UF-2 从历史点分叉到草稿（写操作，需授权）
1. 当前会话 Idle，且已有落盘文件。
2. 选中一个 **user** 节点 → 「从此处分叉」。
3. 确认框显示：该用户消息全文（可展开）、"将创建新会话文件"、"原会话不会被修改"、"当前 Pi 进程将切换到新会话"。
4. 确认 → 新会话出现在概览，`sessions.parent_session_id` 指向父、`fork_entry_id` = 所选 entry。
5. 新会话**输入框内有分叉点文本作为草稿但未发送**；父会话文件 SHA-256 前后一致（`ParentFileUnchanged=true`）。
6. 选中 assistant 或 toolResult 节点时，「从此处分叉」**禁用并显示原因**。
7. **验收**：父文件未被修改；子会话可独立继续；运行态会话上该按钮禁用。

### UF-3 比较两个分支（只读）
1. 选中遗弃分支的 e8 与当前 leaf e7。
2. 面板显示最近公共祖先 e4，共同前缀置灰，两侧差异分色。
3. 显示各自条目数 / user 消息数 / 是否跨越压缩点。
4. **验收**：不提供合并按钮；跨文件（不同会话）比较显示为"暂不支持"而非给出错误结果。

---

## 11. 最小实施方案（可独立提交的小增量）

按依赖顺序，每一步都可单独提交与验证：

| # | 增量 | 主要文件 | 依赖 | 说明 |
|---|---|---|---|---|
| **I-1** | Core 增加 `GetTreeAsync` | `ChatSessionCoordinator.cs`（参照 `ForkAsync`/`QueryEntriesAsync` 的既有写法） | 无 | 发 `get_tree`，解析 `tree[]` + `leafId`。**当前缺口**，是图页唯一必需的新的 Pi 调用 |
| **I-2** | Core 增加树视图模型 | 新文件 `src/Fufucode.Core/SessionTree.cs`（建议） | I-1 | 由 `get_tree` 构出：节点表、LCA 计算、`OnCurrentBranch` 复用 `SessionHistory` 的规则；**只读、不写 Pi** |
| **I-3** | 修正"可 fork 候选"口径 | 图页/分支 UI | 无 | 不直接用 `get_fork_messages`（它不过滤分支，见 §4.1）；改用 I-2 的当前路径 user 节点 |
| **I-4** | App 图页（只读） | 新页面（遵循宿主协议，默认右区） | I-2 | 只渲染 + 详情面板 + 「在对话中定位」/「复制原文」；窄宽度降级纵向列表 |
| **I-5** | 「从此处分叉」接线 | 复用 `PersistentSessionManager.ForkAsync` | I-4 | 复用既有 fork，不新增 Core 写逻辑；分叉后草稿态、不自动发送 |
| **I-6** | 分支比较（同文件） | I-2 的 LCA | I-4 | 跨文件比较显式标"暂不支持" |

**明确不做**（本轮）：树内导航（需扩展桥，见 §4.2 路径 B）、`context_edit`、节点标签写入 Pi、跨会话自动发送、任务 DAG、跨文件分支比较。

---

## 12. 最小验证方案（需主 agent 授权后执行；未运行）

除 I-1/I-2 的单元测试外，需要一次真实 Pi 取证（可用现有 `scripts/pi-probe/` 的合成模型，不动真实凭据）：

| 编号 | 验证 | 通过依据 | 当前状态 |
|---|---|---|---|
| V-1 | `get_tree` 在 1.0.0 真实返回且结构与文档一致 | 响应含 `tree[]` 与 `leafId`；多根情况按数组处理 | **未运行** |
| V-2 | fork 后原文件字节不变、新文件 header 含 `parentSession` | fork 前后父文件 SHA-256 一致；新文件首行含 `parentSession` | **未运行**（Core 已有同类校验逻辑，但非实机） |
| V-3 | fork 非 user 条目返回 `success:false` | 错误响应可被 Core 正确抛出 | **未运行** |
| V-4 | 未落盘会话上 fork 被拒 | 出现 "has not been saved yet" 类错误 | **未运行** |
| V-5 | 遗弃分支 + 压缩点在图页正确渲染 | 用 `scripts/pi-probe/fixtures/pi-1.0.0.jsonl` 造含分叉/压缩的会话后人工核对 | **未运行** |
| V-6 | 压缩后 `contextUsage` 为 null 时图页显示"未知" | UI 检查 | **未运行** |

以上均**未执行**；不声称任何一项已通过。

---

## 13. 实施影响

| 影响面 | 说明 |
|---|---|
| Core | 新增 `GetTreeAsync` + 树视图模型（只读）；**不新增任何写路径**；不改动 `ForkAsync` |
| Pi / 扩展 | **零改动**（推荐路线下）。若将来做树内导航才需改 `extensions/fufucode-bridge/` |
| App | 新增一个功能页 + 详情面板；需符合宿主协议与布局存储（L-01/02/05/07/08/09） |
| 数据库 | 建议**不新增表**。图页视图状态走页面状态持久化；引用边若日后落地再评估加表 |
| 已定计划 | 与 `frontend-functional-plan.md` §3.1 N 系列、§4.3 A-02（分支）、§2.1 L 系列一致；与 `multiagent-collab-mvp-spec.md` §8"不采用 graph canvas（设计期编排）"一致 |
| 文档 | `docs/runtime-baseline.md` 的 Pi 版本表需与 main 实际（1.0.0）对齐，见 §16 注记 |

---

## 14. 风险

| 风险 | 等级 | 说明与缓解 |
|---|---|---|
| 基线文档与实际安装版本不一致 | 中 | `runtime-baseline.md` 仍写 0.87.1，而 main HEAD 的 `runtime/pi` 已是 1.0.0。本报告按 1.0.0 取证；若主 agent 确认应回退基线，§4 的 fork/navigateTree 结论需重新核对 |
| `get_fork_messages` 口径与文档不符 | 中 | 实现不过滤分支（§4.1）。缓解：I-3 自行过滤 |
| 把"分叉"误当作"树内切换" | 中 | fork 产生**新文件**并切换进程会话，语义比 UI 上"切分支"重得多。缓解：命名与确认文案必须说清"创建新会话" |
| 画出无法兑现的编辑/导航入口 | 高 | 直接违反既有纪律（D-06）。缓解：能力未接线前不出按钮 |
| 跨会话发送造成信息泄露 | 高 | 缓解：默认关闭 + 三要素确认 + 默认取消 + 完整内容预览 + 跨项目禁止 |
| 分叉后重放造成二次副作用 | 高 | 缓解：分叉后不自动发送；"分叉并重发"需列出后续工具调用并逐项确认 |
| 图上显示假的运行状态 | 中 | 缓解：只以 `TurnSettled`/`agent_settled` 为准；离线显示未知 |
| 本地库变成第二份权威历史 | 中 | 缓解：只存视图状态，条目内容一律回源 Pi |
| `get_tree` 多根/孤儿条目 | 低 | 文档明确可能发生；渲染时按森林处理，不做单根假设 |

---

## 15. 开放问题与未定问题

1. **"控制"的语义仍未定**（任务书已指出）：本报告的立场是"控制在最小范围内 = 看 + 分叉"，不含自动编排。需用户确认。
2. **是否需要树内导航**：若用户期望"点节点就切过去（不产生新会话）"，当前 Pi 无 RPC 入口，需走扩展桥（§4.2 路径 B），属**新的 Pi 扩展能力**，需单独立项与实测。
3. **跨文件分支比较**是否必要：成本明显高于同文件比较，本轮后置。
4. **分支命名/标签**：Pi 的 `label` 条目无 RPC 入口。是否接受"标签只存在本地库、换机器不跟随"？需产品决策。
5. **`get_fork_messages` 的行为差异**是 Pi 的实现缺陷还是文档过时？需向 Pi 上游确认或直接用 `get_entries` 规避（本报告建议规避）。
6. **图页默认停靠区**：建议右区，需用户确认（属 §10 的"建议本轮确认的四个决定"之外的补充项）。
7. **与 R02 多窗口的关系**：图页是"页"不是"窗口"；若 R02 引入独立窗口，图页能否/如何在多窗口间共享状态尚未定义。
8. **`navigateTree` 的 `summarize` 会触发 LLM 调用**：即便将来走路径 B，是否默认关闭摘要也需决策。

---

## 16. 版本与一手来源

| 来源 | 版本/定位 | 用途 |
|---|---|---|
| `@earendil-works/pi-coding-agent` | **1.0.0**（`runtime/pi/node_modules/.../package.json`；lock integrity `sha512-/FtbxoSQU/...`） | 全部 Pi 行为取证 |
| `docs/rpc-commands.md` | 1.0.0 随包 | fork/clone/get_tree/get_entries/get_fork_messages/get_state/compaction 契约 |
| `docs/session-format.md` | 1.0.0 随包 | 条目类型、树结构、上下文重建、context_edit/branch_summary/label |
| `docs/sessions.md` | 1.0.0 随包 | /tree vs /fork vs /clone 的官方语义区分、导出泄露提示 |
| `docs/compaction.md` | 1.0.0 随包 | 压缩触发、cut point、firstKeptEntryId、overflow 恢复顺序 |
| `docs/json.md` | 1.0.0 随包 | 事件序列、压缩/重试/RPC-only 事件 |
| `dist/modes/rpc/rpc-mode.js` | 1.0.0 随包 | **RPC 命令 dispatch 全表（33 个）**、fork/clone 真实实现路径 |
| `dist/modes/rpc/rpc-types.d.ts` | 1.0.0 随包 | `RpcCommand` 联合类型（用于确认 navigateTree **不在**其中） |
| `dist/core/agent-session-runtime.js` | 1.0.0 随包 | `fork()` 逐步实现（175-235 行） |
| `dist/core/agent-session.js` / `.d.ts` | 1.0.0 随包 | `getUserMessagesForForking()`（3325-3339）、`navigateTree`（d.ts 811-831） |
| `dist/core/extensions/types.d.ts` | 1.0.0 随包 | `ExtensionCommandContextActions`（1638-1666） |
| `dist/core/session-manager.d.ts` | 1.0.0 随包 | `getTree`/`resetLeaf`/`branchWithSummary`/`createBranchedSession` |
| `src/Fufucode.Core/PersistentSessionManager.cs` | main@4d7c2a2 | `ForkAsync`（143-202）、`SessionForkResult`（582-584） |
| `src/Fufucode.Core/SessionHistory.cs` | main@4d7c2a2 | 当前分支/压缩前判定（56-89） |
| `src/Fufucode.Core/DesktopMetadataStore.cs` | main@4d7c2a2 | `parent_session_id`/`fork_entry_id` 列、`CreateForkSession`（144-164） |
| `docs/design/frontend-functional-plan.md` | 工作区未跟踪 | 停靠区 L 系列、宿主协议、A-02/A-03、D-06、B-06、I-03 |
| `docs/design/multiagent-collab-mvp-spec.md` | main@4d7c2a2 | §4 状态纪律、§6 缺口、§8 不采用清单 |
| `docs/AI_DEVELOPMENT_PLAN.md` | main@4d7c2a2 | §1 首版不含多 Agent 编排、§6.4 生命周期、§7.1 状态分层、§7.2/7.3 数据归属与所有权 |

SHA-256 明细见 `evidence.md`。

> **注记**：`docs/runtime-baseline.md` 表格仍写"Pi npm 包 0.87.1"，并把 1.0.0 描述为未合并的 `track-a-pi10`。但当前 main HEAD 的 `runtime/pi/package.json` 已为 `1.0.0`、工作区已安装 1.0.0、`git status runtime/pi` 干净。本报告以 **1.0.0** 为准，并把该文档不一致作为风险交主 agent 处理。

---

## 17. 证据分级汇总

| 结论 | 分级 |
|---|---|
| Pi 1.0.0 已安装、HEAD 声明一致 | **实测**（`git`/`cat`/`sha256sum`） |
| RPC 共 33 个命令、无 navigate/context_edit/label | **源码事实**（读 `rpc-mode.js` dispatch + `rpc-types.d.ts`） |
| `navigateTree` 仅对扩展命令可用 | **源码事实**（`agent-session.d.ts:811` + `types.d.ts:1653`） |
| fork 要求 user 消息、建新文件、同进程切换、需已落盘 | **源码事实**（`agent-session-runtime.js:175-235`） |
| `get_fork_messages` 不过滤当前分支 | **源码事实**（`agent-session.js:3325-3339`） |
| 压缩不删原条目、context_edit 不改原条目 | **源码事实**（`session-format.md`、`compaction.md`） |
| Core 有 ForkAsync / parent_session_id / SessionHistory 分支判定 | **源码事实**（读 C#） |
| Core 从不调用 `get_tree` | **源码事实**（全仓 `src/` 检索无 `get_tree`/`GetTree`） |
| 六类节点的原生/投影归属 | **推断**（由上述源码事实推导） |
| 三条路线比较与推荐 A | **推断**（设计判断，附反对理由） |
| 四个操作流程、边语义、状态机、线框 | **推断**（设计提案，待用户与主 agent 确认） |
| V-1～V-6 验证结果 | **未证实** —— 未运行，不声称通过 |
| 跨会话发送的信息泄露实际影响 | **未证实** —— 未做真实数据演练 |
| 图页在真实 Avalonia 窄窗口/DPI 下的表现 | **未证实** —— 未做实机 UI 验证 |

---

**交付人**：WorkBuddy 研究 agent（R03）
**状态**：待主 agent 审查
**文件**：`docs/research-results/R03/claim.md`、`docs/research-results/R03/report.md`、`docs/research-results/R03/evidence.md`
