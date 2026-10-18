# Fufucode 多 agent 协作可视化——MVP 规格（右栏·协作 tab）

> 日期：2026-10-02。分支 track-i。依据：multiagent-collab-study.md §4。目标：为“多会话状态仪表盘”输出**可实施字段级规格**，并列出实现所需的核心契约缺口及接口草案。

---

## 1. 视图身份与边界

| 项目 | 取值 |
|---|---|
| **tab kind** | `agents`（右栏新增 tab） |
| **scope** | 当前项目 `projectId`；仅显示该项目下已登记的会话（来自 `DesktopMetadataStore.ListSessions`） |
| **数据时效** | 本进程内已 attach 的会话用实时订阅更新；其余标“离线/未知” |
| **只读** | 本 MVP 不写操作；点击产出门径 → Diff/Changes tab |

---

## 2. 聚合摘要条（顶部固定）

**位置**：协作 tab 头部左端。  
**字段与口径**：

| 字段 | 来源 | 口径与备注 | 置信度 |
|---|---|---|---|
| `运行中` | `TurnSettled` 计数（进程内活跃会话数 = `ChatSessionCoordinator.IsIdle == false` 且无 settled 标记） | “本进程内已 attach 会话”的 running 估计；无法覆盖后台离线会话 | [已有] |
| `待操作(审批)` | `IExtensionUiSession.PendingExtensionUiRequests.Count`（限同进程挂起请求数） | 每会话的 pending UI 汇总；跨进程不承诺 | [已有，限同进程] |
| `完成` | `ListSessions` 过滤 `active == false` 数量 | 最近一轮 `TurnSettled` 为 Completed 的会话列表计数 | [已有] |
| `[刷新]` | — | 用户手动触发，重查 ListSessions + ChangeRangeQuery.SessionWrites + 挂起审批 | UI |

**交互**：点数字不展开明细，仅跳对应行；"[刷新]"按钮触发表单重查询。

---

## 3. 卡片/行规格（纵向列表）

每行代表一个会话/agent；字段如下：

| 字段 | 展示文本示例 | 数据来源 | 更新方式 | 备注 |
|---|---|---|---|---|
| **标识**（名称/ID/项目） | `Track A core` | `ListSessions[session].name` + `.id` | 加载时；切换会话时校验 | 若 name 不存在用 id 前缀 |
| **当前任务** | `任务：实现 changes_list 只读契约` | 最近一条 user message 摘要（JSONL 最近 entry text ≤ 120 字截断） | 会话切换时 | 从 JSONL history 读取，避免解析全部文件 |
| **状态** | `运行中 · 待操作(审批) 2 · 完成 · 失败` | `TurnSettled` 状态 + 挂起审批数投影 | 订阅式 + 手动刷新 | 映射见 §4 |
| **产出指针** | `▸变更 11 · 无审批 · 12m 前` | `ChangeRangeQuery.SessionWritesAsync(projectPath, messages).Files.Count`；挂起审批数；会话最后时间 | 切换会话时 + 刷新按钮 | 点击 → 打开该会话 Changes tab 并定位 |
| **辅助信息**（可选） | `离线 · 未连接 Pi` | 元数据中 `isActive=false` 或心跳丢失 | 加载时 | 明确诚实态，不冒充实时 |

**行操作**（右侧按钮/悬停菜单）：
- `▸` 展开 peek 浮层（轻量详情：会话 ID、branch、tokens 概览、最近一条文本首行）；
- 右键“切换到中央”→ Central 面板切到该会话；
- “删除”（仅禁用态会话可用）。

**空态/加载态**：
- 无会话：显示 "无活动会话"；
- 加载失败：显示 "读取失败，[重试]"；不假计数。

---

## 4. 状态四态映射表

**输入事件**：`SessionEvent.TurnSettled`（来自 `PersistentSession.Chat.Event`）：
```csharp
TurnSettled { sessionId, state: Idle | Completed | Failed | Interrupted | Unknown }
```

**映射规则**（Fufucode 内部枚举 → 展示标签）：

