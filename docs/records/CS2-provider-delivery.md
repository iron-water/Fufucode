# CS-2 原生 SSE 模型客户端真实端点打通与 Agent Loop 强化

日期：2026-10-15
任务 ID：CS-2（难度 L3，执行模型 `group/auto-deepseek-v4-1-flash` high）
独立工作树：`C:\Projects\Fucode\worktrees\cs2-provider`
分支：`agent/cs2-provider`
基线 HEAD：`965e52d84ef783add2f7ec88a2dd6b1aed937b3c`
交付 HEAD：`bf402b0ac7f648834d3d2a067dc5df36a30e0ba6`
任务书：`docs/tasks/native-csharp-runtime.md`（最低真实闭环第 1 条）

---

## 1. 交付内容

### 1.1 核心实现（`src/Fufucode.Core/Backend/`）

| 文件 | 状态 | 作用 |
|---|---|---|
| `ServerSentEvents.cs` | 新增 | 规范形 SSE 行协议解析器：CRLF/LF/CR 分帧、多行 `data`、注释与心跳、`event`/`id`/`retry` 字段、`[DONE]` 哨兵；使用有状态 UTF-8 解码器，多字节字符跨网络分片不丢字 |
| `ChatCompletionSseParser.cs` | 新增 | 增量消费 `delta.content` 与 `delta.tool_calls`；按 `index` 拼装工具调用分片；采集 `finish_reason`/`model`；流内 `error` 对象中断而不是退化为截断的“成功” |
| `ModelClientErrors.cs` | 新增 | `ModelClientException`（`Kind`/`StatusCode`/`ErrorCode`/`ErrorBody`/`IsRetryable`）与 HTTP 401/403/429/5xx/4xx → 语义类别的准确映射；错误体保留上限 2048 字符 |
| `StreamingModelContract.cs` | 新增 | `IStreamingModelClient`、`ModelStreamCallbacks`（`OnTextDelta` / `OnToolCallChunk`，另加带分片索引的重载）与 `StreamedCompletion` / `StreamedChoice` 聚合快照 |
| `OpenAiCompatibleModelClient.cs` | 重写 | `stream=true` 请求体 + `HttpCompletionOption.ResponseHeadersRead` 增量读取；非流式路径保持 `stream=false` 并改为结构化失败（原先抛出无判定的 `HttpRequestException`） |
| `NativeAgentLoop.cs` | 强化 | 客户端实现 `IStreamingModelClient` 时每轮走流式并透传回调；工具结果回填后再次请求模型不变；新增 `CompletedModelCalls`、`StreamingEnabled`；取消异常不被包装 |

对外回调签名按任务书约定：`onTextDelta(string delta)` 与
`onToolCallChunk(string id, string name, string argsFragment)`（映射到 `OnTextDelta` /
`OnToolCallChunk`；分片序号由 `OnToolCallChunkIndexed` 提供）。

### 1.2 测试（`tests/Fufucode.App.Backend.Tests/`）

| 文件 | 作用 |
|---|---|
| `SseProtocolFixtures.cs` | 按脚本分片回放的字节流、脚本化 HTTP 处理器（零真实网络）、SSE 报文构造器 |
| `SseProtocolTests.cs` | 行协议分帧 + `OpenAiCompatibleModelClient` 的真实流式消费断言 |
| `StreamingLoopTests.cs` | `NativeAgentLoop` 流式多轮调度稳定性 |
| `SseCounterexampleTests.cs` | 红/绿对照反例（证明断言有鉴别力） |
| `LiveSseProbe.cs` | 可选的真实端点探针（不进默认套件） |
| `Harness.cs` / `Program.cs` | 新增 `CapturesAsync` 断言助手并注册上述四组测试 |

---

## 2. 验证证据

### 2.1 构建

```
dotnet build Fufucode.sln -v m   → 已成功生成，0 个警告 0 个错误（退出 0）
```

日志：`tmp/cs2-solution-build.log`

### 2.2 定向测试套件

```
dotnet run --project tests/Fufucode.App.Backend.Tests   → 全部通过：339 项断言（退出 0）
```

日志：`tmp/cs2-backend-tests.log`

说明：`tests/Fufucode.App.Backend.Tests` 未注册在 `Fufucode.sln`（M-owned solution 未包含它，
本任务亦未获授权改动 solution），因此按既有做法单独 `dotnet build` / `dotnet run` 该工程；
两者都是 0 警告 0 错误。测试项目入口改为 `Main(string[] args)` 以承载可选探针参数，
默认无参数时行为不变。

已覆盖的关键行为（真实 SSE 报文回放，非合成对象）：

- 每 1 / 3 / 5 / 7 / 9 / 11 / 13 字节切分的真实报文，含行中断与多字节 UTF-8 截断；
- 单个工具调用的 `arguments` 被切成 4 段（含半截 JSON 边界）→ 顺序拼接为
  `{"path":"README.md"}` 并能被 `ProductionToolDispatcher` 真实执行；
- 同轮两个不同 `index` 的工具调用分别聚合；
- `delta.content` 与 `delta.tool_calls` 交错；
- `data: [DONE]` 与缺失 `[DONE]` 但有增量（网关直接关流）两种收束；
- 空流必须报错而不是伪造空回复；
- HTTP 401 / 429 / 500 / 400 / 502（含非 JSON 错误体）的类别、状态码、`error.code`、
  可重试性，且流式与非流式路径映射一致；
