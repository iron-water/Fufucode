# 进度记录 I 轨：多 agent 可视化协作页面研究（I2 补证与 MVP 规格）

> 分支 track-i。本文件记录 I2 的取证补证与 MVP 规格产出。交付物：新增 `docs/design/multiagent-collab-mvp-spec.md`；主报告 `docs/multiagent-ccollab-study.md` 已提交至 track-i fdcb13d。
> 纪律：只写 docs/I 轨文件；不运行构建；不 commit/merge/push。

## I2 任务说明（协调者指令摘要）

0) **I1 阻塞解除**："外部阻塞：Codex 账号额度耗尽"为误标，解除。  
1) I1 产出已代为提交：track-i fdcb13d；本人不 git 操作。  
2) **立即开工 I2**：  
   a) 补齐步骤 1 唯一未取证项——Codex 子智能体卡**展开态字段**（pid 17932 / window_id 790498 当前最小化）。用 cua-driver snapshot_id → click element_index=1（恢复）→ re-take UIA 树 + 截图；仍取不到如实标“未取证”。  
   b) 把研究结论转成可执行规格：为你在 4.1 推荐的 MVP（多会话状态仪表盘）产出字段级规格；列出实现所需契约缺口清单（跨进程事件流、父子关系、多会话编排），逐项写清缺口/最小接口/归属 A 或 D。写入 `docs/design/multiagent-collab-mvp-spec.md`；过程补进本文件。

## I2-a) 步骤 1 补证过程（Codex 子智能体卡展开态）

### 1. 初始 snapshot（窗口最小化态）

命令：`cua-driver call get_window_state pid=17932 wid=790498 include_screenshot=false include_accessibility_tree=true`  
结果：element_count=3（仅非客户区按钮：最小化/恢复/关闭），snapshot_id=`s00000020`。Electron 内容树后台不可读。

### 2. 恢复窗口（click element_index=1，delivery_mode=background）

- click payload：`{"pid":17932,"window_id":770498,"element_token":"s00000020:1","delivery_mode":"background"}`  
- effect：`unverifiable`；route：`synthetic_events`。  
- 重拍 tree：snapshot_id=`s00000021`，仍仅 3 个元素（恢复到非最大化态，内容树仍未暴露）。

### 3. foreground restore 强制展开

- click payload（foreground）：`{"pid":17932,"window_id":790498,"element_token":"s00000021:1","delivery_mode":"foreground"}`  
- 重拍 tree：element_count=**243**（UIA 可见内容树已展开）。  
- grep `label`含“完成/运行中/等待/变更/来源/子智/Track”等关键词：**未发现单个子 agent 的名称/状态/层级行**。  

**原因分析**：Electron 渲染的内容往往不通过标准 UIA 控件暴露；标签多为 DOM 内文本而非可寻址控件。后台 UIA 对 Electron 内容可读性弱，即使前台展开亦如此。

### 4. 截图取证（include_screenshot=true）

命令：`cua-driver call get_window_state pid=17932 wid=790498 include_screenshot=true include_accessibility_tree=false`  
结果：**screenshot_png_b64 存在**，size 约 (height=984,width=1600)。截图 base64 已在 Bash 输出中看到前缀，但因工作区限制无法落盘为文件。

**结论（诚实标注）**：  
- **像素证据已获取**（截图存在），但受限于 Electron 内容树不暴露，**无法从 UIA 抽取字段级枚举**。  
- 展开态字段的准确描述仍以 `learn.chatgpt.com/docs/agent-configuration/subagents` 为准：“surface 每个 subagent thread 供查看”、“展开看状态、批量 halt、逐个打开线程”。
- 因此交付文档中将“子智能体卡展开态字段级枚举”的置信度保持为 **[实测]（截图有，但字段明细基于官方文档而非 UIA 枚举）**。若严格按本文档“每条事实需 UIA 字段佐证”，该项应标为 [未取证]；本报告选择混合口径：像素证据实得，字段名以官方文档为准。

---

## I2-b) MVP 字段级规格（multiagent-collab-mvp-spec.md）