| TurnSettled.state | 展示标签 | 说明 |
|---|---|---|
| `Idle`（无 settle 事件） | `运行中` | 尚未收到 settled；注意不得用 agent_end/final text 冒充 |
| `Completed` | `完成` | 正常结束 |
| `Failed` | `失败` | 异常结束 |
| `Interrupted` | `待操作 (审批)` | 若有 `PendingExtensionUiRequests > 0`，显示数字；否则单显“待操作” |
| `Unknown` | `未完成（未知）` | **不发成功通知**；MVP 默认隐藏 Unknown，或在离线行标“离线/未知” |

**关键纪律**：不得用文本结尾、工具结果成功回灌判断完成；必须以 `TurnSettled` 为准（cli-design P1-2）。

---

## 5. 刷新与聚合口径

| 场景 | 刷新内容 | 调用链（只读） | 幂等性 |
|---|---|---|---|
| **Tab 首次渲染** | ListSessions；对每会话：TurnSettled（如存活）、SessionWrites、挂起审批 | `DesktopMetadataStore.ListSessions(projectId)` → 逐会话：`ReadOnlySession.ReadJsonl()` + `ChangeRangeQuery.SessionWritesAsync()` + `extensionUi.PendingExtensionUiRequests` | 是（由 snapshotId 去重） |
| **会话切换** | 当前会话 TurnSettled、SessionWrites、挂起审批 | 同上（单会话） | 是 |
| **手动刷新** | 全量（同上 + 最近一次 TurnSettled 的最新确认） | 强制再发 `SessionUsageQuery` 口径检查 | 是 |
| **事件驱动** | 本进程内活跃会话的 TurnSettled 更新 + 挂起审批变化 | `TurnSettled` 回调入本地 Channel；挂起 UI 队列事件 | 是（带 generation 号） |

**不可达会话**：对于关闭/离线的会话，不拉取实时状态，仅显示元数据 + 最后已知状态（"离线"）。

---

## 6. 实现依赖的 Core 契约（现状 vs 缺口）

| # | 契约需求 | 缺口是什么 | 最小接口草案 | 归属 | 实现难度 | MVP 内可用性 |
|---|---|---|---|---|---|---|
| 1 | **跨进程聚合事件流** | 当前 `TurnSettled` 仅限进程内订阅；无跨进程订阅口；重启/断线后无回放/sequence | `events stream --ui <instanceId>`：server 持窗口状态，stdin 消费 `{id,method,params}`，stdout 推 `{type:"event",schemaVersion,instanceId,seq,timestamp,kind,elementId,payload}`；`TurnSettled` 需带 `generation` 和 `sessionRef` | A（Core+Pi） | 高（需 IPC/通道） | ❌ MVP 只覆盖本进程 |
| 2 | **父子 agent 关系** | Desktop SQLite schema 2 sessions 无 `parent` 列；Pi fork/clone/get_tree 返回 id/parentId/children/leafId 但未被产品元数据采用 | `MetadataSchema2.AddColumn("parentSessionId")` + migration；`Session.SetParent(parentId)`；API 支持 `ListSessions(projectId, includeHierarchy=true)` | A（Core） | 中高（需迁移 + 锁交接） | ❌ 后置（层级树） |
| 3 | **多会话编排** | `ChatSessionCoordinator` 管单连接；无统一编排器；多开 Pi 不保证结果聚合/审批归属清晰 | `Orchestrator.StartBatch(sessionIds[])` → return `batchId`；`Orchestrator.Aggregate(batchId)`；审批归属绑定 batchId | A（Core） | 高（资源/调度/结果聚合） | ❌ MVP 不承诺编排 |

**结论**：MVP 只能消费**已有**的 `ListSessions`、`TurnSettled`（进程内）、`SessionWrites`、挂起审批。层级树与泳道时间线列为**后置**。

---

## 7. MVP 验收条件（工程化）

1. 右栏 `协作` tab 存在；列表按 `lastOpenedUtc` 倒序；无会话时显空态。
2. 每行含：标识（name/id）、当前任务（最近 user 文本摘要≤120 字）、状态（四态）、产出（变更计数 + 审批数 + 相对时间）。
3. 状态来自 `TurnSettled`，不以 agent_end/text 冒充完成。
4. 点击产出门径（"变更"）→ 打开该会话 Changes tab；点击行主体→中央切会活。
5. 挂起审批仅统计同进程 `PendingExtensionUiRequests`，跨进程不承诺。
6. 离线会话不伪造状态，显式标“离线/未知”。
7. 测试覆盖：有会话/无会话/加载失败/TurnSettled 四态/审批挂起/刷新/点击跳转；mock data + headless preview 验证 UI。

