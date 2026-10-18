# Claude Code 第五后端接入

日期：2026-10-16。状态：需求已确认，协议已实测取证，产品尚未实施。来源：用户在提问线程的直接要求。

## 用户原始意图

用户原文（2026-10-16）：

> 现在已经有了四个后端，dsh, codex, pi, omp，还有一个纯 C 后端正在开发。现在我打算做第五个后端，
> 也就是 claude 的后端。

## 关键结论（已实测，非推测）

**不需要 Claude Code 源码，直接调用官方接口即可。** 依据：

1. `claude` CLI（v2.1.296）提供 **`--output-format stream-json` + `--input-format stream-json`** 双向流式协议。
2. 官方随包发布 **`sdk-tools.d.ts`（171,449 字节）**，是 40+ 工具 I/O 的 JSON Schema 类型定义，
   文件头声明由 json-schema-to-typescript 自动生成。
3. 该协议定位与 Pi RPC 同级，`Fufucode.Pi` 项目结构可直接作为模板。

## 实测协议取证（本次抓取，真实帧）

抓取命令：
```
claude -p --output-format stream-json --verbose "<prompt>"
```

### 帧类型总览

| type | subtype | 说明 |
|---|---|---|
| `system` | `hook_started` / `hook_response` | 生命周期钩子 |
| `system` | `init` | 会话初始化，含 cwd / tools / model / session_id / permissionMode |
| `system` | `commands_changed` | 斜杠命令与技能列表变更 |
| `system` | `permission_denied` | 权限拒绝 |
| `assistant` | — | 模型输出，`message.content[]` 多块 |
| `user` | — | 工具结果回灌 |
| `result` | — | 终止帧，含 usage / cost / stop_reason |

### 关键帧实际样本

**init 帧（节选，全字段已抓）**
```json
{"type":"system","subtype":"init",
 "cwd":"C:\\Users\\mio",
 "session_id":"16043382-ffe6-4efc-9a27-cc33ace582fa",
 "tools":["Task","Bash","Edit","Read","Write","Glob","Grep",...],
 "mcp_servers":[{"name":"tavily","status":"connected","source":"user"}],
 "model":"group/auto-deepseek-v4-1-flash[1m]",
 "permissionMode":"auto",
 "claude_code_version":"2.1.296",
 "capabilities":["interrupt_receipt_v1","msg_lifecycle_v1","sdk_mcp_tools_list_changed",...],
 "messaging_socket_path":"\\\\.\\pipe\\LOCAL\\cc-msg-..."}
```

**assistant 帧（文本块）**
```json
{"type":"assistant",
 "message":{"content":[{"type":"text","text":"ok"}],
            "model":"deepseek-v4.1-flash","role":"assistant"},
 "parent_tool_use_id":null,
 "session_id":"...","uuid":"...","timestamp":"..."}
```

**assistant 帧（工具调用块）**
```json
{"type":"assistant",
 "message":{"content":[{"type":"tool_use","name":"Bash",
                        "id":"call_00_UhNep7BmwCQ8hydW79PN5225",
                        "input":{"command":"dir C:\\Windows",
                                 "description":"List Windows directory contents"}}]}}
```

**user 帧（工具结果回灌）**
```json
{"type":"user",
 "message":{"role":"user","content":[{"type":"tool_result",
                                      "content":"This command requires approval",
                                      "is_error":true,
                                      "tool_use_id":"call_00_UhNep7BmwCQ8hydW79PN5225"}]},
 "tool_use_result":"Error: This command requires approval",
 "tool_result_meta":[{"id":"call_00_...","non_execution_kind":"user-rejected",
                      "permission_decision":{"decision":"reject","source":"config"}}]}
```

**result 帧（终止）**
```json
{"type":"result",
 "stop_reason":"end_turn",
 "session_id":"...",
 "total_cost_usd":0.058888,
 "usage":{"input_tokens":7815,"output_tokens":109,"cache_read_input_tokens":34176},
 "num_turns":1,"subtype":"success","is_error":false,
 "result":"ok","duration_ms":3737,"ttft_ms":3227,
 "permission_denials":[],
 "subagent_stats":{...},
 "modelUsage":{"group/auto-deepseek-v4-1-flash[1m]":{...}}}
```

## 现有接缝（已核实坐标）

| 接缝 | 文件 | 现状 |
|---|---|---|
| 后端枚举 | `src/Fufucode.Pi/BackendProtocol.cs` | `enum BackendId { Pi, Omp }` |
| 能力声明 | 同上 `BackendCapabilities.For()` | 含 `PersistentHistory` / `ApprovalBridge` / `ProjectFork` |
| 存储标识 | 同上 `StorageId()` / `Parse()` | `"pi"` / `"omp"` |
| RPC 客户端模板 | `src/Fufucode.Pi/PiRpcClient.cs` | `Process` + `Channel` 双向流 + 请求配对 + 退出观测 |
| 插件目录 | `src/Fufucode.Core/Plugins/PluginMarketplace.cs` | 已有 `backend.pi/omp/codex/dsh` 四条 |

### PiRpcClient 可直接复用的骨架

```csharp
public sealed class PiRpcClient : IAsyncDisposable
{
    private readonly Process _process;                          // 子进程
    private readonly Channel<JsonElement> _records;             // 读流
    private readonly Channel<PiRpcEvent> _events;               // 事件流
    private readonly ConcurrentDictionary<...> _pending;        // 请求-响应配对（RPC 需要，流式不需要）
    private Task _stdoutTask, _stderrTask, _dispatchTask, _exitTask;
    public Guid Generation { get; }
    public ChannelReader<PiRpcEvent> Events => _events.Reader;
    public PiProcessExit? ProcessExit { get; }
}
```