- 流内 `error` 帧：错误前已派发的分片仍送达、`OnCompleted` 不触发、抛出结构化异常；
- 多轮循环：轮次上限有界停止、失败回落为 `Failed` 终态、中途取消抛 `OperationCanceledException`。

### 2.3 红/绿对照反例

`SseCounterexampleTests` 在同一套件内实现四种错误写法并证明其真实失败，
从而证明断言不是“怎么写都能过”：

| 反例 | 红（错误实现）观察到的失败 | 绿（生产实现） |
|---|---|---|
| 逐网络分片独立 UTF-8 解码 | 中文被截断，产生 `U+FFFD` 替换字符 | 1 字节分片下中文完整还原，无替换字符 |
| 工具分片 last-write-wins | `arguments` 丢前段，`JsonDocument.Parse` 抛 `JsonException` | 顺序追加还原完整参数并可被真实调度器执行 |
| 只按 `"\n\n"` 切事件 | CRLF 报文被并成一条，解析失败；换成 LF 报文却“碰巧通过” | 规范解析器剥净 `\r`，两条事件均正确 |
| 忽略流内 `error` 字段 | 只得到残缺文本 `partial` 并被当作成功结果 | 抛出结构化异常并暴露 `error.code` |

### 2.4 真实端点证据（可选探针，已实跑）

```
dotnet run --project tests/Fufucode.App.Backend.Tests --no-build -- \
  --live-sse http://127.0.0.1:3425/v1 devin/swe-2 local-probe-key
```

日志：`tmp/cs2-live-sse.log`（退出 0，RESULT: PASS）

该探针对本机运行中的 OpenAI 兼容网关发起 `stream=true` 真实请求（按成本纪律使用免费
`devin/swe-2`，未使用 S/A 级模型）：

- 纯文本流：`textDeliveries=2`（收到 2 个真实增量分片）、`textLength=9`、`sawDone=True`、
  `firstDeltaAfter≈5.8s`、`total≈6.7s`、`model=swe-2`；增量顺序拼接等于聚合文本；
- 真实多轮 Agent Loop：`status=Completed`、`modelCalls=2`、`toolResults=1`；
  模型自发调用 `read_file` 读取真实临时文件，工具返回 `CS2-LIVE-MARKER-9182`，
  工具结果以 Tool 角色回填后第二轮请求产出终态文本 `CS2-LIVE-MARKER-9182`；
- 错误映射：对不存在的路径请求返回 `kind=InvalidRequest status=404`（结构化，非静默）。

该探针仅用于真实端点连通性证据，**不进入默认测试套件**（避免网络与凭据依赖）。

---

## 3. 未覆盖 / 未验证（诚实边界）

1. **真实公网供应商（OpenAI/DeepSeek 官方域名）未验证**：本轮真实端点证据来自本机
   运行中的 OpenAI 兼容网关（`127.0.0.1:3425`）。官方域名的鉴权/配额/限流行为、以及
   真实 429 退避重试策略，均未在本轮实测（429 仅由脚本化响应验证映射正确）。
2. **未做重试/退避实现**：本轮只提供 `IsRetryable` 判定，未实现自动重试与指数退避。
3. **`NativeCSharpChatSession` 未改为增量时间线回放**：桌面时间线仍是轮次结束后的
   整段回放（`SendAsync` 现有结构）。流式回调已可被 `NativeAgentLoop` 透传，但把
   `OnTextDelta` → `SessionEvent.TextAppended`、`OnToolCallChunk` →
   `SessionEvent.ToolCallAppended` 的实时接线属于桌面宿主/会话层改动，本任务范围内未做，
   也未声明完成。
4. **多 choice（`n>1`）仅聚合不分别返回**：`StreamedCompletion` 保留全部 choice，
   但 `ToResponse()` 只取主 choice（下标 0），与内核现有单响应契约一致。
5. **未做真实长任务压力验证**（长时间大响应的内存/背压、超长上下文的 SSE 分片吞吐）。
6. **`tests/Fufucode.Core.Tests` 全量运行未通过**：失败原因与本改动无关——该套件在
   `ExtensionUiTests.RunRealAsync` 处要求 `runtime/node/node.exe`，本工作树该文件不存在
   （`Test-Path runtime/node/node.exe` → `False`），抛出
   `FileNotFoundException: Pi runtime executable is missing`。该失败在基线即存在，
   属于环境缺失而非本交付引入；同次运行中 324 项断言 0 失败。
7. **其他未注册在 solution 的测试工程未逐一实跑**（依赖本机 Pi/node 环境的套件不在本任务
   验证范围）。

---

## 4. 集成提示（给主控 M）

- 新增接口未破坏既有契约：`IPureModelClient` 签名不变，`OpenAiCompatibleModelClient`
  额外实现 `IStreamingModelClient`；`NativeAgentLoop` 原 4 参重载保留，新增 6 参重载。
- 行为变更：请求体现在显式包含 `"stream": false|true`；非流式失败从
  `HttpRequestException` 变为 `ModelClientException`（需要判定类别/重试的调用方应改捕获类型）。
- 会话层实时接线（未做项 3）建议作为后续任务，可在 `NativeCSharpChatSession.SendAsync`
  中通过新增的 `ExecuteLoopAsync(..., ModelStreamCallbacks, ...)` 重载接入。