---

## 8. 不采用 / 后置清单（本轨重申）

- **不采用**：graph canvas（设计期编排）、多会话并发编排（无审批归属/结果聚合）、后台 daemon（进程生命周期即边界）。
- **后置**：主子层级树（需 parent 列迁移）、泳道时间线（需带 sequence 聚合事件流）、跨进程实时状态与审批。

## §7 决策稿：三项契约缺口评估与推荐立项组合（2026-10-04）

> **日期**：2026-10-04  
> **工作区**：C:\Users\21894\.codex\worktrees\track-i-research\Fufucode（分支 track-i）  
> **依据文档**：
> - `docs/archive/progress/progress-i.md`（I1/I2 完成记录与未解决项）[置信度：高]
> - `docs/design/multiagent-collab-mvp-spec.md` §6（三项契约缺口清单）[置信度：高]
> - `docs/design/frontend-design-guideline.md` §3（右栏 tab 化交互规范）[置信度：高]
> 
> **任务来源**：用户指令——对 I2 列出的三项缺口（跨进程事件流 / 父子会话关系 / 多会话编排）逐项评估"**不做则 MVP 缩水成什么样**"vs"**做则 A 轨需要什么最小 Core 能力**"，给出推荐立项组合与理由。
> 
> **纪律**：每条结论标注证据来源与置信度；不虚构未实测行为；无法写入 worktree 时保留此草稿供协调者代提。

---

## 一、缺口①：跨进程聚合事件流

### 现状证据

| 项目 | 内容 | 证据来源 | 置信度 |
|---|---|---|---|
| **当前能力** | `TurnSettled` 仅限进程内订阅；无跨进程订阅口；重启/断线后无回放/sequence 号 | [MVP 规格 §6] 高 | 高 |
| **代码位置** | `src/Fufucode.Core/Sessions/ChatSessionCoordinator.cs` 的事件订阅是单实例 Channel | [runtime-baseline.md][Track-A 代码扫描] | 中高 |

### "不做则 MVP 缩水"评估

**情景推演：**
```
假设：A 轨不提供跨进程事件流 → UI 只能消费本进程内活跃会话的 TurnSettled 事件
结果：
  1. 后台离线的 Pi 子进程全部显示"离线/未知"（因为没有跨进程订阅口读取其状态）
  2. 摘要计数（运行中/待操作/完成）只统计本进程会话，实际总数被低估
  3. 用户点击"运行中 · 5"但只能看到 1 个会话卡片（其他 4 个在后台不可见）
  4. MVP 核心价值"一眼看到所有 agent"失效，退化为"单实例监控仪表盘"
```

**影响等级：** 🔴 **P0（严重）**  
**置信度：** 高（基于现有单进程模型的事实，未经 UI 实测验证）

### "做则需要 A 轨最小能力"评估

**最小接口草案（源自 MVP 规格 §6）：**
```csharp
// 1. IPC server 模式或事件总线
interface IEventServer {
    void Publish(EventEnvelope envelope);  // Pi 子进程推送事件
    void Subscribe(string instanceId, Channel<EventEnvelope> channel);  // UI 注册
}

// 2. EventEnvelope 需扩展字段
record EventEnvelope {
    string id;
    string schemaVersion;
    string instanceId;          // Pi 子进程标识
    long sequence;              // 全局序列号（用于重放/去重）
    DateTime timestamp;
    SessionEventKind kind;      // TurnSettled / ToolCallStatusChanged ...
    string sessionId;           // 关联哪个会话
    int generation;             // 数据代次（断线重连后增量拉取）
    object payload;
}

// 3. PersistentSessionManager 增加 event publisher
class PersistentSessionManager {
    Task StartAsync(...) {
        // 启动时向 IEventServer 注册
        _eventServer.Subscribe(this.SessionId, _localChannel);
        // TurnSettled 事件包装后发布
        OnTurnSettled(sessionId, state) {
            var envelope = new EventEnvelope(..., generation: 1, sequence: 100);
            _eventServer.Publish(envelope);
        }
    }
}
```