> 注意差异：Pi 是**请求-响应 RPC**（需 `_pending` 配对）；Claude stream-json 是**单向事件流**，
> 无需配对字典，但**多轮对话需要维持进程 + 复用 `session_id`**。

## 范围

### 纳入

1. 新建 `src/Fufucode.Claude/` 项目（仿 `Fufucode.Pi`）：起 `claude.exe` 子进程，双向流编解码。
2. `BackendId` 加 `Claude`；`BackendCapabilities.For()` 加分支；`StorageId()` / `Parse()` 加 `"claude"`。
3. stream-json 帧模型（`system` / `assistant` / `user` / `result` 四类 + 各 subtype）。
4. 工具契约映射：从 init 帧的 `tools[]` 与 `sdk-tools.d.ts` 提取，映射到 Fufucode 统一工具声明。
5. 权限桥：Claude 的 `permissionMode`（`default`/`acceptEdits`/`plan`/`bypassPermissions`/`dontAsk`/`auto`）
   与 `permission_denied` / `tool_result_meta.permission_decision` 帧，映射到 Fufucode 统一审批。
6. 插件目录新增 `backend.claude` 条目（`PluginCategory.BackendEngine`，默认安装并启用）。
7. 会话 `backendId` 白名单与持久化扩展。

### 排除

- 不读、不改、不反编译 Claude Code 二进制。
- 不自行实现 Anthropic 模型协议（遵项目原则：不移植内核，不实现供应商协议）。
- 不绕过 Fufucode 统一审批（不得简单使用 `--dangerously-skip-permissions` 回避权限模型）。
- 不改动现有四后端行为。

## 需要实测确认的剩余项

以下本次**未抓取**，实施前需补充取证，不得凭推测编码：

1. **多轮对话维持方式** —— `--input-format stream-json` 的输入帧格式（本次只用了单次 `-p`）。
2. **`--resume <session-id>` 的实际行为** —— 是否复用同一进程，还是新起进程恢复上下文。
3. **权限批准回灌格式** —— 本次样本中命令被拒（`is_error:true`），**未抓到批准路径**的完整往返。
4. **中断（interrupt）协议** —— init 帧声明了 `interrupt_receipt_v1` / `interrupt_send_now_v1` 能力，
   但具体帧格式未抓。
5. **流式增量** —— `--include-partial-messages` 打开后的 partial 帧形状。

## 环境注意事项

1. **本机 claude 经 magpie 转发**：`~/.claude/settings.json` 中
   `ANTHROPIC_BASE_URL = http://127.0.0.1:3425`，实测 init 帧中 `model` 为
   `group/auto-deepseek-v4-1-flash[1m]`（非 Anthropic 官方模型）。
   接入时需决定是否用 env 覆盖，或尊重用户既有配置。
2. **已装 3 个 MCP 服务器**（playwright / fetch / tavily），init 帧会列出。
   工具名形如 `mcp__tavily__tavily_search`。
3. **已装 SessionStart 钩子**（`herdr-agent-state.ps1`），每次启动产生 `hook_started`/`hook_response` 帧。
   适配器需容忍并正确跳过这两类帧。
4. **模型未被识别**：实测日志有
   `[claude-code:unrecognized_model] {"model":"group/auto-deepseek-v4-1-flash[1m]","query_source":"sdk"}`。
   这是 magpie 路由导致的，非本机故障。

## 验收条件

- `dotnet build Fufucode.sln` → 0 警告 0 错误。
- 后端可启动 claude 子进程，正确解析 init 帧并暴露会话能力。
- 单轮文本往返可完整走通（发送 → assistant text → result）。
- 工具调用往返可走通（assistant tool_use → 审批 → user tool_result → 续轮）。
- 权限拒绝与批准两条路径均能正确映射到统一审批，**且拒绝路径不静默吞掉**。
- 会话持久化后重开可恢复（`--resume` 或 `--continue`）。
- 进程退出被观测并记录（沿用 `PiProcessExit` 模式）。
- 定向测试正负双向覆盖；未运行的验证不得标记为通过。

## 文件所有权与工作树

按 `docs/tasks/execution-contract.md` 执行：执行会话用独立 worktree，不直接写 main。
共享组合根、设置页、发布、权威计划/进度/决策由主控 M 统一接线。
建议新增路径：

```
src/Fufucode.Claude/                      ← 新项目
├── ClaudeStreamClient.cs                 ← 仿 PiRpcClient
├── ClaudeMessageCodec.cs                 ← 帧编解码
├── ClaudeProtocol.cs                     ← 帧模型 + capabilities
├── ClaudeToolCatalog.cs                  ← 工具契约
└── ClaudeBackendProtocol.cs              ← BackendId 扩展（或改 Fufucode.Pi 现有枚举）
docs/records/<任务ID>-claude-backend.md   ← 交付记录
```

## 未执行 / 未验证声明

- 本任务书基于**实际抓取的两份 stream-json 样本**与**实际读取的源码文件**形成。
- **未读** `PiRpcClient.cs` 全文（只读前 60 行）、未读 `PiProtocol.cs` / `PiRpcOptions.cs`。
- 未定位会话持久化的 `backendId` 白名单确切位置。
- 未评估新增 `BackendId.Claude` 对现有 `switch` 表达式与穷尽性检查的影响面。
- **未运行构建、未运行任何测试、未修改任何产品源码**。
- 本文件为计划，不代表任何功能已实施。
