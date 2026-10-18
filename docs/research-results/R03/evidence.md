# R03 证据清单

配合 `report.md`。全部 SHA-256 由本机 `sha256sum` 实测（2026-10-05，America/Los_Angeles）。本任务未修改任何被取证文件。

## 1. 版本基线（实测）

| 项 | 值 | 取证命令 |
|---|---|---|
| 分支 | `main` | `git rev-parse --abbrev-ref HEAD` |
| HEAD | `4d7c2a26cbdab744924dadb466990783ef2f7725` | `git rev-parse HEAD` |
| HEAD 声明 Pi 版本 | `1.0.0` | `git show HEAD:runtime/pi/package.json` |
| 工作区安装版本 | `1.0.0` | `runtime/pi/node_modules/@earendil-works/pi-coding-agent/package.json` |
| lock integrity | `sha512-/FtbxoSQU/mEv1QnichJjRjqteqaIaMWxmhB4G367+MwZfX7/DI5B9YAg5lqbN7nztFskBEtUSZ+FlmMBECtMw==` | `runtime/pi/package-lock.json` |
| `runtime/pi` 相对 HEAD | 干净（`git status --short runtime/pi` 无输出） | `git status` |

## 2. Pi 1.0.0 一手来源与 SHA-256

前缀：`runtime/pi/node_modules/@earendil-works/pi-coding-agent/`

| 文件 | SHA-256 | 报告中的用途 |
|---|---|---|
| `package.json` | `81ac04bbcbc8d80bdb28bcb88cabe8345ffabc6b4c9389fd74c85fab76a0aac6` | 版本证据 |
| `docs/rpc-commands.md` | `4deeee367852f4633ef594bc079f9e9fb14640a4e3ba8a69b7c89b6f54740838` | fork/clone/get_tree/get_entries/get_fork_messages/get_state/compact 契约；fork 取消响应陷阱；压缩后 contextUsage 为 null |
| `docs/session-format.md` | `4f8fc42179b332bcf71fe4c85cc95d914853ff7486d68742954a45316b512a4a` | 条目类型全集、id/parentId 树、parentSession header、compaction/context_edit/branch_summary/label/session_info、buildContextEntries |
| `docs/sessions.md` | `5ad5bda19caaa0794bb44f86c85c8e66be6f493c52bb5e6a4d127b58cd8c2c1e` | `/tree` vs `/fork` vs `/clone` 官方语义区分；导出/分享包含工具参数与文件内容 |
| `docs/json.md` | `839a2a2da8112298a7f8870afa71d1998db2d43b8f68c9c179a0c696b6477a5e` | 事件序列、compaction 事件、retry 事件、RPC-only 事件 |
| `docs/compaction.md` | `190b8c651148504613aea98b0637807c536ed0362948fda1bcd1ae137d75ff4a` | 触发阈值、cut point 规则、firstKeptEntryId、overflow 恢复顺序 |
| `dist/modes/rpc/rpc-mode.js` | `631697cd35928fc827f4a423538c43ff227b8cbf63c2a2f11060616f55eba6db` | **RPC 命令 dispatch 全表**、fork/clone 分支实现、`rebindSession` 注入 `navigateTree` 给扩展 |
| `dist/modes/rpc/rpc-types.d.ts` | `68b6dc2e47a3969c09c961a40d0462473407b6786f3bd02715fa035e816e2af1` | `RpcCommand` 联合类型（确认 navigateTree/context_edit/label **不在**其中） |
| `dist/core/agent-session.d.ts` | `2e50b35a37f9c7149c6297ae554b2d965bd74dbfcb8ccd7be44f13226ce497e7` | `navigateTree` 签名与注释（811-831 行）、`getUserMessagesForForking` |
| `dist/core/agent-session-runtime.js` | `928b6528bb17d26273af8f7023a4f98e5f440d5896a4280d060b9b3e1f9a6882` | `fork()` 逐步实现（175-235 行） |
| `dist/core/extensions/types.d.ts` | `6c78c89ad1ffb421e81ff0ad11ceff1a635b21fd818a32aa3e87009392298d9f` | `ExtensionCommandContextActions`（1638-1666 行） |

补充定位（同一包内，未单列哈希）：`dist/core/agent-session.js:3325-3339`（`getUserMessagesForForking` 实现）、`dist/core/session-manager.d.ts:340/353/359/365/394`（`getTree`/`resetLeaf`/`branchWithSummary`/`createBranchedSession`/`forkFrom`）。

## 3. Fufucode 产品侧取证（只读，未修改）