**归属轨道：** **A（Core+Pi）**  
- 需修改 `PersistentSessionManager`、`ChatSessionCoordinator` 增加 event publisher + ipc server  
- 预计工作量：1-2 周（含 IPC 通信、事件序列化、重放逻辑、连接代次管理）  

**置信度：** 中高（基于 MVP 规格 §6 草案推演，未经编码验证）

### 推荐决策

**✅ 建议立项（P0）**

**理由：**
1. 这是 MVP 核心主张"多 agent 可视"的前提条件，否则 MVP 退化为单实例监控失去存在意义
2. 与其他两项相比，此项最基础也最关键
3. 风险可控：若无法按期交付，MVP 可降级为"仅本进程可见"但要明确告知用户限制

**风险提示：**
- 若跳过此项直接上线，产品口碑会受损（用户期待"多 agent"但实际只能看单实例）
- 建议在 release notes 中明确边界："v1.0 暂支持单实例会话监控，跨进程聚合预计 v1.2 上线"

---

## 二、缺口②：父子 agent 关系

### 现状证据（已修正）

| 项目 | 内容 | 证据来源 | 置信度 |
|---|---|---|---|
| **DB Schema** | Desktop SQLite `sessions` 表已有 `parent_session_id` 列 (v3) | [DesktopMetadataStore.cs:8,86-95] 高 | 高 |
| **Pi API** | Pi 的 fork/clone/get_tree 返回 id/parentId/children/leafId 但未被产品元数据采用 | [runtime-baseline.md] 中 | 中 |
| **CreateForkSession** | 方法已实现自动设 parentSessionId (lines 144-164) | [DesktopMetadataStore.cs:144-164] 高 | 高 |
| **ListSessions 树支持** | ❌ 仅返回扁平列表，无 includeHierarchy 参数 | [DesktopMetadataStore.cs:214-224] 中 | 中 |

### "不做则 MVP 缩水"评估

**情景推演：**
```
假设：ListSessions 不支持树结构 → UI 只能渲染扁平列表（但每条记录带 parent_session_id 字段）
结果：
  1. 用户可以点击卡片展开 Peek 浮层，看到"父会话：Track A core"的提示
  2. 或者 UI 自行在本地构建树：先拉所有会话，再按 parentSessionId 组装层级
  3. 不需要 Core API 支持即可显示基本层级关系
  4. MVP 可以呈现一定程度的协作视图，虽然不是完美的树形展开
```

**影响等级：** 🟢 **P3（很低）** - 实际上已不是真正缺口！  
**置信度：** 高（基于现有代码分析）

### "做则需要 A 轨最小能力"评估

**如果要做完整树形支持：**
```csharp
// 最小增量改动（仅需改一个方法）
class DesktopMetadataStore {
    public IReadOnlyList<SessionRecord> ListSessions(string projectId, bool includeHierarchy = false) {
        var sessions = GetRawSessions(projectId); // 获取全量扁平列表
        if (!includeHierarchy) return sessions;
        
        // 本地构建树（O(n) 复杂度）
        var dict = sessions.ToDictionary(s => s.Id);
        var roots = new List<SessionTreeNode>();
        foreach (var s in sessions) {
            if (s.ParentSessionId == null || !dict.ContainsKey(s.ParentSessionId)) {
                roots.Add(new SessionTreeNode(s));
            } else {
                dict[s.ParentSessionId].Children.Add(s);
            }
        }
        return MapToTreeNodes(roots).ToList();
    }
}
```

**归属轨道：** **D（App/UI）** —— 可以在 UI 侧就地构建，无需 Core API 改动！  
**预计工作量：** < 1 天（纯 UI 逻辑）  

**置信度：** 高（基于 C# LINQ 常用模式）

### 推荐决策

**✅ 建议立即采用（P0 升级）** - 从 P2 上调至 P0！

**理由：**
1. **Core 已有必要数据**：`parent_session_id` 列已存在 (schema v3)，`CreateForkSession` 已正确填充该字段
2. **UI 可就地构建树**：只需要读取所有会话 + 本地组装，不需要新增 Core API
3. **风险极低**：即使 UI 不显示层级，Core 数据仍然是完整的，不会造成数据丢失
4. **用户价值明确**：父子关系是协作可视化的核心要素之一，应该优先交付

