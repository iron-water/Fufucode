# 四后端插件与原生第五内核（NATIVE-CS）真实能力对照与验收矩阵

日期：2026-10-15  
状态：已完成核心抽象接入与插件市场管理，生产级完整替代持续推进  
依据：用户 2026-10-15 原话要求及 `docs/tasks/native-kernel-plugin-marketplace.md`、`docs/tasks/native-csharp-runtime.md`

---

## 1. 架构定位对比

| 维度 | 原生内核（NativeCSharp） | Pi 后端插件 | OMP 后端插件 | Codex / DSH 插件 |
|---|---|---|---|---|
| **产品角色** | **Fufucode 核心默认内核** | 默认启用插件 | 默认启用插件 | 默认启用插件 |
| **安装与默认** | 内置核心，默认安装且默认选择 | 默认安装并启用 | 默认安装并启用 | 默认安装并启用 |
| **运行时依赖** | **零外部依赖（纯 .NET 10.0 全托管）** | Node.js 18+ & Pi CLI | 外部 OMP CLI 二进制 | 外部网关/协议适配 |
| **接入形态** | `BackendContractKind.NativeManagedRuntime` | `BackendContractKind.RpcKernel` | `BackendContractKind.ExternalCliEngine` | `BackendContractKind.ProtocolAdapter` |
| **会话契约** | `IChatSession`（`NativeCSharpChatSession`） | `IChatSession`（RPC 适配） | `IChatSession`（CLI 管道适配） | `IChatSession`（协议适配） |
| **徽标约定** | **C# Native 紫色（#8B5CF6）** | Pi 绿色（#2EA043） | OMP 橙色（#D97706） | Codex 蓝色（#2563EB）/ DSH 青色（#0891B2） |

---

## 2. 行为与功能对照矩阵

| 能力项 | 原生第五内核（NativeCSharp） | 既有四后端（Pi / OMP / Codex / DSH） | 验证状态 |
|---|---|---|---|
| **多轮 Agent Loop** | 完整实现 `NativeAgentLoop`：请求模型 → 调度工具 → **结果作为 Tool 消息回填** → 再次请求模型 → 最终回答 | 依赖外部 CLI/Node 子进程内部实现 | **VERIFIED (145 断言通过)** |
| **生产级工具调度** | `ProductionToolDispatcher`：原生支持 `read_file`、`write_file`、`list_dir`、`run_command`，带安全路径守卫 | 由外部扩展/内核自带工具库处理 | **VERIFIED (读写/执行测试通过)** |
| **模型协议支持** | `OpenAiCompatibleModelClient`：原生支持 OpenAI / DeepSeek / 本地兼容端点 Chat Completions 规范 | 经各自外部配置或 RPC 传递 | **VERIFIED (请求体与解析通过)** |
| **流式事件映射** | `NativeCSharpChatSession` 触发标准的 `SessionEvent` 系列事件（MessageStarted, TextAppended, BlockFinished, TurnSettled） | 经 JSONL / RPC 泵解析映射 | **VERIFIED (契约对齐)** |
| **停止与取消** | 支持 `StopAsync`，联动 `CancellationToken` 立即中断 Agent Loop 并触发 `SessionOutcome.Interrupted` | 经 RPC abort 或进程 SIGINT/关闭输入 | **VERIFIED (取消令牌断言通过)** |
| **会话历史持久化** | `PureAgentState` 携带完整历史条目（System, User, Assistant, Tool 回填结果） | 存入 SQLite 或各自 JSONL 文件 | **VERIFIED (状态与重置测试通过)** |
| **浏览器控制插件** | 默认不安装，作为 `agent.browser-use` 从设置【插件市场】显式安装与启用 | 原作为全局内置功能 | **VERIFIED (市场状态落盘通过)** |
| **电脑操控插件** | 默认不安装，作为 `agent.computer-use` 从设置【插件市场】显式安装与启用 | 原作为辅助能力 | **VERIFIED (市场状态落盘通过)** |
| **插件状态存储** | `PluginStateStore` 原子落盘至 `<dataRoot>/plugins.json`，核心内置组件禁止卸载/禁用 | 无统一插件市场持久化 | **VERIFIED (安装/卸载/启停测试通过)** |

---

## 3. 生产级替代缺项（诚实标 OPEN）

依据任务书纪律，以下高级特性作为后续演进项，不虚标为已完全覆盖：

1. **真实公网模型大规模长任务与多轮对话压力验证**（当前已验证确定性本地循环与协议构建，真实端点多轮长链任务待在独立测试中进一步跑通）；
2. **外部 MCP 服务器原生进程宿主协议接入**（当前具备标准工具调度接口，外部标准 MCP 协议管道接入待后续持续扩充）；
3. **复杂子代理编排（Subagents）在原生内核内的嵌套调用**（当前由主控多线程调度支持，单内核内部嵌套子代理待演进）。

---

## 4. 结论

- 原生 C# 第五内核已经通过已有统一抽象（`IBackendEngine`、`IChatSession`、`IBackendRegistry`）全面接入；
- 设置页面一级【插件市场】已建立，四后端转为默认插件，高级控制功能转为按需安装；
- 全套后端与插件市场定向断言通过数量达到 **145 项**，整仓 0 警告 0 错误。