| 文件 | SHA-256 | 关键结论 |
|---|---|---|
| `src/Fufucode.Core/PersistentSessionManager.cs` | `a7e615897b52e5ef4b0d7f8befcf365713d9e8b03b7b9b5af58d39a2c5277115` | `ForkAsync`（143-202）：Idle 前置校验、父文件 SHA-256 前后比对、租约先子后父、父行保留；`SessionForkResult`（582-584）声明 `ParentFileUnchanged` 是证据非保证 |
| `src/Fufucode.Core/SessionHistory.cs` | `350b436f4b59b202284524be665030d0942523c8945f7687ba903708f8011b62` | `Rebuild()`（56-89）：由 leaf 回溯构 `activeIds`、计算 `firstKeptIndex`、标记 `OnCurrentBranch`/`BeforeCompaction`；检出循环与重复 id |
| `src/Fufucode.Core/DesktopMetadataStore.cs` | `dff1027d9377df83c4034bc9505c64b5197da24b59c0d75b43133c190f212ab1` | schema v3 的 `parent_session_id`/`fork_entry_id`/`forked_at_utc` 列（8、86-95）；`CreateForkSession`（144-164）；`ListSessions` 仍为扁平查询（227） |
| `src/Fufucode.Core/ChatSessionCoordinator.cs` | `a68faf729d6c5e52e4feda1240ffc2d37899e9a831234b8e2a984e77ba65b833` | `ForkAsync`（102-105）发 `fork`；`QueryEntriesAsync`（113）发 `get_entries`；**无** `get_tree` 调用 |
| `docs/design/frontend-functional-plan.md` | `6fbffe6ccf25bfffa7df0bc8c6048f45be5dd825ea4bcf02ce747cf58ead664f` | L-01/02/05/07/08/09 停靠与布局；宿主协议（§3 前言）；A-02 分支、A-03 副作用重放、A-04 引用、D-06 不做伪可用按钮、B-06 浏览器无 Pi 权限、I-03 草稿 |
| `docs/design/multiagent-collab-mvp-spec.md` | `a34428d6c097df4876adec942cc904a94e7a06e1bce7081efcb4126995e4c483` | §4 终态以 `TurnSettled` 为准；§6 三项契约缺口；§8"不采用 graph canvas（设计期编排）、多会话并发编排" |
| `docs/frontend-prototype/index.html` | `a0d7d8be201038f1b1d6f642b548b46660832ebe96fe73e6b9ad7a1c9c34d0c3` | 确认原型中**无** graph 页面（`grep -c "graph"` = 0） |
| `docs/research-tasks/R03-graph.md` | `b3498821e5a8feeca03b2a233cb64a7f81b627ffd116a42af0e97c1f87c3a75f` | 任务书 |
| `docs/research-tasks/README.md` | `d5a4dec45f56afba3d8cbfce0e8a244d082bb00eba6244d31f57c9d8d7af1c7c` | 共同协议 |

## 4. 关键检索结论（可复现）

| 检索 | 结果 | 支撑的报告结论 |
|---|---|---|
| `dist/modes/rpc/rpc-mode.js` 中 `case "<cmd>":` | 33 个命令，无 `navigate`/`set_leaf`/`reset_leaf`/`context_edit`/`label` | §4.2 树内导航与条目编辑无 RPC 入口 |
| `RpcCommand` 联合类型 | 同上，一致 | 同上（双重确认：实现 + 类型） |
| `docs/` 全文检索 `navigateTree` | **0 命中** | §4.2 该能力未文档化，不应作为契约依赖 |
| `dist/` 检索 `subagent|multi.?agent|agent instance|spawn|delegate` | 无 agent 实例/编排相关 RPC 或实体 | §3 "agent 实例"非 Pi 原生 |
| `src/` 检索 `get_tree|GetTreeAsync` | **0 命中** | §11 I-1 为真实缺口 |
| `src/` 检索 `ForkAsync` | Core 与 Cli 均已实现 | §4.1 fork 已可用 |
| 原型 `grep -c "graph"` | `0` | 图页为全新页面，无既有原型可依赖 |

## 5. 未做的事（诚信声明）

- 未启动 Pi 进程、未发送任何 RPC、未调用模型、未读写凭据。
- 未执行构建（`dotnet build`）或测试。
- 未运行 `scripts/pi-probe/` 或任何实验；`report.md` §12 的 V-1～V-6 均为**提案，未运行**。
- 未执行任何 Git 写操作；未写入 `docs/research-results/R03/` 之外的路径。
- `reference/`（第三方克隆）本任务未使用。