**实施建议：**
- Phase 1（MVP v1.0）**包含此项**：UI 读取 `ListSessions` 扁平列表后，在 ViewModel 层就地构建并展示树形结构
- UI 提供交互："折叠全部"/"展开全部"/"切换平铺模式"
- 优先级**提升到与跨进程事件流同等重要**（都是 MVP 核心价值）

**关键修正说明：**
> 原评估认为需要先做 DB migration + API 改造才能支持层级，这是误判。实际检查 `DesktopMetadataStore.cs` 后发现 schema v3 已包含 `parent_session_id` 列，且 `CreateForkSession` 方法已正确设置该字段。真正的"缺口"只是 UI 缺少就地构树的逻辑，这是 App 轨道的工作，不涉及 Core 改动。

---

## 三、缺口③：多会话编排

### 现状证据

| 项目 | 内容 | 证据来源 | 置信度 |
|---|---|---|---|
| **协调器设计** | `ChatSessionCoordinator` 管单连接；无统一编排器 | [runtime-baseline.md] 高 | 高 |
| **Pi 语义** | Pi 本身是单会话模型，多开是外部逻辑拼接 | [runtime-baseline.md][Pi 源码阅读] | 高 |
| **审批隔离** | PendingExtensionUiRequests 无 batchId，无法批量归属 | [MVP 规格 §6] 高 | 高 |

### "不做则 MVP 缩水"评估

**情景推演：**
```
假设：无 Orchestrator 服务 → 只有独立会话查看，无批量操作
结果：
  1. 无法"全部暂停""按批次统计输出"
  2. 审批挂起时不知道哪个批次的总审批数，可能遗漏
  3. 用户需要逐个点击每个会话卡片才能批量操作
  4. MVP 缺少"批量管理能力"，但单会话查看仍可用
```

**影响等级：** 🟢 **P3（低）**  
**置信度：** 中（基于单会话模型推导）

### "做则需要 A 轨最小能力"评估

**最小接口草案（源自 MVP 规格 §6）：**
```csharp
// 1. Orchestrator 服务
interface ISessionOrchestrator {
    string StartBatch(IEnumerable<string> sessionIds);  // 返回 batchId
    void HaltBatch(string batchId);
    void ResumeBatch(string batchId);
    BatchStatistics Aggregate(string batchId);
}

record BatchStatistics {
    string BatchId;
    int TotalSessions;
    int RunningCount;
    int CompletedCount;
    int PendingApprovalCount;
    IEnumerable<SessionContribution> Contributions;
}

// 2. PendingExtensionUiRequests 扩展 batchId 字段
class PendingExtensionUiRequest {
    public string BatchId { get; set; }  // 可选
}

// 3. UI 侧可按 batchId 聚合显示审批数
class AgentCardViewModel {
    int GetBatchPendingApprovalCount() {
        return _orchestator.Aggregate(_currentBatchId)
                          .Contributions.Sum(c => c.PendingApprovalCount);
    }
}
```

**归属轨道：** **A（Core）**  
- 需新增 `ISessionOrchestrator` 接口与实现  
- 预计工作量：高（资源调度、结果聚合、审批归属逻辑复杂），约 2-3 周  

**置信度：** 中（基于常见批量操作模式推演，未经实际编码验证）

### 推荐决策

**⏭️ 建议后置（P3）**

**理由：**
1. **只读展示先行**：MVP 可以先不做写操作，让用户先看到各个会话的独立状态
2. **编排是高级功能**：等跨进程事件流 + 父子关系落地后再追加，不影响 MVP 核心流程
3. **优先级最低**：资源允许再上，否则可以作为"未来功能"放在 roadmap
4. **风险很低**：用户不会因为这个功能缺失而弃用产品

**实施建议：**
- Phase 1（MVP v1.0）：完全忽略，UI 只展示单会话卡片
- Phase 3（MVP v2.0+）：如果 Phase 2 顺利且资源充足再加，否则作为可选 enhancement

---

## 四、推荐立项组合与实施路线图（已修正版）

### 总体策略（基于最新代码审查）

| 阶段 | 必做项 | 后置项 | 预计时间 |
|---|---|---|---|
| **Phase 1（MVP v1.0）** | ✅ 缺口①跨进程事件流 + ✅ 缺口②父子层级树 | ⏭️ 缺口③编排 | 2-3 周（A 轨 1-2 周 + D 轨 1 周） |
| **Phase 2（MVP v1.5）** | （可选）✅ 缺口②增强（视觉连接线） | ⏭️ - | +0.5 周 |
| **Phase 3（MVP v2.0+）** | （可选）✅ 缺口③编排 | - | +2-3 周 |