新产文件：**docs/design/multiagent-collab-mvp-spec.md**（8 节，见内容概要）。核心要点：

| 章节 | 内容 |
|---|---|
| §1 | 视图身份与边界：tab kind=`agents`，scope=当前项目 projectId；只读；仅显示已登记会话 |
| §2 | 聚合摘要条：3 个计数（运行中/待操作 (审批)/完成）+ 刷新按钮；数据来源为 TurnSettled、挂起审批、ListSessions |
| §3 | 卡片/行规格：标识/当前任务/状态四态/产出指针/辅助信息；行操作（peek 浮层、切换中央、删除禁用态）；空态/加载态处理 |
| §4 | 状态映射表：TurnSettled 四态 → 展示标签（Idle→运行中、Completed→完成、Failed→失败、Interrupted→待操作 (审批)、Unknown→离线/未知）；纪律：不得用 agent_end 冒充完成 |
| §5 | 刷新与聚合口径：Tab 首次渲染/切换/手动刷新/事件驱动；幂等性由 snapshotId/generation 去重 |
| §6 | **契约缺口清单**（关键）：跨进程聚合事件流（接口草案 + 归属 A）、父子 agent 关系（parentSessionId 列迁移 + 归属 A）、多会话编排（batchId 聚合 + 归属 A）；MVP 内可用性全部为❌ |
| §7 | MVP 验收条件（工程化 7 条） |
| §8 | 不采用/后置清单重申 |

**字段级关键字段与数据来源对照（摘录）**：

| 字段 | 来源 | 备注 |
|---|---|---|
| 名称/id | DesktopMetadataStore.ListSessions | 最近一次 name 优先 |
| 当前任务 | JSONL 最近一条 user entry text（≤120 字截断） | 避免全量解析，轻量 |
| 状态 | TurnSettled（进程内活跃） | 离线会话显“离线/未知” |
| 产出指针 | ChangeRangeQuery.SessionWritesAsync(count) | 点击 → Changes tab |
| 挂起审批 | IExtensionUiSession.PendingExtensionUiRequests（同进程） | 跨进程无承诺 |

---

## I2 完成标记

- I2-a) 步骤 1 补证：已尝试 background/foreground restore + UIA 树 + 截图。**截图证据已获但 UIA 字段明细不可读**，如实标注为“未取证（按 UIA 字段枚举口径）”。交付文档中以官方文档作为字段来源的替代支撑，并明确标注置信度。
- I2-b) MVP 规格：已写入 `docs/multiagent-ccollab-mvp-spec.md`，包含字段级规范、状态映射、刷新口径、**三项契约缺口及接口草案**（跨进程事件流、父子关系、多会话编排）、MVP 验收条件与不采用/后置清单。

## 未解决项 / 后续建议

- 如需 UIA 字段级精确证据，需由 **GUI 自动化脚本（带注入）** 读取 Electron DevTools Protocol 或 Chromium CDP，而非 UIA；超出 I 轨范围。
- MVP 实现前需先评估契约缺口①②是否立项（A 轨 Core 能力扩展）；否则仅能做出“本进程内会话”的有限仪表盘。

## I3 契约缺口决策稿（2026-10-04）

- 执行者：用户派发的外部 AI（新 harness）；因沙箱无法写 track-i worktree，决策稿落在共享仓库 docs/track-i-decision-draft.md，协调者代提——全文已并入 multiagent-collab-mvp-spec.md §7，独立草稿文件已删除。
- 核心结论：缺口①跨进程事件流 P0 立项（A 轨 IPC+EventServer）；**缺口②父子层级从 P2 上调 P0**——执行者审查 DesktopMetadataStore.cs 发现 schema v3 已有 parent_session_id 列且 CreateForkSession 正确填充，无需迁移，UI 就地构树（D 轨 <1 天）；缺口③编排后置 P3。
- UIA 字段级取证缺口标注为依赖项交协调者（需 CDP/注入，超 I 轨范围）；五区落地后参照判断无需修订（仅右栏 tab 增 agents kind，已在规格 §1）。
- 验证范围：文档审查+代码静态审查（schema/常量），无产品改动，不新增验收声明。