**关键变化：**
- 原评估认为缺口②需要 DB migration + Core API 改动，现已纠正为"**Core 已有数据，UI 就地构树即可**"
- 父子层级关系从 P2 上调至 P0，与跨进程事件流同等优先级
- MVP v1.0 预期交付功能更完整：既有跨进程可见性 + 父子协作视图

### 详细实施建议

#### Phase 1（MVP v1.0）：跨进程事件流

**关键里程碑：**
1. **设计评审（Week 1，Day 1-3）**：
   - A 轨输出 IPC 协议草案（EventEnvelope 结构、publisher/subscriber 接口）
   - B 轨提供测试场景（模拟多个 Pi 子进程并发执行）
   - D 轨准备 UI 的渐进增强策略（先假设"本进程可见"，等缺口①落地后无缝切换）

2. **核心实现（Week 1，Day 4 - Week 2）**：
   - `PersistentSessionManager` 增加 event publisher
   - IPC server 原型（可以用内存 Channel 简化实现）
   - TurnSettled 事件带 generation/sessionRef 字段
   - Unit test：验证跨进程事件能到达 UI

3. **集成验证（Week 3）**：
   - B 轨跑真实多进程场景
   - D 轨 UI 对接 EventServer 并渲染跨进程会话卡片
   - Release note 标注："支持多实例会话监控"

**风险应对：**
- **If 进度滞后**：回退到"仅本进程可见"但要明确标注限制
- **If 跨进程事件丢包**：加 sequence 号和重放机制
- **If 性能问题**：限制同时打开的 Pi 子进程数量（如最多 10 个）

**交付物：**
- `docs/decisions.md` 追加决议条目
- `src/Fufucode.Core/Events/` 新目录（EventServer + 协议）
- B 轨集成测试报告

---

#### Phase 2（MVP v1.5）：父子关系

**前置条件：**
- Phase 1 已交付并通过验收
- UI 已支持基本会话卡片渲染

**关键里程碑：**
1. **DB 迁移（Week 1，Day 1-2）**：
   ```sql
   ALTER TABLE sessions ADD COLUMN parentSessionId INTEGER REFERENCES sessions(id);
   -- 历史数据处理：existing rows 的 parentSessionId = NULL
   ```

2. **API 改造（Week 1，Day 3-5）**：
   - `PersistentSession.SetParent(parentId)`
   - `ListSessions(projectId, includeHierarchy=true)` 返回树结构
   - Fork/Clone 路径自动设 parent

3. **UI 升级（Week 2）**：
   - 卡片列表支持树形展开/折叠
   - 父子连接线（可选视觉元素）
   - 过滤按钮："只看根节点"/"展开全部"

**风险应对：**
- **Schema 迁移失败**：备份 + rollback 预案
- **历史数据兼容**：迁移前清空 parentSessionId，旧代码继续正常工作
- **UI 复杂度**：先支持简单缩进树，不要画连接线（留作 v2.0 增强）

**交付物：**
- DB migration script (`scripts/migrations/add_parent_sessionid.sql`)
- `docs/archive/progress/progress-i.md` 追加 Phase 2 完成记录
- 用户手册更新："父子 agent 层级视图"

---

#### Phase 3（MVP v2.0+）：多会话编排（可选）

**前置条件：**
- Phase 2 已交付
- 业务需求强烈（用户调研显示批量操作是高频诉求）

**建议暂缓：**
- 除非有明显的业务驱动（如企业客户需要"一键暂停所有实验"）
- 否则可以作为 backlog item 放在 GitHub Issues

**如果要做：**
- 先定义 batch 的语义（是临时的还是持久的？）
- 审批归属怎么算（按 batchId 聚合还是按项目聚合？）
- UI 设计：batch selector + 批量操作按钮

---

## 五、决策依据总结（最终版）

### 最终推荐

| 缺口 | 原评估 | 审查后修正 | 推荐决策 | 关键理由 | 置信度 |
|---|---|---|---|---|---|
| **①跨进程事件流** | P0 | ✅ 维持 | ✅ 立项 P0 | 不做则 MVP 核心主张失效（"看不到后台会话"=伪多 agent 可视化） | 高 |
| **②父子关系** | P2 | ⚠️ **上调至 P0** | ✅ **立即采用** | Core schema v3 已有 `parent_session_id`，UI 可就地构树；用户价值明确 | 高 |
| **③编排** | P3 | ✅ 维持 | ⏭️ 后置 P3 | 只读展示先行，编排是增值功能；优先级最低 | 中 |

### 补充说明

1. **UIA 取证缺口结论：**
   - 如需精确的 Electron 内容树字段级枚举（如子智能体卡的 pid/window_id/状态机字段名），需要 GUI 自动化脚本通过 DevTools Protocol 或 Chromium CDP 读取，而非标准 UIA
   - 这项超出了 I 轨研究范围，应标注为**依赖项交协调者**（需评估是否需要装调试工具/起注入权限）
   - 参考证据：`docs/archive/progress/progress-i.md` §I2-a 已尝试 foreground/background restore + UIA 树 + 截图，但**Pixel 证据已获但 UIA 字段明细不可读**
   - **置信度：高**

2. **前端设计指南对照修订（对应 `docs/design/frontend-design-guideline.md` §3）：**
   - **现状确认**：右栏 tab 化工作视图已有 split-view 机制（L05/L06）、独立滚动（§3.5）、tab strip 机制（E4866 静态证据）
   - **适配结论**：多 agent 可视化的判断不需要修订，现有五区骨架（左栏/中央/右栏/底栏/设置）已支持该功能
   - **唯一需补充的点**：右栏 tab kind 需增加 `"agents"` 选项（已在 MVP 规格 §1 定义），但不涉及布局行为变化
   - **置信度：高**（基于 E4866 静态调用链分析）

3. **下一步行动：**
   - ✅ A 轨立即启动缺口①的设计评审（IPC 协议 + EventServer 接口），同时冻结相关 DTO 并记入 `docs/decisions.md`
   - ✅ D 轨准备 MVP UI 的渐进增强策略（确保 UI 代码不 hard-code 单进程限制），同时实现就地构树的 ViewModel 逻辑
   - ⏭️ B 轨准备 Phase 1 的集成测试场景（多个 Pi 子进程并发验证事件流）
   - ❌ 缺口③不入 Phase 1 backlog，单独写进 `docs/archive/progress/progress-i.md` 的未来建议项

4. **MVP 规格文档更新需求：**
   - `docs/design/multiagent-collab-mvp-spec.md` §6 需修正缺口②的描述：将"DB migration + API 改造"改为"UI 就地构树（无需 Core 改动）"
   - 建议在表格第 2 行"最小接口草案"列补充："D 轨道在 ViewModel 层就地组装树结构（LINQ ToDictionary）"
   - "归属"列从"A（Core）"改为"D（App）"，"实现难度"从"中高"下调为"低"

---

## 六、附录：术语与缩写

| 术语 | 含义 |
|---|---|
| **MVP** | Minimum Viable Product（最小可行产品） |
| **P0/P1/P2/P3** | Priority level（优先级从高到低） |
| **IPC** | Inter-Process Communication（进程间通信） |
| **EventEnvelope** | 跨进程事件包装结构（含 sequence/generation/sessionRef） |
| **Orchestrator** | 多会话编排服务（批量操作/审批聚合） |
| **Gap ①/②/③** | 分别指跨进程事件流/父子关系/多会话编排 |
| **DT** | DesktopMetadataStore，管理会话元数据的 SQLite 封装 |
| **Schema v3** | DT 的最新数据库版本（2026-09-29 引入 parent_session_id 列） |

---

**文档版本控制：**
- v1.1 (2026-10-04): 修正缺口②评估——发现 Schema v3 已有 parent_session_id 列，UI 可就地构树，从 P2 上调至 P0
- v1.0 (2026-10-04): Initial draft by Track-I

**Confidence Disclosure:** Assessment based on static code review of `DesktopMetadataStore.cs` + MVP spec §6 interface draft reasoning. Key correction: Gap 2 is not a Core dependency but an App/UI task. Overall confidence for Gap 1: High; for Gap 2 (corrected): High; for Gap 3: Medium.
